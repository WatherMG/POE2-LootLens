using System.Collections.ObjectModel;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Poe2LootLens;

// DivineValue  = price in divine orbs.
// ExaltedValue = the same price converted to exalted orbs for display below one divine.
internal sealed record PriceEntry(
    decimal DivineValue,
    decimal ExaltedValue,
    string SourceId = "",
    string SourceName = "");

internal sealed record PricedItem(string Id, string Name, PriceEntry Price);

internal sealed class PriceRepository : IDisposable
{
    private readonly HttpClient _http;
    private volatile IReadOnlyDictionary<string, PriceEntry> _prices =
        new ReadOnlyDictionary<string, PriceEntry>(
            new Dictionary<string, PriceEntry>(StringComparer.Ordinal));
    private volatile IReadOnlySet<string> _knownNames =
        new HashSet<string>(StringComparer.Ordinal);
    private volatile IReadOnlySet<string> _ambiguousNames =
        new HashSet<string>(StringComparer.Ordinal);
    private volatile IReadOnlyDictionary<string, string> _localizedNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private System.Threading.Timer? _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public IReadOnlyDictionary<string, PriceEntry> Prices => _prices;
    public IReadOnlySet<string> KnownNames => _knownNames;
    public IReadOnlySet<string> AmbiguousNames => _ambiguousNames;
    public DateTime? LastFetchedAt { get; private set; }
    public decimal ExaltedPerDivine { get; private set; }
    public string? LastError { get; private set; }
    public int ItemCount => _prices.Count;

    public event Action? PricesUpdated;

    private static readonly string[] ExchangeTypes =
        ["Verisium", "Runes", "Expedition", "Currency", "UncutGems"];

    public PriceRepository(HttpClient http) => _http = http;

    public Task InitialFetchAsync(AppConfig config) => RefreshAsync(config, waitForTurn: true);

    public Task RefreshNowAsync(AppConfig config) => RefreshAsync(config, waitForTurn: true);

    public void StartAutoRefresh(AppConfig config)
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(
            state =>
            {
                _ = RefreshAsync(config, waitForTurn: false);
            },
            null,
            TimeSpan.FromMinutes(Math.Clamp(config.DataRefreshIntervalMinutes, 15, 240)),
            TimeSpan.FromMinutes(Math.Clamp(config.DataRefreshIntervalMinutes, 15, 240)));
    }

    private async Task RefreshAsync(AppConfig config, bool waitForTurn)
    {
        bool entered = false;
        try
        {
            if (waitForTurn)
            {
                await _refreshGate.WaitAsync(_cts.Token);
                entered = true;
            }
            else
            {
                entered = await _refreshGate.WaitAsync(0, _cts.Token);
            }

            if (!entered)
                return; // a previous refresh is still running; never overlap network cycles

            await FetchAndMergeAsync(config, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Application shutdown.
        }
        finally
        {
            if (entered)
                _refreshGate.Release();
        }
    }

    private async Task FetchAndMergeAsync(AppConfig config, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            // Price endpoints and the localized-name endpoint are independent. Start them together so
            // the one-time launch fetch and every scheduled refresh finish as quickly as the slowest call.
            var priceTasks = ExchangeTypes
                .Select(type => FetchTypeAsync(config.LeagueName, type, cancellationToken))
                .ToArray();
            var localizedNamesTask = FetchLocalizedNamesAsync(
                config.GameLanguage,
                cancellationToken);

            var priceResults = await Task.WhenAll(priceTasks);
            var localizedNames = await localizedNamesTask;

            var pricedItemsById = new Dictionary<string, PricedItem>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var entries in priceResults)
            {
                foreach (var (id, item) in entries)
                    pricedItemsById[id] = item;
            }

            var prices = new Dictionary<string, PriceEntry>(StringComparer.Ordinal);
            var knownNames = new HashSet<string>(StringComparer.Ordinal);
            var ambiguousNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var localizedName in localizedNames.Values)
            {
                var key = NormalizeName(localizedName);
                if (!string.IsNullOrEmpty(key))
                    knownNames.Add(key);
            }

            foreach (var item in pricedItemsById.Values)
            {
                var englishKey = NormalizeName(item.Name);
                if (string.IsNullOrEmpty(englishKey))
                    continue;

                knownNames.Add(englishKey);
                AddPriceKey(prices, ambiguousNames, englishKey, item.Price);
            }

            // First custom-price pass updates canonical English entries before localized aliases copy
            // their PriceEntry. The second pass lets a user explicitly override a localized key itself.
            ApplyCustomOverride(prices, knownNames, ambiguousNames, config.CustomPricesPath);

            ApplyLocalizedNames(
                prices,
                knownNames,
                ambiguousNames,
                pricedItemsById.Values,
                localizedNames);
            ApplyCustomOverride(prices, knownNames, ambiguousNames, config.CustomPricesPath);
            ApplyAliasOverrides(prices, knownNames, ambiguousNames, config.ItemAliasesPath);

            _localizedNames = new ReadOnlyDictionary<string, string>(localizedNames);
            _knownNames = knownNames;
            _ambiguousNames = ambiguousNames;
            _prices = new ReadOnlyDictionary<string, PriceEntry>(prices);
            ExaltedPerDivine = ResolveExaltedPerDivine(pricedItemsById.Values);
            LastFetchedAt = DateTime.Now;
            WriteDiagnosticSnapshot(
                config.LeagueName,
                LastFetchedAt.Value,
                pricedItemsById.Values,
                localizedNames);
            PricesUpdated?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Console.Error.WriteLine($"[PriceRepository] fetch failed: {exception.Message}");
            // Keep the previous immutable snapshot. A failed category/localization request must never
            // replace a working Russian catalog with an empty or partial dictionary.
        }
    }

    private async Task<Dictionary<string, PricedItem>> FetchTypeAsync(
        string league,
        string type,
        CancellationToken cancellationToken)
    {
        var leagueSlug = league.Replace(" ", string.Empty).ToLowerInvariant();
        var typeSlug = type.ToLowerInvariant();
        var url =
            $"https://poe.ninja/poe2/api/economy/exchange/current/overview" +
            $"?league={Uri.EscapeDataString(league)}&type={type}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(request);
        request.Headers.TryAddWithoutValidation(
            "Referer",
            $"https://poe.ninja/poe2/economy/{leagueSlug}/{typeSlug}");

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"poe.ninja {type}: HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParsePriceResponse(json);
    }

    private async Task<Dictionary<string, string>> FetchLocalizedNamesAsync(
        string language,
        CancellationToken cancellationToken)
    {
        var normalizedLanguage = (language ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedLanguage is "" or "en" or "en-us" or "en-gb")
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var host = normalizedLanguage switch
        {
            "ru" or "ru-ru" => "https://ru.pathofexile.com",
            _ => null,
        };

        if (host is null)
        {
            Console.Error.WriteLine(
                $"[PriceRepository] unsupported GameLanguage '{language}', English names only");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var url = $"{host}/api/trade2/data/static";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(request);
            request.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9");
            request.Headers.TryAddWithoutValidation("Referer", $"{host}/trade2");

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"localized static data: HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = ParseLocalizedStaticResponse(json);
            if (parsed.Count == 0)
                throw new InvalidDataException("localized static data is empty");
            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[PriceRepository] localized static data failed: {exception.Message}");

            if (_localizedNames.Count > 0)
            {
                return _localizedNames.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
            }

            throw;
        }
    }

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/148.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
    }

    // poe.ninja exchange/current/overview:
    // items[] -> { id, name }
    // lines[] -> { id, primaryValue }
    // core.primary -> divine | exalted
    // core.rates -> conversion rates relative to the primary currency.
    private static Dictionary<string, PricedItem> ParsePriceResponse(string json)
    {
        var result = new Dictionary<string, PricedItem>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var root = JObject.Parse(json);
            var nameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root["items"] is JArray items)
            {
                foreach (var item in items)
                {
                    var id = item["id"]?.Value<string>();
                    var name = item["name"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        nameById[id] = name;
                }
            }

            var core = root["core"];
            var primary = core?["primary"]?.Value<string>() ?? "divine";
            var rates = core?["rates"];
            var divinePerPrimary = primary == "divine"
                ? 1m
                : rates?["divine"]?.Value<decimal>() ?? 0m;
            var exaltedPerPrimary = primary == "exalted"
                ? 1m
                : rates?["exalted"]?.Value<decimal>() ?? 1m;

            if (root["lines"] is not JArray lines)
                return result;

            foreach (var line in lines)
            {
                var id = line["id"]?.Value<string>();
                if (id is null || !nameById.TryGetValue(id, out var name))
                    continue;

                var primaryValue = line["primaryValue"]?.Value<decimal>() ?? 0m;
                var price = new PriceEntry(
                    primaryValue * divinePerPrimary,
                    Math.Round(primaryValue * exaltedPerPrimary, 1),
                    id,
                    name);

                result[id] = new PricedItem(id, name, price);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[PriceRepository] price response parse failed: {exception.Message}");
        }

        return result;
    }

    internal static Dictionary<string, string> ParseLocalizedStaticResponse(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var root = JToken.Parse(json);
            CollectLocalizedEntries(root, result);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[PriceRepository] localized response parse failed: {exception.Message}");
        }

        return result;
    }

    private static void CollectLocalizedEntries(
        JToken token,
        Dictionary<string, string> result)
    {
        if (token is JObject obj)
        {
            var id = obj["id"]?.Value<string>();
            var text = obj["text"]?.Value<string>();

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(text))
                result[id] = text;

            foreach (var property in obj.Properties())
                CollectLocalizedEntries(property.Value, result);

            return;
        }

        if (token is JArray array)
        {
            foreach (var child in array)
                CollectLocalizedEntries(child, result);
        }
    }

    private static void ApplyLocalizedNames(
        Dictionary<string, PriceEntry> prices,
        HashSet<string> knownNames,
        HashSet<string> ambiguousNames,
        IEnumerable<PricedItem> pricedItems,
        IReadOnlyDictionary<string, string> localizedNames)
    {
        foreach (var item in pricedItems)
        {
            if (!localizedNames.TryGetValue(item.Id, out var localizedName))
                continue;

            var englishKey = NormalizeName(item.Name);
            var localizedKey = NormalizeName(localizedName);
            if (string.IsNullOrEmpty(localizedKey))
                continue;

            knownNames.Add(localizedKey);
            if (prices.TryGetValue(englishKey, out var price))
                AddPriceKey(prices, ambiguousNames, localizedKey, price);
        }
    }

    private static void AddPriceKey(
        Dictionary<string, PriceEntry> prices,
        HashSet<string> ambiguousNames,
        string key,
        PriceEntry price)
    {
        if (ambiguousNames.Contains(key))
            return;

        if (prices.TryGetValue(key, out var existing) &&
            !string.IsNullOrEmpty(existing.SourceId) &&
            !string.IsNullOrEmpty(price.SourceId) &&
            !string.Equals(existing.SourceId, price.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            // Two different market items normalized to the same visible name. A silent overwrite can
            // display a perfectly plausible but completely wrong price, so remove the key instead.
            prices.Remove(key);
            ambiguousNames.Add(key);
            return;
        }

        prices[key] = price;
    }

    private static void ApplyCustomOverride(
        Dictionary<string, PriceEntry> prices,
        HashSet<string> knownNames,
        HashSet<string> ambiguousNames,
        string path)
    {
        try
        {
            var fullPath = ResolvePath(path);
            if (!File.Exists(fullPath))
                return;

            var json = File.ReadAllText(fullPath);
            var overrides = JsonConvert.DeserializeObject<
                Dictionary<string, CustomPriceEntry>>(json);

            if (overrides is null)
                return;

            foreach (var (rawKey, entry) in overrides)
            {
                var key = NormalizeName(rawKey);
                if (string.IsNullOrEmpty(key))
                    continue;

                knownNames.Add(key);
                ambiguousNames.Remove(key);
                prices[key] = new PriceEntry(
                    entry.DivineValue,
                    entry.ExaltedValue,
                    "custom",
                    rawKey);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[PriceRepository] custom price override failed: {exception.Message}");
        }
    }

    private static void ApplyAliasOverrides(
        Dictionary<string, PriceEntry> prices,
        HashSet<string> knownNames,
        HashSet<string> ambiguousNames,
        string path)
    {
        try
        {
            var fullPath = ResolvePath(path);
            if (!File.Exists(fullPath))
                return;

            var json = File.ReadAllText(fullPath);
            var aliases = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (aliases is null)
                return;

            foreach (var (rawAlias, rawTarget) in aliases)
            {
                var alias = NormalizeName(rawAlias);
                if (string.IsNullOrEmpty(alias))
                    continue;

                // An empty target is an explicit "known reward without a market quote" declaration.
                // A missing target is treated the same way for backwards compatibility. Neither case
                // is an error: it is used for rewards such as Verisium piles, skills, or generic uniques.
                knownNames.Add(alias);
                ambiguousNames.Remove(alias);

                var target = NormalizeName(rawTarget ?? string.Empty);
                if (string.IsNullOrEmpty(target))
                    continue;

                if (prices.TryGetValue(target, out var price))
                    prices[alias] = price;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[PriceRepository] item aliases failed: {exception.Message}");
        }
    }


    private static void WriteDiagnosticSnapshot(
        string league,
        DateTime fetchedAt,
        IEnumerable<PricedItem> pricedItems,
        IReadOnlyDictionary<string, string> localizedNames)
    {
        try
        {
            var snapshot = new
            {
                league,
                fetchedAtUtc = fetchedAt.ToUniversalTime(),
                source = "poe.ninja exchange overview",
                note = "Aggregated estimate; it can differ from the current best live trade listing.",
                items = pricedItems
                    .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new
                    {
                        id = item.Id,
                        englishName = item.Name,
                        localizedName = localizedNames.TryGetValue(item.Id, out var localized)
                            ? localized
                            : string.Empty,
                        divineValue = item.Price.DivineValue,
                        exaltedValue = item.Price.ExaltedValue,
                    })
                    .ToArray(),
            };

            string path = Path.Combine(AppContext.BaseDirectory, "price_snapshot.json");
            string temporaryPath = path + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonConvert.SerializeObject(snapshot, Formatting.Indented));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[PriceRepository] diagnostic snapshot failed: {exception.Message}");
        }
    }


    private static decimal ResolveExaltedPerDivine(IEnumerable<PricedItem> pricedItems)
    {
        foreach (var item in pricedItems)
        {
            string normalized = NormalizeName(item.Name);
            if (string.Equals(item.Id, "divine", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Id, "divine-orb", StringComparison.OrdinalIgnoreCase) ||
                normalized == "divine orb")
            {
                if (item.Price.ExaltedValue > 0m)
                    return item.Price.ExaltedValue;
            }
        }

        return 0m;
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

    internal static string NormalizeName(string name) => ItemNameNormalizer.Normalize(name);

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _timer = null;
        _cts.Dispose();
    }

    private sealed class CustomPriceEntry
    {
        public decimal DivineValue { get; set; }
        public decimal ExaltedValue { get; set; }
    }
}

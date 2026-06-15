using System.IO;
using System.Net;
using System.Net.Http;
using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class PriceRepositoryTests
{
    private const string FakeApiResponse = """
        {
          "items": [
            { "id": "chilling-flux",             "name": "Chilling Flux" },
            { "id": "support-scattering-flame", "name": "Support: Scattering Flame" }
          ],
          "lines": [
            { "id": "chilling-flux",             "primaryValue": 0.5 },
            { "id": "support-scattering-flame", "primaryValue": 1.2 }
          ],
          "core": { "primary": "divine", "rates": { "exalted": 80.0 } }
        }
        """;

    private const string FakeHardcoreResponse = """
        {
          "items": [
            { "id": "orb-of-alchemy", "name": "Orb of Alchemy" },
            { "id": "divine-orb",     "name": "Divine Orb" }
          ],
          "lines": [
            { "id": "orb-of-alchemy", "primaryValue": 1.13 },
            { "id": "divine-orb",     "primaryValue": 67.51 }
          ],
          "core": { "primary": "exalted", "rates": { "divine": 0.01481, "chaos": 0.2785 } }
        }
        """;

    private const string FakeTransmutationPriceResponse = """
        {
          "items": [
            { "id": "greater-orb-of-transmutation", "name": "Greater Orb of Transmutation" }
          ],
          "lines": [
            { "id": "greater-orb-of-transmutation", "primaryValue": 0.25 }
          ],
          "core": { "primary": "divine", "rates": { "exalted": 80.0 } }
        }
        """;

    private const string FakeRussianStaticResponse = """
        {
          "result": [
            {
              "id": "Currency",
              "label": "Валюта",
              "entries": [
                {
                  "id": "greater-orb-of-transmutation",
                  "text": "Большая сфера превращения"
                }
              ]
            }
          ]
        }
        """;

    private static AppConfig DefaultConfig(string tempDirectory) => new()
    {
        LeagueName = "Test League",
        GameLanguage = "en",
        CustomPricesPath = Path.Combine(tempDirectory, "custom_prices.json"),
        ItemAliasesPath = Path.Combine(tempDirectory, "item_aliases.json"),
    };

    [Fact]
    public async Task FetchPopulatesDict_WithNormalizedKeys()
    {
        using var http = FakeHttp(FakeApiResponse);
        using var directory = new TempDir();
        using var repository = new PriceRepository(http);

        await repository.InitialFetchAsync(DefaultConfig(directory.Path));

        Assert.True(repository.Prices.ContainsKey("chilling flux"));
        Assert.True(repository.Prices.ContainsKey("support scattering flame"));
        Assert.Equal(0.5m, repository.Prices["chilling flux"].DivineValue);
        Assert.Equal(40.0m, repository.Prices["chilling flux"].ExaltedValue);
    }

    [Fact]
    public async Task ExaltedPrimary_DenominatesInExalted_NotDivine()
    {
        using var http = FakeHttp(FakeHardcoreResponse);
        using var directory = new TempDir();
        using var repository = new PriceRepository(http);

        await repository.InitialFetchAsync(DefaultConfig(directory.Path));

        var alchemy = repository.Prices["orb of alchemy"];
        Assert.Equal(1.1m, alchemy.ExaltedValue);
        Assert.True(alchemy.DivineValue < 1m);
        Assert.True(repository.Prices["divine orb"].DivineValue >= 0.99m);
        Assert.Equal(67.5m, repository.ExaltedPerDivine);
    }

    [Fact]
    public async Task RussianStaticName_IsAddedAsAliasForTheSamePrice()
    {
        using var directory = new TempDir();
        using var http = new HttpClient(new RoutingHandler(request =>
            request.RequestUri?.Host == "ru.pathofexile.com"
                ? FakeRussianStaticResponse
                : FakeTransmutationPriceResponse));
        using var repository = new PriceRepository(http);

        var config = DefaultConfig(directory.Path);
        config.GameLanguage = "ru";

        await repository.InitialFetchAsync(config);

        Assert.True(repository.Prices.ContainsKey("greater orb of transmutation"));
        Assert.True(repository.Prices.ContainsKey("большая сфера превращения"));
        Assert.Equal(
            repository.Prices["greater orb of transmutation"],
            repository.Prices["большая сфера превращения"]);
    }

    [Fact]
    public async Task ItemAliasFile_AddsManualAlias()
    {
        using var directory = new TempDir();
        using var http = FakeHttp(FakeTransmutationPriceResponse);
        using var repository = new PriceRepository(http);

        File.WriteAllText(
            Path.Combine(directory.Path, "item_aliases.json"),
            """{"великая сфера превращения":"greater orb of transmutation"}""");

        await repository.InitialFetchAsync(DefaultConfig(directory.Path));

        Assert.True(repository.Prices.ContainsKey("великая сфера превращения"));
        Assert.Equal(
            repository.Prices["greater orb of transmutation"],
            repository.Prices["великая сфера превращения"]);
    }

    [Fact]
    public async Task EmptyAliasTarget_ExplicitlyMarksKnownWithoutPrice()
    {
        using var directory = new TempDir();
        using var http = FakeHttp(FakeTransmutationPriceResponse);
        using var repository = new PriceRepository(http);

        File.WriteAllText(
            Path.Combine(directory.Path, "item_aliases.json"),
            """{"груда веризия":""}""");

        await repository.InitialFetchAsync(DefaultConfig(directory.Path));

        Assert.True(repository.KnownNames.Contains("груда веризия"));
        Assert.False(repository.Prices.ContainsKey("груда веризия"));
    }

    [Fact]
    public async Task MissingAliasTarget_IsStillKnownWithoutPrice()
    {
        using var directory = new TempDir();
        using var http = FakeHttp(FakeTransmutationPriceResponse);
        using var repository = new PriceRepository(http);

        File.WriteAllText(
            Path.Combine(directory.Path, "item_aliases.json"),
            """{"груда веризия":"pile of verisium"}""");

        await repository.InitialFetchAsync(DefaultConfig(directory.Path));

        Assert.True(repository.KnownNames.Contains("груда веризия"));
        Assert.False(repository.Prices.ContainsKey("груда веризия"));
    }

    [Fact]
    public async Task CustomOverride_ReplacesPoENinjaEntry()
    {
        using var http = FakeHttp(FakeApiResponse);
        using var directory = new TempDir();
        File.WriteAllText(
            Path.Combine(directory.Path, "custom_prices.json"),
            """{"chilling flux":{"divineValue":2.0,"exaltedValue":160.0}}""");

        using var repository = new PriceRepository(http);
        await repository.InitialFetchAsync(DefaultConfig(directory.Path));

        Assert.Equal(2.0m, repository.Prices["chilling flux"].DivineValue);
    }

    [Fact]
    public async Task CustomOverride_InsertsNewEntry()
    {
        using var http = FakeHttp(
            """{"items":[],"lines":[],"core":{"rates":{"exalted":80}}}""");
        using var directory = new TempDir();
        File.WriteAllText(
            Path.Combine(directory.Path, "custom_prices.json"),
            """{"support scattering flame":{"divineValue":1.5,"exaltedValue":120.0}}""");

        using var repository = new PriceRepository(http);
        await repository.InitialFetchAsync(DefaultConfig(directory.Path));

        Assert.True(repository.Prices.ContainsKey("support scattering flame"));
        Assert.Equal(1.5m, repository.Prices["support scattering flame"].DivineValue);
    }

    [Fact]
    public async Task FailedRefresh_KeepsPreviousWorkingSnapshot()
    {
        var handler = new SwitchableFailureHandler(FakeApiResponse);
        using var http = new HttpClient(handler);
        using var directory = new TempDir();
        using var repository = new PriceRepository(http);

        await repository.InitialFetchAsync(DefaultConfig(directory.Path));
        var before = repository.Prices;
        Assert.True(before.ContainsKey("chilling flux"));

        handler.Fail = true;
        await repository.RefreshNowAsync(DefaultConfig(directory.Path));

        Assert.Same(before, repository.Prices);
        Assert.True(repository.Prices.ContainsKey("chilling flux"));
        Assert.NotNull(repository.LastError);
    }

    [Fact]
    public async Task MissingCustomFiles_AreIgnoredSilently()
    {
        using var http = FakeHttp(FakeApiResponse);
        var config = new AppConfig
        {
            LeagueName = "Test League",
            GameLanguage = "en",
            CustomPricesPath = "/nonexistent/path/custom_prices.json",
            ItemAliasesPath = "/nonexistent/path/item_aliases.json",
        };

        using var repository = new PriceRepository(http);
        await repository.InitialFetchAsync(config);

        Assert.True(repository.Prices.ContainsKey("chilling flux"));
    }

    [Theory]
    [InlineData("Runes of Aldur", "league=Runes%20of%20Aldur&", "/economy/runesofaldur/")]
    [InlineData("HC Runes of Aldur", "league=HC%20Runes%20of%20Aldur&", "/economy/hcrunesofaldur/")]
    public async Task LeagueName_DrivesApiParamAndReferer(
        string league,
        string expectedParameter,
        string expectedSlug)
    {
        var handler = new CapturingFakeHttpHandler(FakeApiResponse);
        using var http = new HttpClient(handler);
        using var directory = new TempDir();
        var config = DefaultConfig(directory.Path);
        config.LeagueName = league;

        using var repository = new PriceRepository(http);
        await repository.InitialFetchAsync(config);

        Assert.All(handler.Urls, url => Assert.Contains(expectedParameter, url));
        Assert.All(handler.Referers, referer => Assert.Contains(expectedSlug, referer));
    }

    [Fact]
    public async Task Fetch_IncludesOptionalLineageSupportGemCategory()
    {
        var handler = new CapturingFakeHttpHandler(FakeApiResponse);
        using var http = new HttpClient(handler);
        using var directory = new TempDir();
        using var repository = new PriceRepository(http);

        await repository.InitialFetchAsync(DefaultConfig(directory.Path));

        Assert.Contains(handler.Urls, url => url.Contains("type=LineageSupportGems", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OptionalLineageCategoryFailure_DoesNotDiscardCorePrices()
    {
        using var http = new HttpClient(new StatusRoutingHandler(request =>
            request.RequestUri?.Query.Contains("type=LineageSupportGems", StringComparison.Ordinal) == true
                ? (HttpStatusCode.NotFound, "missing")
                : (HttpStatusCode.OK, FakeApiResponse)));
        using var directory = new TempDir();
        using var repository = new PriceRepository(http);

        await repository.InitialFetchAsync(DefaultConfig(directory.Path));

        Assert.True(repository.Prices.ContainsKey("chilling flux"));
        Assert.Null(repository.LastError);
    }

    [Fact]
    public void ParseLocalizedStaticResponse_CollectsNestedIdAndTextEntries()
    {
        var result = PriceRepository.ParseLocalizedStaticResponse(FakeRussianStaticResponse);

        Assert.Equal(
            "Большая сфера превращения",
            result["greater-orb-of-transmutation"]);
    }

    [Theory]
    [InlineData("Support: Scattering Flame", "support scattering flame")]
    [InlineData("CHILLING FLUX", "chilling flux")]
    [InlineData("  Grip's Edge  ", "grip s edge")]
    [InlineData("Rune-of-Aldur", "rune of aldur")]
    [InlineData("НЕОГРАНЁННЫЙ САМОЦВЕТ", "неограненный самоцвет")]
    public void NormalizeName_ProducesConsistentKey(string input, string expected)
    {
        Assert.Equal(expected, PriceRepository.NormalizeName(input));
    }

    private static HttpClient FakeHttp(string responseJson)
    {
        var handler = new FakeHttpMessageHandler(responseJson);
        return new HttpClient(handler);
    }

    private sealed class SwitchableFailureHandler(string responseJson)
        : HttpMessageHandler
    {
        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(
                Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(Fail ? "temporary failure" : responseJson),
                RequestMessage = request,
            });
        }
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, string> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseFactory(request)),
                RequestMessage = request,
            };

            return Task.FromResult(response);
        }
    }

    private sealed class StatusRoutingHandler(
        Func<HttpRequestMessage, (HttpStatusCode Status, string Content)> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var result = responseFactory(request);
            return Task.FromResult(new HttpResponseMessage(result.Status)
            {
                Content = new StringContent(result.Content),
                RequestMessage = request,
            });
        }
    }
}

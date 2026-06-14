using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Poe2LootLens;

internal enum RumorKind
{
    Expedition,
    Boss,
    Unique,
}

internal sealed class RumorCatalogDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string CanonicalSource { get; set; } = string.Empty;
    public string CommunitySource { get; set; } = string.Empty;
    public List<RumorCatalogEntry> Entries { get; set; } = [];
}

internal sealed class RumorCatalogUserDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<RumorCatalogEntry> Entries { get; set; } = [];
    public List<string> DisabledIds { get; set; } = [];
}

internal sealed class RumorCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string[] Phrases { get; set; } = [];
    public string Kind { get; set; } = string.Empty;
    public string TitleRu { get; set; } = string.Empty;
    public string TitleEn { get; set; } = string.Empty;
    public string MapEn { get; set; } = string.Empty;
    public string MapRu { get; set; } = string.Empty;
    public string DetailRu { get; set; } = string.Empty;
    public string DetailEn { get; set; } = string.Empty;
    public string MapTypeRu { get; set; } = string.Empty;
    public string MapTypeEn { get; set; } = string.Empty;
    public string ModsRu { get; set; } = string.Empty;
    public string ModsEn { get; set; } = string.Empty;
    public string Rating { get; set; } = string.Empty;
    public string NoteRu { get; set; } = string.Empty;
    public string NoteEn { get; set; } = string.Empty;
    public string ResultRu { get; set; } = string.Empty;
    public string ResultEn { get; set; } = string.Empty;
    public string RatingNotesRu { get; set; } = string.Empty;
    public string RatingNotesEn { get; set; } = string.Empty;
    public string[] Tags { get; set; } = [];
    public int Priority { get; set; }

    [JsonIgnore]
    public bool IsDisabled { get; set; }

    [JsonIgnore]
    public double DisplayOpacity => IsDisabled ? 0.48d : 1d;

    [JsonIgnore]
    public string DisabledDisplay => IsDisabled ? "ОТКЛ." : string.Empty;

    [JsonIgnore]
    public RumorKind ParsedKind => Kind.ToLowerInvariant() switch
    {
        "boss" => RumorKind.Boss,
        "unique" => RumorKind.Unique,
        _ => RumorKind.Expedition,
    };

    [JsonIgnore]
    public string PrimaryPhrase => Phrases.FirstOrDefault() ?? Id;

    [JsonIgnore]
    public string KindIconPath => ParsedKind switch
    {
        RumorKind.Boss => "Assets/Rumors/boss.png",
        RumorKind.Unique => "Assets/Rumors/unique.png",
        _ => "Assets/Rumors/expedition.png",
    };

    [JsonIgnore]
    public string KindAccentColor => ParsedKind switch
    {
        RumorKind.Boss => "#FF5F6D",
        RumorKind.Unique => "#E77F29",
        _ => "#2BB7FF",
    };

    [JsonIgnore]
    public string RatingDisplay => string.IsNullOrWhiteSpace(Rating) ? "—" : Rating;
}

internal sealed record RumorMatch(
    RumorCatalogEntry Entry,
    string RawText,
    double Score,
    bool Exact);

internal sealed class RumorCatalog
{
    private sealed record PhraseIndex(string Normalized, RumorCatalogEntry Entry);

    private readonly List<PhraseIndex> _phrases;

    public RumorCatalogDocument Document { get; }
    public IReadOnlyList<RumorCatalogEntry> Entries => Document.Entries;
    public string DefaultPath { get; }
    public string UserPath { get; }
    public string SourcePath => UserPath;
    public DateTime SourceLastWriteTimeUtc { get; }

    private RumorCatalog(
        RumorCatalogDocument document,
        string defaultPath,
        string userPath,
        DateTime sourceLastWriteTimeUtc)
    {
        Document = document;
        DefaultPath = defaultPath;
        UserPath = userPath;
        SourceLastWriteTimeUtc = sourceLastWriteTimeUtc;
        _phrases = document.Entries
            .SelectMany(entry => entry.Phrases.Select(phrase =>
                new PhraseIndex(NormalizeForMatch(phrase), entry)))
            .Where(index => index.Normalized.Length >= 4)
            .ToList();
    }

    public static RumorCatalog Load(string? defaultPath = null, string? userPath = null)
    {
        string resolvedDefault = EnsureDefaultFile(defaultPath);
        string resolvedUser = userPath is null
            ? Path.Combine(Path.GetDirectoryName(resolvedDefault) ?? AppContext.BaseDirectory, "rumor_catalog.user.json")
            : ResolvePath(userPath);
        if (!File.Exists(resolvedUser))
            TryMigrateLegacyEditableCatalog(resolvedDefault, resolvedUser);
        resolvedUser = EnsureUserFile(resolvedUser);
        RumorCatalogDocument defaults = LoadDefaultDocument(resolvedDefault);
        RumorCatalogUserDocument user = LoadUserDocument(resolvedUser);
        RumorCatalogDocument effective = Merge(defaults, user);
        return new RumorCatalog(
            effective,
            resolvedDefault,
            resolvedUser,
            MaxWriteTime(resolvedDefault, resolvedUser));
    }

    public DateTime GetCurrentWriteTimeUtc() => MaxWriteTime(DefaultPath, UserPath);

    public static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    public static string EnsureDefaultFile(string? path = null)
    {
        string resolved = ResolvePath(path ?? "rumor_catalog.default.json");
        if (File.Exists(resolved))
            return resolved;

        Directory.CreateDirectory(Path.GetDirectoryName(resolved) ?? AppContext.BaseDirectory);
        var assembly = typeof(RumorCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("POE2LootLens.rumor_catalog.default.json");
        if (stream is null)
            throw new FileNotFoundException(
                "Rumor catalog was not found and the embedded default is unavailable.",
                resolved);
        using var output = new FileStream(resolved, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.CopyTo(output);
        return resolved;
    }

    public static string EnsureUserFile(string? path = null)
    {
        string resolved = ResolvePath(path ?? "rumor_catalog.user.json");
        if (File.Exists(resolved))
            return resolved;
        Directory.CreateDirectory(Path.GetDirectoryName(resolved) ?? AppContext.BaseDirectory);
        SaveUserDocumentAtomic(resolved, new RumorCatalogUserDocument());
        return resolved;
    }

    // Backwards-compatible alias used by older UI code.
    public static string EnsureEditableFile(string? path = null) => EnsureUserFile(path);

    public static RumorCatalogDocument LoadDefaultDocument(string path)
    {
        var document = JsonConvert.DeserializeObject<RumorCatalogDocument>(File.ReadAllText(path))
                       ?? new RumorCatalogDocument();
        NormalizeDocument(document);
        return document;
    }

    public static RumorCatalogUserDocument LoadUserDocument(string path)
    {
        if (!File.Exists(path))
            return new RumorCatalogUserDocument();
        var document = JsonConvert.DeserializeObject<RumorCatalogUserDocument>(File.ReadAllText(path))
                       ?? new RumorCatalogUserDocument();
        document.Entries ??= [];
        document.DisabledIds = (document.DisabledIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var wrapper = new RumorCatalogDocument { Entries = document.Entries };
        NormalizeDocument(wrapper);
        document.Entries = wrapper.Entries;
        return document;
    }

    public static RumorCatalogDocument Merge(
        RumorCatalogDocument defaults,
        RumorCatalogUserDocument user)
    {
        NormalizeDocument(defaults);
        var disabled = user.DisabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultById = defaults.Entries.ToDictionary(
            entry => entry.Id,
            CloneEntry,
            StringComparer.OrdinalIgnoreCase);
        var byId = defaultById
            .Where(pair => !disabled.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => CloneEntry(pair.Value), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in user.Entries)
        {
            if (disabled.Contains(entry.Id))
                continue;

            RumorCatalogEntry merged = CloneEntry(entry);
            // Built-in OCR aliases are compatibility data. Keep them when a user override created by
            // an older version replaces the rest of the entry, otherwise newly shipped recognition
            // fixes would never reach existing installations.
            if (defaultById.TryGetValue(entry.Id, out var defaultEntry))
            {
                merged.Phrases = defaultEntry.Phrases
                    .Concat(entry.Phrases ?? [])
                    .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
                    .Select(phrase => phrase.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            byId[entry.Id] = merged;
        }

        var result = new RumorCatalogDocument
        {
            SchemaVersion = Math.Max(defaults.SchemaVersion, 1),
            CanonicalSource = defaults.CanonicalSource,
            CommunitySource = defaults.CommunitySource,
            Entries = byId.Values.OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        NormalizeDocument(result);
        return result;
    }

    public static RumorCatalogUserDocument BuildUserOverrides(
        RumorCatalogDocument defaults,
        IEnumerable<RumorCatalogEntry> effectiveEntries)
    {
        NormalizeDocument(defaults);
        var entries = effectiveEntries
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var defaultById = defaults.Entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);

        var user = new RumorCatalogUserDocument();
        foreach (var (id, entry) in entries)
        {
            RumorCatalogEntry persisted = CloneEntry(entry);
            if (!defaultById.TryGetValue(id, out var defaultEntry) || !EntriesEqual(defaultEntry, persisted))
                user.Entries.Add(persisted);
            if (entry.IsDisabled)
                user.DisabledIds.Add(id);
        }
        // Missing built-in entries are treated as disabled for compatibility with older editor builds
        // that physically removed them from the effective collection.
        foreach (string id in defaultById.Keys)
        {
            if (!entries.ContainsKey(id))
                user.DisabledIds.Add(id);
        }
        user.Entries = user.Entries.OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).ToList();
        user.DisabledIds = user.DisabledIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return user;
    }

    private static void TryMigrateLegacyEditableCatalog(string defaultPath, string userPath)
    {
        string directory = Path.GetDirectoryName(defaultPath) ?? AppContext.BaseDirectory;
        string legacyPath = Path.Combine(directory, "rumor_catalog.json");
        if (string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(defaultPath), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            RumorCatalogDocument defaults = LoadDefaultDocument(defaultPath);
            RumorCatalogDocument legacyEffective = LoadDefaultDocument(legacyPath);
            var defaultById = defaults.Entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
            var migrated = new RumorCatalogUserDocument();

            // The old file represented the complete effective catalog. Preserve changed and custom
            // entries, but do not disable entries that were added by a newer application version.
            foreach (var legacyEntry in legacyEffective.Entries)
            {
                if (!defaultById.TryGetValue(legacyEntry.Id, out var defaultEntry) ||
                    !EntriesEqual(defaultEntry, legacyEntry))
                {
                    migrated.Entries.Add(CloneEntry(legacyEntry));
                }
            }

            SaveUserDocumentAtomic(userPath, migrated);
        }
        catch
        {
            // A malformed legacy file must not prevent startup. The editor can still import or repair
            // it manually, while the application creates a clean user override file below.
        }
    }

    public static void SaveUserDocumentAtomic(string path, RumorCatalogUserDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        string temporary = path + ".tmp";
        string backup = path + ".bak";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(document, Formatting.Indented));
        // Validate the exact bytes that will replace the live file.
        _ = JsonConvert.DeserializeObject<RumorCatalogUserDocument>(File.ReadAllText(temporary))
            ?? throw new InvalidDataException("The user rumor catalog could not be deserialized.");
        if (File.Exists(path))
        {
            if (File.Exists(backup))
                File.Delete(backup);
            File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    public static RumorCatalogEntry CloneEntry(RumorCatalogEntry entry)
    {
        RumorCatalogEntry clone = JsonConvert.DeserializeObject<RumorCatalogEntry>(
            JsonConvert.SerializeObject(entry)) ?? new RumorCatalogEntry();
        clone.IsDisabled = entry.IsDisabled;
        return clone;
    }

    private static bool EntriesEqual(RumorCatalogEntry left, RumorCatalogEntry right) =>
        string.Equals(
            JsonConvert.SerializeObject(left, Formatting.None),
            JsonConvert.SerializeObject(right, Formatting.None),
            StringComparison.Ordinal);

    private static void NormalizeDocument(RumorCatalogDocument document)
    {
        document.Entries ??= [];
        foreach (var entry in document.Entries)
        {
            entry.Id = entry.Id?.Trim() ?? string.Empty;
            entry.Kind = entry.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
            entry.Rating = entry.Rating?.Trim().ToUpperInvariant() ?? string.Empty;
            entry.Phrases = (entry.Phrases ?? [])
                .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
                .Select(phrase => phrase.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            entry.Tags = (entry.Tags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        document.Entries = document.Entries
            .Where(entry => entry.Id.Length > 0 && entry.Phrases.Length > 0)
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
    }

    private static DateTime MaxWriteTime(params string[] paths)
    {
        DateTime latest = DateTime.MinValue;
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                    latest = new[] { latest, File.GetLastWriteTimeUtc(path) }.Max();
            }
            catch { }
        }
        return latest;
    }

    public IReadOnlyList<RumorMatch> MatchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        string[] rawLines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToArray();
        var candidates = new List<string>(rawLines);

        // Tesseract can split a cursive rumor phrase into two adjacent lines. Add short adjacent
        // combinations without replacing the original lines, so normal one-line matching remains
        // unchanged while split phrases become recoverable.
        for (int index = 0; index + 1 < rawLines.Length; index++)
        {
            string combined = $"{rawLines[index]} {rawLines[index + 1]}";
            if (combined.Length <= 96)
                candidates.Add(combined);
        }

        string[] expandedCandidates = candidates
            .SelectMany(ExpandCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var matches = new Dictionary<string, RumorMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in expandedCandidates)
        {
            string normalized = NormalizeForMatch(rawLine);
            if (normalized.Length < 4)
                continue;

            bool exactFound = false;
            foreach (var phrase in _phrases)
            {
                if (normalized == phrase.Normalized ||
                    (normalized.Length >= phrase.Normalized.Length &&
                     ContainsWholePhrase(normalized, phrase.Normalized)))
                {
                    exactFound = true;
                    var exact = new RumorMatch(phrase.Entry, rawLine.Trim(), 1d, true);
                    if (!matches.TryGetValue(exact.Entry.Id, out var previous) || exact.Score > previous.Score)
                        matches[exact.Entry.Id] = exact;
                }
            }

            // Even when one exact phrase is already present in a noisy OCR fragment, keep checking
            // the derived sub-fragments. This recovers cases where slot bleed contains two rumors
            // and only one of them matched exactly in the original capture.
            var fuzzy = MatchLine(rawLine);
            if (fuzzy is not null &&
                (!matches.TryGetValue(fuzzy.Entry.Id, out var previousFuzzy) ||
                 fuzzy.Score > previousFuzzy.Score ||
                 (fuzzy.Exact && !previousFuzzy.Exact)))
            {
                matches[fuzzy.Entry.Id] = fuzzy;
            }

            if (exactFound)
                continue;
        }

        return matches.Values
            .OrderByDescending(match => match.Exact)
            .ThenByDescending(match => match.Score)
            .ToList();
    }

    public RumorMatch? MatchLine(string? rawLine)
    {
        string normalized = NormalizeForMatch(rawLine);
        if (normalized.Length < 4)
            return null;

        PhraseIndex? exact = null;
        foreach (var phrase in _phrases)
        {
            if (normalized == phrase.Normalized ||
                (normalized.Length >= phrase.Normalized.Length &&
                 ContainsWholePhrase(normalized, phrase.Normalized)))
            {
                exact = phrase;
                break;
            }
        }

        if (exact is not null)
            return new RumorMatch(exact.Entry, rawLine?.Trim() ?? string.Empty, 1d, true);

        PhraseIndex? best = null;
        double bestScore = 0d;
        double secondScore = 0d;

        foreach (var phrase in _phrases)
        {
            int lengthDifference = Math.Abs(normalized.Length - phrase.Normalized.Length);
            if (lengthDifference > Math.Max(6, phrase.Normalized.Length / 3))
                continue;

            double score = Similarity(normalized, phrase.Normalized);
            score = Math.Max(score, OrderedWordHeuristic(normalized, phrase.Normalized));
            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                best = phrase;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        double threshold = normalized.Length >= 16 ? 0.72d : 0.80d;
        if (best is null || bestScore < threshold || bestScore - secondScore < 0.055d)
            return null;

        return new RumorMatch(best.Entry, rawLine?.Trim() ?? string.Empty, bestScore, false);
    }

    private static IEnumerable<string> ExpandCandidates(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
            yield break;

        yield return trimmed;

        foreach (string part in trimmed
                     .Split(['|', '/', '\\', '·'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(part => part.Length >= 4))
        {
            yield return part;
        }

        string[] words = NormalizeForMatch(trimmed)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < 2)
            yield break;

        int maxWindow = Math.Min(4, words.Length);
        for (int window = 2; window <= maxWindow; window++)
        {
            for (int start = 0; start + window <= words.Length; start++)
            {
                yield return string.Join(' ', words.Skip(start).Take(window));
            }
        }
    }

    public static bool LooksLikeRumorPanel(string? text)
    {
        // Header detection must preserve the original alphabet. OCR matching uses a separate
        // homoglyph-folded representation, but folding every Cyrillic letter to Latin would make
        // genuine Russian headers impossible to recognize.
        string normalized = Normalize(text);
        return normalized.Contains("слухи об острове", StringComparison.Ordinal) ||
               normalized.Contains("слухи острова", StringComparison.Ordinal) ||
               normalized.Contains("неизведанные воды", StringComparison.Ordinal) ||
               normalized.Contains("rumors of the island", StringComparison.Ordinal) ||
               normalized.Contains("rumours of the island", StringComparison.Ordinal) ||
               normalized.Contains("island rumors", StringComparison.Ordinal) ||
               normalized.Contains("island rumours", StringComparison.Ordinal) ||
               normalized.Contains("uncharted waters", StringComparison.Ordinal);
    }

    internal static string Normalize(string? value) => NormalizeCore(value, foldHomoglyphs: false);

    internal static string NormalizeForMatch(string? value) => NormalizeCore(value, foldHomoglyphs: true);

    private static string NormalizeCore(string? value, bool foldHomoglyphs)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Replace("’", string.Empty, StringComparison.Ordinal)
            .Replace("‘", string.Empty, StringComparison.Ordinal)
            .Replace("ʼ", string.Empty, StringComparison.Ordinal)
            .Replace("´", string.Empty, StringComparison.Ordinal)
            .Replace("ʹ", string.Empty, StringComparison.Ordinal)
            .Replace("＇", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal);

        if (foldHomoglyphs)
            normalized = FoldCommonHomoglyphs(normalized);

        normalized = Regex.Replace(
            normalized,
            @"[^\p{L}\p{Nd}\s]",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant);
        return normalized.Trim();
    }

    private static string FoldCommonHomoglyphs(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            builder.Append(ch switch
            {
                'а' => 'a',
                'е' => 'e',
                'ё' => 'e',
                'с' => 'c',
                'о' => 'o',
                'р' => 'p',
                'х' => 'x',
                'у' => 'y',
                'к' => 'k',
                'м' => 'm',
                'т' => 't',
                'в' => 'b',
                'н' => 'h',
                'і' => 'i',
                'ї' => 'i',
                _ => ch,
            });
        }
        return builder.ToString();
    }

    private static bool ContainsWholePhrase(string text, string phrase)
    {
        int index = text.IndexOf(phrase, StringComparison.Ordinal);
        if (index < 0)
            return false;

        bool leftOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
        int rightIndex = index + phrase.Length;
        bool rightOk = rightIndex >= text.Length || !char.IsLetterOrDigit(text[rightIndex]);
        return leftOk && rightOk;
    }

    private static double OrderedWordHeuristic(string left, string right)
    {
        string[] leftWords = left.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] rightWords = right.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (leftWords.Length < 2 || leftWords.Length > 4 || leftWords.Length != rightWords.Length)
            return 0d;

        int samePosition = 0;
        for (int index = 0; index < leftWords.Length; index++)
        {
            if (string.Equals(leftWords[index], rightWords[index], StringComparison.Ordinal))
                samePosition++;
        }

        bool sameLastWord = string.Equals(leftWords[^1], rightWords[^1], StringComparison.Ordinal);
        if (!sameLastWord)
            return 0d;
        if (samePosition >= leftWords.Length)
            return 1d;
        if (samePosition < leftWords.Length - 1)
            return 0d;

        return leftWords.Length switch
        {
            2 => 0.82d,
            3 => 0.84d,
            _ => 0.86d,
        };
    }

    internal static double Similarity(string left, string right)
    {
        if (left == right)
            return 1d;
        if (left.Length == 0 || right.Length == 0)
            return 0d;

        int distance = Levenshtein(left, right);
        return 1d - distance / (double)Math.Max(left.Length, right.Length);
    }

    private static int Levenshtein(string left, string right)
    {
        if (left.Length > right.Length)
            (left, right) = (right, left);

        int[] previous = new int[left.Length + 1];
        int[] current = new int[left.Length + 1];
        for (int index = 0; index <= left.Length; index++)
            previous[index] = index;

        for (int row = 1; row <= right.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= left.Length; column++)
            {
                int substitution = previous[column - 1] +
                                   (left[column - 1] == right[row - 1] ? 0 : 1);
                current[column] = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[left.Length];
    }
}

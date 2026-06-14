using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Poe2LootLens;

internal static class ConfigStore
{
    private const string FileName = "config.json";

    public static AppConfig Load(string? dir = null)
    {
        string path = PathFor(dir);
        try
        {
            if (!File.Exists(path))
                return Normalize(new AppConfig(), migrateLegacyDefaults: false);

            string json = File.ReadAllText(path);
            var document = JObject.Parse(json);
            var serializer = JsonSerializer.CreateDefault(new JsonSerializerSettings
            {
                ObjectCreationHandling = ObjectCreationHandling.Replace,
            });
            var config = document.ToObject<AppConfig>(serializer) ?? new AppConfig();
            bool hasSchemaVersion = document.TryGetValue(
                nameof(AppConfig.SchemaVersion),
                StringComparison.OrdinalIgnoreCase,
                out JToken? schemaToken);
            int storedSchemaVersion = hasSchemaVersion && schemaToken?.Type == JTokenType.Integer
                ? schemaToken.Value<int>()
                : 0;
            bool legacy = storedSchemaVersion < AppConfig.CurrentSchemaVersion;
            if (legacy)
                config.SchemaVersion = storedSchemaVersion;
            config = Normalize(config, migrateLegacyDefaults: legacy);
            if (legacy)
                Save(config, dir);
            return config;
        }
        catch
        {
            return Normalize(new AppConfig(), migrateLegacyDefaults: false);
        }
    }

    public static void Save(AppConfig config, string? dir = null)
    {
        config = Normalize(config, migrateLegacyDefaults: false);
        string path = PathFor(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        string json = JsonConvert.SerializeObject(config, Formatting.Indented);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json);
        if (File.Exists(path))
            File.Replace(temporaryPath, path, null);
        else
            File.Move(temporaryPath, path);
    }

    internal static AppConfig Normalize(AppConfig config, bool migrateLegacyDefaults)
    {
        int incomingSchemaVersion = config.SchemaVersion;
        if (migrateLegacyDefaults)
        {
            // Historical builds installed F3/F4/F5/F6 globally. Clear only untouched defaults;
            // explicitly customized bindings survive migration.
            if (config.StartStopHotkey == "VcF5") config.StartStopHotkey = string.Empty;
            if (config.DebugHotkey == "VcF3") config.DebugHotkey = string.Empty;
            if (config.CalibrateHotkey == "VcF4") config.CalibrateHotkey = string.Empty;
            if (config.RumorManualScanHotkey == "VcF6") config.RumorManualScanHotkey = string.Empty;
            if (string.Equals(config.RumorCatalogPath, "rumor_catalog.json", StringComparison.OrdinalIgnoreCase))
                config.RumorCatalogPath = "rumor_catalog.default.json";
            // Schema 4 changes the safe default to explicit/manual scanning. Older builds stored
            // "auto" as an untouched default, so migrate it once to prevent background OCR from
            // probing unrelated parchment panels after an upgrade.
            if (incomingSchemaVersion < 4 &&
                string.Equals(config.RumorScanMode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                config.RumorScanMode = "manual";
            }
        }

        config.SchemaVersion = AppConfig.CurrentSchemaVersion;
        config.UiLanguage = NormalizeChoice(config.UiLanguage, "auto", "auto", "ru", "en");
        config.GameLanguage = NormalizeChoice(config.GameLanguage, "ru", "ru", "en");
        config.AppLanguage = NormalizeChoice(config.AppLanguage, "ru", "ru", "en");
        config.RumorOcrLanguage = NormalizeChoice(config.RumorOcrLanguage, "en", "en", "ru", "en+ru");
        config.DisplayThresholdCurrency = PriceDisplayFormatter.NormalizeThresholdCurrency(config.DisplayThresholdCurrency);
        config.DivineDisplayThreshold = PriceDisplayFormatter.NormalizeThreshold(config.DivineDisplayThreshold);
        config.PriceUnknownAttempts = Math.Clamp(config.PriceUnknownAttempts, 2, 10);
        config.DataRefreshIntervalMinutes = Math.Clamp(config.DataRefreshIntervalMinutes, 15, 240);
        config.LogLevel = AppLogLevelParser.Normalize(config.LogLevel);
        config.LogMaxFileSizeMb = Math.Clamp(config.LogMaxFileSizeMb, 1, 100);
        config.LogRetainedFiles = Math.Clamp(config.LogRetainedFiles, 1, 20);

        // panelClosed was removed: in practice it is too sensitive to capture/overlay jitter and
        // causes flicker. Existing configs are migrated to the stable manual-close mode.
        if (string.Equals(config.RumorOverlayHideMode, "panelClosed", StringComparison.OrdinalIgnoreCase))
            config.RumorOverlayHideMode = "never";
        config.RumorOverlayHideMode = NormalizeChoice(
            config.RumorOverlayHideMode,
            config.RumorOverlayTimeoutSeconds == 0 ? "never" : "timeout",
            "never", "timeout");
        config.RumorOverlayHideDelayMs = Math.Clamp(config.RumorOverlayHideDelayMs, 300, 2000);
        config.RumorOverlayTimeoutSeconds = Math.Clamp(config.RumorOverlayTimeoutSeconds, 1, 3600);
        config.RumorScanMode = NormalizeChoice(config.RumorScanMode, "manual", "auto", "manual");
        config.RumorSortMode = NormalizeChoice(config.RumorSortMode, "kindThenTier", "tier", "kindThenTier");
        config.RumorScanIntervalMs = Math.Clamp(config.RumorScanIntervalMs, 500, 5000);
        config.RumorConfirmationFrames = Math.Clamp(config.RumorConfirmationFrames, 2, 4);
        config.RumorConfirmationWindowMs = Math.Clamp(config.RumorConfirmationWindowMs, 1500, 6000);
        config.RumorCategoryOrder = NormalizeCategoryOrder(config.RumorCategoryOrder);
        config.RumorCatalogPath = string.IsNullOrWhiteSpace(config.RumorCatalogPath)
            ? "rumor_catalog.default.json"
            : config.RumorCatalogPath.Trim();
        config.RumorUserCatalogPath = string.IsNullOrWhiteSpace(config.RumorUserCatalogPath)
            ? "rumor_catalog.user.json"
            : config.RumorUserCatalogPath.Trim();

        config.StartStopHotkey = HotkeyBinding.NormalizeStorage(config.StartStopHotkey);
        config.PriceScanHotkey = HotkeyBinding.NormalizeStorage(config.PriceScanHotkey);
        config.DebugHotkey = HotkeyBinding.NormalizeStorage(config.DebugHotkey);
        config.CalibrateHotkey = HotkeyBinding.NormalizeStorage(config.CalibrateHotkey);
        config.RumorStartStopHotkey = HotkeyBinding.NormalizeStorage(config.RumorStartStopHotkey);
        config.RumorManualScanHotkey = HotkeyBinding.NormalizeStorage(config.RumorManualScanHotkey);
        config.RumorDebugHotkey = HotkeyBinding.NormalizeStorage(config.RumorDebugHotkey);
        return config;
    }

    private static string NormalizeChoice(string? value, string fallback, params string[] allowed)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return allowed.FirstOrDefault(candidate =>
                   string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
               ?? fallback;
    }

    private static List<string> NormalizeCategoryOrder(IEnumerable<string>? order)
    {
        string[] allowed = ["expedition", "boss", "unique"];
        var result = (order ?? [])
            .Select(value => value?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(allowed.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (string value in allowed)
        {
            if (!result.Contains(value, StringComparer.Ordinal))
                result.Add(value);
        }
        return result;
    }

    private static string PathFor(string? dir) =>
        Path.Combine(dir ?? AppContext.BaseDirectory, FileName);
}

using System.Drawing;
using Newtonsoft.Json;

namespace Poe2LootLens;

internal sealed class AppConfig
{
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string LeagueName { get; set; } = "Runes of Aldur";

    // Application UI language: auto | ru | en. Auto follows Windows and falls back to English.
    public string UiLanguage { get; set; } = "auto";

    // Language of the Path of Exile 2 client. It controls reward-name data used by the price scanner.
    public string GameLanguage { get; set; } = "ru";

    // Language used for community-maintained rumor descriptions in the overlay.
    public string AppLanguage { get; set; } = "ru";

    // OCR language for the in-game rumor tooltip: en | ru | en+ru. This is intentionally
    // independent from GameLanguage because reward recipes and Atlas rumors may use different languages.
    public string RumorOcrLanguage { get; set; } = "en";

    public string ItemAliasesPath { get; set; } = "item_aliases.json";

    [JsonIgnore]
    public List<string> AvailableLeagues { get; set; } = ["Runes of Aldur", "HC Runes of Aldur"];

    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }
    public int OverlayXOffset { get; set; } = 8;

    public decimal DivineDisplayThreshold { get; set; } = 1m;
    public string DisplayThresholdCurrency { get; set; } = "divine";
    public int PriceUnknownAttempts { get; set; } = 5;

    public int DataRefreshIntervalMinutes { get; set; } = 30;
    public string LogLevel { get; set; } = "info";
    public int LogMaxFileSizeMb { get; set; } = 10;
    public int LogRetainedFiles { get; set; } = 5;
    // Persisted so the close-to-tray explanation is shown only once for each user profile.
    public bool TrayHintShown { get; set; }

    public bool RumorScannerEnabled { get; set; }
    public string RumorCatalogPath { get; set; } = "rumor_catalog.default.json";
    public string RumorUserCatalogPath { get; set; } = "rumor_catalog.user.json";

    // never | timeout
    public string RumorOverlayHideMode { get; set; } = "never";
    // Legacy serialized value retained only so old config files round-trip cleanly; no UI uses it.
    public int RumorOverlayHideDelayMs { get; set; } = 650;
    public int RumorOverlayTimeoutSeconds { get; set; } = 15;

    // auto | manual. Manual is the safe default; it can be triggered from the main-window button
    // even when no global hotkey is configured. Auto means hover-driven experimental scanning.
    public string RumorScanMode { get; set; } = "manual";
    public string RumorManualScanHotkey { get; set; } = string.Empty;

    // tier | kindThenTier
    public string RumorSortMode { get; set; } = "kindThenTier";
    public List<string> RumorCategoryOrder { get; set; } = ["expedition", "boss", "unique"];

    // Kept for backwards compatibility with v10.x configs. Content-based island merging stays off.
    public bool RumorReassociateByContent { get; set; }

    public int RumorScanIntervalMs { get; set; } = 900;
    public int RumorConfirmationFrames { get; set; } = 2;
    public int RumorConfirmationWindowMs { get; set; } = 3000;

    // Hotkeys are opt-in. Empty means disabled and avoids conflicts with games or other software.
    // StartStopHotkey controls the price-scanner module; PriceScanHotkey requests a fresh analysis
    // without disabling the module.
    public string StartStopHotkey { get; set; } = string.Empty;
    public string PriceScanHotkey { get; set; } = string.Empty;
    public string DebugHotkey { get; set; } = string.Empty;
    public string CalibrateHotkey { get; set; } = string.Empty;

    // Rumor scanner has the same two-level control: module on/off and analysis now.
    public string RumorStartStopHotkey { get; set; } = string.Empty;
    public string RumorDebugHotkey { get; set; } = string.Empty;

    public string ReferencePixelColor { get; set; } = "#000000";
    public string CustomPricesPath { get; set; } = "custom_prices.json";

    public Rectangle RegionRect
    {
        get => new(RegionX, RegionY, RegionWidth, RegionHeight);
        set
        {
            RegionX = value.X;
            RegionY = value.Y;
            RegionWidth = value.Width;
            RegionHeight = value.Height;
        }
    }

    public bool IsCalibrated => RegionWidth > 0 && RegionHeight > 0;
}

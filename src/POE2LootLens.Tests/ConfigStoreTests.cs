using System.Drawing;
using System.IO;
using System.Linq;
using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        using var dir = new TempDir();
        var cfg = LoadFrom(dir.Path);
        Assert.Equal("Runes of Aldur", cfg.LeagueName);
        Assert.Equal(8, cfg.OverlayXOffset);
        Assert.Equal("custom_prices.json", cfg.CustomPricesPath);
        Assert.Equal(string.Empty, cfg.StartStopHotkey);
        Assert.False(cfg.IsCalibrated);
        Assert.Equal("en", cfg.RumorOcrLanguage);
        Assert.Equal("manual", cfg.RumorScanMode);
        Assert.False(cfg.StartMinimized);
        Assert.False(cfg.CloseToTray);
        Assert.False(cfg.AutoStartPriceScanner);
        Assert.False(cfg.AutoStartRumorScanner);
        Assert.False(cfg.RumorOverlayPinnedByDefault);
    }

    [Fact]
    public void StartStopHotkey_RoundTrips()
    {
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig { StartStopHotkey = "VcF7" });
        Assert.Equal("VcF7", LoadFrom(dir.Path).StartStopHotkey);
    }

    [Fact]
    public void RumorSorting_RoundTripsCategoryFirstOrder()
    {
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig
        {
            RumorSortMode = "kindThenTier",
            RumorCategoryOrder = ["unique", "boss", "expedition"],
        });

        AppConfig loaded = LoadFrom(dir.Path);

        Assert.Equal("kindThenTier", loaded.RumorSortMode);
        Assert.Equal(new[] { "unique", "boss", "expedition" }, loaded.RumorCategoryOrder);
    }

    [Fact]
    public void ConfigCopy_PreservesRumorCategoryOrderWithoutAppendingDefaults()
    {
        var source = new AppConfig
        {
            RumorSortMode = "kindThenTier",
            RumorCategoryOrder = ["unique", "boss", "expedition"],
        };

        AppConfig clone = ConfigCopy.Clone(source);

        Assert.Equal("kindThenTier", clone.RumorSortMode);
        Assert.Equal(new[] { "unique", "boss", "expedition" }, clone.RumorCategoryOrder);
    }

    [Fact]
    public void NewModuleAndScanHotkeys_RoundTrip()
    {
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig
        {
            PriceScanHotkey = "Ctrl+VcP",
            RumorStartStopHotkey = "Alt+VcR",
            RumorManualScanHotkey = "Shift+VcR",
            RumorDebugHotkey = "Ctrl+Shift+VcR",
        });

        AppConfig loaded = LoadFrom(dir.Path);
        Assert.Equal("Ctrl+VcP", loaded.PriceScanHotkey);
        Assert.Equal("Alt+VcR", loaded.RumorStartStopHotkey);
        Assert.Equal("Shift+VcR", loaded.RumorManualScanHotkey);
        Assert.Equal("Ctrl+Shift+VcR", loaded.RumorDebugHotkey);
    }

    [Fact]
    public void RumorOcrLanguage_RoundTripsAndNormalizes()
    {
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig { RumorOcrLanguage = "en+ru" });
        Assert.Equal("en+ru", LoadFrom(dir.Path).RumorOcrLanguage);

        SaveTo(dir.Path, new AppConfig { RumorOcrLanguage = "unsupported" });
        Assert.Equal("en", LoadFrom(dir.Path).RumorOcrLanguage);
    }

    [Fact]
    public void RoundTrip_AllFields()
    {
        using var dir = new TempDir();
        var original = new AppConfig
        {
            LeagueName = "Test League",
            RegionX = 10, RegionY = 20, RegionWidth = 300, RegionHeight = 400,
            OverlayXOffset = 16,
            ReferencePixelColor = "#AABBCC",
            CustomPricesPath = "my_prices.json",
            TrayHintShown = true,
            FirstRunCompleted = true,
            StartMinimized = true,
            CloseToTray = true,
            AutoStartPriceScanner = true,
            AutoStartRumorScanner = true,
            RumorOverlayPinnedByDefault = true,
            StartStopHotkey = "Ctrl+Shift+VcF8",
        };
        SaveTo(dir.Path, original);
        var loaded = LoadFrom(dir.Path);
        Assert.Equal("Test League", loaded.LeagueName);
        Assert.Equal(new Rectangle(10, 20, 300, 400), loaded.RegionRect);
        Assert.Equal(16, loaded.OverlayXOffset);
        Assert.Equal("#AABBCC", loaded.ReferencePixelColor);
        Assert.Equal("my_prices.json", loaded.CustomPricesPath);
        Assert.True(loaded.TrayHintShown);
        Assert.True(loaded.FirstRunCompleted);
        Assert.True(loaded.StartMinimized);
        Assert.True(loaded.CloseToTray);
        Assert.True(loaded.AutoStartPriceScanner);
        Assert.True(loaded.AutoStartRumorScanner);
        Assert.True(loaded.RumorOverlayPinnedByDefault);
        Assert.Equal("Ctrl+Shift+VcF8", loaded.StartStopHotkey);
    }

    [Fact]
    public void AvailableLeagues_NotDuplicated_OnRoundTrip()
    {
        // Newtonsoft's ObjectCreationHandling.Auto appends a deserialized list onto a pre-populated
        // default, doubling entries. AvailableLeagues is [JsonIgnore]'d to stay code-only and avoid it.
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig());
        var loaded = LoadFrom(dir.Path);
        Assert.Equal(new AppConfig().AvailableLeagues, loaded.AvailableLeagues);
        Assert.Equal(loaded.AvailableLeagues.Count, loaded.AvailableLeagues.Distinct().Count());
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenJsonMalformed()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "config.json"), "{ invalid json !!!");
        var cfg = LoadFrom(dir.Path);
        Assert.Equal("Runes of Aldur", cfg.LeagueName);
    }

    [Fact]
    public void Save_OverwritesExisting_AndLeavesNoTempFile()
    {
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig { LeagueName = "First" });
        SaveTo(dir.Path, new AppConfig { LeagueName = "Second" }); // exercises the File.Replace path
        Assert.Equal("Second", LoadFrom(dir.Path).LeagueName);
        // The atomic-swap temp file must not linger next to the real config.
        Assert.False(File.Exists(Path.Combine(dir.Path, "config.json.tmp")));
    }

    // Exercise the real ConfigStore (its path is injectable for exactly this reason) rather than
    // reimplementing the round-trip, so these tests cover the production load/save code.
    private static AppConfig LoadFrom(string dir) => ConfigStore.Load(dir);

    private static void SaveTo(string dir, AppConfig cfg) => ConfigStore.Save(cfg, dir);
}


public class ConfigMigrationTests
{
    [Fact]
    public void Load_MigratesHistoricalDefaultHotkeysToDisabled()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "config.json"), """
            {
              "startStopHotkey": "VcF5",
              "debugHotkey": "VcF3",
              "calibrateHotkey": "VcF4",
              "rumorManualScanHotkey": "VcF6",
              "rumorCatalogPath": "rumor_catalog.json"
            }
            """);

        AppConfig config = ConfigStore.Load(dir.Path);

        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Empty(config.StartStopHotkey);
        Assert.Empty(config.DebugHotkey);
        Assert.Empty(config.CalibrateHotkey);
        Assert.Empty(config.RumorManualScanHotkey);
        Assert.Equal("rumor_catalog.default.json", config.RumorCatalogPath);
    }


    [Fact]
    public void Load_MigratesLegacyAutomaticRumorDefaultToManual()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "config.json"), """
            {
              "schemaVersion": 3,
              "rumorScanMode": "auto",
              "rumorOcrLanguage": "en"
            }
            """);

        AppConfig config = ConfigStore.Load(dir.Path);

        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Equal("manual", config.RumorScanMode);
        Assert.Equal("en", config.RumorOcrLanguage);
    }

    [Fact]
    public void Normalize_PreservesCustomHotkeysAndCompletesCategoryOrder()
    {
        var config = new AppConfig
        {
            StartStopHotkey = "VcF8",
            RumorCategoryOrder = ["unique", "unique"],
            DataRefreshIntervalMinutes = 1,
        };

        AppConfig normalized = ConfigStore.Normalize(config, migrateLegacyDefaults: false);

        Assert.Equal("VcF8", normalized.StartStopHotkey);
        Assert.Equal(new[] { "unique", "expedition", "boss" }, normalized.RumorCategoryOrder);
        Assert.Equal(15, normalized.DataRefreshIntervalMinutes);
    }
}

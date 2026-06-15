using System.IO.Compression;
using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class DiagnosticsExporterTests
{
    [Fact]
    public async Task ExportAsync_CreatesExpectedSafeBundleAndRedactsPaths()
    {
        using var dir = new TempDir();
        string logs = Path.Combine(dir.Path, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "price-scan.log"), $"failure at {dir.Path}\\secret.txt");

        var priceState = new ModuleStateMachine();
        Assert.True(priceState.TryBeginStart());
        priceState.MarkRunning();
        var rumorState = new ModuleStateMachine();
        var snapshot = new DiagnosticsSnapshot(
            "0.9.0-beta",
            new AppConfig
            {
                RegionWidth = 100,
                RegionHeight = 200,
                StartStopHotkey = "Ctrl+Shift+VcF8",
            },
            priceState.Snapshot,
            rumorState.Snapshot,
            718,
            140m,
            DateTime.UtcNow,
            null);
        string destination = Path.Combine(dir.Path, "diagnostics.zip");

        await DiagnosticsExporter.ExportAsync(destination, snapshot, dir.Path);

        using ZipArchive archive = ZipFile.OpenRead(destination);
        string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("summary.txt", entries);
        Assert.Contains("application.json", entries);
        Assert.Contains("configuration.safe.json", entries);
        Assert.Contains("modules.json", entries);
        Assert.Contains("market.json", entries);
        Assert.Contains("displays.json", entries);
        Assert.Contains("logs/price-scan.log", entries);
        Assert.DoesNotContain("config.json", entries);

        ZipArchiveEntry logEntry = Assert.Single(archive.Entries.Where(entry => entry.FullName == "logs/price-scan.log"));
        using var reader = new StreamReader(logEntry.Open());
        string log = await reader.ReadToEndAsync();
        Assert.Contains("<APP_DIR>", log);
        Assert.False(log.Contains(dir.Path, StringComparison.OrdinalIgnoreCase));

        ZipArchiveEntry configEntry = Assert.Single(archive.Entries.Where(entry => entry.FullName == "configuration.safe.json"));
        using var configReader = new StreamReader(configEntry.Open());
        string safeConfiguration = await configReader.ReadToEndAsync();
        Assert.Contains("hotkeysConfigured", safeConfiguration);
        Assert.DoesNotContain("Ctrl+Shift+VcF8", safeConfiguration);
    }
}

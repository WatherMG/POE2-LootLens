using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace Poe2LootLens;

internal sealed record DiagnosticsSnapshot(
    string ApplicationVersion,
    AppConfig Config,
    ModuleStateSnapshot PriceModule,
    ModuleStateSnapshot RumorModule,
    int PriceItemCount,
    decimal ExaltedPerDivine,
    DateTime? PricesFetchedAt,
    string? PriceSourceError);

internal static class DiagnosticsExporter
{
    private const int FormatVersion = 1;

    public static Task ExportAsync(
        string destinationPath,
        DiagnosticsSnapshot snapshot,
        string? baseDirectory = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Export(destinationPath, snapshot, baseDirectory ?? AppContext.BaseDirectory, cancellationToken),
            cancellationToken);

    private static void Export(
        string destinationPath,
        DiagnosticsSnapshot snapshot,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(snapshot);

        string fullDestination = Path.GetFullPath(destinationPath);
        string? destinationDirectory = Path.GetDirectoryName(fullDestination);
        if (!string.IsNullOrEmpty(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        string temporaryPath = fullDestination + ".tmp";
        TryDelete(temporaryPath);

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddText(archive, "summary.txt", BuildSummary(snapshot));
                AddJson(archive, "application.json", BuildApplication(snapshot, baseDirectory));
                AddJson(archive, "configuration.safe.json", BuildSafeConfiguration(snapshot.Config));
                AddJson(archive, "modules.json", BuildModules(snapshot));
                AddJson(archive, "market.json", BuildMarket(snapshot));
                AddJson(archive, "displays.json", BuildDisplays());
                AddLogs(archive, Path.Combine(baseDirectory, "logs"), baseDirectory, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(fullDestination);
            File.Move(temporaryPath, fullDestination);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static object BuildApplication(DiagnosticsSnapshot snapshot, string baseDirectory)
    {
        using Process process = Process.GetCurrentProcess();
        string tessdata = Path.Combine(baseDirectory, "tessdata");
        return new
        {
            diagnosticsFormatVersion = FormatVersion,
            createdAtUtc = DateTime.UtcNow,
            applicationVersion = snapshot.ApplicationVersion,
            assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            operatingSystem = RuntimeInformation.OSDescription,
            framework = RuntimeInformation.FrameworkDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            is64BitProcess = Environment.Is64BitProcess,
            processorCount = Environment.ProcessorCount,
            currentCulture = CultureInfo.CurrentCulture.Name,
            currentUiCulture = CultureInfo.CurrentUICulture.Name,
            workingSetBytes = process.WorkingSet64,
            privateMemoryBytes = process.PrivateMemorySize64,
            appDirectoryWritable = IsDirectoryWritable(baseDirectory),
            files = new
            {
                configurationExists = File.Exists(Path.Combine(baseDirectory, "config.json")),
                logsDirectoryExists = Directory.Exists(Path.Combine(baseDirectory, "logs")),
                englishOcrModelExists = File.Exists(Path.Combine(tessdata, "eng.traineddata")),
                russianOcrModelExists = File.Exists(Path.Combine(tessdata, "rus.traineddata")),
                defaultRumorCatalogExists = File.Exists(Path.Combine(baseDirectory, "rumor_catalog.default.json")),
            },
        };
    }

    private static object BuildSafeConfiguration(AppConfig config) => new
    {
        config.SchemaVersion,
        config.UiLanguage,
        config.GameLanguage,
        rumorDescriptionLanguage = config.AppLanguage,
        config.RumorOcrLanguage,
        config.LeagueName,
        captureArea = new
        {
            configured = config.IsCalibrated,
            config.RegionX,
            config.RegionY,
            config.RegionWidth,
            config.RegionHeight,
            config.OverlayXOffset,
        },
        priceScanner = new
        {
            config.DivineDisplayThreshold,
            config.DisplayThresholdCurrency,
            config.PriceUnknownAttempts,
            autoStart = config.AutoStartPriceScanner,
        },
        rumorScanner = new
        {
            autoStart = config.AutoStartRumorScanner,
            config.RumorScanMode,
            config.RumorOverlayHideMode,
            config.RumorOverlayTimeoutSeconds,
            config.RumorOverlayPinnedByDefault,
            config.RumorScanIntervalMs,
            config.RumorConfirmationFrames,
            config.RumorConfirmationWindowMs,
            config.RumorSortMode,
            config.RumorCategoryOrder,
            defaultCatalogFile = Path.GetFileName(config.RumorCatalogPath),
            userCatalogFile = Path.GetFileName(config.RumorUserCatalogPath),
        },
        application = new
        {
            config.StartMinimized,
            config.CloseToTray,
            config.DataRefreshIntervalMinutes,
            config.LogLevel,
            config.LogMaxFileSizeMb,
            config.LogRetainedFiles,
        },
        hotkeysConfigured = new
        {
            priceModule = !string.IsNullOrWhiteSpace(config.StartStopHotkey),
            priceScan = !string.IsNullOrWhiteSpace(config.PriceScanHotkey),
            calibration = !string.IsNullOrWhiteSpace(config.CalibrateHotkey),
            priceDebug = !string.IsNullOrWhiteSpace(config.DebugHotkey),
            rumorModule = !string.IsNullOrWhiteSpace(config.RumorStartStopHotkey),
            rumorScan = !string.IsNullOrWhiteSpace(config.RumorManualScanHotkey),
            rumorDebug = !string.IsNullOrWhiteSpace(config.RumorDebugHotkey),
        },
    };

    private static object BuildModules(DiagnosticsSnapshot snapshot) => new
    {
        priceScanner = SafeModuleSnapshot(snapshot.PriceModule),
        rumorScanner = SafeModuleSnapshot(snapshot.RumorModule),
    };

    private static object SafeModuleSnapshot(ModuleStateSnapshot snapshot) => new
    {
        state = snapshot.State.ToString(),
        snapshot.ChangedAtUtc,
        lastError = Redact(snapshot.LastError),
    };

    private static object BuildMarket(DiagnosticsSnapshot snapshot) => new
    {
        itemCount = snapshot.PriceItemCount,
        snapshot.ExaltedPerDivine,
        snapshot.PricesFetchedAt,
        sourceError = Redact(snapshot.PriceSourceError),
    };

    private static object BuildDisplays()
    {
        try
        {
            System.Drawing.Rectangle virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
            return new
            {
                virtualScreen = new
                {
                    x = virtualScreen.X,
                    y = virtualScreen.Y,
                    width = virtualScreen.Width,
                    height = virtualScreen.Height,
                },
                screens = System.Windows.Forms.Screen.AllScreens.Select(screen => new
                {
                    deviceName = screen.DeviceName,
                    isPrimary = screen.Primary,
                    bounds = new
                    {
                        x = screen.Bounds.X,
                        y = screen.Bounds.Y,
                        width = screen.Bounds.Width,
                        height = screen.Bounds.Height,
                    },
                    workingArea = new
                    {
                        x = screen.WorkingArea.X,
                        y = screen.WorkingArea.Y,
                        width = screen.WorkingArea.Width,
                        height = screen.WorkingArea.Height,
                    },
                    bitsPerPixel = screen.BitsPerPixel,
                }).ToArray(),
            };
        }
        catch (Exception exception)
        {
            return new
            {
                error = $"{exception.GetType().Name}: {Redact(exception.Message)}",
                screens = Array.Empty<object>(),
            };
        }
    }

    private static string BuildSummary(DiagnosticsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("POE2 LootLens diagnostics");
        builder.AppendLine($"Created (UTC): {DateTime.UtcNow:O}");
        builder.AppendLine($"Application: {snapshot.ApplicationVersion}");
        builder.AppendLine($"Price scanner: {snapshot.PriceModule.State}");
        builder.AppendLine($"Rumor scanner: {snapshot.RumorModule.State}");
        builder.AppendLine();
        builder.AppendLine("The archive contains application/runtime information, a safe configuration subset,");
        builder.AppendLine("module states, display bounds and redacted application logs.");
        builder.AppendLine("It does not contain screenshots, the full config file, hotkey values or user files.");
        return builder.ToString();
    }

    private static void AddLogs(
        ZipArchive archive,
        string logsDirectory,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(logsDirectory))
        {
            AddText(archive, "logs/README.txt", "No logs directory was found.");
            return;
        }

        string[] files = Directory.EnumerateFiles(logsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).Contains(".log", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            AddText(archive, "logs/README.txt", "The logs directory is empty.");
            return;
        }

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entryName = "logs/" + Path.GetFileName(file);
            try
            {
                using var source = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string content = reader.ReadToEnd();
                AddText(archive, entryName, Redact(content, baseDirectory));
            }
            catch (Exception exception)
            {
                AddText(
                    archive,
                    entryName + ".error.txt",
                    $"Could not read this log: {exception.GetType().Name}: {Redact(exception.Message, baseDirectory)}");
            }
        }
    }

    private static void AddJson(ZipArchive archive, string name, object value) =>
        AddText(archive, name, JsonConvert.SerializeObject(value, Formatting.Indented));

    private static void AddText(ZipArchive archive, string name, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    internal static string Redact(string? value, string? baseDirectory = null)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        string result = value;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
            result = ReplaceIgnoreCase(result, Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar), "<APP_DIR>");
        result = ReplaceIgnoreCase(result, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        result = ReplaceIgnoreCase(result, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "%TEMP%");
        return result;
    }

    private static string ReplaceIgnoreCase(string source, string oldValue, string newValue)
    {
        if (string.IsNullOrWhiteSpace(oldValue))
            return source;

        var result = new StringBuilder(source.Length);
        int start = 0;
        while (true)
        {
            int index = source.IndexOf(oldValue, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                result.Append(source, start, source.Length - start);
                return result.ToString();
            }
            result.Append(source, start, index - start);
            result.Append(newValue);
            start = index + oldValue.Length;
        }
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, $".diagnostics-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The original exception, if any, is more useful to the caller.
        }
    }
}

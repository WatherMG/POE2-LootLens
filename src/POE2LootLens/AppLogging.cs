using System.Text;

namespace Poe2LootLens;

internal enum AppLogLevel
{
    Error = 0,
    Warning = 1,
    Information = 2,
    Debug = 3,
    Trace = 4,
}

internal static class AppLogLevelParser
{
    public static AppLogLevel Parse(string? value) => Normalize(value) switch
    {
        "error" => AppLogLevel.Error,
        "warning" => AppLogLevel.Warning,
        "debug" => AppLogLevel.Debug,
        "trace" => AppLogLevel.Trace,
        _ => AppLogLevel.Information,
    };

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "error" => "error",
        "warning" or "warn" => "warning",
        "debug" => "debug",
        "trace" or "verbose" => "trace",
        _ => "info",
    };
}

internal sealed class RollingLogWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly AppLogLevel _minimumLevel;
    private StreamWriter? _writer;

    public RollingLogWriter(string fileName, AppConfig config)
    {
        _path = Path.Combine(AppContext.BaseDirectory, "logs", fileName);
        _maxBytes = Math.Max(1, config.LogMaxFileSizeMb) * 1024L * 1024L;
        _retainedFiles = Math.Max(1, config.LogRetainedFiles);
        AppLogLevel configuredLevel = AppLogLevelParser.Parse(config.LogLevel);
        _minimumLevel = App.DebugMode && configuredLevel < AppLogLevel.Debug
            ? AppLogLevel.Debug
            : configuredLevel;
        Open();
    }

    public AppLogLevel MinimumLevel => _minimumLevel;

    public bool IsEnabled(AppLogLevel level) => level <= _minimumLevel;

    public void Write(AppLogLevel level, string message)
    {
        if (!IsEnabled(level))
            return;

        lock (_sync)
        {
            try
            {
                EnsureCapacity();
                _writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LevelLabel(level)}] {message}");
            }
            catch
            {
                // Diagnostics must never break scanning.
            }
        }

        if ((App.DebugMode || _minimumLevel >= AppLogLevel.Debug) && level <= _minimumLevel)
        {
            string consoleMessage = $"[{Path.GetFileNameWithoutExtension(_path)}] {message}";
            Console.WriteLine(consoleMessage);
            System.Diagnostics.Debug.WriteLine(consoleMessage);
        }
    }

    private void Open()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _writer = new StreamWriter(
                new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false),
                4096)
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _writer = null;
        }
    }

    private void EnsureCapacity()
    {
        if (_writer is null)
        {
            Open();
            return;
        }

        if (_writer.BaseStream.Length < _maxBytes)
            return;

        _writer.Dispose();
        _writer = null;

        // _retainedFiles includes the active file. For example, a value of 5 keeps the current
        // file plus .1 through .4, never six files while claiming five in the UI.
        int backupCount = Math.Max(0, _retainedFiles - 1);
        if (backupCount == 0)
        {
            if (File.Exists(_path)) File.Delete(_path);
            Open();
            return;
        }

        string oldest = $"{_path}.{backupCount}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int index = backupCount - 1; index >= 1; index--)
        {
            string source = $"{_path}.{index}";
            string destination = $"{_path}.{index + 1}";
            if (File.Exists(source)) File.Move(source, destination);
        }
        string first = _path + ".1";
        if (File.Exists(first)) File.Delete(first);
        if (File.Exists(_path)) File.Move(_path, first);
        Open();
    }

    private static string LevelLabel(AppLogLevel level) => level switch
    {
        AppLogLevel.Error => "ERR",
        AppLogLevel.Warning => "WRN",
        AppLogLevel.Information => "INF",
        AppLogLevel.Debug => "DBG",
        _ => "TRC",
    };

    public void Dispose()
    {
        lock (_sync)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}

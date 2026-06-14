using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using SharpHook;
using SharpHook.Data;

namespace Poe2LootLens;

public partial class App : System.Windows.Application
{
    internal static bool DebugMode { get; private set; }
    private TaskPoolGlobalHook? _hook;
    private HotkeyModifiers _modifiersDown;

    // Hotkeys are opt-in and may contain Ctrl/Alt/Shift modifiers. They are read on the
    // SharpHook worker thread and edited on the WPF dispatcher thread, so access is synchronized.
    private static readonly object HotkeySync = new();
    private static HotkeyGesture? _startStopGesture;
    private static HotkeyGesture? _priceScanGesture;
    private static HotkeyGesture? _debugGesture;
    private static HotkeyGesture? _calibrateGesture;
    private static HotkeyGesture? _rumorStartStopGesture;
    private static HotkeyGesture? _rumorManualScanGesture;
    private static HotkeyGesture? _rumorDebugGesture;

    internal static void SetStartStopGesture(HotkeyGesture? gesture) => SetBinding(gesture, HotkeyBinding.Action.StartStop);
    internal static void SetPriceScanGesture(HotkeyGesture? gesture) => SetBinding(gesture, HotkeyBinding.Action.PriceScan);
    internal static void SetDebugGesture(HotkeyGesture? gesture) => SetBinding(gesture, HotkeyBinding.Action.Debug);
    internal static void SetCalibrateGesture(HotkeyGesture? gesture) => SetBinding(gesture, HotkeyBinding.Action.Calibrate);
    internal static void SetRumorStartStopGesture(HotkeyGesture? gesture) => SetBinding(gesture, HotkeyBinding.Action.RumorStartStop);
    internal static void SetRumorManualScanGesture(HotkeyGesture? gesture) => SetBinding(gesture, HotkeyBinding.Action.RumorManualScan);
    internal static void SetRumorDebugGesture(HotkeyGesture? gesture) => SetBinding(gesture, HotkeyBinding.Action.RumorDebug);

    // Compatibility wrappers for callers/tests that still use a single key.
    internal static void SetStartStopKey(KeyCode? key) => SetStartStopGesture(key is { } value ? new HotkeyGesture(value) : null);
    internal static void SetDebugKey(KeyCode? key) => SetDebugGesture(key is { } value ? new HotkeyGesture(value) : null);
    internal static void SetCalibrateKey(KeyCode? key) => SetCalibrateGesture(key is { } value ? new HotkeyGesture(value) : null);
    internal static void SetRumorManualScanKey(KeyCode? key) => SetRumorManualScanGesture(key is { } value ? new HotkeyGesture(value) : null);

    private static void SetBinding(HotkeyGesture? value, HotkeyBinding.Action action)
    {
        lock (HotkeySync)
        {
            switch (action)
            {
                case HotkeyBinding.Action.StartStop:
                    _startStopGesture = value;
                    break;
                case HotkeyBinding.Action.PriceScan:
                    _priceScanGesture = value;
                    break;
                case HotkeyBinding.Action.Debug:
                    _debugGesture = value;
                    break;
                case HotkeyBinding.Action.Calibrate:
                    _calibrateGesture = value;
                    break;
                case HotkeyBinding.Action.RumorStartStop:
                    _rumorStartStopGesture = value;
                    break;
                case HotkeyBinding.Action.RumorManualScan:
                    _rumorManualScanGesture = value;
                    break;
                case HotkeyBinding.Action.RumorDebug:
                    _rumorDebugGesture = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }
    }

    private readonly record struct HotkeySnapshot(
        HotkeyGesture? StartStop,
        HotkeyGesture? PriceScan,
        HotkeyGesture? Debug,
        HotkeyGesture? Calibrate,
        HotkeyGesture? RumorStartStop,
        HotkeyGesture? RumorManualScan,
        HotkeyGesture? RumorDebug);

    private static HotkeySnapshot ReadHotkeys()
    {
        lock (HotkeySync)
        {
            return new HotkeySnapshot(
                _startStopGesture,
                _priceScanGesture,
                _debugGesture,
                _calibrateGesture,
                _rumorStartStopGesture,
                _rumorManualScanGesture,
                _rumorDebugGesture);
        }
    }

    // One-shot rebind capture. While active, the hook swallows keys from their normal actions and the
    // next available key becomes the binding. Outcomes are reported via the callback, marshalled to the
    // UI thread. Reserved keys (or a key already bound to another action) report back but keep
    // listening; Esc cancels. _captureAction is the binding being replaced, so its own current key
    // doesn't count as a collision.
    internal enum CaptureOutcome { Captured, Cancelled, Reserved }
    private static volatile bool _capturing;
    private static volatile HotkeyBinding.Action _captureAction;
    private static Action<CaptureOutcome, HotkeyGesture>? _captureCallback;

    internal static void BeginHotkeyCapture(HotkeyBinding.Action action, Action<CaptureOutcome, HotkeyGesture> onEvent)
    {
        _captureAction = action;
        _captureCallback = onEvent;
        _capturing = true;
    }

    internal static void CancelHotkeyCapture()
    {
        _capturing = false;
        _captureCallback = null;
    }

    // Single-instance guard. Held for the lifetime of the process; a second launch fails to
    // create it, focuses the already-running window, and exits. Without this, every extra launch
    // is a full second app that also receives the global F3 hook and paints its own overlay —
    // which is how testers ended up seeing two or three calibration boxes at once.
    private static Mutex? _instanceMutex;
    private const string InstanceMutexName = @"Global\POE2LootLens.SingleInstance";

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    // Hide the overlay immediately (if it's up because a panel was detected) and pause detection
    // briefly so the closing panel's fading brightness can't re-trigger it.
    private static void DismissOverlay()
    {
        if (!ScanEngine.IsShowing) return;
        PriceOverlayManager.HideNow();   // instant, off the scan loop
        ScanEngine.RequestDismiss();     // keep it hidden until the panel actually closes
    }

    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless OCR repro: run the real OCR pipeline on a screenshot and print what it sees.
        //   POE2 LootLens.exe --ocr-test <imagePath>
        if (e.Args.Length >= 2 && e.Args[0] == "--ocr-test")
        {
            RunOcrTest(e.Args[1]);
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);
        AppFonts.ConfigureWpf(Resources);

        // Refuse to start a second copy: only one instance owns the global hook + overlay.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            FocusExistingInstance();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--debug"))
        {
            DebugMode = true;
            if (!AttachConsole(-1)) AllocConsole(); // attach to parent terminal, else open new window
            Console.WriteLine("[Debug] POE2 LootLens starting");
        }

        _hook = new TaskPoolGlobalHook();
        _hook.KeyPressed += (_, ev) =>
        {
            KeyCode code = ev.Data.KeyCode;
            if (HotkeyBinding.TryGetModifier(code, out HotkeyModifiers modifier))
                _modifiersDown |= modifier;

            if (_capturing)
                return;

            // ESC closes the in-game panel — hide the overlay the instant the key goes down.
            if (code == KeyCode.VcEscape)
                DismissOverlay();
        };
        _hook.KeyReleased += (_, ev) =>
        {
            KeyCode code = ev.Data.KeyCode;
            if (HotkeyBinding.TryGetModifier(code, out HotkeyModifiers releasedModifier))
            {
                _modifiersDown &= ~releasedModifier;
                return;
            }

            var gesture = new HotkeyGesture(code, _modifiersDown);
            if (_capturing)
            {
                HandleCapture(gesture);
                return;
            }

            // Act on release (not press) so holding a key can't auto-repeat-fire many times.
            HotkeySnapshot hotkeys = ReadHotkeys();
            if (hotkeys.Debug == gesture) PriceOverlayManager.ToggleDebug();
            else if (hotkeys.RumorDebug == gesture) RumorLineOverlayManager.ToggleDebug();
            else if (hotkeys.Calibrate == gesture) InvokeCalibrate();
            else if (hotkeys.PriceScan == gesture) InvokePriceScan();
            else if (hotkeys.RumorManualScan == gesture) InvokeManualRumorScan();
            else if (hotkeys.RumorStartStop == gesture) InvokeRumorStartStopToggle();
            else if (hotkeys.StartStop == gesture) InvokeStartStopToggle();
        };
        // Left-Ctrl + left click (the in-game "purchase" gesture) also dismisses the overlay.
        _hook.MousePressed += (_, ev) =>
        {
            if (_capturing) return;
            if (ev.Data.Button == MouseButton.Button1 && _modifiersDown.HasFlag(HotkeyModifiers.Control))
                DismissOverlay();
            if (ev.Data.Button == MouseButton.Button1)
                Current?.Dispatcher.BeginInvoke(() =>
                    (Current.MainWindow as MainWindow)?.NotifyGlobalMousePressed(System.Windows.Forms.Cursor.Position));
        };
        _ = _hook.RunAsync();
    }

    // Runs on a hook thread-pool thread. Esc cancels; a gesture already bound to another action
    // reports back but keeps listening; anything else is the new binding. Modifier-only input is
    // ignored because capture completes only when a normal key is released.
    private static void HandleCapture(HotkeyGesture gesture)
    {
        if (gesture.Key == KeyCode.VcEscape)
        {
            FinishCapture(CaptureOutcome.Cancelled, gesture);
            return;
        }
        if (HotkeyBinding.IsReserved(gesture.Key) || CollidesWithOtherAction(gesture, _captureAction))
        {
            ReportCapture(CaptureOutcome.Reserved, gesture);
            return;
        }
        FinishCapture(CaptureOutcome.Captured, gesture);
    }

    private static bool CollidesWithOtherAction(HotkeyGesture gesture, HotkeyBinding.Action target)
    {
        HotkeySnapshot hotkeys = ReadHotkeys();
        if (target != HotkeyBinding.Action.StartStop && hotkeys.StartStop == gesture) return true;
        if (target != HotkeyBinding.Action.PriceScan && hotkeys.PriceScan == gesture) return true;
        if (target != HotkeyBinding.Action.Debug && hotkeys.Debug == gesture) return true;
        if (target != HotkeyBinding.Action.Calibrate && hotkeys.Calibrate == gesture) return true;
        if (target != HotkeyBinding.Action.RumorStartStop && hotkeys.RumorStartStop == gesture) return true;
        if (target != HotkeyBinding.Action.RumorManualScan && hotkeys.RumorManualScan == gesture) return true;
        if (target != HotkeyBinding.Action.RumorDebug && hotkeys.RumorDebug == gesture) return true;
        return false;
    }

    private static void FinishCapture(CaptureOutcome outcome, HotkeyGesture gesture)
    {
        _capturing = false;
        var callback = _captureCallback;
        _captureCallback = null;
        ReportTo(callback, outcome, gesture);
    }

    private static void ReportCapture(CaptureOutcome outcome, HotkeyGesture gesture) =>
        ReportTo(_captureCallback, outcome, gesture);

    private static void ReportTo(
        Action<CaptureOutcome, HotkeyGesture>? callback,
        CaptureOutcome outcome,
        HotkeyGesture gesture)
    {
        if (callback is null) return;
        Current?.Dispatcher.BeginInvoke(() => callback(outcome, gesture));
    }

    private static void InvokeStartStopToggle() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.ToggleStartStop());

    private static void InvokeCalibrate() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.RunCalibration());

    private static void InvokePriceScan() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.RequestPriceScan());

    private static void InvokeRumorStartStopToggle() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.ToggleRumorScanner());

    private static void InvokeManualRumorScan() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.RequestManualRumorScan());

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _instanceMutex?.Dispose();
        AppFonts.Dispose();
        base.OnExit(e);
    }

    // Bring the already-running instance's window to the foreground so the user gets feedback that
    // the app is up (instead of "nothing happened, click again" — which spawned the extra copies).
    private static void FocusExistingInstance()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
                break;
            }
        }
        catch { /* best-effort focus; the guard still prevents the second instance */ }
    }

    private static void RunOcrTest(string imagePath)
    {
        var outPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ocr_test.txt");
        var lines = new List<string>();
        void Out(string s) => lines.Add(s);
        try
        {
            var config = ConfigStore.Load();
            var r = config.RegionRect;
            Out($"[ocr-test] image='{imagePath}' region={r}");
            using var full = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(imagePath);
            Out($"[ocr-test] image size {full.Width}x{full.Height}");

            // Crop the calibrated region (or use the whole image if it's already the region).
            var rect = System.Drawing.Rectangle.Intersect(
                new System.Drawing.Rectangle(0, 0, full.Width, full.Height), r);
            if (rect.Width <= 0 || rect.Height <= 0) rect = new System.Drawing.Rectangle(0, 0, full.Width, full.Height);
            using var region = new System.Drawing.Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(region))
                g.DrawImage(full, new System.Drawing.Rectangle(0, 0, rect.Width, rect.Height), rect, System.Drawing.GraphicsUnit.Pixel);

            var tessdata = System.IO.Path.Combine(AppContext.BaseDirectory, "tessdata");
            using var scanner = new OcrScanner(tessdata, Out, debug: true);   // --ocr-test wants the dump
            var rows = scanner.Scan(region);
            Out($"[ocr-test] merged {rows.Count} rows:");
            foreach (var row in rows)
                Out($"    y={row.CenterY} mult={row.Multiplier} norm='{row.NormalizedName}' raw='{row.RawText}'");
        }
        catch (Exception ex)
        {
            Out($"[ocr-test] ERROR {ex}");
        }
        try { System.IO.File.WriteAllLines(outPath, lines); } catch { }
    }
}

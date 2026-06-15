using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Poe2LootLens;

internal static class GameWindowDetector
{
    public static bool IsPathOfExileForeground()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;

        _ = GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
            return false;

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

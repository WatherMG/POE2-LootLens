using SharpHook.Data;

namespace Poe2LootLens;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

internal readonly record struct HotkeyGesture(KeyCode Key, HotkeyModifiers Modifiers = HotkeyModifiers.None)
{
    public override string ToString() => HotkeyBinding.ToStorage(this);
}

internal static class HotkeyBinding
{
    public enum Action
    {
        StartStop,
        PriceScan,
        Debug,
        Calibrate,
        RumorStartStop,
        RumorManualScan,
        RumorDebug,
    }

    public static readonly IReadOnlyList<KeyCode> Reserved =
    [
        KeyCode.VcEscape,
        KeyCode.VcLeftControl,
        KeyCode.VcRightControl,
    ];

    public static bool IsReserved(KeyCode key) => Reserved.Contains(key);

    public static bool TryGetModifier(KeyCode key, out HotkeyModifiers modifier)
    {
        modifier = key.ToString() switch
        {
            "VcLeftControl" or "VcRightControl" => HotkeyModifiers.Control,
            "VcLeftAlt" or "VcRightAlt" => HotkeyModifiers.Alt,
            "VcLeftShift" or "VcRightShift" => HotkeyModifiers.Shift,
            _ => HotkeyModifiers.None,
        };
        return modifier != HotkeyModifiers.None;
    }

    public static bool IsModifier(KeyCode key) => TryGetModifier(key, out _);

    // Backwards-compatible single-key API used by existing tests and old configuration files.
    public static string ToStorage(KeyCode key) => key.ToString();

    public static string ToStorage(HotkeyGesture gesture)
    {
        var parts = new List<string>(4);
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(gesture.Key.ToString());
        return string.Join('+', parts);
    }

    public static bool TryParse(string? stored, out KeyCode key)
    {
        key = default;
        HotkeyGesture? gesture = ParseGestureOptional(stored);
        if (gesture is null || gesture.Value.Modifiers != HotkeyModifiers.None)
            return false;
        key = gesture.Value.Key;
        return true;
    }

    public static HotkeyGesture? ParseGestureOptional(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        string[] parts = stored
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return null;

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        KeyCode? key = null;
        foreach (string part in parts)
        {
            if (part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Control;
                continue;
            }
            if (part.Equals("alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Alt;
                continue;
            }
            if (part.Equals("shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Shift;
                continue;
            }

            if (key is not null ||
                !Enum.TryParse(part, ignoreCase: false, out KeyCode parsed) ||
                !Enum.IsDefined(typeof(KeyCode), parsed) ||
                IsModifier(parsed))
            {
                return null;
            }
            key = parsed;
        }

        return key is null ? null : new HotkeyGesture(key.Value, modifiers);
    }

    public static KeyCode? ParseOptional(string? stored) =>
        TryParse(stored, out var key) ? key : null;

    // Invalid/empty values use F5 only as a local parser fallback; global hooks use gesture parsing
    // and therefore remain disabled.
    public static KeyCode Parse(string? stored) =>
        TryParse(stored, out var key) ? key : KeyCode.VcF5;

    public static string NormalizeStorage(string? stored) =>
        ParseGestureOptional(stored) is { } gesture ? ToStorage(gesture) : string.Empty;

    public static string Display(KeyCode key)
    {
        string name = key.ToString();
        return name.StartsWith("Vc", StringComparison.Ordinal) ? name[2..] : name;
    }

    public static string Display(HotkeyGesture gesture)
    {
        var parts = new List<string>(4);
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(Display(gesture.Key));
        return string.Join(" + ", parts);
    }

    public static string DisplayOptional(string? stored, string language = "ru") =>
        ParseGestureOptional(stored) is { } gesture
            ? Display(gesture)
            : string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
                ? "Not assigned"
                : "Не назначен";
}

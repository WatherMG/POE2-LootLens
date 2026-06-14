using System.Globalization;

namespace Poe2LootLens;

internal static class UiLanguage
{
    public static string Resolve(string? configured)
    {
        string value = configured?.Trim().ToLowerInvariant() ?? "auto";
        if (value is "ru" or "en")
            return value;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
            "ru",
            StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : "en";
    }

    public static bool IsEnglish(string? configured) => Resolve(configured) == "en";

    public static string Pick(string? configured, string russian, string english) =>
        IsEnglish(configured) ? english : russian;
}

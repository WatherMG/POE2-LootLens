using System.Text;
using System.Text.RegularExpressions;

namespace Poe2LootLens;

internal static class ItemNameNormalizer
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Replace('ё', 'е');

        // Keep Unicode letters and decimal digits. Preserve the multiplication sign because
        // Russian OCR may read the stack marker as x, Cyrillic х, or ×.
        normalized = Regex.Replace(
            normalized,
            @"[^\p{L}\p{Nd}\s×]",
            " ",
            RegexOptions.CultureInvariant);

        normalized = Regex.Replace(
            normalized,
            @"\s+",
            " ",
            RegexOptions.CultureInvariant);

        return normalized.Trim();
    }
}

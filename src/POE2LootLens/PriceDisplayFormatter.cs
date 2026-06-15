using System.Globalization;

namespace Poe2LootLens;

internal readonly record struct PriceDisplay(
    bool UseDivine,
    decimal UnitValue,
    decimal TotalValue,
    string Label);

internal static class PriceDisplayFormatter
{
    internal const string DivineThresholdCurrency = "divine";
    internal const string ExaltedThresholdCurrency = "exalted";

    public static PriceDisplay Format(
        decimal unitDivine,
        decimal unitExalted,
        int multiplier,
        decimal divineThreshold = 1m,
        string? thresholdCurrency = DivineThresholdCurrency,
        string? gameLanguage = "ru",
        bool quantityTrusted = true)
    {
        multiplier = Math.Max(1, multiplier);
        decimal displayThreshold = NormalizeThreshold(divineThreshold);
        thresholdCurrency = NormalizeThresholdCurrency(thresholdCurrency);

        decimal effectiveMultiplier = quantityTrusted ? multiplier : 1m;
        decimal totalDivine = unitDivine * effectiveMultiplier;
        decimal totalExalted = unitExalted * effectiveMultiplier;
        decimal comparisonValue = thresholdCurrency == ExaltedThresholdCurrency
            ? totalExalted
            : totalDivine;

        bool useDivine = comparisonValue >= displayThreshold && unitDivine > 0m;
        decimal unit = useDivine ? unitDivine : unitExalted;
        decimal total = unit * effectiveMultiplier;
        string format = useDivine ? "0.##" : "0.#";
        string quantitySuffix = IsRussian(gameLanguage) ? "шт." : "pcs.";

        string label = !quantityTrusted
            ? $"{unit.ToString(format, CultureInfo.InvariantCulture)} (× ? {quantitySuffix})"
            : multiplier > 1
                ? $"{total.ToString(format, CultureInfo.InvariantCulture)} " +
                  $"({unit.ToString(format, CultureInfo.InvariantCulture)} × {multiplier} {quantitySuffix})"
                : total.ToString(format, CultureInfo.InvariantCulture);

        return new PriceDisplay(useDivine, unit, total, label);
    }

    internal static decimal NormalizeThreshold(decimal value) =>
        value is > 0m and <= 100000m ? value : 1m;

    internal static string NormalizeThresholdCurrency(string? value) =>
        string.Equals(value, ExaltedThresholdCurrency, StringComparison.OrdinalIgnoreCase)
            ? ExaltedThresholdCurrency
            : DivineThresholdCurrency;

    private static bool IsRussian(string? language) =>
        string.IsNullOrWhiteSpace(language) ||
        language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
}

using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class PriceDisplayFormatterTests
{
    [Fact]
    public void ExpensiveSingleItem_UsesDivineValue()
    {
        var display = PriceDisplayFormatter.Format(1.423m, 185m, 1);

        Assert.True(display.UseDivine);
        Assert.Equal("1.42", display.Label);
    }

    [Fact]
    public void BundleCrossingOneDivine_UsesDivineValueForWholeReward()
    {
        var display = PriceDisplayFormatter.Format(0.7m, 91m, 2);

        Assert.True(display.UseDivine);
        Assert.Equal("1.4 (0.7 × 2 шт.)", display.Label);
    }

    [Fact]
    public void CheapBundle_RemainsInExaltedValue()
    {
        var display = PriceDisplayFormatter.Format(0.08m, 10.4m, 2);

        Assert.False(display.UseDivine);
        Assert.Equal("20.8 (10.4 × 2 шт.)", display.Label);
    }

    [Fact]
    public void DivineThreshold_KeepsValueInExaltedBelowThreshold()
    {
        var display = PriceDisplayFormatter.Format(
            1.5m,
            195m,
            1,
            divineThreshold: 2m,
            thresholdCurrency: PriceDisplayFormatter.DivineThresholdCurrency);

        Assert.False(display.UseDivine);
        Assert.Equal("195", display.Label);
    }

    [Fact]
    public void ExaltedThreshold_UsesDivineOnceTotalExaltedReachesThreshold()
    {
        var display = PriceDisplayFormatter.Format(
            1.5m,
            195m,
            1,
            divineThreshold: 180m,
            thresholdCurrency: PriceDisplayFormatter.ExaltedThresholdCurrency);

        Assert.True(display.UseDivine);
        Assert.Equal("1.5", display.Label);
    }

    [Fact]
    public void ExaltedThreshold_AppliesToWholeBundle()
    {
        var display = PriceDisplayFormatter.Format(
            0.45m,
            58.5m,
            2,
            divineThreshold: 100m,
            thresholdCurrency: PriceDisplayFormatter.ExaltedThresholdCurrency);

        Assert.True(display.UseDivine);
        Assert.Equal("0.9 (0.45 × 2 шт.)", display.Label);
    }

    [Fact]
    public void ConfigurableFractionalThreshold_UsesDivineAtThreshold()
    {
        var display = PriceDisplayFormatter.Format(3.1m, 403m, 1, divineThreshold: 3.1m);

        Assert.True(display.UseDivine);
        Assert.Equal("3.1", display.Label);
    }


    [Fact]
    public void EnglishBundle_UsesPcsSuffix()
    {
        var display = PriceDisplayFormatter.Format(
            0.08m,
            10.4m,
            3,
            gameLanguage: "en");

        Assert.Equal("31.2 (10.4 × 3 pcs.)", display.Label);
    }

    [Theory]
    [InlineData("exalted", "exalted")]
    [InlineData("EXALTED", "exalted")]
    [InlineData("divine", "divine")]
    [InlineData("unknown", "divine")]
    [InlineData(null, "divine")]
    public void ThresholdCurrency_IsNormalized(string? raw, string expected)
    {
        Assert.Equal(expected, PriceDisplayFormatter.NormalizeThresholdCurrency(raw));
    }

    [Fact]
    public void InvalidThreshold_FallsBackToOneDivine()
    {
        foreach (decimal threshold in new decimal[] { 0m, -1m, 100001m })
        {
            var display = PriceDisplayFormatter.Format(1m, 130m, 1, threshold);
            Assert.True(display.UseDivine);
        }
    }
}

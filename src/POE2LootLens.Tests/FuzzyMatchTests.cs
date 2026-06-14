using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class FuzzyMatchTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "abd", 1)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("vision", "viswn", 2)]
    public void Levenshtein_ComputesEditDistance(string a, string b, int expected)
    {
        Assert.Equal(expected, ScanEngine.Levenshtein(a, b));
    }

    // Real misreads from the scan log should clear the 0.84 fuzzy threshold against the
    // correct key, while an unrelated item should not.
    [Theory]
    [InlineData("greater viswn rune", "greater vision rune", true)]
    [InlineData("greater reblrth rune", "greater rebirth rune", true)]
    [InlineData("grgater inspiration rune", "greater inspiration rune", true)]
    [InlineData("greater vision rune", "greater rebirth rune", false)] // different item, must NOT match
    public void Similarity_AbsorbsMisreadsButNotWrongItems(string ocr, string key, bool shouldMatch)
    {
        int dist = ScanEngine.Levenshtein(ocr, key);
        double score = 1.0 - (double)dist / System.Math.Max(ocr.Length, key.Length);
        Assert.Equal(shouldMatch, score > 0.84);
    }

    // Uncut gems are pinned by type + level (no fuzzy), so the canonical key must carry both exactly.
    [Theory]
    [InlineData("uncut spirit gem level 19", "uncut spirit gem level 19")]
    [InlineData("uncut skill gem level 7", "uncut skill gem level 7")]
    [InlineData("uncut support gem level 3", "uncut support gem level 3")]
    // Boilerplate slips ("uncot", "ger", "levei") don't hide a gem or change the pinned key.
    [InlineData("uncot spirit gem level 19", "uncut spirit gem level 19")]
    public void TryResolveGemKey_PinsTypeAndLevel(string ocr, string expectedKey)
    {
        Assert.True(ScanEngine.TryResolveGemKey(ocr, out var key));
        Assert.Equal(expectedKey, key);
    }

    // A gem whose level can't be read is still recognised as a gem (so it never falls through to
    // fuzzy), but yields no key → the row shows '?' instead of guessing a neighbouring level.
    [Fact]
    public void TryResolveGemKey_GemWithoutLevel_RecognisedButNoKey()
    {
        Assert.True(ScanEngine.TryResolveGemKey("uncut spirit gem", out var key));
        Assert.Null(key);
    }

    // Non-gem names are left for the normal exact/prefix/fuzzy path.
    [Theory]
    [InlineData("greater vision rune")]
    [InlineData("exalted orb")]
    public void TryResolveGemKey_NonGem_ReturnsFalse(string ocr)
    {
        Assert.False(ScanEngine.TryResolveGemKey(ocr, out var key));
        Assert.Null(key);
    }
}

public class RewardClassificationTests
{
    [Theory]
    [InlineData("умение дождь клинков")]
    [InlineData("поддержка огненный снаряд")]
    [InlineData("уникальное кольцо")]
    [InlineData("очень редкий уникальный предмет")]
    [InlineData("5 шт случайной валюты")]
    [InlineData("талисман")]
    [InlineData("двуручная булава")]
    public void KnownUnpricedRewards_DoNotBecomeUnknownQuestionMarks(string name)
    {
        Assert.True(ScanEngine.IsKnownUnpricedReward(name));
    }

    [Theory]
    [InlineData("редкость")]
    [InlineData("предметы и монстры могут быть обычными")]
    [InlineData("вместе с редкостью монстров возрастает их сложность")]
    [InlineData("опшебньмы ный редкими жаль и")]
    [InlineData("козраелает ик спожиость")]
    public void TooltipText_IsNotRenderedAsRewardRow(string name)
    {
        Assert.True(ScanEngine.IsNonRewardUiText(name));
    }

    [Theory]
    [InlineData("возвышенный сплав")]
    [InlineData("сфера хаоса")]
    public void ActualRewardNames_AreNotFilteredAsTooltipText(string name)
    {
        Assert.False(ScanEngine.IsNonRewardUiText(name));
    }
}

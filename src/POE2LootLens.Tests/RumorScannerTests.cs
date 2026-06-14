using System.Drawing;
using System.Drawing.Imaging;
using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class RumorScannerTests
{
    [Fact]
    public void TryFindRumorPanelBounds_FindsLargeLocalParchmentPanel()
    {
        using var bitmap = new Bitmap(960, 900, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var parchment = new SolidBrush(Color.FromArgb(198, 170, 112));
        graphics.Clear(Color.FromArgb(18, 23, 31));
        graphics.FillRectangle(
            parchment,
            new Rectangle(170, 120, 610, 520));

        bool found = RumorScanner.TryFindRumorPanelBounds(bitmap, out Rectangle bounds);

        Assert.True(found);
        Assert.True(bounds.IntersectsWith(new Rectangle(170, 120, 610, 520)));
        Assert.InRange(bounds.Width, 580, 670);
        Assert.InRange(bounds.Height, 490, 580);
    }

    [Theory]
    [InlineData(100, 100, 760, 650, true)]
    [InlineData(40, 40, 360, 300, false)]
    [InlineData(560, 470, 340, 300, false)]
    public void PanelInteriorContainsCursor_RejectsBookUnderCursor(
        int x,
        int y,
        int width,
        int height,
        bool expected)
    {
        Assert.Equal(
            expected,
            RumorScanner.PanelInteriorContainsCursor(new Rectangle(x, y, width, height)));
    }

    [Fact]
    public void TryFindRumorPanelBounds_RejectsDarkFrame()
    {
        using var bitmap = new Bitmap(960, 900, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(18, 23, 31));

        Assert.False(RumorScanner.TryFindRumorPanelBounds(bitmap, out _));
    }

    [Fact]
    public void TryFindRumorPanelBounds_RejectsNearlyFullFrameParchment()
    {
        using var bitmap = new Bitmap(960, 900, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(198, 170, 112));

        Assert.False(RumorScanner.TryFindRumorPanelBounds(bitmap, out _));
    }

    [Theory]
    [InlineData(464, 304, true)]
    [InlineData(396, 304, true)]
    [InlineData(616, 604, false)]
    [InlineData(452, 880, false)]
    [InlineData(264, 144, false)]
    public void PlausiblePanelGeometry_RejectsRewardBookAndTinyFragments(
        int width,
        int height,
        bool expected)
    {
        Assert.Equal(
            expected,
            RumorScanner.IsPlausibleRumorPanelGeometry(new Rectangle(0, 0, width, height)));
    }

    [Theory]
    [InlineData("en", "eng")]
    [InlineData("ru", "rus")]
    [InlineData("en+ru", "eng+rus")]
    public void RumorOcrLanguage_IsIndependentFromClientLanguage(string setting, string expected)
    {
        Assert.Equal(expected, RumorScanner.ResolveRumorOcrLanguages(setting));
    }

    [Fact]
    public void IsConfirmedRumorPanel_AcceptsSingleExactCatalogMatchWithoutHeader()
    {
        var entry = new RumorCatalogEntry
        {
            Id = "one",
            Phrases = ["Endless cliffs"],
        };
        RumorMatch[] matches = [new(entry, "Endless cliffs", 1d, true)];

        Assert.True(RumorScanner.IsConfirmedRumorPanel(false, matches));
        Assert.True(RumorScanner.IsConfirmedRumorPanel(true, matches));
    }


    [Fact]
    public void IsConfirmedRumorPanel_AcceptsSingleExactMatchFromDedicatedRumorSlot()
    {
        var entry = new RumorCatalogEntry
        {
            Id = "one",
            Phrases = ["Endless cliffs"],
        };
        RumorMatch[] matches = [new(entry, "Endless cliffs", 1d, true)];

        Assert.True(RumorScanner.IsConfirmedRumorPanel(false, matches, trustedSlotMatch: true));
    }

    [Fact]
    public void IsConfirmedRumorPanel_AcceptsTwoExactMatchesWhenHeaderWasMissed()
    {
        RumorMatch[] matches =
        [
            new(new RumorCatalogEntry { Id = "one", Phrases = ["Endless cliffs"] }, "Endless cliffs", 1d, true),
            new(new RumorCatalogEntry { Id = "two", Phrases = ["Bleak and awful"] }, "Bleak and awful", 1d, true),
        ];

        Assert.True(RumorScanner.IsConfirmedRumorPanel(false, matches));
    }


    [Fact]
    public void StrongExactPanelEvidence_ConfirmsAllExactLinesWithoutSecondFrame()
    {
        RumorMatch[] matches =
        [
            new(new RumorCatalogEntry { Id = "one", Phrases = ["A good fellow"] }, "A good fellow", 1d, true),
            new(new RumorCatalogEntry { Id = "two", Phrases = ["Origin of the fall"] }, "Origin of the fall", 1d, true),
            new(new RumorCatalogEntry { Id = "three", Phrases = ["Cold as ice"] }, "Cold as ice", 1d, true),
        ];

        Assert.True(RumorScanner.HasStrongExactPanelEvidence(matches));
    }

    [Fact]
    public void GetRumorLineSlotBounds_CoversAllThreeRelativeRumorPositions()
    {
        IReadOnlyList<Rectangle> slots = RumorScanner.GetRumorLineSlotBounds(new Size(600, 500));

        Assert.Equal(3, slots.Count);
        Assert.All(slots, slot =>
        {
            Assert.True(slot.Width > 400);
            Assert.InRange(slot.Top, 180, 385);
            Assert.InRange(slot.Bottom, 270, 475);
        });
        Assert.True(slots[0].Top < slots[1].Top);
        Assert.True(slots[1].Top < slots[2].Top);
    }

    [Theory]
    [InlineData(468, 192, 44, 88, 132)]
    [InlineData(464, 236, 80, 127, 172)]
    [InlineData(396, 304, 146, 201, 255)]
    public void GetRumorLineSlotBounds_TracksCompactNormalAndTallPanels(
        int width,
        int height,
        int firstCenter,
        int secondCenter,
        int thirdCenter)
    {
        IReadOnlyList<Rectangle> slots = RumorScanner.GetRumorLineSlotBounds(new Size(width, height));
        int[] expectedCenters = [firstCenter, secondCenter, thirdCenter];

        Assert.Equal(3, slots.Count);
        for (int index = 0; index < slots.Count; index++)
            Assert.InRange(slots[index].Top + slots[index].Height / 2, expectedCenters[index] - 3, expectedCenters[index] + 3);
    }

    [Fact]
    public void GetRumorLineSlotBounds_TracksMeasuredUnchartedWatersPanel()
    {
        // Measured from the supplied 582×634 screenshot after the parchment detector crops the
        // 456×300 rumor area: line centres are approximately 144, 198 and 252 pixels.
        IReadOnlyList<Rectangle> slots = RumorScanner.GetRumorLineSlotBounds(new Size(456, 300));
        int[] expected = [144, 198, 252];

        Assert.Equal(3, slots.Count);
        for (int index = 0; index < slots.Count; index++)
            Assert.InRange(slots[index].Top + slots[index].Height / 2, expected[index] - 3, expected[index] + 3);
    }

    [Fact]
    public void RumorFingerprint_IgnoresHeaderDecorationButChangesWithLineContent()
    {
        using var bitmap = new Bitmap(456, 300, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(190, 174, 145));
        IReadOnlyList<Rectangle> slots = RumorScanner.GetRumorLineSlotBounds(bitmap.Size);
        using var ink = new SolidBrush(Color.FromArgb(28, 24, 20));
        graphics.FillRectangle(ink, slots[0].Left + 90, slots[0].Top + 22, 110, 8);

        ulong original = RumorScanner.ComputeRumorContentFingerprint(bitmap);
        graphics.FillRectangle(Brushes.Black, 10, 10, 80, 20); // outside all three slots
        ulong headerChanged = RumorScanner.ComputeRumorContentFingerprint(bitmap);
        graphics.FillRectangle(ink, slots[1].Left + 120, slots[1].Top + 20, 90, 10);
        ulong rumorChanged = RumorScanner.ComputeRumorContentFingerprint(bitmap);

        Assert.Equal(original, headerChanged);
        Assert.NotEqual(original, rumorChanged);
    }

    [Fact]
    public void RumorFingerprintSimilarity_AllowsOneJitterBitPerLineButRejectsChangedLine()
    {
        const ulong lineOneBit = 1UL << 2;
        const ulong lineTwoBit = 1UL << (21 + 5);
        const ulong lineThreeBit = 1UL << (42 + 7);

        Assert.True(RumorScanner.AreRumorFingerprintsEquivalent(
            0UL,
            lineOneBit | lineTwoBit | lineThreeBit));
        Assert.True(RumorScanner.AreRumorFingerprintsEquivalent(
            0UL,
            lineOneBit | (1UL << 3)));
        Assert.False(RumorScanner.AreRumorFingerprintsEquivalent(
            0UL,
            lineOneBit | (1UL << 3) | (1UL << 4)));
        Assert.False(RumorScanner.AreRumorFingerprintsEquivalent(
            0UL,
            1UL << 20)); // occupancy changed: a blank line became a rumor
    }

    [Fact]
    public void CenteredRumorTextGate_RejectsBlankParchmentAndAcceptsCursiveInk()
    {
        using var bitmap = new Bitmap(402, 54, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(190, 174, 145));
        using var separator = new Pen(Color.FromArgb(82, 72, 58), 2f);
        graphics.DrawLine(separator, 0, 1, bitmap.Width - 1, 1);
        graphics.DrawLine(separator, 0, bitmap.Height - 2, bitmap.Width - 1, bitmap.Height - 2);

        Assert.False(RumorScanner.HasCenteredRumorText(bitmap));

        using var ink = new SolidBrush(Color.FromArgb(28, 24, 20));
        for (int index = 0; index < 15; index++)
            graphics.FillRectangle(ink, 105 + index * 12, 20 + index % 4, 7, 16);

        Assert.True(RumorScanner.HasCenteredRumorText(bitmap));
    }

    [Fact]
    public void ResolvedRumorLines_TreatsTwoMatchesAndBlankThirdSlotAsComplete()
    {
        Assert.True(RumorScanner.AreAllVisibleRumorLinesResolved(
        [
            RumorLineRecognitionStatus.Matched,
            RumorLineRecognitionStatus.Matched,
            RumorLineRecognitionStatus.Empty,
        ]));
        Assert.False(RumorScanner.AreAllVisibleRumorLinesResolved(
        [
            RumorLineRecognitionStatus.Matched,
            RumorLineRecognitionStatus.Unmatched,
            RumorLineRecognitionStatus.Empty,
        ]));
    }

    [Fact]
    public void FullPanelFallback_MarksTwoStrongRowsAndLeavesBlankThirdSlotEmpty()
    {
        RumorLineRecognitionStatus[] statuses =
        [
            RumorLineRecognitionStatus.Unmatched,
            RumorLineRecognitionStatus.Unmatched,
            RumorLineRecognitionStatus.Unmatched,
        ];
        double[] inkScores = [0.052d, 0.061d, 0.004d];

        RumorScanner.ApplyFullPanelMatchStatuses(statuses, inkScores, reliableMatchCount: 2);

        Assert.Equal(RumorLineRecognitionStatus.Matched, statuses[0]);
        Assert.Equal(RumorLineRecognitionStatus.Matched, statuses[1]);
        Assert.Equal(RumorLineRecognitionStatus.Empty, statuses[2]);
    }

    [Fact]
    public void FindNearestIslandIndex_UsesNearestAnchorInsideRadius()
    {
        Point[] anchors = [new(100, 100), new(300, 300), new(500, 500)];

        Assert.Equal(1, RumorScanner.FindNearestIslandIndex(anchors, new Point(325, 285), 80));
        Assert.Equal(-1, RumorScanner.FindNearestIslandIndex(anchors, new Point(700, 700), 80));
    }

    [Fact]
    public void ToScreenPanelBounds_UsesCaptureOriginAroundCursor()
    {
        Rectangle screen = RumorScanner.ToScreenPanelBounds(
            new Point(1000, 700),
            new Rectangle(240, 80, 460, 260));

        Assert.Equal(new Rectangle(760, 330, 460, 260), screen);
    }
}

public class RumorOverlayTierTests
{

    [Fact]
    public void OverlayPlacement_AvoidsGamePanelWhenScreenHasSpace()
    {
        var workingArea = new Rectangle(0, 0, 2560, 1440);
        var gamePanel = new Rectangle(200, 100, 520, 500);

        Rectangle overlay = RumorOverlayForm.CalculateBoundsWithinWorkingArea(
            new Point(480, 680),
            gamePanel,
            new Size(720, 500),
            workingArea);

        Assert.True(Rectangle.Intersect(overlay, gamePanel).IsEmpty);
        Assert.True(workingArea.Contains(overlay));
    }

    [Fact]
    public void OverlayPlacement_AvoidsShipAnchorAsWellAsPanel()
    {
        var workingArea = new Rectangle(0, 0, 1600, 900);
        var gamePanel = new Rectangle(388, 184, 468, 304);
        var anchor = new Point(849, 819);

        Rectangle overlay = RumorOverlayForm.CalculateBoundsWithinWorkingArea(
            anchor,
            gamePanel,
            new Size(600, 500),
            workingArea);
        var anchorGuard = new Rectangle(anchor.X - 58, anchor.Y - 58, 116, 116);

        Assert.True(Rectangle.Intersect(overlay, gamePanel).IsEmpty);
        Assert.True(Rectangle.Intersect(overlay, anchorGuard).IsEmpty);
        Assert.True(workingArea.Contains(overlay));
    }

    [Fact]
    public void OverlayPlacement_IgnoresDetectorJitterButReactsToRealPanelMove()
    {
        var original = new Rectangle(400, 200, 464, 304);

        Assert.False(RumorOverlayForm.HasMaterialPanelMove(
            original,
            new Rectangle(412, 190, 468, 308)));
        Assert.True(RumorOverlayForm.HasMaterialPanelMove(
            original,
            new Rectangle(920, 520, 464, 304)));
    }

    [Fact]
    public void OrderForDisplay_PutsHighestTierAboveCurrentLowerTier()
    {
        var lowCurrent = new RumorDisplayItem(
            new RumorMatch(
                new RumorCatalogEntry { Id = "low", Phrases = ["low"], Rating = "B" },
                "low",
                1d,
                true),
            true);
        var highHistorical = new RumorDisplayItem(
            new RumorMatch(
                new RumorCatalogEntry { Id = "high", Phrases = ["high"], Rating = "S+" },
                "high",
                1d,
                true),
            false);

        var ordered = RumorOverlayForm.OrderForDisplay(new[] { lowCurrent, highHistorical });

        Assert.Equal("high", ordered[0].Match.Entry.Id);
        Assert.Equal("low", ordered[1].Match.Entry.Id);
    }

    [Fact]
    public void OrderForDisplay_CanPreferKindBeforeTier()
    {
        var boss = new RumorDisplayItem(
            new RumorMatch(
                new RumorCatalogEntry { Id = "boss", Kind = "boss", Phrases = ["boss"], Rating = "B" },
                "boss",
                1d,
                true),
            false);
        var expedition = new RumorDisplayItem(
            new RumorMatch(
                new RumorCatalogEntry { Id = "expedition", Kind = "expedition", Phrases = ["expedition"], Rating = "S+" },
                "expedition",
                1d,
                true),
            false);

        var ordered = RumorOverlayForm.OrderForDisplay(
            new[] { boss, expedition },
            sortMode: "kindThenTier",
            categoryOrder: ["boss", "expedition", "unique"]);

        Assert.Equal("boss", ordered[0].Match.Entry.Id);
        Assert.Equal("expedition", ordered[1].Match.Entry.Id);
    }

    [Fact]
    public void OrderForDisplay_HonorsSavedThreeCategoryOrderExactly()
    {
        static RumorDisplayItem Item(string id, string kind) => new(
            new RumorMatch(
                new RumorCatalogEntry
                {
                    Id = id,
                    Kind = kind,
                    Phrases = [id],
                    Rating = "S+",
                },
                id,
                1d,
                true),
            false);

        IReadOnlyList<RumorDisplayItem> ordered = RumorOverlayForm.OrderForDisplay(
            [Item("expedition", "expedition"), Item("boss", "boss"), Item("unique", "unique")],
            sortMode: "kindThenTier",
            categoryOrder: ["unique", "boss", "expedition"]);

        Assert.Equal(
            new[] { "unique", "boss", "expedition" },
            ordered.Select(item => item.Match.Entry.Id));
    }

    [Theory]
    [InlineData("S+", "S", true)]
    [InlineData("S", "A", true)]
    [InlineData("A", "B", true)]
    [InlineData("B", "", true)]
    public void TierRank_OrdersUserFacingTiers(string better, string worse, bool expected)
    {
        Assert.Equal(
            expected,
            RumorOverlayForm.TierRank(better) > RumorOverlayForm.TierRank(worse));
    }
}

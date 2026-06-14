using System.Drawing;
using System.Drawing.Imaging;
using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class PerformanceOptimizationTests
{
    [Fact]
    public void OverlayBounds_AreSmallerThanWholeMonitor_AndContainRegion()
    {
        var monitor = new Rectangle(0, 0, 3840, 2160);
        var region = new Rectangle(300, 200, 900, 1200);

        var overlay = PriceOverlayManager.CalculateOverlayBounds(region, monitor, 8);

        Assert.True(overlay.Contains(region));
        Assert.True(overlay.Width < monitor.Width);
        Assert.True(overlay.Height < monitor.Height);
    }

    [Fact]
    public void Fingerprint_IsStableForSamePixels_AndChangesForPanelContent()
    {
        using var bitmap = new Bitmap(160, 120);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.Clear(Color.FromArgb(40, 50, 60));

        ulong first = ScreenCapture.ComputeFingerprint(bitmap);
        ulong second = ScreenCapture.ComputeFingerprint(bitmap);
        Assert.Equal(first, second);

        using (var graphics = Graphics.FromImage(bitmap))
            graphics.FillRectangle(Brushes.White, 60, 40, 40, 40);

        ulong changed = ScreenCapture.ComputeFingerprint(bitmap);
        Assert.NotEqual(first, changed);
    }

    [Theory]
    [InlineData("Стекольная масса (3)", "Стекольная масса", 3)]
    [InlineData("Стекольная масса {2)", "Стекольная масса", 2)]
    [InlineData("Сфера алхимии [3].", "Сфера алхимии", 3)]
    public void BracketedBundleCount_IsExtractedFromRawOcr(
        string raw,
        string expectedName,
        int expectedCount)
    {
        int count = OcrScanner.ExtractTrailingBundleCount(raw, out var name);

        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("Чародейский расплав Уровень 19")]
    [InlineData("Путевой камень 15")]
    [InlineData("Руна 3")]
    public void BareTrailingNumber_IsNeverTreatedAsBundle(string raw)
    {
        int count = OcrScanner.ExtractTrailingBundleCount(raw, out var name);

        Assert.Equal(1, count);
        Assert.Equal(raw, name);
    }

    [Fact]
    public void RowGeometry_DetectsRegularHorizontalSeparators()
    {
        using var bitmap = new Bitmap(700, 500, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(180, 160, 120));
            using var rowBrush = new SolidBrush(Color.FromArgb(245, 238, 215));
            using var linePen = new Pen(Color.FromArgb(40, 35, 30), 3);

            const int spacing = 62;
            for (int row = 0; row < 6; row++)
            {
                int top = row * spacing;
                graphics.FillRectangle(rowBrush, 210, top, 470, spacing);
                graphics.DrawLine(linePen, 210, top, 680, top);
            }
            graphics.DrawLine(linePen, 210, 6 * spacing, 680, 6 * spacing);
        }

        var bands = OcrScanner.DetectRowBands(bitmap);

        Assert.Equal(6, bands.Count);
        Assert.All(bands, band => Assert.InRange(band.Height, 55, 70));
    }
}

public class RecognitionSafetyTests
{
    [Fact]
    public void BundleMultiplier_IsAppliedOnlyToExactIdentity()
    {
        var row = new OcrRow(
            "стекольная масса",
            "Стекольная масса (3)",
            100,
            3,
            90f,
            "gray",
            1,
            3,
            true);

        Assert.Equal(3, ScanEngine.ResolveEffectiveMultiplier(row, exactIdentity: true));
        Assert.Equal(1, ScanEngine.ResolveEffectiveMultiplier(row, exactIdentity: false));
    }

    [Theory]
    [InlineData(true, true, 1, true)]
    [InlineData(true, false, 2, true)]
    [InlineData(true, false, 1, false)]
    [InlineData(false, true, 3, false)]
    public void PricePanelConfirmation_RequiresStructureOrRepeatedSingleRow(
        bool hasLockedPrice,
        bool strongStructure,
        int repeatedFrames,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScanEngine.HasSufficientPanelEvidence(hasLockedPrice, strongStructure, repeatedFrames));
    }

    [Fact]
    public void RuneAndQuantityMatches_RequireTemporalConsensus()
    {
        var rune = new PriceRow(
            100,
            "Руна акробатики (1)",
            0.1m,
            3.9m,
            true,
            1,
            "руна акробатики",
            true,
            MemeKind.None,
            92f,
            "exact",
            1d,
            "rune-of-acrobatics",
            "gray",
            true,
            1);
        var bundle = rune with
        {
            Name = "стекольная масса",
            OcrText = "Стекольная масса (3)",
            PriceSourceId = "bauble",
            Multiplier = 3,
            BundleCount = 3,
        };
        var ordinary = rune with
        {
            Name = "сфера алхимии",
            OcrText = "Сфера алхимии (1)",
            PriceSourceId = "alch",
        };

        Assert.Equal(2, ScanEngine.RequiredConfirmations(rune));
        Assert.Equal(2, ScanEngine.RequiredConfirmations(bundle));
        Assert.Equal(1, ScanEngine.RequiredConfirmations(ordinary));
    }
}

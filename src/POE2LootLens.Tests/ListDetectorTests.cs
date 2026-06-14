using System.Drawing;
using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class ListDetectorTests
{
    private static Bitmap SolidBitmap(int w, int h, Color c)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(c);
        return bmp;
    }

    private static Bitmap RewardPanel(Color background)
    {
        var bmp = SolidBitmap(240, 220, background);
        using var graphics = Graphics.FromImage(bmp);
        using var pen = new Pen(Color.FromArgb(54, 45, 35), 3);
        foreach (int y in new[] { 8, 52, 96, 140, 184, 216 })
            graphics.DrawLine(pen, 34, y, 232, y);
        return bmp;
    }

    [Fact]
    public void IsOpen_True_WhenBrightParchmentHasRewardSeparators()
    {
        var detector = new ListDetector();
        using var bmp = RewardPanel(Color.FromArgb(187, 179, 162));
        Assert.True(detector.IsOpen(bmp));
    }

    [Fact]
    public void IsOpen_True_WhenMediumParchmentHasRewardSeparators()
    {
        var detector = new ListDetector();
        using var bmp = RewardPanel(Color.FromArgb(116, 103, 84));
        Assert.True(detector.IsOpen(bmp));
    }

    [Fact]
    public void IsOpen_False_WhenBrightBackgroundHasNoRewardSeparators()
    {
        var detector = new ListDetector();
        using var bmp = SolidBitmap(240, 220, Color.FromArgb(187, 179, 162));
        Assert.False(detector.IsOpen(bmp));
    }

    [Fact]
    public void IsOpen_False_WhenStripIsDark()
    {
        var detector = new ListDetector();
        using var bmp = SolidBitmap(100, 100, Color.FromArgb(6, 6, 6));
        Assert.False(detector.IsOpen(bmp));
    }

    [Fact]
    public void IsOpen_False_WhenStripIsBlack()
    {
        var detector = new ListDetector();
        using var bmp = SolidBitmap(100, 100, Color.Black);
        Assert.False(detector.IsOpen(bmp));
    }

    [Fact]
    public void IsOpen_ReturnsSampledAverageAndSeparatorCount()
    {
        var detector = new ListDetector();
        using var bmp = RewardPanel(Color.FromArgb(120, 120, 120));

        ListPanelSample sample = detector.Analyze(bmp);

        Assert.True(sample.IsOpen);
        Assert.InRange(sample.AverageColor.R, 116, 120);
        Assert.Equal(sample.AverageColor.R, sample.AverageColor.G);
        Assert.Equal(sample.AverageColor.G, sample.AverageColor.B);
        Assert.True(sample.SeparatorCount >= 4);
    }
}

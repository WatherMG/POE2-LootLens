using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;

namespace Poe2LootLens;

internal readonly record struct ListPanelSample(
    bool IsOpen,
    Color AverageColor,
    int AverageBrightness,
    double ParchmentFraction,
    double BrightnessDeviation,
    int SeparatorCount);

internal sealed class ListDetector
{
    private const int Cols = 16;
    // Sample the RIGHT portion only — the left icon column has dark gaps and rune glyphs.
    private const double LeftFraction = 0.40;
    private const double RightFraction = 0.98;
    private static readonly double[] RowFractions = [0.14, 0.27, 0.40, 0.53, 0.66, 0.79, 0.91];

    public bool IsOpen(Bitmap regionBitmap) => Analyze(regionBitmap).IsOpen;

    public bool IsOpen(Bitmap regionBitmap, out Color sampledAvg)
    {
        ListPanelSample sample = Analyze(regionBitmap);
        sampledAvg = sample.AverageColor;
        return sample.IsOpen;
    }

    // Colour alone is not a reliable close detector: after the reward book is closed, the Atlas can
    // still be bright and parchment-coloured. The list itself has several long horizontal row
    // separators, so require that structural signal as well as a plausible bright/warm background.
    internal ListPanelSample Analyze(Bitmap regionBitmap)
    {
        int x0 = Math.Clamp((int)(regionBitmap.Width * LeftFraction), 0, regionBitmap.Width - 1);
        int x1 = Math.Clamp((int)(regionBitmap.Width * RightFraction), x0 + 1, regionBitmap.Width);
        int span = Math.Max(1, x1 - x0);

        long r = 0, g = 0, b = 0;
        double brightnessSum = 0d;
        double brightnessSquaredSum = 0d;
        int parchment = 0;
        int count = 0;

        foreach (double yf in RowFractions)
        {
            int cy = Math.Clamp((int)(regionBitmap.Height * yf), 0, regionBitmap.Height - 1);
            for (int i = 0; i < Cols; i++)
            {
                int cx = Math.Clamp(x0 + (int)((i + 0.5) * span / Cols), 0, regionBitmap.Width - 1);
                Color px = regionBitmap.GetPixel(cx, cy);
                int brightness = (px.R + px.G + px.B) / 3;
                r += px.R;
                g += px.G;
                b += px.B;
                brightnessSum += brightness;
                brightnessSquaredSum += brightness * brightness;
                if (IsParchmentLike(px))
                    parchment++;
                count++;
            }
        }

        int avgR = (int)(r / Math.Max(1, count));
        int avgG = (int)(g / Math.Max(1, count));
        int avgB = (int)(b / Math.Max(1, count));
        int averageBrightness = (avgR + avgG + avgB) / 3;
        double mean = brightnessSum / Math.Max(1, count);
        double variance = Math.Max(0d, brightnessSquaredSum / Math.Max(1, count) - mean * mean);
        double deviation = Math.Sqrt(variance);
        double parchmentFraction = (double)parchment / Math.Max(1, count);
        int separatorCount = CountHorizontalSeparators(regionBitmap);

        bool plausibleSurface = parchmentFraction >= 0.12d ||
                                averageBrightness >= 108 && deviation <= 34d;
        bool open = plausibleSurface && separatorCount >= 2;

        return new ListPanelSample(
            open,
            Color.FromArgb(avgR, avgG, avgB),
            averageBrightness,
            parchmentFraction,
            deviation,
            separatorCount);
    }

    internal static int CountHorizontalSeparators(Bitmap bitmap)
    {
        if (bitmap.Width < 80 || bitmap.Height < 80)
            return 0;

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        byte[]? buffer = null;
        try
        {
            int stride = Math.Abs(data.Stride);
            int length = stride * bitmap.Height;
            buffer = ArrayPool<byte>.Shared.Rent(length);
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, length);

            int left = Math.Clamp((int)(bitmap.Width * 0.16), 0, bitmap.Width - 1);
            int right = Math.Clamp((int)(bitmap.Width * 0.96), left + 1, bitmap.Width);
            int stepX = Math.Max(2, (right - left) / 180);
            var rowLuminance = new int[bitmap.Height];

            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = data.Stride >= 0
                    ? y * stride
                    : (bitmap.Height - 1 - y) * stride;
                int sum = 0;
                int samples = 0;
                for (int x = left; x < right; x += stepX)
                {
                    int index = row + x * 3;
                    sum +=
                        (77 * buffer[index + 2] + 150 * buffer[index + 1] + 29 * buffer[index]) >> 8;
                    samples++;
                }
                rowLuminance[y] = sum / Math.Max(1, samples);
            }

            // Use the brighter row population as the local parchment level instead of a fixed
            // threshold. The game can dim the book considerably, while the separator is still
            // visibly darker than the surrounding row. A fixed 125 threshold classified an entire
            // dim parchment panel as one large dark block and therefore missed all separators.
            int[] sorted = rowLuminance.ToArray();
            Array.Sort(sorted);
            int surfaceIndex = Math.Clamp((int)Math.Round((sorted.Length - 1) * 0.72d), 0, sorted.Length - 1);
            int surface = sorted[surfaceIndex];
            int contrast = Math.Max(16, (int)Math.Round(surface * 0.17d));
            int threshold = Math.Max(8, surface - contrast);
            var mask = rowLuminance
                .Select(value => value <= threshold)
                .ToArray();

            int runs = 0;
            int start = -1;
            int lastActive = -1;
            for (int y = 0; y <= mask.Length; y++)
            {
                bool active = y < mask.Length && mask[y];
                if (active)
                {
                    if (start < 0)
                        start = y;
                    lastActive = y;
                    continue;
                }

                // Anti-aliased separator shadows can contain a one-pixel bright gap.
                if (start >= 0 && y - lastActive <= 2)
                    continue;

                if (start >= 0)
                {
                    int thickness = lastActive - start + 1;
                    if (thickness is >= 1 and <= 18)
                        runs++;
                    start = -1;
                    lastActive = -1;
                }
            }

            return runs;
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            bitmap.UnlockBits(data);
        }
    }

    private static bool IsParchmentLike(Color color) =>
        color.R >= 92 && color.G >= 76 && color.B >= 45 &&
        color.R >= color.B + 16 && color.G >= color.B + 6 &&
        color.R + color.G + color.B >= 245;
}

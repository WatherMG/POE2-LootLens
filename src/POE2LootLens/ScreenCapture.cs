using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;

namespace Poe2LootLens;

internal sealed class ScreenCaptureSession : IDisposable
{
    private readonly Rectangle _region;
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;

    public ScreenCaptureSession(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region), "Capture region must be non-empty.");

        _region = region;
        _bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        _graphics = Graphics.FromImage(_bitmap);
    }

    // The bitmap is owned by this session and reused on every cycle. Callers must not dispose it.
    public Bitmap Capture()
    {
        _graphics.CopyFromScreen(
            _region.X,
            _region.Y,
            0,
            0,
            _region.Size,
            CopyPixelOperation.SourceCopy);
        return _bitmap;
    }

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}

internal static class ScreenCapture
{
    // Kept for compatibility with any existing callers/tests. The scan loop uses ScreenCaptureSession
    // so it does not allocate and dispose a new Bitmap/Graphics pair every 50-100 ms.
    public static Bitmap CaptureRegion(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            region.X,
            region.Y,
            0,
            0,
            region.Size,
            CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    // Coarse sampled fingerprint. It is stable across tiny antialiasing noise but changes when the
    // panel contents, stack counts, scrolling position, or close animation changes. The pooled buffer
    // avoids a new large managed byte[] on every check.
    public static ulong ComputeFingerprint(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
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

            int stepX = Math.Max(1, bitmap.Width / 96);
            int stepY = Math.Max(1, bitmap.Height / 64);
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;

            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x += stepX)
                {
                    int index = row + x * 3;
                    // Quantize to 4 bits/channel so negligible pixel noise does not retrigger OCR.
                    hash ^= (byte)(buffer[index] >> 4);
                    hash *= prime;
                    hash ^= (byte)(buffer[index + 1] >> 4);
                    hash *= prime;
                    hash ^= (byte)(buffer[index + 2] >> 4);
                    hash *= prime;
                }
            }

            hash ^= (uint)bitmap.Width;
            hash *= prime;
            hash ^= (uint)bitmap.Height;
            return hash;
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            bitmap.UnlockBits(data);
        }
    }

    public static bool IsAllBlack(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
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

            int stepX = Math.Max(1, bitmap.Width / 16);
            int stepY = Math.Max(1, bitmap.Height / 8);
            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x += stepX)
                {
                    int index = row + x * 3;
                    if (buffer[index] != 0 || buffer[index + 1] != 0 || buffer[index + 2] != 0)
                        return false;
                }
            }

            return true;
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            bitmap.UnlockBits(data);
        }
    }
}

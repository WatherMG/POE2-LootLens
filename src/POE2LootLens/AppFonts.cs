using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using DrawingFont = System.Drawing.Font;
using DrawingFontFamily = System.Drawing.FontFamily;
using DrawingFontStyle = System.Drawing.FontStyle;
using DrawingGraphicsUnit = System.Drawing.GraphicsUnit;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace Poe2LootLens;

/// <summary>
/// Centralized font access for both WPF windows and System.Drawing overlays.
/// Roboto is loaded from application resources when the files are present under
/// Assets/Fonts. If a custom font resource is missing or invalid, Segoe UI is used.
/// </summary>
internal static class AppFonts
{
    private static readonly string[] ResourcePaths =
    [
        "Assets/Fonts/Roboto-Regular.ttf",
        "Assets/Fonts/Roboto-Medium.ttf",
        "Assets/Fonts/Roboto-SemiBold.ttf",
        "Assets/Fonts/Roboto-Bold.ttf",
    ];

    private static readonly object DrawingSync = new();
    private static readonly List<IntPtr> AllocatedFontBuffers = [];
    private static PrivateFontCollection? _drawingCollection;
    private static DrawingFontFamily? _drawingRoboto;
    private static bool _drawingInitialized;

    internal static void ConfigureWpf(ResourceDictionary resources)
    {
        var fallback = new WpfFontFamily("Segoe UI");
        resources["AppFontFamily"] = fallback;
        resources["AppMediumFontFamily"] = fallback;
        resources["AppSemiBoldFontFamily"] = fallback;
        resources["AppHeadingFontFamily"] = fallback;

        try
        {
            var baseUri = new Uri("pack://application:,,,/", UriKind.Absolute);
            WpfFontFamily regular = ResourceExists(ResourcePaths[0])
                ? new WpfFontFamily(baseUri, "./Assets/Fonts/#Roboto")
                : fallback;
            WpfFontFamily medium = ResourceExists(ResourcePaths[1])
                ? new WpfFontFamily(baseUri, "./Assets/Fonts/#Roboto Medium")
                : regular;
            WpfFontFamily semiBold = ResourceExists(ResourcePaths[2])
                ? new WpfFontFamily(baseUri, "./Assets/Fonts/#Roboto SemiBold")
                : medium;
            WpfFontFamily heading = ResourceExists(ResourcePaths[3])
                ? new WpfFontFamily(baseUri, "./Assets/Fonts/#Roboto")
                : semiBold;

            resources["AppFontFamily"] = regular;
            resources["AppMediumFontFamily"] = medium;
            resources["AppSemiBoldFontFamily"] = semiBold;
            resources["AppHeadingFontFamily"] = heading;
        }
        catch
        {
            // Font customization must never prevent the application from starting.
        }
    }

    internal static DrawingFont CreateDrawingFont(
        float size,
        DrawingFontStyle style = DrawingFontStyle.Regular,
        DrawingGraphicsUnit unit = DrawingGraphicsUnit.Pixel)
    {
        DrawingFontFamily? family = GetDrawingRoboto();
        if (family is not null)
        {
            try
            {
                return new DrawingFont(family, size, style, unit);
            }
            catch
            {
                // Fall through to the system font if a particular face is unavailable.
            }
        }

        return new DrawingFont("Segoe UI", size, style, unit);
    }

    internal static void Dispose()
    {
        lock (DrawingSync)
        {
            _drawingRoboto = null;
            _drawingCollection?.Dispose();
            _drawingCollection = null;
            foreach (IntPtr buffer in AllocatedFontBuffers)
                Marshal.FreeCoTaskMem(buffer);
            AllocatedFontBuffers.Clear();
            _drawingInitialized = false;
        }
    }

    private static DrawingFontFamily? GetDrawingRoboto()
    {
        lock (DrawingSync)
        {
            if (_drawingInitialized)
                return _drawingRoboto;

            _drawingInitialized = true;
            var collection = new PrivateFontCollection();
            foreach (string path in ResourcePaths)
            {
                byte[]? bytes = ReadResource(path);
                if (bytes is null || bytes.Length == 0)
                    continue;

                IntPtr buffer = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                try
                {
                    collection.AddMemoryFont(buffer, bytes.Length);
                    AllocatedFontBuffers.Add(buffer);
                }
                catch
                {
                    Marshal.FreeCoTaskMem(buffer);
                }
            }

            _drawingRoboto = collection.Families.FirstOrDefault(
                family => family.Name.Equals("Roboto", StringComparison.OrdinalIgnoreCase));
            if (_drawingRoboto is null)
            {
                collection.Dispose();
                return null;
            }

            _drawingCollection = collection;
            return _drawingRoboto;
        }
    }

    private static bool ResourceExists(string path)
    {
        try
        {
            return System.Windows.Application.GetResourceStream(ResourceUri(path)) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? ReadResource(string path)
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(ResourceUri(path));
            if (info is null)
                return null;
            using Stream stream = info.Stream;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static Uri ResourceUri(string path) =>
        new($"pack://application:,,,/{path.Replace('\\', '/')}", UriKind.Absolute);
}

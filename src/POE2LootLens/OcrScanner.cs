using System.Buffers;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using Tesseract;

namespace Poe2LootLens;

internal sealed record OcrRow(
    string NormalizedName,
    string RawText,
    int CenterY,
    int Multiplier = 1,
    float Confidence = 0f,
    string Variant = "",
    int LeadingMultiplier = 1,
    int BundleCount = 1,
    bool VariantAgreement = false);

internal readonly record struct OcrRowBand(int Top, int Bottom)
{
    public int CenterY => Top + (Bottom - Top) / 2;
    public int Height => Bottom - Top;
}

internal sealed class OcrScanner : IDisposable
{
    private readonly TesseractEngine[] _engines;
    private readonly Action<string>? _log;
    private readonly bool _debug;

    // Row geometry is part of the UI, not OCR. Keep the last trustworthy geometry while the
    // same panel is open so a single weak capture cannot suddenly reduce a 5-row panel to 2 rows.
    private List<OcrRowBand> _stableBands = [];
    private List<OcrRowBand> _pendingBands = [];
    private int _pendingBandRepeats;
    private readonly Dictionary<int, string> _lastRowDebugSignature = new();
    private readonly Dictionary<int, int> _suppressedRowDebug = new();
    private readonly Dictionary<int, DateTime> _lastRowDebugAt = new();
    private string _lastGeometryDebug = string.Empty;

    private const float MinConfidence = 10f;
    private const float BinaryFallbackConfidence = 55f;
    private const int UpscaleFactor = 2;
    private const int MinNameLength = 4;
    private const int MinWordLength = 4;

    private static readonly Regex MultiplierRegex = new(
        @"(?<![\p{L}\p{Nd}])(\d{1,3})\s*[xх×](?![\p{L}\p{Nd}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuantityMarkerRegex = new(
        @"(?<![\p{L}\p{Nd}])\d{1,3}\s*[xх×]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The runecrafting UI writes package size as a bracketed suffix: "Стекольная масса (3)".
    // OCR occasionally substitutes square/curly brackets, so all common bracket shapes are accepted.
    // A bare trailing digit is deliberately NOT accepted: that caused false multipliers for legitimate
    // item names containing a level or another numeric suffix.
    private static readonly Regex TrailingBundleRegex = new(
        @"[\(\[\{（]\s*(?<count>\d{1,3})\s*[\)\]\}）]\s*[\p{P}\p{S}\p{Nd}]{0,4}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Recovery for a missing opening bracket, for example "Сфера астромантии 6)". The
    // damaged suffix is removed only when the remaining base name is an exact catalog key.
    private static readonly Regex DamagedClosingBundleRegex = new(
        @"\d{1,3}\s*[\)\]\}）]\s*[\p{P}\p{S}]*\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PotentialBundleSuffixRegex = new(
        @"(?:[\(\[\{（]\s*\d{1,3}|\d{1,3}\s*[\)\]\}）])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Full-line OCR often turns a tiny final "(2)" into a bare "6" or "6;". This pattern is
    // only a signal to run the isolated quantity reader; the bare digit is never trusted itself.
    private static readonly Regex SuspiciousTrailingQuantityGlyphRegex = new(
        @"(?<![\p{L}\p{Nd}])\d{1,2}\s*[\p{P}\p{S}]{0,3}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Recovery for OCR such as "Руна разума (9" where the closing bracket is lost and 1 is
    // misread as 9. We only strip this suffix when the remaining base name is an exact catalog
    // name, and we deliberately do NOT trust the malformed number as a multiplier.
    private static readonly Regex MalformedTrailingBundleRegex = new(
        @"[\(\[\{（]\s*\d{1,3}\s*[\p{P}\p{S}]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Backwards-compatible overload used by the existing --ocr-test entry point.
    public OcrScanner(
        string tessdataDirectory,
        Action<string>? log = null,
        bool debug = false)
        : this(tessdataDirectory, "ru", log, debug)
    {
    }

    public OcrScanner(
        string tessdataDirectory,
        string gameLanguage,
        Action<string>? log = null,
        bool debug = false)
    {
        string language = ResolvePriceOcrLanguage(gameLanguage);
        var languageDataPath = Path.Combine(tessdataDirectory, $"{language}.traineddata");
        if (!File.Exists(languageDataPath))
        {
            throw new FileNotFoundException(
                $"OCR language data was not found: {language}.traineddata",
                languageDataPath);
        }

        _engines = Enumerable.Range(0, 3)
            .Select(_ => new TesseractEngine(tessdataDirectory, language, EngineMode.LstmOnly))
            .ToArray();
        foreach (TesseractEngine engine in _engines)
        {
            engine.SetVariable("preserve_interword_spaces", "1");
            engine.SetVariable("user_defined_dpi", "300");
        }
        _log = log;
        _debug = debug;
    }

    internal const double IconColumnFraction = 0.30;
    internal const double RightTrimFraction = 0.02;

    internal static string ResolvePriceOcrLanguage(string? gameLanguage) =>
        (gameLanguage ?? string.Empty).Trim().StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            ? "rus"
            : "eng";

    // The panel geometry is stable: every reward row is bounded by long dark horizontal separators.
    // Detecting those separators and OCRing each row with SingleLine is materially more reliable than
    // asking Tesseract to segment the complete parchment panel itself.
    public IReadOnlyList<OcrRow> Scan(
        Bitmap regionBitmap,
        IReadOnlyCollection<int>? skipCenters = null,
        IReadOnlySet<string>? knownNames = null)
    {
        var detectedBands = DetectRowBands(regionBitmap);
        var bands = StabilizeBands(detectedBands);
        if (bands.Count > 0)
        {
            // GDI+ does not guarantee concurrent reads from the same Bitmap. Copy each non-overlapping
            // row once on the caller thread, then run OCR in parallel only on independent bitmaps.
            // This prevents intermittent "Object is currently in use elsewhere" AggregateExceptions.
            var rowBitmaps = new Bitmap?[bands.Count];
            for (int index = 0; index < bands.Count; index++)
            {
                OcrRowBand band = bands[index];
                int rowCenter = GetDisplayCenterY(band);
                if (ShouldSkip(rowCenter, skipCenters))
                    continue;
                rowBitmaps[index] = CropBitmap(regionBitmap, 0, band.Top, regionBitmap.Width, band.Height);
            }

            var scannedRows = new OcrRow?[bands.Count];
            var enginePool = new ConcurrentBag<TesseractEngine>(_engines);
            try
            {
                Parallel.For(
                    0,
                    bands.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = _engines.Length },
                    index =>
                    {
                        Bitmap? rowBitmap = rowBitmaps[index];
                        if (rowBitmap is null)
                            return;

                        TesseractEngine engine = null!;
                        while (!enginePool.TryTake(out engine))
                            Thread.Yield();
                        try
                        {
                            OcrRowBand sourceBand = bands[index];
                            var localBand = new OcrRowBand(0, rowBitmap.Height);
                            scannedRows[index] = ScanRow(
                                engine,
                                rowBitmap,
                                localBand,
                                knownNames,
                                GetDisplayCenterY(sourceBand));
                        }
                        catch (Exception exception)
                        {
                            _log?.Invoke(
                                $"row OCR error y={GetDisplayCenterY(bands[index])}: " +
                                $"{exception.GetType().Name}: {exception.Message}");
                        }
                        finally
                        {
                            enginePool.Add(engine);
                        }
                    });
            }
            finally
            {
                foreach (Bitmap? rowBitmap in rowBitmaps)
                    rowBitmap?.Dispose();
            }

            var rows = new List<OcrRow>(bands.Count);
            for (int index = 0; index < bands.Count; index++)
            {
                OcrRow? row = scannedRows[index];
                if (row is null)
                    continue;
                OcrRowBand band = bands[index];
                if (ShouldIgnoreEdgeRow(band, regionBitmap.Height, row))
                {
                    if (_debug)
                    {
                        _log?.Invoke(
                            $"row ignored at capture edge y={row.CenterY} h={band.Height} " +
                            $"conf={row.Confidence:0} raw='{row.RawText}'");
                    }
                    continue;
                }

                if (_debug)
                    LogRowDebug(row.CenterY, band.Height, row);
                rows.Add(row);
            }

            if (_debug)
            {
                string detected = string.Join(", ", detectedBands.Select(b => $"{b.Top}-{b.Bottom}"));
                string used = string.Join(", ", bands.Select(b => $"{b.Top}-{b.Bottom}"));
                string geometry = detected == used
                    ? $"row geometry: {used}"
                    : $"row geometry: detected=[{detected}] stable=[{used}]";
                if (!string.Equals(geometry, _lastGeometryDebug, StringComparison.Ordinal))
                {
                    _lastGeometryDebug = geometry;
                    _log?.Invoke(geometry);
                }
            }

            return rows;
        }

        // Geometry detection is intentionally conservative. Keep a whole-panel fallback for unusual
        // UI scales, partial calibration, or future game layout changes.
        return ScanWholePanel(regionBitmap, knownNames);
    }

    public void ResetPanelState()
    {
        _stableBands.Clear();
        _pendingBands.Clear();
        _pendingBandRepeats = 0;
        _lastRowDebugSignature.Clear();
        _suppressedRowDebug.Clear();
        _lastRowDebugAt.Clear();
        _lastGeometryDebug = string.Empty;
    }

    private IReadOnlyList<OcrRowBand> StabilizeBands(IReadOnlyList<OcrRowBand> detected)
    {
        if (detected.Count == 0)
            return _stableBands.Count > 0 ? _stableBands : detected;

        if (_stableBands.Count == 0)
        {
            _stableBands = detected.ToList();
            return _stableBands;
        }

        if (BandsEquivalent(_stableBands, detected))
        {
            _stableBands = detected.ToList();
            _pendingBands.Clear();
            _pendingBandRepeats = 0;
            return _stableBands;
        }

        // Scrolling or a re-cropped selection can shift every separator together. Waiting for a
        // second frame in that case leaves price labels attached to the previous rows. Accept a
        // coherent same-count translation immediately, but reject deformations and mixed shifts.
        if (HasCoherentShift(_stableBands, detected))
        {
            _stableBands = detected.ToList();
            _pendingBands.Clear();
            _pendingBandRepeats = 0;
            return _stableBands;
        }

        // A weak frame may temporarily expose only a prefix/subset of the real rows. Keep the stable
        // model for the first two observations, but do not keep it forever: opening a genuinely shorter
        // list often produces exactly the same aligned prefix. Three identical subset observations are
        // therefore treated as a real in-place panel change.
        bool alignedSubset =
            detected.Count < _stableBands.Count &&
            IsAlignedSubset(detected, _stableBands);

        // More rows that contain the current geometry are almost always a more complete observation.
        // Accept them immediately; this also supports a mix of normal and expanded-height rows.
        if (detected.Count > _stableBands.Count && IsAlignedSubset(_stableBands, detected))
        {
            _stableBands = detected.ToList();
            _pendingBands.Clear();
            _pendingBandRepeats = 0;
            return _stableBands;
        }

        if (BandsEquivalent(_pendingBands, detected))
            _pendingBandRepeats++;
        else
        {
            _pendingBands = detected.ToList();
            _pendingBandRepeats = 1;
        }

        int requiredRepeats = alignedSubset ? 3 : 2;

        // A real in-place list change is accepted only after temporal agreement; a single bad detector
        // frame cannot destroy the row model.
        if (_pendingBandRepeats >= requiredRepeats)
        {
            _stableBands = detected.ToList();
            _pendingBands.Clear();
            _pendingBandRepeats = 0;
        }

        return _stableBands;
    }

    private static bool BandsEquivalent(
        IReadOnlyList<OcrRowBand> left,
        IReadOnlyList<OcrRowBand> right)
    {
        if (left.Count != right.Count)
            return false;
        if (left.Count == 0)
            return true;

        int tolerance = Math.Max(
            6,
            (int)Math.Round(left.Average(band => band.Height) * 0.16));

        for (int index = 0; index < left.Count; index++)
        {
            if (Math.Abs(left[index].Top - right[index].Top) > tolerance ||
                Math.Abs(left[index].Bottom - right[index].Bottom) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool HasCoherentShift(
        IReadOnlyList<OcrRowBand> previous,
        IReadOnlyList<OcrRowBand> current)
    {
        if (previous.Count == 0 || previous.Count != current.Count)
            return false;

        int[] shifts = previous
            .Select((band, index) => current[index].CenterY - band.CenterY)
            .OrderBy(value => value)
            .ToArray();
        int median = shifts[shifts.Length / 2];
        double averageHeight = previous.Average(band => band.Height);
        int maxShift = Math.Max(18, (int)Math.Round(averageHeight * 0.70));
        if (Math.Abs(median) < 7 || Math.Abs(median) > maxShift)
            return false;

        int spreadTolerance = Math.Max(5, (int)Math.Round(averageHeight * 0.10));
        for (int index = 0; index < previous.Count; index++)
        {
            int shift = current[index].CenterY - previous[index].CenterY;
            if (Math.Abs(shift - median) > spreadTolerance)
                return false;
            if (Math.Abs(current[index].Height - previous[index].Height) > spreadTolerance * 2)
                return false;
        }

        return true;
    }

    internal static bool ShouldIgnoreEdgeRow(OcrRowBand band, int captureHeight, OcrRow row)
    {
        bool touchesEdge = band.Top <= 5 || band.Bottom >= captureHeight - 8;
        if (!touchesEdge)
            return false;

        int letters = row.NormalizedName.Count(char.IsLetter);
        return string.IsNullOrWhiteSpace(row.NormalizedName) ||
               row.Confidence < 35f ||
               letters < MinNameLength;
    }

    private static bool IsAlignedSubset(
        IReadOnlyList<OcrRowBand> subset,
        IReadOnlyList<OcrRowBand> complete)
    {
        if (subset.Count == 0)
            return true;

        int tolerance = Math.Max(
            8,
            (int)Math.Round(complete.Average(band => band.Height) * 0.20));

        foreach (var band in subset)
        {
            if (!complete.Any(stable => Math.Abs(stable.CenterY - band.CenterY) <= tolerance))
                return false;
        }

        return true;
    }

    private static int GetDisplayCenterY(OcrRowBand band) =>
        band.Height >= 84
            ? band.Top + (int)Math.Round(band.Height * 0.74)
            : band.CenterY;

    private static bool ShouldSkip(int centerY, IReadOnlyCollection<int>? skipCenters)
    {
        if (skipCenters is null || skipCenters.Count == 0)
            return false;

        foreach (int lockedCenter in skipCenters)
        {
            if (Math.Abs(lockedCenter - centerY) <= 14)
                return true;
        }

        return false;
    }

    internal static IReadOnlyList<OcrRowBand> DetectRowBands(Bitmap bitmap)
    {
        if (bitmap.Width < 100 || bitmap.Height < 40)
            return [];

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

            int left = Math.Clamp((int)(bitmap.Width * IconColumnFraction), 0, bitmap.Width - 1);
            int right = Math.Clamp(
                bitmap.Width - (int)(bitmap.Width * RightTrimFraction),
                left + 1,
                bitmap.Width);
            int stepX = Math.Max(1, (right - left) / 180);
            int samplesPerLine = Math.Max(1, (right - left + stepX - 1) / stepX);

            var separatorMask = new bool[bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                int dark = 0;
                int rowOffset = y * stride;
                for (int x = left; x < right; x += stepX)
                {
                    int index = rowOffset + x * 3;
                    int blue = buffer[index];
                    int green = buffer[index + 1];
                    int red = buffer[index + 2];
                    int luminance = (77 * red + 150 * green + 29 * blue) >> 8;
                    if (luminance < 135)
                        dark++;
                }

                separatorMask[y] = dark >= samplesPerLine * 0.72;
            }

            var candidates = MergeNearbySeparators(CollapseSeparatorRuns(separatorMask));
            if (candidates.Count < 2)
                return [];

            // Rows may have two different heights: ordinary rewards and expanded recipes with a
            // second icon lane. Build the longest plausible separator path instead of assuming one
            // global spacing. Short 20-40 px gaps are usually icon/text artefacts; legitimate rows
            // observed in the game are roughly 45-135 px high at the supported UI scales.
            var separators = SelectLongestSeparatorPath(candidates);
            if (separators.Count < 2)
                return [];

            // If the user calibrated the region tightly to the visible list, the final visible row
            // may be fully readable while its lower separator is just outside the capture. Add a
            // synthetic bottom boundary for a plausible final row instead of dropping it entirely.
            int syntheticBottom = bitmap.Height - 1;
            int trailingGap = syntheticBottom - separators[^1];
            int typicalHeight = separators.Count >= 2
    ? separators.Zip(separators.Skip(1), (a, b) => b - a)
        .OrderBy(value => value)
        .ElementAt((separators.Count - 1) / 2)
    : 0;
            bool plausibleTrailingHeight =
                trailingGap is >= 44 and <= 138 &&
                (typicalHeight <= 0 || trailingGap <= Math.Max(76, (int)Math.Round(typicalHeight * 1.55)));
            var trailingBand = new OcrRowBand(separators[^1], syntheticBottom);
            if (plausibleTrailingHeight &&
                LooksLikeRewardBand(buffer, stride, bitmap.Width, trailingBand) &&
                HasRewardTextInk(buffer, stride, bitmap.Width, trailingBand))
            {
                separators.Add(syntheticBottom);
            }

            var bands = new List<OcrRowBand>(separators.Count - 1);
            for (int index = 0; index + 1 < separators.Count; index++)
            {
                int top = separators[index];
                int bottom = separators[index + 1];
                int height = bottom - top;
                if (height is < 44 or > 138)
                    continue;

                var band = new OcrRowBand(top, bottom);
                if (LooksLikeRewardBand(buffer, stride, bitmap.Width, band))
                    bands.Add(band);
            }

            return bands;
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            bitmap.UnlockBits(data);
        }
    }

    private static List<int> CollapseSeparatorRuns(IReadOnlyList<bool> mask)
    {
        var result = new List<int>();
        int start = -1;

        for (int y = 0; y <= mask.Count; y++)
        {
            bool active = y < mask.Count && mask[y];
            if (active && start < 0)
            {
                start = y;
            }
            else if (!active && start >= 0)
            {
                int end = y;
                int thickness = end - start;

                // Real row separators are thin strokes. A decorative dark header or another large
                // filled area may satisfy the per-line darkness threshold for dozens of pixels; using
                // its centre as a separator merges the header with the first reward row.
                if (thickness <= 16)
                {
                    result.Add(start + (thickness - 1) / 2);
                }
                else
                {
                    // Preserve the lower boundary: the first reward row often begins immediately
                    // after a dark decorative title block, so its bottom edge is a valid separator.
                    result.Add(end - 1);
                }

                start = -1;
            }
        }

        return result;
    }

    private static List<int> MergeNearbySeparators(IReadOnlyList<int> candidates)
    {
        if (candidates.Count == 0)
            return [];

        const int mergeDistance = 12;
        var merged = new List<int>();
        int sum = candidates[0];
        int count = 1;
        int last = candidates[0];

        for (int index = 1; index < candidates.Count; index++)
        {
            int value = candidates[index];
            if (value - last <= mergeDistance)
            {
                sum += value;
                count++;
            }
            else
            {
                merged.Add((int)Math.Round((double)sum / count));
                sum = value;
                count = 1;
            }

            last = value;
        }

        merged.Add((int)Math.Round((double)sum / count));
        return merged;
    }

    private static List<int> SelectLongestSeparatorPath(
        IReadOnlyList<int> candidates)
    {
        const int minRowHeight = 44;
        const int maxRowHeight = 138;

        var bestCount = new int[candidates.Count];
        var bestCoverage = new int[candidates.Count];
        var previous = new int[candidates.Count];
        Array.Fill(previous, -1);

        for (int index = 0; index < candidates.Count; index++)
        {
            bestCount[index] = 1;
            for (int prior = 0; prior < index; prior++)
            {
                int gap = candidates[index] - candidates[prior];
                if (gap < minRowHeight || gap > maxRowHeight)
                    continue;

                int candidateCount = bestCount[prior] + 1;
                int candidateCoverage = bestCoverage[prior] + gap;
                if (candidateCount > bestCount[index] ||
                    candidateCount == bestCount[index] &&
                    candidateCoverage > bestCoverage[index])
                {
                    bestCount[index] = candidateCount;
                    bestCoverage[index] = candidateCoverage;
                    previous[index] = prior;
                }
            }
        }

        int bestEnd = 0;
        for (int index = 1; index < candidates.Count; index++)
        {
            if (bestCount[index] > bestCount[bestEnd] ||
                bestCount[index] == bestCount[bestEnd] &&
                bestCoverage[index] > bestCoverage[bestEnd])
            {
                bestEnd = index;
            }
        }

        if (bestCount[bestEnd] < 2)
            return [];

        var path = new List<int>(bestCount[bestEnd]);
        for (int index = bestEnd; index >= 0; index = previous[index])
        {
            path.Add(candidates[index]);
            if (previous[index] < 0)
                break;
        }
        path.Reverse();

        return path;
    }

    private static bool LooksLikeRewardBand(
        byte[] buffer,
        int stride,
        int width,
        OcrRowBand band)
    {
        int left = Math.Clamp((int)Math.Round(width * 0.46), 0, width - 1);
        int right = Math.Clamp((int)Math.Round(width * 0.97), left + 1, width);
        int top = band.Top + Math.Clamp(band.Height / 12, 3, 8);
        int bottom = band.Bottom - Math.Clamp(band.Height / 12, 3, 8);
        if (bottom <= top)
            return false;

        int stepX = Math.Max(2, (right - left) / 80);
        int stepY = Math.Max(2, (bottom - top) / 24);
        int bright = 0;
        int samples = 0;

        for (int y = top; y < bottom; y += stepY)
        {
            int rowOffset = y * stride;
            for (int x = left; x < right; x += stepX)
            {
                int index = rowOffset + x * 3;
                int luminance =
                    (77 * buffer[index + 2] +
                     150 * buffer[index + 1] +
                     29 * buffer[index]) >> 8;
                if (luminance >= 160)
                    bright++;
                samples++;
            }
        }

        // Reward rows use a bright parchment field on their right side. The decorative title/header
        // above the list is much darker and otherwise looks like an expanded row geometrically.
        return samples > 0 && bright >= samples * 0.30;
    }


    private static bool HasRewardTextInk(
        byte[] buffer,
        int stride,
        int width,
        OcrRowBand band)
    {
        int left = Math.Clamp((int)Math.Round(width * 0.38), 0, width - 1);
        int right = Math.Clamp((int)Math.Round(width * 0.97), left + 1, width);
        int top = Math.Clamp(band.Top + 5, 0, band.Bottom);
        int bottom = Math.Clamp(band.Bottom - 5, top, band.Bottom);
        if (bottom <= top)
            return false;

        int stepX = Math.Max(1, (right - left) / 180);
        int stepY = Math.Max(1, (bottom - top) / 48);
        int dark = 0;
        int samples = 0;
        for (int y = top; y < bottom; y += stepY)
        {
            int rowOffset = y * stride;
            for (int x = left; x < right; x += stepX)
            {
                int index = rowOffset + x * 3;
                int luminance =
                    (77 * buffer[index + 2] +
                     150 * buffer[index + 1] +
                     29 * buffer[index]) >> 8;
                if (luminance < 115)
                    dark++;
                samples++;
            }
        }

        // Blank parchment has virtually no very-dark pixels; reward glyphs form a small but stable
        // cluster. Requiring both an absolute count and a fraction avoids synthesizing a blank tail.
        return samples > 0 && dark >= 12 && dark >= samples * 0.006;
    }

    private OcrRow ScanRow(
        TesseractEngine engine,
        Bitmap regionBitmap,
        OcrRowBand band,
        IReadOnlySet<string>? knownNames,
        int? displayCenterY = null)
    {
        bool tallRow = band.Height >= 84;
        int centerY = displayCenterY ?? GetDisplayCenterY(band);
        var candidates = new List<OcrRow>(6);

        // Standard rows place icons and text on the same baseline. Expanded rows place a larger
        // rune grid above the reward name, so OCR only the lower text lane.
        int verticalPadding = Math.Clamp(band.Height / 16, 2, 5);
        int top = tallRow
            ? Math.Clamp(
                band.Top + (int)Math.Round(band.Height * 0.56),
                0,
                regionBitmap.Height - 1)
            : Math.Clamp(band.Top + verticalPadding, 0, regionBitmap.Height - 1);
        int bottom = Math.Clamp(
            band.Bottom - verticalPadding,
            top + 1,
            regionBitmap.Height);

        AddRowCandidates(
            engine,
            candidates,
            regionBitmap,
            band,
            top,
            bottom,
            tallRow ? 0.42 : IconColumnFraction,
            tallRow ? 3 : UpscaleFactor,
            tallRow ? "tall" : "wide",
            knownNames);

        var selected = SelectBestCandidate(candidates, knownNames);

        // If the wide crop was polluted by the last rune icon or missed a long right-aligned name,
        // retry from a safer text-only column. This costs one extra OCR pass only for unresolved rows.
        if (!IsKnown(selected.NormalizedName, knownNames) ||
            string.IsNullOrEmpty(selected.NormalizedName))
        {
            AddRowCandidates(
                engine,
                candidates,
                regionBitmap,
                band,
                top,
                bottom,
                tallRow ? 0.50 : 0.38,
                tallRow ? 3 : UpscaleFactor,
                tallRow ? "tall-narrow" : "right",
                knownNames);

            selected = SelectBestCandidate(candidates, knownNames);

            // A very long reward name may begin left of the normal tall-row text lane. Only after the
            // clean text-only attempts fail, widen the crop enough to recover it; catalog-aware candidate
            // selection prevents leftover rune glyphs from beating an exact item name.
            if (tallRow && !IsKnown(selected.NormalizedName, knownNames))
            {
                AddRowCandidates(
                    engine,
                    candidates,
                    regionBitmap,
                    band,
                    top,
                    bottom,
                    0.34,
                    3,
                    "tall-wide",
                    knownNames);
                selected = SelectBestCandidate(candidates, knownNames);
            }
        }

        int agreeingCandidates = candidates.Count(candidate =>
            !string.IsNullOrEmpty(selected.NormalizedName) &&
            candidate.NormalizedName == selected.NormalizedName &&
            candidate.LeadingMultiplier == selected.LeadingMultiplier &&
            candidate.BundleCount == selected.BundleCount);

        selected = selected with
        {
            CenterY = centerY,
            VariantAgreement = agreeingCandidates >= 2,
        };

        return selected;
    }

    private void LogRowDebug(int centerY, int height, OcrRow row)
    {
        int debugKey = QuantizeDebugY(centerY);
        string signature =
            $"{height}|{row.Variant}|{row.VariantAgreement}|{row.Confidence:0}|" +
            $"{row.RawText}|{row.NormalizedName}|{row.LeadingMultiplier}|{row.BundleCount}";
        var now = DateTime.UtcNow;
        bool unchanged = _lastRowDebugSignature.TryGetValue(debugKey, out string? previous) &&
                         string.Equals(previous, signature, StringComparison.Ordinal);
        if (unchanged &&
            _lastRowDebugAt.TryGetValue(debugKey, out DateTime last) &&
            now - last < TimeSpan.FromSeconds(8))
        {
            _suppressedRowDebug[debugKey] = _suppressedRowDebug.GetValueOrDefault(debugKey) + 1;
            return;
        }

        int suppressed = _suppressedRowDebug.GetValueOrDefault(debugKey);
        string repeat = suppressed > 0 ? $" unchanged×{suppressed + 1}" : string.Empty;
        _log?.Invoke(
            $"row y={centerY} h={height} variant={row.Variant} " +
            $"agree={row.VariantAgreement} conf={row.Confidence:0} " +
            $"raw='{row.RawText}' norm='{row.NormalizedName}' " +
            $"lead={row.LeadingMultiplier} bundle={row.BundleCount} x{row.Multiplier}{repeat}");
        _lastRowDebugSignature[debugKey] = signature;
        _lastRowDebugAt[debugKey] = now;
        _suppressedRowDebug[debugKey] = 0;
    }

    private void AddRowCandidates(
        TesseractEngine engine,
        List<OcrRow> candidates,
        Bitmap regionBitmap,
        OcrRowBand band,
        int top,
        int bottom,
        double leftFraction,
        int upscaleFactor,
        string variantPrefix,
        IReadOnlySet<string>? knownNames)
    {
        int left = Math.Max(1, (int)Math.Round(regionBitmap.Width * leftFraction));
        int rightTrim = (int)(regionBitmap.Width * RightTrimFraction);
        int width = Math.Max(1, regionBitmap.Width - left - rightTrim);

        using var row = CropBitmap(regionBitmap, left, top, width, bottom - top);
        using var textRow = CropToInkBounds(row);

        var gray = RunSingleLine(
            engine,
            textRow,
            binary: false,
            GetDisplayCenterY(band),
            $"{variantPrefix}-gray",
            knownNames,
            upscaleFactor,
            quantitySource: row);
        candidates.Add(gray);

        bool grayKnown = IsKnown(gray.NormalizedName, knownNames);
        bool qualitySensitive =
            gray.BundleCount > 1 ||
            IsRuneLike(gray.NormalizedName);

        bool runBinary =
            gray.Confidence < BinaryFallbackConfidence ||
            string.IsNullOrEmpty(gray.NormalizedName) ||
            !grayKnown ||
            qualitySensitive;

        if (runBinary)
        {
            candidates.Add(RunSingleLine(
                engine,
                textRow,
                binary: true,
                GetDisplayCenterY(band),
                $"{variantPrefix}-binary",
                knownNames,
                upscaleFactor,
                quantitySource: row));
        }
    }

    private static OcrRow SelectBestCandidate(
        IReadOnlyList<OcrRow> candidates,
        IReadOnlySet<string>? knownNames)
    {
        if (candidates.Count == 0)
            return new OcrRow(string.Empty, string.Empty, 0);

        OcrRow best = candidates[0];
        double bestScore = CandidateScore(best, IsKnown(best.NormalizedName, knownNames));

        for (int index = 1; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            double score = CandidateScore(
                candidate,
                IsKnown(candidate.NormalizedName, knownNames));

            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static OcrRow SelectCandidate(
        OcrRow primary,
        OcrRow secondary,
        IReadOnlySet<string>? knownNames)
    {
        bool primaryKnown = IsKnown(primary.NormalizedName, knownNames);
        bool secondaryKnown = IsKnown(secondary.NormalizedName, knownNames);

        if (primaryKnown != secondaryKnown)
            return primaryKnown ? primary : secondary;

        return CandidateScore(secondary, secondaryKnown) > CandidateScore(primary, primaryKnown)
            ? secondary
            : primary;
    }

    private static bool IsKnown(string name, IReadOnlySet<string>? knownNames) =>
        !string.IsNullOrEmpty(name) && knownNames?.Contains(name) == true;

    private static bool IsRuneLike(string name) =>
        name.Contains("руна", StringComparison.Ordinal) ||
        name.Contains("rune", StringComparison.Ordinal);

    private static int QuantizeDebugY(int centerY) =>
        (int)Math.Round(centerY / 5d, MidpointRounding.AwayFromZero) * 5;

    private static double CandidateScore(OcrRow row, bool known)
    {
        int letters = row.NormalizedName.Count(char.IsLetter);
        double score = row.Confidence + Math.Min(letters, 40) * 0.6;
        if (known)
            score += 100;
        // Quantity is less reliable than identity and must never make a malformed suffix win.
        // A multiplier is applied only after identity/temporal checks in ScanEngine.
        if (string.IsNullOrEmpty(row.NormalizedName))
            score -= 100;
        return score;
    }

    private OcrRow RunSingleLine(
        TesseractEngine engine,
        Bitmap source,
        bool binary,
        int centerY,
        string variant,
        IReadOnlySet<string>? knownNames,
        int upscaleFactor,
        Bitmap? quantitySource = null)
    {
        using var prepared = PrepareForOcr(source, binary);
        using var upscaled = Upscale(prepared, upscaleFactor);
        byte[] png = ToPng(upscaled);

        (string? Text, float Confidence) candidate;
        using (var pix = Pix.LoadFromMemory(png))
        using (var page = engine.Process(pix, PageSegMode.SingleLine))
            candidate = ExtractBestLine(page);

        if (candidate.Text is null)
            return new OcrRow(
                string.Empty,
                string.Empty,
                centerY,
                1,
                0f,
                variant,
                1,
                1,
                false);

        var parsed = ParseRow(
            candidate.Text,
            centerY,
            candidate.Confidence,
            variant,
            knownNames);

        // Full-line OCR is optimized for the item name and may turn a tiny "(2)" into "6)".
        // Verify only suspicious/non-unit suffixes using a tiny right-edge crop and a digits-only
        // whitelist. Exact unit suffixes stay on the fast path.
        string trimmedCandidate = candidate.Text.Trim();
        _ = ExtractTrailingBundleCount(trimmedCandidate, out string directSuffixBase);
        bool hasTrustedFinalSuffix =
            !string.Equals(directSuffixBase, trimmedCandidate, StringComparison.Ordinal);
        bool damagedSuffix =
            !hasTrustedFinalSuffix &&
            (DamagedClosingBundleRegex.IsMatch(candidate.Text) ||
             MalformedTrailingBundleRegex.IsMatch(candidate.Text));
        bool verifyQuantity =
            !hasTrustedFinalSuffix &&
            (PotentialBundleSuffixRegex.IsMatch(candidate.Text) ||
             SuspiciousTrailingQuantityGlyphRegex.IsMatch(candidate.Text)) &&
            (damagedSuffix || !IsKnown(parsed.NormalizedName, knownNames));
        if (verifyQuantity)
        {
            int? verifiedBundleCount = TryReadBundleCount(engine, quantitySource ?? source);
            if (verifiedBundleCount is { } recoveredCount &&
                !IsSafeRecoveredBundleCount(recoveredCount))
            {
                // High bundle sizes are accepted only from a complete bracketed suffix in the
                // full-line OCR fast path. When the full line lost or mangled its brackets, a tiny
                // quantity crop may repeat the same glyph error (for example real "(1)" -> "6);")
                // and would otherwise create a catastrophic x6/x7 result. Failing safe to x1 is
                // preferable; intact values such as "(6)" are still parsed before this recovery path.
                verifiedBundleCount = null;
            }
            if (verifiedBundleCount is not null)
            {
                parsed = ParseRow(
                    candidate.Text,
                    centerY,
                    candidate.Confidence,
                    variant,
                    knownNames,
                    verifiedBundleCount);
            }
        }

        return parsed;
    }

    internal static bool IsSafeRecoveredBundleCount(int count) => count is >= 1 and <= 3;

    private int? TryReadBundleCount(TesseractEngine engine, Bitmap source)
    {
        int cropWidth = Math.Min(
            source.Width,
            Math.Clamp((int)Math.Round(source.Width * 0.24), 64, 132));
        if (cropWidth <= 0 || source.Height <= 0)
            return null;

        using var suffix = CropBitmap(
            source,
            Math.Max(0, source.Width - cropWidth),
            0,
            cropWidth,
            source.Height);

        var votes = new Dictionary<int, int>();
        engine.SetVariable("tessedit_char_whitelist", "0123456789()[]{}（）");
        try
        {
            foreach (bool binary in new[] { true, false })
            foreach (int scale in new[] { 4, 5 })
            foreach (PageSegMode mode in new[] { PageSegMode.SingleWord, PageSegMode.SingleLine })
            {
                using var prepared = PrepareForOcr(suffix, binary);
                using var upscaled = Upscale(prepared, scale);
                using var pix = Pix.LoadFromMemory(ToPng(upscaled));
                using var page = engine.Process(pix, mode);
                string text = page.GetText()?.Trim() ?? string.Empty;
                int bracketed = ExtractTrailingBundleCount(text, out string withoutBundle);
                if (string.Equals(text, withoutBundle, StringComparison.Ordinal) ||
                    bracketed is < 1 or > 999)
                {
                    continue;
                }

                votes[bracketed] = votes.GetValueOrDefault(bracketed) + 1;
            }
        }
        finally
        {
            // The same engine handles names afterwards; never leak the digits-only whitelist.
            engine.SetVariable("tessedit_char_whitelist", string.Empty);
        }

        if (votes.Count == 0)
            return null;

        var winner = votes.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First();
        // A single OCR pass is not enough to multiply a reward. Two independent preprocessing/
        // segmentation variants must agree; otherwise fail safe to x1.
        return winner.Value >= 2 ? winner.Key : null;
    }

    private IReadOnlyList<OcrRow> ScanWholePanel(
        Bitmap regionBitmap,
        IReadOnlySet<string>? knownNames)
    {
        int left = Math.Max(1, (int)(regionBitmap.Width * IconColumnFraction));
        int rightTrim = (int)(regionBitmap.Width * RightTrimFraction);
        int width = Math.Max(1, regionBitmap.Width - left - rightTrim);

        using var cropped = CropBitmap(regionBitmap, left, 0, width, regionBitmap.Height);
        using var prepared = PrepareForOcr(cropped, binary: false);
        using var upscaled = Upscale(prepared, UpscaleFactor);
        byte[] png = ToPng(upscaled);

        var rows = RunWholePanelPass(
            png,
            PageSegMode.SingleColumn,
            regionBitmap.Height,
            "column",
            knownNames);
        if (rows.Count == 0)
        {
            using var binary = PrepareForOcr(cropped, binary: true);
            using var binaryUpscaled = Upscale(binary, UpscaleFactor);
            rows = RunWholePanelPass(
                ToPng(binaryUpscaled),
                PageSegMode.SparseText,
                regionBitmap.Height,
                "sparse-binary",
                knownNames);
        }

        return rows;
    }

    private IReadOnlyList<OcrRow> RunWholePanelPass(
        byte[] png,
        PageSegMode mode,
        int regionHeight,
        string variant,
        IReadOnlySet<string>? knownNames,
        int? verifiedBundleCount = null)
    {
        using var pix = Pix.LoadFromMemory(png);
        using var page = _engines[0].Process(pix, mode);
        var rows = new List<OcrRow>();
        using var iterator = page.GetIterator();
        iterator.Begin();

        do
        {
            if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var box))
                continue;

            string? text = iterator.GetText(PageIteratorLevel.TextLine);
            float confidence = iterator.GetConfidence(PageIteratorLevel.TextLine);
            if (string.IsNullOrWhiteSpace(text) || confidence < MinConfidence)
                continue;

            int centerY = Math.Clamp(
                (box.Y1 + (box.Y2 - box.Y1) / 2) / UpscaleFactor,
                0,
                regionHeight - 1);
            var row = ParseRow(text, centerY, confidence, variant, knownNames);
            if (!string.IsNullOrEmpty(row.NormalizedName))
                rows.Add(row);
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return rows;
    }

    private static (string? Text, float Confidence) ExtractBestLine(Page page)
    {
        string? bestText = null;
        float bestConfidence = 0f;
        int bestLetters = -1;

        using var iterator = page.GetIterator();
        iterator.Begin();
        do
        {
            string? text = iterator.GetText(PageIteratorLevel.TextLine);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            int letters = text.Count(char.IsLetter);
            float confidence = iterator.GetConfidence(PageIteratorLevel.TextLine);
            if (letters > bestLetters || letters == bestLetters && confidence > bestConfidence)
            {
                bestText = text.Trim();
                bestConfidence = confidence;
                bestLetters = letters;
            }
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return (bestText, bestConfidence);
    }

    private static OcrRow ParseRow(
        string rawText,
        int centerY,
        float confidence,
        string variant,
        IReadOnlySet<string>? knownNames,
        int? verifiedBundleCount = null)
    {
        string withoutBundle = rawText.Trim();
        int bundleCount = ExtractTrailingBundleCount(withoutBundle, out string strippedText);
        if (!string.Equals(strippedText, withoutBundle, StringComparison.Ordinal))
        {
            withoutBundle = strippedText;
            if (verifiedBundleCount is > 0)
                bundleCount = verifiedBundleCount.Value;
        }
        else if (verifiedBundleCount is > 0 &&
                 TryStripVerifiedBundleGlyphSuffix(
                     withoutBundle,
                     knownNames,
                     out string verifiedBase))
        {
            withoutBundle = verifiedBase;
            bundleCount = verifiedBundleCount.Value;
        }
        else if (TryStripDamagedClosingBundleSuffix(
                     withoutBundle,
                     knownNames,
                     out string recoveredClosingBase))
        {
            withoutBundle = recoveredClosingBase;
            bundleCount = verifiedBundleCount is > 0
                ? verifiedBundleCount.Value
                : 1;
        }
        else if (TryStripMalformedBundleSuffix(
                     withoutBundle,
                     knownNames,
                     out string recoveredOpenBase))
        {
            // The name is trustworthy. Trust the tiny quantity-only pass when available; otherwise
            // fail safe to one item rather than multiplying by a damaged full-line digit.
            withoutBundle = recoveredOpenBase;
            bundleCount = verifiedBundleCount is > 0
                ? verifiedBundleCount.Value
                : 1;
        }

        string normalizedRaw = NormalizeName(withoutBundle);
        int leadingMultiplier = ExtractMultiplier(normalizedRaw);
        string normalizedName = StripLeadingNoise(normalizedRaw);
        int multiplier = MultiplyAndClamp(leadingMultiplier, bundleCount);

        if (normalizedName.Length < MinNameLength || !HasLongWord(normalizedName, MinWordLength))
            normalizedName = string.Empty;

        return new OcrRow(
            normalizedName,
            rawText.Trim(),
            centerY,
            multiplier,
            confidence,
            variant,
            leadingMultiplier,
            bundleCount,
            false);
    }

    internal static bool TryStripVerifiedBundleGlyphSuffix(
        string rawText,
        IReadOnlySet<string>? knownNames,
        out string textWithoutBundle)
    {
        textWithoutBundle = rawText.Trim();
        var suspicious = SuspiciousTrailingQuantityGlyphRegex.Match(textWithoutBundle);
        if (!suspicious.Success)
            return false;

        string possibleBaseRaw = textWithoutBundle[..suspicious.Index].TrimEnd();
        string possibleBase = StripLeadingNoise(NormalizeName(possibleBaseRaw));
        if (string.IsNullOrEmpty(possibleBase) || knownNames?.Contains(possibleBase) != true)
            return false;

        textWithoutBundle = possibleBaseRaw;
        return true;
    }

    internal static bool TryStripDamagedClosingBundleSuffix(
        string rawText,
        IReadOnlySet<string>? knownNames,
        out string textWithoutBundle)
    {
        textWithoutBundle = rawText.Trim();
        var damaged = DamagedClosingBundleRegex.Match(textWithoutBundle);
        if (!damaged.Success)
            return false;

        string possibleBaseRaw = textWithoutBundle[..damaged.Index].TrimEnd();
        string possibleBase = StripLeadingNoise(NormalizeName(possibleBaseRaw));
        if (string.IsNullOrEmpty(possibleBase) ||
            knownNames?.Contains(possibleBase) != true)
        {
            return false;
        }

        textWithoutBundle = possibleBaseRaw;
        return true;
    }

    internal static bool TryStripMalformedBundleSuffix(
        string rawText,
        IReadOnlySet<string>? knownNames,
        out string textWithoutBundle)
    {
        textWithoutBundle = rawText.Trim();
        var malformed = MalformedTrailingBundleRegex.Match(textWithoutBundle);
        if (!malformed.Success)
            return false;

        string possibleBaseRaw = textWithoutBundle[..malformed.Index].TrimEnd();
        string possibleBase = StripLeadingNoise(NormalizeName(possibleBaseRaw));
        if (string.IsNullOrEmpty(possibleBase) ||
            knownNames?.Contains(possibleBase) != true)
        {
            return false;
        }

        textWithoutBundle = possibleBaseRaw;
        return true;
    }

    internal static int ExtractTrailingBundleCount(string rawText, out string textWithoutBundle)
    {
        textWithoutBundle = rawText.Trim();
        var match = TrailingBundleRegex.Match(textWithoutBundle);
        if (!match.Success ||
            !int.TryParse(match.Groups["count"].Value, out int count) ||
            count < 1)
        {
            return 1;
        }

        textWithoutBundle = textWithoutBundle[..match.Index].TrimEnd();
        return Math.Min(count, 999);
    }

    private static int MultiplyAndClamp(int left, int right)
    {
        long result = (long)Math.Max(1, left) * Math.Max(1, right);
        return (int)Math.Min(result, 999);
    }


    // Reward names are right-aligned and short names can leave hundreds of blank pixels to their left.
    // Tesseract's SingleLine mode is much more stable when it receives the ink itself rather than a
    // mostly-empty row, so trim to columns that contain genuinely dark glyph pixels.
    private static Bitmap CropToInkBounds(Bitmap source)
    {
        var data = source.LockBits(
            new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        byte[]? buffer = null;
        int cropX = 0;
        int cropWidth = source.Width;

        try
        {
            int stride = Math.Abs(data.Stride);
            int length = stride * source.Height;
            buffer = ArrayPool<byte>.Shared.Rent(length);
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, length);

            int yStart = Math.Clamp(source.Height / 12, 0, source.Height - 1);
            int yEnd = Math.Clamp(source.Height - source.Height / 12, yStart + 1, source.Height);
            int requiredDarkPixels = Math.Max(2, (yEnd - yStart) / 18);
            int minX = source.Width;
            int maxX = -1;

            for (int x = 0; x < source.Width; x++)
            {
                int darkPixels = 0;
                for (int y = yStart; y < yEnd; y++)
                {
                    int index = y * stride + x * 3;
                    int luminance =
                        (77 * buffer[index + 2] + 150 * buffer[index + 1] + 29 * buffer[index]) >> 8;
                    if (luminance < 115 && ++darkPixels >= requiredDarkPixels)
                        break;
                }

                if (darkPixels < requiredDarkPixels)
                    continue;

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }

            if (maxX >= minX && maxX - minX >= 20)
            {
                const int horizontalPadding = 12;
                minX = Math.Max(0, minX - horizontalPadding);
                maxX = Math.Min(source.Width - 1, maxX + horizontalPadding);
                cropX = minX;
                cropWidth = maxX - minX + 1;
            }
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            source.UnlockBits(data);
        }

        // Do not clone/draw the source while it is locked. Graphics.DrawImage on a locked bitmap throws
        // "Bitmap region is already locked" and leaves the scan loop stuck in the Reading state.
        return CropBitmap(source, cropX, 0, cropWidth, source.Height);
    }

    private static Bitmap CropBitmap(Bitmap source, int x, int y, int width, int height)
    {
        var destination = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(destination);
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, width, height),
            new Rectangle(x, y, width, height),
            GraphicsUnit.Pixel);
        return destination;
    }

    private static Bitmap PrepareForOcr(Bitmap source, bool binary)
    {
        var destination = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(destination))
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);

        var data = destination.LockBits(
            new Rectangle(0, 0, destination.Width, destination.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format24bppRgb);

        byte[]? buffer = null;
        try
        {
            int stride = Math.Abs(data.Stride);
            int length = stride * destination.Height;
            buffer = ArrayPool<byte>.Shared.Rent(length);
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, length);

            var histogram = new int[256];
            int pixelCount = destination.Width * destination.Height;
            for (int y = 0; y < destination.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < destination.Width; x++)
                {
                    int index = row + x * 3;
                    int gray = (77 * buffer[index + 2] + 150 * buffer[index + 1] + 29 * buffer[index]) >> 8;
                    histogram[gray]++;
                    buffer[index] = buffer[index + 1] = buffer[index + 2] = (byte)gray;
                }
            }

            int low = Percentile(histogram, pixelCount, 0.01);
            int high = Percentile(histogram, pixelCount, 0.99);
            if (high <= low)
            {
                low = 0;
                high = 255;
            }

            var stretchedHistogram = new int[256];
            for (int y = 0; y < destination.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < destination.Width; x++)
                {
                    int index = row + x * 3;
                    int gray = buffer[index];
                    int stretched = Math.Clamp((gray - low) * 255 / Math.Max(1, high - low), 0, 255);
                    stretchedHistogram[stretched]++;
                    buffer[index] = buffer[index + 1] = buffer[index + 2] = (byte)stretched;
                }
            }

            if (binary)
            {
                int threshold = ComputeOtsuThreshold(stretchedHistogram, pixelCount);
                for (int y = 0; y < destination.Height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < destination.Width; x++)
                    {
                        int index = row + x * 3;
                        byte value = buffer[index] > threshold ? (byte)255 : (byte)0;
                        buffer[index] = buffer[index + 1] = buffer[index + 2] = value;
                    }
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, length);
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            destination.UnlockBits(data);
        }

        return destination;
    }

    private static int Percentile(int[] histogram, int count, double percentile)
    {
        int target = (int)Math.Round(count * percentile);
        int cumulative = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
                return value;
        }

        return 255;
    }

    private static int ComputeOtsuThreshold(int[] histogram, int pixelCount)
    {
        long totalWeighted = 0;
        for (int value = 0; value < histogram.Length; value++)
            totalWeighted += (long)value * histogram[value];

        long backgroundWeighted = 0;
        int backgroundCount = 0;
        double bestVariance = -1;
        int bestThreshold = 180;

        for (int threshold = 0; threshold < 255; threshold++)
        {
            backgroundCount += histogram[threshold];
            if (backgroundCount == 0)
                continue;

            int foregroundCount = pixelCount - backgroundCount;
            if (foregroundCount == 0)
                break;

            backgroundWeighted += (long)threshold * histogram[threshold];
            double backgroundMean = (double)backgroundWeighted / backgroundCount;
            double foregroundMean = (double)(totalWeighted - backgroundWeighted) / foregroundCount;
            double difference = backgroundMean - foregroundMean;
            double variance = (double)backgroundCount * foregroundCount * difference * difference;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = threshold;
            }
        }

        return bestThreshold;
    }

    private static Bitmap Upscale(Bitmap source, int factor)
    {
        var destination = new Bitmap(
            source.Width * factor,
            source.Height * factor,
            PixelFormat.Format24bppRgb);

        using var graphics = Graphics.FromImage(destination);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, destination.Width, destination.Height);
        return destination;
    }

    internal static int ExtractMultiplier(string normalized)
    {
        var match = MultiplierRegex.Match(normalized);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out int multiplier) &&
            multiplier >= 1)
        {
            return Math.Min(multiplier, 999);
        }

        return 1;
    }

    internal static string StripLeadingNoise(string normalized)
    {
        string value = normalized;
        var quantityMarker = QuantityMarkerRegex.Match(value);

        if (quantityMarker.Success)
        {
            value = value[(quantityMarker.Index + quantityMarker.Length)..];
        }
        else
        {
            value = Regex.Replace(
                value,
                @"^(?:\S{1,2}\s+|\S*\d\S*\s+)+",
                string.Empty,
                RegexOptions.CultureInvariant);
        }

        value = Regex.Replace(
            value,
            @"^[^\p{L}]+",
            string.Empty,
            RegexOptions.CultureInvariant);

        return value.Trim();
    }

    private static bool HasLongWord(string normalized, int minLength)
    {
        int run = 0;
        foreach (char character in normalized)
        {
            if (char.IsLetter(character))
            {
                if (++run >= minLength)
                    return true;
            }
            else
            {
                run = 0;
            }
        }

        return false;
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    internal static string NormalizeName(string text) => ItemNameNormalizer.Normalize(text);

    public void Dispose()
    {
        foreach (TesseractEngine engine in _engines)
            engine.Dispose();
    }
}

using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace Poe2LootLens;

internal sealed class ScanEngine : IDisposable
{
    private static readonly HashSet<string> GenericUnpricedRewardNames = new(StringComparer.Ordinal)
    {
        "кольцо",
        "пояс",
        "амулет",
        "бижутерия",
        "жезл",
        "двуручная булава",
        "талисман",
        "посох",
        "копье",
        "скипетр",
        "боевой посох",
        "одноручная булава",
        "фокус",
        "самострел",
        "лук",
        "шлем",
        "перчатки",
        "сапоги",
        "нательный доспех",
    };

    private static readonly string[] TooltipReferenceLines =
    [
        "предметы и монстры могут быть обычными",
        "волшебными синий редкими желтый и",
        "возрастает их сложность",
        "items and monsters can be normal magic rare and unique",
    ];

    private readonly AppConfig _config;
    private readonly PriceRepository _prices;
    private readonly IconCache _icons;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Dictionary<string, int> _lastPositions = new();
    private RollingLogWriter? _logWriter;

    private IReadOnlyDictionary<string, PriceEntry>? _matchSnapshot;
    private readonly Dictionary<string, CachedMatch> _matchCache = new(StringComparer.Ordinal);
    private int _resetRequested;
    private string _lastOcrTraceSignature = string.Empty;
    private int _suppressedOcrTraceCount;
    private DateTime _lastOcrTraceAt = DateTime.MinValue;
    private IReadOnlyDictionary<int, string[]> _keysByLength =
        new Dictionary<int, string[]>();

    private static volatile bool _dismissed;
    private static volatile bool _showing;

    public bool IsRunning => _loopTask is { IsCompleted: false };
    public static bool IsShowing => _showing;
    public static void RequestDismiss() => _dismissed = true;
    public void RequestReset() => Interlocked.Exchange(ref _resetRequested, 1);
    public void RequestScanNow() => RequestReset();

    public ScanEngine(AppConfig config, PriceRepository prices, IconCache icons)
    {
        _config = config;
        _prices = prices;
        _icons = icons;
    }

    public void Start()
    {
        if (IsRunning)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    public void StopAndWait(TimeSpan timeout)
    {
        Stop();
        try
        {
            _loopTask?.Wait(timeout);
        }
        catch
        {
            // Cancellation/shutdown.
        }
    }

    private void OpenLog()
    {
        try { _logWriter = new RollingLogWriter("price-scan.log", _config); }
        catch { _logWriter = null; }
    }

    private void Log(string message)
    {
        AppLogLevel level = message.StartsWith("ERROR", StringComparison.Ordinal)
            ? AppLogLevel.Error
            : message.StartsWith("START", StringComparison.Ordinal) ||
              message.StartsWith("STOP", StringComparison.Ordinal) ||
              message.Contains("CONFIRMED", StringComparison.Ordinal) ||
              message.Contains("RESET", StringComparison.Ordinal)
                ? AppLogLevel.Information
                : AppLogLevel.Debug;
        _logWriter?.Write(level, message);
    }

    private void LogDebug(string message) => _logWriter?.Write(AppLogLevel.Debug, message);
    private void Trace(string message) => _logWriter?.Write(AppLogLevel.Trace, message);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        OpenLog();
        try
        {
            var tessdataDirectory = Path.Combine(AppContext.BaseDirectory, "tessdata");
            if (!Directory.Exists(tessdataDirectory))
            {
                Log($"ERROR tessdata not found at {tessdataDirectory}");
                return;
            }

            string priceOcrLanguage = OcrScanner.ResolvePriceOcrLanguage(_config.GameLanguage);
            Log(
                $"START prices={_prices.ItemCount} icons={_icons.IsAvailable} " +
                $"region={_config.RegionRect} gameLanguage={_config.GameLanguage} " +
                $"ocr={priceOcrLanguage} ocrWorkers=3 " +
                $"displayThreshold={_config.DivineDisplayThreshold:0.##} " +
                $"thresholdCurrency={_config.DisplayThresholdCurrency}");

            bool ocrDiagnosticsEnabled = _logWriter?.IsEnabled(AppLogLevel.Debug) == true;
            using var scanner = new OcrScanner(
                tessdataDirectory,
                _config.GameLanguage,
                ocrDiagnosticsEnabled ? LogDebug : null,
                ocrDiagnosticsEnabled);
            using var capture = new ScreenCaptureSession(_config.RegionRect);
            var detector = new ListDetector();
            var stopwatch = Stopwatch.StartNew();
            var slots = new List<RowSlot>();

            IReadOnlyList<PriceRow> lastRows = [];
            IReadOnlyList<PriceRow>? publishedRows = null;
            bool publishedOpen = false;
            bool publishedReading = false;

            _dismissed = false;
            bool isOpen = false;
            bool confirmedOpen = false;
            bool candidateDetected = false;
            bool suppressHintUntilConfirm = false;
            int brightStreak = 0;
            int darkStreak = 0;
            int dismissDarkStreak = 0;
            string? panelCandidateIdentity = null;
            int panelCandidateFrames = 0;

            ulong lastFingerprint = 0;
            bool hasFingerprint = false;
            IReadOnlyDictionary<string, PriceEntry>? lastPriceSnapshot = null;
            DateTime lastOcrAt = DateTime.MinValue;
            DateTime lastUnresolvedRetryAt = DateTime.MinValue;
            DateTime lastTopmostAt = DateTime.MinValue;

            const int MinOcrIntervalMs = 90;
            const int UnresolvedRetryMs = 280;
            const int FailedRetryMs = 1500;
            const int OpenCycleMs = 65;
            const int ClosedCycleMs = 90;
            const int DarkToReleaseDismiss = 2;
            const int ConfirmedCloseDarkFrames = 2;

            void Publish(IReadOnlyList<PriceRow> rows, bool panelOpen, bool reading, bool force = false)
            {
                if (!force &&
                    ReferenceEquals(rows, publishedRows) &&
                    panelOpen == publishedOpen &&
                    reading == publishedReading)
                {
                    return;
                }

                publishedRows = rows;
                publishedOpen = panelOpen;
                publishedReading = reading;
                PriceOverlayManager.UpdateState(rows, panelOpen, reading);
            }

            void CloseConfirmedPanel(string reason)
            {
                if (confirmedOpen || isOpen)
                    Log($"panel CLOSED ({reason})");

                isOpen = false;
                confirmedOpen = false;
                candidateDetected = false;
                suppressHintUntilConfirm = true;
                brightStreak = 0;
                darkStreak = 0;
                panelCandidateIdentity = null;
                panelCandidateFrames = 0;
                slots.Clear();
                scanner.ResetPanelState();
                lastRows = [];
                hasFingerprint = false;
                _showing = false;

                // HideNow is deliberately sent before the normal state publication so closing through
                // the panel's X button or another UI action cannot leave stale prices on screen.
                PriceOverlayManager.HideNow();
                Publish(lastRows, false, false, force: true);

                // A close decision is deliberately sticky for a few dark frames. Without this guard,
                // one stale/bright capture could immediately resurrect the just-closed overlay with the
                // previous rows. The normal dismiss branch releases automatically once the panel is
                // genuinely gone; a transient detector miss reopens only after fresh stable evidence.
                _dismissed = true;
                dismissDarkStreak = 0;
            }

            PriceOverlayManager.EnsureVisible(
                _config.RegionRect,
                _config.OverlayXOffset,
                _icons,
                _config.DivineDisplayThreshold,
                _config.DisplayThresholdCurrency,
                _config.GameLanguage,
                RequestReset,
                () =>
                {
                    RequestDismiss();
                    PriceOverlayManager.HideNow();
                });
            Log("overlay ready");

            while (!cancellationToken.IsCancellationRequested)
            {
                long cycleStartedAt = stopwatch.ElapsedMilliseconds;

                try
                {
                    if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
                    {
                        slots.Clear();
                        scanner.ResetPanelState();
                        _lastPositions.Clear();
                        lastRows = [];
                        confirmedOpen = false;
                        candidateDetected = true;
                        panelCandidateIdentity = null;
                        panelCandidateFrames = 0;
                        _dismissed = false;
                        suppressHintUntilConfirm = false;
                        hasFingerprint = false;
                        lastUnresolvedRetryAt = DateTime.MinValue;
                        Publish([], false, true, force: true);
                        Log("manual RESCAN requested; row locks and geometry cleared");
                    }

                    Bitmap bitmap = capture.Capture();
                    ListPanelSample panelSample = detector.Analyze(bitmap);
                    int brightness = panelSample.AverageBrightness;
                    bool rumorPanelFrame = panelSample.IsOpen && RumorScanner.LooksLikeRumorPanelImage(bitmap);
                    bool brightFrame = panelSample.IsOpen && !rumorPanelFrame;
                    bool darkFrame = !brightFrame;
                    if (rumorPanelFrame)
                    {
                        candidateDetected = false;
                        Trace("price scan ignored Atlas rumor panel geometry");
                    }

                    if (_dismissed)
                    {
                        if (darkFrame)
                            dismissDarkStreak++;
                        else
                            dismissDarkStreak = 0;

                        if (dismissDarkStreak >= DarkToReleaseDismiss)
                        {
                            _dismissed = false;
                            suppressHintUntilConfirm = true;
                            Log("dismiss released (panel closed)");
                        }

                        isOpen = false;
                        confirmedOpen = false;
                        brightStreak = 0;
                        darkStreak = 0;
                        slots.Clear();
                        scanner.ResetPanelState();
                        lastRows = [];
                        panelCandidateIdentity = null;
                        panelCandidateFrames = 0;
                        hasFingerprint = false;
                        _showing = false;
                        Publish(lastRows, false, false);
                    }
                    else
                    {
                        dismissDarkStreak = 0;

                        // A single dark sample may be caused by a transient capture/hover/overlay frame.
                        // Require two consecutive dark frames: this still hides the overlay in roughly
                        // 130 ms while preventing a valid panel from being closed immediately after OCR.
                        if (confirmedOpen && darkFrame)
                        {
                            darkStreak++;
                            if (darkStreak >= ConfirmedCloseDarkFrames)
                                CloseConfirmedPanel($"detector brightness={brightness} parchment={panelSample.ParchmentFraction:0.00} deviation={panelSample.BrightnessDeviation:0.0} separators={panelSample.SeparatorCount}");
                        }
                        else
                        {
                            if (confirmedOpen)
                                darkStreak = 0;
                            if (brightFrame)
                            {
                                brightStreak++;
                                darkStreak = 0;
                            }
                            else if (darkFrame)
                            {
                                darkStreak++;
                                brightStreak = 0;
                            }
                            else
                            {
                                brightStreak = 0;
                                darkStreak = 0;
                            }

                            bool wasOpen = isOpen;
                            if (!isOpen && brightStreak >= 2)
                                isOpen = true;
                            else if (isOpen && darkStreak >= 2)
                                isOpen = false;

                            if (isOpen != wasOpen)
                            {
                                Trace(
                                    $"panel brightness-state {(isOpen ? "OPEN" : "CLOSED")} " +
                                    $"brightness={brightness} parchment={panelSample.ParchmentFraction:0.00} separators={panelSample.SeparatorCount}");

                                // Bright parchment alone is not enough to show OCR feedback. Atlas labels,
                                // tooltips and other panels can have the same average colour. Wait until OCR
                                // confirms a plausible reward-row structure or the user explicitly requests it.
                            }

                            if (isOpen)
                            {
                                var now = DateTime.UtcNow;
                                bool priceSnapshotChanged =
                                    !ReferenceEquals(lastPriceSnapshot, _prices.Prices);
                                if (priceSnapshotChanged)
                                {
                                    lastPriceSnapshot = _prices.Prices;
                                    hasFingerprint = false;
                                }

                                bool intervalElapsed =
                                    (now - lastOcrAt).TotalMilliseconds >= MinOcrIntervalMs;

                                if (intervalElapsed)
                                {
                                    lastOcrAt = now;
                                    ulong fingerprint = ScreenCapture.ComputeFingerprint(bitmap);
                                    bool unchanged =
                                        hasFingerprint &&
                                        fingerprint == lastFingerprint &&
                                        !priceSnapshotChanged;

                                    bool hasActiveUnresolvedRows =
                                        !confirmedOpen ||
                                        slots.Count == 0 ||
                                        slots.Any(slot => !slot.Locked && !slot.RecognitionFailed);
                                    bool hasFailedUnresolvedRows =
                                        slots.Any(slot => !slot.Locked && slot.RecognitionFailed);
                                    bool hasUnresolvedRows =
                                        hasActiveUnresolvedRows || hasFailedUnresolvedRows;
                                    int retryInterval =
                                        hasActiveUnresolvedRows ? UnresolvedRetryMs : FailedRetryMs;
                                    bool retryUnresolved =
                                        hasUnresolvedRows &&
                                        (now - lastUnresolvedRetryAt).TotalMilliseconds >= retryInterval;
                                    bool shouldScan =
                                        priceSnapshotChanged ||
                                        !hasFingerprint ||
                                        hasUnresolvedRows && (!unchanged || retryUnresolved);

                                    if (shouldScan)
                                    {
                                        lastFingerprint = fingerprint;
                                        hasFingerprint = true;
                                        lastUnresolvedRetryAt = now;

                                        // The panel is static. Once a row is trusted, skip it and spend OCR
                                        // only on unresolved rows. This also prevents a later noisy frame from
                                        // replacing a correct price merely because the mouse changed hover state.
                                        int[] lockedCenters =
                                            confirmedOpen &&
                                            !priceSnapshotChanged
                                                ? slots.Where(slot => slot.Locked).Select(slot => slot.Y).ToArray()
                                                : [];
                                        bool partialScan = lockedCenters.Length > 0;
                                        var ocrStopwatch = Stopwatch.StartNew();
                                        var ocrRows = scanner.Scan(
                                            bitmap,
                                            partialScan ? lockedCenters : null,
                                            _prices.KnownNames);
                                        ocrStopwatch.Stop();
                                        var reads = ocrRows.Count == 0
                                            ? (IReadOnlyList<PriceRow>)[]
                                            : BuildPriceRows(ocrRows);
                                        int[] emptyCenters = ocrRows
                                            .Where(row => string.IsNullOrEmpty(row.NormalizedName))
                                            .Select(row => row.CenterY)
                                            .ToArray();
                                        RemoveEmptyUnconfirmedSlots(slots, emptyCenters);
                                        int trustedReadCount = reads.Count(row =>
                                            (row.HasPrice && row.QuantityTrusted) ||
                                            row.MatchKind == "known-no-price");
                                        bool hasPricedRead = reads.Any(row =>
                                            row.HasPrice && row.QuantityTrusted);
                                        bool hasQuantityUncertainRead = reads.Any(row =>
                                            row.HasPrice && !row.QuantityTrusted);
                                        bool hasKnownUnpricedRead = reads.Any(row =>
                                            row.MatchKind == "known-no-price");
                                        bool strongRowStructure =
                                            ocrRows.Count >= 2 && trustedReadCount >= 2;
                                        // A single catalog price or known generic reward is a candidate, not final
                                        // proof. Strong multi-row structure confirms immediately; a single row must
                                        // repeat on a second OCR frame through HasSufficientPanelEvidence below.
                                        bool hasPlausibleRows =
                                            hasPricedRead ||
                                            hasKnownUnpricedRead ||
                                            hasQuantityUncertainRead ||
                                            strongRowStructure;
                                        // The reading indicator describes the latest completed OCR pass, not
                                        // historical suspicion. If the current pass is OCR_EMPTY/unrelated, clear
                                        // it immediately instead of leaving a spinner over an empty crop.
                                        candidateDetected = hasPlausibleRows;

                                        TraceOcrSummary(
                                            ocrRows.Count,
                                            partialScan,
                                            reads,
                                            ocrStopwatch.ElapsedMilliseconds);

                                        if (isOpen && reads.Count > 0 &&
                                            (confirmedOpen || hasPlausibleRows))
                                        {
                                            lastRows = MergeReads(slots, reads, partialScan);
                                        }

                                        string trustedIdentity = string.Join(
                                            "|",
                                            reads.Where(row =>
                                                    row.HasPrice ||
                                                    row.MatchKind == "known-no-price")
                                                .OrderBy(row => row.CenterY)
                                                .Select(row => row.HasPrice
                                                    ? row.QuantityTrusted
                                                        ? $"price:{row.PriceSourceId}:{row.Multiplier}:{QuantizeDebugY(row.CenterY)}"
                                                        : $"quantity:{row.PriceSourceId}:{QuantizeDebugY(row.CenterY)}"
                                                    : $"known:{row.Name}:{QuantizeDebugY(row.CenterY)}"));
                                        if (trustedIdentity.Length == 0)
                                        {
                                            panelCandidateIdentity = null;
                                            panelCandidateFrames = 0;
                                        }
                                        else if (string.Equals(
                                                     panelCandidateIdentity,
                                                     trustedIdentity,
                                                     StringComparison.Ordinal))
                                        {
                                            panelCandidateFrames++;
                                        }
                                        else
                                        {
                                            panelCandidateIdentity = trustedIdentity;
                                            panelCandidateFrames = 1;
                                        }

                                        bool hasLockedTrustedRow = lastRows.Any(row =>
                                            (row.HasPrice && row.QuantityTrusted) ||
                                            row.MatchKind == "known-no-price");
                                        bool hasDisplayableQuantityUncertainRow = lastRows.Any(row =>
                                            row.HasPrice && !row.QuantityTrusted);
                                        bool panelEvidenceConfirmed = HasSufficientPanelEvidence(
                                            hasLockedTrustedRow,
                                            strongRowStructure,
                                            panelCandidateFrames) ||
                                            (hasDisplayableQuantityUncertainRow && panelCandidateFrames >= 2);
                                        if (!confirmedOpen && panelEvidenceConfirmed)
                                        {
                                            confirmedOpen = true;
                                            suppressHintUntilConfirm = false;
                                            Log(
                                                "panel CONFIRMED " +
                                                $"(trusted rewards; rows={ocrRows.Count}, trusted={trustedReadCount}, frames={panelCandidateFrames})");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (confirmedOpen)
                                    CloseConfirmedPanel("brightness hysteresis");
                                else
                                {
                                    slots.Clear();
                                    scanner.ResetPanelState();
                                    lastRows = [];
                                    confirmedOpen = false;
                                    candidateDetected = false;
                                    panelCandidateIdentity = null;
                                    panelCandidateFrames = 0;
                                    hasFingerprint = false;
                                }
                            }

                            if (isOpen)
                            {
                                bool reading =
                                    !confirmedOpen &&
                                    candidateDetected &&
                                    !suppressHintUntilConfirm;
                                _showing = confirmedOpen;
                                Publish(confirmedOpen ? lastRows : [], confirmedOpen, reading);
                            }
                            else
                            {
                                _showing = false;
                                Publish(lastRows, false, false);
                            }

                            if (confirmedOpen &&
                                (DateTime.UtcNow - lastTopmostAt).TotalSeconds >= 2)
                            {
                                PriceOverlayManager.ForceTopmost();
                                lastTopmostAt = DateTime.UtcNow;
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    Log($"ERROR {exception.GetType().Name}: {exception.Message}");
                }

                long cycleDuration = stopwatch.ElapsedMilliseconds - cycleStartedAt;
                int targetCycle = isOpen ? OpenCycleMs : ClosedCycleMs;
                int delay = (int)Math.Max(0, targetCycle - cycleDuration);
                if (delay > 0)
                {
                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _showing = false;
            PriceOverlayManager.Hide();
            Log("loop exited");
        }
        finally
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }

    private void TraceOcrSummary(
        int ocrRowCount,
        bool partialScan,
        IReadOnlyList<PriceRow> reads,
        long elapsedMilliseconds)
    {
        string signature = string.Join("|", reads.Select(row =>
            $"{QuantizeDebugY(row.CenterY)}:{row.Name}:{row.MatchKind}:{row.Multiplier}:" +
            $"{row.BundleCount}:{row.QuantityTrusted}:{row.RecognitionFailed}"));
        var now = DateTime.UtcNow;
        if (signature == _lastOcrTraceSignature &&
            now - _lastOcrTraceAt < TimeSpan.FromSeconds(8))
        {
            _suppressedOcrTraceCount++;
            return;
        }

        if (_suppressedOcrTraceCount > 0)
        {
            Log($"OCR unchanged x{_suppressedOcrTraceCount} (suppressed)");
            _suppressedOcrTraceCount = 0;
        }

        _lastOcrTraceSignature = signature;
        _lastOcrTraceAt = now;
        LogDebug(
            $"OCR {ocrRowCount} rows partial={partialScan} elapsed={elapsedMilliseconds}ms -> " +
            string.Join(" | ", reads.Select(row =>
                $"raw='{row.OcrText.Trim()}' y={row.CenterY} " +
                (row.HasPrice
                    ? $"HIT->'{row.Name}' {row.MatchKind} " +
                      $"score={row.MatchScore:0.000} ocr={row.OcrConfidence:0} " +
                      $"agree={row.VariantAgreement} " +
                      $"qty={(row.QuantityTrusted ? "trusted" : "uncertain")} " +
                      $"unitEx={row.ExaltedValue:0.###} id={row.PriceSourceId}"
                    : $"{row.MatchKind.ToUpperInvariant()} " +
                      $"ocr={row.OcrConfidence:0} bundle={row.BundleCount}"))));
    }

    private IReadOnlyList<PriceRow> BuildPriceRows(IReadOnlyList<OcrRow> ocrRows)
    {
        var snapshot = _prices.Prices;
        if (!ReferenceEquals(snapshot, _matchSnapshot))
        {
            _matchSnapshot = snapshot;
            _matchCache.Clear();
            _keysByLength = snapshot.Keys
                .GroupBy(key => key.Length)
                .ToDictionary(group => group.Key, group => group.ToArray());
        }

        var rows = new List<PriceRow>(ocrRows.Count);
        var newPositions = new Dictionary<string, int>(ocrRows.Count, StringComparer.Ordinal);
        IReadOnlyDictionary<int, string> fluxRepairs = InferThaumaturgicFluxNames(ocrRows);

        foreach (var row in ocrRows)
        {
            if (row.NormalizedName.Contains("runeshape", StringComparison.Ordinal) ||
                row.NormalizedName.Contains("рунотвор", StringComparison.Ordinal))
            {
                continue;
            }

            string lookupName = fluxRepairs.TryGetValue(row.CenterY, out string? repairedFluxName)
                ? repairedFluxName
                : row.NormalizedName;
            int stableY = row.CenterY;
            if (!string.IsNullOrEmpty(lookupName) &&
                _lastPositions.TryGetValue(lookupName, out int previousY) &&
                Math.Abs(previousY - row.CenterY) < 5)
            {
                stableY = previousY;
            }

            if (!string.IsNullOrEmpty(lookupName))
                newPositions[lookupName] = stableY;

            if (string.IsNullOrEmpty(lookupName))
            {
                // OCR_EMPTY is not a user-facing result. It means the crop did not contain a stable
                // reward name, so showing a spinner/question mark would turn transient geometry noise
                // into a permanent overlay artefact. Keep retrying internally and render nothing.
                continue;
            }

            if (IsNonRewardUiText(lookupName))
                continue;

            if (TryResolveGemKey(lookupName, out var gemKey))
            {
                if (gemKey is not null && snapshot.TryGetValue(gemKey, out var gemEntry))
                {
                    rows.Add(CreatePricedRow(
                        row,
                        stableY,
                        gemEntry,
                        gemKey,
                        exactMatch: true,
                        matchKind: "exact",
                        matchScore: 1d));
                }
                else
                {
                    rows.Add(CreateUnpricedRow(
                        row,
                        stableY,
                        lookupName,
                        "known-no-price",
                        1d));
                }

                continue;
            }

            if (TryResolvePriceCandidates(
                    snapshot,
                    lookupName,
                    out var entry,
                    out var matchedKey,
                    out bool trusted,
                    out string matchKind,
                    out double matchScore))
            {
                rows.Add(CreatePricedRow(
                    row,
                    stableY,
                    entry,
                    matchedKey,
                    trusted,
                    matchKind,
                    matchScore));
                continue;
            }

            var lookupCandidates = BuildLookupCandidates(lookupName);
            bool ambiguous = lookupCandidates.Any(candidate =>
                _prices.AmbiguousNames.Contains(candidate.Name));
            bool knownWithoutPrice = lookupCandidates.Any(candidate =>
                _prices.KnownNames.Contains(candidate.Name));
            bool labeledReward = lookupCandidates.Any(candidate =>
                candidate.Kind.StartsWith("label", StringComparison.Ordinal));
            bool genericRewardWithoutMarketPrice = IsKnownUnpricedReward(lookupName);
            string missKind = ambiguous
                ? "ambiguous"
                : knownWithoutPrice || labeledReward || genericRewardWithoutMarketPrice
                    ? "known-no-price"
                    : "unmatched";
            rows.Add(CreateUnpricedRow(
                row,
                stableY,
                lookupName,
                missKind,
                missKind == "unmatched" ? 0d : 1d));
        }

        _lastPositions = newPositions;
        return rows;
    }

    private static PriceRow CreatePricedRow(
        OcrRow row,
        int stableY,
        PriceEntry entry,
        string matchedKey,
        bool exactMatch,
        string matchKind,
        double matchScore)
    {
        bool exactIdentity =
            matchKind is "exact" or "label" or "suffix" &&
            (string.Equals(matchedKey, row.NormalizedName, StringComparison.Ordinal) ||
             matchKind is "label" or "suffix");
        int multiplier = row.QuantityTrusted
            ? ResolveEffectiveMultiplier(row, exactIdentity)
            : Math.Max(1, row.LeadingMultiplier);

        return new PriceRow(
            stableY,
            row.RawText,
            entry.DivineValue,
            entry.ExaltedValue,
            true,
            multiplier,
            matchedKey,
            exactMatch,
            MemeKind.None,
            row.Confidence,
            matchKind,
            matchScore,
            entry.SourceId,
            row.Variant,
            row.VariantAgreement,
            row.BundleCount,
            row.QuantityTrusted);
    }

    private static PriceRow CreateUnpricedRow(
        OcrRow row,
        int stableY,
        string name,
        string matchKind,
        double matchScore) =>
        new(
            stableY,
            row.RawText,
            0m,
            0m,
            false,
            Math.Max(1, row.LeadingMultiplier),
            name,
            false,
            MemeKind.None,
            row.Confidence,
            matchKind,
            matchScore,
            string.Empty,
            row.Variant,
            row.VariantAgreement,
            row.BundleCount,
            true);

    internal static int ResolveEffectiveMultiplier(OcrRow row, bool exactIdentity)
    {
        int leading = Math.Max(1, row.LeadingMultiplier);
        int bundle = exactIdentity ? Math.Max(1, row.BundleCount) : 1;
        return (int)Math.Min((long)leading * bundle, 999L);
    }

    internal static IReadOnlyDictionary<int, string> InferThaumaturgicFluxNames(
        IReadOnlyList<OcrRow> ocrRows)
    {
        var ordered = ocrRows
            .OrderBy(row => row.CenterY)
            .ToArray();
        var result = new Dictionary<int, string>();
        int start = 0;

        while (start < ordered.Length)
        {
            while (start < ordered.Length && !LooksLikeThaumaturgicFluxSignal(ordered[start].NormalizedName))
                start++;
            if (start >= ordered.Length)
                break;

            int end = start + 1;
            while (end < ordered.Length && LooksLikeThaumaturgicFluxSignal(ordered[end].NormalizedName))
                end++;

            OcrRow[] segment = ordered[start..end];
            int strongSignals = 0;
            foreach (OcrRow row in segment)
            {
                bool strong = row.NormalizedName.Contains("расплав", StringComparison.Ordinal) ||
                              row.NormalizedName.Contains("чарод", StringComparison.Ordinal);
                if (!strong)
                    continue;

                strongSignals++;
                int? directLevel = TryExtractThaumaturgicFluxLevel(row.NormalizedName);
                if (directLevel is not null)
                    result[row.CenterY] = $"чародейский расплав уровень {directLevel.Value}";
            }

            // Sequence inference is intentionally restricted to panels with at least two explicit
            // family signals. This recovers a damaged middle level without rewriting unrelated
            // levelled gems that happen to be adjacent in another reward list.
            if (strongSignals >= 2)
                InferThaumaturgicFluxSegment(segment, result);

            start = end;
        }

        return result;
    }

    private static void InferThaumaturgicFluxSegment(
        IReadOnlyList<OcrRow> segment,
        IDictionary<int, string> result)
    {
        var anchors = new List<(int Index, int Level)>();
        for (int index = 0; index < segment.Count; index++)
        {
            string name = segment[index].NormalizedName;
            bool strong = name.Contains("расплав", StringComparison.Ordinal) ||
                          name.Contains("чарод", StringComparison.Ordinal);
            int? level = strong ? TryExtractThaumaturgicFluxLevel(name) : null;
            if (level is null)
                continue;

            anchors.Add((index, level.Value));
            result[segment[index].CenterY] = $"чародейский расплав уровень {level.Value}";
        }

        if (anchors.Count < 2)
            return;

        int descendingVotes = 0;
        int ascendingVotes = 0;
        for (int left = 0; left < anchors.Count; left++)
        {
            for (int right = left + 1; right < anchors.Count; right++)
            {
                int indexDelta = anchors[right].Index - anchors[left].Index;
                int levelDelta = anchors[right].Level - anchors[left].Level;
                if (levelDelta == -indexDelta)
                    descendingVotes++;
                else if (levelDelta == indexDelta)
                    ascendingVotes++;
            }
        }

        int direction = descendingVotes > ascendingVotes && descendingVotes > 0
            ? -1
            : ascendingVotes > descendingVotes && ascendingVotes > 0
                ? 1
                : 0;
        if (direction == 0)
            return;

        int firstAnchorIndex = anchors.Min(anchor => anchor.Index);
        int lastAnchorIndex = anchors.Max(anchor => anchor.Index);
        for (int index = firstAnchorIndex; index <= lastAnchorIndex; index++)
        {
            if (result.ContainsKey(segment[index].CenterY))
                continue;

            int? inferred = null;
            bool conflict = false;
            foreach ((int anchorIndex, int anchorLevel) in anchors)
            {
                int candidate = anchorLevel + direction * (index - anchorIndex);
                if (candidate is < 1 or > 20)
                    continue;
                if (inferred is null)
                    inferred = candidate;
                else if (inferred.Value != candidate)
                    conflict = true;
            }

            if (!conflict && inferred is not null)
                result[segment[index].CenterY] = $"чародейский расплав уровень {inferred.Value}";
        }
    }

    private static bool LooksLikeThaumaturgicFluxSignal(string name) =>
        name.Contains("расплав", StringComparison.Ordinal) ||
        name.Contains("чарод", StringComparison.Ordinal) ||
        name.Contains("уровень", StringComparison.Ordinal);

    internal static int? TryExtractThaumaturgicFluxLevel(string name)
    {
        int marker = name.IndexOf("уровень", StringComparison.Ordinal);
        if (marker < 0)
            return null;

        string tail = name[(marker + "уровень".Length)..].Trim();
        MatchCollection tokens = Regex.Matches(
            tail,
            @"(?<!\p{L})(?:\d{1,2}|т)(?!\p{L})",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (tokens.Count != 1)
            return null;

        string token = tokens[0].Value;
        if (string.Equals(token, "т", StringComparison.OrdinalIgnoreCase))
            return 7;
        if (!int.TryParse(token, out int level) || level is < 1 or > 20)
            return null;
        return level;
    }

    private bool TryResolvePriceCandidates(
        IReadOnlyDictionary<string, PriceEntry> snapshot,
        string originalName,
        out PriceEntry entry,
        out string matchedKey,
        out bool trusted,
        out string matchKind,
        out double matchScore)
    {
        foreach (var candidate in BuildLookupCandidates(originalName))
        {
            if (!TryResolvePrice(
                    snapshot,
                    candidate.Name,
                    out entry,
                    out matchedKey,
                    out trusted,
                    out matchKind,
                    out matchScore))
            {
                continue;
            }

            if (candidate.Kind != "raw" && matchKind == "exact")
                matchKind = candidate.Kind;
            return true;
        }

        if (TryResolveUniqueSuffix(snapshot, originalName, out entry, out matchedKey, out matchScore))
        {
            trusted = matchScore >= 0.70d;
            matchKind = "suffix";
            return true;
        }

        entry = null!;
        matchedKey = originalName;
        trusted = false;
        matchKind = "none";
        matchScore = 0d;
        return false;
    }

    internal static IReadOnlyList<(string Name, string Kind)> BuildLookupCandidates(string name)
    {
        var candidates = new List<(string Name, string Kind)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string candidateName, string kind)
        {
            candidateName = candidateName.Trim();
            if (candidateName.Length >= 4 && seen.Add(candidateName))
                candidates.Add((candidateName, kind));
        }

        Add(name, "raw");

        // A row can describe a concrete skill/support gem instead of a priced uncut gem. Keep the raw
        // value first, then try the semantic name without the UI label. Later transformations are also
        // applied to this stripped candidate, so "Умение: Возвышенн ый ..." can be both unlabelled and
        // repaired without globally mutating OCR text.
        foreach (var candidate in candidates.ToArray())
        {
            Match label = Regex.Match(
                candidate.Name,
                @"^(?:умен\s*ие|поддерж\s*ка|skill|support)\s+",
                RegexOptions.CultureInvariant);
            if (label.Success)
                Add(candidate.Name[label.Length..], "label");
        }

        foreach (var candidate in candidates.ToArray())
        {
            string joined = Regex.Replace(
                candidate.Name,
                @"(?<=\p{L}{5})\s+(?=\p{L}{1,2}(?:\s|$))",
                string.Empty,
                RegexOptions.CultureInvariant);
            if (joined != candidate.Name)
            {
                string kind = candidate.Kind == "raw"
                    ? "repair"
                    : $"{candidate.Kind}-repair";
                Add(joined, kind);
            }
        }

        foreach (var candidate in candidates.ToArray())
        {
            // Observed Russian OCR occasionally drops the first syllable in "расплав" and returns
            // "Чародейский сплав (Уровень N)". Repair only this stable item-family prefix so the
            // level remains pinned and unrelated alloy names are never rewritten.
            const string damagedThaumaturgicPrefix = "чародейский сплав уровень ";
            if (candidate.Name.StartsWith(damagedThaumaturgicPrefix, StringComparison.Ordinal))
            {
                string repaired = "чародейский расплав уровень " +
                                  candidate.Name[damagedThaumaturgicPrefix.Length..];
                string kind = candidate.Kind == "raw"
                    ? "ocr-repair"
                    : $"{candidate.Kind}-ocr-repair";
                Add(repaired, kind);
            }
        }

        foreach (var candidate in candidates.ToArray())
        {
            int? fluxLevel = TryExtractThaumaturgicFluxLevel(candidate.Name);
            if (fluxLevel is null ||
                !candidate.Name.Contains("расплав", StringComparison.Ordinal) &&
                !candidate.Name.Contains("чарод", StringComparison.Ordinal))
            {
                continue;
            }

            string repaired = $"чародейский расплав уровень {fluxLevel.Value}";
            string kind = candidate.Kind == "raw"
                ? "ocr-family-repair"
                : $"{candidate.Kind}-ocr-family-repair";
            Add(repaired, kind);
        }

        foreach (var candidate in candidates.ToArray())
        {
            if (string.Equals(candidate.Name, "не альдура", StringComparison.Ordinal))
                Add("гнев альдура", "ocr-repair");
        }

        // Observed Russian OCR can lose several narrow strokes in "Деталь доспеха" and return
        // stable garbage such as "дет иослеха". Recover only when both the distinctive beginning
        // and the damaged armour-word stem are present; this is far narrower than lowering the global
        // fuzzy threshold and therefore cannot turn arbitrary low-confidence rows into priced items.
        foreach (var candidate in candidates.ToArray())
        {
            string compact = candidate.Name.Replace(" ", string.Empty, StringComparison.Ordinal);
            bool looksLikeArmourPart =
                compact.StartsWith("дет", StringComparison.Ordinal) &&
                (compact.Contains("оспех", StringComparison.Ordinal) ||
                 compact.Contains("ослех", StringComparison.Ordinal) ||
                 compact.Contains("иослех", StringComparison.Ordinal));
            if (!looksLikeArmourPart)
                continue;

            string kind = candidate.Kind == "raw"
                ? "ocr-family-repair"
                : $"{candidate.Kind}-ocr-family-repair";
            Add("деталь доспеха", kind);
        }

        foreach (var candidate in candidates.ToArray())
        {
            int lastSpace = candidate.Name.LastIndexOf(' ');
            if (lastSpace <= 0 || candidate.Name.Length - lastSpace - 1 != 1)
                continue;

            string trimmedTail = candidate.Name[..lastSpace].TrimEnd();
            if (trimmedTail.Length < 8)
                continue;

            string kind = candidate.Kind == "raw"
                ? "tail-trim"
                : $"{candidate.Kind}-tail-trim";
            Add(trimmedTail, kind);
        }

        return candidates;
    }

    internal static bool IsKnownUnpricedReward(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string normalized = name.Trim();
        if (Regex.IsMatch(
                normalized,
                @"^(?:умен\s*ие|поддерж\s*ка|skill|support)\s+",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        bool randomCurrency =
            normalized.Contains("случайн", StringComparison.Ordinal) &&
            normalized.Contains("валют", StringComparison.Ordinal) ||
            normalized.Contains("random", StringComparison.Ordinal) &&
            normalized.Contains("currency", StringComparison.Ordinal);

        return normalized.StartsWith("уникальн", StringComparison.Ordinal) ||
               normalized.StartsWith("редкий уникальный предмет", StringComparison.Ordinal) ||
               normalized.StartsWith("очень редкий уникальный предмет", StringComparison.Ordinal) ||
               normalized.StartsWith("unique ", StringComparison.Ordinal) ||
               normalized.StartsWith("rare unique item", StringComparison.Ordinal) ||
               normalized.StartsWith("very rare unique item", StringComparison.Ordinal) ||
               GenericUnpricedRewardNames.Contains(normalized) ||
               randomCurrency;
    }

    internal static bool IsNonRewardUiText(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        string normalized = name.Trim();
        if (normalized is "редкость" or "rarity")
            return true;

        string[] tooltipFragments =
        [
            "предметы и монстры могут быть",
            "обычными серый цвет",
            "волшебными синий",
            "редкими желтый",
            "уникальными коричневый",
            "вместе с редкостью монстров",
            "возрастает их сложность",
            "items and monsters can be",
            "normal magic rare and unique",
        ];
        if (tooltipFragments.Any(fragment =>
                normalized.Contains(fragment, StringComparison.Ordinal)))
        {
            return true;
        }

        // The in-game rarity help tooltip can cover several reward rows. Its small beige text is often
        // heavily distorted by OCR, so exact fragments alone are insufficient. Compare only reasonably
        // long lines against a tiny fixed set of help-text references; the conservative threshold keeps
        // normal reward names out while suppressing real observed garbage such as
        // "опшебньмы ... редкими ..." and "козраелает ... спожиость".
        if (normalized.Length < 18)
            return false;

        foreach (string reference in TooltipReferenceLines)
        {
            int distance = Levenshtein(normalized, reference);
            double similarity = 1d - (double)distance / Math.Max(normalized.Length, reference.Length);
            if (similarity >= 0.64d)
                return true;
        }

        return false;
    }

    private static int QuantizeDebugY(int centerY) =>
        (int)Math.Round(centerY / 5d, MidpointRounding.AwayFromZero) * 5;

    private static bool TryResolveUniqueSuffix(
        IReadOnlyDictionary<string, PriceEntry> snapshot,
        string name,
        out PriceEntry entry,
        out string matchedKey,
        out double score)
    {
        entry = null!;
        matchedKey = name;
        score = 0d;
        if (name.Length < 8)
            return false;

        string? unique = null;
        int count = 0;
        foreach (string key in snapshot.Keys)
        {
            if (key.Length <= name.Length || !key.EndsWith(name, StringComparison.Ordinal))
                continue;

            double coverage = (double)name.Length / key.Length;
            if (coverage < 0.62d)
                continue;

            unique = key;
            score = coverage;
            if (++count > 1)
                return false;
        }

        if (unique is null)
            return false;

        matchedKey = unique;
        entry = snapshot[unique];
        return true;
    }

    private bool TryResolvePrice(
        IReadOnlyDictionary<string, PriceEntry> snapshot,
        string name,
        out PriceEntry entry,
        out string matchedKey,
        out bool trusted,
        out string matchKind,
        out double matchScore)
    {
        if (snapshot.TryGetValue(name, out entry!))
        {
            matchedKey = name;
            trusted = true;
            matchKind = "exact";
            matchScore = 1d;
            return true;
        }

        if (_matchCache.TryGetValue(name, out var cached))
        {
            if (cached.Key is not null && snapshot.TryGetValue(cached.Key, out entry!))
            {
                matchedKey = cached.Key;
                trusted = cached.Trusted;
                matchKind = cached.Kind;
                matchScore = cached.Score;
                return true;
            }

            entry = null!;
            matchedKey = name;
            trusted = false;
            matchKind = "none";
            matchScore = 0d;
            return false;
        }

        string? uniquePrefix = null;
        int prefixCount = 0;
        if (name.Length >= 8)
        {
            foreach (var key in snapshot.Keys)
            {
                if (!key.StartsWith(name, StringComparison.Ordinal))
                    continue;

                double coverage = (double)name.Length / key.Length;
                if (coverage < 0.72)
                    continue;

                uniquePrefix = key;
                prefixCount++;
                if (prefixCount > 1)
                    break;
            }
        }

        if (prefixCount == 1 && uniquePrefix is not null)
        {
            double score = (double)name.Length / uniquePrefix.Length;
            bool strong = score >= 0.96;
            _matchCache[name] = new CachedMatch(uniquePrefix, strong, "prefix", score);
            entry = snapshot[uniquePrefix];
            matchedKey = uniquePrefix;
            trusted = strong;
            matchKind = "prefix";
            matchScore = score;
            return true;
        }

        if (name.Length >= 6 &&
            BestFuzzy(_keysByLength, name) is { } fuzzy &&
            fuzzy.Score >= (name.Length >= 18 ? LongNameFuzzyThreshold : FuzzyThreshold) &&
            fuzzy.Margin >= FuzzyMinimumMargin)
        {
            bool strong = fuzzy.Score >= ImmediateFuzzyThreshold;
            _matchCache[name] = new CachedMatch(fuzzy.Key, strong, "fuzzy", fuzzy.Score);
            entry = snapshot[fuzzy.Key];
            matchedKey = fuzzy.Key;
            trusted = strong;
            matchKind = "fuzzy";
            matchScore = fuzzy.Score;
            return true;
        }

        _matchCache[name] = new CachedMatch(null, false, "none", 0d);
        entry = null!;
        matchedKey = name;
        trusted = false;
        matchKind = "none";
        matchScore = 0d;
        return false;
    }

    private const double FuzzyThreshold = 0.90;
    private const double LongNameFuzzyThreshold = 0.86;
    private const double FuzzyMinimumMargin = 0.055;
    private const double ImmediateFuzzyThreshold = 0.96;

    private static FuzzyResult? BestFuzzy(
        IReadOnlyDictionary<int, string[]> keysByLength,
        string name)
    {
        string? best = null;
        double bestScore = 0d;
        double secondScore = 0d;

        int lengthWindow = Math.Clamp(name.Length / 5, 3, 8);
        int minLength = Math.Max(1, name.Length - lengthWindow);
        int maxLength = name.Length + lengthWindow;
        for (int length = minLength; length <= maxLength; length++)
        {
            if (!keysByLength.TryGetValue(length, out var candidates))
                continue;

            foreach (var key in candidates)
            {
                int distance = Levenshtein(name, key);
                double score = 1.0 - (double)distance / Math.Max(name.Length, key.Length);
                if (score > bestScore)
                {
                    secondScore = bestScore;
                    bestScore = score;
                    best = key;
                }
                else if (score > secondScore)
                {
                    secondScore = score;
                }
            }
        }

        return best is null
            ? null
            : new FuzzyResult(best, bestScore, bestScore - secondScore);
    }

    internal static bool TryResolveGemKey(string normalizedName, out string? key)
    {
        key = null;
        if (!normalizedName.Contains("gem", StringComparison.Ordinal))
            return false;

        var type = Regex.Match(normalizedName, @"\b(skill|spirit|support)\b");
        if (!type.Success)
            return false;

        var level = Regex.Match(normalizedName, @"\blevel\s+(\d+)\b");
        if (level.Success)
            key = $"uncut {type.Groups[1].Value} gem level {level.Groups[1].Value}";

        return true;
    }

    internal static int Levenshtein(string left, string right)
    {
        int[] previous = ArrayPool<int>.Shared.Rent(right.Length + 1);
        int[] current = ArrayPool<int>.Shared.Rent(right.Length + 1);

        try
        {
            for (int column = 0; column <= right.Length; column++)
                previous[column] = column;

            for (int row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (int column = 1; column <= right.Length; column++)
                {
                    int cost = left[row - 1] == right[column - 1] ? 0 : 1;
                    current[column] = Math.Min(
                        Math.Min(current[column - 1] + 1, previous[column] + 1),
                        previous[column - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[right.Length];
        }
        finally
        {
            ArrayPool<int>.Shared.Return(previous);
            ArrayPool<int>.Shared.Return(current);
        }
    }

    private sealed class RowSlot
    {
        public int Y;
        public PriceRow Latest = null!;
        public bool Locked;
        public PriceRow LockedRow = null!;
        public string? PendingIdentity;
        public int PendingCount;
        public string? ReplacementIdentity;
        public int ReplacementCount;
        public string? FailureIdentity;
        public int FailureCount;
        public int RecognitionAttempts;
        public bool RecognitionFailed;
        public int Unseen;
    }

    private static void RemoveEmptyUnconfirmedSlots(
        List<RowSlot> slots,
        IReadOnlyList<int> emptyCenters)
    {
        const int tolerance = 20;
        if (emptyCenters.Count == 0 || slots.Count == 0)
            return;

        // OCR_EMPTY means this crop currently contains no usable glyphs. Remove only unresolved
        // visual feedback at that location; a trusted lock survives transient blank captures and a
        // later real row will be recreated normally.
        slots.RemoveAll(slot =>
            !slot.Locked && emptyCenters.Any(center => Math.Abs(slot.Y - center) <= tolerance));
    }

    private IReadOnlyList<PriceRow> MergeReads(
        List<RowSlot> slots,
        IReadOnlyList<PriceRow> reads,
        bool partialScan)
    {
        const int tolerance = 20;
        const int evictAfter = 3;
        int failAfterAttempts = _config.PriceUnknownAttempts;

        // A confirmed row remains locked while the panel is open. The identity includes the item,
        // source id, and multiplier, so an OCR error in "(3)" cannot silently mutate a correct lock.
        var matched = new HashSet<RowSlot>();
        foreach (var read in reads)
        {
            RowSlot? slot = null;
            int bestDistance = int.MaxValue;

            foreach (var candidate in slots)
            {
                if (matched.Contains(candidate))
                    continue;

                int distance = Math.Abs(candidate.Y - read.CenterY);
                if (distance <= tolerance && distance < bestDistance)
                {
                    bestDistance = distance;
                    slot = candidate;
                }
            }

            if (slot is null)
            {
                slot = new RowSlot { Y = read.CenterY };
                slots.Add(slot);
            }

            matched.Add(slot);
            slot.Unseen = 0;
            slot.Latest = read;

            if (slot.Locked)
            {
                string currentIdentity = RowIdentity(slot.LockedRow);
                string incomingIdentity = RowIdentity(read);
                bool trustedIncoming = read.HasPrice || read.MatchKind == "known-no-price";

                if (trustedIncoming && incomingIdentity == currentIdentity)
                {
                    // Same identity: allow a 30-minute price snapshot refresh to update the numeric value
                    // and keep known-without-price rows stable without spending more OCR cycles on them.
                    slot.LockedRow = read with
                    {
                        CenterY = slot.Y,
                        RecognitionAttempts = read.MatchKind == "known-no-price" ? 1 : 0,
                        RecognitionFailed = read.MatchKind == "known-no-price",
                    };
                    slot.ReplacementIdentity = null;
                    slot.ReplacementCount = 0;
                }
                else if (trustedIncoming &&
                         (read.MatchKind == "exact" || read.MatchKind == "known-no-price"))
                {
                    // A panel can change without a dark frame, but never replace a lock from one noisy
                    // read. This also protects bundle counts from a single 1↔3 OCR substitution.
                    if (slot.ReplacementIdentity == incomingIdentity)
                        slot.ReplacementCount++;
                    else
                    {
                        slot.ReplacementIdentity = incomingIdentity;
                        slot.ReplacementCount = 1;
                    }

                    if (slot.ReplacementCount >= 2)
                    {
                        Trace(
                            $"relocked y={slot.Y} '{slot.LockedRow.Name}' x{slot.LockedRow.Multiplier} " +
                            $"-> '{read.Name}' x{read.Multiplier}");
                        slot.LockedRow = read with
                        {
                            CenterY = slot.Y,
                            RecognitionAttempts = read.MatchKind == "known-no-price" ? 1 : 0,
                            RecognitionFailed = read.MatchKind == "known-no-price",
                        };
                        slot.ReplacementIdentity = null;
                        slot.ReplacementCount = 0;
                    }
                }

                continue;
            }

            if (read.MatchKind == "known-no-price")
            {
                // This is a successful classification, not an OCR failure. Lock it immediately so skills,
                // support gems and generic unique rewards show an explanatory status once and are skipped by
                // partial scans just like priced rows.
                slot.PendingIdentity = null;
                slot.PendingCount = 0;
                slot.FailureIdentity = null;
                slot.FailureCount = 0;
                slot.RecognitionAttempts = 1;
                slot.RecognitionFailed = true;
                slot.Locked = true;
                slot.LockedRow = read with
                {
                    CenterY = slot.Y,
                    RecognitionAttempts = 1,
                    RecognitionFailed = true,
                };
                Trace($"locked y={slot.Y} '{read.Name}' as known-no-price");
                continue;
            }

            if (read.HasPrice && !read.QuantityTrusted)
            {
                // The item and unit price are known, but the hovered gold outline made a large
                // multiplier unreliable. Keep the row visible as "× ?", do not lock it, and keep
                // rescanning until the cursor leaves or OCR variants agree.
                slot.PendingIdentity = null;
                slot.PendingCount = 0;
                slot.FailureIdentity = null;
                slot.FailureCount = 0;
                slot.RecognitionAttempts = 0;
                slot.RecognitionFailed = false;
                continue;
            }

            if (read.HasPrice)
            {
                slot.FailureIdentity = null;
                slot.FailureCount = 0;
                slot.RecognitionAttempts = 0;
                slot.RecognitionFailed = false;
                string identity = RowIdentity(read);
                if (slot.PendingIdentity == identity)
                    slot.PendingCount++;
                else
                {
                    slot.PendingIdentity = identity;
                    slot.PendingCount = 1;
                }

                int required = RequiredConfirmations(read);
                if (slot.PendingCount >= required)
                {
                    Trace(
                        $"locked y={slot.Y} '{read.Name}' x{read.Multiplier} " +
                        $"{read.MatchKind} {read.MatchScore:0.000} after {required} read(s)");
                    slot.Locked = true;
                    slot.LockedRow = read with { CenterY = slot.Y };
                }
            }
            else
            {
                slot.PendingIdentity = null;
                slot.PendingCount = 0;

                string failureKey = string.IsNullOrEmpty(read.Name)
                    ? read.OcrText.Trim()
                    : read.Name;
                string failureIdentity = $"{read.MatchKind}\u001f{failureKey}";
                if (string.Equals(slot.FailureIdentity, failureIdentity, StringComparison.Ordinal))
                    slot.FailureCount++;
                else
                {
                    slot.FailureIdentity = failureIdentity;
                    slot.FailureCount = 1;
                }

                // Count actual OCR attempts, not only repetitions of byte-identical garbage. Tesseract
                // often changes one or two characters between frames; the user should still get a spinner
                // for a bounded number of reads and then a clear unknown marker instead of an endless retry.
                slot.RecognitionAttempts++;
                slot.RecognitionFailed = slot.RecognitionAttempts >= failAfterAttempts;
            }
        }

        for (int index = slots.Count - 1; index >= 0; index--)
        {
            if (matched.Contains(slots[index]))
                continue;

            if (slots[index].Locked || partialScan)
                continue;

            if (++slots[index].Unseen > evictAfter)
                slots.RemoveAt(index);
        }

        var display = new List<PriceRow>(slots.Count);
        foreach (var slot in slots.OrderBy(slot => slot.Y))
        {
            display.Add(slot.Locked
                ? slot.LockedRow
                : slot.Latest.HasPrice && !slot.Latest.QuantityTrusted
                    ? slot.Latest with
                    {
                        CenterY = slot.Y,
                        RecognitionAttempts = 0,
                        RecognitionFailed = false,
                    }
                    : slot.Latest with
                    {
                        CenterY = slot.Y,
                        HasPrice = false,
                        DivineValue = 0m,
                        ExaltedValue = 0m,
                        RecognitionAttempts = slot.RecognitionAttempts,
                        RecognitionFailed = slot.RecognitionFailed,
                    });
        }

        return display;
    }

    private static string RowIdentity(PriceRow row) =>
        $"{row.Name}\u001f{row.Multiplier}\u001f{row.PriceSourceId}";


    internal static bool HasSufficientPanelEvidence(
        bool hasLockedTrustedRow,
        bool strongRowStructure,
        int repeatedCandidateFrames) =>
        hasLockedTrustedRow && (strongRowStructure || repeatedCandidateFrames >= 2);

    internal static int RequiredConfirmations(PriceRow row)
    {
        if (row.Meme != MemeKind.None)
            return 1;

        // Quantities are the easiest characters to misread and directly multiply the displayed value.
        if (row.Multiplier > 1 || row.BundleCount > 1)
            return 2;

        if (row.MatchKind == "exact")
        {
            bool runeLike =
                row.Name.Contains("руна", StringComparison.Ordinal) ||
                row.Name.Contains("rune", StringComparison.Ordinal);

            // Rune names are very similar to one another. Require temporal consensus even when the
            // OCR result happens to be an exact dictionary key.
            if (runeLike)
                return 2;

            return row.OcrConfidence >= 65f ? 1 : 2;
        }

        // Prefix/fuzzy matches are useful recovery paths, not proof. Two matching frames are cheap
        // during the short acquisition burst and avoid a confidently wrong market quote.
        return 2;
    }

    public void Dispose()
    {
        StopAndWait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
    }

    private readonly record struct CachedMatch(
        string? Key,
        bool Trusted,
        string Kind,
        double Score);

    private readonly record struct FuzzyResult(
        string Key,
        double Score,
        double Margin);
}

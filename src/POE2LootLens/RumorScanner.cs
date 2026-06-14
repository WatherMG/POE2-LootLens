using System.Buffers;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Tesseract;

namespace Poe2LootLens;

internal sealed class RumorScanner : IDisposable
{
    // Only a local rectangle around the cursor is captured. It is intentionally large enough for a
    // tooltip placed above, below, left, right, or diagonally from the hovered ship icon, but it is
    // still much smaller than a full-screen capture.
    private const int CaptureWidth = 960;
    private const int CaptureHeight = 900;
    private const int CaptureCursorOffsetX = CaptureWidth / 2;
    private const int CaptureCursorOffsetY = CaptureHeight / 2;
    private static readonly CaptureLayout PrimaryCapture =
        new(CaptureWidth, CaptureHeight, CaptureCursorOffsetX, CaptureCursorOffsetY);
    private static readonly CaptureLayout ExpandedCapture =
        new(1280, 1200, 640, 700);

    private const int IslandAssociationRadius = 56;
    private const int CursorStableRadius = 6;
    private const int MaxRecognitionCacheEntries = 64;
    private const int ManualBurstCaptureCount = 4;
    private static readonly TimeSpan CursorSettleTime = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan AutomaticAcquisitionInterval = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan ManualBurstInterval = TimeSpan.FromMilliseconds(320);

    private RumorCatalog _catalog;
    private readonly TesseractEngine _engine;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<IslandSession> _islands = [];
    private readonly Dictionary<ulong, CachedRecognition> _recognitionCache = [];
    private readonly Queue<ulong> _recognitionCacheOrder = [];
    private readonly AppConfig _config;
    private readonly int _scanIntervalMs;
    private readonly int _overlayTimeoutSeconds;
    private readonly string _overlayHideMode;
    private readonly int _confirmationFrames;
    private readonly TimeSpan _confirmationWindow;
    private readonly bool _automaticMode;
    private readonly string _sortMode;
    private readonly IReadOnlyList<string> _categoryOrder;
    private readonly string _language;
    private readonly string _uiLanguage;
    private readonly string _ocrLanguages;
    private RollingLogWriter? _logWriter;
    private DateTime _catalogObservedWriteTimeUtc;
    private DateTime _lastCatalogCheckAt = DateTime.MinValue;
    private string _lastLoggedStatus = string.Empty;

    private Task? _loopTask;
    private IslandSession? _activeIsland;
    private int _viewIslandIndex = -1;
    private Point _stableCursor;
    private DateTime _cursorStableSince = DateTime.MinValue;
    private DateTime _lastActivityAt = DateTime.MinValue;
    private DateTime _lastPanelSeenAt = DateTime.MinValue;
    private DateTime _lastOcrAt = DateTime.MinValue;
    private DateTime _lastPublishedAt = DateTime.MinValue;
    private int _scanCount;
    private int _resetRequested;
    private int _hideRequested;
    private int _togglePinRequested;
    private int _previousIslandRequested;
    private int _nextIslandRequested;
    private int _manualScanRequested;
    private int _manualBurstRemaining;
    private int _automaticAcquisitionRemaining;
    private int _panelMissCount;
    private DateTime _manualBurstDeadline = DateTime.MinValue;
    private DateTime _lastExpandedCaptureAt = DateTime.MinValue;
    private bool _overlayVisible;
    private bool _pinned;
    private bool _disposed;

    public event Action<string>? StatusChanged;
    public bool IsRunning => _loopTask is { IsCompleted: false };

    public RumorScanner(
        string tessdataDirectory,
        RumorCatalog catalog,
        AppConfig config,
        bool automaticMode = false)
    {
        _catalog = catalog;
        _config = ConfigStore.Normalize(ConfigCopy.Clone(config), migrateLegacyDefaults: false);
        _catalogObservedWriteTimeUtc = catalog.SourceLastWriteTimeUtc;
        _scanIntervalMs = _config.RumorScanIntervalMs;
        _overlayTimeoutSeconds = _config.RumorOverlayTimeoutSeconds;
        _overlayHideMode = _config.RumorOverlayHideMode;
        _confirmationFrames = _config.RumorConfirmationFrames;
        _confirmationWindow = TimeSpan.FromMilliseconds(_config.RumorConfirmationWindowMs);
        _automaticMode = automaticMode;
        _sortMode = _config.RumorSortMode;
        _categoryOrder = _config.RumorCategoryOrder.ToArray();
        _language = _config.AppLanguage;
        _uiLanguage = UiLanguage.Resolve(_config.UiLanguage);

        string languages = ResolveRumorOcrLanguages(_config.RumorOcrLanguage);
        foreach (string model in languages.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string modelPath = Path.Combine(tessdataDirectory, $"{model}.traineddata");
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException(
                    $"Rumor OCR model was not found: {model}.traineddata",
                    modelPath);
            }
        }

        _ocrLanguages = languages;
        _engine = new TesseractEngine(tessdataDirectory, languages, EngineMode.LstmOnly);
        _engine.SetVariable("preserve_interword_spaces", "1");
        _engine.SetVariable("user_defined_dpi", "300");

        RumorOverlayManager.Configure(
            RequestReset,
            RequestHide,
            RequestTogglePin,
            RequestPreviousIsland,
            RequestNextIsland);
    }

    internal static string ResolveRumorOcrLanguages(string? setting)
    {
        string normalized = (setting ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "ru" => "rus",
            "en+ru" => "eng+rus",
            _ => "eng",
        };
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return;
        _loopTask = Task.Run(() => RunLoopAsync(_cancellation.Token));
    }

    public void RequestReset() => Interlocked.Exchange(ref _resetRequested, 1);
    public void RequestHide() => Interlocked.Exchange(ref _hideRequested, 1);
    public void RequestTogglePin() => Interlocked.Exchange(ref _togglePinRequested, 1);
    public void RequestPreviousIsland() => Interlocked.Exchange(ref _previousIslandRequested, 1);
    public void RequestNextIsland() => Interlocked.Exchange(ref _nextIslandRequested, 1);
    public void RequestManualScan()
    {
        Interlocked.Exchange(ref _manualScanRequested, 1);
    }

    public void RequestHideIfUnpinned()
    {
        if (!_pinned)
            Interlocked.Exchange(ref _hideRequested, 1);
    }

    public void StopAndWait(TimeSpan timeout)
    {
        if (_disposed)
            return;
        _cancellation.Cancel();
        try { _loopTask?.Wait(timeout); } catch { }
        RumorOverlayManager.HideNow();
        RumorLineOverlayManager.HideNow();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        OpenLog();
        LogInformation(
            $"START mode={(_automaticMode ? "auto" : "manual")} interval={_scanIntervalMs}ms " +
            $"hide={_overlayHideMode} timeout={_overlayTimeoutSeconds}s " +
            $"confirmation={_confirmationFrames}/{_confirmationWindow.TotalMilliseconds:0}ms " +
            $"ocrSetting={_config.RumorOcrLanguage} ocrModels={_ocrLanguages} " +
            $"sort={_sortMode} categories=[{string.Join(",", _categoryOrder)}] " +
            $"capture={CaptureWidth}x{CaptureHeight} fallback={ExpandedCapture.Width}x{ExpandedCapture.Height} " +
            $"defaultCatalog='{_catalog.DefaultPath}' " +
            $"userCatalog='{_catalog.UserPath}' entries={_catalog.Entries.Count}");
        ReportStatus(_automaticMode
            ? Ui("Сканер слухов активен — наведите курсор на значок острова", "Rumor scanner is active — hover an island icon")
            : Ui("Ручной режим — наведите курсор на значок острова и нажмите кнопку сканирования или хоткей", "Manual mode — hover an island icon and press the scan button or hotkey"));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    HandleOverlayCommands();

                    if (_manualBurstRemaining > 0 &&
                        DateTime.UtcNow > _manualBurstDeadline)
                    {
                        _manualBurstRemaining = 0;
                        _manualBurstDeadline = DateTime.MinValue;
                        LogDebug("MANUAL-BURST deadline reached");
                    }

                    bool manualRequest = Interlocked.Exchange(ref _manualScanRequested, 0) == 1;
                    if (manualRequest)
                    {
                        _manualBurstRemaining = 0;
                        foreach (IslandSession existingIsland in _islands)
                            PrunePending(existingIsland, DateTime.UtcNow);
                        _manualBurstRemaining = Math.Max(ManualBurstCaptureCount, _confirmationFrames);
                        _manualBurstDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(
                            Math.Max(8000d, _confirmationWindow.TotalMilliseconds + 3000d));
                        _lastOcrAt = DateTime.MinValue;
                        LogInformation($"MANUAL-BURST start captures={_manualBurstRemaining}");
                    }

                    bool manualPulse = _manualBurstRemaining > 0 &&
                                       DateTime.UtcNow <= _manualBurstDeadline;
                    if (!_automaticMode && !manualPulse)
                    {
                        ApplyOptionalTimeout();
                        await Task.Delay(90, cancellationToken);
                        continue;
                    }

                    Point cursor = System.Windows.Forms.Cursor.Position;
                    if (RumorOverlayManager.ContainsScreenPoint(cursor) ||
                        RumorLineOverlayManager.ContainsScreenPoint(cursor))
                    {
                        // Do not treat hovering the overlay as hovering another island. This prevents
                        // corner-to-corner jumps when the user moves the mouse over the quick card.
                        await Task.Delay(120, cancellationToken);
                        continue;
                    }

                    UpdateCursorStability(cursor);

                    if (!manualPulse && DateTime.UtcNow - _cursorStableSince < CursorSettleTime)
                    {
                        await Task.Delay(90, cancellationToken);
                        continue;
                    }

                    bool fastAutomaticAcquisition = _automaticMode && _automaticAcquisitionRemaining > 0;
                    TimeSpan minimumInterval = manualPulse
                        ? ManualBurstInterval
                        : fastAutomaticAcquisition
                            ? AutomaticAcquisitionInterval
                            : TimeSpan.FromMilliseconds(_scanIntervalMs);
                    if (DateTime.UtcNow - _lastOcrAt < minimumInterval)
                    {
                        ApplyOptionalTimeout();
                        await Task.Delay(manualPulse ? 45 : 100, cancellationToken);
                        continue;
                    }

                    _lastOcrAt = DateTime.UtcNow;
                    if (fastAutomaticAcquisition)
                        _automaticAcquisitionRemaining--;
                    ReloadCatalogIfChanged();
                    using Bitmap screenshot = CapturePanelAroundCursor(
                        cursor,
                        out Rectangle panelBounds,
                        out CaptureLayout captureLayout);
                    if (panelBounds.IsEmpty)
                    {
                        RumorLineOverlayManager.HideNow();
                        RegisterPanelMiss();
                        ConsumeManualBurstAttempt(manualPulse, null);
                        ApplyOptionalTimeout();
                        await Task.Delay(140, cancellationToken);
                        continue;
                    }

                    if (PanelInteriorContainsCursor(panelBounds, captureLayout))
                    {
                        RumorLineOverlayManager.HideNow();
                        Trace($"candidate rejected before OCR: cursor is inside panel bounds={panelBounds}");
                        RegisterPanelMiss();
                        ConsumeManualBurstAttempt(manualPulse, null);
                        ApplyOptionalTimeout();
                        await Task.Delay(140, cancellationToken);
                        continue;
                    }

                    // The parchment geometry is much cheaper and more stable than OCR. Treat a
                    // plausible panel as present immediately so one poor text pass cannot make the
                    // quick-preview overlay blink while the game tooltip is still open.
                    _panelMissCount = 0;
                    _lastPanelSeenAt = DateTime.UtcNow;
                    Rectangle screenPanelBounds = ToScreenPanelBounds(cursor, panelBounds, captureLayout);
                    using var panel = CropBitmap(screenshot, panelBounds);
                    ulong fingerprint = ComputeRumorContentFingerprint(panel);
                    Trace(
                        $"candidate capture={captureLayout.Width}x{captureLayout.Height} " +
                        $"bounds={panelBounds} fingerprint={fingerprint:X16}");

                    CachedRecognition recognition;
                    if (TryGetCachedRecognition(fingerprint, out var cached, out bool approximateCacheHit))
                    {
                        recognition = cached;
                        if (approximateCacheHit)
                            RememberRecognition(fingerprint, recognition);
                        IReadOnlyList<Rectangle> cachedSlots = DetectRumorLineSlotBounds(panel);
                        PublishLineIndicators(
                            screenPanelBounds,
                            cachedSlots,
                            recognition.LineStatuses);
                        Trace(
                            $"cache HIT{(approximateCacheHit ? "-SIMILAR" : string.Empty)} " +
                            $"header={recognition.HeaderDetected} matches={recognition.Matches.Count}");
                    }
                    else
                    {
                        OcrPanelResult ocrResult = await Task.Run(() => RecognizePanel(panel, screenPanelBounds), cancellationToken);
                        recognition = BuildRecognition(ocrResult);
                        // Cache only a fully resolved line layout. An unresolved/question-mark result
                        // must remain retryable: otherwise every capture in a manual burst would replay
                        // the first poor OCR result and the user could never improve it without moving
                        // the panel enough to change the fingerprint.
                        if (AreAllVisibleRumorLinesResolved(recognition.LineStatuses))
                            RememberRecognition(fingerprint, recognition);
                        else
                            Trace("cache SKIP unresolved line statuses");
                        LogRecognition(fingerprint, panelBounds, cursor, recognition, ocrResult.Passes);
                    }

                    IReadOnlyList<RumorMatch> matches = recognition.Matches;
                    bool strongPanel = IsConfirmedRumorPanel(
                        recognition.HeaderDetected,
                        matches,
                        recognition.TrustedSlotMatch);
                    bool weakCandidate = matches.Any(match => match.Exact) ||
                                         matches.Count(match => match.Score >= 0.84d) >= 2 ||
                                         matches.Count >= 3;
                    if (!strongPanel && !weakCandidate)
                    {
                        RumorLineOverlayManager.HideNow();
                        LogDebug(
                            $"REJECT fingerprint={fingerprint:X16}: parchment-like panel without a reliable rumor " +
                            $"(matches={matches.Count}, header={recognition.HeaderDetected}, trustedSlot={recognition.TrustedSlotMatch})");
                        RegisterPanelMiss();
                        ConsumeManualBurstAttempt(manualPulse, null);
                        ApplyOptionalTimeout();
                        await Task.Delay(120, cancellationToken);
                        continue;
                    }

                    _panelMissCount = 0;
                    _lastPanelSeenAt = DateTime.UtcNow;
                    _scanCount++;
                    IslandSession island = FindOrCreateIsland(cursor);
                    island.LastPanelBounds = screenPanelBounds;
                    island.LastDiagnostic = recognition.Diagnostic;
                    _activeIsland = island;
                    _viewIslandIndex = _islands.IndexOf(island);
                    island.LastPanelAt = DateTime.UtcNow;
                    island.ScanCount++;
                    _lastActivityAt = DateTime.UtcNow;

                    ObservationResult observation = ApplyObservation(island, recognition, fingerprint);
                    if (matches.Count > 0)
                    {
                        island.LastMatchAt = DateTime.UtcNow;
                        island.FailedAttempts = 0;
                        _overlayVisible = true;
                        Publish(observation.CurrentConfirmed, scanning: island.Pending.Count > 0, panelDetected: true);
                        int totalRumors = _islands.Sum(candidate => candidate.Seen.Count);
                        LogInformation(
                            $"OBSERVE island={_viewIslandIndex + 1}/{_islands.Count} " +
                            $"matches=[{string.Join(",", matches.Select(match => match.Entry.Id))}] " +
                            $"confirmedNow={observation.ConfirmedNow} pending={island.Pending.Count} " +
                            $"islandTotal={island.Seen.Count} allTotal={totalRumors}");
                        ReportStatus(island.Pending.Count > 0
                            ? Ui(
                                $"Проверяется: {island.Pending.Count} · остров {_viewIslandIndex + 1}/{_islands.Count} · подтверждено: {island.Seen.Count}",
                                $"Checking: {island.Pending.Count} · island {_viewIslandIndex + 1}/{_islands.Count} · confirmed: {island.Seen.Count}")
                            : Ui(
                                $"Распознано: {observation.CurrentConfirmed.Count} · остров {_viewIslandIndex + 1}/{_islands.Count} · на острове: {island.Seen.Count} · всего: {totalRumors}",
                                $"Recognized: {observation.CurrentConfirmed.Count} · island {_viewIslandIndex + 1}/{_islands.Count} · on island: {island.Seen.Count} · total: {totalRumors}"));
                        bool allVisibleLinesResolved =
                            AreAllVisibleRumorLinesResolved(recognition.LineStatuses);
                        ConsumeManualBurstAttempt(
                            manualPulse,
                            island,
                            matches.Count,
                            allVisibleLinesResolved);
                        await Task.Delay(120, cancellationToken);
                        continue;
                    }

                    PrunePending(island, DateTime.UtcNow);
                    island.FailedAttempts++;
                    _overlayVisible = true;
                    Publish([], scanning: island.FailedAttempts < 5 || island.Pending.Count > 0, panelDetected: true);
                    LogDebug(
                        $"PANEL island={_viewIslandIndex + 1}/{_islands.Count} " +
                        $"recognized header, but no catalog phrase matched; attempt={island.FailedAttempts}");
                    ReportStatus(island.FailedAttempts >= 5
                        ? Ui(
                            $"Остров {_viewIslandIndex + 1}/{_islands.Count}: слух пока не сопоставлен",
                            $"Island {_viewIslandIndex + 1}/{_islands.Count}: rumor is not matched yet")
                        : Ui(
                            $"Остров {_viewIslandIndex + 1}/{_islands.Count}: распознаю слухи…",
                            $"Island {_viewIslandIndex + 1}/{_islands.Count}: recognizing rumors…"));

                    ConsumeManualBurstAttempt(manualPulse, island);
                    await Task.Delay(120, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    LogError($"{exception.GetType().Name}: {exception.Message}");
                    ReportStatus(Ui("Ошибка сканера слухов: ", "Rumor scanner error: ") + exception.Message);
                    try
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            RumorOverlayManager.HideNow();
            RumorLineOverlayManager.HideNow();
            ReportStatus(Ui("Сканер слухов остановлен", "Rumor scanner stopped"));
            LogInformation("STOP");
            CloseLog();
        }
    }

    private void ConsumeManualBurstAttempt(
        bool manualPulse,
        IslandSession? island,
        int currentMatchCount = 0,
        bool allVisibleLinesResolved = false)
    {
        if (!manualPulse)
            return;

        _manualBurstRemaining = Math.Max(0, _manualBurstRemaining - 1);
        bool complete = island is not null &&
                        island.Pending.Count == 0 &&
                        currentMatchCount > 0 &&
                        allVisibleLinesResolved;
        bool expired = DateTime.UtcNow >= _manualBurstDeadline;
        if (complete || expired || _manualBurstRemaining == 0)
        {
            int unresolved = island?.Pending.Count ?? 0;
            if (unresolved > 0)
            {
                string[] unresolvedIds = island!.Pending.Keys.ToArray();
                island.Pending.Clear();
                LogDebug(
                    $"MANUAL-BURST finished with unresolved={unresolved} " +
                    $"ids=[{string.Join(",", unresolvedIds)}]");
                ReportStatus(Ui(
                    $"Ручная проверка завершена: подтверждено {island.Seen.Count}, не подтверждено {unresolved}. Можно повторить OCR.",
                    $"Manual check finished: {island.Seen.Count} confirmed, {unresolved} unconfirmed. You can run OCR again."));
                _overlayVisible = true;
                // Unconfirmed candidates must not leave a permanent CHECK 1/2 badge after the
                // finite manual burst has completed. A subsequent hotkey starts fresh evidence.
                Publish([], scanning: false, panelDetected: true);
            }
            else
            {
                LogInformation("MANUAL-BURST complete");
            }
            _manualBurstRemaining = 0;
            _manualBurstDeadline = DateTime.MinValue;
        }
    }

    internal static bool IsConfirmedRumorPanel(
        bool headerDetected,
        IReadOnlyList<RumorMatch> matches,
        bool trustedSlotMatch = false)
    {
        if (headerDetected || trustedSlotMatch)
            return true;

        // Without a panel-specific header or an exact hit in one of the dedicated rumor-position
        // bands, one fuzzy phrase is too easy to obtain from another parchment UI. Two exact catalog
        // phrases remain a strong fallback for multi-rumor panels whose heading was clipped by OCR.
        return matches.Count(match => match.Exact) >= 2 ||
               (matches.Count == 1 && matches[0].Exact && matches[0].Score >= 0.999d);
    }

    private void HandleOverlayCommands()
    {
        if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
        {
            ClearAllHistory(hide: true);
            _recognitionCache.Clear();
            _recognitionCacheOrder.Clear();
            _cursorStableSince = DateTime.UtcNow;
            ReportStatus(Ui("История всех островов сброшена", "All island history was reset"));
        }

        if (Interlocked.Exchange(ref _hideRequested, 0) == 1)
        {
            _overlayVisible = false;
            RumorOverlayManager.HideNow();
            RumorLineOverlayManager.HideNow();
            ReportStatus(Ui(
                "Окно слухов закрыто; история сохранена до выхода из приложения",
                "Rumor window closed; history is kept until the application exits"));
        }

        if (Interlocked.Exchange(ref _togglePinRequested, 0) == 1)
        {
            _pinned = !_pinned;
            if (_islands.Count > 0)
            {
                _overlayVisible = true;
                Publish([], scanning: false, panelDetected: false);
            }
            ReportStatus(_pinned
                ? Ui("Окно слухов закреплено; его можно перетащить за заголовок", "Rumor window pinned; drag it by the header")
                : Ui("Окно слухов снова следует за активным островом", "Rumor window follows the active island again"));
            Log($"PIN state={_pinned}");
        }

        if (Interlocked.Exchange(ref _previousIslandRequested, 0) == 1)
            NavigateIslands(-1);
        if (Interlocked.Exchange(ref _nextIslandRequested, 0) == 1)
            NavigateIslands(1);
    }

    private void NavigateIslands(int direction)
    {
        if (_islands.Count == 0)
            return;

        if (_viewIslandIndex < 0 || _viewIslandIndex >= _islands.Count)
            _viewIslandIndex = 0;
        else
            _viewIslandIndex = (_viewIslandIndex + direction + _islands.Count) % _islands.Count;

        _overlayVisible = true;
        Publish([], scanning: false, panelDetected: false);
        ReportStatus(Ui(
            $"Показана история острова {_viewIslandIndex + 1}/{_islands.Count}",
            $"Showing island history {_viewIslandIndex + 1}/{_islands.Count}"));
    }

    private void UpdateCursorStability(Point cursor)
    {
        if (_stableCursor == Point.Empty || Distance(cursor, _stableCursor) > CursorStableRadius)
        {
            _stableCursor = cursor;
            _cursorStableSince = DateTime.UtcNow;
            if (_automaticMode)
                _automaticAcquisitionRemaining = 4;
        }
    }

    private void ApplyOptionalTimeout()
    {
        if (!_overlayVisible || _pinned)
            return;

        DateTime now = DateTime.UtcNow;
        bool shouldHide = _overlayHideMode == "timeout" &&
                          _lastActivityAt != DateTime.MinValue &&
                          now - _lastActivityAt >= TimeSpan.FromSeconds(_overlayTimeoutSeconds);
        if (!shouldHide)
            return;

        _overlayVisible = false;
        RumorOverlayManager.HideNow();
        RumorLineOverlayManager.HideNow();
        LogDebug($"AUTO-HIDE mode={_overlayHideMode} misses={_panelMissCount}");
    }

    private void RegisterPanelMiss()
    {
        if (!_overlayVisible)
            return;
        _panelMissCount = Math.Min(_panelMissCount + 1, 1000);
    }

    private ObservationResult ApplyObservation(
        IslandSession island,
        CachedRecognition recognition,
        ulong fingerprint)
    {
        DateTime now = DateTime.UtcNow;
        PrunePending(island, now);
        var currentConfirmed = new List<RumorMatch>();
        int confirmedNow = 0;

        foreach (var match in recognition.Matches)
        {
            recognition.Evidence.TryGetValue(match.Entry.Id, out MatchEvidenceSnapshot? evidence);
            if (island.Seen.ContainsKey(match.Entry.Id))
            {
                island.Seen[match.Entry.Id] = match;
                currentConfirmed.Add(match);
                island.Pending.Remove(match.Entry.Id);
                continue;
            }

            bool immediate = match.Exact &&
                             (HasStrongExactPanelEvidence(recognition.Matches) ||
                              evidence is { ExactPassCount: >= 2 } ||
                              recognition.HeaderDetected && evidence is { ExactSlot: true });
            if (immediate)
            {
                ConfirmRumor(island, match);
                currentConfirmed.Add(match);
                confirmedNow++;
                continue;
            }

            if (!island.Pending.TryGetValue(match.Entry.Id, out var pending))
            {
                pending = new RumorConfirmationState(match, now, _confirmationFrames);
                island.Pending[match.Entry.Id] = pending;
            }
            pending.Observe(match, now, fingerprint);
            LogDebug(
                $"PENDING island={_islands.IndexOf(island) + 1} id={match.Entry.Id} " +
                $"progress={pending.ObservationCount}/{pending.RequiredObservations} " +
                $"exact={match.Exact} passCount={evidence?.PassCount ?? 0}");

            if (pending.ObservationCount >= pending.RequiredObservations)
            {
                RumorMatch confirmed = pending.BestMatch;
                island.Pending.Remove(match.Entry.Id);
                ConfirmRumor(island, confirmed);
                currentConfirmed.Add(confirmed);
                confirmedNow++;
                LogInformation(
                    $"CONFIRMED island={_islands.IndexOf(island) + 1} id={confirmed.Entry.Id} " +
                    $"observations={pending.ObservationCount}");
            }
        }

        return new ObservationResult(currentConfirmed, confirmedNow);
    }

    internal static bool HasStrongExactPanelEvidence(IReadOnlyList<RumorMatch> matches) =>
        matches.Count(match => match.Exact) >= 2;

    private void ConfirmRumor(IslandSession island, RumorMatch match)
    {
        if (!island.Seen.ContainsKey(match.Entry.Id))
            island.SeenOrder.Add(match.Entry.Id);
        island.Seen[match.Entry.Id] = match;
    }

    private void PrunePending(IslandSession island, DateTime now)
    {
        if (!_automaticMode && _manualBurstRemaining > 0)
            return;

        TimeSpan effectiveWindow = _automaticMode
            ? _confirmationWindow
            : TimeSpan.FromMilliseconds(Math.Max(60000d, _confirmationWindow.TotalMilliseconds));
        foreach (string id in island.Pending
                     .Where(pair => now - pair.Value.LastSeenAt > effectiveWindow)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            island.Pending.Remove(id);
            LogDebug($"PENDING-EXPIRED island={_islands.IndexOf(island) + 1} id={id}");
        }
    }

    private IslandSession FindOrCreateIsland(Point cursor)
    {
        int index = FindNearestIslandIndex(
            _islands.Select(island => island.Anchor).ToArray(),
            cursor,
            IslandAssociationRadius);
        if (index >= 0)
        {
            var existing = _islands[index];
            existing.Anchor = new Point(
                (existing.Anchor.X * 3 + cursor.X) / 4,
                (existing.Anchor.Y * 3 + cursor.Y) / 4);
            return existing;
        }

        var created = new IslandSession(cursor);
        _islands.Add(created);
        LogInformation($"ISLAND-NEW index={_islands.Count} anchor={cursor.X},{cursor.Y}");
        return created;
    }

    internal static int FindNearestIslandIndex(
        IReadOnlyList<Point> anchors,
        Point cursor,
        double radius)
    {
        int nearestIndex = -1;
        double nearestDistance = double.MaxValue;
        for (int index = 0; index < anchors.Count; index++)
        {
            double distance = Distance(cursor, anchors[index]);
            if (distance <= radius && distance < nearestDistance)
            {
                nearestIndex = index;
                nearestDistance = distance;
            }
        }
        return nearestIndex;
    }

    private void Publish(
        IReadOnlyList<RumorMatch> currentMatches,
        bool scanning,
        bool panelDetected)
    {
        if (!_overlayVisible && !panelDetected && currentMatches.Count == 0)
            return;

        IslandSession? viewed = _viewIslandIndex >= 0 && _viewIslandIndex < _islands.Count
            ? _islands[_viewIslandIndex]
            : _activeIsland;
        if (viewed is null)
            return;

        bool viewingActive = ReferenceEquals(viewed, _activeIsland);
        var currentIds = viewingActive
            ? currentMatches.Select(match => match.Entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var unsortedItems = new List<RumorDisplayItem>(viewed.Seen.Count + viewed.Pending.Count);
        foreach (string id in viewed.SeenOrder)
        {
            if (viewed.Seen.TryGetValue(id, out var match))
            {
                unsortedItems.Add(new RumorDisplayItem(
                    match,
                    currentIds.Contains(id),
                    IsPending: false,
                    ConfirmationProgress: 0,
                    ConfirmationRequired: 0));
            }
        }

        foreach (var pending in viewed.Pending.Values)
        {
            if (viewed.Seen.ContainsKey(pending.BestMatch.Entry.Id))
                continue;
            unsortedItems.Add(new RumorDisplayItem(
                pending.BestMatch,
                IsCurrent: viewingActive,
                IsPending: true,
                ConfirmationProgress: pending.ObservationCount,
                ConfirmationRequired: pending.RequiredObservations));
        }

        IReadOnlyList<RumorDisplayItem> items = RumorOverlayForm.OrderForDisplay(
            unsortedItems,
            _sortMode,
            _categoryOrder);

        _lastPublishedAt = DateTime.UtcNow;
        RumorOverlayManager.Update(new RumorOverlayState(
            viewed.Anchor,
            items,
            scanning && viewingActive,
            panelDetected && viewingActive,
            viewed.FailedAttempts,
            _pinned,
            _scanCount,
            _lastPublishedAt,
            Diagnostic: viewed.LastDiagnostic,
            IslandIndex: Math.Max(0, _viewIslandIndex) + 1,
            IslandCount: _islands.Count,
            IslandScanCount: viewed.ScanCount,
            SortMode: _sortMode,
            CategoryOrder: _categoryOrder,
            Language: _language,
            UiLanguage: _uiLanguage,
            PanelBounds: viewed.LastPanelBounds));
    }

    private void ClearAllHistory(bool hide)
    {
        _islands.Clear();
        _activeIsland = null;
        _viewIslandIndex = -1;
        _lastActivityAt = DateTime.MinValue;
        _lastPanelSeenAt = DateTime.MinValue;
        _panelMissCount = 0;
        _scanCount = 0;
        if (hide)
        {
            _overlayVisible = false;
            RumorOverlayManager.HideNow();
            RumorLineOverlayManager.HideNow();
        }
    }

    private bool TryGetCachedRecognition(
        ulong fingerprint,
        out CachedRecognition recognition,
        out bool approximate)
    {
        if (_recognitionCache.TryGetValue(fingerprint, out recognition!))
        {
            approximate = false;
            return true;
        }

        foreach (ulong cachedFingerprint in _recognitionCacheOrder.Reverse())
        {
            if (!AreRumorFingerprintsEquivalent(cachedFingerprint, fingerprint) ||
                !_recognitionCache.TryGetValue(cachedFingerprint, out recognition!))
            {
                continue;
            }

            approximate = true;
            return true;
        }

        recognition = null!;
        approximate = false;
        return false;
    }

    internal static bool AreRumorFingerprintsEquivalent(ulong left, ulong right)
    {
        const int bitsPerLine = 21;
        const ulong lineMask = (1UL << bitsPerLine) - 1UL;
        const ulong occupancyBit = 1UL << 20;
        for (int line = 0; line < 3; line++)
        {
            ulong difference = ((left >> (line * bitsPerLine)) ^
                                (right >> (line * bitsPerLine))) & lineMask;
            if ((difference & occupancyBit) != 0)
                return false;

            // Up to two coarse dHash edges may flip because the parchment detector moved by a
            // pixel. Three or more changes within one line are treated as real content change.
            if (System.Numerics.BitOperations.PopCount(difference & ~occupancyBit) > 2)
                return false;
        }

        return true;
    }

    private void RememberRecognition(ulong fingerprint, CachedRecognition recognition)
    {
        if (_recognitionCache.ContainsKey(fingerprint))
            return;

        _recognitionCache[fingerprint] = recognition;
        _recognitionCacheOrder.Enqueue(fingerprint);
        while (_recognitionCacheOrder.Count > MaxRecognitionCacheEntries)
        {
            ulong oldest = _recognitionCacheOrder.Dequeue();
            _recognitionCache.Remove(oldest);
        }
    }

    private Bitmap CapturePanelAroundCursor(
        Point cursor,
        out Rectangle panelBounds,
        out CaptureLayout captureLayout)
    {
        Bitmap primary = CaptureAroundCursor(cursor, PrimaryCapture);
        bool primaryFound = TryFindRumorPanelBounds(primary, out Rectangle primaryBounds);
        bool clipped = primaryFound && NeedsExpandedCapture(cursor, primaryBounds, PrimaryCapture);
        if (primaryFound && !clipped)
        {
            panelBounds = primaryBounds;
            captureLayout = PrimaryCapture;
            return primary;
        }

        // A miss is the normal idle state in automatic mode. Do not double the capture work on
        // every tick: probe the larger area periodically, while a manual burst may use it on every
        // attempt because the user explicitly requested recognition.
        DateTime now = DateTime.UtcNow;
        bool expandedProbeDue = _manualBurstRemaining > 0 ||
                                now - _lastExpandedCaptureAt >= TimeSpan.FromSeconds(2);
        if (!clipped && !expandedProbeDue)
        {
            panelBounds = Rectangle.Empty;
            captureLayout = PrimaryCapture;
            return primary;
        }

        _lastExpandedCaptureAt = now;
        Bitmap expanded = CaptureAroundCursor(cursor, ExpandedCapture);
        if (TryFindRumorPanelBounds(expanded, out Rectangle expandedBounds))
        {
            primary.Dispose();
            panelBounds = expandedBounds;
            captureLayout = ExpandedCapture;
            return expanded;
        }

        expanded.Dispose();
        panelBounds = primaryFound ? primaryBounds : Rectangle.Empty;
        captureLayout = PrimaryCapture;
        return primary;
    }

    private static bool NeedsExpandedCapture(
        Point cursor,
        Rectangle panelBounds,
        CaptureLayout layout)
    {
        const int edgeTolerance = 6;
        Rectangle virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        int desiredLeft = cursor.X - layout.CursorOffsetX;
        int desiredTop = cursor.Y - layout.CursorOffsetY;
        int desiredRight = desiredLeft + layout.Width;
        int desiredBottom = desiredTop + layout.Height;

        return panelBounds.Left <= edgeTolerance && desiredLeft > virtualScreen.Left ||
               panelBounds.Top <= edgeTolerance && desiredTop > virtualScreen.Top ||
               panelBounds.Right >= layout.Width - edgeTolerance && desiredRight < virtualScreen.Right ||
               panelBounds.Bottom >= layout.Height - edgeTolerance && desiredBottom < virtualScreen.Bottom;
    }

    private static Bitmap CaptureAroundCursor(Point cursor, CaptureLayout layout)
    {
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        int desiredLeft = cursor.X - layout.CursorOffsetX;
        int desiredTop = cursor.Y - layout.CursorOffsetY;
        var desired = new Rectangle(desiredLeft, desiredTop, layout.Width, layout.Height);
        var intersection = Rectangle.Intersect(desired, virtualScreen);

        var bitmap = new Bitmap(layout.Width, layout.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        if (intersection.Width > 0 && intersection.Height > 0)
        {
            var destination = new Point(intersection.Left - desired.Left, intersection.Top - desired.Top);
            graphics.CopyFromScreen(
                intersection.Location,
                destination,
                intersection.Size,
                CopyPixelOperation.SourceCopy);
        }
        return bitmap;
    }

    internal static Rectangle ToScreenPanelBounds(Point cursor, Rectangle localPanelBounds) =>
        ToScreenPanelBounds(cursor, localPanelBounds, PrimaryCapture);

    private static Rectangle ToScreenPanelBounds(
        Point cursor,
        Rectangle localPanelBounds,
        CaptureLayout layout) =>
        new(
            cursor.X - layout.CursorOffsetX + localPanelBounds.X,
            cursor.Y - layout.CursorOffsetY + localPanelBounds.Y,
            localPanelBounds.Width,
            localPanelBounds.Height);

    internal static bool PanelInteriorContainsCursor(Rectangle panelBounds)
        => PanelInteriorContainsCursor(panelBounds, PrimaryCapture);

    private static bool PanelInteriorContainsCursor(Rectangle panelBounds, CaptureLayout layout)
    {
        var interior = panelBounds;
        interior.Inflate(-12, -12);
        return interior.Width > 0 && interior.Height > 0 &&
               interior.Contains(layout.CursorOffsetX, layout.CursorOffsetY);
    }

    internal static bool LooksLikeRumorPanelImage(Bitmap bitmap) =>
        TryFindRumorPanelBounds(bitmap, out _);

    internal static bool TryFindRumorPanelBounds(Bitmap bitmap, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (bitmap.Width < 32 || bitmap.Height < 32)
            return false;

        const int sampleStep = 4;
        int gridWidth = (bitmap.Width + sampleStep - 1) / sampleStep;
        int gridHeight = (bitmap.Height + sampleStep - 1) / sampleStep;
        var parchment = new bool[gridWidth * gridHeight];
        var visited = new bool[parchment.Length];
        var queue = new int[parchment.Length];

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

            for (int gridY = 0; gridY < gridHeight; gridY++)
            {
                int y = Math.Min(bitmap.Height - 1, gridY * sampleStep);
                int row = data.Stride >= 0 ? y * stride : (bitmap.Height - 1 - y) * stride;
                for (int gridX = 0; gridX < gridWidth; gridX++)
                {
                    int x = Math.Min(bitmap.Width - 1, gridX * sampleStep);
                    int index = row + x * 3;
                    int blue = buffer[index];
                    int green = buffer[index + 1];
                    int red = buffer[index + 2];
                    parchment[gridY * gridWidth + gridX] = IsParchmentPixel(red, green, blue);
                }
            }
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            bitmap.UnlockBits(data);
        }

        int bestCount = 0;
        int bestMinX = 0;
        int bestMaxX = 0;
        int bestMinY = 0;
        int bestMaxY = 0;
        ReadOnlySpan<int> neighborX = [-1, 0, 1, -1, 1, -1, 0, 1];
        ReadOnlySpan<int> neighborY = [-1, -1, -1, 0, 0, 1, 1, 1];

        for (int start = 0; start < parchment.Length; start++)
        {
            if (!parchment[start] || visited[start])
                continue;

            int head = 0;
            int tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            int count = 0;
            int minX = gridWidth;
            int maxX = 0;
            int minY = gridHeight;
            int maxY = 0;

            while (head < tail)
            {
                int current = queue[head++];
                int x = current % gridWidth;
                int y = current / gridWidth;
                count++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);

                for (int neighbor = 0; neighbor < neighborX.Length; neighbor++)
                {
                    int nextX = x + neighborX[neighbor];
                    int nextY = y + neighborY[neighbor];
                    if (nextX < 0 || nextX >= gridWidth || nextY < 0 || nextY >= gridHeight)
                        continue;
                    int next = nextY * gridWidth + nextX;
                    if (!parchment[next] || visited[next])
                        continue;
                    visited[next] = true;
                    queue[tail++] = next;
                }
            }

            if (count > bestCount)
            {
                bestCount = count;
                bestMinX = minX;
                bestMaxX = maxX;
                bestMinY = minY;
                bestMaxY = maxY;
            }
        }

        int componentWidth = (bestMaxX - bestMinX + 1) * sampleStep;
        int componentHeight = (bestMaxY - bestMinY + 1) * sampleStep;
        int minimumSamples = Math.Max(300, parchment.Length / 100);
        if (bestCount < minimumSamples || componentWidth < 180 || componentHeight < 100)
            return false;

        // Reject a nearly full-frame parchment/background. A rumor tooltip can be large, but after
        // capturing 960x900 around the cursor it should still have a visible margin around it.
        double componentAreaRatio =
            (double)componentWidth * componentHeight / (bitmap.Width * bitmap.Height);
        if (componentAreaRatio > 0.76d)
            return false;

        const int padding = 20;
        int left = Math.Max(0, bestMinX * sampleStep - padding);
        int top = Math.Max(0, bestMinY * sampleStep - padding);
        int right = Math.Min(bitmap.Width, (bestMaxX + 1) * sampleStep + padding);
        int bottom = Math.Min(bitmap.Height, (bestMaxY + 1) * sampleStep + padding);
        bounds = Rectangle.FromLTRB(left, top, right, bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0 || !IsPlausibleRumorPanelGeometry(bounds))
        {
            bounds = Rectangle.Empty;
            return false;
        }
        return true;
    }

    internal static bool IsPlausibleRumorPanelGeometry(Rectangle bounds)
    {
        if (bounds.Width < 340 || bounds.Height < 170)
            return false;

        double aspect = (double)bounds.Width / bounds.Height;
        // Real rumor cards are landscape-oriented. The reward book is near-square or portrait and
        // used to be misclassified as a huge parchment tooltip by the hover scanner.
        return aspect is >= 1.12d and <= 2.80d;
    }

    private static bool IsParchmentPixel(int red, int green, int blue) =>
        red >= 105 && green >= 88 && blue >= 58 &&
        red >= blue + 18 && green >= blue + 7 &&
        red + green + blue >= 285;

    internal static ulong ComputeRumorContentFingerprint(Bitmap bitmap)
    {
        // A 63-bit difference hash over the three normalized rumor bands. Each line is split into
        // 8×3 coarse cells and adjacent horizontal darkness values are compared (7×3 bits per line).
        // Coarse cell averages ignore one-pixel detector jitter and parchment brightness changes, while
        // changing a handwritten line alters its horizontal shape and therefore the fingerprint.
        IReadOnlyList<Rectangle> slots = GetRumorLineSlotBounds(bitmap.Size);
        if (slots.Count != 3)
            return ScreenCapture.ComputeFingerprint(bitmap);

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

            ulong hash = 0UL;
            int bit = 0;
            foreach (Rectangle slot in slots)
            {
                int veryDarkSamples = 0;
                int slotSamples = 0;
                for (int verticalCell = 0; verticalCell < 3; verticalCell++)
                {
                    var darkness = new double[8];
                    int cellTop = slot.Top + verticalCell * slot.Height / 3;
                    int cellBottom = slot.Top + (verticalCell + 1) * slot.Height / 3;
                    for (int horizontalCell = 0; horizontalCell < 8; horizontalCell++)
                    {
                        int cellLeft = slot.Left + horizontalCell * slot.Width / 8;
                        int cellRight = slot.Left + (horizontalCell + 1) * slot.Width / 8;
                        int stepX = Math.Max(1, (cellRight - cellLeft) / 18);
                        int stepY = Math.Max(1, (cellBottom - cellTop) / 10);
                        long total = 0;
                        int samples = 0;
                        for (int y = cellTop; y < cellBottom; y += stepY)
                        {
                            int rowOffset = data.Stride >= 0
                                ? y * stride
                                : (bitmap.Height - 1 - y) * stride;
                            for (int x = cellLeft; x < cellRight; x += stepX)
                            {
                                int index = rowOffset + x * 3;
                                int luminance =
                                    (77 * buffer[index + 2] +
                                     150 * buffer[index + 1] +
                                     29 * buffer[index]) >> 8;
                                // Ignore most parchment texture; nearly black handwritten ink carries
                                // the stable content signal.
                                total += Math.Max(0, 150 - luminance);
                                if (luminance < 60)
                                    veryDarkSamples++;
                                samples++;
                                slotSamples++;
                            }
                        }

                        double average = samples > 0 ? (double)total / samples : 0d;
                        darkness[horizontalCell] = Math.Round(average / 2d) * 2d;
                    }

                    int comparisons = verticalCell == 2 ? 6 : 7;
                    for (int horizontalCell = 0; horizontalCell < comparisons; horizontalCell++)
                    {
                        if (darkness[horizontalCell] > darkness[horizontalCell + 1] + 1d)
                            hash |= 1UL << bit;
                        bit++;
                    }
                }

                // The 21st bit of each line records whether cursive ink exists at all. It prevents a
                // newly inserted third rumor from being treated as jitter-equivalent to a blank slot.
                if (slotSamples > 0 && veryDarkSamples >= slotSamples * 0.015d)
                    hash |= 1UL << bit;
                bit++;
            }

            return hash;
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            bitmap.UnlockBits(data);
        }
    }

    private CachedRecognition BuildRecognition(OcrPanelResult ocrResult)
    {
        var evidence = new Dictionary<string, MatchEvidence>(StringComparer.OrdinalIgnoreCase);
        foreach (var pass in ocrResult.Passes)
        {
            foreach (var match in _catalog.MatchText(pass.Text))
            {
                if (!evidence.TryGetValue(match.Entry.Id, out var item))
                {
                    item = new MatchEvidence(match);
                    evidence[match.Entry.Id] = item;
                }
                item.Observe(
                    match,
                    exactSlot: pass.Name.StartsWith("slot-", StringComparison.Ordinal) && match.Exact);
            }
        }

        foreach (var match in _catalog.MatchText(ocrResult.CombinedText))
        {
            if (!evidence.TryGetValue(match.Entry.Id, out var item))
            {
                item = new MatchEvidence(match);
                evidence[match.Entry.Id] = item;
                item.Observe(match, exactSlot: false);
            }
            else
            {
                item.KeepBest(match);
            }
        }

        IReadOnlyList<RumorMatch> matches = evidence.Values
            .Select(item => item.BestMatch)
            .OrderByDescending(match => match.Exact)
            .ThenByDescending(match => match.Score)
            .ToList();
        bool trustedSlotMatch = evidence.Values.Any(item => item.ExactSlot);
        string diagnostic = BuildDiagnosticText(ocrResult.Passes, matches);
        return new CachedRecognition(
            matches,
            RumorCatalog.LooksLikeRumorPanel(ocrResult.HeaderText),
            trustedSlotMatch,
            diagnostic,
            evidence.ToDictionary(pair => pair.Key, pair => pair.Value.ToSnapshot(), StringComparer.OrdinalIgnoreCase),
            ocrResult.LineStatuses.ToArray());
    }

    private OcrPanelResult RecognizePanel(Bitmap bitmap, Rectangle screenPanelBounds)
    {
        // Use the same high-level strategy as the price scanner: derive stable row regions first,
        // crop each row, normalize it, then OCR each row independently. The full-panel pass is only
        // a fallback for rows that could not be resolved, which reduces noise from the Russian
        // instruction header and decorative rune icons.
        IReadOnlyList<Rectangle> slots = DetectRumorLineSlotBounds(bitmap);
        var passes = new List<OcrPassResult>(10);
        var reliableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var statuses = Enumerable.Repeat(RumorLineRecognitionStatus.Waiting, slots.Count).ToArray();
        var inkScores = new double[slots.Count];
        PublishLineIndicators(screenPanelBounds, slots, statuses);

        for (int index = 0; index < slots.Count; index++)
        {
            Rectangle slotBounds = slots[index];
            statuses[index] = RumorLineRecognitionStatus.Scanning;
            PublishLineIndicators(screenPanelBounds, slots, statuses);

            using Bitmap slot = CropBitmap(bitmap, slotBounds);
            inkScores[index] = RumorTextInkScore(slot);
            if (!HasCenteredRumorTextScore(inkScores[index]))
            {
                statuses[index] = RumorLineRecognitionStatus.Empty;
                PublishLineIndicators(screenPanelBounds, slots, statuses);
                continue;
            }

            using Bitmap ink = CropRumorLineToInk(slot);

            OcrPassResult gray = RecognizeRumorSlotVariant(
                _engine,
                index + 1,
                slotBounds,
                ink,
                RumorLineVariant.Grayscale,
                PageSegMode.SingleLine,
                "gray");
            passes.Add(gray);
            HashSet<string> lineIds = ReliableMatchIds(gray.Text);
            reliableIds.UnionWith(lineIds);

            if (lineIds.Count == 0)
            {
                OcrPassResult soft = RecognizeRumorSlotVariant(
                    _engine,
                    index + 1,
                    slotBounds,
                    ink,
                    RumorLineVariant.BinarySoft,
                    PageSegMode.SingleLine,
                    "bin-soft");
                passes.Add(soft);
                lineIds.UnionWith(ReliableMatchIds(soft.Text));
                reliableIds.UnionWith(lineIds);
            }

            if (lineIds.Count == 0)
            {
                OcrPassResult strong = RecognizeRumorSlotVariant(
                    _engine,
                    index + 1,
                    slotBounds,
                    ink,
                    RumorLineVariant.BinaryStrong,
                    PageSegMode.SingleLine,
                    "bin-strong");
                passes.Add(strong);
                lineIds.UnionWith(ReliableMatchIds(strong.Text));
                reliableIds.UnionWith(lineIds);
            }

            statuses[index] = lineIds.Count > 0
                ? RumorLineRecognitionStatus.Matched
                : RumorLineRecognitionStatus.Unmatched;
            PublishLineIndicators(screenPanelBounds, slots, statuses);
        }

        int occupiedSlotCount = statuses.Count(status => status != RumorLineRecognitionStatus.Empty);
        string fullText = string.Empty;
        if (reliableIds.Count < occupiedSlotCount)
        {
            var full = RecognizeBitmap(_engine, bitmap, PageSegMode.SparseText);
            fullText = full.Text;
            passes.Add(new OcrPassResult(
                "full-fallback",
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                full.Text,
                full.Confidence));
            reliableIds.UnionWith(ReliableMatchIds(full.Text));
            ApplyFullPanelMatchStatuses(statuses, inkScores, reliableIds.Count);
            PublishLineIndicators(screenPanelBounds, slots, statuses);
        }

        string headerText = string.IsNullOrWhiteSpace(fullText)
            ? string.Join(Environment.NewLine, passes.Select(pass => pass.Text))
            : fullText;
        return BuildOcrPanelResult(headerText, passes, statuses);
    }

    private static void PublishLineIndicators(
        Rectangle screenPanelBounds,
        IReadOnlyList<Rectangle> localSlots,
        IReadOnlyList<RumorLineRecognitionStatus> statuses)
    {
        if (screenPanelBounds.IsEmpty || localSlots.Count == 0)
            return;

        var indicators = new List<RumorLineIndicator>(localSlots.Count);
        for (int index = 0; index < localSlots.Count; index++)
        {
            Rectangle local = localSlots[index];
            var screen = new Rectangle(
                screenPanelBounds.Left + local.Left,
                screenPanelBounds.Top + local.Top,
                local.Width,
                local.Height);
            RumorLineRecognitionStatus status = index < statuses.Count
                ? statuses[index]
                : RumorLineRecognitionStatus.Waiting;
            indicators.Add(new RumorLineIndicator(
                screen,
                status,
                $"slot {index + 1}: {local.X},{local.Y} {local.Width}×{local.Height} {status}"));
        }

        RumorLineOverlayManager.Update(new RumorLineOverlayState(
            screenPanelBounds,
            indicators,
            Visible: true));
    }

    internal static bool AreAllVisibleRumorLinesResolved(
        IReadOnlyList<RumorLineRecognitionStatus> statuses) =>
        statuses.Any(status => status == RumorLineRecognitionStatus.Matched) &&
        statuses.All(status =>
            status is RumorLineRecognitionStatus.Matched or RumorLineRecognitionStatus.Empty);

    internal static void ApplyFullPanelMatchStatuses(
        RumorLineRecognitionStatus[] statuses,
        IReadOnlyList<double> inkScores,
        int reliableMatchCount)
    {
        int target = Math.Clamp(reliableMatchCount, 0, statuses.Length);
        if (target == 0)
            return;

        // The whole-panel fallback knows how many catalog entries were read but not their exact rows.
        // Preserve direct row matches first, then assign any remaining matches to the strongest ink
        // regions. This fixes two-rumor panels where one blank parchment band was previously shown as
        // a permanent question mark even though the final overlay had already identified both rumors.
        int directMatches = statuses.Count(status => status == RumorLineRecognitionStatus.Matched);
        int remaining = Math.Max(0, target - directMatches);
        foreach (int index in Enumerable.Range(0, statuses.Length)
                     .Where(index => statuses[index] != RumorLineRecognitionStatus.Matched)
                     .OrderByDescending(index => inkScores[index])
                     .Take(remaining))
        {
            statuses[index] = RumorLineRecognitionStatus.Matched;
        }

        double weakestMatched = Enumerable.Range(0, statuses.Length)
            .Where(index => statuses[index] == RumorLineRecognitionStatus.Matched)
            .Select(index => inkScores[index])
            .DefaultIfEmpty(0d)
            .Min();
        for (int index = 0; index < statuses.Length; index++)
        {
            if (statuses[index] != RumorLineRecognitionStatus.Unmatched)
                continue;

            // A truly blank slot has far less very-dark ink than the cursive rumor rows. Mark it as
            // empty when it is clearly separated from the matched rows; otherwise keep '?' because a
            // third rumor may genuinely be present but unresolved.
            if (!HasCenteredRumorTextScore(inkScores[index]) ||
                weakestMatched > 0d && inkScores[index] < weakestMatched * 0.42d)
            {
                statuses[index] = RumorLineRecognitionStatus.Empty;
            }
        }
    }

    internal static bool HasCenteredRumorText(Bitmap source) =>
        HasCenteredRumorTextScore(RumorTextInkScore(source));

    internal static bool HasCenteredRumorTextScore(double score) => score >= 0.015d;

    internal static double RumorTextInkScore(Bitmap source)
    {
        if (source.Width < 20 || source.Height < 12)
            return 0d;

        BitmapData data = source.LockBits(
            new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        byte[]? buffer = null;
        try
        {
            int stride = Math.Abs(data.Stride);
            int length = stride * source.Height;
            buffer = ArrayPool<byte>.Shared.Rent(length);
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, length);

            int left = Math.Clamp((int)Math.Round(source.Width * 0.08d), 0, source.Width - 1);
            int right = Math.Clamp((int)Math.Round(source.Width * 0.97d), left + 1, source.Width);
            int top = Math.Clamp((int)Math.Round(source.Height * 0.22d), 0, source.Height - 1);
            int bottom = Math.Clamp((int)Math.Round(source.Height * 0.78d), top + 1, source.Height);
            int stepX = Math.Max(1, (right - left) / 220);
            int stepY = Math.Max(1, (bottom - top) / 48);
            int dark = 0;
            int samples = 0;
            for (int y = top; y < bottom; y += stepY)
            {
                int rowOffset = data.Stride >= 0
                    ? y * stride
                    : (source.Height - 1 - y) * stride;
                for (int x = left; x < right; x += stepX)
                {
                    int index = rowOffset + x * 3;
                    int luminance =
                        (77 * buffer[index + 2] + 150 * buffer[index + 1] + 29 * buffer[index]) >> 8;
                    // The handwritten rumor glyphs are nearly black. A threshold of 105 also
                    // counted parchment grain and made empty rows look occupied; 60 cleanly separates
                    // the supplied 3-row captures from blank parchment while remaining scale-agnostic.
                    if (luminance < 60)
                        dark++;
                    samples++;
                }
            }

            return samples > 0 ? (double)dark / samples : 0d;
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            source.UnlockBits(data);
        }
    }

    private static Bitmap CropRumorLineToInk(Bitmap source)
    {
        BitmapData data = source.LockBits(
            new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        byte[]? buffer = null;
        int minX = source.Width;
        int maxX = -1;
        try
        {
            int stride = Math.Abs(data.Stride);
            int length = stride * source.Height;
            buffer = ArrayPool<byte>.Shared.Rent(length);
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, length);
            int yStart = Math.Clamp(source.Height / 8, 0, source.Height - 1);
            int yEnd = Math.Clamp(source.Height - source.Height / 8, yStart + 1, source.Height);
            int requiredInk = Math.Max(2, (yEnd - yStart) / 18);

            for (int x = 0; x < source.Width; x++)
            {
                int ink = 0;
                for (int y = yStart; y < yEnd; y++)
                {
                    int row = data.Stride >= 0
                        ? y * stride
                        : (source.Height - 1 - y) * stride;
                    int index = row + x * 3;
                    int luminance =
                        (77 * buffer[index + 2] + 150 * buffer[index + 1] + 29 * buffer[index]) >> 8;
                    if (luminance < 125 && ++ink >= requiredInk)
                        break;
                }
                if (ink < requiredInk)
                    continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            source.UnlockBits(data);
        }

        if (maxX < minX || maxX - minX < 20)
            return CropBitmap(source, new Rectangle(0, 0, source.Width, source.Height));

        const int padding = 14;
        int left = Math.Max(0, minX - padding);
        int right = Math.Min(source.Width, maxX + padding + 1);
        return CropBitmap(source, new Rectangle(left, 0, right - left, source.Height));
    }

    private static OcrPanelResult BuildOcrPanelResult(
        string headerText,
        IReadOnlyList<OcrPassResult> passes,
        IReadOnlyList<RumorLineRecognitionStatus> lineStatuses)
    {
        string combined = string.Join(
            Environment.NewLine,
            passes
                .Select(pass => pass.Text?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        return new OcrPanelResult(headerText, combined, passes, lineStatuses.ToArray());
    }

    private HashSet<string> ReliableMatchIds(string? text) =>
        _catalog.MatchText(text)
            .Where(match => match.Exact || match.Score >= 0.88d)
            .Select(match => match.Entry.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private bool HasReliableSlotMatch(string? text)
    {
        IReadOnlyList<RumorMatch> matches = _catalog.MatchText(text);
        return matches.Any(match => match.Exact || match.Score >= 0.88d);
    }

    private OcrPassResult RecognizeRumorSlotVariant(
        TesseractEngine engine,
        int slotIndex,
        Rectangle slotBounds,
        Bitmap slot,
        RumorLineVariant variant,
        PageSegMode pageSegMode,
        string suffix)
    {
        using var prepared = PrepareRumorLine(slot, variant);
        var line = RecognizeBitmap(engine, prepared, pageSegMode);
        return new OcrPassResult(
            $"slot-{slotIndex}-{suffix}",
            slotBounds,
            line.Text,
            line.Confidence);
    }

    private static (string Text, float Confidence) RecognizeBitmap(
        TesseractEngine engine,
        Bitmap bitmap,
        PageSegMode pageSegMode)
    {
        using var pix = Pix.LoadFromMemory(ToPng(bitmap));
        using var page = engine.Process(pix, pageSegMode);
        return (page.GetText() ?? string.Empty, page.GetMeanConfidence());
    }

    internal static IReadOnlyList<Rectangle> DetectRumorLineSlotBounds(Bitmap panel)
    {
        IReadOnlyList<Rectangle> fallback = GetRumorLineSlotBounds(panel.Size);
        if (fallback.Count != 3 || panel.Width < 120 || panel.Height < 100)
            return fallback;

        int left = Math.Clamp((int)Math.Round(panel.Width * 0.20d), 0, panel.Width - 1);
        int right = Math.Clamp((int)Math.Round(panel.Width * 0.97d), left + 1, panel.Width);
        int firstY = Math.Clamp((int)Math.Round(panel.Height * 0.17d), 0, panel.Height - 1);
        int lastY = Math.Clamp((int)Math.Round(panel.Height * 0.94d), firstY + 1, panel.Height);
        var active = new bool[panel.Height];

        BitmapData data = panel.LockBits(
            new Rectangle(0, 0, panel.Width, panel.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        byte[]? buffer = null;
        try
        {
            int stride = Math.Abs(data.Stride);
            int length = stride * panel.Height;
            buffer = ArrayPool<byte>.Shared.Rent(length);
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, length);
            int stepX = Math.Max(1, (right - left) / 220);
            int sampledWidth = Math.Max(1, (right - left + stepX - 1) / stepX);
            int requiredInk = Math.Max(5, (int)Math.Round(sampledWidth * 0.018d));

            for (int y = firstY; y < lastY; y++)
            {
                int rowOffset = data.Stride >= 0
                    ? y * stride
                    : (panel.Height - 1 - y) * stride;
                int ink = 0;
                for (int x = left; x < right; x += stepX)
                {
                    int index = rowOffset + x * 3;
                    int blue = buffer[index];
                    int green = buffer[index + 1];
                    int red = buffer[index + 2];
                    int luminance = (77 * red + 150 * green + 29 * blue) >> 8;
                    if (luminance < 128 && ++ink >= requiredInk)
                        break;
                }
                active[y] = ink >= requiredInk;
            }
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            panel.UnlockBits(data);
        }

        var bands = new List<(int Top, int Bottom)>();
        int runStart = -1;
        int lastActive = -1;
        for (int y = firstY; y <= lastY; y++)
        {
            bool isActive = y < lastY && active[y];
            if (isActive)
            {
                if (runStart < 0)
                    runStart = y;
                lastActive = y;
                continue;
            }

            if (runStart >= 0 && y - lastActive <= 3)
                continue;
            if (runStart >= 0)
            {
                int height = lastActive - runStart + 1;
                if (height is >= 2 and <= 34)
                    bands.Add((runStart, lastActive + 1));
                runStart = -1;
                lastActive = -1;
            }
        }

        if (bands.Count == 0)
            return fallback;

        var result = new List<Rectangle>(3);
        var used = new HashSet<int>();
        for (int slotIndex = 0; slotIndex < fallback.Count; slotIndex++)
        {
            Rectangle expected = fallback[slotIndex];
            int expectedCenter = expected.Top + expected.Height / 2;
            int bestIndex = -1;
            int bestDistance = int.MaxValue;
            for (int bandIndex = 0; bandIndex < bands.Count; bandIndex++)
            {
                if (used.Contains(bandIndex))
                    continue;
                int center = bands[bandIndex].Top + (bands[bandIndex].Bottom - bands[bandIndex].Top) / 2;
                int distance = Math.Abs(center - expectedCenter);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = bandIndex;
                }
            }

            int tolerance = Math.Max(24, (int)Math.Round(panel.Height * 0.10d));
            if (bestIndex < 0 || bestDistance > tolerance)
            {
                result.Add(expected);
                continue;
            }

            used.Add(bestIndex);
            var band = bands[bestIndex];
            int desiredHeight = Math.Clamp(
                Math.Max(expected.Height, band.Bottom - band.Top + 18),
                36,
                Math.Max(36, panel.Height / 4));
            int centerY = band.Top + (band.Bottom - band.Top) / 2;
            int top = Math.Clamp(centerY - desiredHeight / 2, 0, panel.Height - desiredHeight);
            result.Add(new Rectangle(expected.Left, top, expected.Width, desiredHeight));
        }

        return result;
    }

    internal static IReadOnlyList<Rectangle> GetRumorLineSlotBounds(Size panelSize)
    {
        if (panelSize.Width < 80 || panelSize.Height < 80)
            return [];

        // Exclude the icon/rune column and keep each crop close to one cursive baseline. The old
        // 8%-92% / 21%-height crop admitted too much decoration and made the per-slot fallback less
        // useful than the full-panel pass.
        int left = Math.Clamp((int)Math.Round(panelSize.Width * 0.08d), 0, panelSize.Width - 1);
        int right = Math.Clamp((int)Math.Round(panelSize.Width * 0.96d), left + 1, panelSize.Width);
        int slotHeight = Math.Clamp(
            (int)Math.Round(panelSize.Height * 0.18d),
            40,
            Math.Max(40, panelSize.Height / 4));

        // The candidate detector can return a compact crop with most of the heading removed, a
        // normal crop, or a tall crop that still contains the instruction block. Use a profile for
        // each shape instead of forcing one ratio onto all three cases.
        double[] centers = panelSize.Height switch
        {
            <= 205 => [0.23d, 0.46d, 0.69d],
            >= 280 => [0.48d, 0.66d, 0.84d],
            _ => [0.34d, 0.54d, 0.73d],
        };

        var result = new List<Rectangle>(centers.Length);
        foreach (double centerRatio in centers)
        {
            int centerY = (int)Math.Round(panelSize.Height * centerRatio);
            int top = Math.Clamp(centerY - slotHeight / 2, 0, Math.Max(0, panelSize.Height - slotHeight));
            int height = Math.Min(slotHeight, panelSize.Height - top);
            result.Add(new Rectangle(left, top, right - left, height));
        }
        return result;
    }

    private enum RumorLineVariant
    {
        Grayscale,
        BinarySoft,
        BinaryStrong,
    }

    private static Bitmap PrepareRumorLine(Bitmap source, RumorLineVariant variant)
    {
        const int scale = 3;
        var result = new Bitmap(
            Math.Max(1, source.Width * scale),
            Math.Max(1, source.Height * scale),
            PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.White);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;

        using var attributes = new ImageAttributes();
        var grayscale = new ColorMatrix(new[]
        {
            new[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
            new[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
            new[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { 0f, 0f, 0f, 0f, 1f },
        });
        attributes.SetColorMatrix(grayscale);
        switch (variant)
        {
            case RumorLineVariant.Grayscale:
                attributes.SetGamma(1.18f);
                break;
            case RumorLineVariant.BinaryStrong:
                attributes.SetGamma(1.26f);
                attributes.SetThreshold(0.68f);
                break;
            default:
                attributes.SetGamma(1.14f);
                attributes.SetThreshold(0.60f);
                break;
        }

        graphics.DrawImage(
            source,
            new Rectangle(0, 0, result.Width, result.Height),
            0,
            0,
            source.Width,
            source.Height,
            GraphicsUnit.Pixel,
            attributes);
        return result;
    }

    private string BuildDiagnosticText(
        IReadOnlyList<OcrPassResult> passes,
        IReadOnlyList<RumorMatch> matches)
    {
        var lines = new List<string>(passes.Count + 1);
        foreach (var pass in passes)
        {
            string location = pass.Name == "full"
                ? Ui("всё окно", "full panel")
                : $"y={pass.Bounds.Top}-{pass.Bounds.Bottom}";
            string text = CompactText(pass.Text);
            if (text.Length == 0)
                text = Ui("<пусто>", "<empty>");
            lines.Add($"{pass.Name} {location} {pass.Confidence:P0}: {text}");
        }

        string matchText = matches.Count == 0
            ? Ui("совпадения: нет", "matches: none")
            : Ui("совпадения: ", "matches: ") + string.Join(
                ", ",
                matches.Select(match =>
                    $"{match.Entry.Id} {(match.Exact ? "exact" : $"{match.Score:F2}")}"));
        lines.Add(matchText);
        return string.Join(Environment.NewLine, lines);
    }

    private void LogRecognition(
        ulong fingerprint,
        Rectangle panelBounds,
        Point cursor,
        CachedRecognition recognition,
        IReadOnlyList<OcrPassResult> passes)
    {
        Log(
            $"OCR fingerprint={fingerprint:X16} cursor={cursor.X},{cursor.Y} " +
            $"panel={panelBounds.X},{panelBounds.Y},{panelBounds.Width}x{panelBounds.Height} " +
            $"header={recognition.HeaderDetected} trustedSlot={recognition.TrustedSlotMatch} " +
            $"matches={recognition.Matches.Count}");
        foreach (var pass in passes)
        {
            Log(
                $"OCR-PASS name={pass.Name} bounds={pass.Bounds.X},{pass.Bounds.Y}," +
                $"{pass.Bounds.Width}x{pass.Bounds.Height} conf={pass.Confidence:P0} " +
                $"text='{CompactText(pass.Text)}'");
        }
        Log($"OCR-MATCH {BuildMatchLog(recognition.Matches)}");
    }

    private static string BuildMatchLog(IReadOnlyList<RumorMatch> matches) =>
        matches.Count == 0
            ? "none"
            : string.Join(
                " | ",
                matches.Select(match =>
                    $"id={match.Entry.Id} raw='{CompactText(match.RawText)}' " +
                    $"kind={(match.Exact ? "exact" : "fuzzy")} score={match.Score:F3}"));

    private static Bitmap CropBitmap(Bitmap source, Rectangle rectangle)
    {
        var result = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, result.Width, result.Height),
            rectangle,
            GraphicsUnit.Pixel);
        return result;
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private static double Distance(Point left, Point right)
    {
        long dx = left.X - right.X;
        long dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void OpenLog()
    {
        try { _logWriter = new RollingLogWriter("rumors.log", _config); }
        catch { _logWriter = null; }
    }

    private void CloseLog()
    {
        try { _logWriter?.Dispose(); } catch { }
        _logWriter = null;
    }

    private void LogError(string message) => _logWriter?.Write(AppLogLevel.Error, message);
    private void LogWarning(string message) => _logWriter?.Write(AppLogLevel.Warning, message);
    private void LogInformation(string message) => _logWriter?.Write(AppLogLevel.Information, message);
    private void LogDebug(string message) => _logWriter?.Write(AppLogLevel.Debug, message);
    private void Trace(string message) => _logWriter?.Write(AppLogLevel.Trace, message);

    // OCR passes are intentionally Debug-level: useful for support, but too verbose for normal use.
    private void Log(string message) => LogDebug(message);

    private void ReportStatus(string value)
    {
        if (!string.Equals(value, _lastLoggedStatus, StringComparison.Ordinal))
        {
            _lastLoggedStatus = value;
            LogInformation($"STATUS {value}");
        }
        try { StatusChanged?.Invoke(value); } catch { }
    }

    private void ReloadCatalogIfChanged()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastCatalogCheckAt < TimeSpan.FromMilliseconds(750))
            return;
        _lastCatalogCheckAt = now;

        DateTime currentWriteTime = _catalog.GetCurrentWriteTimeUtc();
        if (currentWriteTime == DateTime.MinValue || currentWriteTime == _catalogObservedWriteTimeUtc)
            return;

        _catalogObservedWriteTimeUtc = currentWriteTime;
        try
        {
            var reloaded = RumorCatalog.Load(_catalog.DefaultPath, _catalog.UserPath);
            _catalog = reloaded;
            _catalogObservedWriteTimeUtc = reloaded.SourceLastWriteTimeUtc;
            RefreshStoredMatches(reloaded);
            _recognitionCache.Clear();
            _recognitionCacheOrder.Clear();
            LogInformation($"catalog RELOADED entries={reloaded.Entries.Count}; visible history refreshed");
            ReportStatus(Ui(
                $"Описания слухов обновлены · записей: {reloaded.Entries.Count}",
                $"Rumor descriptions updated · entries: {reloaded.Entries.Count}"));
            if (_overlayVisible)
                Publish([], scanning: false, panelDetected: false);
        }
        catch (Exception exception)
        {
            LogWarning($"catalog RELOAD ERROR {exception.GetType().Name}: {exception.Message}");
            ReportStatus(Ui(
                "Ошибка в JSON описаний — используется последняя корректная версия",
                "Description JSON is invalid — the last valid version is still in use"));
        }
    }

    private void RefreshStoredMatches(RumorCatalog catalog)
    {
        var entriesById = catalog.Entries
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var island in _islands)
        {
            foreach (string id in island.Seen.Keys.ToArray())
            {
                if (entriesById.TryGetValue(id, out var updatedEntry))
                {
                    island.Seen[id] = island.Seen[id] with { Entry = updatedEntry };
                    continue;
                }

                island.Seen.Remove(id);
                island.SeenOrder.RemoveAll(existing =>
                    string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));
                LogInformation($"catalog entry removed during reload; removed visible history id={id}");
            }

            foreach (string id in island.Pending.Keys.ToArray())
            {
                if (entriesById.TryGetValue(id, out var updatedEntry))
                {
                    island.Pending[id].RefreshEntry(updatedEntry);
                    continue;
                }
                island.Pending.Remove(id);
                LogDebug($"catalog entry removed during reload; removed pending id={id}");
            }
        }
    }

    private string Ui(string russian, string english) =>
        _uiLanguage == "en" ? english : russian;

    private static string CompactText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string compact = string.Join(" | ", value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));
        return compact.Length <= 260 ? compact : compact[..260] + "…";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cancellation.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _engine.Dispose();
        _cancellation.Dispose();
        CloseLog();
        RumorOverlayManager.Close();
        RumorLineOverlayManager.Close();
    }

    private sealed class IslandSession(Point anchor)
    {
        public Point Anchor { get; set; } = anchor;
        public Dictionary<string, RumorMatch> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RumorConfirmationState> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SeenOrder { get; } = [];
        public DateTime LastMatchAt { get; set; } = DateTime.MinValue;
        public DateTime LastPanelAt { get; set; } = DateTime.MinValue;
        public int FailedAttempts { get; set; }
        public int ScanCount { get; set; }
        public string LastDiagnostic { get; set; } = string.Empty;
        public Rectangle LastPanelBounds { get; set; } = Rectangle.Empty;
    }

    private sealed class MatchEvidence
    {
        public MatchEvidence(RumorMatch initial) => BestMatch = initial;

        public RumorMatch BestMatch { get; private set; }
        public int PassCount { get; private set; }
        public int ExactPassCount { get; private set; }
        public bool ExactSlot { get; private set; }

        public void Observe(RumorMatch match, bool exactSlot)
        {
            PassCount++;
            if (match.Exact)
                ExactPassCount++;
            ExactSlot |= exactSlot;
            KeepBest(match);
        }

        public void KeepBest(RumorMatch match)
        {
            if ((match.Exact && !BestMatch.Exact) ||
                (match.Exact == BestMatch.Exact && match.Score > BestMatch.Score))
            {
                BestMatch = match;
            }
        }

        public MatchEvidenceSnapshot ToSnapshot() =>
            new(PassCount, ExactPassCount, ExactSlot);
    }

    private sealed record MatchEvidenceSnapshot(
        int PassCount,
        int ExactPassCount,
        bool ExactSlot);

    private sealed record ObservationResult(
        IReadOnlyList<RumorMatch> CurrentConfirmed,
        int ConfirmedNow);

    private sealed record OcrPassResult(
        string Name,
        Rectangle Bounds,
        string Text,
        float Confidence);

    private sealed record OcrPanelResult(
        string HeaderText,
        string CombinedText,
        IReadOnlyList<OcrPassResult> Passes,
        IReadOnlyList<RumorLineRecognitionStatus> LineStatuses);

    private readonly record struct CaptureLayout(
        int Width,
        int Height,
        int CursorOffsetX,
        int CursorOffsetY);

    private sealed record CachedRecognition(
        IReadOnlyList<RumorMatch> Matches,
        bool HeaderDetected,
        bool TrustedSlotMatch,
        string Diagnostic,
        IReadOnlyDictionary<string, MatchEvidenceSnapshot> Evidence,
        IReadOnlyList<RumorLineRecognitionStatus> LineStatuses);
}

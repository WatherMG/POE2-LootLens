using System.Diagnostics;
using DrawingRectangle = System.Drawing.Rectangle;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MahApps.Metro.Controls;

namespace Poe2LootLens;

public partial class MainWindow : MetroWindow
{
    private AppConfig _config = new();
    private PriceRepository? _repo;
    private IconCache? _icons;
    private ScanEngine? _engine;
    private RumorScanner? _rumorScanner;
    private readonly ModuleStateMachine _priceModuleState = new();
    private readonly ModuleStateMachine _rumorModuleState = new();
    private RumorCatalog? _rumorCatalog;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _dataOperationGate = new(1, 1);
    private static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedRefreshCooldown = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _refreshCooldownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _nextManualRefreshAtUtc = DateTime.MinValue;
    private bool _dataControlsEnabled;
    private bool _calibrationInProgress;
    private bool _diagnosticsExportInProgress;
    private bool _closing;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _trayBalloonShown;
    private bool _allowExit;
    private readonly bool _startHiddenOnLaunch;
    private bool _restoreRequested;
    private bool _startupStarted;

    public MainWindow()
    {
        InitializeComponent();
        _config = ConfigStore.Load();
        bool firstRun = !_config.FirstRunCompleted;
        bool activationRequested = App.ConsumePendingActivationRequest();
        _startHiddenOnLaunch = _config.StartMinimized && !firstRun && !activationRequested;
        _restoreRequested = activationRequested;
        if (firstRun)
        {
            _config.FirstRunCompleted = true;
            try { ConfigStore.Save(_config); } catch { }
        }

        VersionLabel.Text = GetDisplayVersion();
        _refreshCooldownTimer.Tick += (_, _) => UpdateRefreshCooldownUi();
        StateChanged += OnStateChanged;
    }

    internal void StartApplication()
    {
        if (_startupStarted)
            return;
        _startupStarted = true;

        if (App.ConsumePendingActivationRequest())
            _restoreRequested = true;
        _trayBalloonShown = _config.TrayHintShown;
        ApplyHotkeys();
        ApplyUiLanguage();
        RefreshModuleUi();
        _refreshCooldownTimer.Start();

        if (_startHiddenOnLaunch && !_restoreRequested)
        {
            // Do not call Show/Hide for a tray-only launch. Creating a native WPF window and hiding
            // it from Loaded left a black rectangle on some systems for the first rendered frame.
            ShowInTaskbar = false;
            EnsureTrayIcon();
            _trayIcon!.Visible = true;
        }
        else
        {
            ShowInTaskbar = true;
            Show();
        }

        _ = StartupAsync(applyModuleAutoStart: true);
    }

    private static string GetDisplayVersion()
    {
        string? informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return $"v{informational.Split('+')[0]}";
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "v0.9.0-beta" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private async Task StartupAsync(bool applyModuleAutoStart = false)
    {
        await _dataOperationGate.WaitAsync();
        try
        {
            SetDataControlsEnabled(false);
            SetStatusPill(T("ЗАГРУЗКА", "LOADING"), "#FFBF69");
            StatusLabel.Text = T("Проверка OCR-модели…", "Checking OCR model…");

            var ocrStatus = await OcrDataManager.EnsurePriceAsync(_http, _config.GameLanguage);
            if (!ocrStatus.Success)
            {
                StatusLabel.Text = ocrStatus.Message;
                SetStatusPill(T("ОШИБКА", "ERROR"), "#FF7272");
                return;
            }

            StatusLabel.Text = T("Загрузка цен и названий…", "Loading prices and names…");
            if (_repo is not null)
                _repo.PricesUpdated -= OnPricesUpdated;
            _repo?.Dispose();
            _icons?.Dispose();
            _repo = new PriceRepository(_http);
            _repo.PricesUpdated += OnPricesUpdated;
            _icons = new IconCache(_http);

            await Task.WhenAll(_repo.InitialFetchAsync(_config), _icons.LoadAsync());
            UpdateCurrencyIcons();
            _repo.StartAutoRefresh(_config);
            if (_repo.ItemCount == 0)
            {
                StatusLabel.Text = string.IsNullOrEmpty(_repo.LastError)
                    ? T("Источник цен вернул пустой снимок.", "The price source returned an empty snapshot.")
                    : T("Не удалось получить цены: ", "Could not load prices: ") + _repo.LastError;
                SetStatusPill(T("ОШИБКА", "ERROR"), "#FF7272");
                return;
            }

            _nextManualRefreshAtUtc = DateTime.UtcNow + ManualRefreshCooldown;
            UpdateStatusLabel();
            SetStatusPill(T("ГОТОВ", "READY"), "#56D69A");

            if (applyModuleAutoStart && _config.AutoStartPriceScanner)
            {
                if (_config.IsCalibrated)
                {
                    StartEngine();
                }
                else
                {
                    StatusLabel.Text = T(
                        "Цены загружены. Автозапуск оценщика пропущен: сначала выберите область захвата.",
                        "Prices loaded. Price-scanner autostart was skipped: select a capture area first.");
                }
            }

            if (applyModuleAutoStart &&
                _config.AutoStartRumorScanner &&
                _rumorModuleState.Snapshot.State is ModuleState.Stopped or ModuleState.Faulted)
            {
                await StartRumorScannerAsync(ensureOcrModel: true);
            }
        }
        catch (Exception exception)
        {
            StatusLabel.Text = T("Ошибка загрузки данных: ", "Data loading error: ") + exception.Message;
            SetStatusPill(T("ОШИБКА", "ERROR"), "#FF7272");
        }
        finally
        {
            SetDataControlsEnabled(true);
            UpdateRefreshCooldownUi();
            RefreshModuleUi();
            _dataOperationGate.Release();
        }
    }

    private async void RefreshDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (_repo is null || _icons is null || _closing)
            return;
        TimeSpan remaining = _nextManualRefreshAtUtc - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            StatusLabel.Text = T("Данные уже свежие. Повторное обновление через ", "Data is fresh. Retry in ") +
                               FormatRemaining(remaining) + ".";
            UpdateRefreshCooldownUi();
            return;
        }

        await _dataOperationGate.WaitAsync();
        try
        {
            SetDataControlsEnabled(false);
            SetStatusPill(T("ОБНОВЛЕНИЕ", "REFRESHING"), "#FFBF69");
            StatusLabel.Text = T("Обновление цен и справочников…", "Refreshing prices and reference data…");
            var ocrTask = OcrDataManager.EnsurePriceAsync(_http, _config.GameLanguage);
            var pricesTask = _repo.RefreshNowAsync(_config);
            await Task.WhenAll(ocrTask, pricesTask);
            var ocrStatus = await ocrTask;
            if (!string.IsNullOrEmpty(_repo.LastError))
            {
                _nextManualRefreshAtUtc = DateTime.UtcNow + FailedRefreshCooldown;
                StatusLabel.Text = T("Обновление не удалось; используется предыдущий снимок: ",
                    "Refresh failed; the previous snapshot is still active: ") + _repo.LastError;
                SetStatusPill(T("ПРЕДУПРЕЖДЕНИЕ", "WARNING"), "#FFBF69");
            }
            else
            {
                _nextManualRefreshAtUtc = DateTime.UtcNow + ManualRefreshCooldown;
                StatusLabel.Text = ocrStatus.Success
                    ? BuildStatusText()
                    : T("Цены обновлены, но ", "Prices updated, but ") + ocrStatus.Message;
                bool priceRunning = _priceModuleState.Snapshot.IsRunning;
                SetStatusPill(priceRunning ? T("РАБОТАЕТ", "RUNNING") : T("ГОТОВ", "READY"),
                    priceRunning ? "#59A9FF" : "#56D69A");
            }
        }
        catch (Exception exception)
        {
            _nextManualRefreshAtUtc = DateTime.UtcNow + FailedRefreshCooldown;
            StatusLabel.Text = T("Ошибка обновления: ", "Refresh error: ") + exception.Message;
            SetStatusPill(T("ОШИБКА", "ERROR"), "#FF7272");
        }
        finally
        {
            SetDataControlsEnabled(true);
            _dataOperationGate.Release();
            UpdateStatusLabel();
            UpdateRefreshCooldownUi();
        }
    }

    private void SetDataControlsEnabled(bool enabled)
    {
        _dataControlsEnabled = enabled;
        ModuleStateSnapshot priceState = _priceModuleState.Snapshot;
        ModuleStateSnapshot rumorState = _rumorModuleState.Snapshot;
        GeneralSettingsButton.IsEnabled = enabled;
        RefreshDataButton.IsEnabled = enabled;
        CalibrateButton.IsEnabled = enabled && !_calibrationInProgress && !priceState.IsBusy;
        StartStopButton.IsEnabled = enabled &&
                                    !_calibrationInProgress &&
                                    !priceState.IsBusy &&
                                    (priceState.IsRunning ||
                                     (_config.IsCalibrated && _repo is { ItemCount: > 0 }));
        PriceSettingsButton.IsEnabled = enabled && !priceState.IsBusy;
        RumorSettingsButton.IsEnabled = enabled && !rumorState.IsBusy;
        ToggleRumorScannerButton.IsEnabled = enabled && !rumorState.IsBusy;
        ManualRumorScanButton.IsEnabled = enabled && rumorState.IsRunning;
        ExportDiagnosticsButton.IsEnabled = !_diagnosticsExportInProgress;
        UpdateRefreshCooldownUi();
    }

    private void UpdateRefreshCooldownUi()
    {
        if (!IsLoaded)
            return;
        TimeSpan remaining = _nextManualRefreshAtUtc - DateTime.UtcNow;
        bool coolingDown = remaining > TimeSpan.Zero;
        RefreshDataButton.IsEnabled = _dataControlsEnabled && !coolingDown;
        RefreshDataCooldownLabel.Text = coolingDown
            ? T("Защита API: ручное обновление через ", "API protection: manual refresh in ") + FormatRemaining(remaining)
            : T($"Автообновление каждые {_config.DataRefreshIntervalMinutes} мин. · ручное обновление доступно",
                $"Auto refresh every {_config.DataRefreshIntervalMinutes} min · manual refresh available");
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        int totalSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    private void UpdateCurrencyIcons()
    {
        DivineRateIconImage.Source = LoadImageSource(Path.Combine(AppContext.BaseDirectory, "divine.png"));
    }

    private static BitmapImage? LoadImageSource(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private void OnPricesUpdated() => _ = Dispatcher.BeginInvoke(UpdateStatusLabel);

    private void UpdateStatusLabel()
    {
        if (_repo is null)
            return;
        StatusLabel.Text = BuildStatusText();
        LeagueSummaryLabel.Text = T("Лига: ", "League: ") + _config.LeagueName;
        RateValueLabel.Text = _repo.ExaltedPerDivine > 0m
            ? T($"1 див. = {_repo.ExaltedPerDivine:0.#} экз.", $"1 div = {_repo.ExaltedPerDivine:0.#} ex")
            : T("1 див. = — экз.", "1 div = — ex");
        RateSourceLabel.Text = _repo.LastFetchedAt is { } time
            ? $"poe.ninja · {time:dd.MM HH:mm}"
            : "poe.ninja";
    }

    private string BuildStatusText()
    {
        if (_repo is null)
            return T("Данные не загружены", "Data not loaded");
        string fetched = _repo.LastFetchedAt is { } time ? time.ToString("dd.MM HH:mm") : T("никогда", "never");
        return T($"Загружено предметов: {_repo.ItemCount} · обновлено: {fetched}",
            $"Loaded items: {_repo.ItemCount} · updated: {fetched}");
    }

    private void RefreshModuleUi()
    {
        UpdateRegionLabel();
        ModuleStateSnapshot priceState = _priceModuleState.Snapshot;
        ModuleStateSnapshot rumorState = _rumorModuleState.Snapshot;

        GeneralSettingsButton.IsEnabled = _dataControlsEnabled && !priceState.IsBusy && !rumorState.IsBusy;
        CalibrateButton.IsEnabled = _dataControlsEnabled && !_calibrationInProgress && !priceState.IsBusy;
        PriceSettingsButton.IsEnabled = _dataControlsEnabled && !priceState.IsBusy;
        RumorSettingsButton.IsEnabled = _dataControlsEnabled && !rumorState.IsBusy;

        PriceModuleStatusText.Text = ModuleStateText(priceState.State);
        PriceModuleStatusText.ToolTip = priceState.LastError;
        PriceStatusDot.Fill = BrushFrom(ModuleStateColor(priceState.State));
        StartStopButton.Content = priceState.State switch
        {
            ModuleState.Running => T("Остановить", "Stop"),
            ModuleState.Starting => T("Запуск…", "Starting…"),
            ModuleState.Stopping => T("Остановка…", "Stopping…"),
            ModuleState.Faulted => T("Повторить", "Retry"),
            _ => T("Запустить", "Start"),
        };
        StartStopButton.Background = priceState.IsRunning
            ? BrushFrom("#71303C")
            : (System.Windows.Media.Brush)FindResource("AccentStrongBrush");
        StartStopButton.IsEnabled = _dataControlsEnabled &&
                                    !_calibrationInProgress &&
                                    !priceState.IsBusy &&
                                    (priceState.IsRunning ||
                                     (_config.IsCalibrated && _repo is { ItemCount: > 0 }));

        string uiLanguage = UiLanguage.Resolve(_config.UiLanguage);
        string priceToggle = HotkeyBinding.DisplayOptional(_config.StartStopHotkey, uiLanguage);
        string priceScan = HotkeyBinding.DisplayOptional(_config.PriceScanHotkey, uiLanguage);
        string priceArea = HotkeyBinding.DisplayOptional(_config.CalibrateHotkey, uiLanguage);
        string priceDebug = HotkeyBinding.DisplayOptional(_config.DebugHotkey, uiLanguage);
        PriceHotkeySummaryText.Text = T(
            $"Модуль: {priceToggle} · Оценить: {priceScan} · Область: {priceArea} · Отладка: {priceDebug}",
            $"Module: {priceToggle} · Scan: {priceScan} · Area: {priceArea} · Debug: {priceDebug}");

        bool manual = NormalizeRumorScanMode(_config.RumorScanMode) == "manual";
        RumorModeLabel.Text = manual ? T("Ручной", "Manual") : T("По наведению · экспериментальный", "Hover · experimental");
        string rumorToggle = HotkeyBinding.DisplayOptional(_config.RumorStartStopHotkey, uiLanguage);
        string rumorScan = HotkeyBinding.DisplayOptional(_config.RumorManualScanHotkey, uiLanguage);
        string rumorDebug = HotkeyBinding.DisplayOptional(_config.RumorDebugHotkey, uiLanguage);
        RumorHotkeySummaryText.Text = T(
            $"Модуль: {rumorToggle} · Сканировать: {rumorScan} · Отладка: {rumorDebug}",
            $"Module: {rumorToggle} · Scan: {rumorScan} · Debug: {rumorDebug}");
        RumorModuleStatusText.Text = ModuleStateText(rumorState.State);
        RumorModuleStatusText.ToolTip = rumorState.LastError;
        RumorStatusDot.Fill = BrushFrom(ModuleStateColor(rumorState.State));
        ToggleRumorScannerButton.Content = rumorState.State switch
        {
            ModuleState.Running => T("Остановить", "Stop"),
            ModuleState.Starting => T("Запуск…", "Starting…"),
            ModuleState.Stopping => T("Остановка…", "Stopping…"),
            ModuleState.Faulted => T("Повторить", "Retry"),
            _ => T("Запустить", "Start"),
        };
        ToggleRumorScannerButton.Background = rumorState.IsRunning
            ? BrushFrom("#71303C")
            : (System.Windows.Media.Brush)FindResource("AccentStrongBrush");
        ToggleRumorScannerButton.IsEnabled = _dataControlsEnabled && !rumorState.IsBusy;
        ManualRumorScanButton.Visibility = Visibility.Visible;
        ManualRumorScanButton.IsEnabled = _dataControlsEnabled && rumorState.IsRunning;
    }

    private string ModuleStateText(ModuleState state) => state switch
    {
        ModuleState.Starting => T("Запускается", "Starting"),
        ModuleState.Running => T("Работает", "Running"),
        ModuleState.Stopping => T("Останавливается", "Stopping"),
        ModuleState.Faulted => T("Ошибка", "Faulted"),
        _ => T("Остановлен", "Stopped"),
    };

    private static string ModuleStateColor(ModuleState state) => state switch
    {
        ModuleState.Running => "#59A9FF",
        ModuleState.Starting or ModuleState.Stopping => "#FFBF69",
        ModuleState.Faulted => "#FF7272",
        _ => "#75849A",
    };

    private void UpdateRegionLabel()
    {
        RegionLabel.Text = _config.IsCalibrated
            ? $"x={_config.RegionX}, y={_config.RegionY}, {_config.RegionWidth}×{_config.RegionHeight}"
            : T("Не настроена", "Not configured");
    }

    internal async void RunCalibration()
    {
        ModuleStateSnapshot priceState = _priceModuleState.Snapshot;
        if (_calibrationInProgress || priceState.IsBusy || _closing)
            return;

        _calibrationInProgress = true;
        bool restart = priceState.IsRunning;
        string previousStatus = StatusLabel.Text;
        try
        {
            SetDataControlsEnabled(false);
            StatusLabel.Text = T("Подготовка выбора области…", "Preparing area selection…");
            if (restart)
                await StopEngineAsync();
            else
                PriceOverlayManager.HideNow();

            StatusLabel.Text = T("Выделите список наград и нажмите Enter…", "Select the reward list and press Enter…");
            DrawingRectangle? rectangle = await Task.Run(CalibrationOverlay.RunOnStaThread);
            if (rectangle is null)
            {
                StatusLabel.Text = previousStatus;
                return;
            }

            _config.RegionRect = rectangle.Value;
            ConfigStore.Save(_config);
            UpdateRegionLabel();
            StatusLabel.Text = T("Область сохранена", "Capture area saved");
        }
        catch (Exception exception)
        {
            StatusLabel.Text = T("Ошибка выбора области: ", "Area selection error: ") + exception.Message;
        }
        finally
        {
            _calibrationInProgress = false;
            SetDataControlsEnabled(true);
            if (restart && _config.IsCalibrated)
                StartEngine();
            RefreshModuleUi();
        }
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e) => RunCalibration();

    private void PriceGuideButton_Click(object sender, RoutedEventArgs e)
    {
        var guide = new PriceCaptureGuideWindow(_config.UiLanguage) { Owner = this };
        guide.ShowDialog();
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e) => ToggleStartStop();

    internal async void ToggleStartStop()
    {
        ModuleStateSnapshot state = _priceModuleState.Snapshot;
        if (state.IsBusy || _calibrationInProgress || _closing)
            return;

        if (state.IsRunning)
        {
            await StopEngineAsync();
            return;
        }

        if (!_config.IsCalibrated)
        {
            StatusLabel.Text = T("Сначала выберите область захвата.", "Select a capture area first.");
            return;
        }

        StartEngine();
    }

    private bool StartEngine()
    {
        if (!_priceModuleState.TryBeginStart())
            return false;

        RefreshModuleUi();
        ScanEngine? engine = null;
        try
        {
            if (_repo is null || _icons is null || _repo.ItemCount <= 0)
                throw new InvalidOperationException(T("Данные рынка ещё не загружены.", "Market data is not loaded yet."));
            if (!_config.IsCalibrated)
                throw new InvalidOperationException(T("Область захвата не выбрана.", "The capture area is not configured."));

            engine = new ScanEngine(_config, _repo, _icons);
            engine.Start();
            _engine = engine;
            _priceModuleState.MarkRunning();
            SetStatusPill(T("РАБОТАЕТ", "RUNNING"), "#59A9FF");
            return true;
        }
        catch (Exception exception)
        {
            try { engine?.Dispose(); } catch { }
            _engine = null;
            _priceModuleState.MarkFaulted(exception);
            StatusLabel.Text = T("Не удалось запустить оценщик: ", "Could not start the price scanner: ") + exception.Message;
            SetStatusPill(T("ОШИБКА", "ERROR"), "#FF7272");
            return false;
        }
        finally
        {
            RefreshModuleUi();
        }
    }

    private async Task StopEngineAsync()
    {
        if (!_priceModuleState.TryBeginStop())
            return;

        ScanEngine? engine = _engine;
        _engine = null;
        RefreshModuleUi();
        try
        {
            PriceOverlayManager.Hide();
            if (engine is not null)
            {
                await Task.Run(() =>
                {
                    try { engine.StopAndWait(TimeSpan.FromSeconds(3)); }
                    finally { engine.Dispose(); }
                });
            }
            _priceModuleState.MarkStopped();
            SetStatusPill(T("ГОТОВ", "READY"), "#56D69A");
        }
        catch (Exception exception)
        {
            _priceModuleState.MarkFaulted(exception);
            StatusLabel.Text = T("Ошибка остановки оценщика: ", "Price-scanner stop error: ") + exception.Message;
            SetStatusPill(T("ОШИБКА", "ERROR"), "#FF7272");
        }
        finally
        {
            RefreshModuleUi();
        }
    }

    private void ToggleRumorScannerButton_Click(object sender, RoutedEventArgs e) => ToggleRumorScanner();

    internal async void ToggleRumorScanner()
    {
        ModuleStateSnapshot state = _rumorModuleState.Snapshot;
        if (state.IsBusy || _closing)
            return;

        if (state.IsRunning)
            await StopRumorScannerAsync();
        else
            await StartRumorScannerAsync(ensureOcrModel: true);
    }

    private async Task<bool> StartRumorScannerAsync(bool ensureOcrModel)
    {
        if (!_rumorModuleState.TryBeginStart())
            return false;

        RefreshModuleUi();
        RumorScanner? scanner = null;
        try
        {
            if (ensureOcrModel)
            {
                OcrDataStatus rumorOcr = await OcrDataManager.EnsureRumorAsync(
                    _http,
                    _config.RumorOcrLanguage);
                if (!rumorOcr.Success)
                    throw new InvalidOperationException(rumorOcr.Message);
            }

            bool manual = NormalizeRumorScanMode(_config.RumorScanMode) == "manual";
            string tessdata = Path.Combine(AppContext.BaseDirectory, "tessdata");
            _rumorCatalog = RumorCatalog.Load(_config.RumorCatalogPath, _config.RumorUserCatalogPath);
            scanner = new RumorScanner(
                tessdata,
                _rumorCatalog,
                _config,
                automaticMode: !manual);
            scanner.StatusChanged += OnRumorScannerStatusChanged;
            scanner.Start();
            _rumorScanner = scanner;
            _rumorModuleState.MarkRunning();
            _config.RumorScannerEnabled = true;
            ConfigStore.Save(_config);
            RumorScannerStatusLabel.Text = manual
                ? T("Ручной режим активен — используйте кнопку сканирования или хоткей.", "Manual mode active — use the scan button or hotkey.")
                : T("Режим по наведению активен (экспериментальный).", "Hover mode is active (experimental).");
            return true;
        }
        catch (Exception exception)
        {
            if (scanner is not null)
            {
                scanner.StatusChanged -= OnRumorScannerStatusChanged;
                try { scanner.Dispose(); } catch { }
            }
            _rumorScanner = null;
            _rumorModuleState.MarkFaulted(exception);
            _config.RumorScannerEnabled = false;
            try { ConfigStore.Save(_config); } catch { }
            RumorScannerStatusLabel.Text = T("Не удалось запустить: ", "Could not start: ") + exception.Message;
            return false;
        }
        finally
        {
            RefreshModuleUi();
        }
    }

    private async Task StopRumorScannerAsync()
    {
        if (!_rumorModuleState.TryBeginStop())
            return;

        RumorScanner? scanner = _rumorScanner;
        _rumorScanner = null;
        RefreshModuleUi();
        try
        {
            if (scanner is not null)
            {
                scanner.StatusChanged -= OnRumorScannerStatusChanged;
                await Task.Run(() =>
                {
                    try { scanner.StopAndWait(TimeSpan.FromSeconds(3)); }
                    finally { scanner.Dispose(); }
                });
            }
            _rumorModuleState.MarkStopped();
            _config.RumorScannerEnabled = false;
            ConfigStore.Save(_config);
            RumorScannerStatusLabel.Text = T("Сканер выключен", "Scanner stopped");
        }
        catch (Exception exception)
        {
            _rumorModuleState.MarkFaulted(exception);
            RumorScannerStatusLabel.Text = T("Ошибка остановки: ", "Stop error: ") + exception.Message;
        }
        finally
        {
            RefreshModuleUi();
        }
    }

    private void OnRumorScannerStatusChanged(string status) =>
        _ = Dispatcher.BeginInvoke(() => RumorScannerStatusLabel.Text = status);

    internal void RequestManualRumorScan()
    {
        if (!_rumorModuleState.Snapshot.IsRunning || _rumorScanner is null)
            return;
        _rumorScanner.RequestManualScan();
        RumorScannerStatusLabel.Text = T("Сканирование запрошено…", "Scan requested…");
    }

    internal void RequestPriceScan()
    {
        if (!_priceModuleState.Snapshot.IsRunning || _engine is null)
        {
            StatusLabel.Text = T("Сначала запустите оценщик.", "Start the price scanner first.");
            return;
        }
        _engine.RequestScanNow();
        StatusLabel.Text = T("Повторное сканирование запрошено…", "Rescan requested…");
    }

    private void ManualRumorScanButton_Click(object sender, RoutedEventArgs e) => RequestManualRumorScan();

    internal void NotifyGlobalMousePressed(System.Drawing.Point point)
    {
        if (!_rumorModuleState.Snapshot.IsRunning ||
            _rumorScanner is null ||
            RumorOverlayManager.ContainsScreenPoint(point) ||
            RumorLineOverlayManager.ContainsScreenPoint(point))
        {
            return;
        }
        _rumorScanner.RequestHideIfUnpinned();
    }

    private async void GeneralSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new GeneralSettingsWindow(_config) { Owner = this };
        if (window.ShowDialog() != true || window.Result is null)
            return;

        AppConfig previous = _config;
        AppConfig next = window.Result;
        bool marketChanged = previous.LeagueName != next.LeagueName ||
                             previous.GameLanguage != next.GameLanguage ||
                             previous.DataRefreshIntervalMinutes != next.DataRefreshIntervalMinutes;
        bool loggingChanged = previous.LogLevel != next.LogLevel;
        bool rumorChanged = previous.AppLanguage != next.AppLanguage ||
                            previous.UiLanguage != next.UiLanguage ||
                            loggingChanged;
        bool restartPrice = _priceModuleState.Snapshot.IsRunning;
        bool restartRumor = _rumorModuleState.Snapshot.IsRunning;
        if ((marketChanged || loggingChanged) && restartPrice) await StopEngineAsync();
        if (rumorChanged && restartRumor) await StopRumorScannerAsync();

        _config = next;
        ConfigStore.Save(_config);
        ApplyHotkeys();
        ApplyUiLanguage();
        PriceOverlayManager.SetDisplayThreshold(_config.DivineDisplayThreshold, _config.DisplayThresholdCurrency);
        if (marketChanged)
            await StartupAsync();
        else
        {
            _repo?.StartAutoRefresh(_config);
            UpdateStatusLabel();
        }
        if (restartPrice && _config.IsCalibrated)
            StartEngine();
        if (restartRumor)
            await StartRumorScannerAsync(ensureOcrModel: false);
        RefreshModuleUi();
    }

    private async void PriceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        bool restart = _priceModuleState.Snapshot.IsRunning;
        if (restart) await StopEngineAsync();
        var window = new PriceSettingsWindow(_config) { Owner = this };
        bool saved = window.ShowDialog() == true && window.Result is not null;
        if (saved)
        {
            _config = window.Result!;
            ConfigStore.Save(_config);
            ApplyHotkeys();
        }
        if (restart && _config.IsCalibrated) StartEngine();
        RefreshModuleUi();
    }

    private async void RumorSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        bool restart = _rumorModuleState.Snapshot.IsRunning;
        if (restart) await StopRumorScannerAsync();
        var window = new RumorSettingsWindow(_config) { Owner = this };
        bool saved = window.ShowDialog() == true && window.Result is not null;
        if (saved)
        {
            _config = window.Result!;
            ConfigStore.Save(_config);
            ApplyHotkeys();
            OcrDataStatus ocrStatus = await OcrDataManager.EnsureRumorAsync(
                _http,
                _config.RumorOcrLanguage);
            if (!ocrStatus.Success)
            {
                RumorScannerStatusLabel.Text = ocrStatus.Message;
                restart = false;
            }
        }
        if (restart)
            await StartRumorScannerAsync(ensureOcrModel: false);
        RefreshModuleUi();
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(path);
        OpenExternalPath(path, T("Не удалось открыть папку логов: ", "Could not open the log folder: "));
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsExportInProgress)
            return;

        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = T("Сохранить архив диагностики", "Save diagnostics archive"),
            FileName = $"POE2-LootLens-diagnostics-{timestamp}.zip",
            DefaultExt = ".zip",
            Filter = T("ZIP-архив (*.zip)|*.zip", "ZIP archive (*.zip)|*.zip"),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _diagnosticsExportInProgress = true;
        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            StatusLabel.Text = T("Формирование архива диагностики…", "Creating diagnostics archive…");
            var snapshot = new DiagnosticsSnapshot(
                GetDisplayVersion().TrimStart('v'),
                ConfigCopy.Clone(_config),
                _priceModuleState.Snapshot,
                _rumorModuleState.Snapshot,
                _repo?.ItemCount ?? 0,
                _repo?.ExaltedPerDivine ?? 0m,
                _repo?.LastFetchedAt,
                _repo?.LastError);
            await DiagnosticsExporter.ExportAsync(dialog.FileName, snapshot);
            StatusLabel.Text = T(
                "Архив диагностики создан: ",
                "Diagnostics archive created: ") + Path.GetFileName(dialog.FileName);

            string? folder = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(folder))
                OpenExternalPath(folder, T("Не удалось открыть папку диагностики: ", "Could not open the diagnostics folder: "));
        }
        catch (Exception exception)
        {
            StatusLabel.Text = T("Не удалось создать диагностику: ", "Could not create diagnostics: ") + exception.Message;
        }
        finally
        {
            _diagnosticsExportInProgress = false;
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private void TelegramButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalPath("https://t.me/sysoev_alexey", T("Не удалось открыть Telegram: ", "Could not open Telegram: "));

    private void SupportButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalPath("https://www.tbank.ru/cf/dd0Trkzow9", T("Не удалось открыть страницу поддержки: ", "Could not open the support page: "));

    private void OpenExternalPath(string path, string errorPrefix)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { StatusLabel.Text = errorPrefix + exception.Message; }
    }

    private void ApplyHotkeys()
    {
        App.SetStartStopGesture(HotkeyBinding.ParseGestureOptional(_config.StartStopHotkey));
        App.SetPriceScanGesture(HotkeyBinding.ParseGestureOptional(_config.PriceScanHotkey));
        App.SetDebugGesture(HotkeyBinding.ParseGestureOptional(_config.DebugHotkey));
        App.SetCalibrateGesture(HotkeyBinding.ParseGestureOptional(_config.CalibrateHotkey));
        App.SetRumorStartStopGesture(HotkeyBinding.ParseGestureOptional(_config.RumorStartStopHotkey));
        App.SetRumorManualScanGesture(HotkeyBinding.ParseGestureOptional(_config.RumorManualScanHotkey));
        App.SetRumorDebugGesture(HotkeyBinding.ParseGestureOptional(_config.RumorDebugHotkey));
    }

    private void ApplyUiLanguage()
    {
        bool en = UiLanguage.IsEnglish(_config.UiLanguage);
        Title = "POE2 LootLens";
        AppSubtitleText.Text = en ? "Reward pricing and Atlas rumor scanning" : "Оценка наград и сканирование слухов Атласа";
        GeneralSettingsButton.Content = en ? "⚙  Settings" : "⚙  Настройки";
        DataTitleText.Text = en ? "Market data" : "Данные рынка";
        RefreshDataButton.Content = en ? "Refresh data" : "Обновить данные";
        PriceModuleTitleText.Text = en ? "Combination price scanner" : "Оценщик комбинаций";
        PriceModuleDescriptionText.Text = en
            ? "Recognizes rewards in the selected area and displays their value over the game."
            : "Распознаёт награды в выбранной области и показывает их стоимость поверх игры.";
        PriceAreaLabelText.Text = en ? "Capture area" : "Область захвата";
        CalibrateButton.Content = en ? "Select area" : "Выбрать область";
        PriceGuideButton.ToolTip = en ? "How to select the capture area" : "Как правильно выделить область";
        PriceSettingsButton.ToolTip = en ? "Price scanner settings" : "Настройки оценщика";
        RumorModuleTitleText.Text = en ? "Rumor scanner" : "Сканер слухов";
        RumorModuleDescriptionText.Text = en
            ? "Analyzes island rumors, keeps history and sorts findings by importance."
            : "Анализирует слухи островов, сохраняет историю и сортирует находки по важности.";
        RumorModeCaptionText.Text = en ? "Mode" : "Режим";
        RumorSettingsButton.ToolTip = en ? "Rumor scanner settings" : "Настройки сканера слухов";
        ManualRumorScanButton.Content = en ? "Scan now" : "Сканировать сейчас";
        ExportDiagnosticsButton.Content = en ? "Diagnostics ZIP" : "Диагностика ZIP";
        ExportDiagnosticsButton.ToolTip = en
            ? "Create a diagnostics archive to send with a bug report"
            : "Создать архив диагностики для отправки вместе с сообщением об ошибке";
        OpenLogsButton.Content = en ? "Open logs" : "Открыть логи";
        TelegramButton.Content = en ? "Feedback" : "Обратная связь";
        SupportButton.Content = en ? "Support" : "Поддержать";
        UpdateTrayMenu();
        RefreshModuleUi();
        UpdateRefreshCooldownUi();
        UpdateStatusLabel();
    }

    private string T(string ru, string en) => UiLanguage.Pick(_config.UiLanguage, ru, en);

    private void SetStatusPill(string text, string color)
    {
        StatusPillText.Text = text;
        System.Windows.Media.Brush brush = BrushFrom(color);
        StatusDot.Fill = brush;
        StatusPillText.Foreground = brush;
    }

    private static SolidColorBrush BrushFrom(string color)
    {
        var parsed = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        var brush = new SolidColorBrush(parsed);
        brush.Freeze();
        return brush;
    }

    private static string NormalizeRumorScanMode(string? value) =>
        string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "manual";

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            HideToTray(showNotification: true);
    }

    private void HideToTray(bool showNotification)
    {
        EnsureTrayIcon();
        _trayIcon!.Visible = true;
        ShowInTaskbar = false;
        Hide();
        if (!showNotification || _trayBalloonShown)
            return;

        _trayIcon.ShowBalloonTip(
            3500,
            "POE2 LootLens",
            T(
                "Приложение не закрыто и продолжает работать в области уведомлений. Для полного выхода используйте меню значка в трее.",
                "The application is still running in the notification area. Use the tray icon menu to exit completely."),
            System.Windows.Forms.ToolTipIcon.Info);
        _trayBalloonShown = true;
        _config.TrayHintShown = true;
        try
        {
            ConfigStore.Save(_config);
        }
        catch (Exception exception)
        {
            if (App.DebugMode)
                Console.WriteLine($"[Tray] failed to persist hint state: {exception.Message}");
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
            return;
        string? executable = Environment.ProcessPath;
        var icon = executable is not null
            ? System.Drawing.Icon.ExtractAssociatedIcon(executable) ?? System.Drawing.SystemIcons.Application
            : System.Drawing.SystemIcons.Application;
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "POE2 LootLens",
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (_trayIcon is null)
            return;

        _trayIcon.ContextMenuStrip?.Dispose();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(T("Показать", "Show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(T("Выход", "Exit"), null, (_, _) => ExitFromTray());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray() => RestoreFromExternalLaunch();

    internal void RestoreFromExternalLaunch()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => RestoreFromExternalLaunch());
            return;
        }

        _restoreRequested = true;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();
        if (_trayIcon is not null)
            _trayIcon.Visible = false;
    }

    private void ExitFromTray()
    {
        AllowApplicationExit();
        if (_trayIcon is not null)
            _trayIcon.Visible = false;
        Close();
    }

    internal void AllowApplicationExit() => _allowExit = true;

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit && _config.CloseToTray)
        {
            e.Cancel = true;
            WindowState = WindowState.Normal;
            HideToTray(showNotification: true);
            return;
        }

        if (_closing)
            return;
        _closing = true;
        _refreshCooldownTimer.Stop();
        _trayIcon?.Dispose();
        _engine?.StopAndWait(TimeSpan.FromSeconds(3));
        _engine?.Dispose();
        _rumorScanner?.StopAndWait(TimeSpan.FromSeconds(3));
        _rumorScanner?.Dispose();
        RumorOverlayManager.Close();
        RumorLineOverlayManager.Close();
        _repo?.Dispose();
        _icons?.Dispose();
        _http.Dispose();
    }
}

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
    private RumorCatalog? _rumorCatalog;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _dataOperationGate = new(1, 1);
    private static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedRefreshCooldown = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _refreshCooldownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _nextManualRefreshAtUtc = DateTime.MinValue;
    private bool _dataControlsEnabled;
    private bool _calibrationInProgress;
    private bool _engineOperationInProgress;
    private bool _rumorOperationInProgress;
    private bool _closing;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _trayBalloonShown;
    private bool _allowExit;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = GetDisplayVersion();
        _refreshCooldownTimer.Tick += (_, _) => UpdateRefreshCooldownUi();
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _config = ConfigStore.Load();
        _trayBalloonShown = _config.TrayHintShown;
        ApplyHotkeys();
        ApplyUiLanguage();
        RefreshModuleUi();
        _refreshCooldownTimer.Start();
        await StartupAsync();
    }

    private static string GetDisplayVersion()
    {
        string? informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return $"v{informational.Split('+')[0]}";
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "v0.9.0-beta.11" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private async Task StartupAsync()
    {
        await _dataOperationGate.WaitAsync();
        bool dataReady = false;
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

            dataReady = true;
            _nextManualRefreshAtUtc = DateTime.UtcNow + ManualRefreshCooldown;
            UpdateStatusLabel();
            SetStatusPill(T("ГОТОВ", "READY"), "#56D69A");

            if (_config.RumorScannerEnabled && _rumorScanner is null)
            {
                try
                {
                    OcrDataStatus rumorOcr = await OcrDataManager.EnsureRumorAsync(
                        _http,
                        _config.RumorOcrLanguage);
                    if (!rumorOcr.Success)
                        throw new InvalidOperationException(rumorOcr.Message);
                    StartRumorScanner();
                }
                catch (Exception exception)
                {
                    _config.RumorScannerEnabled = false;
                    ConfigStore.Save(_config);
                    RumorScannerStatusLabel.Text = T("Не удалось запустить: ", "Could not start: ") + exception.Message;
                }
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
            StartStopButton.IsEnabled = dataReady && _config.IsCalibrated && !_calibrationInProgress;
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
                SetStatusPill(_engine is null ? T("ГОТОВ", "READY") : T("РАБОТАЕТ", "RUNNING"),
                    _engine is null ? "#56D69A" : "#59A9FF");
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
        GeneralSettingsButton.IsEnabled = enabled;
        RefreshDataButton.IsEnabled = enabled;
        CalibrateButton.IsEnabled = enabled && !_calibrationInProgress;
        StartStopButton.IsEnabled = enabled && _config.IsCalibrated && _repo is { ItemCount: > 0 } && !_calibrationInProgress;
        PriceSettingsButton.IsEnabled = enabled;
        RumorSettingsButton.IsEnabled = enabled && !_rumorOperationInProgress;
        ToggleRumorScannerButton.IsEnabled = enabled && !_rumorOperationInProgress;
        ManualRumorScanButton.IsEnabled = enabled && _rumorScanner is not null;
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
        bool priceRunning = _engine is not null;
        PriceModuleStatusText.Text = priceRunning ? T("Работает", "Running") : T("Остановлен", "Stopped");
        PriceStatusDot.Fill = BrushFrom(priceRunning ? "#59A9FF" : "#75849A");
        StartStopButton.Content = priceRunning ? T("Остановить", "Stop") : T("Запустить", "Start");
        StartStopButton.Background = priceRunning ? BrushFrom("#71303C") : (System.Windows.Media.Brush)FindResource("AccentStrongBrush");
        StartStopButton.IsEnabled = _dataControlsEnabled && _config.IsCalibrated && _repo is { ItemCount: > 0 };

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
        bool rumorRunning = _rumorScanner is not null;
        RumorModuleStatusText.Text = rumorRunning ? T("Работает", "Running") : T("Остановлен", "Stopped");
        RumorStatusDot.Fill = BrushFrom(rumorRunning ? "#59A9FF" : "#75849A");
        ToggleRumorScannerButton.Content = rumorRunning ? T("Остановить", "Stop") : T("Запустить", "Start");
        ToggleRumorScannerButton.Background = rumorRunning ? BrushFrom("#71303C") : (System.Windows.Media.Brush)FindResource("AccentStrongBrush");
        ManualRumorScanButton.Visibility = Visibility.Visible;
        ManualRumorScanButton.IsEnabled = rumorRunning;
    }

    private void UpdateRegionLabel()
    {
        RegionLabel.Text = _config.IsCalibrated
            ? $"x={_config.RegionX}, y={_config.RegionY}, {_config.RegionWidth}×{_config.RegionHeight}"
            : T("Не настроена", "Not configured");
    }

    internal async void RunCalibration()
    {
        if (_calibrationInProgress || _engineOperationInProgress || _closing)
            return;
        _calibrationInProgress = true;
        _engineOperationInProgress = true;
        bool restart = _engine is not null;
        string previousStatus = StatusLabel.Text;
        try
        {
            SetDataControlsEnabled(false);
            StatusLabel.Text = T("Подготовка выбора области…", "Preparing area selection…");
            if (restart) await StopEngineAsync(); else PriceOverlayManager.HideNow();
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
            _engineOperationInProgress = false;
            SetDataControlsEnabled(true);
            if (restart && _config.IsCalibrated) StartEngine();
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
        if (_engineOperationInProgress || _calibrationInProgress || _closing)
            return;
        _engineOperationInProgress = true;
        try
        {
            if (_engine is null)
            {
                if (!_config.IsCalibrated)
                {
                    StatusLabel.Text = T("Сначала выберите область захвата.", "Select a capture area first.");
                    return;
                }
                StartEngine();
            }
            else
            {
                await StopEngineAsync();
            }
        }
        finally
        {
            _engineOperationInProgress = false;
            RefreshModuleUi();
        }
    }

    private void StartEngine()
    {
        if (_engine is not null || _repo is null || _icons is null || !_config.IsCalibrated)
            return;
        _engine = new ScanEngine(_config, _repo, _icons);
        _engine.Start();
        SetStatusPill(T("РАБОТАЕТ", "RUNNING"), "#59A9FF");
        RefreshModuleUi();
    }

    private async Task StopEngineAsync()
    {
        var engine = _engine;
        if (engine is null)
            return;
        _engine = null;
        // Dispose the overlay window with the engine so its rescan/close callbacks cannot
        // keep pointing at a stopped ScanEngine after the module is started again.
        PriceOverlayManager.Hide();
        await Task.Run(() =>
        {
            engine.StopAndWait(TimeSpan.FromSeconds(3));
            engine.Dispose();
        });
        SetStatusPill(T("ГОТОВ", "READY"), "#56D69A");
        RefreshModuleUi();
    }

    private void ToggleRumorScannerButton_Click(object sender, RoutedEventArgs e) => ToggleRumorScanner();

    internal async void ToggleRumorScanner()
    {
        if (_rumorOperationInProgress || _closing)
            return;
        _rumorOperationInProgress = true;
        try
        {
            if (_rumorScanner is null)
            {
                OcrDataStatus rumorOcr = await OcrDataManager.EnsureRumorAsync(
                    _http,
                    _config.RumorOcrLanguage);
                if (!rumorOcr.Success)
                    throw new InvalidOperationException(rumorOcr.Message);
                StartRumorScanner();
            }
            else
            {
                await StopRumorScannerAsync();
            }
        }
        catch (Exception exception)
        {
            RumorScannerStatusLabel.Text = T("Ошибка: ", "Error: ") + exception.Message;
            _config.RumorScannerEnabled = false;
            ConfigStore.Save(_config);
        }
        finally
        {
            _rumorOperationInProgress = false;
            SetDataControlsEnabled(true);
            RefreshModuleUi();
        }
    }

    private void StartRumorScanner()
    {
        if (_rumorScanner is not null)
            return;
        bool manual = NormalizeRumorScanMode(_config.RumorScanMode) == "manual";
        string tessdata = Path.Combine(AppContext.BaseDirectory, "tessdata");
        _rumorCatalog = RumorCatalog.Load(_config.RumorCatalogPath, _config.RumorUserCatalogPath);
        var scanner = new RumorScanner(
            tessdata,
            _rumorCatalog,
            _config,
            automaticMode: !manual);
        scanner.StatusChanged += OnRumorScannerStatusChanged;
        scanner.Start();
        _rumorScanner = scanner;
        _config.RumorScannerEnabled = true;
        ConfigStore.Save(_config);
        RumorScannerStatusLabel.Text = NormalizeRumorScanMode(_config.RumorScanMode) == "manual"
            ? T("Ручной режим активен — используйте кнопку сканирования или хоткей.", "Manual mode active — use the scan button or hotkey.")
            : T("Режим по наведению активен (экспериментальный).", "Hover mode is active (experimental).");
        RefreshModuleUi();
    }

    private async Task StopRumorScannerAsync()
    {
        var scanner = _rumorScanner;
        if (scanner is null)
            return;
        _rumorScanner = null;
        scanner.StatusChanged -= OnRumorScannerStatusChanged;
        await Task.Run(() =>
        {
            scanner.StopAndWait(TimeSpan.FromSeconds(3));
            scanner.Dispose();
        });
        _config.RumorScannerEnabled = false;
        ConfigStore.Save(_config);
        RumorScannerStatusLabel.Text = T("Сканер выключен", "Scanner stopped");
        RefreshModuleUi();
    }

    private void OnRumorScannerStatusChanged(string status) =>
        _ = Dispatcher.BeginInvoke(() => RumorScannerStatusLabel.Text = status);

    internal void RequestManualRumorScan()
    {
        if (_rumorScanner is null)
            return;
        _rumorScanner.RequestManualScan();
        RumorScannerStatusLabel.Text = T("Сканирование запрошено…", "Scan requested…");
    }

    internal void RequestPriceScan()
    {
        if (_engine is null)
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
        if (_rumorScanner is null ||
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
        bool restartPrice = _engine is not null;
        bool restartRumor = _rumorScanner is not null;
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
        if (restartPrice && _config.IsCalibrated) StartEngine();
        if (restartRumor) StartRumorScanner();
        RefreshModuleUi();
    }

    private async void PriceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        bool restart = _engine is not null;
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
        bool restart = _rumorScanner is not null;
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
        if (restart) StartRumorScanner();
        RefreshModuleUi();
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(path);
        OpenExternalPath(path, T("Не удалось открыть папку логов: ", "Could not open the log folder: "));
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

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        if (_trayIcon is not null)
            _trayIcon.Visible = false;
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            WindowState = WindowState.Normal;
            HideToTray(showNotification: true);
            return;
        }

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
        System.Windows.Application.Current.Shutdown();
    }
}

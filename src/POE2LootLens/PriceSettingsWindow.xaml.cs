using System.Windows;
using MahApps.Metro.Controls;
using WpfButton = System.Windows.Controls.Button;

namespace Poe2LootLens;

internal partial class PriceSettingsWindow : MetroWindow
{
    private readonly AppConfig _working;
    private readonly string _language;
    private HotkeyBinding.Action? _capturingAction;
    private WpfButton? _capturingButton;

    internal AppConfig? Result { get; private set; }

    internal PriceSettingsWindow(AppConfig source)
    {
        InitializeComponent();
        _working = ConfigCopy.Clone(source);
        _language = UiLanguage.Resolve(source.UiLanguage);
        AttemptsBox.Text = _working.PriceUnknownAttempts.ToString();
        RegionValueText.Text = source.IsCalibrated
            ? $"x={source.RegionX}, y={source.RegionY}, {source.RegionWidth}×{source.RegionHeight}"
            : T("Не настроена", "Not configured");
        RefreshHotkeyLabels();
        ApplyLanguage();
    }

    private void BindStartButton_Click(object sender, RoutedEventArgs e) =>
        BeginCapture(HotkeyBinding.Action.StartStop, BindStartButton);
    private void BindScanButton_Click(object sender, RoutedEventArgs e) =>
        BeginCapture(HotkeyBinding.Action.PriceScan, BindScanButton);
    private void BindCalibrateButton_Click(object sender, RoutedEventArgs e) =>
        BeginCapture(HotkeyBinding.Action.Calibrate, BindCalibrateButton);
    private void BindDebugButton_Click(object sender, RoutedEventArgs e) =>
        BeginCapture(HotkeyBinding.Action.Debug, BindDebugButton);

    private void ClearStartButton_Click(object sender, RoutedEventArgs e)
    {
        _working.StartStopHotkey = string.Empty;
        RefreshHotkeyLabels();
    }
    private void ClearScanButton_Click(object sender, RoutedEventArgs e)
    {
        _working.PriceScanHotkey = string.Empty;
        RefreshHotkeyLabels();
    }
    private void ClearCalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        _working.CalibrateHotkey = string.Empty;
        RefreshHotkeyLabels();
    }
    private void ClearDebugButton_Click(object sender, RoutedEventArgs e)
    {
        _working.DebugHotkey = string.Empty;
        RefreshHotkeyLabels();
    }

    private void BeginCapture(HotkeyBinding.Action action, WpfButton button)
    {
        if (_capturingAction is not null)
            return;
        _capturingAction = action;
        _capturingButton = button;
        SetHotkeyButtonsEnabled(false);
        button.Content = T("Нажмите сочетание…", "Press a shortcut…");
        App.BeginHotkeyCapture(action, OnCaptured);
    }

    private void OnCaptured(App.CaptureOutcome outcome, HotkeyGesture gesture)
    {
        if (!IsLoaded)
            return;
        if (outcome == App.CaptureOutcome.Reserved)
        {
            if (_capturingButton is not null)
                _capturingButton.Content = T("Занято", "In use");
            return;
        }
        if (outcome == App.CaptureOutcome.Captured && _capturingAction is { } action)
        {
            string storage = HotkeyBinding.ToStorage(gesture);
            switch (action)
            {
                case HotkeyBinding.Action.StartStop: _working.StartStopHotkey = storage; break;
                case HotkeyBinding.Action.PriceScan: _working.PriceScanHotkey = storage; break;
                case HotkeyBinding.Action.Calibrate: _working.CalibrateHotkey = storage; break;
                case HotkeyBinding.Action.Debug: _working.DebugHotkey = storage; break;
            }
        }
        EndCapture();
        RefreshHotkeyLabels();
    }

    private void EndCapture()
    {
        _capturingAction = null;
        _capturingButton = null;
        SetHotkeyButtonsEnabled(true);
        BindStartButton.Content = T("Назначить", "Assign");
        BindScanButton.Content = T("Назначить", "Assign");
        BindCalibrateButton.Content = T("Назначить", "Assign");
        BindDebugButton.Content = T("Назначить", "Assign");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (!int.TryParse(AttemptsBox.Text.Trim(), out int attempts) || attempts is < 2 or > 10)
        {
            ValidationText.Text = T("Количество попыток должно быть от 2 до 10.",
                "The attempt count must be between 2 and 10.");
            return;
        }
        string[] bindings = [_working.StartStopHotkey, _working.PriceScanHotkey, _working.CalibrateHotkey, _working.DebugHotkey];
        var configured = bindings.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (configured.Distinct(StringComparer.OrdinalIgnoreCase).Count() != configured.Length)
        {
            ValidationText.Text = T("Один хоткей нельзя назначить нескольким действиям.",
                "The same hotkey cannot be assigned to multiple actions.");
            return;
        }
        _working.PriceUnknownAttempts = attempts;
        Result = ConfigStore.Normalize(_working, migrateLegacyDefaults: false);
        DialogResult = true;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppConfig();
        _working.PriceUnknownAttempts = defaults.PriceUnknownAttempts;
        _working.StartStopHotkey = string.Empty;
        _working.PriceScanHotkey = string.Empty;
        _working.CalibrateHotkey = string.Empty;
        _working.DebugHotkey = string.Empty;
        AttemptsBox.Text = defaults.PriceUnknownAttempts.ToString();
        RefreshHotkeyLabels();
        ValidationText.Text = T("Настройки оценщика сброшены. Область захвата сохранена.",
            "Price scanner settings were reset. The capture area was preserved.");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosed(EventArgs e)
    {
        if (_capturingAction is not null)
            App.CancelHotkeyCapture();
        base.OnClosed(e);
    }

    private void RefreshHotkeyLabels()
    {
        StartHotkeyValue.Text = HotkeyBinding.DisplayOptional(_working.StartStopHotkey, _language);
        ScanHotkeyValue.Text = HotkeyBinding.DisplayOptional(_working.PriceScanHotkey, _language);
        CalibrateHotkeyValue.Text = HotkeyBinding.DisplayOptional(_working.CalibrateHotkey, _language);
        DebugHotkeyValue.Text = HotkeyBinding.DisplayOptional(_working.DebugHotkey, _language);
    }

    private void SetHotkeyButtonsEnabled(bool enabled)
    {
        BindStartButton.IsEnabled = enabled;
        BindScanButton.IsEnabled = enabled;
        BindCalibrateButton.IsEnabled = enabled;
        BindDebugButton.IsEnabled = enabled;
        ClearStartButton.IsEnabled = enabled;
        ClearScanButton.IsEnabled = enabled;
        ClearCalibrateButton.IsEnabled = enabled;
        ClearDebugButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
    }

    private void ApplyLanguage()
    {
        bool en = _language == "en";
        Title = en ? "Price scanner settings" : "Настройки оценщика";
        HeaderText.Text = en ? "Combination price scanner" : "Оценщик комбинаций";
        DescriptionText.Text = en
            ? "Capture area, unknown-row behavior and optional hotkeys."
            : "Область захвата, поведение неопределённых строк и необязательные хоткеи.";
        RegionSectionText.Text = en ? "Capture area" : "Область захвата";
        RegionHintText.Text = en
            ? "The area is changed from the main window. A running scanner pauses during selection."
            : "Область изменяется из главного окна. Во время выбора работающий оценщик приостанавливается.";
        RegionGuideText.Text = en
            ? "Select reward rows only: exclude the book frame, scrollbar and atlas background."
            : "Выделяйте только строки наград: без рамки книги, полосы прокрутки и фона карты.";
        BehaviorSectionText.Text = en ? "Behavior" : "Поведение";
        AttemptsLabel.Text = en
            ? "OCR attempts before showing an unknown result (2–10)"
            : "Попыток OCR перед отображением неизвестного результата (2–10)";
        HotkeysSectionText.Text = en ? "Hotkeys" : "Хоткеи";
        HotkeysHintText.Text = en
            ? "Disabled by default to avoid conflicts with the game and other software."
            : "По умолчанию отключены, чтобы не конфликтовать с игрой и другими приложениями.";
        StartHotkeyCaption.Text = en ? "Start / stop" : "Запуск / остановка";
        ScanHotkeyCaption.Text = en ? "Scan now" : "Оценить сейчас";
        CalibrateHotkeyCaption.Text = en ? "Select area" : "Выбор области";
        DebugHotkeyCaption.Text = en ? "OCR diagnostics" : "OCR-диагностика";
        BindStartButton.Content = BindScanButton.Content = BindCalibrateButton.Content = BindDebugButton.Content = en ? "Assign" : "Назначить";
        ClearStartButton.Content = ClearScanButton.Content = ClearCalibrateButton.Content = ClearDebugButton.Content = en ? "Clear" : "Снять";
        ResetButton.Content = en ? "Reset price settings" : "Сбросить настройки оценщика";
        CancelButton.Content = en ? "Cancel" : "Отмена";
        SaveButton.Content = en ? "Save" : "Сохранить";
    }

    private string T(string ru, string en) => _language == "en" ? en : ru;
}

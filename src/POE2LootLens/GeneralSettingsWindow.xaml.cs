using System.Globalization;
using System.Windows;
using MahApps.Metro.Controls;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace Poe2LootLens;

internal partial class GeneralSettingsWindow : MetroWindow
{
    private readonly AppConfig _working;
    private readonly string _uiLanguage;
    private bool _initializingControls = true;

    internal AppConfig? Result { get; private set; }

    internal GeneralSettingsWindow(AppConfig source)
    {
        InitializeComponent();
        _working = ConfigCopy.Clone(source);
        _uiLanguage = UiLanguage.Resolve(source.UiLanguage);
        Populate();
        ApplyLanguage();
        _initializingControls = false;
    }

    private void Populate()
    {
        LeagueCombo.ItemsSource = _working.AvailableLeagues;
        LeagueCombo.SelectedItem = _working.AvailableLeagues.Contains(_working.LeagueName)
            ? _working.LeagueName
            : _working.AvailableLeagues.FirstOrDefault();
        SelectByTag(UiLanguageCombo, _working.UiLanguage);
        SelectByTag(GameLanguageCombo, _working.GameLanguage);
        SelectByTag(RumorLanguageCombo, _working.AppLanguage);
        SelectByTag(ThresholdCurrencyCombo, _working.DisplayThresholdCurrency);
        SelectByTag(LogLevelCombo, _working.LogLevel);
        StartMinimizedCheckBox.IsChecked = _working.StartMinimized;
        CloseToTrayCheckBox.IsChecked = _working.CloseToTray;
        AutoStartPriceScannerCheckBox.IsChecked = _working.AutoStartPriceScanner;
        AutoStartRumorScannerCheckBox.IsChecked = _working.AutoStartRumorScanner;
        ThresholdBox.Text = _working.DivineDisplayThreshold.ToString("0.##", CultureInfo.InvariantCulture);
        RefreshIntervalBox.Text = _working.DataRefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture);
    }


    private void AutoStartPriceScannerCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializingControls || _working.IsCalibrated)
            return;

        System.Windows.MessageBox.Show(
            this,
            T(
                "Автозапуск оценщика не сработает, пока не выбрана область захвата. Сначала сохраните настройки, затем выберите область в главном окне.",
                "The price scanner cannot start automatically until a capture area is selected. Save these settings, then select the area in the main window."),
            T("Нужна область захвата", "Capture area required"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        string thresholdText = ThresholdBox.Text.Trim().Replace(',', '.');
        if (!decimal.TryParse(thresholdText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal threshold) ||
            threshold is <= 0m or > 100000m)
        {
            ValidationText.Text = T("Укажите корректный порог больше 0 и не больше 100000.",
                "Enter a valid threshold greater than 0 and not greater than 100000.");
            return;
        }
        if (!int.TryParse(RefreshIntervalBox.Text.Trim(), out int refreshMinutes) || refreshMinutes is < 15 or > 240)
        {
            ValidationText.Text = T("Интервал обновления должен быть от 15 до 240 минут.",
                "The refresh interval must be between 15 and 240 minutes.");
            return;
        }

        _working.UiLanguage = SelectedTag(UiLanguageCombo, "auto");
        _working.GameLanguage = SelectedTag(GameLanguageCombo, "ru");
        _working.AppLanguage = SelectedTag(RumorLanguageCombo, "ru");
        _working.LeagueName = LeagueCombo.SelectedItem as string ?? _working.LeagueName;
        _working.DisplayThresholdCurrency = SelectedTag(ThresholdCurrencyCombo, "divine");
        _working.DivineDisplayThreshold = PriceDisplayFormatter.NormalizeThreshold(threshold);
        _working.DataRefreshIntervalMinutes = refreshMinutes;
        _working.LogLevel = SelectedTag(LogLevelCombo, "info");
        _working.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
        _working.CloseToTray = CloseToTrayCheckBox.IsChecked == true;
        _working.AutoStartPriceScanner = AutoStartPriceScannerCheckBox.IsChecked == true;
        _working.AutoStartRumorScanner = AutoStartRumorScannerCheckBox.IsChecked == true;
        Result = ConfigStore.Normalize(_working, migrateLegacyDefaults: false);
        DialogResult = true;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppConfig();
        _working.UiLanguage = defaults.UiLanguage;
        _working.GameLanguage = defaults.GameLanguage;
        _working.AppLanguage = defaults.AppLanguage;
        _working.LeagueName = defaults.LeagueName;
        _working.DisplayThresholdCurrency = defaults.DisplayThresholdCurrency;
        _working.DivineDisplayThreshold = defaults.DivineDisplayThreshold;
        _working.DataRefreshIntervalMinutes = defaults.DataRefreshIntervalMinutes;
        _working.LogLevel = defaults.LogLevel;
        _working.StartMinimized = defaults.StartMinimized;
        _working.CloseToTray = defaults.CloseToTray;
        _working.AutoStartPriceScanner = defaults.AutoStartPriceScanner;
        _working.AutoStartRumorScanner = defaults.AutoStartRumorScanner;
        Populate();
        ValidationText.Text = T("Общие настройки возвращены к значениям по умолчанию. Нажмите «Сохранить».",
            "General settings were reset to defaults. Click Save to apply.");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ApplyLanguage()
    {
        bool en = _uiLanguage == "en";
        Title = en ? "Application settings" : "Настройки приложения";
        HeaderText.Text = en ? "General settings" : "Общие настройки";
        HeaderDescriptionText.Text = en
            ? "Interface, game client language, market, refresh and diagnostics."
            : "Интерфейс, язык клиента, рынок, обновление и диагностика.";
        LanguageSectionText.Text = en ? "Languages" : "Языки";
        UiLanguageLabel.Text = en ? "Application language" : "Язык приложения";
        GameLanguageLabel.Text = en ? "POE2 client language" : "Язык клиента POE2";
        RumorLanguageLabel.Text = en ? "Rumor description language" : "Язык описаний слухов";
        StartupSectionText.Text = en ? "Application and module startup" : "Запуск приложения и модулей";
        StartMinimizedCheckBox.Content = en ? "Start the application minimized to the tray" : "Запускать приложение свернутым в трей";
        CloseToTrayCheckBox.Content = en ? "Minimize to the tray when the window is closed" : "При закрытии сворачивать приложение в трей";
        AutoStartPriceScannerCheckBox.Content = en ? "Automatically start the combination price scanner" : "Автоматически запускать оценщик комбинаций";
        AutoStartRumorScannerCheckBox.Content = en ? "Automatically start the rumor scanner" : "Автоматически запускать сканер слухов";
        StartupHintText.Text = en
            ? "Minimize sends the app to the tray. Close exits unless close-to-tray is enabled. The first launch and a repeated shortcut launch always show the main window."
            : "Кнопка свернуть отправляет приложение в трей. Крестик закрывает приложение, если отдельно не включено сворачивание при закрытии. Первый и повторный запуск через ярлык показывают главное окно.";
        MarketSectionText.Text = en ? "Market and conversion" : "Рынок и конвертация";
        LeagueLabel.Text = en ? "League" : "Лига";
        ThresholdLabel.Text = en ? "Switch to divine display at" : "Переходить к дивайнам при сумме";
        ThresholdCurrencyLabel.Text = en ? "Threshold currency" : "Единица порога";
        ServiceSectionText.Text = en ? "Refresh and logs" : "Обновление и логи";
        RefreshIntervalLabel.Text = en ? "Auto refresh, minutes (15–240)" : "Автообновление, минут (15–240)";
        LogLevelLabel.Text = en ? "Log level" : "Уровень логирования";
        LogHintText.Text = en
            ? "Debug/Trace is written to the logs folder next to the application; use Open logs in the main window. Files rotate automatically."
            : "Debug/Trace пишутся в папку logs рядом с приложением; используйте «Открыть логи» в главном окне. Файлы ротируются автоматически.";
        ResetButton.Content = en ? "Reset general settings" : "Сбросить общие настройки";
        CancelButton.Content = en ? "Cancel" : "Отмена";
        SaveButton.Content = en ? "Save" : "Сохранить";
        LocalizeItems(UiLanguageCombo, en ? ["Automatic", "Russian", "English"] : ["Автоматически", "Русский", "English"]);
        LocalizeItems(ThresholdCurrencyCombo, en ? ["Divine orbs", "Exalted orbs"] : ["Дивайны", "Экзальты"]);
        LocalizeItems(LogLevelCombo, en
            ? ["Errors only", "Warnings", "Normal", "Debug", "Trace"]
            : ["Только ошибки", "Предупреждения", "Обычный", "Отладочный", "Трассировочный"]);
    }

    private string T(string ru, string en) => _uiLanguage == "en" ? en : ru;

    private static void SelectByTag(WpfComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<WpfComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
        comboBox.SelectedIndex = 0;
    }

    private static string SelectedTag(WpfComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as WpfComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void LocalizeItems(WpfComboBox comboBox, IReadOnlyList<string> values)
    {
        int index = 0;
        foreach (var item in comboBox.Items.OfType<WpfComboBoxItem>())
        {
            if (index < values.Count)
                item.Content = values[index++];
        }
    }
}

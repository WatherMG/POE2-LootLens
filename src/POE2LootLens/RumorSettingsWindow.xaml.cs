using System.Collections.ObjectModel;
using System.Windows;
using MahApps.Metro.Controls;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfSelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace Poe2LootLens;

internal partial class RumorSettingsWindow : MetroWindow
{
    private sealed record CategoryOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly AppConfig _working;
    private readonly string _language;
    private readonly ObservableCollection<CategoryOption> _categoryOrder = [];
    private HotkeyBinding.Action? _capturingAction;
    private System.Windows.Controls.Button? _capturingButton;

    internal AppConfig? Result { get; private set; }

    internal RumorSettingsWindow(AppConfig source)
    {
        InitializeComponent();
        _working = ConfigCopy.Clone(source);
        _language = UiLanguage.Resolve(source.UiLanguage);
        SelectByTag(ModeCombo, _working.RumorScanMode);
        SelectByTag(OcrLanguageCombo, _working.RumorOcrLanguage);
        SelectByTag(HideModeCombo, _working.RumorOverlayHideMode);
        SelectByTag(SortModeCombo, _working.RumorSortMode);
        PinnedByDefaultCheckBox.IsChecked = _working.RumorOverlayPinnedByDefault;
        TimeoutBox.Text = _working.RumorOverlayTimeoutSeconds.ToString();
        RebuildCategoryOrder(_working.RumorCategoryOrder);
        CategoryOrderList.ItemsSource = _categoryOrder;
        RefreshHotkeyLabel();
        ApplyLanguage();
        UpdateModeUi();
        UpdateHideUi();
        UpdateSortUi();
    }

    private void ModeCombo_SelectionChanged(object sender, WpfSelectionChangedEventArgs e) => UpdateModeUi();
    private void HideModeCombo_SelectionChanged(object sender, WpfSelectionChangedEventArgs e) => UpdateHideUi();
    private void SortModeCombo_SelectionChanged(object sender, WpfSelectionChangedEventArgs e) => UpdateSortUi();

    private void UpdateModeUi()
    {
        if (ManualHotkeyPanel is null || ModeCombo is null)
            return;
        bool manual = SelectedTag(ModeCombo, "manual") == "manual";
        ManualHotkeyPanel.Opacity = 1d;
        ModeHintText.Text = manual
            ? T("Ручной режим не запускает OCR в фоне. Наведитесь на кораблик и нажмите кнопку сканирования или назначенный хоткей.",
                "Manual mode does not run OCR in the background. Hover a ship and press the scan button or assigned hotkey.")
            : T("Экспериментальный режим автоматически сканирует окно после остановки курсора и может ошибаться на похожих панелях.",
                "Experimental hover mode scans after the cursor settles and may mistake similar parchment panels.");
    }

    private void UpdateHideUi()
    {
        if (HideModeCombo is null || TimeoutPanel is null)
            return;
        string mode = SelectedTag(HideModeCombo, "never");
        TimeoutPanel.Visibility = mode == "timeout" ? Visibility.Visible : Visibility.Collapsed;
        HideHintText.Text = mode switch
        {
            "timeout" => T(
                "Отсчёт начинается после последнего успешно обнаруженного окна. Закреплённый оверлей не скрывается.",
                "The timer starts after the last detected panel. A pinned overlay never auto-hides."),
            _ => T("Оверлей закрывается только вручную. Закрепление сохраняет его позицию.",
                "The overlay closes only manually. Pinning preserves its position."),
        };
    }

    private void UpdateSortUi()
    {
        if (SortModeCombo is null || CategoryOrderList is null)
            return;
        bool categoryFirst = SelectedTag(SortModeCombo, "tier") == "kindThenTier";
        CategoryOrderList.Opacity = categoryFirst ? 1d : 0.58d;
        MoveUpButton.IsEnabled = categoryFirst;
        MoveDownButton.IsEnabled = categoryFirst;
        CategoryOrderHintText.Text = categoryFirst
            ? T(
                "Категории применяются в указанном порядке; внутри каждой категории выше показывается лучший тир.",
                "Categories use this order; within each category the highest tier is shown first.")
            : T(
                "Сейчас главным остаётся тир. Порядок категорий используется только при равном тире.",
                "Tier is currently primary. Category order is used only when tiers are equal.");
    }

    private void BindToggleButton_Click(object sender, RoutedEventArgs e) =>
        BeginCapture(HotkeyBinding.Action.RumorStartStop, BindToggleButton);

    private void BindManualButton_Click(object sender, RoutedEventArgs e) =>
        BeginCapture(HotkeyBinding.Action.RumorManualScan, BindManualButton);

    private void BindDebugButton_Click(object sender, RoutedEventArgs e) =>
        BeginCapture(HotkeyBinding.Action.RumorDebug, BindDebugButton);

    private void BeginCapture(
        HotkeyBinding.Action action,
        System.Windows.Controls.Button button)
    {
        if (_capturingAction is not null)
            return;
        _capturingAction = action;
        _capturingButton = button;
        button.Content = T("Нажмите сочетание…", "Press a shortcut…");
        SetCaptureControls(false);
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
                case HotkeyBinding.Action.RumorStartStop:
                    _working.RumorStartStopHotkey = storage;
                    break;
                case HotkeyBinding.Action.RumorManualScan:
                    _working.RumorManualScanHotkey = storage;
                    break;
                case HotkeyBinding.Action.RumorDebug:
                    _working.RumorDebugHotkey = storage;
                    break;
            }
        }
        EndCapture();
        RefreshHotkeyLabel();
        UpdateModeUi();
    }

    private void EndCapture()
    {
        _capturingAction = null;
        _capturingButton = null;
        SetCaptureControls(true);
        BindToggleButton.Content = T("Назначить", "Assign");
        BindManualButton.Content = T("Назначить", "Assign");
        BindDebugButton.Content = T("Назначить", "Assign");
    }

    private void ClearToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _working.RumorStartStopHotkey = string.Empty;
        RefreshHotkeyLabel();
    }

    private void ClearManualButton_Click(object sender, RoutedEventArgs e)
    {
        _working.RumorManualScanHotkey = string.Empty;
        RefreshHotkeyLabel();
    }

    private void ClearDebugButton_Click(object sender, RoutedEventArgs e)
    {
        _working.RumorDebugHotkey = string.Empty;
        RefreshHotkeyLabel();
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDownButton_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int direction)
    {
        int index = CategoryOrderList.SelectedIndex;
        int target = index + direction;
        if (index < 0 || target < 0 || target >= _categoryOrder.Count)
            return;
        var item = _categoryOrder[index];
        _categoryOrder.Move(index, target);
        CategoryOrderList.SelectedItem = item;
        // Moving categories is an explicit request to make category order primary. Switching the
        // mode here avoids the confusing state where the list changes but tier-first sorting hides it.
        SelectByTag(SortModeCombo, "kindThenTier");
        UpdateSortUi();
    }

    private void OpenCatalogEditorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var editor = new RumorCatalogEditorWindow(_working)
            {
                Owner = this,
            };
            if (editor.ShowDialog() == true)
            {
                ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                ValidationText.Text = T("Изменения справочника сохранены отдельно от встроенных данных.",
                    "Catalog changes are stored separately from built-in data.");
            }
        }
        catch (Exception exception)
        {
            ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            ValidationText.Text = T("Не удалось открыть редактор: ", "Could not open the editor: ") + exception.Message;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        ValidationText.Text = string.Empty;
        string mode = SelectedTag(ModeCombo, "manual");
        if (!int.TryParse(TimeoutBox.Text.Trim(), out int timeout) || timeout is < 1 or > 3600)
        {
            ValidationText.Text = T("Таймаут должен быть от 1 до 3600 секунд.",
                "The timeout must be between 1 and 3600 seconds.");
            return;
        }
        string[] bindings =
        [
            _working.RumorStartStopHotkey,
            _working.RumorManualScanHotkey,
            _working.RumorDebugHotkey,
        ];
        string[] configured = bindings.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (configured.Distinct(StringComparer.OrdinalIgnoreCase).Count() != configured.Length)
        {
            ValidationText.Text = T(
                "Один хоткей нельзя назначить нескольким действиям.",
                "The same hotkey cannot be assigned to multiple actions.");
            return;
        }

        _working.RumorScanMode = mode;
        _working.RumorOcrLanguage = SelectedTag(OcrLanguageCombo, "en");
        _working.RumorOverlayHideMode = SelectedTag(HideModeCombo, "never");
        _working.RumorOverlayTimeoutSeconds = timeout;
        _working.RumorOverlayPinnedByDefault = PinnedByDefaultCheckBox.IsChecked == true;
        _working.RumorSortMode = SelectedTag(SortModeCombo, "kindThenTier");
        _working.RumorCategoryOrder = _categoryOrder.Select(item => item.Id).ToList();
        Result = ConfigStore.Normalize(_working, migrateLegacyDefaults: false);
        DialogResult = true;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppConfig();
        _working.RumorScanMode = defaults.RumorScanMode;
        _working.RumorOcrLanguage = defaults.RumorOcrLanguage;
        _working.RumorStartStopHotkey = string.Empty;
        _working.RumorManualScanHotkey = string.Empty;
        _working.RumorDebugHotkey = string.Empty;
        _working.RumorOverlayHideMode = defaults.RumorOverlayHideMode;
        _working.RumorOverlayHideDelayMs = defaults.RumorOverlayHideDelayMs;
        _working.RumorOverlayTimeoutSeconds = defaults.RumorOverlayTimeoutSeconds;
        _working.RumorOverlayPinnedByDefault = defaults.RumorOverlayPinnedByDefault;
        _working.RumorSortMode = defaults.RumorSortMode;
        _working.RumorCategoryOrder = [.. defaults.RumorCategoryOrder];
        SelectByTag(ModeCombo, _working.RumorScanMode);
        SelectByTag(OcrLanguageCombo, _working.RumorOcrLanguage);
        SelectByTag(HideModeCombo, _working.RumorOverlayHideMode);
        SelectByTag(SortModeCombo, _working.RumorSortMode);
        PinnedByDefaultCheckBox.IsChecked = _working.RumorOverlayPinnedByDefault;
        TimeoutBox.Text = _working.RumorOverlayTimeoutSeconds.ToString();
        RebuildCategoryOrder(_working.RumorCategoryOrder);
        RefreshHotkeyLabel();
        UpdateModeUi();
        UpdateHideUi();
        UpdateSortUi();
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        ValidationText.Text = T("Настройки сканера сброшены. Пользовательские описания не удалены.",
            "Scanner settings were reset. User descriptions were not deleted.");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosed(EventArgs e)
    {
        if (_capturingAction is not null)
            App.CancelHotkeyCapture();
        base.OnClosed(e);
    }

    private void RefreshHotkeyLabel()
    {
        ToggleHotkeyValue.Text = HotkeyBinding.DisplayOptional(_working.RumorStartStopHotkey, _language);
        ManualHotkeyValue.Text = HotkeyBinding.DisplayOptional(_working.RumorManualScanHotkey, _language);
        DebugHotkeyValue.Text = HotkeyBinding.DisplayOptional(_working.RumorDebugHotkey, _language);
    }

    private void SetCaptureControls(bool enabled)
    {
        ModeCombo.IsEnabled = enabled;
        OcrLanguageCombo.IsEnabled = enabled;
        BindToggleButton.IsEnabled = enabled;
        ClearToggleButton.IsEnabled = enabled;
        BindManualButton.IsEnabled = enabled;
        ClearManualButton.IsEnabled = enabled;
        BindDebugButton.IsEnabled = enabled;
        ClearDebugButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
        ResetButton.IsEnabled = enabled;
        OpenCatalogEditorButton.IsEnabled = enabled;
    }

    private void ApplyLanguage()
    {
        bool en = _language == "en";
        Title = en ? "Rumor scanner settings" : "Настройки сканера слухов";
        HeaderText.Text = en ? "Atlas rumor scanner" : "Сканер слухов Атласа";
        DescriptionText.Text = en
            ? "Scan mode, overlay behavior, sorting and catalog editor."
            : "Режим сканирования, поведение оверлея, сортировка и редактор справочника.";
        ModeSectionText.Text = en ? "Scanning" : "Сканирование";
        ModeLabel.Text = en ? "Mode" : "Режим";
        OcrLanguageLabel.Text = en ? "Rumor OCR language" : "Язык OCR слухов";
        OcrLanguageHintText.Text = en
            ? "This setting controls only rumor OCR. The reward price scanner always uses the separate POE2 client-language setting."
            : "Эта настройка управляет только OCR слухов. Оценщик наград всегда использует отдельную настройку языка клиента POE2.";
        ToggleHotkeyCaption.Text = en ? "Enable / disable" : "Включить / выключить";
        ManualHotkeyCaption.Text = en ? "Scan now" : "Сканировать сейчас";
        DebugHotkeyCaption.Text = en ? "Geometry / diagnostics" : "Геометрия / отладка";
        foreach (var item in ModeCombo.Items.OfType<WpfComboBoxItem>())
        {
            string tag = item.Tag?.ToString() ?? string.Empty;
            item.Content = tag == "manual"
                ? (en ? "Manual — recommended" : "Ручной — рекомендуется")
                : (en ? "On hover — experimental" : "При наведении — экспериментальный");
        }
        foreach (var item in OcrLanguageCombo.Items.OfType<WpfComboBoxItem>())
        {
            string tag = item.Tag?.ToString() ?? string.Empty;
            item.Content = tag switch
            {
                "ru" => en ? "Russian" : "Русский",
                "en+ru" => en ? "English + Russian — slower" : "Английский + русский — медленнее",
                _ => en ? "English — currently recommended" : "Английский — рекомендуется сейчас",
            };
        }
        OverlaySectionText.Text = en ? "Overlay behavior" : "Поведение оверлея";
        PinnedByDefaultCheckBox.Content = en
            ? "Show the overlay pinned by default"
            : "Показывать оверлей закреплённым по умолчанию";
        PinnedByDefaultHintText.Text = en
            ? "An unpinned overlay follows the active island and hides after a click outside it. A pinned overlay keeps its position until closed manually."
            : "Откреплённый оверлей следует за активным островом и скрывается при клике вне него. Закреплённый сохраняет позицию до ручного закрытия.";
        HideModeLabel.Text = en ? "Auto-hide" : "Автоскрытие";
        TimeoutLabel.Text = en ? "Timeout, seconds (1–3600)" : "Таймаут, секунд (1–3600)";
        SortSectionText.Text = en ? "Card sorting" : "Сортировка карточек";
        SortModeLabel.Text = en ? "Primary criterion" : "Основной критерий";
        CategoryOrderLabel.Text = en ? "Category order" : "Порядок категорий";
        MoveUpButton.Content = en ? "↑ Up" : "↑ Выше";
        MoveDownButton.Content = en ? "↓ Down" : "↓ Ниже";
        CatalogSectionText.Text = en ? "Rumor descriptions" : "Описания слухов";
        CatalogHintText.Text = en
            ? "Add maps, OCR variants, tiers, modifiers and notes using a validated form."
            : "Добавляйте карты, OCR-варианты, тиры, модификаторы и заметки через безопасную форму.";
        OpenCatalogEditorButton.Content = en ? "Open editor" : "Открыть редактор";
        BindToggleButton.Content = BindManualButton.Content = BindDebugButton.Content = en ? "Assign" : "Назначить";
        ClearToggleButton.Content = ClearManualButton.Content = ClearDebugButton.Content = en ? "Clear" : "Снять";
        ResetButton.Content = en ? "Reset scanner settings" : "Сбросить настройки сканера";
        CancelButton.Content = en ? "Cancel" : "Отмена";
        SaveButton.Content = en ? "Save" : "Сохранить";
        LocalizeItems(HideModeCombo, en
            ? ["Never", "After a timeout"]
            : ["Никогда", "Через заданное время"]);
        LocalizeItems(SortModeCombo, en
            ? ["Highest tier first", "Category first, then tier"]
            : ["Сначала лучший тир", "Сначала категория, затем тир"]);
        RebuildCategoryOrder(
            _categoryOrder.Select(option => option.Id),
            (CategoryOrderList.SelectedItem as CategoryOption)?.Id);
        UpdateModeUi();
        UpdateHideUi();
        UpdateSortUi();
    }

    private void RebuildCategoryOrder(IEnumerable<string>? source, string? selectedId = null)
    {
        string[] allowed = ["expedition", "boss", "unique"];
        string[] normalized = (source ?? [])
            .Select(value => value?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(allowed.Contains)
            .Distinct(StringComparer.Ordinal)
            .Concat(allowed)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _categoryOrder.Clear();
        foreach (string id in normalized)
            _categoryOrder.Add(new CategoryOption(id, CategoryLabel(id)));

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            CategoryOrderList.SelectedItem = _categoryOrder.FirstOrDefault(option =>
                string.Equals(option.Id, selectedId, StringComparison.Ordinal));
        }
    }

    private string CategoryLabel(string id) => id switch
    {
        "boss" => T("Боссы", "Bosses"),
        "unique" => T("Уникальные карты", "Unique maps"),
        _ => T("Экспедиции", "Expeditions"),
    };

    private string T(string ru, string en) => _language == "en" ? en : ru;

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

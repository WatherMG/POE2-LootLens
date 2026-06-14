using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using MahApps.Metro.Controls;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfSelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace Poe2LootLens;

internal partial class RumorCatalogEditorWindow : MetroWindow
{
    private sealed class EntryDisplayComparer : System.Collections.IComparer
    {
        public int Compare(object? left, object? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is not RumorCatalogEntry first) return -1;
            if (right is not RumorCatalogEntry second) return 1;
            int disabled = first.IsDisabled.CompareTo(second.IsDisabled);
            return disabled != 0
                ? disabled
                : StringComparer.CurrentCultureIgnoreCase.Compare(first.PrimaryPhrase, second.PrimaryPhrase);
        }
    }

    private static readonly Regex ValidId = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);
    private static readonly HashSet<string> ValidKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "expedition", "boss", "unique",
    };
    private static readonly HashSet<string> ValidTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "S+", "S", "A", "B", "C", "D",
    };
    private static readonly Regex ValidTag = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    private readonly string _language;
    private readonly string _defaultPath;
    private readonly string _userPath;
    private readonly RumorCatalogDocument _defaults;
    private readonly ObservableCollection<RumorCatalogEntry> _entries;
    private readonly ObservableCollection<string> _availableTags = [];
    private readonly ObservableCollection<string> _selectedTags = [];
    private readonly ICollectionView _view;
    private RumorCatalogEntry? _selected;
    private bool _loadingFields;

    internal RumorCatalogEditorWindow(AppConfig config)
    {
        InitializeComponent();
        _language = UiLanguage.Resolve(config.UiLanguage);
        var effectiveCatalog = RumorCatalog.Load(config.RumorCatalogPath, config.RumorUserCatalogPath);
        _defaultPath = effectiveCatalog.DefaultPath;
        _userPath = effectiveCatalog.UserPath;
        _defaults = RumorCatalog.LoadDefaultDocument(_defaultPath);
        RumorCatalogUserDocument user = RumorCatalog.LoadUserDocument(_userPath);
        var disabled = user.DisabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allById = _defaults.Entries.ToDictionary(
            entry => entry.Id,
            RumorCatalog.CloneEntry,
            StringComparer.OrdinalIgnoreCase);
        foreach (RumorCatalogEntry overrideEntry in user.Entries)
        {
            RumorCatalogEntry merged = RumorCatalog.CloneEntry(overrideEntry);
            if (allById.TryGetValue(overrideEntry.Id, out RumorCatalogEntry? defaultEntry))
            {
                merged.Phrases = defaultEntry.Phrases
                    .Concat(overrideEntry.Phrases ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            allById[overrideEntry.Id] = merged;
        }
        foreach (RumorCatalogEntry entry in allById.Values)
            entry.IsDisabled = disabled.Contains(entry.Id);
        _entries = new ObservableCollection<RumorCatalogEntry>(allById.Values);
        EntriesList.ItemsSource = _entries;
        TagsList.ItemsSource = _selectedTags;
        TagSelectorCombo.ItemsSource = _availableTags;
        RefreshAvailableTags();
        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = FilterEntry;
        if (_view is ListCollectionView listView)
            listView.CustomSort = new EntryDisplayComparer();
        KindFilter.SelectedIndex = 0;
        TierFilter.SelectedIndex = 0;
        ApplyLanguage();
        if (_entries.Count > 0)
            EntriesList.SelectedIndex = 0;
    }

    private bool FilterEntry(object value)
    {
        if (value is not RumorCatalogEntry entry)
            return false;
        string search = SearchBox?.Text?.Trim() ?? string.Empty;
        string kind = SelectedTag(KindFilter, string.Empty);
        string tier = SelectedTag(TierFilter, string.Empty);
        if (kind.Length > 0 && !string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase))
            return false;
        if (tier == "NONE" && !string.IsNullOrWhiteSpace(entry.Rating))
            return false;
        if (tier.Length > 0 && tier != "NONE" && !string.Equals(entry.Rating, tier, StringComparison.OrdinalIgnoreCase))
            return false;
        if (search.Length == 0)
            return true;
        return entry.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.PrimaryPhrase.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.TitleRu.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.TitleEn.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.Phrases.Any(phrase => phrase.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view is null)
            return;
        StoreCurrent(validatePriority: false);
        _view.Refresh();
    }

    private void EntriesList_SelectionChanged(object sender, WpfSelectionChangedEventArgs e)
    {
        if (_loadingFields)
            return;
        StoreCurrent(validatePriority: false);
        _selected = EntriesList.SelectedItem as RumorCatalogEntry;
        LoadSelected();
    }

    private void LoadSelected()
    {
        _loadingFields = true;
        try
        {
            bool hasSelection = _selected is not null;
            FieldsPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            NoSelectionText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            DeleteButton.IsEnabled = hasSelection;
            if (_selected is null)
                return;
            DeleteButton.Content = _selected.IsDisabled
                ? T("Включить", "Enable")
                : T("Отключить", "Disable");

            IdBox.Text = _selected.Id;
            PhrasesBox.Text = string.Join(Environment.NewLine, _selected.Phrases);
            SelectByTag(KindCombo, string.IsNullOrWhiteSpace(_selected.Kind) ? "expedition" : _selected.Kind);
            SelectByTag(TierCombo, _selected.Rating);
            PriorityBox.Text = _selected.Priority.ToString();
            _selectedTags.Clear();
            foreach (string tag in _selected.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
                _selectedTags.Add(tag);
            RefreshAvailableTags();
            TitleRuBox.Text = _selected.TitleRu;
            TitleEnBox.Text = _selected.TitleEn;
            MapRuBox.Text = _selected.MapRu;
            MapEnBox.Text = _selected.MapEn;
            MapTypeRuBox.Text = _selected.MapTypeRu;
            MapTypeEnBox.Text = _selected.MapTypeEn;
            ModsRuBox.Text = _selected.ModsRu;
            ModsEnBox.Text = _selected.ModsEn;
            ResultRuBox.Text = _selected.ResultRu;
            ResultEnBox.Text = _selected.ResultEn;
            DetailRuBox.Text = _selected.DetailRu;
            DetailEnBox.Text = _selected.DetailEn;
            NoteRuBox.Text = _selected.NoteRu;
            NoteEnBox.Text = _selected.NoteEn;
            RatingNotesRuBox.Text = _selected.RatingNotesRu;
            RatingNotesEnBox.Text = _selected.RatingNotesEn;
        }
        finally
        {
            _loadingFields = false;
        }
    }

    private bool StoreCurrent(bool validatePriority)
    {
        if (_loadingFields || _selected is null)
            return true;
        if (!int.TryParse(PriorityBox.Text.Trim(), out int priority))
        {
            if (validatePriority)
            {
                ValidationText.Text = T("Приоритет должен быть целым числом.", "Priority must be an integer.");
                return false;
            }
            priority = _selected.Priority;
        }

        _selected.Phrases = PhrasesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _selected.Kind = SelectedTag(KindCombo, "expedition");
        string id = IdBox.Text.Trim();
        if (id.Length == 0)
        {
            id = CreateUniqueId(BuildGeneratedId(_selected.Kind, _selected.PrimaryPhrase), _selected);
            IdBox.Text = id;
        }
        _selected.Id = id;
        _selected.Rating = SelectedTag(TierCombo, string.Empty);
        _selected.Priority = priority;
        _selected.Tags = _selectedTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _selected.TitleRu = TitleRuBox.Text.Trim();
        _selected.TitleEn = TitleEnBox.Text.Trim();
        _selected.MapRu = MapRuBox.Text.Trim();
        _selected.MapEn = MapEnBox.Text.Trim();
        _selected.MapTypeRu = MapTypeRuBox.Text.Trim();
        _selected.MapTypeEn = MapTypeEnBox.Text.Trim();
        _selected.ModsRu = ModsRuBox.Text.Trim();
        _selected.ModsEn = ModsEnBox.Text.Trim();
        _selected.ResultRu = ResultRuBox.Text.Trim();
        _selected.ResultEn = ResultEnBox.Text.Trim();
        _selected.DetailRu = DetailRuBox.Text.Trim();
        _selected.DetailEn = DetailEnBox.Text.Trim();
        _selected.NoteRu = NoteRuBox.Text.Trim();
        _selected.NoteEn = NoteEnBox.Text.Trim();
        _selected.RatingNotesRu = RatingNotesRuBox.Text.Trim();
        _selected.RatingNotesEn = RatingNotesEnBox.Text.Trim();
        _view.Refresh();
        return true;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        StoreCurrent(validatePriority: false);
        var entry = new RumorCatalogEntry
        {
            Kind = "expedition",
            Phrases = ["New rumor phrase"],
            Priority = 30,
            Tags = ["expedition"],
        };
        entry.Id = CreateUniqueId(BuildGeneratedId(entry.Kind, entry.PrimaryPhrase), except: null);
        _entries.Add(entry);
        RefreshAvailableTags();
        SearchBox.Text = string.Empty;
        KindFilter.SelectedIndex = 0;
        TierFilter.SelectedIndex = 0;
        _view.Refresh();
        EntriesList.SelectedItem = entry;
        EntriesList.ScrollIntoView(entry);
        PhrasesBox.Focus();
        PhrasesBox.SelectAll();
    }

    private void GenerateIdButton_Click(object sender, RoutedEventArgs e)
    {
        string phrase = PhrasesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        string kind = SelectedTag(KindCombo, "expedition");
        IdBox.Text = CreateUniqueId(BuildGeneratedId(kind, phrase), _selected);
        IdBox.CaretIndex = IdBox.Text.Length;
    }

    private void AddTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagSelectorCombo.SelectedItem is string tag)
            AddSelectedTag(tag);
    }

    private void CreateTagButton_Click(object sender, RoutedEventArgs e)
    {
        string tag = NewTagBox.Text.Trim().ToLowerInvariant();
        if (!ValidTag.IsMatch(tag))
        {
            ValidationText.Text = T(
                "Тег должен содержать строчные латинские буквы, цифры и дефисы.",
                "A tag may contain lowercase Latin letters, digits and hyphens.");
            return;
        }
        AddSelectedTag(tag);
        NewTagBox.Clear();
    }

    private void RemoveTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { CommandParameter: string tag })
            _selectedTags.Remove(tag);
    }

    private void AddSelectedTag(string tag)
    {
        string normalized = tag.Trim().ToLowerInvariant();
        if (normalized.Length == 0 || _selectedTags.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return;
        _selectedTags.Add(normalized);
        if (!_availableTags.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            _availableTags.Add(normalized);
        TagSelectorCombo.SelectedItem = normalized;
        ValidationText.Text = string.Empty;
    }

    private void RefreshAvailableTags()
    {
        WpfComboBox tagSelector = TagSelectorCombo;
        string? selected = tagSelector.SelectedItem as string;
        string[] common = ["expedition", "boss", "unique", "valuable", "currency", "rarity", "community"];
        string[] tags = _entries
            .SelectMany(entry => entry.Tags ?? [])
            .Concat(common)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _availableTags.Clear();
        foreach (string tag in tags)
            _availableTags.Add(tag);
        if (selected is not null && _availableTags.Contains(selected, StringComparer.OrdinalIgnoreCase))
            tagSelector.SelectedItem = selected;
        else if (_availableTags.Count > 0)
            tagSelector.SelectedIndex = 0;
    }

    private string CreateUniqueId(string baseId, RumorCatalogEntry? except)
    {
        string candidate = baseId;
        int suffix = 2;
        while (_entries.Any(entry => !ReferenceEquals(entry, except) &&
                                     string.Equals(entry.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseId}-{suffix++}";
        }
        return candidate;
    }

    private static string BuildGeneratedId(string kind, string phrase)
    {
        string normalized = (phrase ?? string.Empty)
            .Normalize(NormalizationForm.FormD)
            .ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        bool separator = false;
        foreach (char character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (separator && builder.Length > 0)
                    builder.Append('-');
                builder.Append(character);
                separator = false;
            }
            else
            {
                separator = true;
            }
        }
        string slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
            slug = "rumor";
        string prefix = kind is "boss" or "unique" or "expedition" ? kind : "rumor";
        return $"{prefix}-{slug}";
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        bool disable = !_selected.IsDisabled;
        if (disable)
        {
            MessageBoxResult result = System.Windows.MessageBox.Show(
                this,
                T(
                    $"Отключить запись «{_selected.PrimaryPhrase}»? Она останется внизу списка и её можно будет включить обратно.",
                    $"Disable “{_selected.PrimaryPhrase}”? It will remain at the bottom of the list and can be enabled again."),
                T("Отключение записи", "Disable entry"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;
        }

        _selected.IsDisabled = disable;
        RumorCatalogEntry selected = _selected;
        _view.Refresh();
        EntriesList.SelectedItem = selected;
        EntriesList.ScrollIntoView(selected);
        DeleteButton.Content = disable ? T("Включить", "Enable") : T("Отключить", "Disable");
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        ValidationText.Text = disable
            ? T("Запись отключена и перемещена в конец списка.", "The entry was disabled and moved to the end of the list.")
            : T("Запись снова включена.", "The entry is enabled again.");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        ValidationText.Text = string.Empty;
        if (!StoreCurrent(validatePriority: true))
            return;
        if (!ValidateEntries(out string error))
        {
            ValidationText.Text = error;
            return;
        }
        try
        {
            var overrides = RumorCatalog.BuildUserOverrides(_defaults, _entries);
            RumorCatalog.SaveUserDocumentAtomic(_userPath, overrides);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ValidationText.Text = T("Не удалось сохранить справочник: ", "Could not save the catalog: ") + exception.Message;
        }
    }

    private bool ValidateEntries(out string error)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phrases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            if (!ValidId.IsMatch(entry.Id))
            {
                error = T(
                    $"Некорректный ID «{entry.Id}». Используйте строчные латинские буквы, цифры и дефисы.",
                    $"Invalid ID “{entry.Id}”. Use lowercase Latin letters, digits and hyphens.");
                return false;
            }
            if (!ids.Add(entry.Id))
            {
                error = T($"ID «{entry.Id}» используется несколько раз.", $"ID “{entry.Id}” is duplicated.");
                return false;
            }
            // Disabled entries are retained only so they can be restored later. Their OCR phrases do
            // not participate in the effective catalog and therefore must not block an active
            // replacement entry with the same phrase.
            if (entry.IsDisabled)
                continue;
            if (!ValidKinds.Contains(entry.Kind))
            {
                error = T($"У записи «{entry.Id}» неизвестная категория.", $"Entry “{entry.Id}” has an unknown category.");
                return false;
            }
            if (!ValidTiers.Contains(entry.Rating))
            {
                error = T($"У записи «{entry.Id}» неизвестный тир.", $"Entry “{entry.Id}” has an unknown tier.");
                return false;
            }
            if (entry.Phrases.Length == 0)
            {
                error = T($"Для «{entry.Id}» добавьте хотя бы одну OCR-фразу.", $"Add at least one OCR phrase for “{entry.Id}”.");
                return false;
            }
            foreach (string tag in entry.Tags)
            {
                if (!ValidTag.IsMatch(tag))
                {
                    error = T(
                        $"У записи «{entry.Id}» некорректный тег «{tag}».",
                        $"Entry “{entry.Id}” has an invalid tag “{tag}”.");
                    return false;
                }
            }
            foreach (string phrase in entry.Phrases)
            {
                string normalized = RumorCatalog.NormalizeForMatch(phrase);
                if (normalized.Length < 4)
                {
                    error = T($"Фраза «{phrase}» слишком короткая для безопасного распознавания.",
                        $"Phrase “{phrase}” is too short for safe recognition.");
                    return false;
                }
                if (phrases.TryGetValue(normalized, out string? existingId) &&
                    !string.Equals(existingId, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    error = T(
                        $"Фраза «{phrase}» уже используется записью «{existingId}».",
                        $"Phrase “{phrase}” is already used by “{existingId}”.");
                    return false;
                }
                phrases[normalized] = entry.Id;
            }
        }
        error = string.Empty;
        return true;
    }

    private void OpenJsonButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_userPath))
                RumorCatalog.SaveUserDocumentAtomic(_userPath, new RumorCatalogUserDocument());
            Process.Start(new ProcessStartInfo(_userPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ValidationText.Text = T("Не удалось открыть файл: ", "Could not open the file: ") + exception.Message;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyLanguage()
    {
        bool en = _language == "en";
        Title = en ? "Rumor catalog editor" : "Редактор описаний слухов";
        HeaderText.Text = en ? "Rumor catalog editor" : "Редактор описаний слухов";
        DescriptionText.Text = en
            ? "Built-in data stays unchanged; your edits are saved in a separate user file."
            : "Встроенный справочник остаётся неизменным; ваши правки сохраняются отдельным пользовательским файлом.";
        OpenJsonButton.Content = en ? "Open overrides JSON" : "Открыть файл правок (JSON)";
        OpenJsonButton.ToolTip = en
            ? "Opens the separate user file containing only your overrides and disabled built-in entries."
            : "Открывает отдельный пользовательский файл, содержащий только ваши правки и отключённые встроенные записи.";
        SearchBox.ToolTip = en ? "Search by ID, phrase or title" : "Поиск по ID, фразе и названию";
        AddButton.Content = en ? "＋ Add" : "＋ Добавить";
        DeleteButton.Content = _selected?.IsDisabled == true
            ? (en ? "Enable" : "Включить")
            : (en ? "Disable" : "Отключить");
        NoSelectionText.Text = en ? "Select an entry or add a new one." : "Выберите запись или добавьте новую.";
        IdentityHeaderText.Text = en ? "Identification and OCR" : "Идентификация и OCR";
        KindLabel.Text = en ? "Category" : "Категория";
        TierLabel.Text = en ? "Tier" : "Тир";
        PhrasesLabel.Text = en ? "OCR phrases — one per line" : "Фразы OCR — по одной на строку";
        PriorityLabel.Text = en ? "Priority" : "Приоритет";
        TagsLabel.Text = en ? "Tags" : "Теги";
        AddTagButton.Content = en ? "Add" : "Добавить";
        CreateTagButton.Content = en ? "Create" : "Создать";
        GenerateIdButton.Content = en ? "Generate" : "Сгенерировать";
        GenerateIdButton.ToolTip = en
            ? "Build an ID from the category and primary OCR phrase"
            : "Сформировать ID из категории и основной OCR-фразы";
        NewTagBox.ToolTip = en
            ? "New tag: lowercase Latin letters, digits and hyphens"
            : "Новый тег: строчные латинские буквы, цифры и дефисы";
        LocalizedHeaderText.Text = en ? "Displayed data" : "Отображаемые данные";
        LocalizedHintText.Text = en
            ? "Russian and English fields are independent; an empty English field falls back to Russian."
            : "Русские и английские поля независимы; пустое английское поле использует русский fallback.";
        CancelButton.Content = en ? "Close" : "Закрыть";
        SaveButton.Content = en ? "Save changes" : "Сохранить изменения";
        LocalizeItems(KindFilter, en
            ? ["All categories", "Expedition", "Boss", "Unique map"]
            : ["Все категории", "Экспедиция", "Босс", "Уникальная карта"]);
        LocalizeItems(KindCombo, en
            ? ["Expedition", "Boss", "Unique map"]
            : ["Экспедиция", "Босс", "Уникальная карта"]);
        LocalizeItems(TierFilter, en
            ? ["All tiers", "S+ — Best", "S — Top", "A — Excellent", "B — Good", "C — Average", "D — Weak", "No tier"]
            : ["Все тиры", "S+ — Лучшее", "S — Топ", "A — Отлично", "B — Хорошо", "C — Средне", "D — Слабо", "Без тира"]);
        LocalizeItems(TierCombo, en
            ? ["No tier", "S+ — Best", "S — Top", "A — Excellent", "B — Good", "C — Average", "D — Weak"]
            : ["Без тира", "S+ — Лучшее", "S — Топ", "A — Отлично", "B — Хорошо", "C — Средне", "D — Слабо"]);
    }

    private string T(string ru, string en) => _language == "en" ? en : ru;

    private static string SelectedTag(WpfComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as WpfComboBoxItem)?.Tag?.ToString() ?? fallback;

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

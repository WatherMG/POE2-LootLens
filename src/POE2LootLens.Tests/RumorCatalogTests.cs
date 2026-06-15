using Newtonsoft.Json;
using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class RumorCatalogTests
{
    [Theory]
    [InlineData("Bleak and awful...", "expedition-bleak")]
    [InlineData("Wild roaming Free...", "expedition-roaming")]
    [InlineData("Nothin' to drink...", "expedition-nothing-to-drink")]
    [InlineData("Origin of the Fall...", "boss-olroth")]
    [InlineData("A good fellow...", "unique-good-fellow")]
    public void MatchLine_RecognizesKnownRumors(string raw, string expectedId)
    {
        using var temporary = new TemporaryCatalog();
        var catalog = RumorCatalog.Load(temporary.Path, temporary.UserPath);

        var result = catalog.MatchLine(raw);

        Assert.NotNull(result);
        Assert.Equal(expectedId, result!.Entry.Id);
    }

    [Theory]
    [InlineData("Nothin’ to drink...")]
    [InlineData("Nothin' to drink...")]
    [InlineData("Nothin to drink...")]
    [InlineData("Nothin’to drink...")]
    public void MatchLine_IgnoresApostropheVariants(string raw)
    {
        using var temporary = new TemporaryCatalog();
        var catalog = RumorCatalog.Load(temporary.Path, temporary.UserPath);

        var result = catalog.MatchLine(raw);

        Assert.NotNull(result);
        Assert.Equal("expedition-nothing-to-drink", result!.Entry.Id);
    }

    [Fact]
    public void MatchText_DeduplicatesRepeatedRumor()
    {
        using var temporary = new TemporaryCatalog();
        var catalog = RumorCatalog.Load(temporary.Path, temporary.UserPath);

        var result = catalog.MatchText("Bleak and awful...\nBleak and awful...");

        Assert.Single(result);
    }


    [Fact]
    public void MatchText_RecoversPhraseSplitAcrossAdjacentOcrLines()
    {
        using var temporary = new TemporaryCatalog();
        var catalog = RumorCatalog.Load(temporary.Path, temporary.UserPath);

        var result = catalog.MatchText("A good\nfellow...");

        Assert.Contains(result, match => match.Entry.Id == "unique-good-fellow");
    }

    [Theory]
    [InlineData("Неизведанные воды\nСлухи об острове", true)]
    [InlineData("Rumours of the island\nExpedition Logbook", true)]
    [InlineData("Журнал экспедиции", false)]
    [InlineData("Expedition Logbook", false)]
    [InlineData("Уникальное\nРедкий уникальный предмет\nУникальный жезл", false)]
    [InlineData("Валтома\nЧародейский расплав (Уровень 20) (1)", false)]
    public void LooksLikeRumorPanel_RequiresRumorSpecificAnchor(string text, bool expected)
    {
        Assert.Equal(expected, RumorCatalog.LooksLikeRumorPanel(text));
    }

    [Fact]
    public void Load_TracksEditableSourceForLiveReload()
    {
        using var temporary = new TemporaryCatalog();
        var catalog = RumorCatalog.Load(temporary.Path, temporary.UserPath);

        Assert.Equal(System.IO.Path.GetFullPath(temporary.Path), System.IO.Path.GetFullPath(catalog.DefaultPath));
        Assert.Equal(System.IO.Path.GetFullPath(temporary.UserPath), System.IO.Path.GetFullPath(catalog.SourcePath));
        Assert.NotEqual(DateTime.MinValue, catalog.SourceLastWriteTimeUtc);
    }

    [Fact]
    public void Load_IgnoresNullPhrasesAndKeepsLastDuplicateId()
    {
        string path = System.IO.Path.GetTempFileName();
        string userPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(path)}.user.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "entries": [
                    { "id": "broken", "phrases": null, "kind": "expedition" },
                    { "id": "same", "phrases": ["Old phrase"], "titleRu": "old" },
                    { "id": "same", "phrases": ["New phrase"], "titleRu": "new" }
                  ]
                }
                """);

            var catalog = RumorCatalog.Load(path, userPath);

            Assert.Single(catalog.Entries);
            Assert.Equal("same", catalog.Entries[0].Id);
            Assert.Equal("new", catalog.Entries[0].TitleRu);
            Assert.NotNull(catalog.Entries[0].Tags);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(userPath); } catch { }
            try { File.Delete(userPath + ".bak"); } catch { }
        }
    }

    [Fact]
    public void FuzzyMatch_ToleratesSmallItalicOcrError()
    {
        using var temporary = new TemporaryCatalog();
        var catalog = RumorCatalog.Load(temporary.Path, temporary.UserPath);

        var result = catalog.MatchLine("Endless cllffs...");

        Assert.NotNull(result);
        Assert.Equal("expedition-endless-cliffs", result!.Entry.Id);
        Assert.False(result.Exact);
    }


    [Theory]
    [InlineData("Orig of the fall...", "boss-olroth")]
    [InlineData("Urigin of the fait...", "boss-olroth")]
    [InlineData("diese cliffs", "expedition-endless-cliffs")]
    [InlineData("Nothin’ во drink...", "expedition-nothing-to-drink")]
    [InlineData("Somethin’ fichy...", "expedition-fishy")]
    public void MatchLine_RecoversObservedHandwritingOcrErrors(string raw, string expectedId)
    {
        using var temporary = new TemporaryCatalog(includeFishy: true);
        var catalog = RumorCatalog.Load(temporary.Path, temporary.UserPath);

        var result = catalog.MatchLine(raw);

        Assert.NotNull(result);
        Assert.Equal(expectedId, result!.Entry.Id);
    }

    [Theory]
    [InlineData("Somathin ficky", "expedition-fishy")]
    [InlineData("Somethin fihy", "expedition-fishy")]
    [InlineData("Eundlecs cliffs", "expedition-endless-cliffs")]
    [InlineData("Cndiess cliffs", "expedition-endless-cliffs")]
    [InlineData("Endlece ehffe", "expedition-endless-cliffs")]
    [InlineData("Cmethin fcky", "expedition-fishy")]
    [InlineData("Nockin te drink", "expedition-nothing-to-drink")]
    [InlineData("A goed follow", "unique-good-fellow")]
    public void MatchLine_RecognizesObservedAliases(string raw, string expectedId)
    {
        using var temporary = new TemporaryCatalog(includeFishy: true, includeObservedAliases: true);
        var catalog = RumorCatalog.Load(temporary.Path, temporary.UserPath);

        RumorMatch? result = catalog.MatchLine(raw);

        Assert.NotNull(result);
        Assert.Equal(expectedId, result!.Entry.Id);
    }

    private sealed class TemporaryCatalog : IDisposable
    {
        public string Path { get; }
        public string UserPath { get; }

        public TemporaryCatalog(bool includeFishy = false, bool includeObservedAliases = false)
        {
            Path = System.IO.Path.GetTempFileName();
            UserPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.user.json");
            var document = new RumorCatalogDocument
            {
                Entries =
                [
                    Entry("expedition-bleak", "expedition", "Bleak and awful"),
                    Entry("expedition-roaming", "expedition", "Wild roaming free", "Roaming free"),
                    Entry("expedition-nothing-to-drink", "expedition", "Nothin' to drink", "Nothing to drink"),
                    Entry("expedition-endless-cliffs", "expedition", "Endless cliffs"),
                    Entry("boss-olroth", "boss", "Origin of the fall"),
                    Entry("unique-good-fellow", "unique", "A good fellow"),
                ],
            };
            if (includeFishy)
                document.Entries.Add(Entry("expedition-fishy", "expedition", "Somethin' fishy", "Something fishy"));
            if (includeObservedAliases)
            {
                AddAliases(document, "expedition-fishy", "Somathin ficky", "Somethin fihy", "Cmethin fcky");
                AddAliases(document, "expedition-endless-cliffs", "Eundlecs cliffs", "Cndiess cliffs", "Endlece ehffe");
                AddAliases(document, "expedition-nothing-to-drink", "Nockin te drink");
                AddAliases(document, "unique-good-fellow", "A goed follow");
            }
            File.WriteAllText(Path, JsonConvert.SerializeObject(document));
        }

        private static void AddAliases(RumorCatalogDocument document, string id, params string[] aliases)
        {
            RumorCatalogEntry entry = document.Entries.Single(value => value.Id == id);
            entry.Phrases = entry.Phrases.Concat(aliases).ToArray();
        }

        private static RumorCatalogEntry Entry(string id, string kind, params string[] phrases) => new()
        {
            Id = id,
            Kind = kind,
            Phrases = phrases,
            TitleRu = id,
        };

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
            try { File.Delete(UserPath); } catch { }
            try { File.Delete(UserPath + ".bak"); } catch { }
            try { File.Delete(UserPath + ".tmp"); } catch { }
        }
    }
}

public class RumorScannerImageGateTests
{
    [Fact]
    public void ParchmentGate_AcceptsLargeRumorPanel()
    {
        using var bitmap = new System.Drawing.Bitmap(760, 690,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(15, 55, 62));
        using var parchment = new System.Drawing.SolidBrush(
            System.Drawing.Color.FromArgb(205, 190, 150));
        graphics.FillRectangle(parchment, 90, 30, 580, 480);

        Assert.True(RumorScanner.LooksLikeRumorPanelImage(bitmap));
    }

    [Fact]
    public void ParchmentGate_RejectsDarkAtlasFrame()
    {
        using var bitmap = new System.Drawing.Bitmap(760, 690,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(18, 62, 70));

        Assert.False(RumorScanner.LooksLikeRumorPanelImage(bitmap));
    }

    [Fact]
    public void ParchmentGate_RejectsPortraitRewardBook()
    {
        using var bitmap = new System.Drawing.Bitmap(680, 782,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(18, 62, 70));
        using var parchment = new System.Drawing.SolidBrush(
            System.Drawing.Color.FromArgb(205, 190, 150));
        graphics.FillRectangle(parchment, 80, 55, 510, 660);

        Assert.False(RumorScanner.LooksLikeRumorPanelImage(bitmap));
    }
}

public class RumorCatalogOverrideTests
{
    [Fact]
    public void Merge_UsesUserOverride_AddsNewEntry_AndDisablesDefault()
    {
        var defaults = new RumorCatalogDocument
        {
            Entries =
            [
                Entry("keep", "Default keep"),
                Entry("override", "Default phrase"),
                Entry("disabled", "Disabled phrase"),
            ],
        };
        var user = new RumorCatalogUserDocument
        {
            DisabledIds = ["disabled"],
            Entries =
            [
                Entry("override", "User phrase", title: "User title"),
                Entry("new-entry", "New phrase"),
            ],
        };

        RumorCatalogDocument merged = RumorCatalog.Merge(defaults, user);

        Assert.Equal(3, merged.Entries.Count);
        Assert.DoesNotContain(merged.Entries, entry => entry.Id == "disabled");
        Assert.Equal("User title", merged.Entries.Single(entry => entry.Id == "override").TitleRu);
        Assert.Contains(merged.Entries, entry => entry.Id == "new-entry");
    }

    [Fact]
    public void BuildUserOverrides_StoresOnlyDifferencesAndDisabledIds()
    {
        var defaults = new RumorCatalogDocument
        {
            Entries = [Entry("same", "Same"), Entry("removed", "Removed")],
        };
        var effective = new[]
        {
            Entry("same", "Same"),
            Entry("added", "Added"),
        };

        RumorCatalogUserDocument user = RumorCatalog.BuildUserOverrides(defaults, effective);

        Assert.Single(user.Entries);
        Assert.Equal("added", user.Entries[0].Id);
        Assert.Equal(new[] { "removed" }, user.DisabledIds);
    }

    [Fact]
    public void Merge_KeepsNewBuiltInOcrAliasesForExistingUserOverride()
    {
        var defaults = new RumorCatalogDocument
        {
            Entries = [Entry("fishy", "Somethin' fishy")],
        };
        defaults.Entries[0].Phrases = ["Somethin' fishy", "Somathin ficky"];
        var user = new RumorCatalogUserDocument
        {
            Entries = [Entry("fishy", "Somethin' fishy", title: "My description")],
        };

        RumorCatalogDocument merged = RumorCatalog.Merge(defaults, user);
        RumorCatalogEntry entry = Assert.Single(merged.Entries);

        Assert.Equal("My description", entry.TitleRu);
        Assert.Contains("Somathin ficky", entry.Phrases);
    }

    [Fact]
    public void BuildUserOverrides_PersistsDisabledCustomEntry()
    {
        var custom = Entry("custom", "Custom rumor");
        custom.IsDisabled = true;

        RumorCatalogUserDocument user = RumorCatalog.BuildUserOverrides(
            new RumorCatalogDocument(),
            [custom]);

        Assert.Contains(user.Entries, entry => entry.Id == "custom");
        Assert.Contains("custom", user.DisabledIds);
    }

    private static RumorCatalogEntry Entry(string id, string phrase, string? title = null) => new()
    {
        Id = id,
        Kind = "expedition",
        Phrases = [phrase],
        TitleRu = title ?? id,
    };
}

public class RumorCatalogLegacyMigrationTests
{
    [Fact]
    public void Load_MigratesChangedLegacyCatalogIntoUserOverridesWithoutDisablingNewDefaults()
    {
        using var dir = new TempDir();
        string defaultPath = Path.Combine(dir.Path, "rumor_catalog.default.json");
        string userPath = Path.Combine(dir.Path, "rumor_catalog.user.json");
        string legacyPath = Path.Combine(dir.Path, "rumor_catalog.json");

        File.WriteAllText(defaultPath, JsonConvert.SerializeObject(new RumorCatalogDocument
        {
            Entries =
            [
                Entry("existing", "Default phrase", "Default title"),
                Entry("new-default", "New default phrase", "New default"),
            ],
        }));
        File.WriteAllText(legacyPath, JsonConvert.SerializeObject(new RumorCatalogDocument
        {
            Entries =
            [
                Entry("existing", "Default phrase", "User title"),
                Entry("custom", "Custom phrase", "Custom title"),
            ],
        }));

        RumorCatalog catalog = RumorCatalog.Load(defaultPath, userPath);

        Assert.True(File.Exists(userPath));
        Assert.Equal("User title", catalog.Entries.Single(entry => entry.Id == "existing").TitleRu);
        Assert.Contains(catalog.Entries, entry => entry.Id == "custom");
        Assert.Contains(catalog.Entries, entry => entry.Id == "new-default");
    }

    private static RumorCatalogEntry Entry(string id, string phrase, string title) => new()
    {
        Id = id,
        Kind = "expedition",
        Phrases = [phrase],
        TitleRu = title,
    };
}

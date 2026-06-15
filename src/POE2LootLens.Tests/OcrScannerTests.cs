using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class OcrScannerTests
{
    [Theory]
    [InlineData("Support: Scattering Flame", "support scattering flame")]
    [InlineData("Chilling Flux", "chilling flux")]
    [InlineData("Skill: Grip Filters", "skill grip filters")]
    [InlineData("  VERISIUM FLUX  ", "verisium flux")]
    [InlineData("Rune-of-Aldur", "rune of aldur")]
    [InlineData("Большая сфера превращения", "большая сфера превращения")]
    [InlineData("НЕОГРАНЁННЫЙ САМОЦВЕТ", "неограненный самоцвет")]
    public void NormalizeName_ProducesExpectedKey(string input, string expected)
    {
        Assert.Equal(expected, OcrScanner.NormalizeName(input));
    }

    [Fact]
    public void NormalizeName_EmptyAfterStrip_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OcrScanner.NormalizeName(":::---"));
    }

    [Fact]
    public void NormalizeName_CollapseWhitespace()
    {
        Assert.Equal("a b c", OcrScanner.NormalizeName("a   b   c"));
    }

    [Theory]
    [InlineData("14x adaptive alloy", "adaptive alloy")]
    [InlineData("1 mystic alloy", "mystic alloy")]
    [InlineData("3x rune of aldur", "rune of aldur")]
    [InlineData("adaptive alloy", "adaptive alloy")]
    [InlineData("1 1 adaptive alloy", "adaptive alloy")]
    [InlineData("e l8 n 1x the greatwolf s rune of willpower", "the greatwolf s rune of willpower")]
    [InlineData("oa a 1x greater orb of transmutation", "greater orb of transmutation")]
    [InlineData("b l38 unique quarterstaff", "unique quarterstaff")]
    [InlineData("krogin 1x ancient rune of decay", "ancient rune of decay")]
    [InlineData("hefod 1x ancient rune of the titan", "ancient rune of the titan")]
    [InlineData("nerog 11x ancient rune of discovery", "ancient rune of discovery")]
    [InlineData("ancient rune of shattering", "ancient rune of shattering")]
    [InlineData("3х большая сфера превращения", "большая сфера превращения")]
    [InlineData("12× древняя руна распада", "древняя руна распада")]
    public void StripLeadingNoise_RemovesQuantityPrefix(string input, string expected)
    {
        var normalized = OcrScanner.NormalizeName(input);
        Assert.Equal(expected, OcrScanner.StripLeadingNoise(normalized));
    }

    [Theory]
    [InlineData("14x adaptive alloy", 14)]
    [InlineData("3x rune of aldur", 3)]
    [InlineData("1 mystic alloy", 1)]
    [InlineData("adaptive alloy", 1)]
    [InlineData("e l8 n 1x the greatwolf", 1)]
    [InlineData("krogin 2x ancient rune of decay", 2)]
    [InlineData("nerog 11x ancient rune of discovery", 11)]
    [InlineData("oa a 1x greater orb of transmutation", 1)]
    [InlineData("warding rune of protection i", 1)]
    [InlineData("3х большая сфера превращения", 3)]
    [InlineData("12× древняя руна распада", 12)]
    public void ExtractMultiplier_ReadsQuantity(string input, int expected)
    {
        var normalized = OcrScanner.NormalizeName(input);
        Assert.Equal(expected, OcrScanner.ExtractMultiplier(normalized));
    }
}

public class OcrScannerLocalizationTests
{
    [Theory]
    [InlineData("ru", "rus")]
    [InlineData("ru-RU", "rus")]
    [InlineData("en", "eng")]
    [InlineData("", "eng")]
    public void ResolvePriceOcrLanguage_UsesSingleClientLanguage(string language, string expected)
    {
        Assert.Equal(expected, OcrScanner.ResolvePriceOcrLanguage(language));
    }

    [Theory]
    [InlineData("ru", "en", "rus,eng")]
    [InlineData("ru", "ru", "rus")]
    [InlineData("en", "en+ru", "eng,rus")]
    public void RequiredOcrModels_KeepPriceAndRumorLanguagesIndependent(
        string gameLanguage,
        string rumorLanguage,
        string expected)
    {
        Assert.Equal(
            expected.Split(','),
            OcrDataManager.ResolveRequiredLanguages(gameLanguage, rumorLanguage));
    }

    [Theory]
    [InlineData("Руна охоты (1)", "Руна охоты", 1)]
    [InlineData("Стекольная масса (3).", "Стекольная масса", 3)]
    [InlineData("Сага Медведя (1) 18", "Сага Медведя", 1)]
    [InlineData("Большая сфера царей (3) |\"", "Большая сфера царей", 3)]
    [InlineData("Чародейский расплав (Уровень 15) {1)", "Чародейский расплав (Уровень 15)", 1)]
    [InlineData("Сфера отмены [2}", "Сфера отмены", 2)]
    [InlineData("Сфера хаоса {3]", "Сфера хаоса", 3)]
    [InlineData("Точильный камень (6)", "Точильный камень", 6)]
    [InlineData("Руна акробатики 3", "Руна акробатики 3", 1)]
    public void ExtractTrailingBundleCount_RequiresBrackets(
        string raw,
        string expectedText,
        int expectedCount)
    {
        int count = OcrScanner.ExtractTrailingBundleCount(raw, out var text);

        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedText, text);
    }
}

public class OcrScannerGeometryTests
{
    [Fact]
    public void EdgeRow_LowConfidenceGarbage_IsIgnored()
    {
        var band = new OcrRowBand(634, 734);
        var row = new OcrRow("нему оз ос", "НЕМУ. оз Ос", 708, Confidence: 14f);

        Assert.True(OcrScanner.ShouldIgnoreEdgeRow(band, 734, row));
    }

    [Fact]
    public void EdgeRow_HighConfidenceKnownName_IsKept()
    {
        var band = new OcrRowBand(676, 734);
        var row = new OcrRow("легкий сплав", "Лёгкий сплав (1)", 705, Confidence: 83f);

        Assert.False(OcrScanner.ShouldIgnoreEdgeRow(band, 734, row));
    }


    [Fact]
    public void EdgeRow_LongLowConfidenceReward_IsKept()
    {
        var band = new OcrRowBand(676, 734);
        var row = new OcrRow(
            "уникальная двуручная булава",
            "Уникальная двуручная булава",
            705,
            Confidence: 24f);

        Assert.False(OcrScanner.ShouldIgnoreEdgeRow(band, 734, row));
    }

    [Fact]
    public void CoherentGeometryShift_IsAcceptedImmediately()
    {
        OcrRowBand[] previous = [new(5, 67), new(67, 134), new(134, 193)];
        OcrRowBand[] shifted = [new(24, 86), new(86, 153), new(153, 212)];
        OcrRowBand[] deformed = [new(24, 86), new(100, 167), new(153, 212)];

        Assert.True(OcrScanner.HasCoherentShift(previous, shifted));
        Assert.False(OcrScanner.HasCoherentShift(previous, deformed));
    }

    [Fact]
    public void DetectRowBands_SupportsExpandedRows()
    {
        using var bitmap = new System.Drawing.Bitmap(
            500,
            230,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(225, 210, 170));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(55, 45, 35), 3);
        graphics.DrawLine(pen, 150, 10, 490, 10);
        graphics.DrawLine(pen, 150, 110, 490, 110);
        graphics.DrawLine(pen, 150, 210, 490, 210);

        var bands = OcrScanner.DetectRowBands(bitmap);

        Assert.Equal(2, bands.Count);
        Assert.All(bands, band => Assert.InRange(band.Height, 94, 106));
    }


    [Fact]
    public void DetectRowBands_SupportsMixedNormalAndExpandedRows()
    {
        using var bitmap = new System.Drawing.Bitmap(
            500,
            260,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(225, 210, 170));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(55, 45, 35), 3);
        graphics.DrawLine(pen, 150, 10, 490, 10);
        graphics.DrawLine(pen, 150, 70, 490, 70);
        graphics.DrawLine(pen, 150, 180, 490, 180);
        graphics.DrawLine(pen, 150, 240, 490, 240);

        var bands = OcrScanner.DetectRowBands(bitmap);

        Assert.Equal(3, bands.Count);
        Assert.InRange(bands[0].Height, 54, 66);
        Assert.InRange(bands[1].Height, 104, 116);
        Assert.InRange(bands[2].Height, 54, 66);
    }

    [Fact]
    public void DetectRowBands_IgnoresDarkDecorativeHeader()
    {
        using var bitmap = new System.Drawing.Bitmap(
            500,
            170,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(225, 210, 170));
        graphics.FillRectangle(
            System.Drawing.Brushes.DimGray,
            new System.Drawing.Rectangle(0, 10, 500, 80));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(55, 45, 35), 3);
        graphics.DrawLine(pen, 150, 10, 490, 10);
        graphics.DrawLine(pen, 150, 90, 490, 90);
        graphics.DrawLine(pen, 150, 150, 490, 150);

        var bands = OcrScanner.DetectRowBands(bitmap);

        Assert.Single(bands);
        Assert.InRange(bands[0].Top, 84, 96);
        Assert.InRange(bands[0].Bottom, 144, 156);
    }

    [Fact]
    public void DetectRowBands_KeepsFinalVisibleRowWithoutBottomSeparator()
    {
        using var bitmap = new System.Drawing.Bitmap(
            500,
            190,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(225, 210, 170));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(55, 45, 35), 3);
        graphics.DrawLine(pen, 150, 10, 490, 10);
        graphics.DrawLine(pen, 150, 70, 490, 70);
        graphics.DrawLine(pen, 150, 130, 490, 130);
        // Model visible glyph ink in the tightly cropped final row. Blank parchment after the last
        // separator must not create a synthetic row, but a readable row without its lower border must.
        using var glyphBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(55, 45, 35));
        for (int index = 0; index < 14; index++)
            graphics.FillRectangle(glyphBrush, 260 + index * 11, 151 + index % 3, 5, 17);

        IReadOnlyList<OcrRowBand> bands = OcrScanner.DetectRowBands(bitmap);

        Assert.Equal(3, bands.Count);
        Assert.InRange(bands[^1].Bottom, 184, 189);
    }


    [Fact]
    public void DetectRowBands_DoesNotStretchFinalNormalRowIntoExtraCaptureTail()
    {
        using var bitmap = new System.Drawing.Bitmap(
            500,
            280,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(225, 210, 170));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(55, 45, 35), 3);
        graphics.DrawLine(pen, 150, 10, 490, 10);
        graphics.DrawLine(pen, 150, 70, 490, 70);
        graphics.DrawLine(pen, 150, 130, 490, 130);
        graphics.DrawLine(pen, 150, 190, 490, 190);
        using var glyphBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(55, 45, 35));
        for (int index = 0; index < 18; index++)
            graphics.FillRectangle(glyphBrush, 250 + index * 9, 211 + index % 3, 5, 17);

        IReadOnlyList<OcrRowBand> bands = OcrScanner.DetectRowBands(bitmap);

        Assert.Equal(4, bands.Count);
        Assert.InRange(bands[^1].Height, 54, 66);
        Assert.InRange(bands[^1].Bottom, 244, 256);
    }

    [Fact]
    public void DetectRowBands_KeepsExpandedFinalRowWithoutBottomSeparator()
    {
        using var bitmap = new System.Drawing.Bitmap(
            500,
            310,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(225, 210, 170));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(55, 45, 35), 3);
        graphics.DrawLine(pen, 150, 10, 490, 10);
        graphics.DrawLine(pen, 150, 70, 490, 70);
        graphics.DrawLine(pen, 150, 130, 490, 130);
        graphics.DrawLine(pen, 150, 190, 490, 190);

        // Expanded rewards have their useful text in the lower lane. The normal-height prefix of
        // the row is intentionally blank in the reward text zone, so the detector must keep the
        // full synthetic band instead of truncating it to the preceding 60 px cadence.
        using var glyphBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(55, 45, 35));
        for (int index = 0; index < 18; index++)
            graphics.FillRectangle(glyphBrush, 250 + index * 9, 276 + index % 3, 5, 17);

        IReadOnlyList<OcrRowBand> bands = OcrScanner.DetectRowBands(bitmap);

        Assert.Equal(4, bands.Count);
        Assert.InRange(bands[^1].Height, 112, 122);
        Assert.InRange(bands[^1].Bottom, 304, 309);
    }

    [Fact]
    public void MalformedBundleSuffix_StripsOnlyForExactKnownBase()
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "руна разума",
        };

        bool recovered = OcrScanner.TryStripMalformedBundleSuffix(
            "_ Руна разума (9",
            known,
            out var text);

        Assert.True(recovered);
        Assert.Equal("_ Руна разума", text);
    }

    [Fact]
    public void MalformedBundleSuffix_DoesNotTrustUnknownBase()
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "руна разума",
        };

        bool recovered = OcrScanner.TryStripMalformedBundleSuffix(
            "Неизвестный предмет (9",
            known,
            out var text);

        Assert.False(recovered);
        Assert.Equal("Неизвестный предмет (9", text);
    }
    [Fact]
    public void DamagedClosingBundleSuffix_StripsOnlyForExactKnownBase()
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "сфера астромантии",
        };

        bool recovered = OcrScanner.TryStripDamagedClosingBundleSuffix(
            "Сфера астромантии 6)",
            known,
            out var text);

        Assert.True(recovered);
        Assert.Equal("Сфера астромантии", text);
    }

    [Fact]
    public void DamagedClosingBundleSuffix_DoesNotStripUnknownBase()
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "сфера астромантии",
        };

        bool recovered = OcrScanner.TryStripDamagedClosingBundleSuffix(
            "Неизвестная сфера 6)",
            known,
            out var text);

        Assert.False(recovered);
        Assert.Equal("Неизвестная сфера 6)", text);
    }

    [Fact]
    public void DetectRowBands_TreatsGoldHoverOutlineAsSeparator()
    {
        using var bitmap = new System.Drawing.Bitmap(
            500,
            200,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(225, 210, 170));
        using var darkPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(55, 45, 35), 3);
        using var hoverPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(180, 145, 90), 3);
        graphics.DrawLine(darkPen, 150, 10, 490, 10);
        graphics.DrawLine(hoverPen, 150, 70, 490, 70);
        graphics.DrawLine(darkPen, 150, 130, 490, 130);
        graphics.DrawLine(darkPen, 150, 190, 490, 190);

        IReadOnlyList<OcrRowBand> bands = OcrScanner.DetectRowBands(bitmap);

        Assert.Equal(3, bands.Count);
        Assert.All(bands, band => Assert.InRange(band.Height, 54, 66));
    }

    [Fact]
    public void DetectRowBands_MarksOnlyFullyGoldOutlinedRowAsHovered()
    {
        using var bitmap = new System.Drawing.Bitmap(
            500,
            200,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(225, 210, 170));
        using var darkPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(55, 45, 35), 3);
        using var hoverPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(180, 145, 90), 3);
        graphics.DrawLine(hoverPen, 150, 10, 490, 10);
        graphics.DrawLine(hoverPen, 150, 70, 490, 70);
        graphics.DrawLine(darkPen, 150, 130, 490, 130);
        graphics.DrawLine(darkPen, 150, 190, 490, 190);

        IReadOnlyList<OcrRowBand> bands = OcrScanner.DetectRowBands(bitmap);

        Assert.Equal(3, bands.Count);
        Assert.True(bands[0].HoverHighlighted);
        Assert.False(bands[1].HoverHighlighted);
        Assert.False(bands[2].HoverHighlighted);
    }

    [Theory]
    [InlineData(true, 9, false, 58f, false)]
    [InlineData(true, 6, true, 95f, true)]
    [InlineData(true, 3, false, 50f, true)]
    [InlineData(true, 9, false, 80f, true)]
    [InlineData(false, 9, false, 58f, true)]
    public void HoverQuantityVerdict_RejectsOnlyLargeWeakDisagreement(
        bool highlighted,
        int bundleCount,
        bool agreement,
        float confidence,
        bool expected)
    {
        Assert.Equal(
            expected,
            OcrScanner.IsHoverQuantityTrusted(
                highlighted,
                bundleCount,
                agreement,
                confidence));
    }

}

public class OcrQuantityRegressionTests
{
    [Theory]
    [InlineData("Чародейский расплав (Уровень 10} (1)", "Чародейский расплав (Уровень 10}", 1)]
    [InlineData("Чародейский расплав (Уровень 18) (1)", "Чародейский расплав (Уровень 18)", 1)]
    [InlineData("Неогранённый камень умения (уровень 20) (3)", "Неогранённый камень умения (уровень 20)", 3)]
    [InlineData("Чародейский расплав (Уровень 8) (1)", "Чародейский расплав (Уровень 8)", 1)]
    public void FinalBracketGroup_IsTheOnlyBundleQuantity(
        string raw,
        string expectedName,
        int expectedCount)
    {
        int count = OcrScanner.ExtractTrailingBundleCount(raw, out string name);
        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void LabelPrefixes_GenerateSafeFallbackCandidates()
    {
        var candidates = ScanEngine.BuildLookupCandidates("умение дождь клинков");
        Assert.Contains(candidates, candidate =>
            candidate.Name == "дождь клинков" && candidate.Kind == "label");
    }

    [Fact]
    public void SplitSkillLabel_GeneratesSemanticNameCandidate()
    {
        var candidates = ScanEngine.BuildLookupCandidates("умен ие дождь клинков");

        Assert.Contains(candidates, candidate =>
            candidate.Name == "дождь клинков" && candidate.Kind == "label");
        Assert.True(ScanEngine.IsKnownUnpricedReward("умен ие дождь клинков"));
    }

    [Fact]
    public void SplitWord_GeneratesRepairCandidateWithoutReplacingOriginal()
    {
        var candidates = ScanEngine.BuildLookupCandidates("вщозвышенн ый сплав");
        Assert.Equal("вщозвышенн ый сплав", candidates[0].Name);
        Assert.Contains(candidates, candidate => candidate.Name == "вщозвышенный сплав");
    }

    [Fact]
    public void LabelAndSplitWord_GenerateCombinedCandidate()
    {
        var candidates = ScanEngine.BuildLookupCandidates("умение возвышенн ый сплав");

        Assert.Equal("умение возвышенн ый сплав", candidates[0].Name);
        Assert.Contains(candidates, candidate =>
            candidate.Name == "возвышенный сплав" &&
            candidate.Kind == "label-repair");
    }

    [Fact]
    public void ThaumaturgicFluxMissingSyllable_GeneratesLevelPreservingCandidate()
    {
        var candidates = ScanEngine.BuildLookupCandidates("чародейский сплав уровень 19");

        Assert.Equal("чародейский сплав уровень 19", candidates[0].Name);
        Assert.Contains(candidates, candidate =>
            candidate.Name == "чародейский расплав уровень 19" &&
            candidate.Kind == "ocr-repair");
    }

    [Theory]
    [InlineData("чародеискии расплав уровень 15")]
    [InlineData("ародеискии расплав уровень 15")]
    public void ThaumaturgicFluxFamilyTypos_GenerateCanonicalCandidate(string input)
    {
        IReadOnlyList<(string Name, string Kind)> candidates = ScanEngine.BuildLookupCandidates(input);

        Assert.Contains(candidates, candidate =>
            candidate.Name == "чародейский расплав уровень 15" &&
            candidate.Kind.Contains("ocr-family-repair", StringComparison.Ordinal));
    }

    [Fact]
    public void ThaumaturgicFluxSequence_RepairsAmbiguousAndLetterLevels()
    {
        OcrRow[] rows =
        [
            new("чародеискии расплав уровень 15", "", 70),
            new("чародейский расплав уровень 14", "", 130),
            new("чародейский расплав уровень 13", "", 190),
            new("чародейский расплав уровень 12", "", 250),
            new("чародейский расплав уровень 1 6", "", 310),
            new("чародейский расплав уровень 10", "", 370),
            new("чародейский расплав уровень 9", "", 430),
            new("чародейский расплав уровень 8", "", 490),
            new("чародейский расплав уровень т", "", 550),
        ];

        IReadOnlyDictionary<int, string> repaired = ScanEngine.InferThaumaturgicFluxNames(rows);

        Assert.Equal("чародейский расплав уровень 15", repaired[70]);
        Assert.Equal("чародейский расплав уровень 11", repaired[310]);
        Assert.Equal("чародейский расплав уровень 7", repaired[550]);
    }

    [Fact]
    public void ThaumaturgicFluxSequence_DoesNotRewriteLevelRowsOutsideAnchors()
    {
        OcrRow[] rows =
        [
            new("неограненный камень умения уровень 20", "", 10),
            new("чародейский расплав уровень 15", "", 70),
            new("уровень 14", "", 130),
            new("чародейский расплав уровень 13", "", 190),
            new("неограненный камень поддержки уровень 3", "", 250),
        ];

        IReadOnlyDictionary<int, string> repaired = ScanEngine.InferThaumaturgicFluxNames(rows);

        Assert.False(repaired.ContainsKey(10));
        Assert.Equal("чародейский расплав уровень 14", repaired[130]);
        Assert.False(repaired.ContainsKey(250));
    }

    [Fact]
    public void VerifiedDamagedGlyphSuffix_StripsOnlyAnExactKnownBase()
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "призма камнереза",
        };

        Assert.True(OcrScanner.TryStripVerifiedBundleGlyphSuffix(
            "Призма камнереза 6;",
            known,
            out string recovered));
        Assert.Equal("Призма камнереза", recovered);

        Assert.False(OcrScanner.TryStripVerifiedBundleGlyphSuffix(
            "Неизвестная награда 6;",
            known,
            out _));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    public void DamagedSuffixRecovery_AcceptsOnlySmallVerifiedCounts(
        int verified,
        bool expected)
    {
        // The recovery path is intentionally capped at three. Intact full-line suffixes such as
        // "(6)" are parsed directly and never reach this rule; malformed "6);" may actually be
        // a misread "(1)" and must fail safe to one item.
        Assert.Equal(expected, OcrScanner.IsSafeRecoveredBundleCount(verified));
    }

    [Fact]
    public void FastBundleVerdict_CorrectsWeakOneVersusNineConflictWithoutExtraOcr()
    {
        var selected = new OcrRow(
            "древняя руна вражды",
            "Древняя руна вражды (9)",
            100,
            9,
            55f,
            "wide-binary",
            1,
            9,
            false);
        OcrRow result = OcrScanner.ResolveFastBundleCandidate(
            selected,
            [
                selected,
                new OcrRow(
                    "древняя руна вражды",
                    "Древняя руна вражды (1)",
                    100,
                    1,
                    70f,
                    "wide-gray",
                    1,
                    1,
                    false),
            ]);

        Assert.Equal(1, result.BundleCount);
        Assert.Equal(1, result.Multiplier);
        Assert.EndsWith("+qty-1v9", result.Variant);
    }

    [Fact]
    public void FastBundleVerdict_KeepsSupportedNine()
    {
        var selected = new OcrRow(
            "награда",
            "Награда (9)",
            100,
            9,
            90f,
            "wide-gray",
            1,
            9,
            false);
        OcrRow result = OcrScanner.ResolveFastBundleCandidate(
            selected,
            [
                selected,
                selected with { Variant = "wide-binary", Confidence = 88f },
                selected with
                {
                    RawText = "Награда (1)",
                    Variant = "right-gray",
                    Confidence = 95f,
                    BundleCount = 1,
                    Multiplier = 1,
                },
            ]);

        Assert.Equal(9, result.BundleCount);
        Assert.Equal(9, result.Multiplier);
    }

    [Fact]
    public void FastBundleVerdict_UsesExistingVariantMajority()
    {
        var selected = new OcrRow(
            "деталь доспеха",
            "Деталь доспеха (6)",
            100,
            6,
            90f,
            "wide-gray",
            1,
            6,
            false);
        OcrRow result = OcrScanner.ResolveFastBundleCandidate(
            selected,
            [
                selected,
                selected with
                {
                    RawText = "Деталь доспеха (4)",
                    Variant = "wide-binary",
                    BundleCount = 4,
                    Multiplier = 4,
                },
                selected with
                {
                    RawText = "Деталь доспеха [4]",
                    Variant = "right-gray",
                    BundleCount = 4,
                    Multiplier = 4,
                },
            ]);

        Assert.Equal(4, result.BundleCount);
        Assert.Equal(4, result.Multiplier);
        Assert.EndsWith("+qty-vote", result.Variant);
    }

    [Fact]
    public void FastBundleVerdict_DoesNotGuessWithoutExplicitCompetingSuffix()
    {
        var selected = new OcrRow(
            "древняя руна вражды",
            "Древняя руна вражды (9)",
            100,
            9,
            55f,
            "wide-binary",
            1,
            9,
            false);
        OcrRow result = OcrScanner.ResolveFastBundleCandidate(
            selected,
            [
                selected,
                selected with
                {
                    RawText = "Древняя руна вражды",
                    Variant = "wide-gray",
                    BundleCount = 1,
                    Multiplier = 1,
                },
            ]);

        Assert.Equal(9, result.BundleCount);
    }

    [Fact]
    public void LevelNumber_NeverBecomesEffectiveQuantity()
    {
        const string raw = "Чародейский расплав (Уровень 18) (1)";
        int bundle = OcrScanner.ExtractTrailingBundleCount(raw, out string name);
        var row = new OcrRow(
            OcrScanner.NormalizeName(name),
            raw,
            CenterY: 100,
            Multiplier: bundle,
            Confidence: 99,
            Variant: "test",
            LeadingMultiplier: 1,
            BundleCount: bundle,
            VariantAgreement: true);

        Assert.Equal(1, bundle);
        Assert.Equal(1, ScanEngine.ResolveEffectiveMultiplier(row, exactIdentity: true));
    }
}

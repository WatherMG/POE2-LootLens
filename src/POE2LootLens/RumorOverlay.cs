using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Poe2LootLens;

internal sealed record RumorDisplayItem(
    RumorMatch Match,
    bool IsCurrent,
    bool IsPending = false,
    int ConfirmationProgress = 0,
    int ConfirmationRequired = 0);

internal sealed record RumorOverlayState(
    Point Anchor,
    IReadOnlyList<RumorDisplayItem> Items,
    bool Scanning,
    bool PanelDetected,
    int FailedAttempts,
    bool Pinned,
    int ScanCount,
    DateTime LastUpdatedAt,
    string Diagnostic = "",
    int IslandIndex = 0,
    int IslandCount = 0,
    int IslandScanCount = 0,
    string SortMode = "tier",
    IReadOnlyList<string>? CategoryOrder = null,
    string Language = "ru",
    string UiLanguage = "ru",
    Rectangle PanelBounds = default);

internal sealed class RumorOverlayForm : Form
{
    private RumorOverlayState _state = new(Point.Empty, [], false, false, 0, false, 0, DateTime.MinValue);
    private readonly System.Windows.Forms.Timer _animationTimer;

    private readonly Font _titleFont = AppFonts.CreateDrawingFont(17f, FontStyle.Bold);
    private readonly Font _statusFont = AppFonts.CreateDrawingFont(12f);
    private readonly Font _summaryFont = AppFonts.CreateDrawingFont(14f, FontStyle.Bold);
    private readonly Font _summarySmallFont = AppFonts.CreateDrawingFont(12f, FontStyle.Bold);
    private readonly Font _itemTitleFont = AppFonts.CreateDrawingFont(16f, FontStyle.Bold);
    private readonly Font _bodyFont = AppFonts.CreateDrawingFont(14f);
    private readonly Font _bodyBoldFont = AppFonts.CreateDrawingFont(14f, FontStyle.Bold);
    private readonly Font _labelFont = AppFonts.CreateDrawingFont(10.5f, FontStyle.Bold);
    private readonly Font _tierFont = AppFonts.CreateDrawingFont(15f, FontStyle.Bold);
    private readonly Font _diagnosticFont = new("Consolas", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);

    private readonly Bitmap? _expeditionIcon;
    private readonly Bitmap? _bossIcon;
    private readonly Bitmap? _uniqueIcon;

    private Action _resetRequested;
    private Action _hideRequested;
    private Action _togglePinRequested;
    private Action _previousIslandRequested;
    private Action _nextIslandRequested;

    private Rectangle _resetButton;
    private Rectangle _pinButton;
    private Rectangle _diagnosticButton;
    private Rectangle _closeButton;
    private Rectangle _previousButton;
    private Rectangle _nextButton;
    private int _visibleItemLimit = MaxVisibleItems;
    private bool _hasPinnedPosition;
    private int _lastAutoIslandIndex = -1;
    private Rectangle _lastAutoBounds = Rectangle.Empty;
    private Rectangle _lastAutoPanelBounds = Rectangle.Empty;
    private bool _showDiagnostics;
    private string _hoveredButton = string.Empty;

    private const int CardWidth = 600;
    private const int HeaderHeight = 62;
    private const int SummaryHeight = 66;
    private const int ItemHeight = 116;
    private const int FooterHeight = 38;
    private const int DiagnosticHeight = 132;
    private const int MaxVisibleItems = 6;
    private const int IconSize = 46;

    private string T(string ru, string en) =>
        string.Equals(_state.UiLanguage, "en", StringComparison.OrdinalIgnoreCase) ? en : ru;

    public RumorOverlayForm(
        Action resetRequested,
        Action hideRequested,
        Action togglePinRequested,
        Action previousIslandRequested,
        Action nextIslandRequested)
    {
        _resetRequested = resetRequested;
        _hideRequested = hideRequested;
        _togglePinRequested = togglePinRequested;
        _previousIslandRequested = previousIslandRequested;
        _nextIslandRequested = nextIslandRequested;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(15, 20, 29);
        DoubleBuffered = true;
        Width = CardWidth;
        Height = HeaderHeight + SummaryHeight + ItemHeight + FooterHeight;

        string assetRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Rumors");
        _expeditionIcon = LoadIcon(Path.Combine(assetRoot, "expedition.png"));
        _bossIcon = LoadIcon(Path.Combine(assetRoot, "boss.png"));
        _uniqueIcon = LoadIcon(Path.Combine(assetRoot, "unique.png"));

        _animationTimer = new System.Windows.Forms.Timer { Interval = 90 };
        _animationTimer.Tick += (_, _) =>
        {
            if (_state.Scanning && Visible)
                Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            // Keep our own overlay out of screen captures. Normal positioning still avoids the
            // in-game panel; this is a second line of defence for pinned or manually moved windows.
            SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        }
        catch
        {
            // Older Windows builds may not support WDA_EXCLUDEFROMCAPTURE.
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;

        if (message.Msg == WM_NCHITTEST)
        {
            int screenX = unchecked((short)(long)message.LParam);
            int screenY = unchecked((short)((long)message.LParam >> 16));
            var client = PointToClient(new Point(screenX, screenY));
            if (_resetButton.Contains(client) ||
                _pinButton.Contains(client) ||
                _diagnosticButton.Contains(client) ||
                _closeButton.Contains(client) ||
                _previousButton.Contains(client) ||
                _nextButton.Contains(client))
            {
                message.Result = new IntPtr(HTCLIENT);
            }
            else if (client.Y >= 0 && client.Y < HeaderHeight)
            {
                // The header is draggable in both quick-preview and pinned modes. In pinned mode
                // the position is retained while browsing different ships; in quick mode the next
                // recognized ship may reposition it again.
                message.Result = new IntPtr(HTCAPTION);
            }
            else
            {
                // Do not pass clicks through the information card. A click-through overlay made it
                // look as if the game tooltip itself was still interactive underneath the card.
                message.Result = new IntPtr(HTCLIENT);
            }
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string hovered = ButtonAt(e.Location);
        if (string.Equals(hovered, _hoveredButton, StringComparison.Ordinal))
            return;
        _hoveredButton = hovered;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredButton.Length == 0)
            return;
        _hoveredButton = string.Empty;
        Invalidate();
    }

    private string ButtonAt(Point point)
    {
        if (_resetButton.Contains(point)) return "reset";
        if (_diagnosticButton.Contains(point)) return "diagnostic";
        if (_pinButton.Contains(point)) return "pin";
        if (_closeButton.Contains(point)) return "close";
        if (_previousButton.Contains(point)) return "previous";
        if (_nextButton.Contains(point)) return "next";
        return string.Empty;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        if (_resetButton.Contains(e.Location))
            _resetRequested();
        else if (_pinButton.Contains(e.Location))
            _togglePinRequested();
        else if (_diagnosticButton.Contains(e.Location))
        {
            _showDiagnostics = !_showDiagnostics;
            ApplyState(_state);
        }
        else if (_closeButton.Contains(e.Location))
            _hideRequested();
        else if (_previousButton.Contains(e.Location))
            _previousIslandRequested();
        else if (_nextButton.Contains(e.Location))
            _nextIslandRequested();
    }

    public void SetCallbacks(
        Action resetRequested,
        Action hideRequested,
        Action togglePinRequested,
        Action previousIslandRequested,
        Action nextIslandRequested)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => SetCallbacks(
                    resetRequested,
                    hideRequested,
                    togglePinRequested,
                    previousIslandRequested,
                    nextIslandRequested));
            }
            catch { }
            return;
        }

        _resetRequested = resetRequested;
        _hideRequested = hideRequested;
        _togglePinRequested = togglePinRequested;
        _previousIslandRequested = previousIslandRequested;
        _nextIslandRequested = nextIslandRequested;
    }

    public void ApplyState(RumorOverlayState state)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => ApplyState(state)); } catch { }
            return;
        }

        bool wasPinned = _state.Pinned;
        _state = state;
        var workingArea = state.Pinned && _hasPinnedPosition
            ? Screen.FromRectangle(new Rectangle(Location, Size)).WorkingArea
            : Screen.FromPoint(state.Anchor).WorkingArea;
        int diagnosticHeight = _showDiagnostics ? DiagnosticHeight : 0;
        int availableItemHeight = Math.Max(
            ItemHeight,
            workingArea.Height - HeaderHeight - SummaryHeight - FooterHeight - diagnosticHeight - 16);
        _visibleItemLimit = Math.Clamp(availableItemHeight / ItemHeight, 1, MaxVisibleItems);
        int visibleCount = Math.Min(Math.Max(state.Items.Count, Math.Min(3, _visibleItemLimit)), _visibleItemLimit);
        var desiredSize = new Size(
            CardWidth,
            HeaderHeight + SummaryHeight + visibleCount * ItemHeight + diagnosticHeight + FooterHeight);

        if (!state.Pinned || !wasPinned || !_hasPinnedPosition)
        {
            Bounds = ChooseAutoBounds(state, desiredSize, workingArea);
        }
        else
        {
            Bounds = ClampToWorkingArea(new Rectangle(Location, desiredSize), workingArea);
        }

        _hasPinnedPosition = state.Pinned;
        if (state.Pinned)
        {
            _lastAutoIslandIndex = -1;
            _lastAutoPanelBounds = Rectangle.Empty;
        }
        UpdateRoundedRegion();

        bool shouldShow = state.PanelDetected || state.Items.Count > 0;
        if (shouldShow && !Visible)
            Show();
        else if (!shouldShow && Visible)
            Hide();

        if (state.Scanning && shouldShow)
            _animationTimer.Start();
        else
            _animationTimer.Stop();

        if (Visible)
        {
            SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Invalidate();
        }
    }

    public void HideNow()
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            try { BeginInvoke(HideNow); } catch { }
            return;
        }

        _animationTimer.Stop();
        Hide();
    }

    private Rectangle ChooseAutoBounds(RumorOverlayState state, Size desiredSize, Rectangle workingArea)
    {
        bool sameIsland = Visible &&
                          _lastAutoIslandIndex == state.IslandIndex &&
                          !_lastAutoBounds.IsEmpty;
        Point stableLocation = Location.IsEmpty ? _lastAutoBounds.Location : Location;
        var stableCandidate = new Rectangle(stableLocation, desiredSize);
        bool panelMovedMaterially = HasMaterialPanelMove(_lastAutoPanelBounds, state.PanelBounds);
        bool stablePositionNowOverlapsPanel = !state.PanelBounds.IsEmpty &&
                                              stableCandidate.IntersectsWith(state.PanelBounds);

        // Position is selected once per island and remains fixed while only OCR data or a few pixels
        // of detector jitter change. Reposition only when the actual game panel moved substantially or
        // the resized card would overlap it. This removes corner flashes without making placement stale
        // after a genuine tooltip flip or resolution/window move.
        if (sameIsland && !panelMovedMaterially && !stablePositionNowOverlapsPanel)
        {
            _lastAutoBounds = ClampToWorkingArea(stableCandidate, workingArea);
            if (!state.PanelBounds.IsEmpty)
                _lastAutoPanelBounds = state.PanelBounds;
            return _lastAutoBounds;
        }

        Rectangle next = ClampToWorkingArea(
            CalculateBounds(state.Anchor, state.PanelBounds, desiredSize),
            workingArea);
        _lastAutoIslandIndex = state.IslandIndex;
        _lastAutoBounds = next;
        _lastAutoPanelBounds = state.PanelBounds;
        return next;
    }

    internal static bool HasMaterialPanelMove(Rectangle previous, Rectangle current)
    {
        if (previous.IsEmpty || current.IsEmpty)
            return false;

        int previousCenterX = previous.Left + previous.Width / 2;
        int previousCenterY = previous.Top + previous.Height / 2;
        int currentCenterX = current.Left + current.Width / 2;
        int currentCenterY = current.Top + current.Height / 2;
        return Math.Abs(previousCenterX - currentCenterX) > 72 ||
               Math.Abs(previousCenterY - currentCenterY) > 72 ||
               Math.Abs(previous.Width - current.Width) > 64 ||
               Math.Abs(previous.Height - current.Height) > 64;
    }

    public void PrepareForCapture(Point cursor)
    {
        // Intentionally no-op. Moving the overlay before every capture caused visible corner
        // flicker. Windows capture exclusion plus stable placement after recognition is less noisy.
    }

    public void AvoidPanel(Point anchor, Rectangle panelBounds)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => AvoidPanel(anchor, panelBounds)); } catch { }
            return;
        }
        if (!Visible || panelBounds.IsEmpty || _state.Pinned || !Bounds.IntersectsWith(panelBounds))
            return;

        Bounds = CalculateBounds(anchor, panelBounds, Size);
        _lastAutoBounds = Bounds;
    }

    internal static Rectangle CalculateBounds(Point anchor, Rectangle panelBounds, Size size)
    {
        Rectangle workingArea = panelBounds.IsEmpty
            ? Screen.FromPoint(anchor).WorkingArea
            : Screen.FromRectangle(panelBounds).WorkingArea;
        return CalculateBoundsWithinWorkingArea(anchor, panelBounds, size, workingArea);
    }

    internal static Rectangle CalculateBoundsWithinWorkingArea(
        Point anchor,
        Rectangle panelBounds,
        Size size,
        Rectangle workingArea)
    {

        if (panelBounds.IsEmpty)
        {
            const int horizontalGap = 110;
            const int verticalOffset = 360;
            int fallbackLeft = anchor.X + horizontalGap;
            if (fallbackLeft + size.Width > workingArea.Right - 8)
                fallbackLeft = anchor.X - horizontalGap - size.Width;
            return ClampToWorkingArea(
                new Rectangle(fallbackLeft, anchor.Y - verticalOffset, size.Width, size.Height),
                workingArea);
        }

        const int gap = 18;
        int centeredTop = panelBounds.Top + (panelBounds.Height - size.Height) / 2;
        int centeredLeft = panelBounds.Left + (panelBounds.Width - size.Width) / 2;
        Rectangle[] candidates =
        [
            new(panelBounds.Right + gap, centeredTop, size.Width, size.Height),
            new(panelBounds.Left - gap - size.Width, centeredTop, size.Width, size.Height),
            new(centeredLeft, panelBounds.Bottom + gap, size.Width, size.Height),
            new(centeredLeft, panelBounds.Top - gap - size.Height, size.Width, size.Height),
            new(panelBounds.Right + gap, panelBounds.Top, size.Width, size.Height),
            new(panelBounds.Right + gap, panelBounds.Bottom - size.Height, size.Width, size.Height),
            new(panelBounds.Left - gap - size.Width, panelBounds.Top, size.Width, size.Height),
            new(panelBounds.Left - gap - size.Width, panelBounds.Bottom - size.Height, size.Width, size.Height),
        ];

        var anchorGuard = new Rectangle(anchor.X - 58, anchor.Y - 58, 116, 116);
        Rectangle best = Rectangle.Empty;
        double bestScore = double.MaxValue;
        foreach (Rectangle candidate in candidates)
        {
            Rectangle clamped = ClampToWorkingArea(candidate, workingArea);
            Rectangle overlap = Rectangle.Intersect(clamped, panelBounds);
            long overlapArea = (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
            Rectangle anchorOverlap = Rectangle.Intersect(clamped, anchorGuard);
            long anchorOverlapArea =
                (long)Math.Max(0, anchorOverlap.Width) * Math.Max(0, anchorOverlap.Height);
            long clampDistance = Math.Abs(clamped.Left - candidate.Left) + Math.Abs(clamped.Top - candidate.Top);
            long anchorDistance = Math.Abs((clamped.Left + clamped.Width / 2) - anchor.X) +
                                  Math.Abs((clamped.Top + clamped.Height / 2) - anchor.Y);

            // Any overlap with the game tooltip dominates the score. Among non-overlapping
            // placements, prefer a candidate that required little clamping and remains near the ship.
            double score = overlapArea * 1_000_000d +
                           anchorOverlapArea * 500_000d +
                           clampDistance * 10_000d +
                           anchorDistance;
            if (score >= bestScore)
                continue;
            bestScore = score;
            best = clamped;
        }

        return best.IsEmpty
            ? ClampToWorkingArea(
                new Rectangle(anchor.X + gap, anchor.Y - size.Height / 2, size.Width, size.Height),
                workingArea)
            : best;
    }


    private static Rectangle ClampToWorkingArea(Rectangle bounds, Rectangle workingArea)
    {
        int left = Math.Clamp(
            bounds.Left,
            workingArea.Left + 8,
            Math.Max(workingArea.Left + 8, workingArea.Right - bounds.Width - 8));
        int top = Math.Clamp(
            bounds.Top,
            workingArea.Top + 8,
            Math.Max(workingArea.Top + 8, workingArea.Bottom - bounds.Height - 8));
        return new Rectangle(left, top, bounds.Width, bounds.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var background = new SolidBrush(Color.FromArgb(250, 15, 20, 29));
        using var border = new Pen(Color.FromArgb(125, 92, 121, 158), 1f);
        using var panelPath = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 14);
        graphics.FillPath(background, panelPath);
        graphics.DrawPath(border, panelPath);

        DrawHeader(graphics);
        DrawSummary(graphics);
        DrawItems(graphics);
        DrawDiagnostics(graphics);
        DrawFooter(graphics);
    }

    private void DrawHeader(Graphics graphics)
    {
        using var titleBrush = new SolidBrush(Color.FromArgb(245, 244, 248, 253));
        using var statusBrush = new SolidBrush(Color.FromArgb(175, 158, 177, 201));
        graphics.DrawString(T("Слухи об острове", "Island rumors"), _titleFont, titleBrush, 18, 10);

        string status = _state.Scanning
            ? T("сканирование изменившегося окна…", "scanning changed panel…")
            : _state.Pinned
                ? T("закреплено — перетащите окно за заголовок", "pinned — drag the title to move")
                : T("история хранится до закрытия приложения", "history is kept until the app closes");
        DrawTrimmedText(graphics, status, _statusFont, statusBrush, 18, 36, Math.Max(180, Width - 330));

        _closeButton = new Rectangle(Width - 42, 13, 28, 28);
        _pinButton = new Rectangle(Width - 140, 13, 90, 28);
        _diagnosticButton = new Rectangle(Width - 214, 13, 66, 28);
        _resetButton = new Rectangle(Width - 288, 13, 66, 28);
        DrawHeaderButton(graphics, _resetButton, T("Сброс", "Reset"), "reset");
        DrawHeaderButton(graphics, _diagnosticButton, "OCR", "diagnostic", _showDiagnostics);
        DrawHeaderButton(
            graphics,
            _pinButton,
            _state.Pinned ? T("Открепить", "Unpin") : T("Закрепить", "Pin"),
            "pin",
            _state.Pinned);
        DrawHeaderButton(graphics, _closeButton, "×", "close");

        if (_state.Scanning)
            DrawSpinner(graphics, Width - 304, 27);

        using var separator = new Pen(Color.FromArgb(60, 114, 130, 153), 1f);
        graphics.DrawLine(separator, 12, HeaderHeight - 1, Width - 12, HeaderHeight - 1);
    }

    private void DrawHeaderButton(
        Graphics graphics,
        Rectangle rectangle,
        string text,
        string key,
        bool active = false)
    {
        bool hovered = string.Equals(_hoveredButton, key, StringComparison.Ordinal);
        using var path = RoundedRect(rectangle, 7);
        using var background = new SolidBrush(active
            ? Color.FromArgb(105, 41, 113, 86)
            : hovered
                ? Color.FromArgb(105, 68, 104, 151)
                : Color.FromArgb(62, 74, 91, 116));
        using var border = new Pen(active
            ? Color.FromArgb(210, 86, 208, 154)
            : hovered
                ? Color.FromArgb(225, 111, 181, 255)
                : Color.FromArgb(115, 116, 139, 168), hovered ? 1.4f : 1f);
        using var brush = new SolidBrush(active
            ? Color.FromArgb(250, 166, 245, 205)
            : Color.FromArgb(240, 236, 242, 249));
        using var font = AppFonts.CreateDrawingFont(
            text == "×" ? 17f : 11f,
            FontStyle.Bold);
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        DrawCenteredText(graphics, text, font, brush, rectangle, -1f);
    }

    private void DrawSummary(Graphics graphics)
    {
        int top = HeaderHeight;
        using var background = new SolidBrush(Color.FromArgb(55, 24, 35, 51));
        graphics.FillRectangle(background, 8, top + 5, Width - 16, SummaryHeight - 10);

        string islandText = _state.IslandCount > 0
            ? T($"Остров {_state.IslandIndex} из {_state.IslandCount}", $"Island {_state.IslandIndex} of {_state.IslandCount}")
            : T("Остров не выбран", "No island selected");
        using var strongBrush = new SolidBrush(Color.FromArgb(240, 225, 234, 246));
        graphics.DrawString(islandText, _summaryFont, strongBrush, 18, top + 12);

        var confirmedItems = _state.Items.Where(item => !item.IsPending).ToList();
        int expedition = confirmedItems.Count(item => item.Match.Entry.ParsedKind == RumorKind.Expedition);
        int bosses = confirmedItems.Count(item => item.Match.Entry.ParsedKind == RumorKind.Boss);
        int unique = confirmedItems.Count(item => item.Match.Entry.ParsedKind == RumorKind.Unique);
        int pending = _state.Items.Count(item => item.IsPending);
        using var mutedBrush = new SolidBrush(Color.FromArgb(215, 179, 195, 216));
        string counts = T(
            $"ВСЕГО {confirmedItems.Count}   ЭКСПЕДИЦИИ {expedition}   БОССЫ {bosses}   УНИК. КАРТЫ {unique}",
            $"TOTAL {confirmedItems.Count}   EXPEDITIONS {expedition}   BOSSES {bosses}   UNIQUE MAPS {unique}");
        if (pending > 0)
            counts += T($"   ПРОВЕРЯЕТСЯ {pending}", $"   CHECKING {pending}");
        DrawTrimmedText(graphics, counts, _summarySmallFont, mutedBrush, 18, top + 37, Math.Max(160, Width - 286));

        string bestTier = confirmedItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Match.Entry.Rating))
            .OrderByDescending(item => TierRank(item.Match.Entry.Rating))
            .ThenByDescending(item => item.Match.Entry.Priority)
            .Select(item => item.Match.Entry.Rating.Trim())
            .FirstOrDefault() ?? "—";
        Rectangle tierRect = new(Width - 254, top + 13, 188, 34);
        DrawTierPill(graphics, tierRect, bestTier, prefix: T("ЛУЧШИЙ ", "BEST "));

        _previousButton = new Rectangle(Width - 58, top + 15, 22, 30);
        _nextButton = new Rectangle(Width - 32, top + 15, 22, 30);
        bool canNavigate = _state.IslandCount > 1;
        DrawNavigationButton(graphics, _previousButton, "‹", canNavigate, "previous");
        DrawNavigationButton(graphics, _nextButton, "›", canNavigate, "next");
    }

    private void DrawNavigationButton(
        Graphics graphics,
        Rectangle rectangle,
        string text,
        bool enabled,
        string key)
    {
        bool hovered = enabled && string.Equals(_hoveredButton, key, StringComparison.Ordinal);
        using var path = RoundedRect(rectangle, 6);
        using var background = new SolidBrush(!enabled
            ? Color.FromArgb(25, 55, 66, 82)
            : hovered
                ? Color.FromArgb(115, 68, 104, 151)
                : Color.FromArgb(65, 74, 91, 116));
        using var border = new Pen(!enabled
            ? Color.FromArgb(45, 88, 98, 112)
            : hovered
                ? Color.FromArgb(225, 111, 181, 255)
                : Color.FromArgb(125, 116, 139, 168), hovered ? 1.4f : 1f);
        using var brush = new SolidBrush(enabled
            ? Color.FromArgb(240, 236, 242, 249)
            : Color.FromArgb(100, 180, 188, 200));
        using var font = AppFonts.CreateDrawingFont(18f, FontStyle.Bold);
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        DrawCenteredText(graphics, text, font, brush, rectangle, -2f);
    }

    private void DrawItems(Graphics graphics)
    {
        int start = HeaderHeight + SummaryHeight;
        if (_state.Items.Count == 0)
        {
            using var strongBrush = new SolidBrush(Color.FromArgb(230, 219, 228, 240));
            using var mutedBrush = new SolidBrush(Color.FromArgb(180, 165, 181, 202));
            string title = _state.FailedAttempts >= 5
                ? T("Слух не найден в справочнике", "Rumor not found in the catalog")
                : T("Окно слухов найдено — распознаю текст", "Rumor panel found — recognizing text");
            string detail = _state.FailedAttempts >= 5
                ? T("Добавьте OCR-фразу через редактор описаний слухов.", "Add an OCR phrase using the rumor catalog editor.")
                : T("Подержите курсор неподвижно ещё немного.", "Keep the cursor still a little longer.");
            graphics.DrawString(title, _itemTitleFont, strongBrush, 24, start + 28);
            graphics.DrawString(detail, _bodyFont, mutedBrush, 24, start + 58);
            if (_state.Scanning)
                DrawSpinner(graphics, Width - 42, start + 48);
            return;
        }

        int count = Math.Min(_state.Items.Count, _visibleItemLimit);
        for (int index = 0; index < count; index++)
            DrawItem(graphics, _state.Items[index], start + index * ItemHeight);
    }

    private void DrawDiagnostics(Graphics graphics)
    {
        if (!_showDiagnostics)
            return;

        int visibleCount = Math.Min(Math.Max(_state.Items.Count, Math.Min(3, _visibleItemLimit)), _visibleItemLimit);
        int top = HeaderHeight + SummaryHeight + visibleCount * ItemHeight;
        var panel = new Rectangle(8, top + 4, Width - 16, DiagnosticHeight - 8);
        using var background = new SolidBrush(Color.FromArgb(210, 8, 13, 21));
        using var border = new Pen(Color.FromArgb(110, 88, 126, 170), 1f);
        using var titleBrush = new SolidBrush(Color.FromArgb(245, 154, 205, 255));
        using var textBrush = new SolidBrush(Color.FromArgb(220, 202, 216, 234));
        using var path = RoundedRect(panel, 8);
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        graphics.DrawString(T("OCR-диагностика последнего окна", "OCR diagnostics for the last panel"), _labelFont, titleBrush, panel.Left + 12, panel.Top + 8);

        string diagnostic = PrepareDiagnosticText(_state.Diagnostic);
        var textRect = new RectangleF(
            panel.Left + 12,
            panel.Top + 29,
            panel.Width - 24,
            panel.Height - 36);
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit,
        };
        graphics.DrawString(diagnostic, _diagnosticFont, textBrush, textRect, format);
    }

    private static string PrepareDiagnosticText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "OCR ещё не выполнялся для распознанного окна.";

        var lines = value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(7)
            .Select(line => line.Length <= 118 ? line : line[..118] + "…")
            .ToArray();
        return lines.Length == 0
            ? "OCR не вернул текста."
            : string.Join(Environment.NewLine, lines);
    }

    private void DrawItem(Graphics graphics, RumorDisplayItem item, int top)
    {
        var entry = item.Match.Entry;
        Color accent = KindColor(entry.ParsedKind);
        int alpha = item.IsPending ? 185 : item.IsCurrent ? 255 : 185;
        if (item.IsPending)
            accent = Color.FromArgb(111, 164, 218);

        using var rowBackground = new SolidBrush(Color.FromArgb(item.IsPending ? 35 : item.IsCurrent ? 50 : 28, accent));
        using var accentBrush = new SolidBrush(Color.FromArgb(alpha, accent));
        using var mainBrush = new SolidBrush(Color.FromArgb(alpha, 240, 245, 251));
        using var mutedBrush = new SolidBrush(Color.FromArgb(item.IsCurrent ? 220 : 160, 166, 181, 201));
        using var separator = new Pen(Color.FromArgb(45, 115, 132, 154), 1f);

        Rectangle rowRect = new(8, top + 4, Width - 16, ItemHeight - 8);
        graphics.FillRectangle(rowBackground, rowRect);
        graphics.FillRectangle(accentBrush, 8, top + 4, 4, ItemHeight - 8);

        Bitmap? icon = GetKindIcon(entry.ParsedKind);
        DrawKindIcon(graphics, icon, entry.ParsedKind, accent, new Rectangle(20, top + 15, IconSize, IconSize), alpha);

        string category = entry.ParsedKind switch
        {
            RumorKind.Boss => T("БОСС", "BOSS"),
            RumorKind.Unique => T("УНИК. КАРТА", "UNIQUE MAP"),
            _ => T("ЭКСПЕДИЦИЯ", "EXPEDITION"),
        };
        Rectangle categoryRect = new(78, top + 11, entry.ParsedKind == RumorKind.Unique ? 98 : 82, 23);
        DrawCategoryPill(graphics, categoryRect, category, accent, alpha);

        int titleX = categoryRect.Right + 10;
        int tierWidth = 132;
        Rectangle statusRect = new(Width - tierWidth - 46, top + 13, 20, 20);
        Rectangle tierRect = new(Width - tierWidth - 20, top + 10, tierWidth, 28);
        DrawRowStatusBadge(graphics, statusRect, item.IsPending, alpha);
        if (item.IsPending)
            DrawPendingPill(graphics, tierRect, item.ConfirmationProgress, item.ConfirmationRequired, alpha);
        else
            DrawTierPill(graphics, tierRect, entry.Rating, prefix: string.Empty, alpha);

        int titleWidth = statusRect.Left - titleX - 10;
        // The headline is the rumor itself. Category and icon already communicate "Expedition",
        // "Boss" or "Unique map", so repeating the category here wastes the most valuable space.
        string displayTitle = CleanRumorTitle(entry.PrimaryPhrase);
        DrawTrimmedText(graphics, displayTitle, _itemTitleFont, mainBrush, titleX, top + 10, titleWidth);

        List<RumorField> fields = BuildDisplayFields(entry, item);
        int[] lineY = [top + 43, top + 65, top + 87];
        for (int index = 0; index < Math.Min(fields.Count, lineY.Length); index++)
        {
            RumorField field = fields[index];
            DrawLabelAndValue(
                graphics,
                field.Label,
                field.Value,
                78,
                lineY[index],
                Width - 100,
                accentBrush,
                field.Muted ? mutedBrush : mainBrush,
                field.Bold);
        }

        graphics.DrawLine(separator, 16, top + ItemHeight - 1, Width - 16, top + ItemHeight - 1);
    }

    private sealed record RumorField(string Label, string Value, bool Bold = false, bool Muted = false);

    private List<RumorField> BuildDisplayFields(RumorCatalogEntry entry, RumorDisplayItem item)
    {
        var fields = new List<RumorField>(6);
        string map = LocalizedPair(entry.MapRu, entry.MapEn, _state.Language);
        if (!string.IsNullOrWhiteSpace(map) && map != "—")
            fields.Add(new RumorField(T("ТИП КАРТЫ", "MAP TYPE"), map, Bold: true));

        string mods = LocalizedValue(entry.ModsRu, entry.ModsEn, _state.Language);
        if (!string.IsNullOrWhiteSpace(mods))
            fields.Add(new RumorField(T("МОДЫ", "MODS"), mods, Bold: true));

        string reward = LocalizedValue(entry.DetailRu, entry.DetailEn, _state.Language);
        if (!string.IsNullOrWhiteSpace(reward))
        {
            string detailLabel = entry.ParsedKind switch
            {
                RumorKind.Boss => T("БОСС", "BOSS"),
                RumorKind.Unique => T("ОСОБЕННОСТЬ", "FEATURE"),
                _ => T("НАГРАДА", "REWARD"),
            };
            fields.Add(new RumorField(detailLabel, reward));
        }

        string note = LocalizedValue(entry.NoteRu, entry.NoteEn, _state.Language);
        if (string.IsNullOrWhiteSpace(note))
            note = LocalizedValue(entry.RatingNotesRu, entry.RatingNotesEn, _state.Language);
        if (!string.IsNullOrWhiteSpace(note))
            fields.Add(new RumorField(T("ЗАМЕТКА", "NOTE"), note, Muted: true));

        string tags = FormatTags(entry.Tags);
        if (!string.IsNullOrWhiteSpace(tags))
            fields.Add(new RumorField(T("ТЕГИ", "TAGS"), tags, Muted: true));

        if (item.IsPending)
        {
            string pending = T(
                $"Проверяется… {Math.Min(item.ConfirmationProgress, item.ConfirmationRequired)}/{item.ConfirmationRequired}",
                $"Checking… {Math.Min(item.ConfirmationProgress, item.ConfirmationRequired)}/{item.ConfirmationRequired}");
            fields.Insert(0, new RumorField(T("СТАТУС", "STATUS"), pending, Muted: true));
        }

        if (fields.Count == 0)
        {
            fields.Add(new RumorField(
                T("СТАТУС", "STATUS"),
                item.IsCurrent
                    ? T("Сейчас в открытом окне", "Currently visible")
                    : T("Ранее найдено на этом острове", "Previously found on this island"),
                Muted: true));
        }

        return fields;
    }

    private static string CleanRumorTitle(string? value)
    {
        string title = (value ?? string.Empty).Trim();
        return title.TrimEnd('.', '…').Trim();
    }

    private static string FormatTags(IEnumerable<string>? tags)
    {
        string[] hidden = ["expedition", "boss", "unique", "map"];
        return string.Join(
            " · ",
            (tags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Where(tag => !hidden.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4));
    }

    private static void DrawRowStatusBadge(Graphics graphics, Rectangle rectangle, bool pending, int alpha)
    {
        if (pending)
        {
            DrawSpinner(graphics, rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
            return;
        }

        Color color = Color.FromArgb(104, 230, 150);
        using var background = new SolidBrush(Color.FromArgb(Math.Min(alpha, 70), color));
        using var border = new Pen(Color.FromArgb(alpha, color), 1.3f);
        graphics.FillEllipse(background, rectangle);
        graphics.DrawEllipse(border, rectangle);
        using var brush = new SolidBrush(Color.FromArgb(alpha, color));
        using var pen = new Pen(brush, 2.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        Point p1 = new(rectangle.Left + 5, rectangle.Top + 10);
        Point p2 = new(rectangle.Left + 9, rectangle.Top + 14);
        Point p3 = new(rectangle.Left + 15, rectangle.Top + 6);
        graphics.DrawLines(pen, new System.Drawing.Point[] { p1, p2, p3 });
    }

    private void DrawLabelAndValue(
        Graphics graphics,
        string label,
        string value,
        int x,
        int y,
        int maxWidth,
        Brush labelBrush,
        Brush valueBrush,
        bool boldValue = false)
    {
        graphics.DrawString(label, _labelFont, labelBrush, x, y + 2);
        int labelWidth = (int)Math.Ceiling(graphics.MeasureString(label, _labelFont).Width) + 10;
        DrawTrimmedText(
            graphics,
            string.IsNullOrWhiteSpace(value) ? "—" : value,
            boldValue ? _bodyBoldFont : _bodyFont,
            valueBrush,
            x + labelWidth,
            y,
            Math.Max(30, maxWidth - labelWidth));
    }

    private static string LocalizedPair(string ru, string en, string language)
    {
        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(en) ? (string.IsNullOrWhiteSpace(ru) ? "—" : ru) : en;
        if (string.IsNullOrWhiteSpace(ru))
            return string.IsNullOrWhiteSpace(en) ? "—" : en;
        return string.IsNullOrWhiteSpace(en) ? ru : $"{ru} · {en}";
    }

    private static string LocalizedValue(string ru, string en, string language)
    {
        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(en))
            return en;
        return !string.IsNullOrWhiteSpace(ru) ? ru : en;
    }

    private void DrawFooter(Graphics graphics)
    {
        int y = Height - FooterHeight + 9;
        using var brush = new SolidBrush(Color.FromArgb(155, 148, 164, 184));
        string updated = _state.LastUpdatedAt == DateTime.MinValue
            ? "—"
            : _state.LastUpdatedAt.ToLocalTime().ToString("HH:mm:ss");
        string visiblePrefix = _state.Items.Count > _visibleItemLimit
            ? $"показано {_visibleItemLimit} из {_state.Items.Count} · "
            : string.Empty;
        string text = _state.Items.Count > 0
            ? $"{visiblePrefix}сканов острова: {_state.IslandScanCount} · всего OCR: {_state.ScanCount} · обновлено: {updated}"
            : "OCR запускается только после остановки курсора и изменения окна";
        DrawTrimmedText(graphics, text, _statusFont, brush, 16, y, Width - 32);
    }

    private Bitmap? GetKindIcon(RumorKind kind) => kind switch
    {
        RumorKind.Boss => _bossIcon,
        RumorKind.Unique => _uniqueIcon,
        _ => _expeditionIcon,
    };

    private static Color KindColor(RumorKind kind) => kind switch
    {
        RumorKind.Boss => Color.FromArgb(255, 95, 109),
        RumorKind.Unique => Color.FromArgb(0xE7, 0x7F, 0x29),
        _ => Color.FromArgb(43, 183, 255),
    };

    private static void DrawKindIcon(
        Graphics graphics,
        Bitmap? icon,
        RumorKind kind,
        Color accent,
        Rectangle rectangle,
        int alpha)
    {
        if (icon is not null)
        {
            var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha / 255f };
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(matrix);
            graphics.DrawImage(icon, rectangle, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
            return;
        }

        using var background = new SolidBrush(Color.FromArgb(Math.Min(alpha, 210), 22, 33, 46));
        using var border = new Pen(Color.FromArgb(alpha, accent), 2f);
        graphics.FillEllipse(background, rectangle);
        graphics.DrawEllipse(border, rectangle);
        string fallback = kind switch
        {
            RumorKind.Boss => "B",
            RumorKind.Unique => "U",
            _ => "E",
        };
        using var font = AppFonts.CreateDrawingFont(18f, FontStyle.Bold);
        using var brush = new SolidBrush(Color.FromArgb(alpha, accent));
        DrawCenteredText(graphics, fallback, font, brush, rectangle, -1f);
    }

    private static void DrawCategoryPill(
        Graphics graphics,
        Rectangle rectangle,
        string text,
        Color accent,
        int alpha)
    {
        using var path = RoundedRect(rectangle, 6);
        using var background = new SolidBrush(Color.FromArgb(Math.Min(alpha, 70), accent));
        using var border = new Pen(Color.FromArgb(Math.Min(alpha, 190), accent), 1f);
        using var brush = new SolidBrush(Color.FromArgb(alpha, accent));
        using var font = AppFonts.CreateDrawingFont(10.5f, FontStyle.Bold);
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        DrawCenteredText(graphics, text, font, brush, rectangle, -1f);
    }

    private void DrawTierPill(
        Graphics graphics,
        Rectangle rectangle,
        string? rating,
        string prefix,
        int alpha = 255)
    {
        string tier = string.IsNullOrWhiteSpace(rating) ? "—" : rating.Trim().ToUpperInvariant();
        Color color = TierColor(tier);
        string text = prefix.Length > 0 ? $"{prefix}{TierCaption(tier)}" : TierCaption(tier);
        using var path = RoundedRect(rectangle, 8);
        using var background = new SolidBrush(Color.FromArgb(Math.Min(alpha, 78), color));
        using var border = new Pen(Color.FromArgb(Math.Min(alpha, 220), color), 1.2f);
        using var brush = new SolidBrush(Color.FromArgb(alpha, color));
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        DrawCenteredText(graphics, text, _tierFont, brush, rectangle, -1f);
    }

    private void DrawPendingPill(Graphics graphics, Rectangle rectangle, int progress, int required, int alpha)
    {
        Color color = Color.FromArgb(111, 181, 255);
        using var path = RoundedRect(rectangle, 8);
        using var background = new SolidBrush(Color.FromArgb(Math.Min(alpha, 65), color));
        using var border = new Pen(Color.FromArgb(Math.Min(alpha, 210), color), 1.2f);
        using var brush = new SolidBrush(Color.FromArgb(alpha, color));
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        DrawCenteredText(graphics, T($"ПРОВЕРКА {Math.Min(progress, required)}/{required}", $"CHECK {Math.Min(progress, required)}/{required}"), _labelFont, brush, rectangle, -1f);
    }

    private string TierCaption(string tier) => tier switch
    {
        "S+" => T("S+ · ЛУЧШЕЕ", "S+ · BEST"),
        "S" => T("S · ТОП", "S · TOP"),
        "A" => T("A · ОТЛИЧНО", "A · EXCELLENT"),
        "B" => T("B · ХОРОШО", "B · GOOD"),
        "C" => T("C · СРЕДНЕ", "C · AVERAGE"),
        "D" => T("D · СЛАБО", "D · WEAK"),
        _ => T("БЕЗ ОЦЕНКИ", "NOT RATED"),
    };

    internal static IReadOnlyList<RumorDisplayItem> OrderForDisplay(
        IEnumerable<RumorDisplayItem> items,
        string sortMode = "tier",
        IReadOnlyList<string>? categoryOrder = null)
    {
        var order = (categoryOrder is { Count: > 0 }
                ? categoryOrder
                : ["expedition", "boss", "unique"])
            .Select((value, index) => new { Key = value.Trim().ToLowerInvariant(), Index = index })
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);

        int CategoryRank(RumorKind kind)
        {
            string key = kind switch
            {
                RumorKind.Boss => "boss",
                RumorKind.Unique => "unique",
                _ => "expedition",
            };
            return order.TryGetValue(key, out int index) ? index : int.MaxValue;
        }

        var candidates = items.Select((item, index) => new { Item = item, FirstSeenIndex = index });
        if (string.Equals(sortMode, "kindThenTier", StringComparison.OrdinalIgnoreCase))
        {
            return candidates
                .OrderBy(candidate => CategoryRank(candidate.Item.Match.Entry.ParsedKind))
                .ThenByDescending(candidate => TierRank(candidate.Item.Match.Entry.Rating))
                .ThenBy(candidate => candidate.Item.IsPending)
                .ThenByDescending(candidate => candidate.Item.Match.Entry.Priority)
                .ThenByDescending(candidate => candidate.Item.IsCurrent)
                .ThenBy(candidate => candidate.FirstSeenIndex)
                .Select(candidate => candidate.Item)
                .ToList();
        }

        return candidates
            .OrderByDescending(candidate => TierRank(candidate.Item.Match.Entry.Rating))
            .ThenBy(candidate => candidate.Item.IsPending)
            .ThenBy(candidate => CategoryRank(candidate.Item.Match.Entry.ParsedKind))
            .ThenByDescending(candidate => candidate.Item.Match.Entry.Priority)
            .ThenByDescending(candidate => candidate.Item.IsCurrent)
            .ThenBy(candidate => candidate.FirstSeenIndex)
            .Select(candidate => candidate.Item)
            .ToList();
    }

    internal static int TierRank(string? rating)
    {
        string tier = (rating ?? string.Empty).Trim().ToUpperInvariant();
        if (tier.Length == 0)
            return 0;

        int baseRank = tier[0] switch
        {
            'S' => 700,
            'A' => 600,
            'B' => 500,
            'C' => 400,
            'D' => 300,
            _ => 100,
        };
        if (tier.Contains('+'))
            baseRank += 20;
        else if (tier.Contains('-'))
            baseRank -= 20;
        return baseRank;
    }

    private static Color TierColor(string tier)
    {
        if (tier.StartsWith("S", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(99, 232, 148);
        if (tier.StartsWith("A", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(92, 183, 255);
        if (tier.StartsWith("B", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(242, 193, 78);
        if (tier.StartsWith("C", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(0xE7, 0x7F, 0x29);
        return Color.FromArgb(148, 163, 184);
    }

    private static Bitmap? LoadIcon(string path)
    {
        try { return File.Exists(path) ? new Bitmap(path) : null; }
        catch { return null; }
    }

    private static void DrawTrimmedText(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        int x,
        int y,
        int maxWidth)
    {
        graphics.DrawString(TrimToWidth(graphics, text, font, maxWidth), font, brush, x, y);
    }

    private static string TrimToWidth(Graphics graphics, string text, Font font, int maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "—";
        if (graphics.MeasureString(text, font).Width <= maxWidth)
            return text;

        const string ellipsis = "…";
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (graphics.MeasureString(text[..middle] + ellipsis, font).Width <= maxWidth)
                low = middle;
            else
                high = middle - 1;
        }
        return text[..Math.Max(0, low)] + ellipsis;
    }

    private static void DrawCenteredText(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        Rectangle rectangle,
        float yOffset = 0f)
    {
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(
            text,
            font,
            brush,
            rectangle.Left + (rectangle.Width - size.Width) / 2f,
            rectangle.Top + (rectangle.Height - size.Height) / 2f + yOffset);
    }

    private static void DrawSpinner(Graphics graphics, int centerX, int centerY)
    {
        int angle = (int)((Environment.TickCount64 / 3) % 360);
        var rectangle = new Rectangle(centerX - 8, centerY - 8, 16, 16);
        using var track = new Pen(Color.FromArgb(75, 185, 198, 215), 2f);
        using var active = new Pen(Color.FromArgb(240, 91, 177, 255), 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawArc(track, rectangle, 0, 360);
        graphics.DrawArc(active, rectangle, angle, 235);
    }

    private void UpdateRoundedRegion()
    {
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), 14);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    private static GraphicsPath RoundedRect(Rectangle rectangle, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
            _titleFont.Dispose();
            _statusFont.Dispose();
            _summaryFont.Dispose();
            _summarySmallFont.Dispose();
            _itemTitleFont.Dispose();
            _bodyFont.Dispose();
            _bodyBoldFont.Dispose();
            _labelFont.Dispose();
            _tierFont.Dispose();
            _diagnosticFont.Dispose();
            _expeditionIcon?.Dispose();
            _bossIcon?.Dispose();
            _uniqueIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}

internal static class RumorOverlayManager
{
    private static readonly object Sync = new();
    private static volatile RumorOverlayForm? _form;
    private static Thread? _thread;
    private static Action _resetRequested = static () => { };
    private static Action _hideRequested = static () => { };
    private static Action _togglePinRequested = static () => { };
    private static Action _previousIslandRequested = static () => { };
    private static Action _nextIslandRequested = static () => { };

    public static void Configure(
        Action resetRequested,
        Action hideRequested,
        Action togglePinRequested,
        Action previousIslandRequested,
        Action nextIslandRequested)
    {
        lock (Sync)
        {
            _resetRequested = resetRequested;
            _hideRequested = hideRequested;
            _togglePinRequested = togglePinRequested;
            _previousIslandRequested = previousIslandRequested;
            _nextIslandRequested = nextIslandRequested;

            var form = _form;
            if (form is not null && !form.IsDisposed)
            {
                form.SetCallbacks(
                    resetRequested,
                    hideRequested,
                    togglePinRequested,
                    previousIslandRequested,
                    nextIslandRequested);
            }
        }
    }

    public static void Update(RumorOverlayState state)
    {
        EnsureCreated();
        var form = _form;
        if (form is not null && !form.IsDisposed)
            form.ApplyState(state);
    }

    public static void HideNow()
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
            form.HideNow();
    }

    public static void PrepareForCapture(Point cursor)
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
            form.PrepareForCapture(cursor);
    }

    public static void AvoidPanel(Point anchor, Rectangle panelBounds)
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
            form.AvoidPanel(anchor, panelBounds);
    }

    public static bool ContainsScreenPoint(Point point)
    {
        var form = _form;
        if (form is null || form.IsDisposed || !form.Visible)
            return false;
        try
        {
            return form.Bounds.Contains(point);
        }
        catch
        {
            return false;
        }
    }

    public static void Close()
    {
        lock (Sync)
        {
            var form = _form;
            if (form is null || form.IsDisposed)
                return;
            try { form.BeginInvoke(() => form.Close()); } catch { }
        }
    }

    private static void EnsureCreated()
    {
        lock (Sync)
        {
            if (_form is not null && !_form.IsDisposed)
                return;

            using var ready = new ManualResetEventSlim(false);
            Action reset = _resetRequested;
            Action hide = _hideRequested;
            Action togglePin = _togglePinRequested;
            Action previous = _previousIslandRequested;
            Action next = _nextIslandRequested;

            _thread = new Thread(() =>
            {
                var form = new RumorOverlayForm(reset, hide, togglePin, previous, next);
                _form = form;
                form.Shown += (_, _) =>
                {
                    form.Hide();
                    ready.Set();
                };
                System.Windows.Forms.Application.Run(form);
                if (ReferenceEquals(_form, form))
                    _form = null;
            })
            {
                IsBackground = true,
                Name = "RumorOverlay-STA",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }
    }
}

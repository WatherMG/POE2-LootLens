using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Poe2LootLens;

internal enum MemeKind { None }

internal sealed record PriceRow(
    int CenterY,
    string OcrText,
    decimal DivineValue,
    decimal ExaltedValue,
    bool HasPrice,
    int Multiplier = 1,
    string Name = "",
    bool ExactMatch = false,
    MemeKind Meme = MemeKind.None,
    float OcrConfidence = 0f,
    string MatchKind = "",
    double MatchScore = 0d,
    string PriceSourceId = "",
    string OcrVariant = "",
    bool VariantAgreement = false,
    int BundleCount = 1,
    bool QuantityTrusted = true,
    int RecognitionAttempts = 0,
    bool RecognitionFailed = false);

internal sealed class PriceOverlayForm : Form
{
    private IReadOnlyList<PriceRow> _rows = [];
    private bool _panelOpen;
    private bool _reading;
    private volatile bool _debug;
    private readonly IconCache _icons;
    private readonly Rectangle _regionRect;
    private readonly int _xOffset;
    private decimal _displayThreshold;
    private string _thresholdCurrency;
    private readonly string _gameLanguage;
    private readonly Action _resetRequested;
    private readonly Action _closeRequested;
    private Rectangle _resetButtonScreenRect;
    private Rectangle _closeButtonScreenRect;
    private readonly Font _priceFont = new("Consolas", 20, FontStyle.Bold);
    private readonly Font _diagnosticFont = new("Consolas", 9, FontStyle.Regular);
    private readonly Font _diagnosticStatusFont = AppFonts.CreateDrawingFont(13f, FontStyle.Bold);
    private readonly System.Windows.Forms.Timer _animationTimer;

    // Reused drawing surface. The original implementation allocated a monitor-sized 32-bit Bitmap
    // on every update; at 4K that is ~32 MiB plus a GDI copy per render.
    private Bitmap? _surface;
    private Graphics? _surfaceGraphics;

    // Coalesce scan-thread updates: at most one UI callback is queued and it always applies the newest
    // state. This prevents stale render callbacks from delaying a close/hide command.
    private readonly object _pendingLock = new();
    private IReadOnlyList<PriceRow> _pendingRows = [];
    private bool _pendingPanelOpen;
    private bool _pendingReading;
    private int _pendingVersion;
    private bool _updateScheduled;

    private const int IconSize = 38;

    public PriceOverlayForm(
        Rectangle overlayBounds,
        Rectangle regionRect,
        int xOffset,
        IconCache icons,
        decimal displayThreshold,
        string thresholdCurrency,
        string gameLanguage,
        Action resetRequested,
        Action closeRequested)
    {
        _regionRect = regionRect;
        _xOffset = xOffset;
        _icons = icons;
        _displayThreshold = PriceDisplayFormatter.NormalizeThreshold(displayThreshold);
        _thresholdCurrency = PriceDisplayFormatter.NormalizeThresholdCurrency(thresholdCurrency);
        _gameLanguage = gameLanguage;
        _resetRequested = resetRequested;
        _closeRequested = closeRequested;
        _animationTimer = new System.Windows.Forms.Timer { Interval = 90 };
        _animationTimer.Tick += (_, _) =>
        {
            if (!IsDisposed && Visible && _rows.Any(row => !row.HasPrice && !row.RecognitionFailed))
                RenderLayered();
        };

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = overlayBounds;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x00080000;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message message)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        const int HTTRANSPARENT = -1;

        if (message.Msg == WM_NCHITTEST)
        {
            int x = unchecked((short)(long)message.LParam);
            int y = unchecked((short)((long)message.LParam >> 16));
            message.Result = new IntPtr(
                _resetButtonScreenRect.Contains(x, y) || _closeButtonScreenRect.Contains(x, y)
                    ? HTCLIENT
                    : HTTRANSPARENT);
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        Point screenPoint = PointToScreen(e.Location);
        if (_resetButtonScreenRect.Contains(screenPoint))
        {
            _resetRequested();
        }
        else if (_closeButtonScreenRect.Contains(screenPoint))
        {
            _closeRequested();
        }
    }

    public void UpdateState(IReadOnlyList<PriceRow> rows, bool panelOpen, bool reading)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            bool schedule;
            lock (_pendingLock)
            {
                _pendingRows = rows;
                _pendingPanelOpen = panelOpen;
                _pendingReading = reading;
                _pendingVersion++;
                schedule = !_updateScheduled;
                _updateScheduled = true;
            }

            if (schedule)
            {
                try
                {
                    BeginInvoke(DrainPendingState);
                }
                catch
                {
                    lock (_pendingLock)
                        _updateScheduled = false;
                }
            }

            return;
        }

        ApplyState(rows, panelOpen, reading);
    }

    private void DrainPendingState()
    {
        if (IsDisposed)
            return;

        while (true)
        {
            IReadOnlyList<PriceRow> rows;
            bool panelOpen;
            bool reading;
            int version;

            lock (_pendingLock)
            {
                rows = _pendingRows;
                panelOpen = _pendingPanelOpen;
                reading = _pendingReading;
                version = _pendingVersion;
            }

            ApplyState(rows, panelOpen, reading);

            lock (_pendingLock)
            {
                if (version == _pendingVersion)
                {
                    _updateScheduled = false;
                    return;
                }
            }
        }
    }

    private void ApplyState(IReadOnlyList<PriceRow> rows, bool panelOpen, bool reading)
    {
        bool stateChanged =
            _panelOpen != panelOpen ||
            _reading != reading ||
            !RowsEqual(_rows, rows);

        if (!stateChanged)
            return;

        _rows = rows;
        _panelOpen = panelOpen;
        _reading = reading;
        ApplyVisibility();
        UpdateAnimationState();

        if (Visible)
            RenderLayered();
    }

    private static bool RowsEqual(IReadOnlyList<PriceRow> left, IReadOnlyList<PriceRow> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
        {
            if (!Equals(left[index], right[index]))
                return false;
        }

        return true;
    }

    public void ToggleDebug()
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(ToggleDebug);
            return;
        }

        _debug = !_debug;
        ApplyVisibility();
        if (Visible)
            RenderLayered();
    }

    public void SetDisplayThreshold(decimal threshold, string thresholdCurrency)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetDisplayThreshold(threshold, thresholdCurrency));
            return;
        }

        _displayThreshold = PriceDisplayFormatter.NormalizeThreshold(threshold);
        _thresholdCurrency = PriceDisplayFormatter.NormalizeThresholdCurrency(thresholdCurrency);
        if (Visible)
            RenderLayered();
    }

    private void UpdateAnimationState()
    {
        bool animate =
            Visible &&
            (_reading || _rows.Any(row => !row.HasPrice && !row.RecognitionFailed));

        if (animate && !_animationTimer.Enabled)
            _animationTimer.Start();
        else if (!animate && _animationTimer.Enabled)
            _animationTimer.Stop();
    }

    private void ApplyVisibility()
    {
        bool shouldShow = _panelOpen || _reading || _debug;
        if (shouldShow && !Visible)
        {
            Show();
            ForceTopmost();
        }
        else if (!shouldShow && Visible)
        {
            _rows = [];
            Hide();
        }

        UpdateAnimationState();
    }

    public void HideNow()
    {
        if (IsDisposed)
            return;

        // Hide the native layered window immediately, even when the WinForms UI thread is currently
        // rendering. The queued call below then clears managed state in the owning thread.
        if (InvokeRequired)
        {
            lock (_pendingLock)
            {
                _pendingRows = [];
                _pendingPanelOpen = false;
                _pendingReading = false;
                _pendingVersion++;
            }

            if (IsHandleCreated && !_debug)
                ShowWindow(Handle, SW_HIDE);

            try
            {
                BeginInvoke(HideNow);
            }
            catch
            {
                // Form is shutting down.
            }
            return;
        }

        _panelOpen = false;
        _reading = false;
        _rows = [];
        ApplyVisibility();
        if (Visible)
            RenderLayered();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    private void EnsureSurface(int width, int height)
    {
        if (_surface is not null && _surface.Width == width && _surface.Height == height)
            return;

        _surfaceGraphics?.Dispose();
        _surface?.Dispose();

        _surface = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _surfaceGraphics = Graphics.FromImage(_surface);
        _surfaceGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _surfaceGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private void RenderLayered()
    {
        if (!IsHandleCreated || IsDisposed || !Visible)
            return;

        int width = Bounds.Width;
        int height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        EnsureSurface(width, height);
        var bitmap = _surface!;
        var graphics = _surfaceGraphics!;

        graphics.ResetTransform();
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceOver;
        PaintScene(graphics);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBitmap = SelectObject(memoryDc, hBitmap);

        try
        {
            var size = new SIZE { cx = width, cy = height };
            var source = new POINT { x = 0, y = 0 };
            var destination = new POINT { x = Bounds.Left, y = Bounds.Top };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(
                Handle,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                ULW_ALPHA);
        }
        finally
        {
            SelectObject(memoryDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void PaintScene(Graphics graphics)
    {
        graphics.TranslateTransform(-Bounds.Left, -Bounds.Top);

        // Debug visuals are deliberately drawn only to the RIGHT of the calibrated capture region.
        // Drawing boxes over the game text fed our own overlay back into CopyFromScreen and changed OCR.
        int priceX = _regionRect.Right + _xOffset;
        if (_panelOpen || _reading || _rows.Count > 0)
            DrawControlButtons(graphics);
        else
        {
            _resetButtonScreenRect = Rectangle.Empty;
            _closeButtonScreenRect = Rectangle.Empty;
        }

        if (_reading && !_panelOpen && _rows.Count == 0)
        {
            using var readingBrush = new SolidBrush(Color.FromArgb(220, Color.White));
            DrawBackdrop(graphics, priceX, _regionRect.Top + 24, 170);
            graphics.DrawString(
                "распознавание...",
                _priceFont,
                readingBrush,
                priceX,
                _regionRect.Top + 24 - _priceFont.Height / 2);
            return;
        }

        if (!_panelOpen && !_reading)
            return;

        PriceRow? topRow = null;
        int pricedCount = 0;
        decimal topValue = -1m;

        foreach (var row in _rows)
        {
            if (!row.HasPrice)
                continue;

            pricedCount++;
            decimal value = row.DivineValue *
                            (row.QuantityTrusted ? Math.Max(1, row.Multiplier) : 1);

            if (value > topValue)
            {
                topValue = value;
                topRow = row;
            }
        }

        foreach (var row in _rows)
        {
            int screenY = _regionRect.Top + row.CenterY;

            if (_debug)
            {
                var markerColor = row.HasPrice ? Color.LimeGreen : Color.Yellow;
                using var markerPen = new Pen(markerColor, 2);
                graphics.DrawLine(markerPen, priceX - 6, screenY - 18, priceX - 6, screenY + 18);

                string agreement = row.VariantAgreement ? "agree" : row.OcrVariant;
                string diagnostic = row.HasPrice
                    ? $"{row.MatchKind} {row.MatchScore:0.000}  OCR {row.OcrConfidence:0} {agreement}  " +
                      $"{row.ExaltedValue:0.###} экз./ед.  id={row.PriceSourceId} -> {row.Name}"
                    : $"{row.MatchKind.ToUpperInvariant()}  OCR {row.OcrConfidence:0} {agreement}  " +
                      $"attempt={row.RecognitionAttempts} failed={row.RecognitionFailed} " +
                      $"bundle={row.BundleCount} qty={(row.QuantityTrusted ? "trusted" : "uncertain")}  " +
                      $"raw: {row.OcrText}";
                using var diagnosticBrush = new SolidBrush(Color.FromArgb(220, Color.LightGray));
                graphics.DrawString(
                    diagnostic,
                    _diagnosticFont,
                    diagnosticBrush,
                    priceX + 250,
                    screenY - _diagnosticFont.Height / 2);
            }

            if (row.HasPrice)
            {
                bool highlightTop = pricedCount > 1 && ReferenceEquals(row, topRow);
                DrawPrice(graphics, row, priceX, screenY, highlightTop);
            }
            else
            {
                DrawRecognitionStatus(graphics, row, priceX, screenY);
            }
        }
    }

    private void DrawControlButtons(Graphics graphics)
    {
        const int resetWidth = 142;
        const int closeWidth = 96;
        const int height = 38;
        const int gap = 8;
        // Keep controls outside and above the captured bitmap so they never become OCR input.
        int left = _regionRect.Right + Math.Max(4, _xOffset);
        int top = Math.Max(Bounds.Top + 4, _regionRect.Top - height - 18);
        _resetButtonScreenRect = new Rectangle(left, top, resetWidth, height);
        _closeButtonScreenRect = new Rectangle(left + resetWidth + gap, top, closeWidth, height);

        string resetLabel = string.Equals(_gameLanguage, "en", StringComparison.OrdinalIgnoreCase)
            ? "↻  Rescan"
            : "↻  Повторить";
        string closeLabel = string.Equals(_gameLanguage, "en", StringComparison.OrdinalIgnoreCase)
            ? "×  Close"
            : "×  Закрыть";
        DrawControlButton(graphics, _resetButtonScreenRect, resetLabel, accent: true);
        DrawControlButton(graphics, _closeButtonScreenRect, closeLabel, accent: false);
    }

    private static void DrawControlButton(
        Graphics graphics,
        Rectangle rectangle,
        string label,
        bool accent)
    {
        using var path = RoundedRect(rectangle, 7);
        using var background = new SolidBrush(Premultiply(Color.FromArgb(220, 24, 34, 49)));
        using var border = new Pen(
            accent ? Color.FromArgb(220, 91, 169, 242) : Color.FromArgb(210, 133, 146, 166),
            1.2f);
        using var textBrush = new SolidBrush(Color.FromArgb(245, 235, 242, 250));
        using var font = AppFonts.CreateDrawingFont(14f, FontStyle.Bold);
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        SizeF size = graphics.MeasureString(label, font);
        graphics.DrawString(
            label,
            font,
            textBrush,
            rectangle.Left + (rectangle.Width - size.Width) / 2f,
            rectangle.Top + (rectangle.Height - size.Height) / 2f - 1f);
    }

    private void DrawPrice(
        Graphics graphics,
        PriceRow row,
        int x,
        int screenY,
        bool highlightTop)
    {
        int iconY = screenY - IconSize / 2;
        int multiplier = Math.Max(1, row.Multiplier);
        var display = PriceDisplayFormatter.Format(
            row.DivineValue,
            row.ExaltedValue,
            multiplier,
            _displayThreshold,
            _thresholdCurrency,
            _gameLanguage,
            row.QuantityTrusted);

        DrawBackdrop(
            graphics,
            x,
            screenY,
            IconSize + 2 + TextWidth(graphics, display.Label));
        DrawIcon(
            graphics,
            display.UseDivine ? _icons.Divine : _icons.Exalted,
            display.UseDivine ? "див." : "экз.",
            x,
            iconY);

        var color = !row.QuantityTrusted
            ? Color.FromArgb(245, 255, 196, 104)
            : highlightTop
                ? Color.FromArgb(80, 255, 120)
                : display.UseDivine ? Color.Gold : Color.White;
        using var priceBrush = new SolidBrush(color);
        int textY = screenY - _priceFont.Height / 2;
        graphics.DrawString(display.Label, _priceFont, priceBrush, x + IconSize + 2, textY);
    }

    private void DrawRecognitionStatus(
        Graphics graphics,
        PriceRow row,
        int x,
        int screenY)
    {
        if (row.MatchKind == "known-no-price")
        {
            const string label = "нет рыночной цены";
            const int statusIconSize = 24;
            int contentWidth = statusIconSize + 8 + (int)Math.Ceiling(
                graphics.MeasureString(label, _diagnosticStatusFont).Width);
            DrawBackdrop(graphics, x, screenY, contentWidth);

            int statusTop = screenY - statusIconSize / 2;
            var circle = new Rectangle(x, statusTop, statusIconSize, statusIconSize);
            using var outline = new Pen(Color.FromArgb(230, 109, 184, 238), 2f);
            using var iconBrush = new SolidBrush(Color.FromArgb(245, 139, 205, 252));
            using var textBrush = new SolidBrush(Color.FromArgb(240, 205, 220, 236));
            using var infoFont = AppFonts.CreateDrawingFont(13f, FontStyle.Bold);
            graphics.DrawEllipse(outline, circle);
            var infoSize = graphics.MeasureString("i", infoFont);
            graphics.DrawString(
                "i",
                infoFont,
                iconBrush,
                circle.Left + (circle.Width - infoSize.Width) / 2f,
                circle.Top + (circle.Height - infoSize.Height) / 2f - 1f);
            graphics.DrawString(
                label,
                _diagnosticStatusFont,
                textBrush,
                x + statusIconSize + 8,
                screenY - _diagnosticStatusFont.Height / 2f);
            return;
        }

        DrawBackdrop(graphics, x, screenY, IconSize);

        int size = 24;
        int left = x + (IconSize - size) / 2;
        int indicatorTop = screenY - size / 2;
        var rectangle = new Rectangle(left, indicatorTop, size, size);

        if (row.RecognitionFailed)
        {
            Color color = row.MatchKind == "ambiguous"
                ? Color.FromArgb(245, 255, 196, 104)
                : Color.FromArgb(235, 255, 174, 72);
            using var outline = new Pen(color, 2f);
            graphics.DrawEllipse(outline, rectangle);
            using var brush = new SolidBrush(color);
            using var font = AppFonts.CreateDrawingFont(14f, FontStyle.Bold);
            string symbol = row.MatchKind == "ambiguous" ? "!" : "?";
            var measured = graphics.MeasureString(symbol, font);
            graphics.DrawString(
                symbol,
                font,
                brush,
                left + (size - measured.Width) / 2f,
                indicatorTop + (size - measured.Height) / 2f - 1f);
            return;
        }

        int angle = (int)((Environment.TickCount64 / 3) % 360);
        using var track = new Pen(Color.FromArgb(90, 210, 220, 235), 2.4f);
        using var active = new Pen(Color.FromArgb(245, 108, 190, 255), 2.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawArc(track, rectangle, 0, 360);
        graphics.DrawArc(active, rectangle, angle, 245);
    }

    private int TextWidth(Graphics graphics, string text) =>
        (int)Math.Ceiling(graphics.MeasureString(text, _priceFont).Width);

    private void DrawBackdrop(Graphics graphics, int x, int centerY, int contentWidth)
    {
        const int paddingX = 6;
        const int paddingY = 3;
        const int radius = 6;
        int height = Math.Max(IconSize, _priceFont.Height) + paddingY * 2;
        var rectangle = new Rectangle(
            x - paddingX,
            centerY - height / 2,
            contentWidth + paddingX * 2,
            height);

        var previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(rectangle, radius);
        using var background = new SolidBrush(Premultiply(Color.FromArgb(150, 55, 55, 64)));
        graphics.FillPath(background, path);
        graphics.SmoothingMode = previous;
    }

    private static Color Premultiply(Color color) =>
        Color.FromArgb(
            color.A,
            color.R * color.A / 255,
            color.G * color.A / 255,
            color.B * color.A / 255);

    private static GraphicsPath RoundedRect(Rectangle rectangle, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(
            rectangle.Right - diameter,
            rectangle.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawIcon(Graphics graphics, Bitmap? icon, string fallback, int x, int y)
    {
        if (icon is not null && _icons.IsAvailable)
        {
            graphics.DrawImage(icon, new Rectangle(x, y, IconSize, IconSize));
        }
        else
        {
            using var brush = new SolidBrush(Color.White);
            using var fallbackFont = AppFonts.CreateDrawingFont(
                fallback.Length > 2 ? 9f : 15f,
                FontStyle.Bold);
            var size = graphics.MeasureString(fallback, fallbackFont);
            graphics.DrawString(
                fallback,
                fallbackFont,
                brush,
                x + (IconSize - size.Width) / 2f,
                y + (IconSize - size.Height) / 2f);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ForceTopmost();
        RenderLayered();
    }

    public void ForceTopmost()
    {
        if (IsDisposed || !IsHandleCreated || !Visible)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(ForceTopmost);
            return;
        }

        SetWindowPos(
            Handle,
            new IntPtr(-1),
            0,
            0,
            0,
            0,
            0x0002 | 0x0001 | 0x0010);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            _animationTimer.Dispose();
            _surfaceGraphics?.Dispose();
            _surface?.Dispose();
            _priceFont.Dispose();
            _diagnosticFont.Dispose();
            _diagnosticStatusFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private const int SW_HIDE = 0;
    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        IntPtr window,
        IntPtr destinationDc,
        ref POINT destination,
        ref SIZE size,
        IntPtr sourceDc,
        ref POINT source,
        int colorKey,
        ref BLENDFUNCTION blend,
        int flags);
}

internal static class PriceOverlayManager
{
    private static volatile PriceOverlayForm? _form;
    private static Thread? _thread;
    private static readonly object Lock = new();

    // Enough room for the calibrated panel (debug boxes) plus price labels. Keeping the layered window
    // local to this area avoids a full-monitor ARGB surface, especially expensive on 1440p/4K displays.
    private const int PriceAreaWidth = 800;
    private const int Margin = 68;

    public static void EnsureVisible(
        Rectangle regionRect,
        int xOffset,
        IconCache icons,
        decimal displayThreshold,
        string thresholdCurrency,
        string gameLanguage,
        Action resetRequested,
        Action closeRequested)
    {
        lock (Lock)
        {
            if (_form is not null && !_form.IsDisposed)
            {
                var existing = _form;
                existing.Invoke(() =>
                {
                    if (existing.IsDisposed)
                        return;

                    existing.SetDisplayThreshold(displayThreshold, thresholdCurrency);
                    if (!existing.Visible)
                        existing.Show();
                });
                return;
            }

            var monitorBounds = Screen.FromRectangle(regionRect).Bounds;
            var overlayBounds = CalculateOverlayBounds(regionRect, monitorBounds, xOffset);
            using var ready = new ManualResetEventSlim(false);

            _thread = new Thread(() =>
            {
                var form = new PriceOverlayForm(
                    overlayBounds,
                    regionRect,
                    xOffset,
                    icons,
                    displayThreshold,
                    thresholdCurrency,
                    gameLanguage,
                    resetRequested,
                    closeRequested);
                _form = form;
                form.Shown += (_, _) => ready.Set();
                System.Windows.Forms.Application.Run(form);

                lock (Lock)
                {
                    if (ReferenceEquals(_form, form))
                        _form = null;
                }
            })
            {
                IsBackground = true,
                Name = "PriceOverlay-STA",
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }
    }

    internal static Rectangle CalculateOverlayBounds(
        Rectangle regionRect,
        Rectangle monitorBounds,
        int xOffset)
    {
        int left = Math.Max(monitorBounds.Left, regionRect.Left - Margin);
        int top = Math.Max(monitorBounds.Top, regionRect.Top - Margin);
        int right = Math.Min(
            monitorBounds.Right,
            regionRect.Right + Math.Max(xOffset, 0) + PriceAreaWidth);
        int bottom = Math.Min(monitorBounds.Bottom, regionRect.Bottom + Margin);

        if (right <= left)
            right = Math.Min(monitorBounds.Right, left + 1);
        if (bottom <= top)
            bottom = Math.Min(monitorBounds.Bottom, top + 1);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    public static void Hide()
    {
        lock (Lock)
        {
            var form = _form;
            if (form is null || form.IsDisposed)
                return;

            form.Invoke(() =>
            {
                if (!form.IsDisposed)
                    form.Close();
            });
        }
    }

    public static void UpdateState(
        IReadOnlyList<PriceRow> rows,
        bool panelOpen,
        bool reading)
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
            form.UpdateState(rows, panelOpen, reading);
    }

    public static void ForceTopmost()
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
            form.ForceTopmost();
    }

    public static void ToggleDebug()
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
            form.ToggleDebug();
    }

    public static void SetDisplayThreshold(decimal threshold, string thresholdCurrency)
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
            form.SetDisplayThreshold(threshold, thresholdCurrency);
    }

    public static void HideNow()
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
            form.HideNow();
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Poe2LootLens;

internal enum RumorLineRecognitionStatus
{
    Waiting,
    Scanning,
    Matched,
    Unmatched,
    Empty,
}

internal sealed record RumorLineIndicator(
    Rectangle LineBounds,
    RumorLineRecognitionStatus Status,
    string Caption = "");

internal sealed record RumorLineOverlayState(
    Rectangle PanelBounds,
    IReadOnlyList<RumorLineIndicator> Lines,
    bool Visible = true);

internal sealed class RumorLineOverlayForm : Form
{
    private const int BadgeWidth = 38;
    private const int BadgeHeight = 30;
    private const int Gap = 8;
    private static readonly Color TransparentColor = Color.FromArgb(1, 2, 3);

    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly Font _font = AppFonts.CreateDrawingFont(13f, FontStyle.Bold);
    private RumorLineOverlayState _state = new(Rectangle.Empty, [], false);
    private bool _placeRight = true;
    private bool _debug;
    private Rectangle _lastPanel = Rectangle.Empty;
    private Rectangle _railBounds = Rectangle.Empty;

    public RumorLineOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = TransparentColor;
        TransparencyKey = TransparentColor;
        DoubleBuffered = true;
        Width = BadgeWidth;
        Height = 100;

        _animationTimer = new System.Windows.Forms.Timer { Interval = 90 };
        _animationTimer.Tick += (_, _) =>
        {
            if (Visible && _state.Lines.Any(line => line.Status == RumorLineRecognitionStatus.Scanning))
                Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TRANSPARENT = 0x00000020;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); } catch { }
    }

    public void ApplyState(RumorLineOverlayState state)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => ApplyState(state)); } catch { }
            return;
        }

        _state = state;
        if (!state.Visible || state.PanelBounds.IsEmpty || state.Lines.Count == 0)
        {
            _animationTimer.Stop();
            Hide();
            return;
        }

        Rectangle workingArea = Screen.FromRectangle(state.PanelBounds).WorkingArea;
        bool samePanel = !_lastPanel.IsEmpty &&
                         Math.Abs(_lastPanel.Left - state.PanelBounds.Left) <= 16 &&
                         Math.Abs(_lastPanel.Top - state.PanelBounds.Top) <= 16 &&
                         Math.Abs(_lastPanel.Width - state.PanelBounds.Width) <= 24 &&
                         Math.Abs(_lastPanel.Height - state.PanelBounds.Height) <= 24;
        if (!samePanel)
        {
            _placeRight = state.PanelBounds.Right + Gap + BadgeWidth <= workingArea.Right - 4;
            _lastPanel = state.PanelBounds;
        }

        // Keep the indicator rail at the first accepted panel position. Detector bounds jitter by a
        // few pixels between captures; following that jitter makes the badges visibly vibrate.
        Rectangle stablePanel = samePanel ? _lastPanel : state.PanelBounds;
        int left = _placeRight
            ? stablePanel.Right + Gap
            : stablePanel.Left - Gap - BadgeWidth;
        left = Math.Clamp(left, workingArea.Left + 2, workingArea.Right - BadgeWidth - 2);
        _railBounds = new Rectangle(left, stablePanel.Top, BadgeWidth, stablePanel.Height);
        Bounds = _debug
            ? Rectangle.Inflate(Rectangle.Union(stablePanel, _railBounds), 4, 4)
            : _railBounds;

        if (!Visible)
            Show();
        SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

        if (state.Lines.Any(line => line.Status == RumorLineRecognitionStatus.Scanning))
            _animationTimer.Start();
        else
            _animationTimer.Stop();
        Invalidate();
    }

    public void ToggleDebug()
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            try { BeginInvoke(ToggleDebug); } catch { }
            return;
        }
        _debug = !_debug;
        if (_state.Visible)
            ApplyState(_state);
    }

    private Rectangle ToClient(Rectangle screenRectangle) => new(
        screenRectangle.Left - Bounds.Left,
        screenRectangle.Top - Bounds.Top,
        screenRectangle.Width,
        screenRectangle.Height);

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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(TransparentColor);

        if (_debug)
        {
            using var panelPen = new Pen(Color.FromArgb(220, 86, 214, 154), 2f);
            Rectangle panel = ToClient(_state.PanelBounds);
            e.Graphics.DrawRectangle(panelPen, panel);
            using var slotPen = new Pen(Color.FromArgb(220, 89, 169, 255), 1.5f);
            using var debugFont = AppFonts.CreateDrawingFont(9f, FontStyle.Bold);
            using var debugBrush = new SolidBrush(Color.FromArgb(245, 225, 238, 252));
            foreach (RumorLineIndicator line in _state.Lines)
            {
                Rectangle slot = ToClient(line.LineBounds);
                e.Graphics.DrawRectangle(slotPen, slot);
                string caption = string.IsNullOrWhiteSpace(line.Caption)
                    ? $"{slot.Width}×{slot.Height}"
                    : line.Caption;
                e.Graphics.DrawString(caption, debugFont, debugBrush, slot.Left + 3, slot.Top + 2);
            }
        }

        foreach (RumorLineIndicator line in _state.Lines)
        {
            if (line.Status == RumorLineRecognitionStatus.Empty)
                continue;
            int centerY = line.LineBounds.Top + line.LineBounds.Height / 2 - Bounds.Top;
            int badgeX = _railBounds.Left - Bounds.Left + 2;
            var badge = new Rectangle(badgeX, centerY - BadgeHeight / 2, BadgeWidth - 4, BadgeHeight);
            badge.Y = Math.Clamp(badge.Y, 1, Math.Max(1, Height - BadgeHeight - 1));
            DrawBadge(e.Graphics, badge, line);
        }
    }

    private void DrawBadge(Graphics graphics, Rectangle badge, RumorLineIndicator line)
    {
        Color accent = line.Status switch
        {
            RumorLineRecognitionStatus.Matched => Color.FromArgb(86, 214, 154),
            RumorLineRecognitionStatus.Unmatched => Color.FromArgb(255, 191, 105),
            RumorLineRecognitionStatus.Scanning => Color.FromArgb(89, 169, 255),
            _ => Color.FromArgb(117, 132, 154),
        };
        using var path = RoundedRect(badge, 9);
        using var fill = new SolidBrush(Color.FromArgb(230, 15, 21, 31));
        using var border = new Pen(Color.FromArgb(235, accent), 1.5f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        if (line.Status == RumorLineRecognitionStatus.Scanning)
        {
            int angle = (int)((Environment.TickCount64 / 3) % 360);
            var arc = new Rectangle(badge.Left + 9, badge.Top + 6, 18, 18);
            using var track = new Pen(Color.FromArgb(75, 185, 198, 215), 2f);
            using var active = new Pen(accent, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(track, arc, 0, 360);
            graphics.DrawArc(active, arc, angle, 235);
            return;
        }

        string text = line.Status switch
        {
            RumorLineRecognitionStatus.Matched => "✓",
            RumorLineRecognitionStatus.Unmatched => "?",
            _ => "·",
        };
        using var brush = new SolidBrush(accent);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        graphics.DrawString(text, _font, brush, badge, format);
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
            _font.Dispose();
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

internal static class RumorLineOverlayManager
{
    private static readonly object Sync = new();
    private static volatile RumorLineOverlayForm? _form;
    private static Thread? _thread;

    public static void Update(RumorLineOverlayState state)
    {
        EnsureCreated();
        RumorLineOverlayForm? form = _form;
        if (form is not null && !form.IsDisposed)
            form.ApplyState(state);
    }

    public static void ToggleDebug()
    {
        EnsureCreated();
        RumorLineOverlayForm? form = _form;
        if (form is not null && !form.IsDisposed)
            form.ToggleDebug();
    }

    public static void HideNow()
    {
        RumorLineOverlayForm? form = _form;
        if (form is not null && !form.IsDisposed)
            form.HideNow();
    }

    public static bool ContainsScreenPoint(Point point)
    {
        RumorLineOverlayForm? form = _form;
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
            RumorLineOverlayForm? form = _form;
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
            _thread = new Thread(() =>
            {
                var form = new RumorLineOverlayForm();
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
                Name = "RumorLineOverlay-STA",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }
    }
}

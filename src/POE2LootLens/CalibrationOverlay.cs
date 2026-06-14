using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Poe2LootLens;

// Full-screen drag-to-select overlay. User draws a rectangle over the reward-list panel.
internal sealed class CalibrationOverlay : Form
{
    public Rectangle RegionRectResult { get; private set; }

    private Point? _dragStart;
    private Rectangle _currentDrag;
    private Rectangle _confirmedRect;
    private readonly Bitmap _screenSnapshot;

    public CalibrationOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.4;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Text = "PoE 2 LootLens — выбор области";

        var bounds = SystemInformation.VirtualScreen;
        Bounds = bounds;

        _screenSnapshot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(_screenSnapshot);
        graphics.CopyFromScreen(
            bounds.Location,
            Point.Empty,
            bounds.Size,
            CopyPixelOperation.SourceCopy);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _screenSnapshot.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        _dragStart = e.Location;
        _currentDrag = Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not { } start)
            return;

        _currentDrag = Rectangle.FromLTRB(
            Math.Min(start.X, e.X),
            Math.Min(start.Y, e.Y),
            Math.Max(start.X, e.X),
            Math.Max(start.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragStart is null || _currentDrag.Width < 3 || _currentDrag.Height < 3)
        {
            _dragStart = null;
            _currentDrag = Rectangle.Empty;
            Invalidate();
            return;
        }

        _dragStart = null;
        _confirmedRect = _currentDrag;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) &&
            _confirmedRect.Width > 0)
        {
            RegionRectResult = _confirmedRect with
            {
                X = _confirmedRect.X + Bounds.X,
                Y = _confirmedRect.Y + Bounds.Y,
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        using var titleFont = AppFonts.CreateDrawingFont(18f, FontStyle.Bold, GraphicsUnit.Point);
        using var subFont = AppFonts.CreateDrawingFont(12f, FontStyle.Regular, GraphicsUnit.Point);
        using var foreground = new SolidBrush(Color.White);

        graphics.DrawString(
            "Выделите рамкой список наград. Enter — сохранить, Esc — отмена.",
            titleFont,
            foreground,
            30,
            30);

        if (_currentDrag.Width > 0)
        {
            using var pen = new Pen(Color.OrangeRed, 2)
            {
                DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
            };
            graphics.DrawRectangle(pen, _currentDrag);
        }

        if (_confirmedRect.Width > 0)
        {
            using var pen = new Pen(Color.LimeGreen, 3);
            graphics.DrawRectangle(pen, _confirmedRect);
            graphics.DrawString(
                "Enter — сохранить; выделите заново, чтобы изменить",
                subFont,
                foreground,
                _confirmedRect.Left,
                _confirmedRect.Bottom + 6);
        }
    }

    public static Rectangle? RunOnStaThread()
    {
        Rectangle? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Do not call Application.SetHighDpiMode here. The price overlay may already have
                // created a WinForms handle; changing DPI mode afterwards can throw and make F4 appear
                // to freeze. DPI is configured once by the application process.
                using var form = new CalibrationOverlay();
                if (form.ShowDialog() == DialogResult.OK)
                    result = form.RegionRectResult;
            }
            catch (Exception exception)
            {
                error = exception;
            }
        })
        {
            IsBackground = true,
            Name = "CalibrationOverlay-STA",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            throw new InvalidOperationException("Calibration overlay failed.", error);

        return result;
    }
}

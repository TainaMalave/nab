using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Snapshot;

/// <summary>
/// Full-virtual-screen overlay showing a frozen, dimmed copy of the desktop. The user drags a rectangle;
/// the selected area is shown undimmed. Client coordinates == screenshot pixel coordinates (physical px).
/// Esc or right-click cancels.
/// </summary>
internal sealed class RegionOverlay : Form
{
    private readonly Bitmap _screen;
    private readonly Bitmap _dimmed;
    private readonly Rectangle _virtual;
    private readonly Font _font;
    private Point _start;
    private bool _dragging;
    private Rectangle _sel;
    private Rectangle _lastDirty;

    /// <summary>Selected rectangle in screenshot (client) coordinates. Valid when DialogResult == OK.</summary>
    public Rectangle Selection => _sel;

    public RegionOverlay(Bitmap screen, Rectangle virtualScreen)
    {
        _screen = screen;
        _virtual = virtualScreen;
        _dimmed = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(_dimmed))
        {
            g.DrawImageUnscaled(screen, 0, 0);
            using var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            g.FillRectangle(shade, 0, 0, screen.Width, screen.Height);
        }
        float s = Native.ScaleAt(Cursor.Position);
        _font = new Font("Segoe UI", 12f * s, FontStyle.Bold, GraphicsUnit.Pixel);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Bounds = virtualScreen;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Re-assert exact physical bounds (WinForms may have clamped/scaled) and grab the foreground for Esc.
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _virtual.X, _virtual.Y, _virtual.Width, _virtual.Height,
            Native.SWP_SHOWWINDOW);
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        // The window spans monitors; never let a DPI change resize it (that would offset the selection).
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
        // Allow the window to be as large as the whole virtual desktop (default max track size can be smaller
        // on mixed-DPI multi-monitor setups).
        if (m.Msg == Native.WM_GETMINMAXINFO && m.LParam != IntPtr.Zero)
        {
            var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(m.LParam);
            mmi.ptMaxSize = mmi.ptMaxTrackSize = new Native.POINT { X = _virtual.Width, Y = _virtual.Height };
            Marshal.StructureToPtr(mmi, m.LParam, false);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; return; }
        base.OnKeyDown(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && DialogResult == DialogResult.None) DialogResult = DialogResult.Cancel; // e.g. Alt+Tab
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _start = Clamp(e.Location);
        UpdateSelection(new Rectangle(_start, Size.Empty));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = Clamp(e.Location);
        UpdateSelection(Rectangle.FromLTRB(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y),
                                           Math.Max(_start.X, p.X), Math.Max(_start.Y, p.Y)));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        if (_sel.Width >= 4 && _sel.Height >= 4)
            DialogResult = DialogResult.OK;
        else
            UpdateSelection(Rectangle.Empty); // a click without a drag: let the user try again
    }

    private Point Clamp(Point p) =>
        new(Native.Clamp(p.X, 0, ClientSize.Width), Native.Clamp(p.Y, 0, ClientSize.Height));

    private void UpdateSelection(Rectangle sel)
    {
        _sel = sel;
        var dirty = sel.IsEmpty ? Rectangle.Empty : Rectangle.Union(sel, LabelRect(sel));
        dirty.Inflate(4, 4);
        Invalidate(_lastDirty.IsEmpty ? dirty : Rectangle.Union(_lastDirty, dirty));
        _lastDirty = dirty;
    }

    private string LabelText(Rectangle sel) => $"{sel.Width} \u00D7 {sel.Height}";

    private Rectangle LabelRect(Rectangle sel)
    {
        var sz = TextRenderer.MeasureText(LabelText(sel), _font);
        int pad = _font.Height / 3;
        var r = new Rectangle(sel.Right - sz.Width - 2 * pad, sel.Bottom + pad, sz.Width + 2 * pad, sz.Height + pad);
        if (r.Bottom > ClientSize.Height) r.Y = sel.Bottom - r.Height - pad; // no room below: put inside
        if (r.X < 0) r.X = 0;
        return r;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImage(_dimmed, clip, clip, GraphicsUnit.Pixel);
        if (_sel.IsEmpty) return;

        var bright = Rectangle.Intersect(_sel, clip);
        if (!bright.IsEmpty) g.DrawImage(_screen, bright, bright, GraphicsUnit.Pixel);

        g.CompositingMode = CompositingMode.SourceOver;
        using (var pen = new Pen(Color.White, 1))
            g.DrawRectangle(pen, _sel.X, _sel.Y, Math.Max(0, _sel.Width - 1), Math.Max(0, _sel.Height - 1));

        var lr = LabelRect(_sel);
        using (var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20)))
            g.FillRectangle(bg, lr);
        TextRenderer.DrawText(g, LabelText(_sel), _font, lr, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _dimmed.Dispose(); _font.Dispose(); }
        base.Dispose(disposing);
    }
}

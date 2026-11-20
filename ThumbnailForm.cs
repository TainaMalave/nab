using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Snapshot;

/// <summary>
/// CleanShot-style floating preview. Topmost, never activates (so it never steals focus), no taskbar entry.
/// Hover shows Copy / Save As / Folder / close; dragging it drops the PNG file into any app.
/// Buttons are painted by hand (not WinForms Buttons) because focusing a child control would activate the window.
/// </summary>
internal sealed class ThumbnailForm : Form
{
    private enum Btn { None = -1, Copy, SaveAs, Folder, Close }

    private readonly Bitmap _image;      // full-size capture (owned)
    private readonly Bitmap _preview;    // pre-scaled to client size
    private readonly string _path;
    private readonly float _s;
    private readonly Font _font;
    private readonly System.Windows.Forms.Timer _dismiss = new() { Interval = 6000 };
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 1000 };
    private readonly Dictionary<Btn, Rectangle> _buttons = new();

    private bool _hover, _busy;
    private Btn _hot = Btn.None, _pressed = Btn.None;
    private Point? _dragStart;
    private string? _flash;

    public ThumbnailForm(Bitmap image, string path, float scale)
    {
        _image = image;
        _path = path;
        _s = scale;
        _font = new Font("Segoe UI", 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);

        int w = (int)(240 * scale);
        int h = Native.Clamp((int)(w * (double)image.Height / image.Width), (int)(110 * scale), (int)(200 * scale));

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ClientSize = new Size(w, h);
        BackColor = Color.FromArgb(32, 32, 32);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        SetStyle(ControlStyles.Selectable, false); // never take focus on click

        _preview = MakePreview(image, ClientSize);
        _dismiss.Tick += (_, _) => Close();
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flash = null; Invalidate(); };
        LayoutButtons();
    }

    // ---- window behaviour -------------------------------------------------------------------------

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int pref = Native.DWMWCP_ROUND;
        int hr = -1;
        try { hr = Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch (DllNotFoundException) { }
        if (hr != 0) // Windows 10: no DWM rounding, clip to a rounded region instead
        {
            using var path = RoundRect(ClientRectangle, (int)(8 * _s));
            Region = new Region(path);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    /// <summary>Show without activating and pin above other windows.</summary>
    public void ShowNoActivate()
    {
        Show(); // ShowWithoutActivation => SW_SHOWNOACTIVATE
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _dismiss.Start();
    }

    /// <summary>Temporarily hide (e.g. while taking another screenshot) and restore without activation.</summary>
    public void SetHidden(bool hidden)
    {
        if (hidden) { _dismiss.Stop(); Hide(); }
        else { ShowNoActivate(); if (_hover) _dismiss.Stop(); }
    }

    // ---- mouse ------------------------------------------------------------------------------------

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        _dismiss.Stop();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _hot = Btn.None;
        if (!_busy) _dismiss.Start(); // restart the full 6 s countdown
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_hover) { _hover = true; _dismiss.Stop(); Invalidate(); }

        if (_dragStart is Point start && e.Button == MouseButtons.Left)
        {
            var drag = SystemInformation.DragSize;
            if (Math.Abs(e.X - start.X) > drag.Width || Math.Abs(e.Y - start.Y) > drag.Height)
            {
                _dragStart = null;
                StartFileDrag();
            }
            return;
        }

        var hot = HitTest(e.Location);
        if (hot != _hot) { _hot = hot; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = HitTest(e.Location);
        _dragStart = _pressed == Btn.None ? e.Location : null;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;
        _pressed = Btn.None;
        _dragStart = null;
        if (e.Button == MouseButtons.Left && pressed != Btn.None && HitTest(e.Location) == pressed)
            Run(pressed);
    }

    private Btn HitTest(Point p)
    {
        if (!_hover) return Btn.None;
        foreach (var kv in _buttons) if (kv.Value.Contains(p)) return kv.Key;
        return Btn.None;
    }

    private void StartFileDrag()
    {
        if (!File.Exists(_path)) return;
        _busy = true;
        _dismiss.Stop();
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { _path });
        var effect = DoDragDrop(data, DragDropEffects.Copy);
        _busy = false;
        if (effect != DragDropEffects.None) { Close(); return; } // dropped somewhere: done, like CleanShot
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) { _hover = false; _hot = Btn.None; Invalidate(); }
        if (!_hover) _dismiss.Start();
    }

    // ---- actions ----------------------------------------------------------------------------------

    private void Run(Btn btn)
    {
        try
        {
            switch (btn)
            {
                case Btn.Copy:
                    ScreenCapture.CopyToClipboard(_image);
                    Flash("Copied");
                    break;
                case Btn.SaveAs:
                    SaveAs();
                    break;
                case Btn.Folder:
                    if (File.Exists(_path))
                        Process.Start("explorer.exe", $"/select,\"{_path}\"");
                    else
                        Process.Start("explorer.exe", $"\"{ScreenCapture.Folder}\"");
                    break;
                case Btn.Close:
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Flash("Failed");
            Debug.WriteLine(ex);
        }
    }

    private void SaveAs()
    {
        _busy = true;
        _dismiss.Stop();
        try
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save screenshot as",
                FileName = Path.GetFileName(_path),
                Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
                DefaultExt = "png",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            bool jpg = dlg.FilterIndex == 2 || Path.GetExtension(dlg.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                                             || Path.GetExtension(dlg.FileName).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            if (jpg) _image.Save(dlg.FileName, ImageFormat.Jpeg);
            else _image.Save(dlg.FileName, ImageFormat.Png);
            Flash("Saved");
        }
        finally
        {
            _busy = false;
            if (!IsDisposed && !ClientRectangle.Contains(PointToClient(Cursor.Position)))
            {
                _hover = false; _hot = Btn.None; Invalidate();
                _dismiss.Start();
            }
        }
    }

    private void Flash(string text)
    {
        _flash = text;
        _flashTimer.Stop();
        _flashTimer.Start();
        Invalidate();
    }

    // ---- painting ---------------------------------------------------------------------------------

    private void LayoutButtons()
    {
        int h = (int)(26 * _s), gap = (int)(6 * _s), pad = (int)(12 * _s);
        var btns = new[] { Btn.Copy, Btn.SaveAs, Btn.Folder };
        var widths = btns.Select(b => TextRenderer.MeasureText(Label(b), _font).Width + 2 * pad).ToArray();
        int total = widths.Sum() + gap * (btns.Length - 1);
        int x = (ClientSize.Width - total) / 2, y = (ClientSize.Height - h) / 2;
        for (int i = 0; i < btns.Length; i++)
        {
            _buttons[btns[i]] = new Rectangle(x, y, widths[i], h);
            x += widths[i] + gap;
        }
        int c = (int)(22 * _s), m = (int)(6 * _s);
        _buttons[Btn.Close] = new Rectangle(ClientSize.Width - c - m, m, c, c);
    }

    private static string Label(Btn b) => b == Btn.Copy ? "Copy" : b == Btn.SaveAs ? "Save As" : "Folder";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_preview, 0, 0);

        if (_hover || _flash != null)
        {
            using var shade = new SolidBrush(Color.FromArgb(_flash != null && !_hover ? 90 : 120, 0, 0, 0));
            g.FillRectangle(shade, ClientRectangle);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_flash != null)
        {
            var r = ClientRectangle;
            r.Height = (int)(30 * _s);
            r.Y = _hover ? (int)(10 * _s) : (ClientSize.Height - r.Height) / 2;
            TextRenderer.DrawText(g, _flash, _font, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (_hover)
        {
            foreach (var kv in _buttons)
            {
                Btn btn = kv.Key;
                Rectangle r = kv.Value;
                bool hot = btn == _hot;
                if (btn == Btn.Close)
                {
                    using var b = new SolidBrush(Color.FromArgb(hot ? 255 : 200, 245, 245, 245));
                    g.FillEllipse(b, r);
                    using var pen = new Pen(Color.FromArgb(40, 40, 40), 1.6f * _s);
                    int k = r.Width / 3;
                    g.DrawLine(pen, r.Left + k, r.Top + k, r.Right - k, r.Bottom - k);
                    g.DrawLine(pen, r.Right - k, r.Top + k, r.Left + k, r.Bottom - k);
                }
                else
                {
                    using var path = RoundRect(r, r.Height / 2);
                    using var b = new SolidBrush(hot ? Color.FromArgb(37, 99, 235) : Color.FromArgb(235, 250, 250, 250));
                    g.FillPath(b, path);
                    TextRenderer.DrawText(g, Label(btn), _font, r, hot ? Color.White : Color.FromArgb(30, 30, 30),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
        }

        g.SmoothingMode = SmoothingMode.None;
        using var border = new Pen(Color.FromArgb(70, 255, 255, 255));
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private static Bitmap MakePreview(Bitmap src, Size size)
    {
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(32, 32, 32));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Fit inside the thumbnail; letterboxed (centred) only when the aspect ratio had to be clamped.
        double k = Math.Min((double)size.Width / src.Width, (double)size.Height / src.Height);
        int w = (int)Math.Round(src.Width * k), h = (int)Math.Round(src.Height * k);
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // avoids a faint dark edge from bicubic sampling
        g.DrawImage(src, new Rectangle((size.Width - w) / 2, (size.Height - h) / 2, w, h),
            0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return bmp;
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dismiss.Dispose();
            _flashTimer.Dispose();
            _preview.Dispose();
            _image.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}

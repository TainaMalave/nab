using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Snapshot;

internal enum Tool { Arrow, Rect, Ellipse, Text }

/// <summary>
/// Annotation editor (v7): arrow, rectangle, ellipse and text tools, colours, three sizes, undo/redo, zoom,
/// copy, save over the original, save as. Everything is painted by hand (toolbar included) so it scales
/// cleanly with per-monitor DPI. Annotations live in a list and are only merged into pixels on copy/save.
/// </summary>
internal sealed class EditorForm : Form
{
    // ---- remembered between editors (for this run of Snapshot) -------------------------------------
    private static readonly Color[] Presets =
    {
        Color.FromArgb(255, 59, 48),   // red
        Color.FromArgb(255, 204, 0),   // yellow
        Color.FromArgb(52, 199, 89),   // green
        Color.FromArgb(0, 122, 255),   // blue
        Color.FromArgb(0, 0, 0),       // black
        Color.FromArgb(255, 255, 255), // white
    };
    private static readonly float[] StrokeSizes = { 3f, 5f, 8f };  // image px at 100% display scaling
    private static readonly float[] TextSizes = { 18f, 26f, 38f };
    private static Tool s_tool = Tool.Arrow;
    private static Color s_color = Presets[0];
    private static Color s_custom = Color.FromArgb(175, 82, 222); // purple until the user picks one
    private static int s_size = 1;
    private static bool s_fillShapes = false, s_textPill = true;

    // ---- theme ---------------------------------------------------------------------------------------
    private static readonly Color BarBg = Color.FromArgb(38, 38, 38);
    private static readonly Color CanvasBg = Color.FromArgb(24, 24, 24);
    private static readonly Color Hover = Color.FromArgb(62, 62, 62);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentHover = Color.FromArgb(59, 130, 246);
    private static readonly Color Fg = Color.FromArgb(235, 235, 235);
    private static readonly Color FgDisabled = Color.FromArgb(105, 105, 105);

    // ---- document ------------------------------------------------------------------------------------
    private readonly Bitmap _image;    // the capture (owned)
    private readonly Bitmap _display;  // 32bppPArgb copy, fast to draw
    private string _path;              // the PNG in Pictures\Snapshots ("" if the original save failed)
    private readonly float _imgScale;  // DPI scale of the monitor the capture came from: sizes strokes and text
    private readonly List<IReadOnlyList<Annot>> _history = new() { Array.Empty<Annot>() };
    private int _index, _savedIndex;
    private IReadOnlyList<Annot> Current => _history[_index];
    private bool Dirty => _index != _savedIndex;

    // ---- interaction ---------------------------------------------------------------------------------
    private Annot? _draft;
    private PointF _dragFrom;
    private TextAnnot? _editing;       // text being typed (not in history yet)
    private int _editIndex = -1;       // index in Current it replaces, or -1 for new text
    private int _caret;
    private bool _caretOn;
    private Point? _panFrom;
    private PointF _panOffsetFrom;
    private bool _spaceDown;

    // ---- view ----------------------------------------------------------------------------------------
    private float _s;                  // UI scale of the monitor the window is on
    private float _zoom = 1f;
    private PointF _offset;            // client position of image pixel (0,0); kept whole-pixel
    private bool _fit = true;
    private Bitmap? _cache;            // image pre-scaled for zoom < 1
    private float _cacheZoom;

    // ---- toolbar -------------------------------------------------------------------------------------
    private sealed class Item
    {
        public Rectangle R;
        public Func<string> Tip = () => "";
        public Action Click = () => { };
        public Func<bool> On = () => false;
        public Func<bool> Enabled = () => true;
        public Action<Graphics, Rectangle, Color>? Icon;
        public Func<string>? Label;
        public bool Primary, Swatch;
    }
    private readonly List<Item> _items = new();
    private readonly List<Point> _seps = new(); // x, row top
    private Item? _hot, _pressed;
    private int _minBarWidth, _rows = 1;
    private Font _uiFont = null!, _toastFont = null!;
    private readonly ToolTip _tip = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = 450 };
    private readonly System.Windows.Forms.Timer _caretTimer = new() { Interval = 530 };
    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 1200 };
    private string? _toast;
    private bool _closeWithoutPrompt;

    private int BarH => (int)(52 * _s) * _rows;
    private int Pad => (int)(20 * _s);
    private Rectangle CanvasRect => new(0, BarH, ClientSize.Width, Math.Max(1, ClientSize.Height - BarH));

    /// <param name="image">Owned by the editor from now on.</param>
    /// <param name="source">Where the capture was on screen (physical px): the editor opens on that monitor.</param>
    public EditorForm(Bitmap image, string path, Rectangle source)
    {
        _image = image;
        _path = path;
        _display = image.Clone(new Rectangle(Point.Empty, image.Size), PixelFormat.Format32bppPArgb);

        var center = new Point(source.Left + source.Width / 2, source.Top + source.Height / 2);
        var wa = Screen.FromPoint(center).WorkingArea;
        _imgScale = Native.ScaleAt(center);
        _s = Native.ScaleAt(new Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2));

        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = false;
        BackColor = CanvasBg;
        Icon = TrayApp.LoadAppIcon();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        MakeFonts();
        BuildToolbar();

        // Size the window to the image (1:1 if it fits in ~90% of the monitor), centred on the capture's monitor.
        int chromeW = (int)(16 * _s), chromeH = (int)(39 * _s);
        LayoutToolbar(int.MaxValue / 2); // measures the minimum toolbar width
        int minW = Math.Min(_minBarWidth + chromeW, wa.Width);
        int w = Math.Min(Math.Max(image.Width + 2 * Pad + chromeW, minW), Math.Max((int)(wa.Width * 0.92), minW));
        LayoutToolbar(w - chromeW);      // one row or two at this width
        int h = Math.Min(Math.Max(image.Height + 2 * Pad + BarH + chromeH, (int)(360 * _s)), (int)(wa.Height * 0.92));
        Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
        MinimumSize = new Size(minW, Math.Min((int)(300 * _s), wa.Height));

        _tipTimer.Tick += (_, _) => ShowTip();
        _caretTimer.Tick += (_, _) => { _caretOn = !_caretOn; Invalidate(CanvasRect); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast = null; Invalidate(); };
        UpdateTitle();
    }

    private void MakeFonts()
    {
        _uiFont?.Dispose();
        _toastFont?.Dispose();
        _uiFont = new Font("Segoe UI", 13f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
        _toastFont = new Font("Segoe UI", 14f * _s, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private void UpdateTitle()
    {
        string name = _path.Length > 0 ? Path.GetFileName(_path) : "Screenshot";
        Text = (Dirty ? "• " : "") + name + " — Snapshot";
    }

    // ---- window ------------------------------------------------------------------------------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1; // dark title bar (Windows 10 20H1+ / 11); harmless if unsupported
        try { Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)); }
        catch (DllNotFoundException) { }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED)
        {
            // Moved to a monitor with different scaling: rescale our own UI and take Windows' suggested size.
            _s = ((int)m.WParam & 0xFFFF) / 96f;
            var r = Marshal.PtrToStructure<Native.RECT>(m.LParam);
            MakeFonts();
            LayoutToolbar(ClientSize.Width);
            Native.SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_items.Count == 0) return;
        LayoutToolbar(ClientSize.Width);
        if (_fit) _zoom = FitZoom();
        ClampOffset();
        Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitView();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_closeWithoutPrompt || e.Cancel) return;
        CommitText();
        if (!Dirty) return;
        var answer = MessageBox.Show(this, "Save your changes to the screenshot before closing?", "Snapshot",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save()))
            e.Cancel = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _tipTimer.Dispose();
            _caretTimer.Dispose();
            _toastTimer.Dispose();
            _cache?.Dispose();
            _display.Dispose();
            _image.Dispose();
            _uiFont.Dispose();
            _toastFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- toolbar -----------------------------------------------------------------------------------

    private void BuildToolbar()
    {
        Item ToolItem(Tool t, string tip, Action<Graphics, Rectangle, Color> icon) => new()
        {
            Tip = () => tip, Icon = icon, On = () => s_tool == t, Click = () => SetTool(t),
        };
        _items.Add(ToolItem(Tool.Arrow, "Arrow (A)  ·  hold Shift for 45°", IconArrow));
        _items.Add(ToolItem(Tool.Rect, "Rectangle (R)  ·  hold Shift for a square", IconRect));
        _items.Add(ToolItem(Tool.Ellipse, "Ellipse (E)  ·  hold Shift for a circle", IconEllipse));
        _items.Add(ToolItem(Tool.Text, "Text (T)  ·  click to type, Esc to finish", IconText));
        _items.Add(null!); // separator

        string[] sizeNames = { "Small", "Medium", "Large" };
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            _items.Add(new Item
            {
                Tip = () => $"{sizeNames[k]} ({k + 1})",
                On = () => s_size == k,
                Click = () => SetSize(k),
                Icon = (g, r, c) =>
                {
                    float d = (5 + 3.5f * k) * _s;
                    using var b = new SolidBrush(c);
                    g.FillEllipse(b, r.Left + (r.Width - d) / 2, r.Top + (r.Height - d) / 2, d, d);
                },
            });
        }
        _items.Add(new Item
        {
            Tip = () => s_tool == Tool.Text ? "Text background (F)" : "Filled shapes (F)",
            On = () => s_tool == Tool.Text ? s_textPill : s_fillShapes,
            Enabled = () => s_tool != Tool.Arrow,
            Click = ToggleFill,
            Icon = IconFill,
        });
        _items.Add(null!);

        for (int i = 0; i < Presets.Length; i++)
        {
            var c = Presets[i];
            _items.Add(new Item
            {
                Swatch = true,
                Tip = () => "Colour",
                On = () => s_color.ToArgb() == c.ToArgb(),
                Click = () => SetColor(c),
                Icon = (g, r, _) => DrawSwatch(g, r, c, s_color.ToArgb() == c.ToArgb(), false),
            });
        }
        _items.Add(new Item
        {
            Swatch = true,
            Tip = () => "Custom colour…",
            Click = PickCustomColor,
            Icon = (g, r, _) => DrawSwatch(g, r, s_custom, IsCustomColor, true),
        });

        // Right-hand group (laid out from the right edge).
        _items.Add(null!);
        _items.Add(new Item
        {
            Tip = () => _fit ? "Show at 100% (Ctrl+1)" : "Fit to window (Ctrl+0)",
            Label = () => $"{Math.Round(_zoom * 100)}%",
            Click = () => { if (_fit) ZoomAt(1f, Center()); else FitView(); },
        });
        _items.Add(new Item { Tip = () => "Undo (Ctrl+Z)", Icon = (g, r, c) => IconUndo(g, r, c, false), Enabled = () => _index > 0 || _editing != null, Click = Undo });
        _items.Add(new Item { Tip = () => "Redo (Ctrl+Y)", Icon = (g, r, c) => IconUndo(g, r, c, true), Enabled = () => _index < _history.Count - 1, Click = Redo });
        _items.Add(new Item { Tip = () => "Copy to clipboard (Ctrl+C)", Label = () => "Copy", Click = () => Copy() });
        _items.Add(new Item { Tip = () => "Save over the original PNG (Ctrl+S)", Label = () => "Save", Click = () => Save() });
        _items.Add(new Item { Tip = () => "Save a new file (Ctrl+Shift+S)", Label = () => "Save As", Click = SaveAs });
        _items.Add(new Item { Tip = () => "Save, copy and close (Ctrl+Enter)", Label = () => "Done", Primary = true, Click = Done });
    }

    private bool IsCustomColor => !Presets.Any(p => p.ToArgb() == s_color.ToArgb());

    /// <summary>
    /// Positions items for a client width. Null entries are separators; the last one splits the drawing tools
    /// (left) from the actions (right). If both don't fit on one row (e.g. a narrow portrait monitor), the
    /// actions wrap to a second row.
    /// </summary>
    private void LayoutToolbar(int width)
    {
        int rowH = (int)(52 * _s), bh = (int)(34 * _s), gap = (int)(4 * _s), sepW = (int)(14 * _s), margin = (int)(10 * _s);
        int iconW = (int)(36 * _s), swatchW = (int)(28 * _s), padX = (int)(12 * _s);
        int split = _items.LastIndexOf(null!);
        _seps.Clear();

        int ItemWidth(Item it)
        {
            if (it.Swatch) return swatchW;
            if (it.Label == null) return iconW;
            string text = it.Label().EndsWith("%") ? "1600%" : it.Label(); // zoom label: room for the widest value
            return Math.Max(TextRenderer.MeasureText(text, _uiFont).Width + 2 * padX, iconW);
        }

        // Left group, row 1.
        int y = (rowH - bh) / 2, x = margin;
        for (int i = 0; i < split; i++)
        {
            var it = _items[i];
            if (it == null) { _seps.Add(new Point(x - gap + sepW / 2, 0)); x += sepW - gap; continue; }
            int w = ItemWidth(it);
            it.R = new Rectangle(x, y, w, bh);
            x += w + gap;
        }
        int leftEnd = x;

        int rightW = 0;
        for (int i = split + 1; i < _items.Count; i++) rightW += ItemWidth(_items[i]) + gap;
        rightW += sepW; // between zoom label and undo

        int oneRow = leftEnd + (int)(16 * _s) + rightW + margin;
        int twoRows = Math.Max(leftEnd, rightW + 2 * margin);
        _rows = width >= oneRow ? 1 : 2;
        _minBarWidth = twoRows;

        // Right group, right-aligned on row 1 or row 2.
        int top = (_rows - 1) * rowH;
        y = top + (rowH - bh) / 2;
        int right = width - margin;
        for (int i = _items.Count - 1; i > split; i--)
        {
            var it = _items[i];
            int w = ItemWidth(it);
            right -= w;
            it.R = new Rectangle(right, y, w, bh);
            right -= gap;
            if (i == split + 2) { _seps.Add(new Point(right - sepW / 2 + gap, top)); right -= sepW; } // after zoom label
        }
    }

    private void PaintToolbar(Graphics g)
    {
        int rowH = (int)(52 * _s);
        using (var bg = new SolidBrush(BarBg)) g.FillRectangle(bg, 0, 0, ClientSize.Width, BarH);
        using (var line = new Pen(Color.FromArgb(58, 58, 58))) g.DrawLine(line, 0, BarH - 1, ClientSize.Width, BarH - 1);
        using (var sep = new Pen(Color.FromArgb(72, 72, 72)))
            foreach (var sp in _seps) g.DrawLine(sep, sp.X, sp.Y + rowH * 0.28f, sp.X, sp.Y + rowH * 0.72f);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var it in _items)
        {
            if (it == null) continue;
            bool enabled = it.Enabled(), hot = enabled && it == _hot, on = it.On();
            bool isSwatch = it.Swatch;
            Color fg = enabled ? Fg : FgDisabled;
            var r = it.R;

            if (it.Primary || (on && !isSwatch) || (hot && !isSwatch) || it.Label != null)
            {
                Color fill = it.Primary ? (hot ? AccentHover : Accent)
                           : on ? Accent
                           : hot ? Hover
                           : it.Label != null && !it.Label().EndsWith("%") ? Color.FromArgb(52, 52, 52)
                           : BarBg;
                using var path = Annot.RoundRect(r, 6 * _s);
                using var b = new SolidBrush(fill);
                g.FillPath(b, path);
                if (on || it.Primary) fg = Color.White;
            }
            if (it.Icon != null) it.Icon(g, r, fg);
            if (it.Label != null)
                TextRenderer.DrawText(g, it.Label(), _uiFont, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    private Rectangle IconBox(Rectangle r)
    {
        int d = (int)(18 * _s);
        return new Rectangle(r.Left + (r.Width - d) / 2, r.Top + (r.Height - d) / 2, d, d);
    }

    private Pen IconPen(Color c) => new(c, 1.8f * _s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    private void IconArrow(Graphics g, Rectangle r, Color c)
    {
        var b = IconBox(r);
        using var p = IconPen(c);
        p.CustomEndCap = new AdjustableArrowCap(3.2f, 3.2f, true);
        g.DrawLine(p, b.Left + 2 * _s, b.Bottom - 2 * _s, b.Right - 2 * _s, b.Top + 2 * _s);
    }

    private void IconRect(Graphics g, Rectangle r, Color c)
    {
        var b = IconBox(r);
        b.Inflate((int)(-1 * _s), (int)(-2 * _s));
        using var p = IconPen(c);
        using var path = Annot.RoundRect(b, 2.5f * _s);
        g.DrawPath(p, path);
    }

    private void IconEllipse(Graphics g, Rectangle r, Color c)
    {
        var b = IconBox(r);
        b.Inflate((int)(-1 * _s), (int)(-1 * _s));
        using var p = IconPen(c);
        g.DrawEllipse(p, b);
    }

    private void IconText(Graphics g, Rectangle r, Color c)
    {
        var b = IconBox(r);
        using var p = IconPen(c);
        p.Width = 2.2f * _s;
        float m = 2.5f * _s;
        g.DrawLine(p, b.Left + m, b.Top + m, b.Right - m, b.Top + m);
        g.DrawLine(p, b.Left + b.Width / 2f, b.Top + m, b.Left + b.Width / 2f, b.Bottom - m * 0.5f);
    }

    private void IconFill(Graphics g, Rectangle r, Color c)
    {
        var b = IconBox(r);
        b.Inflate((int)(-1 * _s), (int)(-2 * _s));
        using var path = Annot.RoundRect(b, 3 * _s);
        using var br = new SolidBrush(Color.FromArgb(150, c));
        g.FillPath(br, path);
        using var p = IconPen(c);
        g.DrawPath(p, path);
    }

    private void IconUndo(Graphics g, Rectangle r, Color c, bool redo)
    {
        var b = IconBox(r);
        var arc = new RectangleF(b.Left + 1.5f * _s, b.Top + 3 * _s, b.Width - 3 * _s, b.Height - 3 * _s);
        using var p = IconPen(c);
        p.CustomEndCap = new AdjustableArrowCap(3f, 3f, true);
        if (redo) g.DrawArc(p, arc, 150, 225);
        else g.DrawArc(p, arc, 30, -225);
    }

    private void DrawSwatch(Graphics g, Rectangle r, Color c, bool selected, bool custom)
    {
        float d = 18 * _s;
        var rc = new RectangleF(r.Left + (r.Width - d) / 2, r.Top + (r.Height - d) / 2, d, d);
        if (custom)
        {
            // Rainbow ring around the custom colour.
            Color[] ring = { Presets[0], Color.FromArgb(255, 149, 0), Presets[1], Presets[2], Presets[3], Color.FromArgb(175, 82, 222) };
            for (int i = 0; i < ring.Length; i++)
                using (var b = new SolidBrush(ring[i])) g.FillPie(b, rc.X, rc.Y, rc.Width, rc.Height, i * 60 - 90, 60);
            var inner = rc;
            inner.Inflate(-3.5f * _s, -3.5f * _s);
            using (var bg = new SolidBrush(BarBg)) g.FillEllipse(bg, RectangleF.Inflate(inner, 1f * _s, 1f * _s));
            using (var b = new SolidBrush(c)) g.FillEllipse(b, inner);
        }
        else
        {
            using var b = new SolidBrush(c);
            g.FillEllipse(b, rc);
            using var edge = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
            g.DrawEllipse(edge, rc);
        }
        if (selected)
        {
            var ring = rc;
            ring.Inflate(3.5f * _s, 3.5f * _s);
            using var p = new Pen(Color.White, 2f * _s);
            g.DrawEllipse(p, ring);
        }
    }

    private Item? ItemAt(Point p) => p.Y < BarH ? _items.FirstOrDefault(it => it != null && it.R.Contains(p)) : null;

    private void ShowTip()
    {
        _tipTimer.Stop();
        if (_hot == null) return;
        _tip.Show(_hot.Tip(), this, _hot.R.Left, _hot.R.Bottom + (int)(6 * _s), 3000);
    }

    // ---- tool state --------------------------------------------------------------------------------

    private void SetTool(Tool t)
    {
        CommitText();
        s_tool = t;
        UpdateCursor(PointToClient(Cursor.Position));
        Invalidate();
    }

    private void SetSize(int k)
    {
        s_size = k;
        if (_editing != null) { _editing.Px = TextSizes[k] * _imgScale; }
        Invalidate();
    }

    private void SetColor(Color c)
    {
        s_color = c;
        if (_editing != null) _editing.Color = c;
        Invalidate();
    }

    private void ToggleFill()
    {
        if (s_tool == Tool.Text)
        {
            s_textPill = !s_textPill;
            if (_editing != null) _editing.Pill = s_textPill;
        }
        else if (s_tool != Tool.Arrow) s_fillShapes = !s_fillShapes;
        Invalidate();
    }

    private void PickCustomColor()
    {
        using var dlg = new ColorDialog { FullOpen = true, Color = s_custom, AnyColor = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        s_custom = Color.FromArgb(255, dlg.Color);
        SetColor(s_custom);
    }

    private float Stroke => StrokeSizes[s_size] * _imgScale;

    // ---- history -----------------------------------------------------------------------------------

    private void Push(List<Annot> list)
    {
        if (_index < _history.Count - 1) _history.RemoveRange(_index + 1, _history.Count - _index - 1);
        if (_savedIndex > _index) _savedIndex = -1; // the saved state can no longer be reached by redo
        _history.Add(list);
        _index++;
        UpdateTitle();
        Invalidate();
    }

    private void Undo()
    {
        if (_editing != null) { CommitText(); }
        if (_index > 0) { _index--; UpdateTitle(); Invalidate(); }
    }

    private void Redo()
    {
        CommitText();
        if (_index < _history.Count - 1) { _index++; UpdateTitle(); Invalidate(); }
    }

    // ---- text editing ------------------------------------------------------------------------------

    private void BeginText(PointF imagePt)
    {
        var t = new TextAnnot(PointF.Empty, TextSizes[s_size] * _imgScale, s_textPill, s_color);
        t.Pos = new PointF(imagePt.X, imagePt.Y - t.LineHeight / 2);
        _editing = t;
        _editIndex = -1;
        _caret = 0;
        StartCaret();
    }

    private void BeginEdit(int index)
    {
        var src = (TextAnnot)Current[index];
        _editing = src.Clone();
        _editIndex = index;
        _caret = _editing.Text.Length;
        // Pick up its style so the toolbar shows what is being edited.
        s_color = src.Color;
        s_textPill = src.Pill;
        int k = Array.FindIndex(TextSizes, v => Math.Abs(v * _imgScale - src.Px) < 0.01f);
        if (k >= 0) s_size = k;
        StartCaret();
    }

    private void StartCaret()
    {
        _caretOn = true;
        _caretTimer.Stop();
        _caretTimer.Start();
        Invalidate();
    }

    private void CommitText()
    {
        if (_editing == null) return;
        var t = _editing;
        int idx = _editIndex;
        _editing = null;
        _editIndex = -1;
        _caretTimer.Stop();
        t.Text = t.Text.TrimEnd('\n', ' ');
        bool empty = t.Text.Trim().Length == 0;

        var list = Current.ToList();
        if (idx >= 0)
        {
            if (empty) list.RemoveAt(idx);
            else if (t.SameAs((TextAnnot)list[idx])) { Invalidate(); return; }
            else list[idx] = t;
        }
        else
        {
            if (empty) { Invalidate(); return; }
            list.Add(t);
        }
        Push(list);
    }

    private void InsertText(string s)
    {
        if (_editing == null || s.Length == 0) return;
        _editing.Text = _editing.Text.Insert(_caret, s);
        _caret += s.Length;
        StartCaret();
    }

    /// <summary>Keys while typing. Returns true if handled; plain characters arrive through OnKeyPress.</summary>
    private bool HandleTextKey(Keys key, bool ctrl, bool shift, bool alt)
    {
        var t = _editing!;
        if (ctrl && alt) return false; // AltGr characters
        switch (key)
        {
            case Keys.Escape: CommitText(); return true;
            case Keys.Enter when ctrl: CommitText(); return false; // falls through to Done
            case Keys.Enter: InsertText("\n"); return true;
            case Keys.Back:
                if (_caret > 0) { t.Text = t.Text.Remove(_caret - 1, 1); _caret--; StartCaret(); }
                return true;
            case Keys.Delete:
                if (_caret < t.Text.Length) { t.Text = t.Text.Remove(_caret, 1); StartCaret(); }
                return true;
            case Keys.Left: _caret = Math.Max(0, _caret - 1); StartCaret(); return true;
            case Keys.Right: _caret = Math.Min(t.Text.Length, _caret + 1); StartCaret(); return true;
            case Keys.Home: { var (l, _) = t.LineCol(_caret); _caret = t.IndexOf(l, 0); StartCaret(); return true; }
            case Keys.End: { var (l, _) = t.LineCol(_caret); _caret = t.IndexOf(l, int.MaxValue); StartCaret(); return true; }
            case Keys.Up: case Keys.Down:
            {
                var (l, c) = t.LineCol(_caret);
                _caret = t.IndexOf(l + (key == Keys.Up ? -1 : 1), c);
                StartCaret();
                return true;
            }
            case Keys.V when ctrl:
                if (Clipboard.ContainsText()) InsertText(Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'));
                return true;
        }
        if (ctrl) CommitText(); // Ctrl+Z / Ctrl+C / Ctrl+S ...: finish the text, then run the shortcut
        return false;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_editing != null && !char.IsControl(e.KeyChar))
        {
            InsertText(e.KeyChar.ToString());
            e.Handled = true;
        }
    }

    // ---- keyboard ----------------------------------------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
        bool ctrl = (mods & Keys.Control) != 0, shift = (mods & Keys.Shift) != 0, alt = (mods & Keys.Alt) != 0;

        if (_editing != null && HandleTextKey(key, ctrl, shift, alt)) return true;

        if (ctrl && !alt)
        {
            switch (key)
            {
                case Keys.Z: if (shift) Redo(); else Undo(); return true;
                case Keys.Y: Redo(); return true;
                case Keys.C: Copy(); return true;
                case Keys.S: if (shift) SaveAs(); else Save(); return true;
                case Keys.Enter: Done(); return true;
                case Keys.D0: case Keys.NumPad0: FitView(); return true;
                case Keys.D1: case Keys.NumPad1: ZoomAt(1f, Center()); return true;
                case Keys.Oemplus: case Keys.Add: ZoomStep(1, Center()); return true;
                case Keys.OemMinus: case Keys.Subtract: ZoomStep(-1, Center()); return true;
            }
        }
        if (mods == Keys.None && _editing == null)
        {
            switch (key)
            {
                case Keys.A: SetTool(Tool.Arrow); return true;
                case Keys.R: SetTool(Tool.Rect); return true;
                case Keys.E: SetTool(Tool.Ellipse); return true;
                case Keys.T: SetTool(Tool.Text); return true;
                case Keys.F: ToggleFill(); return true;
                case Keys.D1: SetSize(0); return true;
                case Keys.D2: SetSize(1); return true;
                case Keys.D3: SetSize(2); return true;
                case Keys.Space:
                    _spaceDown = true;
                    UpdateCursor(PointToClient(Cursor.Position));
                    return true;
                case Keys.Escape:
                    if (_draft != null) { _draft = null; Capture = false; Invalidate(); }
                    else Close();
                    return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space) { _spaceDown = false; UpdateCursor(PointToClient(Cursor.Position)); }
    }

    // ---- mouse -------------------------------------------------------------------------------------

    private PointF ToImage(Point p) => new((p.X - _offset.X) / _zoom, (p.Y - _offset.Y) / _zoom);
    private PointF ToClient(PointF p) => new(p.X * _zoom + _offset.X, p.Y * _zoom + _offset.Y);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _tip.Hide(this);
        _tipTimer.Stop();

        if (e.Y < BarH)
        {
            if (e.Button == MouseButtons.Left) { _pressed = ItemAt(e.Location); }
            return;
        }

        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _spaceDown))
        {
            _panFrom = e.Location;
            _panOffsetFrom = _offset;
            Cursor = Cursors.SizeAll;
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        var pt = ToImage(e.Location);
        if (s_tool == Tool.Text)
        {
            if (_editing != null)
            {
                if (_editing.Box.Contains(pt)) { _caret = _editing.HitIndex(pt); StartCaret(); return; }
                CommitText(); // click outside finishes the text (like CleanShot); the next click starts a new one
                return;
            }
            for (int i = Current.Count - 1; i >= 0; i--)
                if (Current[i] is TextAnnot t && t.Box.Contains(pt)) { BeginEdit(i); _caret = _editing!.HitIndex(pt); return; }
            BeginText(pt);
            return;
        }

        CommitText();
        _dragFrom = pt;
        _draft = MakeDraft(pt, false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panFrom is Point from)
        {
            _offset = new PointF(_panOffsetFrom.X + e.X - from.X, _panOffsetFrom.Y + e.Y - from.Y);
            ClampOffset();
            Invalidate();
            return;
        }
        if (_draft != null)
        {
            _draft = MakeDraft(ToImage(e.Location), (ModifierKeys & Keys.Shift) != 0);
            Invalidate(CanvasRect);
            return;
        }

        var hot = _pressed == null ? ItemAt(e.Location) : _pressed;
        if (hot != null && !hot.Enabled()) hot = null;
        if (hot != _hot)
        {
            _hot = hot;
            _tip.Hide(this);
            _tipTimer.Stop();
            if (_hot != null) _tipTimer.Start();
            Invalidate(new Rectangle(0, 0, ClientSize.Width, BarH));
        }
        UpdateCursor(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panFrom != null) { _panFrom = null; UpdateCursor(e.Location); return; }

        if (_pressed != null)
        {
            var it = _pressed;
            _pressed = null;
            if (e.Button == MouseButtons.Left && ItemAt(e.Location) == it && it.Enabled())
            {
                try { it.Click(); }
                catch (Exception ex) { Debug.WriteLine(ex); ShowToast("Failed"); }
            }
            Invalidate();
            return;
        }

        if (_draft != null && e.Button == MouseButtons.Left)
        {
            var d = MakeDraft(ToImage(e.Location), (ModifierKeys & Keys.Shift) != 0);
            _draft = null;
            float minLen = 4 / _zoom; // ignore plain clicks
            bool keep = d switch
            {
                ArrowAnnot a => a.Length >= minLen,
                ShapeAnnot s => s.Rect.Width >= minLen && s.Rect.Height >= minLen,
                _ => false,
            };
            if (keep) Push(Current.Append(d).ToList());
            else Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot != null && _pressed == null) { _hot = null; _tipTimer.Stop(); _tip.Hide(this); Invalidate(new Rectangle(0, 0, ClientSize.Width, BarH)); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0)
        {
            ZoomStep(e.Delta > 0 ? 1 : -1, e.Location);
            return;
        }
        float step = e.Delta / 120f * 80 * _s;
        if ((ModifierKeys & Keys.Shift) != 0) _offset.X += step;
        else _offset.Y += step;
        ClampOffset();
        Invalidate();
    }

    private void UpdateCursor(Point p)
    {
        Cursor c;
        if (_panFrom != null) c = Cursors.SizeAll;
        else if (p.Y < BarH) c = ItemAt(p) is Item it && it.Enabled() ? Cursors.Hand : Cursors.Default;
        else if (_spaceDown) c = Cursors.Hand;
        else if (s_tool == Tool.Text) c = Cursors.IBeam;
        else c = Cursors.Cross;
        if (Cursor != c) Cursor = c;
    }

    private Annot MakeDraft(PointF to, bool shift)
    {
        var from = _dragFrom;
        if (s_tool == Tool.Arrow)
        {
            if (shift) // snap to 45°
            {
                double dx = to.X - from.X, dy = to.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
                double a = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                to = new PointF(from.X + (float)(Math.Cos(a) * len), from.Y + (float)(Math.Sin(a) * len));
            }
            return new ArrowAnnot(from, to, Stroke, s_color);
        }
        float w = to.X - from.X, h = to.Y - from.Y;
        if (shift) // square / circle
        {
            float m = Math.Max(Math.Abs(w), Math.Abs(h));
            w = Math.Sign(w == 0 ? 1 : w) * m;
            h = Math.Sign(h == 0 ? 1 : h) * m;
        }
        var r = new RectangleF(Math.Min(from.X, from.X + w), Math.Min(from.Y, from.Y + h), Math.Abs(w), Math.Abs(h));
        return new ShapeAnnot(r, s_tool == Tool.Ellipse, s_fillShapes, Stroke, s_color);
    }

    // ---- view (zoom / pan) -------------------------------------------------------------------------

    private Point Center() { var c = CanvasRect; return new Point(c.Left + c.Width / 2, c.Top + c.Height / 2); }

    private float FitZoom()
    {
        var c = CanvasRect;
        float z = Math.Min((c.Width - 2f * Pad) / _image.Width, (c.Height - 2f * Pad) / _image.Height);
        return Math.Max(0.02f, Math.Min(1f, z));
    }

    private void FitView()
    {
        _fit = true;
        _zoom = FitZoom();
        ClampOffset();
        Invalidate();
    }

    private void ZoomStep(int dir, Point at)
    {
        float z = dir > 0 ? _zoom * 1.25f : _zoom / 1.25f;
        if ((_zoom < 1 && z > 1) || (_zoom > 1 && z < 1)) z = 1; // stop at 100% on the way through
        ZoomAt(z, at);
    }

    private void ZoomAt(float z, Point at)
    {
        z = Math.Max(0.02f, Math.Min(16f, z));
        var img = ToImage(at);
        _zoom = z;
        _fit = false;
        _offset = new PointF(at.X - img.X * z, at.Y - img.Y * z);
        ClampOffset();
        Invalidate();
    }

    /// <summary>Centre the image when it fits; otherwise keep it from scrolling out of view. Whole pixels only.</summary>
    private void ClampOffset()
    {
        var c = CanvasRect;
        float w = _image.Width * _zoom, h = _image.Height * _zoom;
        float x = w <= c.Width - 2 * Pad ? c.Left + (c.Width - w) / 2 : Math.Min(c.Left + Pad, Math.Max(c.Right - Pad - w, _offset.X));
        float y = h <= c.Height - 2 * Pad ? c.Top + (c.Height - h) / 2 : Math.Min(c.Top + Pad, Math.Max(c.Bottom - Pad - h, _offset.Y));
        _offset = new PointF((float)Math.Round(x), (float)Math.Round(y));
    }

    // ---- painting ----------------------------------------------------------------------------------

    protected override void OnPaintBackground(PaintEventArgs e) { } // everything is painted in OnPaint

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var canvas = CanvasRect;
        using (var bg = new SolidBrush(CanvasBg)) g.FillRectangle(bg, canvas);

        var dest = new RectangleF(_offset.X, _offset.Y, _image.Width * _zoom, _image.Height * _zoom);
        var state = g.Save();
        g.SetClip(canvas);
        DrawImage(g, dest, canvas);

        using (var edge = new Pen(Color.FromArgb(60, 60, 60)))
            g.DrawRectangle(edge, dest.X - 1, dest.Y - 1, dest.Width + 1, dest.Height + 1);

        // Annotations, clipped to the image like the saved file.
        g.IntersectClip(dest);
        g.TranslateTransform(_offset.X, _offset.Y);
        g.ScaleTransform(_zoom, _zoom);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        for (int i = 0; i < Current.Count; i++)
            if (i != _editIndex || _editing == null) Current[i].Draw(g);
        _draft?.Draw(g);
        if (_editing != null)
        {
            _editing.Draw(g);
            if (_caretOn)
            {
                var cp = _editing.CaretPoint(_caret);
                Color cc = _editing.Pill ? Annot.Contrast(_editing.Color) : _editing.Color;
                using var cpen = new Pen(cc, Math.Max(1.5f / _zoom, _editing.Px * 0.07f));
                g.DrawLine(cpen, cp.X, cp.Y + _editing.LineHeight * 0.12f, cp.X, cp.Y + _editing.LineHeight * 0.88f);
            }
        }
        g.Restore(state);

        if (_editing != null) // dashed frame around the text being typed, in screen pixels
        {
            var b = _editing.Box;
            var tl = ToClient(b.Location);
            using var dash = new Pen(Color.FromArgb(200, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(dash, tl.X - 3, tl.Y - 3, b.Width * _zoom + 6, b.Height * _zoom + 6);
        }

        PaintToolbar(g);
        if (_toast != null) PaintToast(g);
    }

    private void DrawImage(Graphics g, RectangleF dest, Rectangle canvas)
    {
        if (_zoom < 1f)
        {
            // Pre-scale once per zoom level with high-quality filtering, then blit.
            if (_cache == null || Math.Abs(_cacheZoom - _zoom) > 1e-5f)
            {
                _cache?.Dispose();
                int cw = Math.Max(1, (int)Math.Round(_image.Width * _zoom)), ch = Math.Max(1, (int)Math.Round(_image.Height * _zoom));
                _cache = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
                using var cg = Graphics.FromImage(_cache);
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                cg.DrawImage(_display, new Rectangle(0, 0, cw, ch), 0, 0, _display.Width, _display.Height, GraphicsUnit.Pixel, attrs);
                _cacheZoom = _zoom;
            }
            g.DrawImage(_cache, new Rectangle((int)dest.X, (int)dest.Y, _cache.Width, _cache.Height));
            return;
        }

        // 100% and up: sharp pixels; only draw the visible part so big zooms stay fast.
        var vis = RectangleF.Intersect(dest, canvas);
        if (vis.Width <= 0 || vis.Height <= 0) return;
        int sx = Math.Max(0, (int)Math.Floor((vis.Left - dest.X) / _zoom));
        int sy = Math.Max(0, (int)Math.Floor((vis.Top - dest.Y) / _zoom));
        int ex = Math.Min(_image.Width, (int)Math.Ceiling((vis.Right - dest.X) / _zoom));
        int ey = Math.Min(_image.Height, (int)Math.Ceiling((vis.Bottom - dest.Y) / _zoom));
        var src = new Rectangle(sx, sy, ex - sx, ey - sy);
        var dst = new RectangleF(dest.X + sx * _zoom, dest.Y + sy * _zoom, src.Width * _zoom, src.Height * _zoom);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_display, dst, src, GraphicsUnit.Pixel);
        g.PixelOffsetMode = PixelOffsetMode.Default;
    }

    private void ShowToast(string text)
    {
        _toast = text;
        _toastTimer.Stop();
        _toastTimer.Start();
        Invalidate();
    }

    private void PaintToast(Graphics g)
    {
        var size = TextRenderer.MeasureText(_toast, _toastFont);
        int w = size.Width + (int)(36 * _s), h = size.Height + (int)(16 * _s);
        var c = CanvasRect;
        var r = new Rectangle(c.Left + (c.Width - w) / 2, c.Bottom - h - (int)(24 * _s), w, h);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Annot.RoundRect(r, h / 2f))
        using (var b = new SolidBrush(Color.FromArgb(230, 20, 20, 20)))
            g.FillPath(b, path);
        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, _toast, _toastFont, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    // ---- output ------------------------------------------------------------------------------------

    /// <summary>The capture with all annotations burned in.</summary>
    private Bitmap Flatten()
    {
        var bmp = new Bitmap(_image.Width, _image.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        // Explicit rectangles everywhere: GDI+ would otherwise rescale by the bitmaps' DPI metadata.
        g.DrawImage(_image, new Rectangle(0, 0, _image.Width, _image.Height), 0, 0, _image.Width, _image.Height, GraphicsUnit.Pixel);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        foreach (var a in Current) a.Draw(g);
        return bmp;
    }

    private bool Copy()
    {
        CommitText();
        try
        {
            using var bmp = Flatten();
            ScreenCapture.CopyToClipboard(bmp);
            ShowToast("Copied");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't copy", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>Ctrl+S: overwrite the original PNG (or save a new one if the original was never written).</summary>
    private bool Save()
    {
        CommitText();
        try
        {
            using var bmp = Flatten();
            if (_path.Length == 0) _path = ScreenCapture.SavePng(bmp);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                bmp.Save(_path, ImageFormat.Png);
            }
            _savedIndex = _index;
            UpdateTitle();
            ShowToast("Saved");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void SaveAs()
    {
        CommitText();
        using var dlg = new SaveFileDialog
        {
            Title = "Save annotated screenshot as",
            FileName = _path.Length > 0 ? Path.GetFileNameWithoutExtension(_path) + " (edited).png" : "Snapshot.png",
            InitialDirectory = _path.Length > 0 ? Path.GetDirectoryName(_path) : ScreenCapture.Folder,
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            DefaultExt = "png",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string ext = Path.GetExtension(dlg.FileName);
            bool jpg = dlg.FilterIndex == 2 || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                                            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            using var bmp = Flatten();
            bmp.Save(dlg.FileName, jpg ? ImageFormat.Jpeg : ImageFormat.Png);
            ShowToast("Saved");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Save over the original (if changed), copy, close.</summary>
    private void Done()
    {
        CommitText();
        if (Dirty && !Save()) return;
        if (!Copy()) return;
        _closeWithoutPrompt = true;
        Close();
    }
}

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Snapshot;

/// <summary>
/// One annotation, in image pixel coordinates. Committed annotations are never mutated (the undo history keeps
/// references to them); a change replaces the object with a new one. Only the text being typed is edited in place.
/// </summary>
internal abstract class Annot
{
    public Color Color { get; set; }
    protected Annot(Color color) { Color = color; }

    /// <summary>Draws in image coordinates. Callers set the transform and SmoothingMode.AntiAlias.</summary>
    public abstract void Draw(Graphics g);

    /// <summary>
    /// Draws a list the way it is saved: blur/pixelate first (they belong to the picture underneath), then
    /// everything else in order. Numbered steps are numbered 1, 2, 3… in list order, so they re-flow on undo.
    /// </summary>
    public static void DrawAll(Graphics g, IEnumerable<Annot> list)
    {
        var items = list as IReadOnlyCollection<Annot> ?? list.ToList();
        foreach (var a in items) if (a is BlurAnnot) a.Draw(g);
        int step = 0;
        foreach (var a in items)
        {
            if (a is BlurAnnot) continue;
            if (a is StepAnnot s) s.Draw(g, ++step);
            else a.Draw(g);
        }
    }

    protected static readonly Color ShadowColor = Color.FromArgb(60, 0, 0, 0);
    protected static readonly FontFamily Family = LoadFamily();

    private static FontFamily LoadFamily()
    {
        try { return new FontFamily("Segoe UI"); }
        catch (ArgumentException) { return FontFamily.GenericSansSerif; }
    }

    protected static void FillWithShadow(Graphics g, GraphicsPath path, Color color, float offset)
    {
        using (var shadow = (GraphicsPath)path.Clone())
        using (var m = new Matrix())
        {
            m.Translate(offset * 0.35f, offset);
            shadow.Transform(m);
            using var sb = new SolidBrush(ShadowColor);
            g.FillPath(sb, shadow);
        }
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    protected static void StrokeWithShadow(Graphics g, GraphicsPath path, Color color, float width)
    {
        float off = Math.Max(1f, width * 0.4f);
        var state = g.Save();
        g.TranslateTransform(off * 0.35f, off);
        using (var sp = new Pen(ShadowColor, width) { LineJoin = LineJoin.Round })
            g.DrawPath(sp, path);
        g.Restore(state);
        using var p = new Pen(color, width) { LineJoin = LineJoin.Round };
        g.DrawPath(p, path);
    }

    /// <summary>Dark text on light colours (yellow, white), white text on everything else.</summary>
    public static Color Contrast(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.62 ? Color.FromArgb(25, 25, 25) : Color.White;

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Max(0.01f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected static float Dist(PointF a, PointF b) =>
        (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>Arrow with a solid head, like CleanShot's. Straight and tapered, or curved (quadratic) through <see cref="Control"/>.</summary>
internal sealed class ArrowAnnot : Annot
{
    public PointF From { get; }
    public PointF To { get; }
    public PointF? Control { get; }
    public float Width { get; }

    public ArrowAnnot(PointF from, PointF to, float width, Color color, PointF? control = null) : base(color)
    {
        From = from; To = to; Width = width; Control = control;
    }

    public float Length => Dist(From, To);

    /// <summary>
    /// Control point for a curved arrow from the path the mouse took: the curve's apex sits where the drag strayed
    /// furthest from the straight line. A nearly straight drag gets a gentle default bend (upwards, or left for
    /// vertical arrows).
    /// </summary>
    public static PointF? CurveControl(PointF a, PointF b, IReadOnlyList<PointF> path)
    {
        float len = Dist(a, b);
        if (len < 1) return null;
        float nx = -(b.Y - a.Y) / len, ny = (b.X - a.X) / len;
        float best = 0;
        foreach (var p in path)
        {
            float d = (p.X - a.X) * nx + (p.Y - a.Y) * ny;
            if (Math.Abs(d) > Math.Abs(best)) best = d;
        }
        if (Math.Abs(best) < len * 0.06f)
        {
            float up = Math.Abs(ny) > 0.01f ? -Math.Sign(ny) : -Math.Sign(nx);
            best = up * len * 0.2f;
        }
        var mid = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var apex = new PointF(mid.X + nx * best, mid.Y + ny * best);
        return new PointF(2 * apex.X - mid.X, 2 * apex.Y - mid.Y); // quadratic through apex at t = 0.5
    }

    public override void Draw(Graphics g)
    {
        if (Length < 1) return;
        if (Control is PointF c) { DrawCurved(g, c); return; }

        float len = Length;
        float ux = (To.X - From.X) / len, uy = (To.Y - From.Y) / len; // direction
        float nx = -uy, ny = ux;                                          // normal

        float head = Math.Min(Width * 3.2f + 6f, len);
        float half = head * 0.62f;
        var joinP = new PointF(To.X - ux * head * 0.7f, To.Y - uy * head * 0.7f); // shaft runs into the head: no seam
        float t0 = Width * 0.32f, t1 = Width * 0.62f;                              // tail is thinner than the neck

        using var path = new GraphicsPath(FillMode.Winding);
        path.AddPolygon(new[]
        {
            new PointF(From.X + nx * t0, From.Y + ny * t0),
            new PointF(joinP.X + nx * t1, joinP.Y + ny * t1),
            new PointF(joinP.X - nx * t1, joinP.Y - ny * t1),
            new PointF(From.X - nx * t0, From.Y - ny * t0),
        });
        AddHead(path, To, ux, uy, head, half);
        FillWithShadow(g, path, Color, Math.Max(1f, Width * 0.4f));
    }

    private static void AddHead(GraphicsPath path, PointF tip, float ux, float uy, float head, float half)
    {
        var baseP = new PointF(tip.X - ux * head, tip.Y - uy * head);
        path.AddPolygon(new[]
        {
            tip,
            new PointF(baseP.X - uy * half, baseP.Y + ux * half),
            new PointF(baseP.X + uy * half, baseP.Y - ux * half),
        });
    }

    private PointF Q(PointF c, float t)
    {
        float u = 1 - t;
        return new PointF(u * u * From.X + 2 * u * t * c.X + t * t * To.X, u * u * From.Y + 2 * u * t * c.Y + t * t * To.Y);
    }

    private void DrawCurved(Graphics g, PointF c)
    {
        // Curve length, then the parameter where the head starts (walk back from the tip).
        float curveLen = 0;
        var prev = From;
        for (int i = 1; i <= 24; i++) { var p = Q(c, i / 24f); curveLen += Dist(prev, p); prev = p; }
        float head = Math.Min(Width * 3.2f + 6f, curveLen * 0.6f);
        float half = head * 0.62f;

        float tBase = 1, tJoin = 1;
        for (float t = 1; t >= 0; t -= 0.005f)
        {
            float d = Dist(Q(c, t), To);
            if (tJoin == 1 && d >= head * 0.7f) tJoin = t;
            if (d >= head) { tBase = t; break; }
        }
        var basePt = Q(c, tBase);
        float hl = Dist(basePt, To);
        if (hl < 0.5f) return;
        float ux = (To.X - basePt.X) / hl, uy = (To.Y - basePt.Y) / hl;

        // Shaft = the curve from 0..tJoin (split with de Casteljau), stroked and turned into an outline.
        var q1 = new PointF(From.X + tJoin * (c.X - From.X), From.Y + tJoin * (c.Y - From.Y));
        var pj = Q(c, tJoin);
        using var shaft = new GraphicsPath();
        shaft.AddBezier(From,
            new PointF(From.X + 2f / 3 * (q1.X - From.X), From.Y + 2f / 3 * (q1.Y - From.Y)),
            new PointF(pj.X + 2f / 3 * (q1.X - pj.X), pj.Y + 2f / 3 * (q1.Y - pj.Y)),
            pj);
        using (var pen = new Pen(Color.Black, Width * 1.05f) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
            shaft.Widen(pen);

        using var path = new GraphicsPath(FillMode.Winding);
        path.AddPath(shaft, false);
        AddHead(path, To, ux, uy, head, half);
        FillWithShadow(g, path, Color, Math.Max(1f, Width * 0.4f));
    }
}

/// <summary>Rectangle (slightly rounded) or ellipse, outlined or filled.</summary>
internal sealed class ShapeAnnot : Annot
{
    public RectangleF Rect { get; }
    public bool Ellipse { get; }
    public bool Filled { get; }
    public float Width { get; }

    public ShapeAnnot(RectangleF rect, bool ellipse, bool filled, float width, Color color) : base(color)
    {
        Rect = rect; Ellipse = ellipse; Filled = filled; Width = width;
    }

    public override void Draw(Graphics g)
    {
        if (Rect.Width < 0.5f || Rect.Height < 0.5f) return;
        using var path = new GraphicsPath();
        if (Ellipse) path.AddEllipse(Rect);
        else
        {
            using var rr = RoundRect(Rect, Width * 1.2f);
            path.AddPath(rr, false);
        }
        if (Filled) FillWithShadow(g, path, Color, Math.Max(1f, Width * 0.4f));
        else StrokeWithShadow(g, path, Color, Width);
    }
}

/// <summary>See-through marker stroke. Drawn as one path so overlapping parts of a stroke don't get darker.</summary>
internal sealed class HighlightAnnot : Annot
{
    public PointF[] Points { get; }
    public float Width { get; }

    public HighlightAnnot(PointF[] points, float width, Color color) : base(color)
    {
        Points = points; Width = width;
    }

    public override void Draw(Graphics g)
    {
        if (Points.Length < 2) return;
        using var path = new GraphicsPath();
        path.AddLines(Points);
        using var pen = new Pen(Color.FromArgb(115, Color), Width)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };
        g.DrawPath(pen, path);
    }
}

/// <summary>Numbered circle. The number comes from its position among the steps (see <see cref="Annot.DrawAll"/>).</summary>
internal sealed class StepAnnot : Annot
{
    public PointF Center { get; }
    public float Diameter { get; }

    public StepAnnot(PointF center, float diameter, Color color) : base(color)
    {
        Center = center; Diameter = diameter;
    }

    public override void Draw(Graphics g) => Draw(g, 1);

    public void Draw(Graphics g, int number)
    {
        float d = Diameter;
        var r = new RectangleF(Center.X - d / 2, Center.Y - d / 2, d, d);
        using (var circle = new GraphicsPath())
        {
            circle.AddEllipse(r);
            FillWithShadow(g, circle, Color, Math.Max(1f, d * 0.05f));
        }
        bool light = Contrast(Color) != Color.White;
        using (var ring = new Pen(light ? Color.FromArgb(120, 0, 0, 0) : Color.FromArgb(235, 255, 255, 255), Math.Max(1f, d * 0.07f)))
        {
            var rr = r;
            rr.Inflate(-ring.Width / 2, -ring.Width / 2);
            g.DrawEllipse(ring, rr);
        }

        string text = number.ToString();
        float em = d * (text.Length > 1 ? 0.46f : 0.56f);
        using var num = new GraphicsPath();
        num.AddString(text, Family, (int)FontStyle.Bold, em, PointF.Empty, StringFormat.GenericTypographic);
        var b = num.GetBounds();
        using (var m = new Matrix())
        {
            m.Translate(Center.X - (b.Left + b.Width / 2), Center.Y - (b.Top + b.Height / 2)); // optically centred digits
            num.Transform(m);
        }
        using var tb = new SolidBrush(Contrast(Color));
        g.FillPath(tb, num);
    }
}

/// <summary>
/// Blur or pixelate a rectangle of the screenshot. The result is computed once from the original pixels and
/// burned into the saved file, so nobody can undo it later. Without <see cref="Patch"/> it is a drag preview.
/// </summary>
internal sealed class BlurAnnot : Annot, IDisposable
{
    public Rectangle Rect { get; }
    public bool Pixelate { get; }
    public float Strength { get; }   // blur radius or pixel block size, in image px
    private readonly Bitmap? _patch;

    public BlurAnnot(Rectangle rect, bool pixelate, float strength, Bitmap? source) : base(Color.Empty)
    {
        Rect = rect; Pixelate = pixelate; Strength = strength;
        if (source != null && rect.Width > 0 && rect.Height > 0) _patch = MakePatch(source, rect, pixelate, strength);
    }

    public override void Draw(Graphics g)
    {
        if (_patch == null)
        {
            using var fill = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
            g.FillRectangle(fill, Rect);
            using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), Math.Max(1f, Rect.Width / 400f + 1)) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, Rect.X, Rect.Y, Rect.Width, Rect.Height);
            return;
        }
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.NearestNeighbor; // exact pixels at 1:1 (the saved file)
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_patch, Rect, 0, 0, _patch.Width, _patch.Height, GraphicsUnit.Pixel);
        g.Restore(state);
    }

    public void Dispose() => _patch?.Dispose();

    private static Bitmap MakePatch(Bitmap source, Rectangle rect, bool pixelate, float strength)
    {
        int w = rect.Width, h = rect.Height;
        var px = new int[w * h];
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w, w);
        }
        finally { source.UnlockBits(data); }

        if (pixelate) PixelateInPlace(px, w, h, Math.Max(2, (int)Math.Round(strength)));
        else
        {
            int r = Math.Max(1, (int)Math.Round(strength));
            for (int pass = 0; pass < 3; pass++) // three box blurs ≈ a Gaussian
            {
                BoxBlur(px, w, h, r, horizontal: true);
                BoxBlur(px, w, h, r, horizontal: false);
            }
        }

        var patch = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var pd = patch.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(px, y * w, pd.Scan0 + y * pd.Stride, w);
        }
        finally { patch.UnlockBits(pd); }
        return patch;
    }

    /// <summary>Running-sum box blur along rows or columns; edges clamp to the rectangle (no outside pixels).</summary>
    private static void BoxBlur(int[] px, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int step = horizontal ? 1 : w;
        var line = new int[len];
        int win = 2 * r + 1;
        for (int l = 0; l < lines; l++)
        {
            int start = horizontal ? l * w : l;
            for (int i = 0; i < len; i++) line[i] = px[start + i * step];
            int sr = 0, sg = 0, sb = 0;
            for (int k = -r; k <= r; k++)
            {
                int c = line[Native.Clamp(k, 0, len - 1)];
                sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255;
            }
            for (int i = 0; i < len; i++)
            {
                px[start + i * step] = unchecked((int)0xFF000000) | ((sr / win) << 16) | ((sg / win) << 8) | (sb / win);
                int cOut = line[Native.Clamp(i - r, 0, len - 1)], cIn = line[Native.Clamp(i + r + 1, 0, len - 1)];
                sr += ((cIn >> 16) & 255) - ((cOut >> 16) & 255);
                sg += ((cIn >> 8) & 255) - ((cOut >> 8) & 255);
                sb += (cIn & 255) - (cOut & 255);
            }
        }
    }

    private static void PixelateInPlace(int[] px, int w, int h, int block)
    {
        for (int by = 0; by < h; by += block)
        for (int bx = 0; bx < w; bx += block)
        {
            int ex = Math.Min(w, bx + block), ey = Math.Min(h, by + block);
            long sr = 0, sg = 0, sb = 0, n = 0;
            for (int y = by; y < ey; y++)
            for (int x = bx; x < ex; x++)
            {
                int c = px[y * w + x];
                sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255; n++;
            }
            int avg = unchecked((int)0xFF000000) | (int)(sr / n) << 16 | (int)(sg / n) << 8 | (int)(sb / n);
            for (int y = by; y < ey; y++)
            for (int x = bx; x < ex; x++)
                px[y * w + x] = avg;
        }
    }
}

/// <summary>
/// Multi-line text. Drawn as a vector path so it looks the same at every zoom level and in the saved file.
/// With <see cref="Pill"/> it sits on a rounded background in the chosen colour; otherwise it gets a contrasting outline.
/// </summary>
internal sealed class TextAnnot : Annot
{
    public PointF Pos;        // top-left of the first line (excluding padding)
    public string Text = "";
    public float Px;          // font size in image pixels
    public bool Pill;

    private const FontStyle Style = FontStyle.Bold;
    private static readonly Graphics Measure = MakeMeasureGraphics();
    private static readonly StringFormat Typo = MakeFormat();

    public TextAnnot(PointF pos, float px, bool pill, Color color) : base(color)
    {
        Pos = pos; Px = px; Pill = pill;
    }

    public TextAnnot Clone() => new(Pos, Px, Pill, Color) { Text = Text };

    public bool SameAs(TextAnnot o) =>
        o.Text == Text && o.Pos == Pos && o.Px == Px && o.Pill == Pill && o.Color.ToArgb() == Color.ToArgb();

    private static Graphics MakeMeasureGraphics()
    {
        var g = Graphics.FromImage(new Bitmap(1, 1));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        return g;
    }

    private static StringFormat MakeFormat()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return f;
    }

    public string[] Lines => Text.Split('\n');
    public float LineHeight => Px * Family.GetLineSpacing(Style) / Family.GetEmHeight(Style);

    public float Width(string s)
    {
        if (s.Length == 0) return 0;
        using var f = new Font(Family, Px, Style, GraphicsUnit.Pixel);
        return Measure.MeasureString(s, f, PointF.Empty, Typo).Width;
    }

    public RectangleF TextRect
    {
        get
        {
            var lines = Lines;
            float w = lines.Max(Width);
            return new RectangleF(Pos.X, Pos.Y, Math.Max(w, Px * 0.3f), lines.Length * LineHeight);
        }
    }

    private float PadX => Pill ? Px * 0.45f : Px * 0.12f;
    private float PadY => Pill ? Px * 0.12f : Px * 0.06f;

    /// <summary>Everything this text paints, used for hit-testing and the editing frame.</summary>
    public RectangleF Box
    {
        get { var r = TextRect; r.Inflate(PadX, PadY); return r; }
    }

    public override void Draw(Graphics g)
    {
        if (Text.Length == 0) return;
        using var path = new GraphicsPath();
        var lines = Lines;
        float lh = LineHeight;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length > 0)
                path.AddString(lines[i], Family, (int)Style, Px, new PointF(Pos.X, Pos.Y + i * lh), Typo);

        if (Pill)
        {
            var box = Box;
            using (var bg = RoundRect(box, Math.Min(Px * 0.45f, box.Height / 2)))
                FillWithShadow(g, bg, Color, Math.Max(1f, Px * 0.06f));
            using var tb = new SolidBrush(Contrast(Color));
            g.FillPath(tb, path);
        }
        else
        {
            using (var outline = new Pen(Contrast(Color), Px * 0.16f) { LineJoin = LineJoin.Round })
                g.DrawPath(outline, path);
            using var b = new SolidBrush(Color);
            g.FillPath(b, path);
        }
    }

    /// <summary>Line number and column of a caret index.</summary>
    public (int line, int col) LineCol(int index)
    {
        int line = 0, start = 0;
        for (int i = 0; i < index && i < Text.Length; i++)
            if (Text[i] == '\n') { line++; start = i + 1; }
        return (line, index - start);
    }

    public int IndexOf(int line, int col)
    {
        var lines = Lines;
        line = Native.Clamp(line, 0, lines.Length - 1);
        int idx = 0;
        for (int i = 0; i < line; i++) idx += lines[i].Length + 1;
        return idx + Native.Clamp(col, 0, lines[line].Length);
    }

    /// <summary>Top of the caret (image coords) for a caret index.</summary>
    public PointF CaretPoint(int index)
    {
        var (line, col) = LineCol(index);
        string s = Lines[line];
        return new PointF(Pos.X + Width(s.Substring(0, col)), Pos.Y + line * LineHeight);
    }

    /// <summary>Caret index nearest to an image point.</summary>
    public int HitIndex(PointF p)
    {
        var lines = Lines;
        int line = Native.Clamp((int)Math.Floor((p.Y - Pos.Y) / LineHeight), 0, lines.Length - 1);
        string s = lines[line];
        float x = p.X - Pos.X;
        int best = 0;
        float bestDist = float.MaxValue;
        for (int c = 0; c <= s.Length; c++)
        {
            float d = Math.Abs(Width(s.Substring(0, c)) - x);
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return IndexOf(line, best);
    }
}

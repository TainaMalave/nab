using System.Drawing.Drawing2D;

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

    protected static readonly Color ShadowColor = Color.FromArgb(60, 0, 0, 0);

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
}

/// <summary>Tapered arrow with a solid head, like CleanShot's.</summary>
internal sealed class ArrowAnnot : Annot
{
    public PointF From { get; }
    public PointF To { get; }
    public float Width { get; }

    public ArrowAnnot(PointF from, PointF to, float width, Color color) : base(color)
    {
        From = from; To = to; Width = width;
    }

    public float Length => (float)Math.Sqrt((To.X - From.X) * (To.X - From.X) + (To.Y - From.Y) * (To.Y - From.Y));

    public override void Draw(Graphics g)
    {
        float len = Length;
        if (len < 1) return;
        float ux = (To.X - From.X) / len, uy = (To.Y - From.Y) / len; // direction
        float nx = -uy, ny = ux;                                          // normal

        float head = Math.Min(Width * 3.2f + 6f, len);
        float half = head * 0.62f;
        var baseP = new PointF(To.X - ux * head, To.Y - uy * head);
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
        path.AddPolygon(new[]
        {
            To,
            new PointF(baseP.X + nx * half, baseP.Y + ny * half),
            new PointF(baseP.X - nx * half, baseP.Y - ny * half),
        });
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
    private static readonly FontFamily Family = LoadFamily();
    private static readonly Graphics Measure = MakeMeasureGraphics();
    private static readonly StringFormat Typo = MakeFormat();

    public TextAnnot(PointF pos, float px, bool pill, Color color) : base(color)
    {
        Pos = pos; Px = px; Pill = pill;
    }

    public TextAnnot Clone() => new(Pos, Px, Pill, Color) { Text = Text };

    public bool SameAs(TextAnnot o) =>
        o.Text == Text && o.Pos == Pos && o.Px == Px && o.Pill == Pill && o.Color.ToArgb() == Color.ToArgb();

    private static FontFamily LoadFamily()
    {
        try { return new FontFamily("Segoe UI"); }
        catch (ArgumentException) { return FontFamily.GenericSansSerif; }
    }

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

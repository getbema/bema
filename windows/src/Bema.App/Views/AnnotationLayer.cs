using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Bema.App.Model;

namespace Bema.App.Views;

/// <summary>
/// Draws the drawings, stamps, the pointer and effects over a slide: on the presenter's current slide,
/// and on the overlay above PowerPoint's slideshow for the audience. Points are 0…1 across the slide.
/// </summary>
public sealed class AnnotationLayer : FrameworkElement
{
    /// <summary>Over the audience's slide: the plain pointer is drawn only there (the presenter has the real one).</summary>
    public bool Audience { get; init; }

    static readonly Typeface Symbols = new(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    public AnnotationLayer()
    {
        IsHitTestVisible = false;
        Presenter.Shared.DrawingChanged += InvalidateVisual;
        CompositionTarget.Rendering += (_, _) => { if (Presenter.Shared.EffectsAnimating) InvalidateVisual(); };
    }

    static Point Pt(Point p, Size s) => new(p.X * s.Width, p.Y * s.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var m = Presenter.Shared;
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(size)));
        var now = DateTime.UtcNow;
        foreach (var s in m.Strokes) DrawStroke(dc, s, size);
        if (m.ActiveStroke is { } active) DrawStroke(dc, active, size);
        foreach (var st in m.Stamps) DrawStamp(dc, st.Kind, Pt(st.Point, size), size, 1);
        if (m.PointerAt is { } p)
        {
            var c = Pt(p, size);
            switch (m.Tool)
            {
                case PointerTool.Spotlight: DrawSpotlight(dc, c, size); break;
                case PointerTool.Magnifier: DrawMagnifier(dc, c, p, size); break;
                case PointerTool.Laser: DrawLaser(dc, c, size, now); break;
                case PointerTool.Stamp: DrawStamp(dc, m.StampKind, c, size, 0.4); break;
                case PointerTool.None when Audience: DrawArrow(dc, c, size); break;
            }
        }
        foreach (var r in m.Ripples) DrawRipple(dc, r, size, now);
        if (m.Reaction is { } reaction) DrawReaction(dc, reaction, size, now);
        dc.Pop();
    }

    static Pen RoundPen(Brush b, double w) => new(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    static void DrawStroke(DrawingContext dc, Stroke s, Size size)
    {
        if (s.Points.Count == 0) return;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var first = Pt(s.Points[0], size);
            ctx.BeginFigure(first, false, false);
            if (s.Points.Count == 1) ctx.LineTo(new Point(first.X + 0.1, first.Y), true, true);
            foreach (var p in s.Points.Skip(1)) ctx.LineTo(Pt(p, size), true, true);
        }
        var pen = s.Tool == PointerTool.Highlighter
            ? RoundPen(new SolidColorBrush(Color.FromArgb(107, 255, 235, 0)), Math.Max(size.Width * 0.022, 8))
            : RoundPen(new SolidColorBrush(Color.FromRgb(230, 26, 26)), Math.Max(size.Width * 0.0035, 2));
        dc.DrawGeometry(null, pen, g);
    }

    static void Text(DrawingContext dc, string text, Point center, double size, Brush brush, Typeface? face = null)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Symbols, size, brush, 1.0);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    static void DrawStamp(DrawingContext dc, StampKind kind, Point c, Size size, double opacity)
    {
        var s = Math.Max(size.Width * 0.06, 22);
        var color = kind.Color();
        var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B));
        dc.PushOpacity(1);
        switch (kind)
        {
            case StampKind.Ring:
                dc.DrawEllipse(null, RoundPen(brush, Math.Max(size.Width * 0.005, 3)), c, s * 1.3, s * 0.85);
                break;
            case StampKind.Wow:
                Text(dc, "✸", c, s * 2.4, brush);
                Text(dc, "WOW!", c, s * 0.55, new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 255, 255, 255)), Bold);
                break;
            default:
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 255, 255, 255)), null, c, s * 0.5, s * 0.5);
                Text(dc, kind.Symbol(), c, s * 0.75, brush);
                break;
        }
        dc.Pop();
    }

    static void DrawSpotlight(DrawingContext dc, Point c, Size size)
    {
        var r = Math.Max(size.Width * 0.12, 60);
        var dark = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(size)), new EllipseGeometry(c, r, r));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(173, 0, 0, 0)), null, dark);
    }

    static void DrawMagnifier(DrawingContext dc, Point c, Point p, Size size)
    {
        var m = Presenter.Shared;
        var r = Math.Max(size.Width * 0.11, 55);
        const double zoom = 2.2;
        var lens = new EllipseGeometry(c, r, r);
        dc.DrawGeometry(Brushes.White, null, lens);
        if (m.Image(m.Current, (int)(size.Width * zoom)) is { } img)
        {
            dc.PushClip(lens);
            dc.DrawImage(img, new Rect(c.X - p.X * size.Width * zoom, c.Y - p.Y * size.Height * zoom, size.Width * zoom, size.Height * zoom));
            dc.Pop();
        }
        dc.DrawGeometry(null, new Pen(Brushes.White, 4), lens);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)), 1), c, r + 2, r + 2);
    }

    static SolidColorBrush Rgba(uint rgb, double a) { var c = Tools.Hex(rgb); return new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B)); }

    static void DrawLaser(DrawingContext dc, Point c, Size size, DateTime now)
    {
        var m = Presenter.Shared;
        var r = Math.Max(size.Width * 0.007, 5);
        var trail = m.LaserTrail.Where(t => (now - t.Time).TotalSeconds < 0.6).ToList();
        switch (m.LaserStyle)
        {
            case LaserStyle.Dot:
                dc.DrawEllipse(Rgba(0xFF0000, 0.18), null, c, r * 2.4, r * 2.4);
                dc.DrawEllipse(Rgba(0xFF0000, 0.35), null, c, r * 1.5, r * 1.5);
                dc.DrawEllipse(Rgba(0xFF1A1A, 1), null, c, r, r);
                dc.DrawEllipse(Rgba(0xFFFFFF, 0.8), null, c, r * 0.4, r * 0.4);
                break;
            case LaserStyle.Hand:
            {
                var s = Math.Max(size.Width * 0.045, 28);
                var at = new Point(c.X + s * 0.32, c.Y + s * 0.46);
                foreach (var (dx, dy) in new[] { (-1.5, 0.0), (1.5, 0.0), (0.0, -1.5), (0.0, 1.5) })
                    Text(dc, "☝", new Point(at.X + dx, at.Y + dy), s, Rgba(0x14161B, 1));
                Text(dc, "☝", at, s, Brushes.White);
                break;
            }
            case LaserStyle.Wand:
            {
                uint[] colors = [0xFFD54F, 0xFFFFFF, 0x80DEEA];
                for (var i = 0; i < trail.Count; i++)
                {
                    var age = (now - trail[i].Time).TotalSeconds / 0.6;
                    var rnd = new SeededRandom((ulong)i * 7919 + (ulong)trail[i].Time.Ticks / 10000);
                    var q = Pt(trail[i].Point, size);
                    var j = new Point(q.X + (rnd.Next() - 0.5) * r * 6, q.Y + (rnd.Next() - 0.5) * r * 6 + age * r * 4);
                    Text(dc, "✦", j, r * (2.6 - 1.6 * age) * 1.4, Rgba(colors[i % 3], 1 - age));
                }
                Text(dc, "✨", new Point(c.X + r * 2, c.Y + r * 2), Math.Max(size.Width * 0.04, 26), Brushes.White);
                break;
            }
            case LaserStyle.Comet:
            {
                var pts = trail.Select(t => Pt(t.Point, size)).Append(c).ToList();
                for (var i = 1; i < pts.Count; i++)
                {
                    var f = (double)i / pts.Count;
                    dc.DrawLine(RoundPen(Rgba(0xFF8A3D, f * 0.9), r * 2.2 * f), pts[i - 1], pts[i]);
                }
                dc.DrawEllipse(Rgba(0xFFB74D, 0.35), null, c, r * 2.2, r * 2.2);
                dc.DrawEllipse(Rgba(0xFFF3E0, 1), null, c, r * 1.1, r * 1.1);
                break;
            }
            case LaserStyle.Plane:
            {
                for (var i = 0; i < trail.Count; i += 2)
                {
                    var age = (now - trail[i].Time).TotalSeconds / 0.6;
                    dc.DrawEllipse(Rgba(0xFFFFFF, 0.8 * (1 - age)), null, Pt(trail[i].Point, size), r * 0.35, r * 0.35);
                }
                var angle = -45.0;
                if (trail.Count > 0)
                {
                    var q = Pt(trail[^1].Point, size);
                    if (Math.Abs(c.X - q.X) + Math.Abs(c.Y - q.Y) > 1) angle = Math.Atan2(c.Y - q.Y, c.X - q.X) * 180 / Math.PI;
                }
                dc.PushTransform(new RotateTransform(angle, c.X, c.Y));
                Text(dc, "✈", c, Math.Max(size.Width * 0.04, 26), Brushes.White);
                dc.Pop();
                break;
            }
        }
    }

    /// <summary>An arrow pointer with its tip at <paramref name="tip"/>, sized like a pointer on a screen the slide's size.</summary>
    static void DrawArrow(DrawingContext dc, Point tip, Size size)
    {
        var h = Math.Max(18, size.Height * 0.03);
        var g = Geometry.Parse("M0,0 L0,16 L4.2,12.1 L7,18.4 L9.6,17.3 L6.9,11.2 L12.4,11.2 Z").Clone();
        g.Transform = new TransformGroup { Children = { new ScaleTransform(h / 18.4, h / 18.4), new TranslateTransform(tip.X, tip.Y) } };
        dc.DrawGeometry(Brushes.Black, new Pen(Brushes.White, Math.Max(1.5, h / 12)) { LineJoin = PenLineJoin.Round }, g);
    }

    static void DrawRipple(DrawingContext dc, Ripple ripple, Size size, DateTime now)
    {
        var t = (now - ripple.Start).TotalSeconds / 0.7;
        if (t is < 0 or >= 1) return;
        var c = Pt(ripple.Point, size);
        var r = Math.Max(size.Width * 0.01, 8) + t * Math.Max(size.Width * 0.035, 30);
        dc.DrawEllipse(null, new Pen(Rgba(0xF5A524, 0.9 * (1 - t)), 3), c, r, r);
        dc.DrawEllipse(null, new Pen(Rgba(0xFFFFFF, 0.6 * (1 - t)), 2), c, r * 0.6, r * 0.6);
    }

    static void DrawReaction(DrawingContext dc, Reaction reaction, Size size, DateTime now)
    {
        var t = (now - reaction.Start).TotalSeconds;
        if (t < 0 || t >= reaction.Kind.Duration()) return;
        var rnd = new SeededRandom(reaction.Seed);
        if (reaction.Kind == ReactionKind.Confetti)
        {
            uint[] colors = [0xF5A524, 0xFF5A4F, 0x4BE3A0, 0x5B9DFF, 0xC792EA, 0xFFFFFF];
            var fade = Math.Min(1, (reaction.Kind.Duration() - t) / 0.6);
            for (var i = 0; i < 140; i++)
            {
                double x0 = rnd.Next(), vx = (rnd.Next() - 0.5) * 0.25, delay = rnd.Next() * 0.6;
                double vy = 0.15 + rnd.Next() * 0.35, spin = (rnd.Next() - 0.5) * 12, w = 6 + rnd.Next() * 8;
                var tt = t - delay;
                if (tt <= 0) continue;
                var x = (x0 + vx * tt) * size.Width;
                var y = (-0.05 + vy * tt + 0.18 * tt * tt) * size.Height;
                if (y > size.Height + 20) continue;
                dc.PushTransform(new TranslateTransform(x, y));
                dc.PushTransform(new RotateTransform(spin * tt * 180 / Math.PI));
                dc.DrawRectangle(Rgba(colors[i % colors.Length], fade), null, new Rect(-w / 2, -w / 4, w, w / 2));
                dc.Pop(); dc.Pop();
            }
            return;
        }
        var symbol = reaction.Kind == ReactionKind.Applause ? "👏" : "♥";
        var tint = reaction.Kind == ReactionKind.Applause ? 0xFFC35Cu : 0xFF5A7Au;
        for (var i = 0; i < 26; i++)
        {
            double x0 = 0.05 + rnd.Next() * 0.9, delay = rnd.Next() * 1.2, speed = 0.25 + rnd.Next() * 0.3;
            var s = Math.Max(size.Width * 0.035, 22) * (0.7 + rnd.Next() * 0.8);
            var wobble = rnd.Next() * 6;
            var tt = t - delay;
            if (tt <= 0) continue;
            var x = (x0 + 0.02 * Math.Sin(tt * 4 + wobble)) * size.Width;
            var y = size.Height * (1.05 - speed * tt);
            Text(dc, symbol, new Point(x, y), s, Rgba(tint, Math.Max(0, Math.Min(1, (1.6 - tt) / 0.5))));
        }
    }
}

/// <summary>
/// The presenter's current slide: the picture PowerPoint made, the drawings on top, and the mouse
/// input for the pointer tools (drawn to the audience through the overlay).
/// </summary>
public sealed class SlideSurface : System.Windows.Controls.Grid
{
    readonly System.Windows.Controls.Image image = new() { Stretch = Stretch.Uniform };
    readonly System.Windows.Controls.Grid frame = new();
    readonly AnnotationLayer layer = new();

    public SlideSurface()
    {
        frame.Children.Add(image);
        frame.Children.Add(layer);
        frame.Background = Brushes.Transparent;
        frame.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35 };
        Children.Add(frame);
        frame.MouseMove += (_, e) => Move(e.GetPosition(frame), e.LeftButton == System.Windows.Input.MouseButtonState.Pressed);
        frame.MouseLeave += (_, _) => Presenter.Shared.MovePointer(null);
        frame.MouseLeftButtonDown += (_, e) => Down(e.GetPosition(frame));
        frame.MouseLeftButtonUp += (_, _) => { Presenter.Shared.CommitStroke(); frame.ReleaseMouseCapture(); };
        SizeChanged += (_, _) => Fit();
    }

    int slide;
    public int Slide
    {
        get => slide;
        set { slide = value; image.Source = Presenter.Shared.Image(value); Fit(); }
    }

    /// <summary>Keeps the frame at the slide's aspect, so 0…1 points match what the audience sees.</summary>
    void Fit()
    {
        var aspect = image.Source is { Width: > 0 } src ? src.Width / src.Height : 16.0 / 9;
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (w / h > aspect) { frame.Height = h; frame.Width = h * aspect; } else { frame.Width = w; frame.Height = w / aspect; }
    }

    Point Norm(Point p) => new(Math.Clamp(p.X / frame.ActualWidth, 0, 1), Math.Clamp(p.Y / frame.ActualHeight, 0, 1));

    void Move(Point p, bool pressed)
    {
        var m = Presenter.Shared;
        var n = Norm(p);
        if (m.Tool is PointerTool.Pen or PointerTool.Highlighter)
        {
            if (pressed && m.ActiveStroke is { } s) { s.Points.Add(n); m.MovePointer(null); }
            return;
        }
        m.MovePointer(n);
    }

    void Down(Point p)
    {
        var m = Presenter.Shared;
        var n = Norm(p);
        switch (m.Tool)
        {
            case PointerTool.Pen or PointerTool.Highlighter:
                m.ActiveStroke = new Stroke(m.Tool);
                m.ActiveStroke.Points.Add(n);
                frame.CaptureMouse();
                break;
            case PointerTool.Stamp: m.PlaceStamp(n); break;
            case PointerTool.None: break;
            default: m.AddRipple(n); break;
        }
    }

    public void Refresh() { image.Source = Presenter.Shared.Image(slide); Fit(); layer.InvalidateVisual(); }
}

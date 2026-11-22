using System.Windows;
using System.Windows.Media;

namespace Bema.App.Model;

/// <summary>What the mouse does over the current slide in the presenter window.</summary>
public enum PointerTool { None, Laser, Spotlight, Magnifier, Pen, Highlighter, Stamp }

public enum LaserStyle { Dot, Hand, Wand, Comet, Plane }

public enum StampKind { Check, Cross, Star, Exclaim, Ring, Wow }

public enum ReactionKind { Confetti, Applause, Hearts }

public static class Tools
{
    public static string Label(this PointerTool t) => t switch
    {
        PointerTool.None => "Mouse pointer: the audience sees your pointer on the slide",
        PointerTool.Laser => "Laser pointer (L)",
        PointerTool.Spotlight => "Spotlight (S)",
        PointerTool.Magnifier => "Magnifier (M)",
        PointerTool.Pen => "Pen (P)",
        PointerTool.Highlighter => "Highlighter (H)",
        _ => "Stamp (T)",
    };

    /// <summary>Segoe Fluent Icons glyphs (Windows 11; Segoe MDL2 Assets on Windows 10 has the same code points).</summary>
    public static string Glyph(this PointerTool t) => t switch
    {
        PointerTool.None => "",
        PointerTool.Laser => "",
        PointerTool.Spotlight => "",
        PointerTool.Magnifier => "",
        PointerTool.Pen => "",
        PointerTool.Highlighter => "",
        _ => "",
    };

    /// <summary>Tools that follow the mouse while hovering (the plain pointer too: the audience sees an arrow).</summary>
    public static bool FollowsHover(this PointerTool t) => t is PointerTool.None or PointerTool.Laser or PointerTool.Spotlight or PointerTool.Magnifier or PointerTool.Stamp;

    public static bool UsesTrail(this LaserStyle s) => s is LaserStyle.Wand or LaserStyle.Comet or LaserStyle.Plane;

    public static string Label(this LaserStyle s) => s switch
    {
        LaserStyle.Dot => "Red dot", LaserStyle.Hand => "Pointing hand", LaserStyle.Wand => "Magic wand",
        LaserStyle.Comet => "Comet trail", _ => "Paper plane",
    };

    public static string Label(this StampKind k) => k switch
    {
        StampKind.Check => "Check", StampKind.Cross => "Cross", StampKind.Star => "Star",
        StampKind.Exclaim => "Exclamation", StampKind.Ring => "Circle", _ => "WOW",
    };

    public static string Symbol(this StampKind k) => k switch
    {
        StampKind.Check => "✔", StampKind.Cross => "✖", StampKind.Star => "★", StampKind.Exclaim => "!", StampKind.Ring => "◯", _ => "WOW",
    };

    public static Color Color(this StampKind k) => k switch
    {
        StampKind.Check => Hex(0x2FBF71), StampKind.Cross => Hex(0xE5352B), StampKind.Star => Hex(0xF5B301),
        StampKind.Exclaim => Hex(0xF57C00), StampKind.Ring => Hex(0xE5352B), _ => Hex(0xF5A524),
    };

    public static string Label(this ReactionKind k) => k switch { ReactionKind.Confetti => "Confetti (1)", ReactionKind.Applause => "Applause (2)", _ => "Hearts (3)" };
    public static double Duration(this ReactionKind k) => k == ReactionKind.Confetti ? 3.2 : 2.8;

    public static Color Hex(uint rgb) => System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>A pen or highlighter stroke; points are 0…1 across the slide, so it draws the same on every display.</summary>
public sealed class Stroke(PointerTool tool)
{
    public PointerTool Tool { get; } = tool;
    public List<Point> Points { get; } = [];
}

public sealed record Stamp(StampKind Kind, Point Point);
public sealed record Ripple(Point Point, DateTime Start);
public sealed record TrailPoint(Point Point, DateTime Time);
public sealed record Reaction(ReactionKind Kind, DateTime Start, ulong Seed);

/// <summary>Small deterministic random numbers, so animated particles look the same on every redraw.</summary>
public struct SeededRandom(ulong seed)
{
    ulong state = seed + 0x9E3779B97F4A7C15;

    public double Next()
    {
        state = state * 6364136223846793005 + 1442695040888963407;
        return ((state >> 11) & ((1UL << 53) - 1)) / (double)(1UL << 53);
    }
}

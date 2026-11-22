using System.Windows;
using System.Windows.Media;
using Bema.App.Model;

namespace Bema.App;

public enum ThemeName { Backstage, Daylight, Contrast }

/// <summary>
/// Bema's "backstage" look: a dark ground that's easy on the eyes in a dim room, one warm amber
/// accent for live state, red only for the laser and "live". Same palettes as the Mac app; the
/// windows use them as DynamicResource brushes, so switching theme repaints at once.
/// </summary>
public static class Theme
{
    sealed record Palette(uint Ground, uint Bar, uint Panel, uint Raised, uint Selected, uint Border, uint BorderStrong,
                          uint Text, uint TextBright, uint TextSoft, uint Muted, uint Dim, uint Accent, uint AccentInk,
                          uint WarnBg, uint WarnBorder, uint WarnText, uint WarnStrong);

    static readonly Palette Backstage = new(0x0E1014, 0x12151A, 0x15181E, 0x181B22, 0x2B2F3A, 0x232731, 0x2A2F3A,
        0xE9EBEF, 0xF2F4F7, 0xC9CED6, 0x9BA3AF, 0x8A93A0, 0xF5A524, 0x1A1206, 0x2A2113, 0x5A4520, 0xD8C9A8, 0xFFC35C);
    static readonly Palette Daylight = new(0xF3F4F6, 0xFFFFFF, 0xFFFFFF, 0xF1F2F5, 0xE2E5EA, 0xE1E4EA, 0xD0D5DD,
        0x15181E, 0x0E1014, 0x363C47, 0x5A6271, 0x666E7C, 0xA85F00, 0xFFFFFF, 0xFFF4DC, 0xEED39B, 0x5E4510, 0x8A5500);
    static readonly Palette Contrast = new(0x000000, 0x0A0A0A, 0x0D0D0D, 0x1A1A1A, 0x3A3A3A, 0x5A5A5A, 0x7A7A7A,
        0xFFFFFF, 0xFFFFFF, 0xF2F2F2, 0xD6D6D6, 0xC2C2C2, 0xFFC233, 0x000000, 0x2E2200, 0xFFC233, 0xFFF0C2, 0xFFD666);

    public static ThemeName Name
    {
        get => Settings.Get("theme", ThemeName.Backstage);
        set { Settings.Set("theme", value); Apply(); }
    }

    public static readonly Color Live = Tools.Hex(0xFF5A4F), LiveSoft = Tools.Hex(0xFF8F87), LiveBg = Tools.Hex(0x3A1A1A),
        Laser = Tools.Hex(0xFF6B60), Ok = Tools.Hex(0x4BE3A0), OkBg = Tools.Hex(0x16352A), Danger = Tools.Hex(0xFFB4AE), DangerBg = Tools.Hex(0x2A1617);

    public static void Apply()
    {
        var p = Name switch { ThemeName.Daylight => Daylight, ThemeName.Contrast => Contrast, _ => Backstage };
        var r = Application.Current.Resources;
        void B(string key, uint rgb) { var b = new SolidColorBrush(Tools.Hex(rgb)); b.Freeze(); r[key] = b; }
        void C(string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); r[key] = b; }
        B("Ground", p.Ground); B("Bar", p.Bar); B("Panel", p.Panel); B("Raised", p.Raised); B("Selected", p.Selected);
        B("Border", p.Border); B("BorderStrong", p.BorderStrong); B("Text", p.Text); B("TextBright", p.TextBright);
        B("TextSoft", p.TextSoft); B("Muted", p.Muted); B("Dim", p.Dim); B("Accent", p.Accent); B("AccentInk", p.AccentInk);
        B("WarnBg", p.WarnBg); B("WarnBorder", p.WarnBorder); B("WarnText", p.WarnText); B("WarnStrong", p.WarnStrong);
        C("Live", Live); C("LiveSoft", LiveSoft); C("LiveBg", LiveBg); C("Ok", Ok); C("OkBg", OkBg); C("Danger", Danger); C("DangerBg", DangerBg);
        var accentSoft = new SolidColorBrush(Color.FromArgb(0x29, (byte)(p.Accent >> 16), (byte)(p.Accent >> 8), (byte)p.Accent)); accentSoft.Freeze();
        r["AccentSoft"] = accentSoft;
        IsDark = Name != ThemeName.Daylight;
    }

    public static bool IsDark { get; private set; } = true;

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

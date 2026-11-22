using System.Windows;
using Forms = System.Windows.Forms;

namespace Bema.App.Model;

/// <summary>A connected display as Bema shows it.</summary>
public sealed record Display(string Id, string Name, Rect Pixels, bool Primary)
{
    public static IReadOnlyList<Display> All()
    {
        var screens = Forms.Screen.AllScreens;
        return screens.Select((s, i) => new Display(
            s.DeviceName,
            $"Display {i + 1}{(s.Primary ? " (main)" : "")} · {s.Bounds.Width}×{s.Bounds.Height}",
            new Rect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height),
            s.Primary)).ToList();
    }

    /// <summary>The display a window (in pixels) is mostly on.</summary>
    public static Display? Containing(Rect pixels)
    {
        var center = new Point(pixels.X + pixels.Width / 2, pixels.Y + pixels.Height / 2);
        return All().FirstOrDefault(d => d.Pixels.Contains(center));
    }
}

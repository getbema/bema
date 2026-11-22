using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Bema.App.Views;

/// <summary>Small builders for Bema's look, so the windows read like the Mac app's SwiftUI views.</summary>
public static class Ui
{
    public static TextBlock Text(string text, double size = 13, string brush = "Text", FontWeight? weight = null, bool wrap = false) => new TextBlock
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    }.Fg(brush);

    public static T Fg<T>(this T e, string brush) where T : FrameworkElement
    {
        switch (e)
        {
            case TextBlock t: t.SetResourceReference(TextBlock.ForegroundProperty, brush); break;
            case Control c: c.SetResourceReference(Control.ForegroundProperty, brush); break;
        }
        return e;
    }

    public static T Bg<T>(this T e, string brush) where T : FrameworkElement
    {
        switch (e)
        {
            case Panel p: p.SetResourceReference(Panel.BackgroundProperty, brush); break;
            case Border b: b.SetResourceReference(Border.BackgroundProperty, brush); break;
            case Control c: c.SetResourceReference(Control.BackgroundProperty, brush); break;
        }
        return e;
    }

    public static TextBlock Caps(string text) { var t = Text(text.ToUpperInvariant(), 11, "Dim", FontWeights.Bold); return t; }

    public static TextBlock Icon(string glyph, double size = 15, string brush = "TextSoft")
    {
        var t = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return t.Fg(brush);
    }

    public static Button Pill(string label, Action click, string style = "Pill", string? glyph = null, string? tip = null)
    {
        var b = new Button { Content = Labeled(label, glyph), ToolTip = tip };
        b.SetResourceReference(FrameworkElement.StyleProperty, style);
        b.Click += (_, _) => click();
        return b;
    }

    static object Labeled(string label, string? glyph)
    {
        if (glyph is null) return label;
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        var i = Icon(glyph, 13, "Text");
        i.ClearValue(TextBlock.ForegroundProperty);
        i.Margin = new Thickness(0, 0, 8, 0);
        s.Children.Add(i);
        s.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return s;
    }

    public static Button IconButton(string glyph, string tip, Action click, double size = 15)
    {
        var b = new Button { Content = Icon(glyph, size, "TextSoft"), ToolTip = tip };
        b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static ToggleButton IconToggle(string glyph, string tip, Action click)
    {
        var b = new ToggleButton { Content = Icon(glyph, 15, "TextSoft"), ToolTip = tip };
        b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static Border Card(UIElement child, double padding = 18, double radius = 14, string bg = "Panel")
    {
        var b = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1) }.Bg(bg);
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        return b;
    }

    public static StackPanel Row(double gap, params UIElement[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0) fe.Margin = new Thickness(gap, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            s.Children.Add(children[i]);
        }
        return s;
    }

    public static StackPanel Column(double gap, params UIElement[] children)
    {
        var s = new StackPanel();
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0) fe.Margin = new Thickness(fe.Margin.Left, gap, fe.Margin.Right, fe.Margin.Bottom);
            s.Children.Add(children[i]);
        }
        return s;
    }

    public static Border Divider(bool vertical = false)
    {
        var b = vertical ? new Border { Width = 1, Margin = new Thickness(6, 8, 6, 8) } : new Border { Height = 1 };
        return b.Bg("Border");
    }

    /// <summary>A menu that drops down from a button.</summary>
    public static Button MenuButton(object content, string tip, Func<ContextMenu> menu, string style = "IconButton")
    {
        var b = new Button { Content = content, ToolTip = tip };
        b.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (style == "IconButton") b.Width = double.NaN;
        b.Click += (_, _) =>
        {
            var m = menu();
            m.PlacementTarget = b;
            m.Placement = PlacementMode.Bottom;
            m.IsOpen = true;
        };
        return b;
    }

    public static MenuItem Item(string header, Action click, bool isChecked = false, bool enabled = true)
    {
        var i = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        i.Click += (_, _) => click();
        return i;
    }

    public static Binding Bind(string path, object source) => new(path) { Source = source };
}

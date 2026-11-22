using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bema.App.Model;
using Bema.Core;
using QRCoder;

namespace Bema.App.Views;

/// <summary>The live poll on the audience's slide: question, results, and a QR code to vote.</summary>
public sealed class PollOverlay : Border
{
    public static PollOverlay Shared { get; } = new();
    static readonly Brush Ink = new SolidColorBrush(Tools.Hex(0x14161B));

    PollOverlay()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Background = Brushes.White;
        CornerRadius = new CornerRadius(16);
        Padding = new Thickness(18);
        Margin = new Thickness(32);
        Width = 380;
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 6, Opacity = 0.25 };
        Visibility = Visibility.Collapsed;
        BemaApp.Instance.Poll.Changed += () => Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        var poll = BemaApp.Instance.Poll;
        Visibility = poll.OnScreen ? Visibility.Visible : Visibility.Collapsed;
        if (!poll.OnScreen) return;
        var stack = new StackPanel { FlowDirection = ScriptText.IsRightToLeft(poll.Question) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        stack.Children.Add(new TextBlock { Text = poll.Question, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        if (poll.ShowResults)
        {
            for (var i = 0; i < poll.Options.Count; i++)
            {
                var share = poll.TotalVotes > 0 ? (double)(i < poll.Votes.Length ? poll.Votes[i] : 0) / poll.TotalVotes : 0;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var pct = new TextBlock { Text = $"{Math.Round(share * 100, MidpointRounding.AwayFromZero)}%", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Ink };
                DockPanel.SetDock(pct, Dock.Right);
                row.Children.Add(pct);
                row.Children.Add(new TextBlock { Text = poll.Options[i], FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ink });
                stack.Children.Add(row);
                var bar = new Grid { Height = 10, Margin = new Thickness(0, 0, 0, 10) };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Tools.Hex(0xE6E8EC)) });
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = Theme.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Left, Width = 344 * share });
                stack.Children.Add(bar);
            }
        }
        var join = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        if (BemaApp.Instance.Server.PollUrl is { } url) join.Children.Add(new Image { Source = Qr(url), Width = 100, Height = 100, Margin = new Thickness(0, 0, 12, 0) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Scan to vote", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ink });
        text.Children.Add(new TextBlock { Text = $"{poll.TotalVotes} vote{(poll.TotalVotes == 1 ? "" : "s")}", FontSize = 14, Foreground = Ink });
        text.Children.Add(new TextBlock { Text = "Same Wi-Fi as the presenter", FontSize = 12, Foreground = Ink, Opacity = 0.6 });
        join.Children.Add(text);
        stack.Children.Add(join);
        Child = stack;
    }

    /// <summary>A QR code for a link, drawn on this PC.</summary>
    public static BitmapSource Qr(string text, int pixelsPerModule = 8)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(png);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }
}

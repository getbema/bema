using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Bema.App.Model;
using Bema.App.Platform;

namespace Bema.App.Views;

/// <summary>
/// A transparent, click-through window over PowerPoint's slideshow, where the pointer, drawings and
/// effects are drawn for the audience. PowerPoint still draws the slides; this only adds on top.
/// </summary>
public sealed class OverlayWindow : Window
{
    readonly AnnotationLayer layer = new() { Audience = true };
    readonly System.Windows.Controls.TextBlock captions = new()
    {
        FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, Margin = new Thickness(80, 0, 80, 48), VerticalAlignment = VerticalAlignment.Bottom,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 },
    };

    public static OverlayWindow Shared { get; } = new();

    OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(layer);
        grid.Children.Add(PollOverlay.Shared);
        grid.Children.Add(captions);
        Content = grid;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
                new IntPtr(style | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
        Presenter.Shared.PropertyChanged += Changed;
        Speech.SpeechHub.Shared.PropertyChanged += (_, _) => UpdateCaptions();
    }

    void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Presenter.ShowFrame) or nameof(Presenter.Phase) or nameof(Presenter.Audience) or "") Place();
    }

    void UpdateCaptions()
    {
        captions.Text = Speech.CaptionsRuntime.Shared.Enabled ? Speech.CaptionsRuntime.Shared.Text : "";
    }

    /// <summary>Sits exactly over the slideshow while it runs; hidden otherwise.</summary>
    void Place()
    {
        var m = Presenter.Shared;
        if (!m.IsPresenting || m.ShowFrame is not { } frame || m.Audience != AudienceScreen.Normal)
        {
            Hide();
            return;
        }
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, new IntPtr(-1) /* topmost */, (int)frame.X, (int)frame.Y, (int)frame.Width, (int)frame.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        // The slide is letterboxed inside the show window when their shapes differ: draw over the slide only.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double w = frame.Width / scale, h = frame.Height / scale;
        var aspect = m.Image(m.Current) is { Width: > 0 } img ? img.Width / img.Height : w / h;
        if (w / h > aspect) { layer.Height = h; layer.Width = h * aspect; } else { layer.Width = w; layer.Height = w / aspect; }
        layer.InvalidateVisual();
    }
}

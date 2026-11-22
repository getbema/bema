using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bema.App.Model;
using Bema.App.PowerPoint;
using Bema.App.Speech;
using Bema.Core;
using Microsoft.Win32;

namespace Bema.App.Views;

/// <summary>
/// Bema's window: the start screen with no deck, and the presenter view with one (current and next
/// slide, large notes, timer, pointer tools, filmstrip). Resize it, move it, keep it on top.
/// </summary>
public sealed class MainWindow : Window
{
    readonly Presenter m = Presenter.Shared;
    readonly ContentControl body = new();
    readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    PresenterView? presenter;

    public MainWindow()
    {
        Title = "Bema";
        Width = 1240; Height = 800; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Bg("Ground");
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Bema.ico"));
        AllowDrop = true;
        Content = body;
        m.PresenterWindowPixels = () =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            return hwnd != IntPtr.Zero && Platform.Native.GetWindowRect(hwnd, out var r) ? new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
        };
        m.PropertyChanged += OnModel;
        Closed += (_, _) => { m.PropertyChanged -= OnModel; clock.Stop(); };
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault(Presenter.IsDeck) is { } deck) await m.Load(deck);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBoxBase) return;     // typing (a draft, settings) keeps its keys
            if (Keyboard.Modifiers != ModifierKeys.None) { Shortcut(e); return; }
            if (m.HandleKey(e.Key == Key.System ? e.SystemKey : e.Key)) e.Handled = true;
        };
        SourceInitialized += (_, _) => ApplySharing();
        clock.Tick += (_, _) => presenter?.Tick();
        clock.Start();
        Show(m.Phase);
    }

    void Shortcut(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { OpenDeck(); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter) { m.RequestStart(); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.T) { Topmost = !Topmost; e.Handled = true; }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.P) { BemaApp.Instance.ShowPrompter(); e.Handled = true; }
    }

    void OnModel(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Presenter.Phase) or "") Show(m.Phase);
        if (e.PropertyName is nameof(Presenter.DisplayChooserOpen) && m.DisplayChooserOpen) new DisplayChooserWindow { Owner = this }.ShowDialog();
        presenter?.ModelChanged(e.PropertyName);
    }

    void Show(Phase phase)
    {
        switch (phase)
        {
            case Phase.Empty: presenter = null; body.Content = new WelcomeView(this); Title = "Bema"; break;
            case Phase.Loading: presenter = null; body.Content = Loading(); break;
            case Phase.Failed: presenter = null; body.Content = Failed(); break;
            default:
                if (presenter is null) { presenter = new PresenterView(this); body.Content = presenter; }
                Title = $"{m.DeckName} · Bema";
                presenter.ModelChanged(null);
                break;
        }
    }

    FrameworkElement Loading()
    {
        var text = Ui.Text("", 15, "TextSoft");
        text.SetBinding(TextBlock.TextProperty, Ui.Bind(nameof(Presenter.LoadingMessage), m));
        var bar = new ProgressBar { IsIndeterminate = true, Width = 240, Height = 4, Margin = new Thickness(0, 14, 0, 0) };
        bar.SetResourceReference(Control.ForegroundProperty, "Accent");
        bar.SetResourceReference(Control.BackgroundProperty, "Raised");
        bar.BorderThickness = new Thickness(0);
        return new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { text, bar } };
    }

    FrameworkElement Failed() => new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520,
        Children =
        {
            Ui.Text("This deck couldn't be opened", 20, "Text", FontWeights.SemiBold),
            new TextBlock { Text = m.Failure, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) }.Fg("Muted"),
            Ui.Row(10, Ui.Pill("Open another deck…", OpenDeck, "AccentPill"), Ui.Pill("Back", () => _ = m.CloseDeck())),
        },
    };

    /// <summary>Bema's window stays out of screen sharing and recordings unless the presenter turns that off.</summary>
    public void ApplySharing()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            Platform.Native.SetWindowDisplayAffinity(hwnd, Settings.Get("hidePresenterFromSharing", true) ? Platform.Native.WDA_EXCLUDEFROMCAPTURE : Platform.Native.WDA_NONE);
    }

    public void OpenDeck()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a PowerPoint presentation",
            Filter = "PowerPoint presentations|*.pptx;*.ppsx;*.pptm;*.ppt|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) _ = m.Load(dlg.FileName);
    }
}

/// <summary>The start screen: drop a deck, present the one open in PowerPoint, quick prompt, recents.</summary>
sealed class WelcomeView : ScrollViewer
{
    public WelcomeView(MainWindow window)
    {
        var m = Presenter.Shared;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var col = new StackPanel { MaxWidth = 680, Margin = new Thickness(40, 48, 40, 40) };

        var logo = new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Bema.ico")), Width = 56, Height = 56 };
        col.Children.Add(Ui.Row(16, logo, Ui.Column(2,
            Ui.Text("Bema", 30, "TextBright", FontWeights.Bold),
            Ui.Text("A presenter view you control, for PowerPoint on Windows", 15, "Muted"))));

        // Drop zone
        var drop = new Border
        {
            CornerRadius = new CornerRadius(18), Padding = new Thickness(28, 36, 28, 36), Margin = new Thickness(0, 30, 0, 0),
            BorderThickness = new Thickness(2),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    Ui.Icon("", 30, "Dim"),
                    new TextBlock { Text = "Drop a PowerPoint deck here", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 4) },
                    new TextBlock { Text = "or press Ctrl+Alt+P with a deck open in PowerPoint", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center }.Fg("Muted"),
                    new Border { Height = 14 },
                    Ui.Pill("Open presentation…", window.OpenDeck, "AccentPill"),
                },
            },
        }.Bg("Bar");
        drop.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        col.Children.Add(drop);

        // The deck open in PowerPoint, offered as "Present …"
        var openCard = new ContentControl { Margin = new Thickness(0, 14, 0, 0) };
        col.Children.Add(openCard);
        _ = Fill(openCard);

        col.Children.Add(new QuickPromptCard { Margin = new Thickness(0, 18, 0, 0) });

        if (m.Recents.Count > 0)
        {
            col.Children.Add(new Border { Height = 26 });
            col.Children.Add(Ui.Caps("Recent"));
            var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var path in m.Recents)
            {
                var row = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), Height = double.NaN, Padding = new Thickness(14, 10, 14, 10) };
                row.SetResourceReference(StyleProperty, "Pill");
                row.Content = Ui.Column(2, Ui.Text(Path.GetFileName(path), 14, "Text", FontWeights.SemiBold),
                                        Ui.Text(Path.GetDirectoryName(path) ?? "", 12, "Muted"));
                row.Click += (_, _) => _ = m.Load(path);
                list.Children.Add(row);
            }
            col.Children.Add(list);
        }

        col.Children.Add(new Border { Height = 26 });
        col.Children.Add(Preflight());
        Content = col;
    }

    static async Task Fill(ContentControl host)
    {
        var deck = await Presenter.Shared.DeckOpenInPowerPoint();
        if (deck is null) return;
        var name = CloudDeck.IsCloud(deck) ? Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(deck).AbsolutePath)) : Path.GetFileNameWithoutExtension(deck);
        host.Content = Ui.Card(new DockPanel
        {
            LastChildFill = false,
            Children =
            {
                Ui.Column(2, Ui.Caps("Open in PowerPoint"), Ui.Text(name, 15, "Text", FontWeights.SemiBold)),
            },
        }.With(d => { var b = Ui.Pill($"Present", () => _ = Presenter.Shared.Load(deck), "AccentPill"); DockPanel.SetDock(b, Dock.Right); d.Children.Add(b); }));
    }

    static FrameworkElement Preflight()
    {
        var langs = SpeechHub.InstalledLanguages();
        var col = Ui.Column(8, Ui.Caps("Before you present"),
            Check(PowerPointController.IsInstalled, PowerPointController.IsInstalled ? "PowerPoint is installed" : "Install Microsoft PowerPoint to present"),
            Check(true, Display.All().Count > 1 ? "Slides open full screen on another display" : "One display connected: slides open in a window for rehearsal"),
            Check(langs.Count > 0, langs.Count > 0
                ? $"Speech on this PC: {string.Join(", ", langs.Select(l => l.DisplayName))}"
                : "No Windows speech languages installed: the voice prompter and coach need one (Settings → Time & language → Speech)"));
        return col;
    }

    static FrameworkElement Check(bool ok, string text) =>
        Ui.Row(10, Ui.Icon(ok ? "" : "", 13, ok ? "Ok" : "WarnStrong"), Ui.Text(text, 13, "TextSoft", wrap: true));
}

static class Extensions
{
    public static T With<T>(this T t, Action<T> f) { f(t); return t; }
}

/// <summary>Paste any text and prompt it in the detached prompter, no deck needed.</summary>
sealed class QuickPromptCard : Border
{
    public QuickPromptCard()
    {
        CornerRadius = new CornerRadius(18);
        Padding = new Thickness(22);
        BorderThickness = new Thickness(1);
        this.Bg("Panel");
        SetResourceReference(BorderBrushProperty, "Border");
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 104, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 8) };
        var summary = Ui.Text(Core.QuickPrompt.Summary(""), 12, "Dim", wrap: true);
        var go = Ui.Pill("Prompt this text  Ctrl+Enter", Go, "AccentPill");
        go.IsEnabled = false;
        box.TextChanged += (_, _) => { summary.Text = Core.QuickPrompt.Summary(box.Text, PrompterRuntime.Shared.WordsPerMinute); go.IsEnabled = box.Text.Trim().Length > 0; };
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Go(); e.Handled = true; } };
        void Go()
        {
            if (box.Text.Trim().Length == 0) return;
            PrompterRuntime.Shared.UseOwnScript(box.Text);
            box.Text = "";
            BemaApp.Instance.ShowPrompter();
        }
        var buttons = Ui.Row(10, Ui.Pill("Open empty prompter", () => BemaApp.Instance.ShowPrompter(editOwnText: true)), go);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Child = Ui.Column(0,
            Ui.Row(14, new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Child = Ui.Icon("", 18, "Accent") }.Bg("WarnBg"),
                Ui.Column(4,
                    Ui.Text("QUICK PROMPT · NO DECK NEEDED", 11, "Accent", FontWeights.Bold),
                    Ui.Text("Prompt any text", 17, "TextBright", FontWeights.SemiBold),
                    Ui.Text("Paste a script, a speech or talking points. Bema opens the prompter in its own window: it follows your voice, scrolls by itself or by hand, sits under your camera and stays hidden from screen sharing.", 13, "Muted", wrap: true))),
            box, summary, buttons);
    }
}

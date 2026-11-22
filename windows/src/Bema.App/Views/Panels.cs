using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Bema.App.Model;
using Bema.App.Platform;
using Bema.App.Speech;
using Bema.Core;

namespace Bema.App.Views;

/// <summary>A plain Bema-styled window for panels and dialogs.</summary>
public abstract class PanelWindow : Window
{
    protected PanelWindow(string title, double width, double height)
    {
        Title = title;
        Width = width; Height = height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        this.Bg("Panel");
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
    }

    protected static CheckBox Toggle(string label, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    protected static StackPanel Section(string title, params UIElement[] content)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        s.Children.Add(Ui.Caps(title).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
        foreach (var c in content) s.Children.Add(c);
        return s;
    }

    protected static TextBlock Note(string text) => Ui.Text(text, 11, "Dim", wrap: true).With(t => t.Margin = new Thickness(0, 4, 0, 0));
}

/// <summary>Stage tools: the phone remote, the live poll, listening, the window and rehearsal.</summary>
public sealed class StageToolsWindow : PanelWindow
{
    readonly StackPanel col = new() { Margin = new Thickness(18) };

    public StageToolsWindow() : base("Stage tools", 440, 640)
    {
        Content = new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Build();
    }

    void Build()
    {
        var app = BemaApp.Instance;
        var poll = app.Poll;
        col.Children.Clear();

        // Phone remote
        var remote = new StackPanel();
        remote.Children.Add(Toggle("Control the show from your phone", app.Router.RemoteEnabled, on =>
        {
            app.Router.RemoteEnabled = on;
            if (on) app.Server.Start(); else if (!poll.OnScreen) app.Server.Stop();
            Build();
        }));
        if (app.Router.RemoteEnabled)
        {
            if (app.Server.RemoteUrl is { } url)
            {
                var qr = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = new Image { Source = PollOverlay.Qr(url), Width = 132, Height = 132 } };
                remote.Children.Add(Ui.Row(14, qr, Ui.Column(6,
                    Ui.Text("Scan with your phone's camera.", 13, "Text", FontWeights.Medium),
                    Ui.Text("Your phone and this PC need to be on the same Wi-Fi. The link has a secret code, so only you can use it.", 12, "Muted", wrap: true).With(t => t.MaxWidth = 220),
                    new TextBox { Text = url, IsReadOnly = true, FontSize = 11, BorderThickness = new Thickness(0), Padding = new Thickness(0) }.Fg("Dim"))));
            }
            else remote.Children.Add(Ui.Text(app.Server.Error ?? "Starting… If Windows asks to allow Bema on private networks, choose Allow.", 12, "WarnStrong", wrap: true));
        }
        col.Children.Add(Section("Phone remote", remote));

        // Live poll
        var question = new TextBox { Text = poll.Question };
        question.TextChanged += (_, _) => { poll.Question = question.Text; PollOverlay.Shared.Refresh(); };
        var options = new TextBox { Text = string.Join("\n", poll.Options), AcceptsReturn = true, Height = 84, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        options.TextChanged += (_, _) =>
        {
            var opts = options.Text.Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).Take(6).ToList();
            if (!opts.SequenceEqual(poll.Options)) poll.SetOptions(opts);
        };
        var votes = Ui.Text($"{poll.TotalVotes} vote{(poll.TotalVotes == 1 ? "" : "s")}", 12, "Muted");
        poll.Changed += () => Dispatcher.BeginInvoke(() => votes.Text = $"{poll.TotalVotes} vote{(poll.TotalVotes == 1 ? "" : "s")}");
        col.Children.Add(Section("Live poll",
            question,
            Ui.Text("Answers, one per line (up to 6)", 12, "Muted").With(t => t.Margin = new Thickness(0, 8, 0, 4)),
            options,
            Toggle("Show on the audience screen", poll.OnScreen, on =>
            {
                poll.OnScreen = on;
                if (on) app.Server.Start(); else if (!app.Router.RemoteEnabled) app.Server.Stop();
                PollOverlay.Shared.Refresh();
            }),
            Toggle("Show results", poll.ShowResults, on => { poll.ShowResults = on; PollOverlay.Shared.Refresh(); }),
            Ui.Row(12, votes, Ui.Pill("Reset votes", poll.Reset).With(b => b.Height = 28))));

        // Listening
        col.Children.Add(Section("Listening",
            Toggle("Live captions on the audience screen", CaptionsRuntime.Shared.Enabled, on => CaptionsRuntime.Shared.Enabled = on),
            Toggle("Voice commands (“next slide”, “go to slide 5”, “go to pricing”)", VoiceCommandsRuntime.Shared.Enabled, on => VoiceCommandsRuntime.Shared.Enabled = on),
            Toggle("Speech coach (speaking speed, filler words)", CoachRuntime.Shared.Listen, on => CoachRuntime.Shared.Listen = on),
            Note("Listening starts when the show starts, with Windows' speech recognition on this PC. Nothing is recorded or sent anywhere.")));

        // Claude Desktop
        var claude = new StackPanel();
        claude.Children.Add(Toggle("Let Claude read and write notes", McpServer.Shared.Enabled, on => { McpServer.Shared.Enabled = on; Build(); }));
        if (McpServer.Shared.Enabled)
        {
            claude.Children.Add(McpServer.ClaudeConnected
                ? Ui.Text("Claude Desktop is connected. Ask it to write notes for your slides.", 12, "Muted", wrap: true)
                : Ui.Pill("Add Bema to Claude Desktop", () =>
                {
                    try { McpServer.ConnectClaude(); Presenter.Shared.Notice = "Bema was added to Claude Desktop. Restart Claude Desktop to use it."; }
                    catch (Exception e) { Presenter.Shared.Notice = e.Message; }
                    Build();
                }).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 4, 0, 0); }));
        }
        claude.Children.Add(Note("Claude reads the slides from Bema only while this is on, through a connection only your Windows account can open. It uses your Claude subscription."));
        col.Children.Add(Section("Claude Desktop", claude));

        // Presenter window
        col.Children.Add(Section("Presenter window",
            Toggle("Hide from screen sharing", Settings.Get("hidePresenterFromSharing", true), on =>
            {
                Settings.Set("hidePresenterFromSharing", on);
                if (Owner is MainWindow w) w.ApplySharing();
            })));

        // Rehearsal
        col.Children.Add(Section("Rehearsal",
            Toggle("Rehearsal mode: report when the show ends", CoachRuntime.Shared.RehearsalMode, on => CoachRuntime.Shared.RehearsalMode = on),
            Toggle("Cue a minute before time is up", CoachRuntime.Shared.TimeCue, on => CoachRuntime.Shared.TimeCue = on),
            Toggle("Play a soft sound with the cue", CoachRuntime.Shared.CueSound, on => CoachRuntime.Shared.CueSound = on)));

        col.Children.Add(Section("Camera",
            Note("The self view and the camera bubble on the slides aren't in the Windows version yet.")));
    }
}

/// <summary>"Where should the slides go?" — asked when the show starts with more than one display.</summary>
public sealed class DisplayChooserWindow : PanelWindow
{
    public DisplayChooserWindow() : base("Where should the slides go?", 640, 330)
    {
        var m = Presenter.Shared;
        ResizeMode = ResizeMode.NoResize;
        var selection = m.AudienceDisplayChoice().Id;
        var presenterOn = m.PresenterWindowPixels?.Invoke() is { } r ? Display.Containing(r)?.Id : null;
        var cards = new WrapPanel();
        var warn = Ui.Text("Bema is on this display too, so the slideshow opens in a window here. Pick another display for full screen.", 12, "WarnText", wrap: true);
        void Select(string id)
        {
            selection = id;
            foreach (ToggleButton c in cards.Children) c.IsChecked = (string)c.Tag == id;
            warn.Visibility = id == presenterOn ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var d in Display.All())
        {
            var card = new ToggleButton
            {
                Tag = d.Id, Width = 190, Height = 110, Margin = new Thickness(0, 0, 12, 12),
                Content = Ui.Column(6, Ui.Icon("", 22, "TextSoft"), Ui.Text(d.Name, 13, "Text", FontWeights.SemiBold, wrap: true),
                                    Ui.Text(d.Id == presenterOn ? "Bema is here" : "", 11, "Muted")),
            };
            card.SetResourceReference(StyleProperty, "Pill");
            card.Click += (_, _) => Select(d.Id);
            card.MouseDoubleClick += (_, _) => { Select(d.Id); Start(); };
            cards.Children.Add(card);
        }
        void Start()
        {
            m.AudienceDisplayId = selection;
            m.DisplayChooserOpen = false;
            Close();
            _ = m.StartShow();
        }
        var ask = Toggle("Ask every time", m.AskForDisplay, on => m.AskForDisplay = on);
        ask.ToolTip = "Turn off to start on this display straight away. Change it later from “Slides on” in the top bar.";
        var start = Ui.Pill("Start slideshow", Start, "AccentPill").With(b => b.IsDefault = true);
        var cancel = Ui.Pill("Cancel", () => { m.DisplayChooserOpen = false; Close(); }).With(b => b.IsCancel = true);
        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 0) };
        var buttons = Ui.Row(10, cancel, start);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(ask);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                Ui.Text("Where should the slides go?", 20, "Text", FontWeights.SemiBold),
                Ui.Text("The audience sees the slideshow on this display. Your notes and the timer stay with Bema.", 13, "Muted", wrap: true).With(t => t.Margin = new Thickness(0, 6, 0, 16)),
                cards, warn, footer,
            },
        };
        Closed += (_, _) => m.DisplayChooserOpen = false;
        Select(selection);
    }
}

/// <summary>The talk report: total against plan, time per slide against its budget, pace and filler words.</summary>
public sealed class ReportWindow : PanelWindow
{
    public ReportWindow(TalkReport r) : base("Talk report", 560, 620)
    {
        ShowInTaskbar = true;
        var col = new StackPanel { Margin = new Thickness(22) };
        col.Children.Add(Ui.Caps(r.Rehearsal ? "Rehearsal" : "Talk"));
        col.Children.Add(Ui.Text(r.Deck, 20, "TextBright", FontWeights.SemiBold));
        var over = r.Target > 0 && r.Total > r.Target;
        col.Children.Add(Ui.Text($"{Coach.FormatDuration(r.Total)}{(r.Target > 0 ? $" of {Coach.FormatDuration(r.Target)} planned" : "")}", 15, over ? "LiveSoft" : "TextSoft")
            .With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        var stats = new List<string>();
        if (r.AverageWpm is { } wpm) stats.Add($"{wpm} words per minute");
        if (r.Fillers.Count > 0) stats.Add("Filler words: " + string.Join(", ", r.Fillers.OrderByDescending(f => f.Value).Take(5).Select(f => $"{f.Key} ×{f.Value}")));
        foreach (var s in stats) col.Children.Add(Ui.Text(s, 13, "Muted", wrap: true));
        col.Children.Add(new Border { Height = 16 });
        foreach (var s in r.Slides)
        {
            var overSlide = s.Budget > 0 && s.Seconds > s.Budget * 1.15;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var time = Ui.Text($"{Coach.FormatDuration(s.Seconds)}{(s.Budget > 0 ? $" / {Coach.FormatDuration(s.Budget)}" : "")}", 13, overSlide ? "LiveSoft" : "TextSoft");
            DockPanel.SetDock(time, Dock.Right);
            row.Children.Add(time);
            row.Children.Add(Ui.Text($"{s.Id}. {s.Title}", 13, "Text"));
            col.Children.Add(row);
        }
        var use = Ui.Pill("Use these timings as slide budgets", () => { CoachRuntime.Shared.SaveRehearsalBudgets(r); Close(); }, "AccentPill");
        use.Margin = new Thickness(0, 16, 0, 0);
        use.HorizontalAlignment = HorizontalAlignment.Left;
        col.Children.Add(use);
        col.Children.Add(Note("Reports keep timings and word counts only, on this PC. No audio or transcript is saved."));
        Content = new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

/// <summary>The small panel from the notification-area icon (the Mac app's menu bar panel).</summary>
public sealed class TrayPanel : Window
{
    readonly StackPanel col = new() { Margin = new Thickness(14) };
    readonly System.Windows.Threading.DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public TrayPanel()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 300;
        this.Bg("Panel");
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "BorderStrong");
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Content = col;
        Deactivated += (_, _) => Hide();
        tick.Tick += (_, _) => { if (IsVisible) Build(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) tick.Start(); else tick.Stop(); };
    }

    public void ShowNearTray()
    {
        Build();
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
        Activate();
    }

    void Build()
    {
        var m = Presenter.Shared;
        var app = BemaApp.Instance;
        col.Children.Clear();
        var head = Ui.Column(2, Ui.Text(m.Deck is null ? "No deck open" : m.DeckName, 13, "Text", FontWeights.SemiBold),
            Ui.Text(m.IsPresenting ? "Presenting" : m.HasDeck ? $"Slide {m.VisiblePosition} of {m.VisibleCount}" : "Ready", 11, "Muted"));
        col.Children.Add(head);
        if (m.HasDeck)
        {
            if (m.TimerShown)
            {
                var look = m.TimerLook(DateTime.UtcNow);
                col.Children.Add(Ui.Text(Coach.FormatTimer(m.Elapsed(DateTime.UtcNow)) + (m.TargetMinutes > 0 ? $"  of {m.TargetMinutes}:00" : ""), 22,
                    look switch { TimerLook.Over => "LiveSoft", TimerLook.Paused => "Muted", _ => "Text" }, FontWeights.SemiBold).With(t => t.Margin = new Thickness(0, 10, 0, 0)));
            }
            col.Children.Add(Ui.Row(8, Ui.IconButton("", "Previous", m.Previous), Ui.Text($"{m.VisiblePosition} / {m.VisibleCount}", 15, "Text", FontWeights.Medium), Ui.IconButton("", "Next", m.Next))
                .With(r => r.Margin = new Thickness(0, 8, 0, 0)));
            col.Children.Add(Ui.Row(8,
                m.IsPresenting ? Ui.Pill("End show", m.EndShowButton, "DangerPill") : Ui.Pill("Start slideshow", m.RequestStart, "AccentPill").With(b => b.IsEnabled = m.CanStart),
                Ui.Pill(PrompterRuntime.Shared.Running ? "Pause prompter" : "Prompter", PrompterRuntime.Shared.Toggle)).With(r => r.Margin = new Thickness(0, 8, 0, 0)));
        }
        col.Children.Add(Ui.Divider().With(d => d.Margin = new Thickness(0, 12, 0, 8)));
        Button Link(string label, Action a)
        {
            var b = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 8, 6), Height = double.NaN, Width = double.NaN };
            b.SetResourceReference(StyleProperty, "IconButton");
            b.Click += (_, _) => { Hide(); a(); };
            return b;
        }
        col.Children.Add(Link("Show Bema window", app.ShowMainWindow));
        col.Children.Add(Link("Present the deck open in PowerPoint  (Ctrl+Alt+P)", () => _ = m.PresentOpenDeck()));
        col.Children.Add(Link("Prompter for your own text", () => app.ShowPrompter(editOwnText: true)));
        col.Children.Add(Link("Open Bema when Windows starts" + (Startup.Enabled ? "  ✓" : ""), () => Startup.Enabled = !Startup.Enabled));
        col.Children.Add(Link("Quit Bema", app.Quit));
    }
}

/// <summary>Open Bema when you sign in to Windows (the Mac's Open at Login), per user.</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Bema") is string;
        }
        set
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe) k.SetValue("Bema", $"\"{exe}\"");
            else k.DeleteValue("Bema", false);
        }
    }
}

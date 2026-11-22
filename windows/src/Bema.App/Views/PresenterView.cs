using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Bema.App.Model;
using Bema.App.Speech;
using Bema.Core;

namespace Bema.App.Views;

/// <summary>The presenter view: top bar, notice, current and next slide, notes, filmstrip.</summary>
sealed class PresenterView : DockPanel
{
    readonly Presenter m = Presenter.Shared;
    readonly MainWindow window;

    // Top bar parts that change
    readonly ContentControl showControl = new();
    readonly TextBlock counter = Ui.Text("", 22, "TextBright", FontWeights.SemiBold);
    readonly TextBlock live = Ui.Text("LIVE", 10, "LiveSoft", FontWeights.Heavy);
    readonly Border liveBadge;
    readonly Dictionary<PointerTool, ToggleButton> toolButtons = [];
    readonly ToggleButton blackButton;
    readonly TimerCluster timer = new();
    readonly TextBlock slidesOn = Ui.Text("", 12, "TextSoft");

    // Notice
    readonly Border noticeBar;
    readonly TextBlock noticeText = Ui.Text("", 13, "WarnText", wrap: true);

    // Body
    readonly SlideSurface currentSlide = new();
    readonly Image nextImage = new() { Stretch = Stretch.Uniform, MaxHeight = 220 };
    readonly TextBlock nextTitle = Ui.Text("", 13, "TextSoft");
    readonly NotesPanel notes = new();
    readonly ListBox filmstrip = new();

    public PresenterView(MainWindow window)
    {
        this.window = window;
        LastChildFill = true;

        liveBadge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 2, 7, 2), Child = live, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Bg("LiveBg");
        blackButton = Ui.IconToggle("", "Black out the audience screen (B). White screen: W", m.ToggleBlack);

        var bar = TopBar();
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        noticeBar = new Border { Padding = new Thickness(16, 10, 16, 10), BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed }.Bg("WarnBg");
        noticeBar.SetResourceReference(Border.BorderBrushProperty, "WarnBorder");
        var close = Ui.IconButton("", "Dismiss", () => m.Notice = null, 12);
        DockPanel.SetDock(close, Dock.Right);
        noticeBar.Child = new DockPanel { Children = { close, Ui.Row(10, Ui.Icon("", 14, "WarnStrong"), noticeText) } };
        SetDock(noticeBar, Dock.Top);
        Children.Add(noticeBar);

        filmstrip.Height = 108;
        filmstrip.BorderThickness = new Thickness(0, 1, 0, 0);
        filmstrip.SetResourceReference(BorderBrushProperty, "Border");
        filmstrip.Bg("Bar");
        ScrollViewer.SetHorizontalScrollBarVisibility(filmstrip, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(filmstrip, ScrollBarVisibility.Disabled);
        filmstrip.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)).With(f => f.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal)));
        filmstrip.SelectionChanged += (_, _) => { if (filmstrip.SelectedItem is ListBoxItem { Tag: int id } && id != m.Current) m.GoTo(id); };
        SetDock(filmstrip, Dock.Bottom);
        Children.Add(filmstrip);

        // Slides on the left, notes on the right, with a splitter between.
        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Settings.Get("slideColumn", 0.42), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Settings.Get("slideColumn", 0.42), GridUnitType.Star) });
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.Children.Add(currentSlide);
        var next = Ui.Column(8, Ui.Caps("Next"), nextImage, nextTitle);
        next.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(next, 1);
        left.Children.Add(next);
        grid.Children.Add(left);
        var splitter = new GridSplitter { Width = 10, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.DragCompleted += (_, _) =>
        {
            var a = grid.ColumnDefinitions[0].ActualWidth; var b = grid.ColumnDefinitions[2].ActualWidth;
            if (a + b > 0) Settings.Set("slideColumn", a / (a + b));
        };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        Grid.SetColumn(notes, 2);
        grid.Children.Add(notes);
        Children.Add(grid);
    }

    FrameworkElement TopBar()
    {
        var bar = new Border { Padding = new Thickness(14, 10, 14, 10), BorderThickness = new Thickness(0, 0, 0, 1) }.Bg("Bar");
        bar.SetResourceReference(Border.BorderBrushProperty, "Border");

        var deck = Ui.Column(2, Ui.Text(m.DeckName, 14, "TextBright", FontWeights.SemiBold),
            Ui.MenuButton(Ui.Row(5, Ui.Text("Slides on", 12, "Muted"), slidesOn, Ui.Icon("", 9, "Muted")), "Which display the audience sees", DisplayMenu, "IconButton")
                .With(b => { b.Height = 20; b.HorizontalAlignment = HorizontalAlignment.Left; b.Padding = new Thickness(0); }));
        deck.MaxWidth = 220;

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var t in Enum.GetValues<PointerTool>())
        {
            var b = Ui.IconToggle(t.Glyph(), t.Label(), () => m.Tool = t == PointerTool.None ? PointerTool.None : (m.Tool == t ? PointerTool.None : t));
            toolButtons[t] = b;
            tools.Children.Add(b);
        }
        tools.Children.Add(Ui.Divider(vertical: true));
        tools.Children.Add(Ui.MenuButton(Ui.Icon("", 15), "Effects: laser style, stamps, reactions", EffectsMenu));
        tools.Children.Add(Ui.IconButton("", "Clear drawings (E)", m.ClearDrawings));
        var toolsBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(3), BorderThickness = new Thickness(1), Child = tools, Margin = new Thickness(14, 0, 0, 0) }.Bg("Ground");
        toolsBox.SetResourceReference(Border.BorderBrushProperty, "Border");

        var nav = Ui.Row(10,
            Ui.IconButton("", "Previous slide (←)", m.Previous),
            counter,
            Ui.IconButton("", "Next slide (→)", m.Next).With(b => b.Bg("AccentSoft")));

        var right = Ui.Row(6,
            timer,
            Ui.IconButton("", "Prompter (Ctrl+Shift+P)", () => BemaApp.Instance.ShowPrompter()),
            Ui.IconButton("", "Stage tools: phone remote, poll, listening, rehearsal", () => new StageToolsWindow { Owner = window }.Show()),
            Ui.MenuButton(Ui.Icon("", 16), "More", MoreMenu));

        var dock = new DockPanel { LastChildFill = false };
        var leftRow = Ui.Row(14, showControl, deck, liveBadge, nav, toolsBox, blackButton);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(leftRow);
        bar.Child = new ScrollViewer { Content = dock, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return bar;
    }

    ContextMenu DisplayMenu()
    {
        var menu = new ContextMenu();
        var chosen = m.AudienceDisplayChoice().Id;
        foreach (var d in Display.All()) menu.Items.Add(Ui.Item(d.Name, () => m.AudienceDisplayId = d.Id, d.Id == chosen));
        if (Display.All().Count > 1)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Item("Ask before each slideshow", () => m.AskForDisplay = !m.AskForDisplay, m.AskForDisplay));
        }
        return menu;
    }

    ContextMenu EffectsMenu()
    {
        var menu = new ContextMenu();
        var laser = new MenuItem { Header = "Laser style" };
        foreach (var s in Enum.GetValues<LaserStyle>()) laser.Items.Add(Ui.Item(s.Label(), () => { m.LaserStyle = s; m.Tool = PointerTool.Laser; }, m.LaserStyle == s));
        menu.Items.Add(laser);
        var stamps = new MenuItem { Header = "Stamp" };
        foreach (var k in Enum.GetValues<StampKind>()) stamps.Items.Add(Ui.Item(k.Label(), () => { m.StampKind = k; m.Tool = PointerTool.Stamp; }, m.StampKind == k));
        menu.Items.Add(stamps);
        menu.Items.Add(new Separator());
        foreach (var r in Enum.GetValues<ReactionKind>()) menu.Items.Add(Ui.Item(r.Label(), () => m.FireReaction(r), enabled: m.IsPresenting));
        return menu;
    }

    ContextMenu MoreMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Open presentation…  Ctrl+O", window.OpenDeck));
        menu.Items.Add(Ui.Item("Close deck", () => _ = m.CloseDeck()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Keep window on top  Ctrl+T", () => window.Topmost = !window.Topmost, window.Topmost));
        var theme = new MenuItem { Header = "Theme" };
        foreach (var (name, label) in new[] { (ThemeName.Backstage, "Backstage (dark)"), (ThemeName.Daylight, "Daylight (bright rooms)"), (ThemeName.Contrast, "High contrast") })
            theme.Items.Add(Ui.Item(label, () => Theme.Name = name, Theme.Name == name));
        menu.Items.Add(theme);
        var target = new MenuItem { Header = "Planned length" };
        foreach (var min in new[] { 5, 10, 15, 20, 30, 45, 60, 90 }) target.Items.Add(Ui.Item($"{min} minutes", () => m.TargetMinutes = min, m.TargetMinutes == min));
        target.Items.Add(Ui.Item("No target", () => m.TargetMinutes = 0, m.TargetMinutes == 0));
        menu.Items.Add(target);
        menu.Items.Add(Ui.Item("Reset timer", m.ResetTimer));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Write notes for slides without them…", () => NotesWriting.WriteMissing(window), enabled: DraftNotes.NeedingNotes(m.Slides).Count > 0));
        menu.Items.Add(Ui.Item("Notes writer settings…", () => new NoteWriterSettingsWindow { Owner = window }.ShowDialog()));
        menu.Items.Add(Ui.Item("Show last talk report", () => { if (CoachRuntime.Shared.LastReport is { } r) new ReportWindow(r) { Owner = window }.Show(); }, enabled: CoachRuntime.Shared.LastReport is not null));
        return menu;
    }

    public void ModelChanged(string? property)
    {
        var all = string.IsNullOrEmpty(property);
        if (all || property is nameof(Presenter.Phase) or nameof(Presenter.CanStart))
        {
            showControl.Content = m.IsPresenting
                ? Ui.Pill("End show", m.EndShowButton, "DangerPill", "", "End the slideshow (Esc)")
                : Ui.Pill("Start slideshow", m.RequestStart, "AccentPill", "", "Start the slideshow (Ctrl+Enter)").With(b => b.IsEnabled = m.CanStart);
            liveBadge.Visibility = m.IsPresenting ? Visibility.Visible : Visibility.Collapsed;
            live.Text = m.IsPresenting && !m.AudienceFullScreen ? "REHEARSAL" : "LIVE";
        }
        if (all || property is nameof(Presenter.Current) or nameof(Presenter.Slides) or nameof(Presenter.VisiblePosition) or nameof(Presenter.CurrentSlide))
        {
            counter.Inlines.Clear();
            counter.Inlines.Add(new Run(m.VisiblePosition.ToString()));
            counter.Inlines.Add(new Run($" / {m.VisibleCount}") { FontSize = 15, Foreground = Theme.Brush("Muted") });
            currentSlide.Slide = m.Current;
            nextImage.Source = m.NextSlide is { } n ? m.Image(n.Id, 640) : null;
            nextTitle.Text = m.NextSlide is { } nx ? nx.Title : "End of the deck";
            notes.Show(m.CurrentSlide);
            if (all || property is nameof(Presenter.Slides)) FillFilmstrip();
            foreach (ListBoxItem item in filmstrip.Items)
            {
                if (item.Tag is int id && id == m.Current) { filmstrip.SelectedItem = item; filmstrip.ScrollIntoView(item); }
            }
        }
        if (all || property is nameof(Presenter.Tool))
            foreach (var (t, b) in toolButtons) b.IsChecked = m.Tool == t;
        if (all || property is nameof(Presenter.Audience) or nameof(Presenter.Phase))
        {
            blackButton.IsChecked = m.Audience == AudienceScreen.Black;
            blackButton.IsEnabled = m.IsPresenting;
        }
        if (all || property is nameof(Presenter.Notice))
        {
            noticeText.Text = m.Notice ?? "";
            noticeBar.Visibility = m.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (all || property is nameof(Presenter.AudienceDisplayId) or nameof(Presenter.AudienceDisplayName) or nameof(Presenter.DisplayCount))
            slidesOn.Text = m.AudienceDisplayName;
        if (all || property is nameof(Presenter.AudienceFullScreen))
            live.Text = m.IsPresenting && !m.AudienceFullScreen ? "REHEARSAL" : "LIVE";
    }

    void FillFilmstrip()
    {
        filmstrip.Items.Clear();
        foreach (var s in m.Slides)
        {
            var img = new Image { Source = m.Image(s.Id, 240), Height = 68, Stretch = Stretch.Uniform, Opacity = s.Hidden ? 0.35 : 1 };
            var label = Ui.Text($"{s.Id}{(s.Draft ? " · draft" : "")}", 11, "Muted");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            filmstrip.Items.Add(new ListBoxItem { Tag = s.Id, Content = Ui.Column(4, img, label), Padding = new Thickness(6), ToolTip = s.Title });
        }
    }

    public void Tick() => timer.Tick();
}

/// <summary>The timer: elapsed against the planned length, the clock, pace, and start/pause.</summary>
sealed class TimerCluster : StackPanel
{
    readonly Presenter m = Presenter.Shared;
    readonly TextBlock elapsed = Ui.Text("00:00", 24, "Text", FontWeights.SemiBold);
    readonly TextBlock of = Ui.Text("", 12, "Muted");
    readonly ProgressBar bar = new() { Height = 5, Width = 126, Maximum = 1, BorderThickness = new Thickness(0) };
    readonly TextBlock clock = Ui.Text("", 17, "TextSoft", FontWeights.Medium);
    readonly TextBlock pace = Ui.Text("", 11, "Muted");
    readonly Button toggle;

    public TimerCluster()
    {
        Orientation = Orientation.Horizontal;
        elapsed.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        Typography.SetNumeralAlignment(elapsed, FontNumeralAlignment.Tabular);
        bar.SetResourceReference(Control.BackgroundProperty, "Border");
        var left = Ui.Column(4, Ui.Row(8, elapsed, of), bar);
        left.Margin = new Thickness(0, 0, 12, 0);
        var mid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0), Children = { clock, pace } };
        clock.HorizontalAlignment = HorizontalAlignment.Right; pace.HorizontalAlignment = HorizontalAlignment.Right;
        toggle = Ui.IconButton("", "Start the timer", m.ToggleTimer);
        toggle.ContextMenu = new ContextMenu { Items = { Ui.Item("Reset timer", m.ResetTimer) } };
        Children.Add(left); Children.Add(mid); Children.Add(toggle);
        Tick();
    }

    public void Tick()
    {
        var now = DateTime.UtcNow;
        var e = m.Elapsed(now);
        var look = m.TimerLook(now);
        var target = m.TargetSeconds;
        elapsed.Text = Coach.FormatTimer(e);
        elapsed.Fg(look switch { TimerLook.Over => "LiveSoft", TimerLook.Paused => "Muted", _ => "Text" });
        if (CoachRuntime.Shared.CueFlashUntil is { } until && until > now)
            elapsed.Opacity = (int)(now.Ticks / 5_000_000) % 2 == 0 ? 0.4 : 1;
        else elapsed.Opacity = 1;
        of.Text = target > 0 ? $"of {m.TargetMinutes}:00" : "no target";
        bar.Visibility = target > 0 ? Visibility.Visible : Visibility.Collapsed;
        bar.Value = target > 0 ? Math.Min(e / target, 1) : 0;
        bar.SetResourceReference(Control.ForegroundProperty, look switch { TimerLook.Over => "Live", TimerLook.Paused => "Dim", _ => "Accent" });
        clock.Text = DateTime.Now.ToString("t");
        if (CoachRuntime.Shared.PaceDelta(now) is { } delta)
        {
            pace.Text = Coach.PaceText(delta);
            pace.Fg(delta > 30 ? "LiveSoft" : delta < -30 ? "Ok" : "Muted");
        }
        else if (target > 0 && look != TimerLook.Paused)
        {
            pace.Text = look == TimerLook.Over ? "over time" : $"ends ~{DateTime.Now.AddSeconds(Math.Max(target - e, 0)):t}";
            pace.Fg(look == TimerLook.Over ? "LiveSoft" : "Muted");
        }
        else pace.Text = "";
        toggle.Content = Ui.Icon(m.TimerRunning ? "" : "", 15);
        toggle.ToolTip = m.TimerRunning ? "Pause the timer" : (e > 0 ? "Paused. The next slideshow starts the timer from 0:00." : "Start the timer");
    }
}

/// <summary>The current slide's speaker notes: large, with cues and bold, editable as a draft.</summary>
sealed class NotesPanel : DockPanel
{
    readonly Presenter m = Presenter.Shared;
    readonly FlowDocumentScrollViewer viewer = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
    readonly TextBlock title = Ui.Text("", 15, "TextBright", FontWeights.SemiBold);
    readonly StackPanel draftBar = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    readonly TextBox editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    SlideInfo? slide;

    double FontSize_
    {
        get => Settings.Get("notesFontSize", 26.0);
        set { Settings.Set("notesFontSize", Math.Clamp(value, 14, 72)); Show(slide); }
    }

    public NotesPanel()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var size = Ui.Row(2,
            Ui.IconButton("", "Smaller notes", () => FontSize_ -= 2, 12),
            Ui.IconButton("", "Larger notes", () => FontSize_ += 2, 12),
            Ui.IconButton("", "Edit these notes (kept as a draft until you save them into the deck)", Edit, 13));
        DockPanel.SetDock(size, Dock.Right);
        header.Children.Add(size);
        header.Children.Add(Ui.Column(2, Ui.Caps("Notes"), title));
        SetDock(header, Dock.Top);
        Children.Add(header);

        draftBar.Children.Add(Ui.Text("Draft, not in the deck yet", 12, "WarnStrong"));
        draftBar.Children.Add(Ui.Pill("Save to deck", async () =>
        {
            try { var n = await m.SaveDrafts(); m.Notice = n > 0 ? null : "PowerPoint didn't take the notes. Is the deck still open there?"; }
            catch (Exception e) { m.Notice = $"Couldn't save the notes into the deck: {e.Message}"; }
        }, "AccentPill").With(b => { b.Height = 28; b.Margin = new Thickness(12, 0, 0, 0); }));
        draftBar.Children.Add(Ui.Pill("Discard", () => { if (slide is not null) m.SetDraft(slide.Id, null); }).With(b => { b.Height = 28; b.Margin = new Thickness(8, 0, 0, 0); }));
        draftBar.Margin = new Thickness(0, 10, 0, 0);
        SetDock(draftBar, Dock.Bottom);
        Children.Add(draftBar);

        editor.LostKeyboardFocus += (_, _) => FinishEdit();
        editor.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { FinishEdit(); e.Handled = true; } };
        var stack = new Grid();
        stack.Children.Add(viewer);
        stack.Children.Add(editor);
        Children.Add(stack);
    }

    void Edit()
    {
        if (slide is null) return;
        editor.Text = slide.Notes;
        editor.FontSize = FontSize_ * 0.7;
        editor.Visibility = Visibility.Visible;
        viewer.Visibility = Visibility.Collapsed;
        editor.Focus();
    }

    void FinishEdit()
    {
        if (editor.Visibility != Visibility.Visible || slide is null) return;
        editor.Visibility = Visibility.Collapsed;
        viewer.Visibility = Visibility.Visible;
        if (!DraftNotes.Same(editor.Text, slide.Notes)) m.SetDraft(slide.Id, editor.Text);
    }

    public void Show(SlideInfo? s)
    {
        slide = s;
        title.Text = s?.Title ?? "";
        draftBar.Visibility = s?.Draft == true ? Visibility.Visible : Visibility.Collapsed;
        var size = FontSize_;
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = size, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "Text");
        var text = s?.Notes ?? "";
        if (text.Trim().Length == 0)
        {
            var p = new Paragraph(new Run(s is null ? "" : "No speaker notes for this slide.") { FontStyle = FontStyles.Italic }) { FontSize = size * 0.6 };
            p.SetResourceReference(Paragraph.ForegroundProperty, "Dim");
            doc.Blocks.Add(p);
            if (s is not null && !s.Hidden)
            {
                var write = new Button { Content = "Write notes for this slide…", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                write.SetResourceReference(StyleProperty, "Pill");
                write.Click += (_, _) => NotesWriting.WriteOne(Window.GetWindow(this), s.Id);
                doc.Blocks.Add(new BlockUIContainer(write));
            }
        }
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, size * 0.5), TextAlignment = ScriptText.IsRightToLeft(line) ? TextAlignment.Right : TextAlignment.Left, FlowDirection = ScriptText.IsRightToLeft(line) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
            foreach (var run in NoteCues.Runs(line))
            {
                if (run.Cue)
                {
                    var cue = new Run($" {run.Text} ") { FontSize = size * 0.5, FontWeight = FontWeights.Bold };
                    cue.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                    cue.SetResourceReference(TextElement.BackgroundProperty, "AccentSoft");
                    p.Inlines.Add(cue);
                }
                else p.Inlines.Add(new Run(run.Text) { FontWeight = run.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = run.Italic ? FontStyles.Italic : FontStyles.Normal });
            }
            doc.Blocks.Add(p);
        }
        viewer.Document = doc;
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Bema.App.Model;
using Bema.App.Platform;
using Bema.App.Speech;
using Bema.Core;

namespace Bema.App.Views;

/// <summary>
/// The detached prompter: a floating window to put under the camera (or on any display, mirrored for
/// teleprompter glass), hidden from screen sharing and screenshots unless you choose otherwise. It
/// prompts the deck's notes or your own pasted text.
/// </summary>
public sealed class PrompterWindow : Window
{
    readonly PrompterRuntime p = PrompterRuntime.Shared;
    readonly DockPanel root = new();
    readonly ScriptView script = new();
    readonly ContentControl body = new();
    readonly StackPanel controls = new() { Margin = new Thickness(14, 10, 14, 10) };
    readonly Border controlsBox;
    readonly Border collapsedBar;
    readonly TextBlock status = Ui.Text("", 12, "Muted", wrap: true);

    bool FloatOnTop { get => Settings.Get("prompterFloat", true); set { Settings.Set("prompterFloat", value); Topmost = value; Rebuild(); } }
    bool HideFromSharing { get => Settings.Get("prompterHideFromSharing", true); set { Settings.Set("prompterHideFromSharing", value); ApplySharing(); Rebuild(); } }
    bool Collapsed { get => Settings.Get("prompterControlsCollapsed", false); set { Settings.Set("prompterControlsCollapsed", value); Rebuild(); } }

    public PrompterWindow()
    {
        Title = "Bema Prompter";
        Width = Settings.Get("prompterWidth", 720.0); Height = Settings.Get("prompterHeight", 360.0);
        MinWidth = 360; MinHeight = 180;
        Topmost = FloatOnTop;
        this.Bg("Panel");
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Bema.ico"));
        controlsBox = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = controls }.Bg("Bar");
        controlsBox.SetResourceReference(Border.BorderBrushProperty, "Border");
        collapsedBar = new Border { Height = 30 }.Bg("Bar");
        DockPanel.SetDock(controlsBox, Dock.Top);
        DockPanel.SetDock(collapsedBar, Dock.Top);
        root.Children.Add(controlsBox);
        root.Children.Add(collapsedBar);
        root.Children.Add(body);
        Content = root;
        SourceInitialized += (_, _) => ApplySharing();
        SizeChanged += (_, _) => { Settings.Set("prompterWidth", Width); Settings.Set("prompterHeight", Height); };
        p.PropertyChanged += OnPrompter;
        SpeechHub.Shared.PropertyChanged += OnHub;
        Closed += (_, _) => { p.PropertyChanged -= OnPrompter; SpeechHub.Shared.PropertyChanged -= OnHub; };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBoxBase) return;
            if (e.Key == Key.Space) { p.Toggle(); e.Handled = true; }
            else if (Presenter.Shared.HandleKey(e.Key)) e.Handled = true;
        };
        // Drag the window by its controls bar.
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border or StackPanel or DockPanel) try { DragMove(); } catch (InvalidOperationException) { } };
        Rebuild();
    }

    void OnPrompter(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PrompterRuntime.Position)) return;   // the script view follows that itself
        Rebuild();
    }

    void OnHub(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SpeechHub.Status)) UpdateStatus();
    }

    /// <summary>Kept out of screen sharing, recordings and screenshots (Windows 10 2004 and later).</summary>
    void ApplySharing()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(hwnd, HideFromSharing ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    /// <summary>The top of the main display, just under a laptop's camera.</summary>
    void PlaceUnderCamera()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.6);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }

    void UpdateStatus()
    {
        var text = p.Status ?? (p.Mode == PrompterMode.Voice && p.Running ? SpeechHub.Shared.Status : null);
        status.Text = text ?? "";
        status.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void Rebuild()
    {
        var detachedEditor = p.ShowsOwnScriptEditor(detached: true);
        if (detachedEditor) { if (body.Content is not OwnScriptEditor) body.Content = new OwnScriptEditor(); }
        else body.Content = script;

        controlsBox.Visibility = Collapsed ? Visibility.Collapsed : Visibility.Visible;
        collapsedBar.Visibility = Collapsed ? Visibility.Visible : Visibility.Collapsed;
        collapsedBar.Child = Ui.Row(8,
            Ui.IconButton(p.Running ? "" : "", p.Running ? "Pause" : "Start", p.Toggle, 12),
            Ui.Icon(HideFromSharing ? "" : "", 12, "Dim").With(i => i.ToolTip = HideFromSharing ? "Hidden from screen sharing" : "Visible in screen sharing"),
            Ui.IconButton("", "Show the controls", () => Collapsed = false, 12)).With(r => r.HorizontalAlignment = HorizontalAlignment.Right);

        controls.Children.Clear();
        var start = Ui.Pill(p.Running ? "Pause" : "Start", p.Toggle, p.Running ? "Pill" : "AccentPill", p.Running ? "" : "");
        start.Height = 32;
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var mode in Enum.GetValues<PrompterMode>())
        {
            var b = new ToggleButton { Content = mode.ToString(), IsChecked = p.Mode == mode, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 2, 0) };
            b.SetResourceReference(StyleProperty, "Pill");
            b.Height = 28;
            b.Click += (_, _) => p.Mode = mode;
            if (p.Mode == mode) b.SetResourceReference(BackgroundProperty, "Selected");
            modes.Children.Add(b);
        }
        modes.ToolTip = "Voice follows what you say · Auto scrolls at a set speed · Manual: drag, scroll or click a word";

        var options = Ui.Row(2,
            Labeled(FloatOnTop ? "" : "", "On top", FloatOnTop, () => FloatOnTop = !FloatOnTop, FloatOnTop ? "Floating above other windows" : "Float above other windows"),
            Labeled(HideFromSharing ? "" : "", HideFromSharing ? "Hidden" : "Shared", HideFromSharing, () => HideFromSharing = !HideFromSharing,
                    HideFromSharing ? "Hidden from screen sharing and screenshots" : "Visible in screen sharing"),
            Labeled("", "Camera", false, PlaceUnderCamera, "Move this window to the top of the screen, just under the camera"),
            Labeled("", "Hide", false, () => Collapsed = true, "Hide the controls"));
        var top = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(options, Dock.Right);
        top.Children.Add(options);
        top.Children.Add(Ui.Row(10, start, modes));
        controls.Children.Add(top);

        var second = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        FrameworkElement modeRow = p.Mode switch
        {
            PrompterMode.Voice => Ui.Row(10, new MicLevel(), Ui.MenuButton(Ui.Row(4, Ui.Text(LanguageLabel(p.Language), 12, "TextSoft"), Ui.Icon("", 8, "Muted")), "Language to listen in", LanguageMenu)),
            PrompterMode.Auto => Ui.Row(10, Ui.Text($"{(int)p.WordsPerMinute} wpm", 12, "TextSoft"),
                new Slider { Minimum = 60, Maximum = 240, TickFrequency = 5, IsSnapToTickEnabled = true, Value = p.WordsPerMinute, Width = 160 }
                    .With(sl => sl.ValueChanged += (_, e) => p.WordsPerMinute = e.NewValue)),
            _ => new Border(),
        };
        second.Children.Add(modeRow);
        var right = Ui.Row(2,
            Labeled("", "Text", p.OwnScript is not null, () => p.EditingOwnScript = true,
                p.OwnScript is null ? "Prompt your own text instead of the deck's notes: paste or type it" : "Prompting your own text. Click to edit it or go back to the deck's notes"));
        if (p.OwnScript is null)
            right.Children.Add(Labeled("", "Follow", p.FollowSlides, () => p.FollowSlides = !p.FollowSlides,
                p.FollowSlides ? "Follow slides is on: the slideshow moves on when the script reaches the next slide, and the script jumps when you change slides"
                               : "Follow slides is off: the script and the slideshow move separately"));
        right.Children.Add(Ui.MenuButton(Ui.Column(0, Ui.Icon("", 14), Ui.Text("Line", 10, "Muted")), "Reading line: where the line you're reading sits", LineMenu));
        right.Children.Add(Labeled("", "Mirror", p.Mirrored, () => p.Mirrored = !p.Mirrored, "Mirror the text left-to-right, for teleprompter glass"));
        right.Children.Add(Labeled("", "Smaller", false, () => script.FontSize_ -= 4, "Smaller text"));
        right.Children.Add(Labeled("", "Bigger", false, () => script.FontSize_ += 4, "Bigger text"));
        DockPanel.SetDock(right, Dock.Right);
        second.Children.Add(right);
        controls.Children.Add(second);
        controls.Children.Add(status.With(s => s.Margin = new Thickness(0, 6, 0, 0)));
        UpdateStatus();
    }

    static string LanguageLabel(PrompterLanguage l) => l switch { PrompterLanguage.English => "English", PrompterLanguage.Hebrew => "Hebrew", _ => "Detect language" };

    ContextMenu LanguageMenu()
    {
        var m = new ContextMenu();
        foreach (var l in Enum.GetValues<PrompterLanguage>()) m.Items.Add(Ui.Item(LanguageLabel(l), () => p.Language = l, p.Language == l));
        return m;
    }

    ContextMenu LineMenu()
    {
        var m = new ContextMenu();
        foreach (var (v, label) in new[] { (0.16, "Top (see the most ahead)"), (0.3, "Upper third"), (0.45, "Middle") })
            m.Items.Add(Ui.Item(label, () => script.ReadingLine = v, Math.Abs(script.ReadingLine - v) < 0.01));
        return m;
    }

    /// <summary>A small icon with a one-word label under it, so less obvious controls explain themselves.</summary>
    static Button Labeled(string glyph, string label, bool on, Action click, string tip)
    {
        var b = new Button { ToolTip = tip, Width = double.NaN, Height = double.NaN, Padding = new Thickness(6, 2, 6, 2) };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.Content = Ui.Column(1, Ui.Icon(glyph, 14, on ? "Accent" : "TextSoft"), Ui.Text(label, 10, on ? "Accent" : "Muted").With(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        System.Windows.Automation.AutomationProperties.SetName(b, label);
        b.Click += (_, _) => click();
        return b;
    }
}

/// <summary>The microphone level while listening.</summary>
sealed class MicLevel : Border
{
    readonly Border fill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };

    public MicLevel()
    {
        Width = 60; Height = 6; CornerRadius = new CornerRadius(3);
        this.Bg("Border");
        fill.Bg("Accent");
        Child = fill;
        ToolTip = "Microphone level (Windows' default microphone)";
        SpeechHub.Shared.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SpeechHub.Level)) fill.Width = 60 * SpeechHub.Shared.Level; };
    }
}

/// <summary>The script, moving so the word you're on sits at the reading line.</summary>
sealed class ScriptView : Grid
{
    readonly PrompterRuntime p = PrompterRuntime.Shared;
    readonly Canvas canvas = new() { ClipToBounds = true };
    readonly StackPanel column = new();
    readonly TranslateTransform shift = new();
    readonly Border readingBand = new() { IsHitTestVisible = false };
    readonly TextBlock readingArrow = Ui.Icon("", 14, "Accent");
    readonly Dictionary<int, TextBlock> wordViews = [];
    readonly TextBlock empty = Ui.Text("This deck has no speaker notes yet. Add notes in PowerPoint and they become your script.", 15, "Muted", wrap: true);
    double[] tops = [];
    double? dragStart;
    Point dragFrom;
    int shownCurrent = -1;

    public double FontSize_
    {
        get => Settings.Get("prompterWindowFont", 44.0);
        set { Settings.Set("prompterWindowFont", Math.Clamp(value, 18, 120)); Rebuild(); }
    }

    public double ReadingLine
    {
        get => Settings.Get("prompterReadingLine", 0.16);
        set { Settings.Set("prompterReadingLine", value); Follow(); }
    }

    public ScriptView()
    {
        this.Bg("Panel");
        column.RenderTransform = shift;
        canvas.Children.Add(column);
        readingBand.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        readingBand.Opacity = 0.45;
        Children.Add(canvas);
        Children.Add(readingBand);
        Children.Add(readingArrow);
        Children.Add(empty);
        readingBand.VerticalAlignment = VerticalAlignment.Top;
        readingArrow.VerticalAlignment = VerticalAlignment.Top;
        readingArrow.HorizontalAlignment = HorizontalAlignment.Left;
        empty.Margin = new Thickness(24);
        // Fade the text above the reading line and at the bottom.
        OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = { new GradientStop(Color.FromArgb(90, 0, 0, 0), 0), new GradientStop(Colors.Black, 0.12), new GradientStop(Colors.Black, 0.9), new GradientStop(Colors.Transparent, 1) },
        };
        SizeChanged += (_, _) => { canvas.Width = ActualWidth; column.Width = Math.Max(100, ActualWidth - Pad * 2); Canvas.SetLeft(column, Pad); Measure(); };
        p.ScriptChanged += Rebuild;
        Loaded += (_, _) => Rebuild();
        p.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PrompterRuntime.Position)) Follow();
            if (e.PropertyName == nameof(PrompterRuntime.Mirrored)) RenderTransform = new ScaleTransform(p.Mirrored ? -1 : 1, 1, ActualWidth / 2, 0);
        };
        MouseLeftButtonDown += (_, e) => { dragStart = Y(p.Position); dragFrom = e.GetPosition(this); CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (dragStart is not { } s || e.LeftButton != MouseButtonState.Pressed) return;
            var dy = e.GetPosition(this).Y - dragFrom.Y;
            if (Math.Abs(dy) > 6) p.SetPosition(PositionFor(s - dy));
        };
        MouseLeftButtonUp += (_, e) =>
        {
            // A click without a drag jumps to the word clicked.
            var moved = Math.Abs(e.GetPosition(this).Y - dragFrom.Y) > 6;
            dragStart = null;
            ReleaseMouseCapture();
            if (!moved && column.InputHitTest(e.GetPosition(column)) is FrameworkElement { Tag: int id }) p.Jump(id);
        };
        // The mouse wheel moves the script in every mode; Voice and Auto carry on from there.
        MouseWheel += (_, e) => { p.SetPosition(PositionFor(Y(p.Position) - e.Delta * 0.6)); e.Handled = true; };
    }

    double Pad => Math.Max(24, ActualWidth * 0.06);

    public void Rebuild()
    {
        var size = FontSize_;
        column.Children.Clear();
        wordViews.Clear();
        empty.Visibility = p.Words.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var para in p.Paragraphs)
        {
            if (para.IsSlideStart && para.Slide > 0)
                column.Children.Add(Ui.Text(("Slide " + para.Slide + (para.SlideTitle.Length > 0 ? " · " + para.SlideTitle : "")).ToUpperInvariant(), 12, "Dim", FontWeights.Bold)
                    .With(t => t.Margin = new Thickness(0, size * 0.5, 0, size * 0.2)));
            var wrap = new WrapPanel { FlowDirection = para.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, Margin = new Thickness(0, 0, 0, size * 0.6) };
            foreach (var w in para.Words)
            {
                TextBlock t;
                if (w.IsPlaceholder) t = Ui.Text(w.Text, size * 0.5, "Dim").With(x => x.FontStyle = FontStyles.Italic);
                else if (w.IsCue)
                {
                    t = Ui.Text(w.Text, size * 0.42, "Accent", FontWeights.Heavy);
                    t.SetResourceReference(TextBlock.BackgroundProperty, "AccentSoft");
                    t.Padding = new Thickness(size * 0.25, size * 0.08, size * 0.25, size * 0.08);
                    t.VerticalAlignment = VerticalAlignment.Center;
                }
                else t = Ui.Text(w.Text, size, "TextBright", w.Bold ? FontWeights.Heavy : FontWeights.Medium);
                t.Margin = new Thickness(0, 0, size * 0.27, size * 0.3);
                t.Cursor = Cursors.Hand;
                t.Tag = w.Id;
                wordViews[w.Id] = t;
                wrap.Children.Add(t);
            }
            column.Children.Add(wrap);
        }
        column.Children.Add(new Border { Height = 2000 });
        readingBand.Height = size * 1.45;
        readingArrow.FontSize = Math.Max(12, size * 0.35);
        shownCurrent = -1;
        Dispatcher.BeginInvoke(new Action(Measure), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Reads where each word sits after layout, to move the script smoothly between words.</summary>
    void Measure()
    {
        column.UpdateLayout();
        tops = new double[p.Words.Count];
        foreach (var (id, view) in wordViews)
            if (id < tops.Length && view.IsLoaded) tops[id] = view.TranslatePoint(new Point(0, 0), column).Y;
        Follow();
    }

    double Y(double position)
    {
        if (tops.Length == 0) return 0;
        var i = Math.Clamp((int)position, 0, tops.Length - 1);
        var frac = position - i;
        var next = i + 1 < tops.Length ? tops[i + 1] : tops[i] + FontSize_ * 1.4;
        return tops[i] + frac * (next - tops[i]);
    }

    double PositionFor(double y)
    {
        if (tops.Length == 0) return 0;
        var i = 0;
        while (i + 1 < tops.Length && tops[i + 1] <= y) i++;
        var next = i + 1 < tops.Length ? tops[i + 1] : tops[i] + FontSize_ * 1.4;
        return i + (next > tops[i] ? Math.Clamp((y - tops[i]) / (next - tops[i]), 0, 0.999) : 0);
    }

    void Follow()
    {
        var size = FontSize_;
        var readingY = Math.Max(size * 0.6, ActualHeight * ReadingLine);
        shift.Y = readingY - Y(p.Position);
        readingBand.Margin = new Thickness(0, readingY - size * 0.2, 0, 0);
        readingArrow.Margin = new Thickness(6, readingY + size * 0.25, 0, 0);
        var current = p.CurrentWordIndex;
        if (current == shownCurrent) return;
        shownCurrent = current;
        foreach (var (id, t) in wordViews)
        {
            var w = p.Words[id];
            if (w.IsCue) continue;
            t.Fg(id < current ? "Dim" : id == current ? "Accent" : "TextBright");
            t.Opacity = id < current ? 0.6 : 1;
        }
    }
}

/// <summary>Paste or type your own text to prompt, instead of the deck's notes.</summary>
sealed class OwnScriptEditor : DockPanel
{
    public OwnScriptEditor()
    {
        var p = PrompterRuntime.Shared;
        Margin = new Thickness(16);
        var box = new TextBox { Text = p.LastOwnScript, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 15 };
        box.SetResourceReference(BackgroundProperty, "Bar");
        var go = Ui.Pill("Prompt this text", () => p.UseOwnScript(box.Text), "AccentPill");
        go.ToolTip = "Use this text as the script (Ctrl+Enter)";
        go.IsEnabled = box.Text.Trim().Length > 0;
        box.TextChanged += (_, _) => go.IsEnabled = box.Text.Trim().Length > 0;
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { if (go.IsEnabled) p.UseOwnScript(box.Text); e.Handled = true; } };
        var title = Ui.Text("Paste or type what you want to say", 13, "TextSoft", FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 10);
        SetDock(title, Dock.Top);
        Children.Add(title);
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        var right = Ui.Row(8);
        if (p.HasDeckNotes) right.Children.Add(Ui.Pill("Use the deck's notes", p.UseDeckNotes));
        else if (p.EditingOwnScript && p.Words.Count > 0) right.Children.Add(Ui.Pill("Cancel", () => p.EditingOwnScript = false));
        right.Children.Add(go.With(g => g.Margin = new Thickness(8, 0, 0, 0)));
        SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(Ui.Text("A new line starts a new paragraph. **bold** and [pause] work as in notes.", 11, "Dim", wrap: true).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        SetDock(buttons, Dock.Bottom);
        Children.Add(buttons);
        Children.Add(box);
        Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
    }
}

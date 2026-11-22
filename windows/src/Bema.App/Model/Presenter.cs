using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bema.App.PowerPoint;
using Bema.App.Speech;
using Bema.Core;

namespace Bema.App.Model;

public enum Phase { Empty, Loading, Ready, Presenting, Failed }
public enum AudienceScreen { Normal, Black, White }

/// <summary>
/// The app's state: the deck, the slide, the show, the timer, the pointer tools. The Windows port of
/// the Mac app's PresenterModel; the rules it follows are in Bema.Core.
/// </summary>
public sealed class Presenter : Observable, IRemoteHost
{
    public static Presenter Shared { get; } = new();

    readonly PowerPointController powerPoint = new();
    readonly DispatcherTimer pollTimer;
    readonly DispatcherTimer effectsTimer;
    int failedPolls;
    readonly Dictionary<(int, int), BitmapSource> imageCache = [];

    Presenter()
    {
        pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        pollTimer.Tick += async (_, _) => await Refresh();
        effectsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        effectsTimer.Tick += (_, _) => TickEffects();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current?.Dispatcher.Invoke(DisplaysChanged);
    }

    // ---------------- State ----------------

    Phase phase = Phase.Empty;
    public Phase Phase
    {
        get => phase;
        private set
        {
            var old = phase;
            if (!Set(ref phase, value)) return;
            if (old != Phase.Presenting && value == Phase.Presenting) CoachRuntime.Shared.ShowStarted();
            if (old == Phase.Presenting && value != Phase.Presenting) CoachRuntime.Shared.ShowEnded();
            if ((old == Phase.Presenting) != (value == Phase.Presenting)) VoiceCommandsRuntime.Shared.ShowChanged();
            Raise(nameof(CanStart)); Raise(nameof(IsPresenting)); Raise(nameof(HasDeck)); Raise(nameof(TimerShown));
        }
    }

    string loadingMessage = "";
    public string LoadingMessage { get => loadingMessage; private set => Set(ref loadingMessage, value); }
    string failure = "";
    public string Failure { get => failure; private set => Set(ref failure, value); }

    public bool IsPresenting => Phase == Phase.Presenting;
    public bool HasDeck => Phase is Phase.Ready or Phase.Presenting;

    string? deck;
    /// <summary>The deck: a file path, or a OneDrive/SharePoint address.</summary>
    public string? Deck { get => deck; private set { if (Set(ref deck, value)) Raise(nameof(DeckName)); } }
    public string DeckName => Deck is null ? "Bema" : CloudDeck.IsCloud(Deck) ? Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(Deck).AbsolutePath)) : Path.GetFileNameWithoutExtension(Deck);

    IReadOnlyList<SlideInfo> deckSlides = [];
    Dictionary<int, string> drafts = [];
    public IReadOnlyList<SlideInfo> Slides { get; private set; } = [];

    void MergeSlides()
    {
        Slides = DraftNotes.Merge(deckSlides, drafts);
        Raise(nameof(Slides)); Raise(nameof(CurrentSlide)); Raise(nameof(NextSlide)); Raise(nameof(VisibleCount)); Raise(nameof(VisiblePosition));
    }

    public IReadOnlyDictionary<int, string> Drafts => drafts;

    public void SetDraft(int slide, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) drafts.Remove(slide); else drafts[slide] = text;
        drafts = DraftNotes.Pruned(drafts, deckSlides);
        if (Deck is not null) DraftNotes.Save(drafts, DraftsPath(Deck));
        MergeSlides();
        PrompterRuntime.Shared.Load(Slides);
    }

    int current = 1;
    public int Current
    {
        get => current;
        private set
        {
            var old = current;
            if (!Set(ref current, value)) return;
            ClearDrawings();
            PrompterRuntime.Shared.SlideChanged(value);
            CoachRuntime.Shared.SlideChanged(old, value);
            Raise(nameof(CurrentSlide)); Raise(nameof(NextSlide)); Raise(nameof(VisiblePosition));
        }
    }

    public SlideInfo? CurrentSlide => Slides.FirstOrDefault(s => s.Id == Current);
    public SlideInfo? NextSlide => Slides.FirstOrDefault(s => s.Id > Current && !s.Hidden);
    public int VisibleCount => Slides.Count(s => !s.Hidden);
    public int VisiblePosition => Slides.Count(s => !s.Hidden && s.Id <= Current);

    AudienceScreen audience = AudienceScreen.Normal;
    public AudienceScreen Audience { get => audience; private set => Set(ref audience, value); }
    bool showEnded;
    public bool ShowEnded { get => showEnded; private set => Set(ref showEnded, value); }
    bool powerPointReady;
    public bool PowerPointReady { get => powerPointReady; private set { if (Set(ref powerPointReady, value)) Raise(nameof(CanStart)); } }

    string? notice;
    public string? Notice { get => notice; set => Set(ref notice, value); }

    public bool CanStart => Phase == Phase.Ready && Deck is not null;

    // ---------------- Displays ----------------

    public bool AskForDisplay
    {
        get => Settings.Get("askForDisplay", true);
        set { Settings.Set("askForDisplay", value); Raise(); }
    }

    public string AudienceDisplayId
    {
        get => Settings.Get("audienceDisplay", "");
        set { Settings.Set("audienceDisplay", value); Raise(); Raise(nameof(AudienceDisplayName)); }
    }

    /// <summary>The window Bema's presenter view is in (set by MainWindow), to keep the slides off its display.</summary>
    public Func<Rect?>? PresenterWindowPixels { get; set; }

    public Display AudienceDisplayChoice()
    {
        var all = Display.All();
        var presenter = PresenterWindowPixels?.Invoke() is { } r ? Display.Containing(r)?.Id : null;
        var id = AudienceDisplay.Choose(AudienceDisplayId, all.Select(d => d.Id).ToList(), presenter);
        return all.FirstOrDefault(d => d.Id == id) ?? all[0];
    }

    public string AudienceDisplayName => AudienceDisplayChoice().Name;

    int displayCount = Display.All().Count;
    public int DisplayCount { get => displayCount; private set => Set(ref displayCount, value); }

    bool audienceFullScreen;
    public bool AudienceFullScreen { get => audienceFullScreen; private set => Set(ref audienceFullScreen, value); }

    Rect? showFrame;
    /// <summary>Where PowerPoint is drawing the slideshow, in screen pixels.</summary>
    public Rect? ShowFrame { get => showFrame; private set => Set(ref showFrame, value); }

    void DisplaysChanged()
    {
        DisplayCount = Display.All().Count;
        Raise(nameof(AudienceDisplayName));
        Notice = AudienceDisplay.NoticeAfterDisplaysChanged(Notice, DisplayCount, IsPresenting && !AudienceFullScreen);
    }

    bool displayChooserOpen;
    public bool DisplayChooserOpen { get => displayChooserOpen; set => Set(ref displayChooserOpen, value); }

    // ---------------- Recents ----------------

    public IReadOnlyList<string> Recents => Settings.Get<List<string>>("recents", []).Where(File.Exists).ToList();

    void AddRecent(string path)
    {
        if (CloudDeck.IsCloud(path)) return;   // a cloud deck can only be presented while PowerPoint has it open
        var list = Settings.Get<List<string>>("recents", []).Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, path);
        Settings.Set("recents", list.Take(8).ToList());
        Raise(nameof(Recents));
    }

    // ---------------- Loading ----------------

    public static bool IsDeck(string path) =>
        CloudDeck.IsCloud(path) || Path.GetExtension(path).ToLowerInvariant() is ".pptx" or ".ppsx" or ".pptm" or ".ppt";

    public async Task Load(string location)
    {
        if (Phase == Phase.Presenting) await EndShow();
        pollTimer.Stop();
        Deck = location;
        drafts = [];
        deckSlides = [];
        imageCache.Clear();
        previewFolder = null;
        PowerPointReady = false;
        ShowEnded = false;
        Audience = AudienceScreen.Normal;
        Notice = null;
        ResetTimer();
        AddRecent(location);
        MergeSlides();

        var cloud = CloudDeck.IsCloud(location);
        // .ppt (the old format) isn't a zip: read it through PowerPoint like a cloud deck.
        var throughApp = cloud || Path.GetExtension(location).Equals(".ppt", StringComparison.OrdinalIgnoreCase);
        if (!throughApp)
        {
            Phase = Phase.Loading; LoadingMessage = "Reading speaker notes…";
            try { deckSlides = await Task.Run(() => PptxReader.Read(location)); }
            catch (Exception e) { Fail(e.Message); return; }
            SlidesLoaded(location);
        }

        Phase = Phase.Loading; LoadingMessage = "Opening in PowerPoint…";
        try
        {
            await powerPoint.Open(location);
            PowerPointReady = true;
        }
        catch (Exception e)
        {
            if (throughApp) { Fail(e.Message); return; }
            Notice = $"{e.Message} You can still read your notes, but the slideshow can't start.";
            Phase = Phase.Ready;
            return;
        }

        if (throughApp)
        {
            LoadingMessage = "Reading speaker notes from PowerPoint…";
            try { deckSlides = await powerPoint.ReadSlides(); }
            catch (Exception e) { Fail(e.Message); return; }
            SlidesLoaded(location);
        }

        var folder = PreviewFolder(location);
        if (cloud || !File.Exists(Path.Combine(folder, "1.png")))
        {
            LoadingMessage = "Preparing slide previews…";
            try { await powerPoint.ExportSlides(folder, 1600); }
            catch (Exception e) { Notice = $"Slide previews aren't available: {e.Message}"; }
        }
        previewFolder = folder;
        imageCache.Clear();
        Raise(nameof(Slides));
        Phase = Phase.Ready;
        // If this deck's slideshow is already running (Bema was restarted), pick it back up.
        if (await SafeStatus() is ShowStatus.Running r)
        {
            if (r.Slide > 0) Current = r.Slide;
            Phase = Phase.Presenting;
            pollTimer.Start();
        }
        ShowPresenterWindow?.Invoke();
    }

    void Fail(string why) { Failure = why; Phase = Phase.Failed; }

    void SlidesLoaded(string location)
    {
        drafts = DraftNotes.Pruned(DraftNotes.Load(DraftsPath(location)), deckSlides);
        MergeSlides();
        current = Slides.FirstOrDefault(s => !s.Hidden)?.Id ?? 1;
        Raise(nameof(Current)); Raise(nameof(CurrentSlide)); Raise(nameof(NextSlide)); Raise(nameof(VisiblePosition));
        PrompterRuntime.Shared.Load(Slides);
        CoachRuntime.Shared.DeckLoaded(location);
    }

    static string Hash(string s, int n) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..n];

    static string DraftsPath(string location) => Path.Combine(Settings.DeckFolder("Draft Notes"), DraftNotes.FileName(CloudDeck.Key(location)));

    /// <summary>Previews are cached per path + modification time + size, so an edited deck is exported again.</summary>
    static string PreviewFolder(string location)
    {
        var stamp = CloudDeck.Key(location);
        if (!CloudDeck.IsCloud(location) && File.Exists(location))
        {
            var info = new FileInfo(location);
            stamp += $"|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        }
        else stamp += $"|{DateTime.UtcNow.Ticks}";
        return Path.Combine(Path.GetTempPath(), "Bema", "Previews", Hash(stamp, 24));
    }

    string? previewFolder;

    /// <summary>A slide's picture (made by PowerPoint) for the presenter's own screens, or null.</summary>
    public BitmapSource? Image(int slide, int width = 0)
    {
        if (previewFolder is null) return null;
        var key = (slide, width);
        if (imageCache.TryGetValue(key, out var cached)) return cached;
        var path = Path.Combine(previewFolder, $"{slide}.png");
        if (!File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            if (width > 0) img.DecodePixelWidth = width;
            img.EndInit();
            img.Freeze();
            if (imageCache.Count > 200) imageCache.Clear();
            return imageCache[key] = img;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Opens Bema's window (set by the app).</summary>
    public Action? ShowPresenterWindow { get; set; }

    public async Task<string?> DeckOpenInPowerPoint() => await powerPoint.ActivePresentation();

    public async Task PresentOpenDeck()
    {
        ShowPresenterWindow?.Invoke();
        var location = await powerPoint.ActivePresentation();
        if (location is null)
        {
            Notice = PowerPointController.IsRunning
                ? "The deck in front hasn't been saved yet. Save it, on this PC or to OneDrive, then try again."
                : "PowerPoint isn't running. Open a deck in it, or choose one here.";
            return;
        }
        if (string.Equals(CloudDeck.Key(location), Deck is null ? null : CloudDeck.Key(Deck), StringComparison.OrdinalIgnoreCase) && HasDeck) return;
        await Load(location);
    }

    public async Task CloseDeck()
    {
        if (Phase == Phase.Presenting) await EndShow();
        Phase = Phase.Empty;
        Deck = null;
        deckSlides = [];
        drafts = [];
        previewFolder = null;
        MergeSlides();
    }

    // ---------------- Slideshow ----------------

    /// <summary>Start from Bema's own button: asks where the slides go when there's a choice.</summary>
    public void RequestStart()
    {
        if (!CanStart) return;
        if (AudienceDisplay.ShouldAsk(AskForDisplay, Display.All().Count)) { ShowPresenterWindow?.Invoke(); DisplayChooserOpen = true; }
        else _ = StartShow();
    }

    public async Task StartShow()
    {
        if (!CanStart) return;
        DisplayChooserOpen = false;
        if (!PowerPointReady && Deck is not null)
        {
            try { await powerPoint.Open(Deck); PowerPointReady = true; Notice = null; }
            catch (Exception e) { Notice = e.Message; return; }
        }
        var screen = AudienceDisplayChoice();
        var presenterOn = PresenterWindowPixels?.Invoke() is { } r ? Display.Containing(r)?.Id : null;
        var count = Display.All().Count;
        var fullScreen = count > 1 && screen.Id != presenterOn;
        try
        {
            ShowEnded = false;
            Audience = AudienceScreen.Normal;
            var ok = await powerPoint.StartShow(screen.Pixels, Current, fullScreen);
            AudienceFullScreen = fullScreen && ok;
            Phase = Phase.Presenting;
            StartTimerForShow();
            if (AudienceDisplay.NoticeAfterStart(fullScreen, count) is { } displayNotice) Notice = displayNotice;
            else if (Notice is not null && AudienceDisplay.Notices.Contains(Notice)) Notice = null;
            failedPolls = 0;
            pollTimer.Start();
            // PowerPoint takes focus when the show starts; take it back so the keyboard and clicker reach Bema.
            await Task.Delay(700);
            ShowPresenterWindow?.Invoke();
        }
        catch (Exception e) { Notice = e.Message; }
    }

    public async Task EndShow()
    {
        pollTimer.Stop();
        try { await powerPoint.EndShow(); } catch (Exception) { }
        ShowStopped();
    }

    public void EndShowButton() => _ = EndShow();

    void ShowStopped()
    {
        pollTimer.Stop();
        failedPolls = 0;
        if (Phase == Phase.Presenting) Phase = Phase.Ready;
        Tool = PointerTool.None;
        Audience = AudienceScreen.Normal;
        ShowEnded = false;
        ShowFrame = null;
        StopTimerForEndedShow();
    }

    void PresentationClosed()
    {
        ShowStopped();
        PowerPointReady = false;
        Notice = "The slideshow ended because the presentation was closed in PowerPoint. The timer has stopped. Start slideshow opens it again.";
    }

    async Task<ShowStatus?> SafeStatus()
    {
        try { return await powerPoint.Status(); } catch (Exception) { return null; }
    }

    async Task Refresh()
    {
        if (Phase != Phase.Presenting) return;
        if (!PowerPointController.IsRunning) { PresentationClosed(); return; }
        var status = await SafeStatus();
        if (status is null)
        {
            if (++failedPolls >= 10)
            {
                ShowStopped();
                PowerPointReady = false;
                Notice = "Bema lost touch with PowerPoint's slideshow, so it ended the show and stopped the timer. Start slideshow to present again.";
            }
            return;
        }
        failedPolls = 0;
        switch (status)
        {
            case ShowStatus.Running r:
                if (r.Slide > 0 && r.Slide != Current) Current = r.Slide;
                Audience = r.Black ? AudienceScreen.Black : r.White ? AudienceScreen.White : AudienceScreen.Normal;
                ShowEnded = r.Done;
                if (r.Frame is { } frame)
                {
                    ShowFrame = frame;
                    if (Display.Containing(frame) is { } d)
                        AudienceFullScreen = Math.Abs(frame.Width - d.Pixels.Width) < 2 && Math.Abs(frame.Height - d.Pixels.Height) < 2;
                }
                break;
            case ShowStatus.NotRunning:
                ShowStopped();   // ended in PowerPoint itself (Esc there)
                break;
            case ShowStatus.PresentationClosed:
                PresentationClosed();
                break;
        }
    }

    // ---------------- Navigation ----------------

    public void Next()
    {
        if (IsPresenting) _ = Do(powerPoint.Next());
        else if (NextSlide is { } n) Current = n.Id;
    }

    public void Previous()
    {
        if (IsPresenting) _ = Do(powerPoint.Previous());
        else if (Slides.LastOrDefault(s => s.Id < Current && !s.Hidden) is { } p) Current = p.Id;
    }

    public void GoTo(int id)
    {
        if (Slides.All(s => s.Id != id)) return;
        Current = id;
        if (IsPresenting) _ = Do(powerPoint.GoTo(id));
    }

    public void First() { if (Slides.FirstOrDefault(s => !s.Hidden) is { } s) GoTo(s.Id); }
    public void Last() { if (Slides.LastOrDefault(s => !s.Hidden) is { } s) GoTo(s.Id); }

    async Task Do(Task t) { try { await t; } catch (Exception) { } await Refresh(); }

    public void ToggleBlack() => SetAudience(Audience == AudienceScreen.Black ? AudienceScreen.Normal : AudienceScreen.Black);
    public void ToggleWhite() => SetAudience(Audience == AudienceScreen.White ? AudienceScreen.Normal : AudienceScreen.White);

    void SetAudience(AudienceScreen state)
    {
        if (!IsPresenting) return;
        Audience = state;
        _ = Do(powerPoint.SetScreen(state switch { AudienceScreen.Black => ScreenState.Black, AudienceScreen.White => ScreenState.White, _ => ScreenState.Normal }));
    }

    /// <summary>Clickers send Page Up/Down, arrows or B. Returns true if the key was used.</summary>
    public bool HandleKey(System.Windows.Input.Key key)
    {
        if (!HasDeck) return false;
        switch (key)
        {
            case System.Windows.Input.Key.Right or System.Windows.Input.Key.Down or System.Windows.Input.Key.Space or System.Windows.Input.Key.PageDown or System.Windows.Input.Key.Enter:
                Next(); return true;
            case System.Windows.Input.Key.Left or System.Windows.Input.Key.Up or System.Windows.Input.Key.PageUp or System.Windows.Input.Key.Back:
                Previous(); return true;
            case System.Windows.Input.Key.Home: First(); return true;
            case System.Windows.Input.Key.End: Last(); return true;
            case System.Windows.Input.Key.Escape:
                if (Tool != PointerTool.None) { Tool = PointerTool.None; return true; }
                if (IsPresenting) { EndShowButton(); return true; }
                return false;
            case System.Windows.Input.Key.B or System.Windows.Input.Key.OemPeriod: ToggleBlack(); return true;
            case System.Windows.Input.Key.W or System.Windows.Input.Key.OemComma: ToggleWhite(); return true;
            case System.Windows.Input.Key.L: ToggleTool(PointerTool.Laser); return true;
            case System.Windows.Input.Key.S: ToggleTool(PointerTool.Spotlight); return true;
            case System.Windows.Input.Key.M: ToggleTool(PointerTool.Magnifier); return true;
            case System.Windows.Input.Key.T: ToggleTool(PointerTool.Stamp); return true;
            case System.Windows.Input.Key.P: ToggleTool(PointerTool.Pen); return true;
            case System.Windows.Input.Key.H: ToggleTool(PointerTool.Highlighter); return true;
            case System.Windows.Input.Key.D1: FireReaction(ReactionKind.Confetti); return true;
            case System.Windows.Input.Key.D2: FireReaction(ReactionKind.Applause); return true;
            case System.Windows.Input.Key.D3: FireReaction(ReactionKind.Hearts); return true;
            case System.Windows.Input.Key.E or System.Windows.Input.Key.C: ClearDrawings(); return true;
            default: return false;
        }
    }

    // ---------------- Pointer tools ----------------

    PointerTool tool = PointerTool.None;
    public PointerTool Tool
    {
        get => tool;
        set { if (Set(ref tool, value) && !value.FollowsHover()) PointerAt = null; DrawingChanged?.Invoke(); }
    }

    public void ToggleTool(PointerTool t) => Tool = Tool == t ? PointerTool.None : t;

    public LaserStyle LaserStyle
    {
        get => Settings.Get("laserStyle", LaserStyle.Dot);
        set { Settings.Set("laserStyle", value); Raise(); }
    }

    public StampKind StampKind
    {
        get => Settings.Get("stampKind", StampKind.Check);
        set { Settings.Set("stampKind", value); Raise(); }
    }

    /// <summary>Where the pointer is over the slide, 0…1; null when it's off the slide.</summary>
    public Point? PointerAt { get; private set; }
    public List<Stroke> Strokes { get; } = [];
    public Stroke? ActiveStroke { get; set; }
    public List<Stamp> Stamps { get; } = [];
    public List<Ripple> Ripples { get; } = [];
    public List<TrailPoint> LaserTrail { get; } = [];
    public Reaction? Reaction { get; private set; }

    /// <summary>Drawings, the pointer or an effect changed: the presenter's slide and the audience overlay redraw.</summary>
    public event Action? DrawingChanged;

    public void MovePointer(Point? p)
    {
        PointerAt = p;
        if (p is { } at && Tool == PointerTool.Laser && LaserStyle.UsesTrail())
        {
            LaserTrail.Add(new TrailPoint(at, DateTime.UtcNow));
            if (LaserTrail.Count > 40) LaserTrail.RemoveRange(0, LaserTrail.Count - 40);
            effectsTimer.Start();
        }
        DrawingChanged?.Invoke();
    }

    public void AddRipple(Point p) { Ripples.Add(new Ripple(p, DateTime.UtcNow)); effectsTimer.Start(); DrawingChanged?.Invoke(); }
    public void PlaceStamp(Point p) { Stamps.Add(new Stamp(StampKind, p)); DrawingChanged?.Invoke(); }

    public void FireReaction(ReactionKind kind)
    {
        Reaction = new Reaction(kind, DateTime.UtcNow, (ulong)Random.Shared.NextInt64(1, long.MaxValue));
        effectsTimer.Start();
        DrawingChanged?.Invoke();
    }

    public void CommitStroke()
    {
        if (ActiveStroke is { Points.Count: > 0 } s) Strokes.Add(s);
        ActiveStroke = null;
        DrawingChanged?.Invoke();
    }

    public void ClearDrawings()
    {
        Strokes.Clear(); Stamps.Clear(); ActiveStroke = null;
        DrawingChanged?.Invoke();
    }

    public bool EffectsAnimating => Ripples.Count > 0 || Reaction is not null || (Tool == PointerTool.Laser && LaserStyle.UsesTrail() && LaserTrail.Count > 0);

    void TickEffects()
    {
        var now = DateTime.UtcNow;
        Ripples.RemoveAll(r => (now - r.Start).TotalSeconds >= 0.7);
        LaserTrail.RemoveAll(t => (now - t.Time).TotalSeconds >= 0.6);
        if (Reaction is { } r && (now - r.Start).TotalSeconds > r.Kind.Duration()) Reaction = null;
        if (!EffectsAnimating) effectsTimer.Stop();
        DrawingChanged?.Invoke();
    }

    // ---------------- Timer ----------------

    public int TargetMinutes
    {
        get => Settings.Get("targetMinutes", 20);
        set { Settings.Set("targetMinutes", value); Raise(); Raise(nameof(TargetSeconds)); }
    }

    public double TargetSeconds => TargetMinutes * 60;

    DateTime? timerStartedAt;
    public DateTime? TimerStartedAt { get => timerStartedAt; private set { if (Set(ref timerStartedAt, value)) { Raise(nameof(TimerRunning)); Raise(nameof(TimerShown)); } } }
    double timerAccumulated;
    public bool TimerRunning => TimerStartedAt is not null;
    /// <summary>The timer holds an ended show's time: shown dimmed; the next show starts from 0:00.</summary>
    public bool TimerFromEndedShow { get; private set; }

    public double Elapsed(DateTime now) => timerAccumulated + (TimerStartedAt is { } s ? (now - s).TotalSeconds : 0);
    public TimerLook TimerLook(DateTime now) => Timing.Look(TimerRunning, Elapsed(now), TargetSeconds);
    public bool TimerShown => IsPresenting || TimerRunning || timerAccumulated > 0;

    public void ToggleTimer()
    {
        TimerFromEndedShow = false;
        if (TimerStartedAt is { } s) { timerAccumulated += (DateTime.UtcNow - s).TotalSeconds; TimerStartedAt = null; }
        else TimerStartedAt = DateTime.UtcNow;
        Raise(nameof(TimerShown));
    }

    public void ResetTimer()
    {
        TimerFromEndedShow = false;
        timerAccumulated = 0;
        TimerStartedAt = IsPresenting ? DateTime.UtcNow : null;
        Raise(nameof(TimerShown));
    }

    public void StopTimerForEndedShow()
    {
        if (TimerStartedAt is not null) ToggleTimer();
        TimerFromEndedShow = timerAccumulated > 0;
    }

    public void StartTimerForShow()
    {
        if (TimerFromEndedShow) ResetTimer();
        if (TimerStartedAt is null) ToggleTimer();
    }

    /// <summary>Bema's window closed (it keeps running in the tray): outside a show the timer is cleared.</summary>
    public void PresenterWindowClosed()
    {
        if (!IsPresenting) ResetTimer();
    }

    // ---------------- Phone remote ----------------

    public IDictionary<string, object> RemoteState() => new Dictionary<string, object>
    {
        ["deck"] = DeckName,
        ["presenting"] = IsPresenting,
        ["ready"] = CanStart,
        ["slide"] = Current,
        ["position"] = VisiblePosition,
        ["count"] = VisibleCount,
        ["title"] = CurrentSlide?.Title ?? "",
        ["notes"] = NoteCues.Plain(CurrentSlide?.Notes ?? ""),
        ["nextTitle"] = NextSlide?.Title ?? "",
        ["elapsed"] = (int)Elapsed(DateTime.UtcNow),
        ["target"] = (int)TargetSeconds,
        ["timerRunning"] = TimerRunning,
        ["black"] = Audience == AudienceScreen.Black,
        ["prompter"] = PrompterRuntime.Shared.Running,
    };

    public bool Command(string command)
    {
        switch (command)
        {
            case "next": Next(); break;
            case "prev": Previous(); break;
            case "black": ToggleBlack(); break;
            case "first": First(); break;
            case "last": Last(); break;
            case "start": _ = StartShow(); break;
            case "prompter": PrompterRuntime.Shared.Toggle(); break;
            case "timer": ToggleTimer(); break;
            case "confetti": FireReaction(ReactionKind.Confetti); break;
            case "applause": FireReaction(ReactionKind.Applause); break;
            default: return false;
        }
        return true;
    }

    public byte[]? SlideJpeg(int slide)
    {
        if (Image(slide, 480) is not { } img) return null;
        var enc = new JpegBitmapEncoder { QualityLevel = 75 };
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    int IRemoteHost.CurrentSlide => Current;

    // ---------------- Notes writing ----------------

    public async Task<string> SlideText(int id)
    {
        try { return await powerPoint.SlideText(id); } catch (Exception) { return Slides.FirstOrDefault(s => s.Id == id)?.Title ?? ""; }
    }

    public Task<IReadOnlySet<int>> SaveDraftsToDeck() => powerPoint.WriteNotes(drafts);

    public async Task<int> SaveDrafts()
    {
        var written = await SaveDraftsToDeck();
        foreach (var id in written)
        {
            var i = deckSlides.ToList().FindIndex(s => s.Id == id);
            if (i >= 0 && drafts.TryGetValue(id, out var text))
            {
                var list = deckSlides.ToList();
                list[i] = list[i].WithNotes(text, false);
                deckSlides = list;
            }
            drafts.Remove(id);
        }
        if (Deck is not null) DraftNotes.Save(drafts, DraftsPath(Deck));
        MergeSlides();
        PrompterRuntime.Shared.Load(Slides);
        return written.Count;
    }

    public void Shutdown()
    {
        pollTimer.Stop();
        powerPoint.Dispose();
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

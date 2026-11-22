using System.Globalization;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Bema.App.Model;
using Bema.Core;

namespace Bema.App.Speech;

public enum PrompterMode { Voice, Auto, Manual }
public enum PrompterLanguage { Automatic, English, Hebrew }

/// <summary>The prompter: the script, where you are, and how it moves (voice, auto or manual).</summary>
public sealed class PrompterRuntime : Observable
{
    public static PrompterRuntime Shared { get; } = new();

    ScriptAligner aligner = new([]);
    readonly VoiceTarget voiceTarget = new();
    readonly List<(DateTime Time, int Index)> advances = [];
    DateTime lastVoiceActivity = DateTime.MinValue, lastGlide = DateTime.UtcNow;
    readonly DispatcherTimer ticker = new(DispatcherPriority.Render);
    int? slideMovedByPrompter;
    IReadOnlyList<SlideInfo> deckSlides = [];

    PrompterRuntime()
    {
        ticker.Tick += (_, _) => Tick();
    }

    public IReadOnlyList<ScriptParagraph> Paragraphs { get; private set; } = [];
    public IReadOnlyList<ScriptWord> Words { get; private set; } = [];

    double position;
    /// <summary>Index of the next word to read; fractional while it glides.</summary>
    public double Position { get => position; set { if (Set(ref position, value)) PositionChanged(); } }
    public int CurrentWordIndex => Math.Min((int)Position, Math.Max(Words.Count - 1, 0));

    public PrompterMode Mode
    {
        get => Settings.Get("prompterMode", PrompterMode.Voice);
        set { Settings.Set("prompterMode", value); Raise(); if (Running) { Stop(); Start(); } }
    }

    public double WordsPerMinute
    {
        get => Settings.Get("prompterWPM", 140.0);
        set { Settings.Set("prompterWPM", value); Raise(); }
    }

    public bool FollowSlides
    {
        get => Settings.Get("prompterFollowSlides", true);
        set { Settings.Set("prompterFollowSlides", value); Raise(); }
    }

    bool mirrored;
    public bool Mirrored { get => mirrored; set => Set(ref mirrored, value); }
    PrompterLanguage language;
    public PrompterLanguage Language { get => language; set { if (Set(ref language, value) && Running && Mode == PrompterMode.Voice) { Stop(); Start(); } } }
    bool running;
    public bool Running { get => running; private set => Set(ref running, value); }
    string? status;
    public string? Status { get => status; private set => Set(ref status, value); }

    string? ownScript;
    /// <summary>Text pasted into the prompter, prompted instead of the deck's notes.</summary>
    public string? OwnScript { get => ownScript; private set => Set(ref ownScript, value); }
    bool editingOwnScript;
    public bool EditingOwnScript { get => editingOwnScript; set => Set(ref editingOwnScript, value); }

    public string LastOwnScript => OwnScript ?? Settings.Get("prompterOwnScript", "");
    public bool HasDeckNotes => deckSlides.Any(s => s.Notes.Trim().Length > 0);
    public bool ShowsOwnScriptEditor(bool detached) => detached && (EditingOwnScript || (Words.Count == 0 && OwnScript is null));

    public event Action? ScriptChanged;

    public void Load(IReadOnlyList<SlideInfo> slides)
    {
        deckSlides = slides;
        if (OwnScript is null) Build(slides);
    }

    public void UseOwnScript(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) { UseDeckNotes(); return; }
        OwnScript = t;
        Settings.Set("prompterOwnScript", t);
        EditingOwnScript = false;
        Build(ScriptBuilder.SlidesForOwnScript(t));
    }

    public void UseDeckNotes()
    {
        OwnScript = null;
        EditingOwnScript = false;
        Build(deckSlides);
    }

    void Build(IReadOnlyList<SlideInfo> slides)
    {
        Stop();
        (Paragraphs, Words) = ScriptBuilder.Build(slides);
        aligner = new ScriptAligner(Words.Select(w => w.Norm).ToList());
        SpeechHub.Shared.Vocabulary = ScriptText.Vocabulary(slides);
        Raise(nameof(Paragraphs)); Raise(nameof(Words)); Raise(nameof(HasDeckNotes));
        ScriptChanged?.Invoke();
        SetPosition(0);
    }

    public void Toggle() { if (Running) Stop(); else Start(); }

    public void Start()
    {
        if (Words.Count == 0)
        {
            Status = OwnScript is null ? "This deck has no speaker notes to prompt from." : "There's no text to prompt.";
            return;
        }
        Running = true;
        Status = null;
        switch (Mode)
        {
            case PrompterMode.Voice: StartListening(); break;
            case PrompterMode.Auto: lastGlide = DateTime.UtcNow; ticker.Interval = TimeSpan.FromSeconds(1.0 / 30); ticker.Start(); break;
            default: Status = "Drag the script or click a word to move"; break;
        }
    }

    public void Stop()
    {
        Running = false;
        ticker.Stop();
        SpeechHub.Shared.Release(Listener.Prompter);
        if (Mode == PrompterMode.Voice) Status = null;
    }

    void StartListening()
    {
        voiceTarget.Reset(CurrentWordIndex);
        SpeechHub.Shared.OnWords(Listener.Prompter, heard =>
        {
            if (!Running || Mode != PrompterMode.Voice) return;
            lastVoiceActivity = DateTime.UtcNow;
            if (aligner.Align(heard, voiceTarget.Index) is { } next && voiceTarget.Propose(Math.Min(next, Words.Count), heard.Count))
            {
                advances.Add((DateTime.UtcNow, voiceTarget.Index));
                if (advances.Count > 30) advances.RemoveRange(0, advances.Count - 30);
            }
        });
        lastGlide = DateTime.UtcNow;
        ticker.Interval = TimeSpan.FromSeconds(1.0 / 60);
        ticker.Start();
        if (SpeechHub.Shared.Request(Listener.Prompter, SpeechCulture()) is { } error)
        {
            Status = error;
            Running = false;
            ticker.Stop();
        }
    }

    void Tick()
    {
        if (!Running) return;
        var now = DateTime.UtcNow;
        if (Mode == PrompterMode.Auto)
        {
            var next = Position + WordsPerMinute / 60 / 30;
            if (next >= Words.Count) { Stop(); return; }
            Position = next;
            return;
        }
        if (Mode != PrompterMode.Voice) return;
        // Glide toward where speech says you are, so the script flows instead of hopping.
        var dt = Math.Min((now - lastGlide).TotalSeconds, 0.1);
        lastGlide = now;
        if (SpeechHub.Shared.Level > 0.06) lastVoiceActivity = now;
        double target = voiceTarget.Index;
        var goal = target + Lead(now);
        if (goal < Position && target > Position - 3) return;   // never drift back while following your voice
        var diff = goal - Position;
        if (Math.Abs(diff) <= 0.002) return;
        var eased = diff * (1 - Math.Exp(-dt / 0.15));
        var limit = (Math.Abs(diff) > 8 ? 60.0 : 16.0) * dt;
        Position += Math.Clamp(eased, -limit, limit);
    }

    /// <summary>Recognition reports words late: while you speak, run a little ahead at your recent pace.</summary>
    double Lead(DateTime now)
    {
        var recent = advances.Where(a => (now - a.Time).TotalSeconds < 4).ToList();
        if ((now - lastVoiceActivity).TotalSeconds >= 0.5 || recent.Count < 2) return 0;
        var (first, last) = (recent[0], recent[^1]);
        var span = (last.Time - first.Time).TotalSeconds;
        if (span <= 0.8) return 0;
        var wps = (last.Index - first.Index) / span;
        var since = (now - last.Time).TotalSeconds;
        return Math.Max(0, Math.Min(1.5, wps * (0.3 + Math.Min(since, 0.3))));
    }

    /// <summary>The language to listen in: chosen, or read from the script (Hebrew letters, else this PC's language).</summary>
    public CultureInfo SpeechCulture()
    {
        switch (Language)
        {
            case PrompterLanguage.English: return new CultureInfo("en-US");
            case PrompterLanguage.Hebrew: return new CultureInfo("he-IL");
        }
        var text = string.Join(' ', Words.Take(400).Select(w => w.Text));
        var hebrew = text.Count(c => c is >= '֐' and <= '׿');
        var latin = text.Count(c => c is >= 'A' and <= 'z');
        return hebrew > latin ? new CultureInfo("he-IL") : CultureInfo.CurrentUICulture;
    }

    void PositionChanged()
    {
        if (!Running || !FollowSlides || OwnScript is not null || Words.Count == 0) return;
        var presenter = Presenter.Shared;
        if (!presenter.IsPresenting) return;
        var slide = Words[CurrentWordIndex].Slide;
        if (slide != presenter.Current) { slideMovedByPrompter = slide; presenter.GoTo(slide); }
    }

    /// <summary>The slideshow moved: jump the script to that slide's text.</summary>
    public void SlideChanged(int slide)
    {
        if (slideMovedByPrompter == slide) { slideMovedByPrompter = null; return; }
        if (OwnScript is not null || Words.Count == 0 || Words[CurrentWordIndex].Slide == slide) return;
        var first = Words.FirstOrDefault(w => w.Slide == slide) ?? Words.FirstOrDefault(w => w.Slide > slide);
        if (first is not null) SetPosition(first.Id);
    }

    public void Jump(int wordIndex) => SetPosition(Math.Clamp(wordIndex, 0, Math.Max(0, Words.Count - 1)));

    public void SetPosition(double p)
    {
        Position = Math.Max(0, p);
        voiceTarget.Reset((int)Math.Round(Position));
        advances.Clear();
    }
}

/// <summary>Spoken commands while presenting: "next slide", "go to slide 5", "black screen".</summary>
public sealed class VoiceCommandsRuntime : Observable
{
    public static VoiceCommandsRuntime Shared { get; } = new();
    int handled;
    DateTime lastFired = DateTime.MinValue;

    public bool Enabled
    {
        get => Settings.Get("voiceCommands", false);
        set { Settings.Set("voiceCommands", value); Raise(); Update(); }
    }

    string? lastCommand;
    public string? LastCommand { get => lastCommand; private set => Set(ref lastCommand, value); }

    public void ShowChanged() { handled = 0; Update(); }

    void Update()
    {
        var hub = SpeechHub.Shared;
        if (Enabled && Presenter.Shared.IsPresenting)
        {
            if (hub.IsUsed(Listener.Commands)) return;
            hub.OnWords(Listener.Commands, w => Heard(w, false));
            hub.OnUtterance(Listener.Commands, w => { Heard(w, true); handled = 0; });
            if (hub.Request(Listener.Commands, PrompterRuntime.Shared.SpeechCulture()) is { } error) Presenter.Shared.Notice = error;
        }
        else if (hub.IsUsed(Listener.Commands)) hub.Release(Listener.Commands);
    }

    void Heard(IReadOnlyList<string> words, bool final)
    {
        var norm = words.Select(ScriptText.Normalize).Where(w => w.Length > 0).ToList();
        if (norm.Count < handled) handled = 0;
        var fresh = norm.Skip(Math.Max(0, handled - 1)).ToList();
        if ((DateTime.UtcNow - lastFired).TotalSeconds <= 1.2 || VoiceCommandParser.Parse(fresh, final) is not { } command) return;
        handled = norm.Count;
        lastFired = DateTime.UtcNow;
        var model = Presenter.Shared;
        var visible = model.Slides.Where(s => !s.Hidden).ToList();
        switch (command)
        {
            case VoiceCommand.Next: model.Next(); LastCommand = "Next slide"; break;
            case VoiceCommand.Previous: model.Previous(); LastCommand = "Previous slide"; break;
            case VoiceCommand.Black: model.ToggleBlack(); LastCommand = "Black screen"; break;
            default:
                if (VoiceCommandParser.Target(command, visible) is { } target)
                {
                    model.GoTo(target.Id);
                    LastCommand = command is VoiceCommand.Title ? $"Go to “{target.Title}”" : $"Slide {visible.IndexOf(target) + 1}";
                }
                break;
        }
    }
}

/// <summary>Live captions of what you say, for the audience (never saved).</summary>
public sealed class CaptionsRuntime : Observable
{
    public static CaptionsRuntime Shared { get; } = new();

    bool enabled;
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (!Set(ref enabled, value)) return;
            var hub = SpeechHub.Shared;
            if (value)
            {
                if (hub.Request(Listener.Captions, PrompterRuntime.Shared.SpeechCulture()) is { } error) { Presenter.Shared.Notice = error; enabled = false; Raise(); }
            }
            else hub.Release(Listener.Captions);
        }
    }

    /// <summary>The last few words, as captions show them.</summary>
    public string Text
    {
        get
        {
            var hub = SpeechHub.Shared;
            var words = string.Join(' ', hub.RecentUtterances.TakeLast(1).Append(string.Join(' ', hub.Stats.Partial))).Trim();
            return words.Length > 120 ? "…" + words[^120..] : words;
        }
    }
}

/// <summary>Pace against budgets, the time-up cue, speech stats and rehearsal reports.</summary>
public sealed class CoachRuntime : Observable
{
    public static CoachRuntime Shared { get; } = new();

    readonly Dictionary<int, double> timings = [];
    readonly Dictionary<int, int> wordsBySlide = [];
    readonly HashSet<int> cuesFired = [];
    int lastWordTotal;
    bool recording;
    string? deckKey;

    CoachRuntime()
    {
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => EverySecond();
        tick.Start();
    }

    public bool Listen
    {
        get => Settings.Get("coachListen", false);
        set { Settings.Set("coachListen", value); Raise(); UpdateListening(); }
    }

    public bool RehearsalMode
    {
        get => Settings.Get("rehearsalMode", false);
        set { Settings.Set("rehearsalMode", value); Raise(); }
    }

    public bool TimeCue
    {
        get => Settings.Get("timeCue", true);
        set { Settings.Set("timeCue", value); Raise(); }
    }

    public bool CueSound
    {
        get => Settings.Get("cueSound", false);
        set { Settings.Set("cueSound", value); Raise(); }
    }

    DateTime? cueFlashUntil;
    public DateTime? CueFlashUntil { get => cueFlashUntil; private set => Set(ref cueFlashUntil, value); }
    DateTime? slideStart;
    TalkReport? lastReport;
    public TalkReport? LastReport { get => lastReport; private set => Set(ref lastReport, value); }
    public event Action<TalkReport>? ReportReady;

    Dictionary<string, Dictionary<int, double>> SavedBudgets
    {
        get => Settings.Get<Dictionary<string, Dictionary<int, double>>>("slideBudgets", []);
        set => Settings.Set("slideBudgets", value);
    }

    public Dictionary<int, double> Budgets()
    {
        var p = Presenter.Shared;
        var saved = deckKey is not null && SavedBudgets.TryGetValue(deckKey, out var b) ? b : null;
        return Coach.Budgets(p.Slides, p.TargetSeconds, saved);
    }

    public bool UsingRehearsalBudgets => deckKey is not null && SavedBudgets.ContainsKey(deckKey);

    public double SlideElapsed(DateTime now) => slideStart is { } s ? (now - s).TotalSeconds : 0;

    public double? PaceDelta(DateTime now)
    {
        var p = Presenter.Shared;
        if (!p.IsPresenting || !p.TimerRunning) return null;
        return Coach.PaceDelta(p.Elapsed(now), p.Current, SlideElapsed(now), p.Slides, Budgets());
    }

    public void SaveRehearsalBudgets(TalkReport report)
    {
        if (deckKey is null) return;
        var all = SavedBudgets;
        all[deckKey] = Coach.BudgetsFrom(report);
        SavedBudgets = all;
        Raise(nameof(UsingRehearsalBudgets));
    }

    public void ResetBudgets()
    {
        if (deckKey is null) return;
        var all = SavedBudgets;
        all.Remove(deckKey);
        SavedBudgets = all;
        Raise(nameof(UsingRehearsalBudgets));
    }

    public void DeckLoaded(string location)
    {
        deckKey = CloudDeck.Key(location);
        LastReport = LoadReport(deckKey);
    }

    public void ShowStarted()
    {
        timings.Clear(); wordsBySlide.Clear(); cuesFired.Clear();
        lastWordTotal = 0;
        SpeechHub.Shared.Stats.Reset();
        slideStart = DateTime.UtcNow;
        recording = true;
        UpdateListening();
    }

    public void SlideChanged(int old, int now)
    {
        if (!recording) return;
        var t = DateTime.UtcNow;
        if (slideStart is { } s) timings[old] = timings.GetValueOrDefault(old) + (t - s).TotalSeconds;
        slideStart = t;
    }

    public void ShowEnded()
    {
        if (!recording) return;
        recording = false;
        var p = Presenter.Shared;
        if (slideStart is { } s) timings[p.Current] = timings.GetValueOrDefault(p.Current) + (DateTime.UtcNow - s).TotalSeconds;
        slideStart = null;
        CountWords();
        UpdateListening();
        var b = Budgets();
        var stats = SpeechHub.Shared.Stats;
        var slides = p.Slides.Where(x => !x.Hidden).Select(x => new SlideTiming(x.Id, x.Title.Replace("\n", " "),
            timings.GetValueOrDefault(x.Id), b.GetValueOrDefault(x.Id), wordsBySlide.GetValueOrDefault(x.Id))).ToList();
        var total = slides.Sum(x => x.Seconds);
        if (total < 5) return;
        var report = new TalkReport(DateTimeOffset.Now, p.DeckName, total, p.TargetSeconds, slides, stats.TotalWords,
                                    new Dictionary<string, int>(stats.Fillers), RehearsalMode);
        LastReport = report;
        if (deckKey is not null) SaveReport(report, deckKey);
        if (RehearsalMode) ReportReady?.Invoke(report);
    }

    void UpdateListening()
    {
        var hub = SpeechHub.Shared;
        if (Listen && recording)
        {
            if (hub.IsUsed(Listener.Coach)) return;
            if (hub.Request(Listener.Coach, PrompterRuntime.Shared.SpeechCulture()) is { } error) Presenter.Shared.Notice = error;
        }
        else if (hub.IsUsed(Listener.Coach)) hub.Release(Listener.Coach);
    }

    void CountWords()
    {
        var stats = SpeechHub.Shared.Stats;
        var total = stats.TotalWords + stats.Partial.Count;
        var delta = total - lastWordTotal;
        var current = Presenter.Shared.Current;
        if (delta > 0) wordsBySlide[current] = wordsBySlide.GetValueOrDefault(current) + delta;
        lastWordTotal = Math.Max(lastWordTotal, total);
    }

    void EverySecond()
    {
        if (recording) CountWords();
        var p = Presenter.Shared;
        if (!TimeCue || !p.IsPresenting || !p.TimerRunning || p.TargetSeconds <= 0) return;
        var remaining = p.TargetSeconds - p.Elapsed(DateTime.UtcNow);
        if (Coach.CuesDue(remaining, cuesFired).Any()) Cue();
    }

    /// <summary>A pulse on the timer and (if turned on) a soft sound.</summary>
    public void Cue()
    {
        CueFlashUntil = DateTime.UtcNow.AddSeconds(4);
        if (CueSound) SystemSounds.Asterisk.Play();
    }

    static string ReportPath(string key) => Path.Combine(Settings.DeckFolder("Reports"), Coach.ReportFileName(key));

    static void SaveReport(TalkReport r, string key)
    {
        try
        {
            Directory.CreateDirectory(Settings.DeckFolder("Reports"));
            File.WriteAllText(ReportPath(key), JsonSerializer.Serialize(r, Coach.Json));
        }
        catch (Exception) { }
    }

    static TalkReport? LoadReport(string key)
    {
        try { return JsonSerializer.Deserialize<TalkReport>(File.ReadAllText(ReportPath(key)), Coach.Json); }
        catch (Exception) { return null; }
    }
}

using System.Globalization;
using System.Speech.Recognition;
using System.Windows;
using Bema.App.Model;
using Bema.Core;

namespace Bema.App.Speech;

public enum Listener { Prompter, Coach, Captions, Commands }

/// <summary>
/// One microphone stream into Windows' on-device speech recognizer (SAPI), shared by everything that
/// listens: the prompter, the coach, captions and voice commands. The microphone opens only while one
/// of them is on and closes when none needs it. Nothing is recorded or sent anywhere: Windows'
/// desktop recognizer runs on this PC. Audio is never written to disk.
/// </summary>
public sealed class SpeechHub : Observable
{
    public static SpeechHub Shared { get; } = new();

    readonly HashSet<Listener> listeners = [];
    readonly Dictionary<Listener, Action<IReadOnlyList<string>>> onWords = [];
    readonly Dictionary<Listener, Action<IReadOnlyList<string>>> onUtterance = [];
    SpeechRecognitionEngine? engine;
    CultureInfo? culture;

    public SpeechStats Stats { get; } = new();

    /// <summary>Words from the deck that help recognition (names, product terms).</summary>
    public IReadOnlyList<string> Vocabulary { get; set; } = [];

    bool listening;
    public bool Listening { get => listening; private set => Set(ref listening, value); }
    string? status;
    public string? Status { get => status; private set => Set(ref status, value); }
    double level;
    /// <summary>Microphone level, 0…1.</summary>
    public double Level { get => level; private set => Set(ref level, value); }
    IReadOnlyList<string> recent = [];
    /// <summary>Finished utterances, newest last (a few, for captions). Never saved.</summary>
    public IReadOnlyList<string> RecentUtterances { get => recent; private set => Set(ref recent, value); }

    public void OnWords(Listener l, Action<IReadOnlyList<string>> a) => onWords[l] = a;
    public void OnUtterance(Listener l, Action<IReadOnlyList<string>> a) => onUtterance[l] = a;
    public bool IsUsed(Listener l) => listeners.Contains(l);

    /// <summary>The languages this PC can recognize on its own (Windows speech languages installed).</summary>
    public static IReadOnlyList<CultureInfo> InstalledLanguages()
    {
        try { return SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture).ToList(); }
        catch (Exception) { return []; }
    }

    /// <summary>Starts listening for <paramref name="who"/>. Returns an error message, or null.</summary>
    public string? Request(Listener who, CultureInfo want)
    {
        listeners.Add(who);
        if (Listening && culture?.Name == want.Name) return null;
        if (Listening) StopEngine();
        if (StartEngine(want) is { } error) { listeners.Remove(who); return error; }
        return null;
    }

    public void Release(Listener who)
    {
        listeners.Remove(who);
        onWords.Remove(who);
        onUtterance.Remove(who);
        if (listeners.Count == 0) StopEngine();
    }

    string? StartEngine(CultureInfo want)
    {
        var installed = InstalledLanguages();
        var match = installed.FirstOrDefault(c => c.Name == want.Name) ?? installed.FirstOrDefault(c => c.TwoLetterISOLanguageName == want.TwoLetterISOLanguageName);
        if (match is null)
        {
            return $"Windows can't recognize {want.DisplayName} speech on this PC. Add it under Settings → Time & language → Speech (Bema listens only on this PC, never online).";
        }
        try
        {
            var e = new SpeechRecognitionEngine(match);
            e.LoadGrammar(new DictationGrammar { Name = "dictation" });
            if (Vocabulary.Count > 0)
            {
                // The deck's unusual words, so names and product terms are heard right.
                var g = new Grammar(new GrammarBuilder(new Choices(Vocabulary.Take(100).ToArray())) { Culture = match }) { Name = "deck", Weight = 0.6f };
                try { e.LoadGrammar(g); } catch (Exception) { }
            }
            e.SetInputToDefaultAudioDevice();   // only now, with a listener on, is the microphone opened
            e.SpeechHypothesized += (_, a) => OnUi(() => Update(Words(a.Result.Text)));
            e.SpeechRecognized += (_, a) => OnUi(() => Finish(Words(a.Result.Text)));
            e.SpeechRecognitionRejected += (_, _) => OnUi(() => Finish(Stats.Partial));
            e.AudioLevelUpdated += (_, a) => OnUi(() => Level = Math.Min(1, a.AudioLevel / 60.0));
            e.RecognizeAsync(RecognizeMode.Multiple);
            engine = e;
            culture = match;
            Listening = true;
            Status = $"Listening in {match.DisplayName}, on this PC";
            return null;
        }
        catch (InvalidOperationException)
        {
            return "Couldn't open the microphone. Check Settings → Privacy & security → Microphone, and that a microphone is connected.";
        }
        catch (Exception e)
        {
            return $"Couldn't start listening: {e.Message}";
        }
    }

    void StopEngine()
    {
        Finish(Stats.Partial);
        Listening = false;
        Status = null;
        Level = 0;
        if (engine is { } e)
        {
            engine = null;
            try { e.RecognizeAsyncCancel(); e.SetInputToNull(); } catch (Exception) { }
            e.Dispose();
        }
    }

    static IReadOnlyList<string> Words(string text) => ScriptText.Tokens(text);

    static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    void Update(IReadOnlyList<string> words)
    {
        Stats.Update(words, DateTimeOffset.UtcNow);
        Raise(nameof(Stats));
        foreach (var h in onWords.Values.ToList()) h(words);
    }

    void Finish(IReadOnlyList<string> words)
    {
        Stats.Finish(words);
        if (words.Count == 0) return;
        RecentUtterances = [.. RecentUtterances.TakeLast(3), string.Join(' ', words)];
        foreach (var h in onUtterance.Values.ToList()) h(words);
        Raise(nameof(Stats));
    }
}

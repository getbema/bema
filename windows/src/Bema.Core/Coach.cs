using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bema.Core;

public sealed record SlideTiming(int Id, string Title, double Seconds, double Budget, int Words);

/// <summary>What happened in one run of a talk: time per slide against its budget, words and filler words.</summary>
public sealed record TalkReport(DateTimeOffset Date, string Deck, double Total, double Target, IReadOnlyList<SlideTiming> Slides,
                                int Words, IReadOnlyDictionary<string, int> Fillers, bool Rehearsal)
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public int? AverageWpm
    {
        get
        {
            var spoken = Slides.Where(s => s.Words > 0).Sum(s => s.Seconds);
            return spoken > 30 && Words > 0 ? (int)(Words / spoken * 60) : null;
        }
    }
}

/// <summary>Pace and per-slide time budgets (pure). Same rules as the Mac app's CoachModel.</summary>
public static class Coach
{
    /// <summary>
    /// Seconds planned for each visible slide: saved rehearsal timings if any, otherwise the planned
    /// talk length shared out by how much is in each slide's notes (at least 15 words' worth).
    /// </summary>
    public static Dictionary<int, double> Budgets(IEnumerable<SlideInfo> slides, double targetSeconds, IReadOnlyDictionary<int, double>? rehearsal)
    {
        var visible = slides.Where(s => !s.Hidden).ToList();
        var out_ = new Dictionary<int, double>();
        if (visible.Count == 0) return out_;
        if (rehearsal is { Count: > 0 })
        {
            foreach (var s in visible) out_[s.Id] = rehearsal.TryGetValue(s.Id, out var v) ? v : 30;
            return out_;
        }
        if (targetSeconds <= 0) return out_;
        var weights = visible.Select(s => Math.Max(15.0, ScriptText.Tokens(s.Notes).Length)).ToList();
        var sum = weights.Sum();
        for (var i = 0; i < visible.Count; i++) out_[visible[i].Id] = targetSeconds * weights[i] / sum;
        return out_;
    }

    public static double PlannedStart(int slide, IEnumerable<SlideInfo> slides, IReadOnlyDictionary<int, double> budgets) =>
        slides.Where(s => !s.Hidden && s.Id < slide).Sum(s => budgets.GetValueOrDefault(s.Id));

    /// <summary>Seconds behind (positive) or ahead (negative) of plan; null without a budget for this slide.</summary>
    public static double? PaceDelta(double elapsed, int current, double slideElapsed, IEnumerable<SlideInfo> slides, IReadOnlyDictionary<int, double> budgets)
    {
        if (!budgets.TryGetValue(current, out var budget)) return null;
        var expected = PlannedStart(current, slides, budgets) + Math.Min(slideElapsed, budget);
        return elapsed - expected;
    }

    /// <summary>"On pace", "1:20 behind", "0:45 ahead".</summary>
    public static string PaceText(double delta) =>
        delta > 30 ? $"{FormatDuration(delta)} behind" : delta < -30 ? $"{FormatDuration(delta)} ahead" : "On pace";

    /// <summary>Rehearsal timings saved as budgets: at least 5 seconds, rounded.</summary>
    public static Dictionary<int, double> BudgetsFrom(TalkReport report) =>
        report.Slides.ToDictionary(s => s.Id, s => Math.Max(5, Math.Round(s.Seconds, MidpointRounding.AwayFromZero)));

    /// <summary>The time cue fires once at a minute left and once at time up (only if just crossed).</summary>
    public static IEnumerable<int> CuesDue(double remaining, ISet<int> fired)
    {
        foreach (var mark in new[] { 60, 0 })
        {
            if (remaining <= mark && fired.Add(mark) && remaining > mark - 5) yield return mark;
        }
    }

    public static string FormatDuration(double t)
    {
        var s = (int)Math.Round(Math.Abs(t), MidpointRounding.AwayFromZero);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>The timer's own format: "05:07", or "1:37:20" past an hour.</summary>
    public static string FormatTimer(double t)
    {
        var s = (int)t;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    /// <summary>The file name a deck's last report is kept under (a hash of the deck's key, never its name).</summary>
    public static string ReportFileName(string deckKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deckKey)))[..20] + ".json";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

/// <summary>How the timer is drawn: a paused timer is dimmed and never says "over time".</summary>
public enum TimerLook { Running, Over, Paused }

public static class Timing
{
    public static TimerLook Look(bool running, double elapsed, double target) =>
        !running ? TimerLook.Paused : target > 0 && elapsed > target ? TimerLook.Over : TimerLook.Running;
}

/// <summary>Which display the audience sees, and the notices about it. Same rules as the Mac app.</summary>
public static class AudienceDisplay
{
    /// <summary>The saved display if it's connected; otherwise one other than Bema's; otherwise the first.</summary>
    public static string? Choose(string saved, IReadOnlyList<string> displays, string? presenter)
    {
        if (displays.Contains(saved)) return saved;
        return displays.FirstOrDefault(d => d != presenter) ?? displays.FirstOrDefault();
    }

    public static bool ShouldAsk(bool enabled, int displayCount) => enabled && displayCount > 1;

    public const string OneDisplayNotice = "Only one display is connected, so the slideshow is in a window for rehearsal.";
    public const string SameDisplayNotice = "The slides are on the same display as Bema, so the slideshow is in a window. Choose another display in “Slides on” and restart the show for full screen.";
    public const string DisplayAddedNotice = "Another display is connected. End and restart the show to put the slides on it full screen.";
    public static readonly IReadOnlySet<string> Notices = new HashSet<string> { OneDisplayNotice, SameDisplayNotice, DisplayAddedNotice };

    public static string? NoticeAfterStart(bool fullScreen, int displayCount) =>
        fullScreen ? null : displayCount > 1 ? SameDisplayNotice : OneDisplayNotice;

    public static string? NoticeAfterDisplaysChanged(string? current, int displayCount, bool rehearsingInWindow)
    {
        if (current is null || !Notices.Contains(current)) return current;
        if (displayCount <= 1) return rehearsingInWindow ? OneDisplayNotice : null;
        return rehearsingInWindow ? DisplayAddedNotice : null;
    }
}

public static class QuickPrompt
{
    public const string Hint = "Remembered on this PC only. **bold** and [pause] work as in notes.";

    /// <summary>What the quick prompt card says under its box.</summary>
    public static string Summary(string text, double wordsPerMinute = 140)
    {
        var words = ScriptText.Tokens(text).Length;
        if (words == 0) return Hint;
        var minutes = Math.Max(1, (int)Math.Round(words / wordsPerMinute, MidpointRounding.AwayFromZero));
        return $"{words} {(words == 1 ? "word" : "words")} · about {minutes} min at a speaking pace";
    }
}

namespace Bema.Core;

/// <summary>A spoken command while presenting.</summary>
public abstract record VoiceCommand
{
    public sealed record Next : VoiceCommand;
    public sealed record Previous : VoiceCommand;
    public sealed record Black : VoiceCommand;
    /// <summary>Slide number among visible slides; -1 = the last.</summary>
    public sealed record Slide(int Number) : VoiceCommand;
    public sealed record Title(string Query) : VoiceCommand;
}

/// <summary>
/// "next slide", "previous slide", "go to slide 5", "go to pricing" (a slide title), "black screen",
/// and Hebrew equivalents. Same phrases as the Mac app.
/// </summary>
public static class VoiceCommandParser
{
    public static readonly IReadOnlyDictionary<string, int> Numbers = new Dictionary<string, int>
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["first"] = 1, ["second"] = 2, ["third"] = 3, ["last"] = -1,
        ["אחת"] = 1, ["שתיים"] = 2, ["שלוש"] = 3, ["ארבע"] = 4, ["חמש"] = 5, ["שש"] = 6, ["שבע"] = 7, ["שמונה"] = 8, ["תשע"] = 9, ["עשר"] = 10,
    };

    static readonly HashSet<string> TitleFiller = ["the", "slide", "about", "ל", "אל"];

    /// <summary>Finds a command in the words heard (normalized). <paramref name="final"/> allows "go to &lt;title&gt;".</summary>
    public static VoiceCommand? Parse(IReadOnlyList<string> words, bool final)
    {
        var text = " " + string.Join(' ', words) + " ";
        bool Has(string phrase) => text.Contains($" {phrase} ", StringComparison.Ordinal);
        if (Has("next slide") || Has("שקופית הבאה") || Has("השקופית הבאה")) return new VoiceCommand.Next();
        if (Has("previous slide") || Has("last slide please") || Has("go back") || Has("שקופית קודמת") || Has("השקופית הקודמת")) return new VoiceCommand.Previous();
        if (Has("black screen") || Has("מסך שחור")) return new VoiceCommand.Black();
        for (var i = 0; i + 1 < words.Count; i++)
        {
            if (words[i] is not ("slide" or "שקופית")) continue;
            var n = words[i + 1];
            if (NumberOf(n) is { } v) return new VoiceCommand.Slide(v);
            if (n == "number" && i + 2 < words.Count && NumberOf(words[i + 2]) is { } v2) return new VoiceCommand.Slide(v2);
        }
        if (final)
        {
            for (var i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (!(w == "goto" || (w == "go" && i + 1 < words.Count && words[i + 1] == "to") || w == "עבור")) continue;
                var start = w == "go" ? i + 2 : i + 1;
                var rest = words.Skip(start).Take(4).Where(x => !TitleFiller.Contains(x)).ToList();
                if (rest.Count > 0) return new VoiceCommand.Title(string.Join(' ', rest));
            }
        }
        return null;
    }

    static int? NumberOf(string s) => int.TryParse(s, out var v) ? v : Numbers.TryGetValue(s, out var n) ? n : null;

    public static SlideInfo? BestTitleMatch(string query, IEnumerable<SlideInfo> slides)
    {
        var q = ScriptText.Tokens(query).Select(ScriptText.Normalize).Where(w => w.Length >= 3).ToList();
        if (q.Count == 0) return null;
        (int Score, SlideInfo Slide)? best = null;
        foreach (var s in slides)
        {
            var t = ScriptText.Tokens(s.Title).Select(ScriptText.Normalize).ToList();
            var score = q.Count(qw => t.Any(tw => ScriptAligner.Similar(qw, tw) || tw.StartsWith(qw, StringComparison.Ordinal)));
            if (score > 0 && score > (best?.Score ?? 0)) best = (score, s);
        }
        return best?.Slide;
    }

    /// <summary>The slide a command means, among the visible slides; null if there's none.</summary>
    public static SlideInfo? Target(VoiceCommand command, IReadOnlyList<SlideInfo> visible) => command switch
    {
        VoiceCommand.Slide { Number: -1 } => visible.Count > 0 ? visible[^1] : null,
        VoiceCommand.Slide s when s.Number >= 1 && s.Number <= visible.Count => visible[s.Number - 1],
        VoiceCommand.Title t => BestTitleMatch(t.Query, visible),
        _ => null,
    };
}

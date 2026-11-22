namespace Bema.Core;

/// <summary>
/// Word and filler-word counts for the speech coach, and the speaking pace. Fed by whatever recognizer
/// the platform has; the counting rules are the Mac app's.
/// </summary>
public sealed class SpeechStats
{
    public static readonly IReadOnlySet<string> FillerWords = new HashSet<string>
    {
        "um", "umm", "uh", "uhh", "er", "erm", "ah", "hmm", "like", "basically", "actually", "literally", "so", "right",
        "אממ", "אה", "אהה", "כאילו", "בעצם", "יעני", "נו",
    };

    public static readonly IReadOnlyList<string[]> FillerPhrases = [["you", "know"], ["sort", "of"], ["kind", "of"], ["i", "mean"], ["זאת", "אומרת"]];

    readonly List<(DateTimeOffset Time, int Words)> samples = [];
    readonly Dictionary<string, int> fillers = [];

    public int TotalWords { get; private set; }
    public IReadOnlyDictionary<string, int> Fillers => fillers;
    public int FillerCount => fillers.Values.Sum();

    /// <summary>The words of the utterance being recognized right now (revised as recognition improves).</summary>
    public IReadOnlyList<string> Partial { get; private set; } = [];

    public void Reset() { TotalWords = 0; fillers.Clear(); samples.Clear(); Partial = []; }

    public void Update(IReadOnlyList<string> words, DateTimeOffset now)
    {
        Partial = words;
        samples.Add((now, TotalWords + words.Count));
        if (samples.Count > 600) samples.RemoveRange(0, samples.Count - 600);
    }

    /// <summary>Counts a finished utterance into the totals.</summary>
    public void Finish(IReadOnlyList<string> words)
    {
        Partial = [];
        if (words.Count == 0) return;
        TotalWords += words.Count;
        var norm = words.Select(ScriptText.Normalize).ToArray();
        foreach (var w in norm.Where(FillerWords.Contains)) fillers[w] = fillers.GetValueOrDefault(w) + 1;
        foreach (var phrase in FillerPhrases)
        {
            for (var i = 0; i + phrase.Length <= norm.Length; i++)
            {
                if (norm.AsSpan(i, phrase.Length).SequenceEqual(phrase))
                {
                    var key = string.Join(' ', phrase);
                    fillers[key] = fillers.GetValueOrDefault(key) + 1;
                }
            }
        }
    }

    /// <summary>Words per minute over the last 30 seconds; null with under 8 seconds of speech.</summary>
    public int? WordsPerMinute()
    {
        if (samples.Count == 0) return null;
        var last = samples[^1];
        var windowStart = last.Time.AddSeconds(-30);
        var first = samples.FirstOrDefault(s => s.Time >= windowStart);
        var span = (last.Time - first.Time).TotalSeconds;
        if (span <= 8) return null;
        return (int)((last.Words - first.Words) / span * 60);
    }
}

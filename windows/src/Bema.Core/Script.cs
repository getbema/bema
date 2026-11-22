using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bema.Core;

/// <summary>One word of the prompter's script.</summary>
/// <param name="Id">Index in the whole script.</param>
/// <param name="Text">As written.</param>
/// <param name="Norm">Normalized for matching speech; empty for cues, which are never matched.</param>
public sealed record ScriptWord(int Id, string Text, string Norm, int Slide, bool IsCue = false, bool Bold = false, bool IsPlaceholder = false);

public sealed record ScriptParagraph(int Id, int Slide, string SlideTitle, bool IsSlideStart, bool Rtl, IReadOnlyList<ScriptWord> Words);

public static class ScriptText
{
    /// <summary>Lowercase, no accents or Hebrew vowel points, letters and digits only.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static string[] Tokens(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when the first letter is Hebrew or Arabic, so the text aligns right.</summary>
    public static bool IsRightToLeft(string s)
    {
        foreach (var rune in s.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            var v = rune.Value;
            return v is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF;
        }
        return false;
    }

    /// <summary>
    /// Unusual words in the deck (product names, acronyms, CamelCase, names mid-sentence, long words),
    /// most frequent first: hints for the speech recognizer.
    /// </summary>
    public static IReadOnlyList<string> Vocabulary(IEnumerable<SlideInfo> slides, int limit = 100)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slide in slides)
        {
            foreach (var text in new[] { slide.Title, slide.Notes })
            {
                var tokens = Tokens(text);
                for (var i = 0; i < tokens.Length; i++)
                {
                    var w = tokens[i].Trim().TrimStart(NotAlnum(tokens[i])).TrimEnd(NotAlnum(tokens[i]));
                    if (w.Length < 2) continue;
                    var letters = w.Where(char.IsLetter).ToArray();
                    var upperInside = w.Skip(1).Any(char.IsUpper);
                    var allCaps = letters.Length >= 2 && letters.All(char.IsUpper);
                    var mixedDigits = w.Any(char.IsDigit) && w.Any(char.IsLetter);
                    var previousEndsSentence = i == 0 || ".!?:".Contains(tokens[i - 1][^1]);
                    var capitalizedMidSentence = char.IsUpper(w[0]) && !previousEndsSentence;
                    if (upperInside || allCaps || mixedDigits || capitalizedMidSentence || w.Length >= 11)
                        counts[w] = counts.GetValueOrDefault(w) + 1;
                }
            }
        }
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(limit).Select(kv => kv.Key).ToList();
    }

    static char[] NotAlnum(string token) => token.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray();
}

/// <summary>
/// Finds where in the script the speaker is, from the last few words recognized. The last few words
/// heard are aligned against the script ending at each nearby word, scoring matches and allowing
/// skipped script words, extra spoken words (ad-libs, fillers), and words recognized split or joined.
/// </summary>
public sealed class ScriptAligner(IReadOnlyList<string> words)
{
    public IReadOnlyList<string> Words { get; } = words;

    /// <summary>The index of the next word to read, or null if the speech doesn't match nearby text.</summary>
    public int? Align(IReadOnlyList<string> heard, int position)
    {
        var tail = heard.Select(ScriptText.Normalize).Where(s => s.Length > 0).TakeLast(8).ToArray();
        if (tail.Length == 0 || Words.Count == 0) return null;
        var last = tail[^1];
        var previous = tail.Length > 1 ? tail[^2] : null;
        var lo = Math.Max(0, position - 10);
        var hi = Math.Min(Words.Count - 1, position + 80);
        if (lo > hi) return null;
        var heardRev = tail.Reverse().ToArray();

        (double Rank, int End)? best = null;
        for (var end = lo; end <= hi; end++)
        {
            if (!EndMatches(last, previous, end)) continue;
            var segStart = Math.Max(0, end - (tail.Length + 8) + 1);
            var scriptRev = new string[end - segStart + 1];
            for (var k = 0; k < scriptRev.Length; k++) scriptRev[k] = Words[end - k];
            var score = AlignmentScore(heardRev, scriptRev);
            // Two solid matches, or one distinctive word just ahead of where we are.
            if (score < 3.5 && !(score >= 2 && last.Length >= 5 && end >= position - 1 && end <= position + 4)) continue;
            double distance = end >= position ? end - position : (position - end) * 3;
            var rank = score * 5 - distance * 0.15;
            if (best is null || rank > best.Value.Rank) best = (rank, end);
        }
        return best is { } b ? b.End + 1 : null;
    }

    bool EndMatches(string last, string? previous, int end)
    {
        var w = Words[end];
        if (w.Length == 0) return false;
        if (Similar(last, w)) return true;
        if (previous is not null && Similar(previous + last, w)) return true;           // "apple" "script" -> "applescript"
        if (end > 0 && Words[end - 1].Length > 0 && Similar(last, Words[end - 1] + w)) return true; // "powerpoint" -> "power" "point"
        return false;
    }

    /// <summary>
    /// Best score for aligning heard words with script words, both newest first and anchored at the newest.
    /// Match +2, split/joined match +2.5, skipped script word -0.4, extra heard word -0.7, mismatch -1.
    /// </summary>
    public static double AlignmentScore(IReadOnlyList<string> heard, IReadOnlyList<string> script)
    {
        int n = heard.Count, m = script.Count;
        var none = double.NegativeInfinity;
        var dp = new double[n + 1, m + 1];
        for (var i = 0; i <= n; i++) for (var j = 0; j <= m; j++) dp[i, j] = none;
        dp[0, 0] = 0;
        var best = 0.0;
        for (var i = 0; i <= n; i++)
        {
            for (var j = 0; j <= m; j++)
            {
                if (i == 0 && j == 0) continue;
                var v = none;
                if (i > 0 && j > 0 && dp[i - 1, j - 1] > none)
                    v = Math.Max(v, dp[i - 1, j - 1] + (Similar(heard[i - 1], script[j - 1]) && script[j - 1].Length > 0 ? 2 : -1));
                if (i > 1 && j > 0 && dp[i - 2, j - 1] > none && script[j - 1].Length > 0 && Similar(heard[i - 1] + heard[i - 2], script[j - 1]))
                    v = Math.Max(v, dp[i - 2, j - 1] + 2.5);
                if (i > 0 && j > 1 && dp[i - 1, j - 2] > none && script[j - 1].Length > 0 && Similar(heard[i - 1], script[j - 1] + script[j - 2]))
                    v = Math.Max(v, dp[i - 1, j - 2] + 2.5);
                if (j > 0 && dp[i, j - 1] > none) v = Math.Max(v, dp[i, j - 1] - (script[j - 1].Length == 0 ? 0 : 0.4));
                if (i > 0 && dp[i - 1, j] > none) v = Math.Max(v, dp[i - 1, j] - 0.7);
                dp[i, j] = v;
                if (v > best) best = v;
            }
        }
        return best;
    }

    public static bool Similar(string a, string b)
    {
        if (a == b) return true;
        int la = a.Length, lb = b.Length;
        if (Math.Min(la, lb) < 4) return false;
        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)) return Math.Abs(la - lb) <= 2;
        var limit = Math.Min(la, lb) >= 8 ? 2 : 1;
        return Math.Abs(la - lb) <= limit && Levenshtein(a, b, limit) <= limit;
    }

    static int Levenshtein(string a, string b, int limit)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > limit) return limit + 1;
            prev = cur;
        }
        return prev[b.Length];
    }
}

/// <summary>
/// Turns the jittery stream of "where the speaker is" guesses into a steady target. Recognition keeps
/// revising its last words, so raw matches wobble back and forth.
/// </summary>
public sealed class VoiceTarget(int index = 0)
{
    int? pending;
    int pendingEvidence = -1;

    public int Index { get; private set; } = index;

    /// <summary>
    /// Offers a new match. <paramref name="evidence"/> identifies what was heard (e.g. the number of
    /// words so far); a big jump is only confirmed by a match made after hearing something new.
    /// Returns true if the target moved.
    /// </summary>
    public bool Propose(int next, int? evidence = null)
    {
        var ev = evidence ?? Random.Shared.Next();
        var delta = next - Index;
        if (delta == 0) { pending = null; return false; }
        if (delta is < 0 and >= -3) return false;                       // recognition revising a word or two
        if (delta is > 0 and <= 25) { Index = next; pending = null; return true; }
        if (pending is { } p && Math.Abs(p - next) <= 2)
        {
            if (ev == pendingEvidence) return false;                     // same words heard again
            Index = next; pending = null; return true;
        }
        pending = next;
        pendingEvidence = ev;
        return false;
    }

    public void Reset(int i) { Index = i; pending = null; }
}

/// <summary>Builds the prompter's script from slides (the pure part of the Mac app's PrompterModel.build).</summary>
public static class ScriptBuilder
{
    /// <summary>Shown, as a cue, in place of a slide's script when the slide has no speaker notes.</summary>
    public const string NoNotesCue = "No notes for this slide";

    /// <summary>Your own text is one "slide" numbered 0: no slide headings, nothing to follow in a slideshow.</summary>
    public static IReadOnlyList<SlideInfo> SlidesForOwnScript(string text) => [new SlideInfo(0, "", text, false)];

    public static (IReadOnlyList<ScriptParagraph> Paragraphs, IReadOnlyList<ScriptWord> Words) Build(IEnumerable<SlideInfo> slides)
    {
        var paras = new List<ScriptParagraph>();
        var all = new List<ScriptWord>();
        var pid = 0;
        var shown = slides.Where(s => !s.Hidden).ToList();
        var deckHasNotes = shown.Any(s => s.Notes.Trim().Length > 0);
        foreach (var slide in shown)
        {
            var title = slide.Title.Replace("\n", " ");
            var lines = slide.Notes.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0 && deckHasNotes)
            {
                var w = new ScriptWord(all.Count, NoNotesCue, "", slide.Id, IsCue: true, IsPlaceholder: true);
                all.Add(w);
                paras.Add(new ScriptParagraph(pid++, slide.Id, title, true, false, [w]));
                continue;
            }
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                // Keep multi-word cues like [look up] as one token.
                var joined = NoteCues.CuePattern.Replace(line, m => "[" + m.Groups[1].Value.Replace(" ", "_") + "]");
                var boldOn = false;
                var ws = new List<ScriptWord>();
                foreach (var raw in ScriptText.Tokens(joined))
                {
                    var token = raw;
                    if (token.StartsWith('[') && token.EndsWith(']') && token.Length > 2)
                    {
                        var label = token[1..^1].Replace("_", " ").ToUpperInvariant();
                        var cue = new ScriptWord(all.Count, label, "", slide.Id, IsCue: true);
                        all.Add(cue); ws.Add(cue);
                        continue;
                    }
                    var bold = boldOn;
                    if (token.StartsWith("**", StringComparison.Ordinal)) { token = token[2..]; bold = true; boldOn = true; }
                    if (token.EndsWith("**", StringComparison.Ordinal) || (token.Length > 0 && token[..^1].EndsWith("**", StringComparison.Ordinal)))
                    {
                        token = token.Replace("**", "");
                        bold = true;
                        boldOn = false;
                    }
                    var word = new ScriptWord(all.Count, token, ScriptText.Normalize(token), slide.Id, Bold: bold);
                    all.Add(word); ws.Add(word);
                }
                paras.Add(new ScriptParagraph(pid++, slide.Id, title, i == 0, ScriptText.IsRightToLeft(line), ws));
            }
        }
        return (paras, all);
    }
}

/// <summary>Speaker-note markup: **bold**, *italic*, and cues in brackets like [pause], [smile], [look up].</summary>
public static partial class NoteCues
{
    [GeneratedRegex(@"\[([^\[\]\n]{1,24})\]")]
    private static partial Regex CueRegex();

    public static Regex CuePattern => CueRegex();

    /// <summary>Notes with markup removed (for one-line previews).</summary>
    public static string Plain(string notes) => CuePattern.Replace(notes, "").Replace("**", "").Replace("  ", " ");

    /// <summary>A run of notes text for display: plain text, bold, italic or a cue chip.</summary>
    public sealed record Run(string Text, bool Bold = false, bool Italic = false, bool Cue = false);

    /// <summary>Splits notes into display runs: cues as chips, **bold** and *italic* applied, whitespace kept.</summary>
    public static IReadOnlyList<Run> Runs(string notes)
    {
        var runs = new List<Run>();
        var at = 0;
        foreach (Match m in CuePattern.Matches(notes))
        {
            runs.AddRange(Inline(notes[at..m.Index]));
            runs.Add(new Run(m.Groups[1].Value.ToUpperInvariant(), Cue: true));
            at = m.Index + m.Length;
        }
        runs.AddRange(Inline(notes[at..]));
        return runs;
    }

    static IEnumerable<Run> Inline(string s)
    {
        // **bold** first, then *italic* inside the rest.
        var parts = Regex.Split(s, @"(\*\*[^*]+\*\*|\*[^*\s][^*]*\*)");
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            if (p.Length > 4 && p.StartsWith("**") && p.EndsWith("**")) yield return new Run(p[2..^2], Bold: true);
            else if (p.Length > 2 && p.StartsWith('*') && p.EndsWith('*')) yield return new Run(p[1..^1], Italic: true);
            else yield return new Run(p);
        }
    }
}

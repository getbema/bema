using Bema.Core;

namespace Bema.Core.Tests;

// Ported from the Mac app's ScriptAlignerTests, so both apps follow speech the same way.
public class ScriptAlignerTests
{
    static ScriptAligner Aligner(string script) => new(ScriptText.Tokens(script).Select(ScriptText.Normalize).ToList());

    const string Script = "PowerPoint still runs the slideshow, so animations and video keep working. Bema drives it in the background and keeps your notes in a window you control.";

    [Fact] public void FollowsExactSpeech() =>
        Assert.Equal(9, Aligner(Script).Align(["PowerPoint", "still", "runs", "the", "slideshow", "so", "animations", "and", "video"], 0));

    [Fact] public void ToleratesMisheardAndSkippedWords() =>
        Assert.Equal(11, Aligner(Script).Align(["so", "animation", "and", "the", "keep", "working"], 5));

    [Fact] public void IgnoresUnrelatedSpeech() =>
        Assert.Null(Aligner(Script).Align(["good", "morning", "everyone", "thanks", "for", "coming"], 0));

    [Fact] public void DoesNotJumpFarAheadOnACommonWord() => Assert.Null(Aligner(Script).Align(["the"], 12));

    [Fact] public void RecoversAfterSpeakerSkipsAhead() =>
        Assert.Equal(24, Aligner(Script).Align(["keeps", "your", "notes", "in", "a", "window"], 3));

    [Fact] public void Hebrew() =>
        Assert.Equal(7, Aligner("המצגת שלנו מראה איך הצוות בנה כלי חדש למציגים בכנסים").Align(["איך", "הצוות", "בנה", "כלי"], 0));

    [Fact]
    public void NormalizeStripsPunctuationAndCase()
    {
        Assert.Equal("slideshow", ScriptText.Normalize("Slideshow,"));
        Assert.Equal("bemas", ScriptText.Normalize("“Bema’s”"));
        Assert.Equal("שלום", ScriptText.Normalize("שָׁלוֹם"));
    }

    [Fact]
    public void SplitWordMatchesJoinedScriptWord()
    {
        var a = Aligner("Bema drives PowerPoint through AppleScript so nothing changes");
        Assert.Equal(5, a.Align(["drives", "PowerPoint", "through", "Apple", "script"], 2));
        Assert.Equal(4, a.Align(["drives", "power", "point", "through"], 1));
    }

    [Fact] public void JumpsForwardPastSkippedWords() =>
        Assert.Equal(14, Aligner("PowerPoint is old but it is still the industry standard for decks and we love it").Align(["PowerPoint", "is", "old", "decks", "and", "we"], 3));

    [Fact] public void AdLibsDoNotBreakFollowing() =>
        Assert.Equal(6, Aligner("Our numbers grew forty percent this quarter across every region").Align(["numbers", "grew", "um", "honestly", "forty", "percent", "this"], 2));

    [Fact]
    public void VocabularyPicksUnusualWords()
    {
        var vocab = ScriptText.Vocabulary([new SlideInfo(1, "How it works", "Bema drives PowerPoint through AppleScript. The CRM and Q1 numbers come from Salesforce.", false)]);
        foreach (var w in new[] { "PowerPoint", "AppleScript", "CRM", "Q1", "Salesforce" }) Assert.Contains(w, vocab);
        Assert.DoesNotContain("The", vocab);
    }

    [Fact]
    public void RightToLeftIsReadFromTheFirstLetter()
    {
        Assert.True(ScriptText.IsRightToLeft("“שלום” world"));
        Assert.False(ScriptText.IsRightToLeft("12 hello שלום"));
    }
}

public class VoiceTargetTests
{
    [Fact]
    public void IgnoresSmallBackwardRevisions()
    {
        var t = new VoiceTarget(10);
        Assert.False(t.Propose(9));
        Assert.False(t.Propose(8));
        Assert.Equal(10, t.Index);
    }

    [Fact]
    public void MovesAheadWhileReading()
    {
        var t = new VoiceTarget(10);
        Assert.True(t.Propose(12));
        Assert.True(t.Propose(13));
        Assert.Equal(13, t.Index);
    }

    [Fact]
    public void BigJumpsNeedConfirmation()
    {
        var t = new VoiceTarget(10);
        Assert.False(t.Propose(60));
        Assert.True(t.Propose(61));
        Assert.Equal(61, t.Index);
        Assert.False(t.Propose(20));
        Assert.False(t.Propose(45));
        Assert.Equal(61, t.Index);
    }

    [Fact]
    public void RepeatedPartialDoesNotConfirmAJump()
    {
        var t = new VoiceTarget(10);
        Assert.False(t.Propose(60, 5));
        Assert.False(t.Propose(60, 5));
        Assert.True(t.Propose(61, 6));
        Assert.Equal(61, t.Index);
    }
}

public class ScriptBuilderTests
{
    [Fact]
    public void ASlideWithoutNotesSaysSo()
    {
        var (paras, words) = ScriptBuilder.Build([
            new SlideInfo(1, "Intro", "Hello everyone", false),
            new SlideInfo(2, "Gap", "", false),
            new SlideInfo(3, "Next", "Moving on", false),
        ]);
        var gap = Assert.Single(paras, p => p.Slide == 2);
        var cue = Assert.Single(gap.Words);
        Assert.Equal(ScriptBuilder.NoNotesCue, cue.Text);
        Assert.True(cue.IsPlaceholder);
        Assert.Equal("", cue.Norm);
        Assert.Equal(5, words.Count);
    }

    [Fact]
    public void CuesAndBoldAreReadFromNotes()
    {
        var (_, words) = ScriptBuilder.Build([new SlideInfo(1, "T", "Say this [look up] **very important** now", false)]);
        Assert.Equal(["Say", "this", "LOOK UP", "very", "important", "now"], words.Select(w => w.Text));
        Assert.True(words[2].IsCue);
        Assert.True(words[3].Bold && words[4].Bold);
        Assert.False(words[5].Bold);
    }

    [Fact]
    public void HiddenSlidesAndDecksWithoutNotesAddNoPlaceholders()
    {
        var (paras, _) = ScriptBuilder.Build([new SlideInfo(1, "A", "", false), new SlideInfo(2, "B", "", true)]);
        Assert.Empty(paras);
    }

    [Fact]
    public void OwnTextIsOneSlideNumberedZero()
    {
        var (paras, words) = ScriptBuilder.Build(ScriptBuilder.SlidesForOwnScript("Line one\nLine two"));
        Assert.Equal(2, paras.Count);
        Assert.All(words, w => Assert.Equal(0, w.Slide));
    }

    [Fact]
    public void NoteRunsMarkCuesBoldAndItalic()
    {
        var runs = NoteCues.Runs("Open **strong** and *soft* [pause] end");
        Assert.Contains(runs, r => r.Bold && r.Text == "strong");
        Assert.Contains(runs, r => r.Italic && r.Text == "soft");
        Assert.Contains(runs, r => r.Cue && r.Text == "PAUSE");
        Assert.Equal("Open strong and *soft* end", NoteCues.Plain("Open **strong** and *soft* [pause] end").Replace("  ", " "));
    }
}

public class PptxReaderTests
{
    static string SamplePath([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "Samples", "Sample.pptx"));

    [Fact]
    public void ReadsTheSampleDeck()
    {
        var slides = PptxReader.Read(SamplePath());
        Assert.NotEmpty(slides);
        Assert.Equal(Enumerable.Range(1, slides.Count), slides.Select(s => s.Id));
        Assert.All(slides, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        Assert.Contains(slides, s => s.Notes.Length > 0);
    }

    [Fact]
    public void RefusesAFileThatIsntAPresentation() =>
        Assert.Throws<PptxException>(() => PptxReader.Read(new MemoryStream("not a zip"u8.ToArray())));

    [Fact]
    public void ResolvesRelativeTargets()
    {
        Assert.Equal("ppt/notesSlides/notesSlide1.xml", PptxReader.Resolve("ppt/slides", "../notesSlides/notesSlide1.xml"));
        Assert.Equal("ppt/slides/slide1.xml", PptxReader.Resolve("ppt", "slides/slide1.xml"));
    }
}

using System.Text;
using Bema.Core;

namespace Bema.Core.Tests;

public class VoiceCommandTests
{
    static VoiceCommand? P(string s, bool final = false) => VoiceCommandParser.Parse(s.Split(' '), final);

    [Fact]
    public void ParsesTheMacAppsPhrases()
    {
        Assert.IsType<VoiceCommand.Next>(P("ok next slide please"));
        Assert.IsType<VoiceCommand.Previous>(P("lets go back"));
        Assert.IsType<VoiceCommand.Black>(P("black screen"));
        Assert.Equal(new VoiceCommand.Slide(5), P("slide five"));
        Assert.Equal(new VoiceCommand.Slide(12), P("slide number 12"));
        Assert.Equal(new VoiceCommand.Slide(-1), P("slide last"));
        Assert.IsType<VoiceCommand.Next>(P("השקופית הבאה"));
        Assert.Null(P("go to pricing"));
        Assert.Equal(new VoiceCommand.Title("pricing"), P("go to the pricing", final: true));
    }

    [Fact]
    public void TitleMatchingFindsTheClosestSlide()
    {
        SlideInfo[] slides = [new(1, "Welcome", "", false), new(2, "Pricing plans", "", false), new(3, "Roadmap", "", false)];
        Assert.Equal(2, VoiceCommandParser.BestTitleMatch("pricing", slides)?.Id);
        Assert.Null(VoiceCommandParser.BestTitleMatch("zz", slides));
        Assert.Equal(3, VoiceCommandParser.Target(new VoiceCommand.Slide(-1), slides)?.Id);
        Assert.Null(VoiceCommandParser.Target(new VoiceCommand.Slide(9), slides));
    }
}

public class CoachTests
{
    static readonly SlideInfo[] Slides =
    [
        new(1, "A", string.Join(' ', Enumerable.Repeat("w", 30)), false),
        new(2, "B", "", false),
        new(3, "C", "", true),
    ];

    [Fact]
    public void BudgetsShareThePlanByNotesLength()
    {
        var b = Coach.Budgets(Slides, 450, null);
        Assert.Equal(300, b[1], 3);   // 30 words against 15 for the empty slide
        Assert.Equal(150, b[2], 3);
        Assert.False(b.ContainsKey(3));
        Assert.Empty(Coach.Budgets(Slides, 0, null));
    }

    [Fact]
    public void RehearsalTimingsWin()
    {
        var b = Coach.Budgets(Slides, 450, new Dictionary<int, double> { [1] = 42 });
        Assert.Equal(42, b[1]);
        Assert.Equal(30, b[2]);
    }

    [Fact]
    public void PaceIsElapsedAgainstPlan()
    {
        var b = Coach.Budgets(Slides, 450, null);
        Assert.Equal(60, Coach.PaceDelta(360, 2, 0, Slides, b)!.Value, 3);
        Assert.Equal("1:00 behind", Coach.PaceText(60));
        Assert.Equal("0:45 ahead", Coach.PaceText(-45));
        Assert.Equal("On pace", Coach.PaceText(10));
    }

    [Fact]
    public void TimeCuesFireOnceEach()
    {
        var fired = new HashSet<int>();
        Assert.Equal([60], Coach.CuesDue(58, fired));
        Assert.Empty(Coach.CuesDue(40, fired));
        Assert.Equal([0], Coach.CuesDue(-2, fired));
    }

    [Fact]
    public void Formats()
    {
        Assert.Equal("05:07", Coach.FormatTimer(307));
        Assert.Equal("1:37:20", Coach.FormatTimer(5840));
        Assert.Equal("1:20", Coach.FormatDuration(80));
        Assert.Equal(20, Coach.ReportFileName("C:\\Decks\\Acme.pptx").Length - 5);
    }

    [Fact]
    public void FillersAndPace()
    {
        var s = new SpeechStats();
        s.Finish(["So", "um", "you", "know", "it", "works"]);
        Assert.Equal(6, s.TotalWords);
        Assert.Equal(1, s.Fillers["um"]);
        Assert.Equal(1, s.Fillers["you know"]);
        Assert.Equal(1, s.Fillers["so"]);
    }
}

public class DisplayAndTimerTests
{
    [Fact]
    public void APausedTimerIsDimmed()
    {
        Assert.Equal(TimerLook.Over, Timing.Look(true, 1300, 1200));
        Assert.Equal(TimerLook.Paused, Timing.Look(false, 5840, 1200));
        Assert.Equal(TimerLook.Running, Timing.Look(true, 1300, 0));
    }

    [Fact]
    public void AudienceDisplayChoiceAndNotices()
    {
        string[] displays = ["Built-in", "Projector"];
        Assert.Equal("Projector", AudienceDisplay.Choose("", displays, "Built-in"));
        Assert.Equal("Built-in", AudienceDisplay.Choose("Built-in", displays, "Built-in"));
        Assert.Equal(AudienceDisplay.OneDisplayNotice, AudienceDisplay.NoticeAfterStart(false, 1));
        Assert.Null(AudienceDisplay.NoticeAfterStart(true, 2));
        Assert.Equal(AudienceDisplay.DisplayAddedNotice, AudienceDisplay.NoticeAfterDisplaysChanged(AudienceDisplay.OneDisplayNotice, 2, true));
        Assert.Equal("other", AudienceDisplay.NoticeAfterDisplaysChanged("other", 2, true));
    }

    [Fact]
    public void QuickPromptSummary()
    {
        Assert.Equal(QuickPrompt.Hint, QuickPrompt.Summary("   "));
        Assert.Equal("1 word · about 1 min at a speaking pace", QuickPrompt.Summary("Hello"));
        Assert.Equal("280 words · about 2 min at a speaking pace", QuickPrompt.Summary(string.Join(' ', Enumerable.Repeat("w", 280))));
    }
}

public class RemoteTests
{
    sealed class Host : IRemoteHost
    {
        public List<string> Commands = [];
        public IDictionary<string, object> RemoteState() => new Dictionary<string, object> { ["slide"] = 3 };
        public bool Command(string c) { Commands.Add(c); return c is "next" or "prev"; }
        public byte[]? SlideJpeg(int slide) => slide == 3 ? [1, 2, 3] : null;
        public int CurrentSlide => 3;
    }

    static HttpRequestHead Req(string line) => HttpRequestHead.Parse(Encoding.UTF8.GetBytes(line + " HTTP/1.1\r\nHost: x\r\n\r\n"))!;

    [Fact]
    public void ParsesARequestHead()
    {
        var r = Req("POST /r/abc/cmd?c=next&v=a%20b");
        Assert.Equal("POST", r.Method);
        Assert.Equal("/r/abc/cmd", r.Path);
        Assert.Equal("a b", r.Query["v"]);
        Assert.Null(HttpRequestHead.Parse("GET / HTTP/1.1\r\n"u8));
    }

    [Fact]
    public void TheRemoteAnswersOnlyUnderItsSecretAndWhileOn()
    {
        var host = new Host();
        var router = new RemoteRouter(host, new PollModel());
        Assert.Same(HttpReply.NotFound, router.Handle(Req($"GET /r/{router.RemoteToken}")));
        router.RemoteEnabled = true;
        Assert.StartsWith("<!doctype html>", Encoding.UTF8.GetString(router.Handle(Req($"GET /r/{router.RemoteToken}")).Body));
        Assert.Same(HttpReply.NotFound, router.Handle(Req("GET /r/wrongtoken")));
        Assert.Same(HttpReply.NoContent, router.Handle(Req($"POST /r/{router.RemoteToken}/cmd?c=next")));
        Assert.Same(HttpReply.BadRequest, router.Handle(Req($"POST /r/{router.RemoteToken}/cmd?c=format")));
        Assert.Same(HttpReply.NotFound, router.Handle(Req($"GET /r/{router.RemoteToken}/cmd?c=next")));
        Assert.Equal("image/jpeg", router.Handle(Req($"GET /r/{router.RemoteToken}/slide/3.jpg")).Type);
        Assert.Equal(["next", "format"], host.Commands);
    }

    [Fact]
    public void OneVotePerPhone()
    {
        var poll = new PollModel { OnScreen = true };
        var router = new RemoteRouter(new Host(), poll);
        router.Handle(Req($"POST /p/{router.PollToken}/vote?o=0&v=phone1"));
        router.Handle(Req($"POST /p/{router.PollToken}/vote?o=1&v=phone1"));
        router.Handle(Req($"POST /p/{router.PollToken}/vote?o=1&v=phone2"));
        Assert.Equal([0, 2, 0], poll.Votes);
        poll.OnScreen = false;
        Assert.Same(HttpReply.NotFound, router.Handle(Req($"GET /p/{router.PollToken}/state")));
    }

    [Fact]
    public void TokensAreRandomAndUnambiguous()
    {
        var t = RemoteRouter.RandomToken();
        Assert.Equal(10, t.Length);
        Assert.DoesNotContain(t, c => "01ilo".Contains(c));
        Assert.NotEqual(t, RemoteRouter.RandomToken());
    }
}

public class NotesTests
{
    static SlideBrief Brief(byte[]? pic = null) => new("Acme kickoff", 2, 8, "Pricing", "Three plans", "Welcome", "Roadmap", pic);

    [Fact]
    public void PromptSaysWhatTheSlideHas()
    {
        var p = NotePrompt.Prompt(Brief());
        Assert.Contains("Slide 2 of 8.", p);
        Assert.Contains("Next slide: Roadmap", p);
        Assert.Contains("Text on the slide:\nThree plans", p);
        Assert.DoesNotContain("picture", p);
    }

    [Fact]
    public void CleanRemovesPreamblesBulletsAndQuotes()
    {
        Assert.Equal("First point\nSecond point", NotePrompt.Clean("Here are the notes for slide 2:\n- First point\n2. Second point"));
        Assert.Equal("Just this", NotePrompt.Clean("```\n“Just this”\n```"));
    }

    [Fact]
    public void ClaudeRequestAndReply()
    {
        var body = ClaudeApi.Body("claude-opus-5-5", Brief([1, 2]));
        Assert.Equal("image", (string?)body["messages"]![0]!["content"]![0]!["type"]);
        Assert.Equal("default", (string?)body["fallbacks"]);
        Assert.Equal("server-side-fallback-2026-07-01", ClaudeApi.Headers("k", "claude-opus-5-5")["anthropic-beta"]);
        Assert.Equal("Hi", ClaudeApi.Parse("""{"content":[{"type":"text","text":"Hi"}],"stop_reason":"end_turn"}"""));
        Assert.True(Assert.Throws<NoteWriterException>(() => ClaudeApi.Parse("""{"stop_reason":"refusal","content":[]}""")).Refused);
    }

    [Fact]
    public void ChatRequestAndReply()
    {
        Assert.Equal("http://localhost:11434/v1/chat/completions", ChatApi.Endpoint("http://localhost:11434/v1/")!.ToString());
        Assert.Equal("text", ChatApi.Body("m", Brief([1]), sendPicture: false)["messages"]![1]!["content"]!.GetValueKind() == System.Text.Json.JsonValueKind.String ? "text" : "array");
        Assert.Equal("Notes", ChatApi.Parse("""{"choices":[{"message":{"content":"Notes"}}]}""", "X"));
        Assert.Equal(["a", "b"], ChatApi.ParseModels("""{"data":[{"id":"b"},{"id":"a"}]}"""));
    }

    [Fact]
    public void DraftsMergeAndPrune()
    {
        SlideInfo[] slides = [new(1, "A", "deck notes", false), new(2, "B", "", false), new(3, "C", "", true)];
        var merged = DraftNotes.Merge(slides, new Dictionary<int, string> { [2] = "draft", [1] = "deck notes\r\n" });
        Assert.False(merged[0].Draft);
        Assert.True(merged[1].Draft);
        Assert.Equal([2], DraftNotes.NeedingNotes(slides));
        Assert.Equal([2], DraftNotes.Pruned(new Dictionary<int, string> { [1] = "deck notes", [2] = "x", [9] = "gone" }, slides).Keys);
    }
}

public class McpTests
{
    sealed class Deck : IMcpDeck
    {
        public string? DeckName { get; set; } = "Acme kickoff";
        public string AppName => "PowerPoint";
        public int CurrentSlide => 1;
        public IReadOnlyList<SlideInfo> Slides { get; } = [new(1, "Welcome", "Hi all", false), new(2, "Plans", "", false)];
        public Dictionary<int, string> Drafts = [];
        public Task<string> SlideText(int id) => Task.FromResult($"text {id}");
        public byte[]? SlidePng(int id) => id == 1 ? [1] : null;
        public string? SetDraft(string notes, int slide) { if (slide == 1) return "has notes"; Drafts[slide] = notes; return null; }
    }

    static System.Text.Json.Nodes.JsonNode Reply(string? s) => System.Text.Json.Nodes.JsonNode.Parse(s!)!;

    [Fact]
    public async Task InitializeAndListTools()
    {
        var deck = new Deck();
        var init = Reply(await McpHandler.Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""", deck, "0.1.0"));
        Assert.Equal("2025-03-26", (string?)init["result"]!["protocolVersion"]);
        var tools = Reply(await McpHandler.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", deck, "0.1.0"));
        Assert.Equal(3, tools["result"]!["tools"]!.AsArray().Count);
        Assert.Null(await McpHandler.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", deck, "0.1.0"));
        Assert.Equal(-32700, (int)Reply(await McpHandler.Handle("not json", deck, "0.1.0"))["error"]!["code"]!);
    }

    [Fact]
    public async Task ToolsReadAndWriteDrafts()
    {
        var deck = new Deck();
        var get = await McpHandler.Call("get_deck", [], deck);
        Assert.Contains("\"text\": \"text 2\"", (string?)get["content"]![0]!["text"]);
        var refused = await McpHandler.Call("set_speaker_notes", new() { ["slide"] = 1, ["notes"] = "x" }, deck);
        Assert.True((bool)refused["isError"]!);
        var ok = await McpHandler.Call("set_speaker_notes", new() { ["slide"] = 2, ["notes"] = " New lines " }, deck);
        Assert.False((bool)ok["isError"]!);
        Assert.Equal("New lines", deck.Drafts[2]);
        Assert.True((bool)(await McpHandler.Call("get_slide_image", new() { ["slide"] = 9 }, deck))["isError"]!);
        deck.DeckName = null;
        Assert.True((bool)(await McpHandler.Call("get_deck", [], deck))["isError"]!);
    }

    [Fact]
    public void ClaudeDesktopConfigKeepsOtherServers()
    {
        var updated = ClaudeDesktopConfig.Adding("C:\\Bema\\Bema.exe", """{"mcpServers":{"other":{"command":"x"}},"theme":"dark"}""");
        Assert.True(ClaudeDesktopConfig.IsConnected(updated, "C:\\Bema\\Bema.exe"));
        Assert.Contains("\"other\"", updated);
        Assert.Contains("\"theme\"", updated);
        Assert.Throws<InvalidOperationException>(() => ClaudeDesktopConfig.Adding("x", "[1,2]"));
        Assert.True(ClaudeDesktopConfig.IsConnected(ClaudeDesktopConfig.Adding("x", null), "x"));
    }
}

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bema.Core;

/// <summary>
/// Speaker notes changed in Bema and not yet in the deck. Kept per deck until saved into the deck or
/// discarded; the deck is never changed without "Save to Deck".
/// </summary>
public static class DraftNotes
{
    public static IReadOnlyList<SlideInfo> Merge(IEnumerable<SlideInfo> slides, IReadOnlyDictionary<int, string> drafts) =>
        slides.Select(s => drafts.TryGetValue(s.Id, out var d) && d.Length > 0 && !Same(d, s.Notes) ? s.WithNotes(d, true) : s).ToList();

    public static IReadOnlyList<int> NeedingNotes(IEnumerable<SlideInfo> slides) =>
        slides.Where(s => !s.Hidden && s.Notes.Trim().Length == 0).Select(s => s.Id).ToList();

    public static Dictionary<int, string> Pruned(IReadOnlyDictionary<int, string> drafts, IEnumerable<SlideInfo> slides)
    {
        var deck = new Dictionary<int, string>();
        foreach (var s in slides) deck.TryAdd(s.Id, s.Notes);
        return drafts.Where(kv => deck.TryGetValue(kv.Key, out var notes) && kv.Value.Trim().Length > 0 && !Same(kv.Value, notes))
                     .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public static bool Same(string a, string b)
    {
        static string Norm(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        return Norm(a) == Norm(b);
    }

    /// <summary>The file a deck's drafts are kept in: a hash of the deck's key, never its name.</summary>
    public static string FileName(string deckKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deckKey)))[..24] + ".json";

    public static Dictionary<int, string> Load(string path)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
            return raw.Where(kv => int.TryParse(kv.Key, out _)).ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public static void Save(IReadOnlyDictionary<int, string> drafts, string path)
    {
        if (drafts.Count == 0) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(drafts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
        File.Move(tmp, path, true);
    }
}

/// <summary>A deck PowerPoint opened from OneDrive, SharePoint or Teams: it has a web address instead of a path.</summary>
public static class CloudDeck
{
    public static bool IsCloud(string location) =>
        location.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>What saved timings, reports and drafts are kept under: the path, or the web address.</summary>
    public static string Key(string location) => IsCloud(location) ? location : Path.GetFullPath(location);
}

/// <summary>Who writes speaker notes for slides that have none. Nothing is sent until the person picks one and asks.</summary>
public enum NoteWriterKind { Local, Claude, OpenAI, ClaudeApp }

public static class NoteWriterKinds
{
    public static string Title(this NoteWriterKind k) => k switch
    {
        NoteWriterKind.Local => "Local model (Ollama, LM Studio)",
        NoteWriterKind.Claude => "Claude API key",
        NoteWriterKind.OpenAI => "OpenAI API key",
        _ => "Claude app (your subscription)",
    };

    public static string ShortName(this NoteWriterKind k) => k switch
    {
        NoteWriterKind.Local => "your local model",
        NoteWriterKind.OpenAI => "OpenAI",
        _ => "Claude",
    };

    public static string Privacy(this NoteWriterKind k) => k switch
    {
        NoteWriterKind.Local => "Sent only to the server address below (on this PC unless you change it).",
        NoteWriterKind.Claude => "The slide's text and picture are sent to Anthropic with your key, only when you ask for notes.",
        NoteWriterKind.OpenAI => "The slide's text and picture are sent to OpenAI with your key, only when you ask for notes.",
        _ => "Claude reads the slides from Bema only while “Let Claude read and write notes” is on.",
    };

    public static bool WritesInBema(this NoteWriterKind k) => k != NoteWriterKind.ClaudeApp;
}

public sealed record NoteWriterSettings
{
    public const string DefaultClaudeModel = "claude-opus-5-5";
    public const string DefaultOpenAIModel = "gpt-5";
    public const string DefaultLocalUrl = "http://localhost:11434/v1";

    public NoteWriterKind? Kind { get; init; }
    public string ClaudeModel { get; init; } = DefaultClaudeModel;
    public string OpenAIModel { get; init; } = DefaultOpenAIModel;
    public string LocalUrl { get; init; } = DefaultLocalUrl;
    public string LocalModel { get; init; } = "";
    /// <summary>Most local models read text only, so pictures are off by default for them.</summary>
    public bool LocalSendsPicture { get; init; }
}

/// <summary>What the writer is told about one slide.</summary>
public sealed record SlideBrief(string DeckName, int Number, int Count, string Title, string Text, string? PreviousTitle, string? NextTitle, byte[]? Picture);

public sealed class NoteWriterException(string message, bool refused = false, bool empty = false) : Exception(message)
{
    public bool Refused { get; } = refused;
    public bool Empty { get; } = empty;
    public static NoteWriterException NotSetUp() => new("Choose who writes your notes first.");
    public static NoteWriterException MissingKey(string who) => new($"Add your {who} API key in the notes writer settings.");
    public static NoteWriterException Declined() => new("The writer declined to write notes for this slide.", refused: true);
    public static NoteWriterException Nothing() => new("The writer returned no notes. Try again.", empty: true);
}

public static partial class NotePrompt
{
    public const string Instructions = """
        You write speaker notes for one slide of a presentation. The presenter reads them on a private screen while speaking, so write what to say, not a description of the slide.
        - Write 3 to 6 short lines, in the first person, as the words to say to the audience. One idea per line.
        - Say the words themselves. Don't comment on the slide or the notes: no "I see", "this slide shows", "I need to emphasize".
        - Explain and connect the points on the slide. If the next slide's title is given, end with a short, natural bridge to it; never just "let's move to the next slide".
        - Don't invent numbers, names, dates or facts that the slide doesn't give.
        - Write in the language of the slide.
        - Plain text only: no headings, no bullets, no numbering, no quotation marks around the notes, no preamble. You may put **two asterisks** around the one phrase to stress.
        """;

    public const int MaxSlideText = 3000;

    public static string Prompt(SlideBrief b)
    {
        var lines = new List<string>();
        if (b.DeckName.Length > 0) lines.Add($"Presentation: {b.DeckName}");
        lines.Add($"Slide {b.Number} of {b.Count}.");
        if (!string.IsNullOrEmpty(b.PreviousTitle)) lines.Add($"Previous slide: {b.PreviousTitle}");
        if (!string.IsNullOrEmpty(b.NextTitle)) lines.Add($"Next slide: {b.NextTitle}");
        lines.Add("");
        if (b.Title.Length > 0) lines.Add($"Title: {b.Title}");
        var text = b.Text.Trim();
        if (text.Length == 0)
            lines.Add(b.Picture is null ? "The slide has no text." : "The slide has no text; the picture shows it.");
        else
        {
            lines.Add("Text on the slide:");
            lines.Add(text.Length > MaxSlideText ? text[..MaxSlideText] : text);
        }
        if (b.Picture is not null && text.Length > 0) lines.Add("The attached picture shows the slide.");
        lines.Add("");
        lines.Add("Write the speaker notes for this slide.");
        return string.Join("\n", lines);
    }

    [GeneratedRegex(@"^\d{1,2}[.)]\s+")]
    private static partial Regex Numbering();

    /// <summary>Tidies what a model returns into notes: no code fences, preambles, bullets, numbering or headings.</summary>
    public static string Clean(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("```", StringComparison.Ordinal)).ToList();
        var first = lines.FirstOrDefault(l => l.Length > 0);
        if (first is not null && first.EndsWith(':') && first.Length < 80) lines.Remove(first);
        lines = lines.Select(line =>
        {
            var l = line.TrimStart('#');
            foreach (var marker in new[] { "- ", "• ", "* ", "– " }) if (l.StartsWith(marker, StringComparison.Ordinal)) l = l[marker.Length..];
            l = Numbering().Replace(l, "");
            return l.Trim();
        }).ToList();
        var text = string.Join("\n", lines);
        while (text.Contains("\n\n\n", StringComparison.Ordinal)) text = text.Replace("\n\n\n", "\n\n");
        text = text.Trim();
        foreach (var (open, close) in new[] { ("\"", "\""), ("“", "”") })
            if (text.Length > 2 && text.StartsWith(open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal)) text = text[1..^1].Trim();
        return text;
    }
}

public static class ClaudeApi
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string Version = "2026-10-21";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    public static readonly IReadOnlySet<string> FallbackModels = new HashSet<string> { "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5", "claude-fable-5-1", "claude-fable-5-2" };

    public static JsonObject Body(string model, SlideBrief brief)
    {
        var content = new JsonArray();
        if (brief.Picture is { } png)
            content.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(png) } });
        content.Add(new JsonObject { ["type"] = "text", ["text"] = NotePrompt.Prompt(brief) });
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 8000,
            ["system"] = NotePrompt.Instructions,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };
        if (FallbackModels.Contains(model))
        {
            body["output_config"] = new JsonObject { ["effort"] = "medium" };
            body["fallbacks"] = "default";
        }
        return body;
    }

    public static Dictionary<string, string> Headers(string key, string model)
    {
        var h = new Dictionary<string, string> { ["x-api-key"] = key, ["anthropic-version"] = Version };
        if (FallbackModels.Contains(model)) h["anthropic-beta"] = FallbackBeta;
        return h;
    }

    public static string Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (JsonException) { root = null; }
        if (root is not JsonObject o) throw new NoteWriterException("Claude sent an answer Bema couldn't read.");
        if (o["error"] is JsonObject err) throw new NoteWriterException($"Claude: {(string?)err["message"] ?? "the request failed."}");
        if ((string?)o["stop_reason"] == "refusal") throw NoteWriterException.Declined();
        var text = string.Concat((o["content"] as JsonArray ?? []).OfType<JsonObject>().Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"] ?? ""));
        if (text.Trim().Length == 0) throw NoteWriterException.Nothing();
        return text;
    }
}

/// <summary>The OpenAI chat API, also spoken by Ollama and LM Studio.</summary>
public static class ChatApi
{
    public const string OpenAIBase = "https://api.openai.com/v1";

    static string Base(string b) => b.Trim().TrimEnd('/');

    public static Uri? Endpoint(string baseUrl)
    {
        var b = Base(baseUrl);
        return Uri.TryCreate(b.EndsWith("/chat/completions", StringComparison.Ordinal) ? b : b + "/chat/completions", UriKind.Absolute, out var u) ? u : null;
    }

    public static Uri? ModelsEndpoint(string baseUrl) => Uri.TryCreate(Base(baseUrl) + "/models", UriKind.Absolute, out var u) ? u : null;

    public static JsonObject Body(string model, SlideBrief brief, bool sendPicture)
    {
        var prompt = NotePrompt.Prompt(sendPicture ? brief : brief with { Picture = null });
        JsonNode user = sendPicture && brief.Picture is { } png
            ? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt },
                            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) } })
            : JsonValue.Create(prompt)!;
        return new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = NotePrompt.Instructions },
                                         new JsonObject { ["role"] = "user", ["content"] = user }),
        };
    }

    public static string Parse(string json, string who)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (JsonException) { root = null; }
        if (root is not JsonObject o) throw new NoteWriterException($"{who} sent an answer Bema couldn't read.");
        if (o["error"] is JsonObject err) throw new NoteWriterException($"{who}: {(string?)err["message"] ?? "the request failed."}");
        if (o["error"] is JsonValue ev && ev.TryGetValue<string>(out var es)) throw new NoteWriterException($"{who}: {es}");
        var message = (o["choices"] as JsonArray)?.FirstOrDefault()?["message"] as JsonObject;
        if (message?["refusal"] is JsonValue r && r.TryGetValue<string>(out var rs) && rs.Length > 0) throw NoteWriterException.Declined();
        var text = message?["content"] is JsonValue c && c.TryGetValue<string>(out var cs) ? cs : "";
        if (text.Trim().Length == 0) throw NoteWriterException.Nothing();
        return text;
    }

    public static IReadOnlyList<string> ParseModels(string json)
    {
        try
        {
            return (JsonNode.Parse(json)?["data"] as JsonArray ?? []).Select(m => (string?)m?["id"]).OfType<string>().Order(StringComparer.Ordinal).ToList();
        }
        catch (JsonException) { return []; }
    }
}

/// <summary>Writes notes for one slide with the chosen writer. API keys come from the app's own key store.</summary>
public sealed class NoteWriter(HttpClient http, Func<NoteWriterKind, string?> readKey)
{
    public async Task<string> Write(SlideBrief brief, NoteWriterSettings settings, CancellationToken ct = default)
    {
        if (settings.Kind is not { } kind || !kind.WritesInBema()) throw NoteWriterException.NotSetUp();
        string raw;
        switch (kind)
        {
            case NoteWriterKind.Claude:
            {
                var key = readKey(kind);
                if (string.IsNullOrEmpty(key)) throw NoteWriterException.MissingKey("Claude");
                using var req = new HttpRequestMessage(HttpMethod.Post, ClaudeApi.Endpoint)
                {
                    Content = new StringContent(ClaudeApi.Body(settings.ClaudeModel, brief).ToJsonString(), Encoding.UTF8, "application/json"),
                };
                foreach (var (k, v) in ClaudeApi.Headers(key, settings.ClaudeModel)) req.Headers.TryAddWithoutValidation(k, v);
                raw = ClaudeApi.Parse(await Send(req, ct));
                break;
            }
            case NoteWriterKind.OpenAI:
            {
                var key = readKey(kind);
                if (string.IsNullOrEmpty(key)) throw NoteWriterException.MissingKey("OpenAI");
                raw = await Chat(ChatApi.OpenAIBase, key, settings.OpenAIModel, brief, true, "OpenAI", ct);
                break;
            }
            default:
                if (settings.LocalModel.Length == 0) throw new NoteWriterException("Choose which local model writes your notes in the notes writer settings.");
                raw = await Chat(settings.LocalUrl, null, settings.LocalModel, brief, settings.LocalSendsPicture, "The local model", ct);
                break;
        }
        var notes = NotePrompt.Clean(raw);
        return notes.Length == 0 ? throw NoteWriterException.Nothing() : notes;
    }

    async Task<string> Chat(string baseUrl, string? key, string model, SlideBrief brief, bool picture, string who, CancellationToken ct)
    {
        var url = ChatApi.Endpoint(baseUrl) ?? throw new NoteWriterException("The server address isn't valid.");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(ChatApi.Body(model, brief, picture).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return ChatApi.Parse(await Send(req, ct), who);
    }

    async Task<string> Send(HttpRequestMessage req, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        try
        {
            using var resp = await http.SendAsync(req, timeout.Token);
            return await resp.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (HttpRequestException)
        {
            throw new NoteWriterException($"Couldn't reach {req.RequestUri?.Host ?? "the server"}. If it's a local model, make sure Ollama or LM Studio is running.");
        }
    }

    public async Task<IReadOnlyList<string>> LocalModels(string baseUrl)
    {
        if (ChatApi.ModelsEndpoint(baseUrl) is not { } url) return [];
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return ChatApi.ParseModels(await http.GetStringAsync(url, cts.Token));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return []; }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bema.Core;

/// <summary>What the MCP tools see of the open deck (the app in practice; a stand-in in tests).</summary>
public interface IMcpDeck
{
    string? DeckName { get; }
    string AppName { get; }
    int CurrentSlide { get; }
    IReadOnlyList<SlideInfo> Slides { get; }
    Task<string> SlideText(int id);
    byte[]? SlidePng(int id);
    /// <summary>Stores notes as a draft. Returns why it couldn't, or null.</summary>
    string? SetDraft(string notes, int slide);
}

/// <summary>
/// Bema's MCP server, so Claude Desktop (and other MCP apps) can read the slides and write speaker
/// notes with the person's own subscription. JSON-RPC, one message per line. Same tools as the Mac app.
/// </summary>
public static class McpHandler
{
    public const string ServerName = "bema";
    public static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    static JsonObject Schema(JsonObject props, params string[] required)
    {
        var o = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0) o["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray());
        return o;
    }

    public static JsonArray Tools() =>
    [
        new JsonObject
        {
            ["name"] = "get_deck",
            ["description"] = "The presentation open in Bema: every slide's number, title, text, speaker notes and whether the slide is hidden, plus the slide the presenter is on. Notes marked as drafts were written for Bema and aren't in the deck yet.",
            ["inputSchema"] = Schema([]),
        },
        new JsonObject
        {
            ["name"] = "get_slide_image",
            ["description"] = "A picture of one slide, to see charts, photos and layout that the text doesn't show.",
            ["inputSchema"] = Schema(new JsonObject { ["slide"] = new JsonObject { ["type"] = "integer", ["description"] = "Slide number, starting at 1." } }, "slide"),
        },
        new JsonObject
        {
            ["name"] = "set_speaker_notes",
            ["description"] = "Saves speaker notes for a slide that has none in the deck. Bema keeps them as a draft the presenter can edit, then save into the deck. Write what the presenter will say: a few short lines in the first person, plain text, in the slide's language. **Bold** one phrase to stress if useful.",
            ["inputSchema"] = Schema(new JsonObject
            {
                ["slide"] = new JsonObject { ["type"] = "integer", ["description"] = "Slide number, starting at 1." },
                ["notes"] = new JsonObject { ["type"] = "string", ["description"] = "The speaker notes, one idea per line." },
            }, "slide", "notes"),
        },
    ];

    /// <summary>The reply to one message, or null for a notification.</summary>
    public static async Task<string?> Handle(string line, IMcpDeck deck, string version)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { msg = null; }
        if (msg is null) return Encode(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" } });
        if (!msg.TryGetPropertyValue("id", out var id) || (string?)msg["method"] is not { } method) return null;
        var p = msg["params"] as JsonObject ?? [];
        string Result(JsonObject r) => Encode(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = r });
        switch (method)
        {
            case "initialize":
                var asked = (string?)p["protocolVersion"] ?? "";
                return Result(new JsonObject
                {
                    ["protocolVersion"] = SupportedVersions.Contains(asked) ? asked : SupportedVersions[1],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "Bema", ["version"] = version },
                    ["instructions"] = "Bema is the presenter app showing the user's slides. Use get_deck to read the presentation, get_slide_image to see a slide, and set_speaker_notes to give a slide notes. Notes you set stay as drafts in Bema until the presenter saves them into the deck.",
                });
            case "ping": return Result([]);
            case "tools/list": return Result(new JsonObject { ["tools"] = Tools() });
            case "tools/call":
                return Result(await Call((string?)p["name"] ?? "", p["arguments"] as JsonObject ?? [], deck));
            default:
                return Encode(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" } });
        }
    }

    static JsonObject Text(string s, bool error = false) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = s }), ["isError"] = error };

    public static async Task<JsonObject> Call(string name, JsonObject args, IMcpDeck deck)
    {
        if (deck.DeckName is not { } deckName)
            return Text("No presentation is open in Bema. Ask the user to open one in Bema (or present the deck open in PowerPoint) first.", true);
        var slides = deck.Slides;
        int? n = args["slide"] switch
        {
            JsonValue v when v.TryGetValue<int>(out var i) => i,
            JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
            JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var i2) => i2,
            _ => null,
        };
        string NoSlide() => $"There's no slide {(n?.ToString() ?? "with that number")}. The deck has {slides.Count} slides.";
        switch (name)
        {
            case "get_deck":
            {
                var list = new JsonArray();
                foreach (var s in slides)
                    list.Add(new JsonObject
                    {
                        ["hidden"] = s.Hidden, ["notes"] = s.Notes, ["notes_are_draft"] = s.Draft, ["number"] = s.Id,
                        ["text"] = await deck.SlideText(s.Id), ["title"] = s.Title,
                    });
                var info = new JsonObject
                {
                    ["app"] = deck.AppName, ["current_slide"] = deck.CurrentSlide, ["presentation"] = deckName,
                    ["slide_count"] = slides.Count, ["slides"] = list,
                };
                return Text(info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            case "get_slide_image":
                if (n is not { } img || slides.All(s => s.Id != img)) return Text(NoSlide(), true);
                if (deck.SlidePng(img) is not { } png) return Text($"Bema has no picture of slide {img} (hidden slides and decks without previews have none).", true);
                return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" }), ["isError"] = false };
            case "set_speaker_notes":
            {
                if (n is not { } id || slides.All(s => s.Id != id)) return Text(NoSlide(), true);
                var notes = ((string?)args["notes"] ?? "").Trim();
                if (notes.Length == 0) return Text("The notes are empty.", true);
                if (deck.SetDraft(notes, id) is { } why) return Text(why, true);
                return Text($"Saved as a draft for slide {id}. The presenter sees it in Bema and can edit it or save it into the deck.");
            }
            default: return Text($"Bema has no tool called {name}.", true);
        }
    }

    static string Encode(JsonObject o) => o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>The reply when Bema isn't there to answer.</summary>
    public static string? Unavailable(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject msg || !msg.TryGetPropertyValue("id", out var id)) return null;
            return Encode(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "Bema isn't answering. Open Bema and turn on “Let Claude read and write notes” in Stage tools." },
            });
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Adds Bema to Claude Desktop's MCP servers (its claude_desktop_config.json), keeping everything else.</summary>
public static class ClaudeDesktopConfig
{
    public const string ServerKey = "bema";

    public static string Adding(string command, string? existing)
    {
        var root = new JsonObject();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(existing); } catch (JsonException) { parsed = null; }
            root = parsed as JsonObject ?? throw new InvalidOperationException("Claude's settings file isn't in the expected format, so Bema left it alone.");
        }
        var servers = root["mcpServers"] as JsonObject ?? [];
        servers[ServerKey] = new JsonObject { ["command"] = command, ["args"] = new JsonArray("--mcp") };
        root["mcpServers"] = servers;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static bool IsConnected(string? config, string command)
    {
        try { return (string?)JsonNode.Parse(config ?? "")?["mcpServers"]?[ServerKey]?["command"] == command; }
        catch (JsonException) { return false; }
    }
}

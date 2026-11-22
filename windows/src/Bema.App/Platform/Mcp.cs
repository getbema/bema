using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Bema.App.Model;
using Bema.Core;

namespace Bema.App.Platform;

/// <summary>Named pipes only this Windows user can open (the Mac's owner-only socket).</summary>
static class Pipes
{
    public static NamedPipeServerStream OwnerOnly(string name, PipeDirection direction)
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetAccessRuleProtection(true, false);
        return NamedPipeServerStreamAcl.Create(name, direction, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>Pipe names are per machine: include the user, so two people on one PC don't meet.</summary>
    public static string ForUser(string name) => $"{name}.{WindowsIdentity.GetCurrent().User!.Value}";
}

/// <summary>
/// While "Let Claude read and write notes" is on, Bema answers MCP messages on a pipe only this user
/// can open. <c>Bema.exe --mcp</c>, started by Claude Desktop, relays to it.
/// </summary>
public sealed class McpServer : IMcpDeck
{
    public static McpServer Shared { get; } = new();
    public static string PipeName => Pipes.ForUser("Bema.MCP");
    CancellationTokenSource? cts;

    public bool Enabled
    {
        get => Settings.Get("mcpEnabled", false);
        set { Settings.Set("mcpEnabled", value); if (value) Start(); else Stop(); }
    }

    public void StartIfEnabled() { if (Enabled) Start(); }

    void Start()
    {
        if (cts is not null) return;
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try { pipe = Pipes.OwnerOnly(PipeName, PipeDirection.InOut); await pipe.WaitForConnectionAsync(ct); }
                catch (Exception) { return; }
                _ = Serve(pipe, ct);
            }
        }, ct);
    }

    void Stop() { cts?.Cancel(); cts = null; }

    async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            try
            {
                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Trim().Length == 0) continue;
                    var reply = await Application.Current.Dispatcher.InvokeAsync(() => McpHandler.Handle(line, this, Version)).Task.Unwrap();
                    if (reply is not null) await writer.WriteLineAsync(reply);
                }
            }
            catch (Exception) { }
        }
    }

    static string Version => typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "dev";

    // IMcpDeck: the open deck as Claude sees it.
    public string? DeckName => Presenter.Shared.Deck is null || Presenter.Shared.Slides.Count == 0 ? null : Presenter.Shared.DeckName;
    public string AppName => "PowerPoint";
    public int CurrentSlide => Presenter.Shared.Current;
    public IReadOnlyList<SlideInfo> Slides => Presenter.Shared.Slides;
    public Task<string> SlideText(int id) => Presenter.Shared.SlideText(id);

    public byte[]? SlidePng(int id)
    {
        if (Presenter.Shared.Image(id, 1280) is not { } img) return null;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public string? SetDraft(string notes, int slide)
    {
        var deckNotes = Presenter.Shared.Slides.FirstOrDefault(s => s.Id == slide);
        if (deckNotes is { Draft: false } && deckNotes.Notes.Trim().Length > 0)
            return $"Slide {slide} already has speaker notes in the deck, and Bema keeps those. Only slides without notes get drafts.";
        Presenter.Shared.SetDraft(slide, notes);
        return null;
    }

    // ---- Claude Desktop ----

    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
    public static string Command => Environment.ProcessPath ?? "Bema.exe";
    public static bool ClaudeConnected => File.Exists(ConfigPath) && ClaudeDesktopConfig.IsConnected(File.ReadAllText(ConfigPath), Command);

    /// <summary>Writes Bema into Claude Desktop's config, keeping a copy of the previous file.</summary>
    public static void ConnectClaude()
    {
        var existing = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
        var updated = ClaudeDesktopConfig.Adding(Command, existing);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        if (existing is not null) File.WriteAllText(Path.Combine(Path.GetDirectoryName(ConfigPath)!, "claude_desktop_config.before-bema.json"), existing);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, updated);
        File.Move(tmp, ConfigPath, true);
    }
}

/// <summary><c>Bema.exe --mcp</c>: relays MCP messages between Claude Desktop (stdin/stdout) and the running Bema.</summary>
public static class McpBridge
{
    public static int Run()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        NamedPipeClientStream? pipe = null;
        StreamWriter? toBema = null;
        while (stdin.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (pipe is null && Connect(stdout) is { } p) { pipe = p; toBema = new StreamWriter(p, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" }; }
                if (toBema is null) break;
                try { toBema.WriteLine(line); goto sent; }
                catch (IOException) { pipe?.Dispose(); pipe = null; toBema = null; }
            }
            if (McpHandler.Unavailable(line) is { } reply) stdout.WriteLine(reply);
            sent:;
        }
        Thread.Sleep(pipe is null ? 0 : 800);   // replies on their way
        return 0;
    }

    /// <summary>Connects to Bema, starting it in the background if needed, and copies its replies to stdout.</summary>
    static NamedPipeClientStream? Connect(StreamWriter stdout)
    {
        var started = false;
        for (var i = 0; i < 60; i++)
        {
            var pipe = new NamedPipeClientStream(".", McpServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(250);
                var reader = new StreamReader(pipe, new UTF8Encoding(false));
                new Thread(() =>
                {
                    try { while (reader.ReadLine() is { } l) stdout.WriteLine(l); } catch (IOException) { }
                }) { IsBackground = true }.Start();
                return pipe;
            }
            catch (Exception) { pipe.Dispose(); }
            if (!started && Environment.ProcessPath is { } exe)
            {
                started = true;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false }); } catch (Exception) { }
            }
        }
        return null;
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bema.Core;

/// <summary>A live poll: one vote per phone, voting again changes the vote.</summary>
public sealed class PollModel
{
    readonly Dictionary<string, int> voters = [];

    public string Question { get; set; } = "What should we cover next?";
    public IReadOnlyList<string> Options { get; private set; } = ["Option A", "Option B", "Option C"];
    public int[] Votes { get; private set; } = [0, 0, 0];
    public bool OnScreen { get; set; }
    public bool ShowResults { get; set; } = true;
    public int TotalVotes => Votes.Sum();
    public event Action? Changed;

    public void SetOptions(IReadOnlyList<string> options) { Options = options; Reset(); }

    public void Reset() { Votes = new int[Options.Count]; voters.Clear(); Changed?.Invoke(); }

    public void Vote(int option, string voter)
    {
        if (option < 0 || option >= Options.Count || voter.Length == 0) return;
        if (Votes.Length != Options.Count) Votes = new int[Options.Count];
        if (voters.TryGetValue(voter, out var previous) && previous >= 0 && previous < Votes.Length) Votes[previous]--;
        voters[voter] = option;
        Votes[option]++;
        Changed?.Invoke();
    }
}

/// <summary>A parsed request head. Bodies are never needed: commands use the query string.</summary>
public sealed record HttpRequestHead(string Method, string Path, IReadOnlyDictionary<string, string> Query)
{
    public static HttpRequestHead? Parse(ReadOnlySpan<byte> data)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); } catch (DecoderFallbackException) { return null; }
        if (!text.Contains("\r\n\r\n", StringComparison.Ordinal)) return null;
        var line = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var target = parts[1];
        var q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        var query = new Dictionary<string, string>();
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var name = Uri.UnescapeDataString(eq >= 0 ? pair[..eq] : pair);
                query[name] = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
            }
        }
        try { path = Uri.UnescapeDataString(path); } catch (UriFormatException) { return null; }
        return new HttpRequestHead(parts[0], path.Length == 0 ? "/" : path, query);
    }
}

public sealed record HttpReply(string Status, string Type, byte[] Body)
{
    public byte[] Bytes()
    {
        var head = $"HTTP/1.1 {Status}\r\nContent-Type: {Type}\r\nContent-Length: {Body.Length}\r\n" +
                   "Cache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n";
        return [.. Encoding.UTF8.GetBytes(head), .. Body];
    }

    public static HttpReply Html(string s) => new("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(s));
    public static HttpReply Json(object o) => new("200 OK", "application/json", JsonSerializer.SerializeToUtf8Bytes(o));
    public static HttpReply Jpeg(byte[] b) => new("200 OK", "image/jpeg", b);
    public static readonly HttpReply NoContent = new("204 No Content", "text/plain", []);
    public static readonly HttpReply NotFound = new("404 Not Found", "text/plain", "Not found"u8.ToArray());
    public static readonly HttpReply BadRequest = new("400 Bad Request", "text/plain", "Bad request"u8.ToArray());
}

/// <summary>What the phone remote can see and do; the app implements it on its UI thread.</summary>
public interface IRemoteHost
{
    /// <summary>The remote's state, as the phone page reads it (same keys as the Mac app).</summary>
    IDictionary<string, object> RemoteState();
    /// <summary>Runs a command: next, prev, black, first, last, start, prompter, timer, confetti, applause. False if unknown.</summary>
    bool Command(string command);
    /// <summary>A slide's picture as JPEG for the phone, or null.</summary>
    byte[]? SlideJpeg(int slide);
    int CurrentSlide { get; }
}

/// <summary>The phone pages, kept as files shared with the Mac app's text.</summary>
public static class PhonePages
{
    public static string Remote => Read("remote.html");
    public static string Poll => Read("poll.html");

    static string Read(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Bema.Core.Pages.{name}")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

/// <summary>
/// Routes the phone remote and the poll. Each lives under a random secret path, so only people who
/// scan the QR code can use them, and only while it's on.
/// </summary>
public sealed class RemoteRouter(IRemoteHost host, PollModel poll)
{
    public string RemoteToken { get; } = RandomToken();
    public string PollToken { get; } = RandomToken();
    public bool RemoteEnabled { get; set; }

    public HttpReply Handle(HttpRequestHead req)
    {
        var parts = req.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return HttpReply.NotFound;

        if (parts[0] == "r" && Equal(parts[1], RemoteToken) && RemoteEnabled)
        {
            if (parts.Length == 2) return HttpReply.Html(PhonePages.Remote);
            switch (parts[2])
            {
                case "state": return HttpReply.Json(host.RemoteState());
                case "cmd" when req.Method == "POST":
                    return host.Command(req.Query.GetValueOrDefault("c") ?? "") ? HttpReply.NoContent : HttpReply.BadRequest;
                case "slide" when parts.Length == 4:
                    var n = int.TryParse(parts[3].Replace(".jpg", ""), out var v) ? v : host.CurrentSlide;
                    return host.SlideJpeg(n) is { } jpeg ? HttpReply.Jpeg(jpeg) : HttpReply.NotFound;
                default: return HttpReply.NotFound;
            }
        }
        if (parts[0] == "p" && Equal(parts[1], PollToken) && poll.OnScreen)
        {
            if (parts.Length == 2) return HttpReply.Html(PhonePages.Poll);
            switch (parts[2])
            {
                case "state": return HttpReply.Json(new Dictionary<string, object> { ["question"] = poll.Question, ["options"] = poll.Options });
                case "vote" when req.Method == "POST":
                    var voter = req.Query.GetValueOrDefault("v") ?? "";
                    poll.Vote(int.TryParse(req.Query.GetValueOrDefault("o"), out var o) ? o : -1, voter.Length > 40 ? voter[..40] : voter);
                    return HttpReply.NoContent;
                default: return HttpReply.NotFound;
            }
        }
        return HttpReply.NotFound;
    }

    static bool Equal(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string RandomToken()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        return string.Create(10, 0, (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)]; });
    }
}

/// <summary>
/// A tiny HTTP server on the local network, running only while the remote or a poll is on. Requests
/// are handed to <paramref name="dispatch"/> so they're answered on the app's UI thread.
/// </summary>
public sealed class RemoteServer(RemoteRouter router, Func<Func<HttpReply>, Task<HttpReply>> dispatch) : IDisposable
{
    public const int PreferredPort = 8765;
    TcpListener? listener;
    CancellationTokenSource? cts;

    public int? Port { get; private set; }
    public string? Error { get; private set; }
    public bool Running => listener is not null;

    public string? RemoteUrl => Url($"/r/{router.RemoteToken}");
    public string? PollUrl => Url($"/p/{router.PollToken}");

    string? Url(string path) => Port is { } p && LocalIPv4() is { } ip ? $"http://{ip}:{p}{path}" : null;

    public void Start()
    {
        if (listener is not null) return;
        try
        {
            listener = TryListen(PreferredPort) ?? TryListen(0) ?? throw new SocketException();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Error = null;
            cts = new CancellationTokenSource();
            _ = AcceptLoop(listener, cts.Token);
        }
        catch (Exception e)
        {
            Error = $"The phone remote couldn't start: {e.Message}";
            listener = null;
            Port = null;
        }
    }

    static TcpListener? TryListen(int port)
    {
        try { var l = new TcpListener(IPAddress.Any, port); l.Start(); return l; }
        catch (SocketException) { return null; }
    }

    public void Stop()
    {
        cts?.Cancel();
        listener?.Stop();
        listener = null;
        Port = null;
    }

    async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct); } catch { return; }
            _ = Serve(client, ct);
        }
    }

    async Task Serve(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (buffer.Length < 1_000_000)
                {
                    var n = await stream.ReadAsync(chunk, timeout.Token);
                    if (n == 0) return;
                    buffer.Write(chunk, 0, n);
                    if (HttpRequestHead.Parse(buffer.ToArray()) is { } req)
                    {
                        var reply = await dispatch(() => router.Handle(req));
                        await stream.WriteAsync(reply.Bytes(), timeout.Token);
                        return;
                    }
                }
            }
            catch (Exception) { /* the phone went away */ }
        }
    }

    /// <summary>This PC's address on the local network (Wi-Fi or Ethernet).</summary>
    public static string? LocalIPv4()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        return candidates.FirstOrDefault()?.ToString();
    }

    public void Dispose() => Stop();
}

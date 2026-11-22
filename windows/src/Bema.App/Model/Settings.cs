using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bema.Core;

namespace Bema.App.Model;

/// <summary>
/// Bema's preferences, in %APPDATA%\Bema\settings.json (the Mac app's UserDefaults). On this PC only;
/// nothing is synced. API keys are kept apart, encrypted for this Windows user (DPAPI).
/// </summary>
public static class Settings
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bema");
    static readonly string FilePath = Path.Combine(Folder, "settings.json");
    static readonly object Gate = new();
    static JsonObject? data;

    static JsonObject Data
    {
        get
        {
            if (data is not null) return data;
            try { data = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject; } catch (Exception) { }
            return data ??= [];
        }
    }

    public static T Get<T>(string key, T fallback)
    {
        lock (Gate)
        {
            try { return Data[key] is { } n ? n.Deserialize<T>() ?? fallback : fallback; }
            catch (Exception) { return fallback; }
        }
    }

    public static void Set<T>(string key, T value)
    {
        lock (Gate)
        {
            if (value is null) Data.Remove(key); else Data[key] = JsonSerializer.SerializeToNode(value);
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
    }

    /// <summary>Where a deck's own files go (reports, drafts, previews), by a hash of its location.</summary>
    public static string DeckFolder(string kind) => Path.Combine(Folder, kind);

    // ---- API keys (the Mac app keeps them in the Keychain) ----

    static string KeyPath(NoteWriterKind kind) => Path.Combine(Folder, "Keys", $"{kind}.bin");

    public static string? ReadKey(NoteWriterKind kind)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath(kind)), null, DataProtectionScope.CurrentUser)); }
        catch (Exception) { return null; }
    }

    public static void SaveKey(NoteWriterKind kind, string? key)
    {
        var path = KeyPath(kind);
        if (string.IsNullOrWhiteSpace(key)) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
    }
}

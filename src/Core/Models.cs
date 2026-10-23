using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Pigeon.Manager;

public static class Json {
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false, PropertyNameCaseInsensitive = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("空数据");
    public static void Save<T>(string path, T value) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, Encode(value), new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
public sealed record ServerProfile {
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "UTSL服务器";
    public string Folder { get; init; } = "";
    public string? ExistingDirectory { get; init; }
    public string Executable { get; init; } = "UnifierTSL.exe";
    public string[] Arguments { get; set; } = [];
    public int[] Ports { get; set; } = [];
    public bool HeadlessWorlds { get; set; } = true;
    public bool AutoStart { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 600;
}
public sealed record ProcessIdentity(int Pid, long StartedUtcTicks, string Executable) {
    public bool IsCurrent() {
        try { using var p = System.Diagnostics.Process.GetProcessById(Pid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == StartedUtcTicks && string.Equals(p.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
public sealed record ServerState(ServerProfile Profile, string State, ProcessIdentity? Identity, long MemoryBytes = 0, string? Error = null, string? RuntimeDirectory = null, string? RuntimeManifest = null);
public sealed record PluginInfo(string Name, string FileName, string Version, long Bytes, string Sha256);
public sealed record LogChunk(long Sequence, string Text);
public sealed record LogPage(long Cursor, bool Truncated, LogChunk[] Chunks);
public sealed record Request(string Action, string? Id = null, string? Value = null, long Cursor = 0, ServerProfile? Profile = null, string[]? Files = null, ProcessIdentity? Identity = null, string? World = null);
public sealed record WorldConsoleInfo(int Index, string Name, bool Running);
public sealed record StartupQueueState(bool Active, string? CurrentId = null, string Message = "", string[]? Order = null);
public sealed record Reply(bool Ok, string Message = "", ServerState[]? Servers = null, LogPage? Log = null, PluginInfo[]? Plugins = null, string? Path = null, StartupQueueState? StartupQueue = null, WorldConsoleInfo[]? Worlds = null, StartupQueueState? StopQueue = null, PluginArtifact[]? Artifacts = null, RuntimeSelection? RuntimeSelection = null, EngineArtifact[]? Engines = null);
public static class Hashes {
    public static string FileSha(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static string RootId(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).TrimEnd('\\').ToUpperInvariant())))[..24];
}

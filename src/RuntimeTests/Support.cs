using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pigeon.Manager;

namespace RuntimeChecks;

sealed class Evidence(string directory) {
    readonly List<object> checks = [];
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public void Check(string name, bool pass, object? detail = null) {
        checks.Add(new { name, pass, detail });
        if (pass) Passed++; else Failed++;
        Console.WriteLine((pass ? "PASS " : "FAIL ") + name + (pass ? "" : " " + Json.Encode(detail)));
        Save();
    }
    public void Require(string name, bool pass, object? detail = null) {
        Check(name, pass, detail);
        if (!pass) throw new InvalidDataException(name);
    }
    public async Task Scenario(string name, Func<Task> run) {
        Console.WriteLine("SCENARIO " + name);
        try { await run(); }
        catch (Exception e) { Check(name + "/completed", false, new { error = e.ToString() }); }
    }
    public void Save() => Json.Save(Path.Combine(directory, "result.json"), new {
        at = DateTimeOffset.Now, passed = Passed, failed = Failed, checks,
        scope = "Offline local fixtures; actual DLL execution, file IDs and Host processes; no SSH or production servers",
        unverified = new[] { "Real UTSL/Terraria engine and third-party plugins are outside this fixture's scope" }
    });
    public void Write(string name, object value) => Json.Save(Path.Combine(directory, name), value);
}

sealed class ServerFixture {
    public Workspace Workspace { get; }
    public ServerProfile[] Profiles { get; }
    public SharedRuntime Runtime { get; }
    public string Events { get; }
    public ServerFixture(string root, string fixturePackage, int count = 2) {
        Workspace = new(root);
        Events = Path.Combine(root, "fixture-events.jsonl");
        Runtime = new(Workspace);
        Profiles = Enumerable.Range(0, count).Select(index => {
            string folder = index + ".Fixture-" + (index == 0 ? "A" : "B");
            string server = index == 0 ? "A" : "B";
            string target = Path.Combine(Workspace.Servers, folder);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(fixturePackage))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            File.Copy(Path.Combine(fixturePackage, "Manager.RuntimeFixture.exe"), Path.Combine(target, "UnifierTSL.exe"));
            File.Copy(Path.Combine(fixturePackage, "Manager.RuntimeFixture.exe"), Path.Combine(target, "FixtureServer.exe"));
            Json.Save(Path.Combine(target, "config", "config.json"), new {
                fixtureServer = server, launcher = new { autoStartServers = new[] { new { name = "fixture-" + server } } }
            });
            Json.Save(Path.Combine(target, "config", "TShockAPI", "config.json"), new {
                RestApiPort = FreePort(),
                ApplicationRestTokens = new Dictionary<string, string> { ["LOCAL_RUNTIME_FIXTURE_ONLY"] = "fixture" }
            });
            foreach (string mutable in new[] { "config", "Data", "world" }) {
                Directory.CreateDirectory(Path.Combine(target, mutable));
                File.WriteAllText(Path.Combine(target, mutable, "fixture-" + server + (mutable == "world" ? ".wld" : ".txt")), "0");
            }
            return new ServerProfile {
                Id = "fixture-" + server, Folder = folder, Name = "Fixture " + server, Executable = "FixtureServer.exe",
                StartupTimeoutSeconds = 15, AutoStart = false
            };
        }).ToArray();
        Workspace.Save(Profiles.ToList());
    }
    static int FreePort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
    public string Source(int index) => Workspace.ServerDirectory(Profiles[index]);
    public RuntimeLaunch Prepare(int index) => Runtime.Prepare(Profiles[index]);
    public void Select(int index, params PluginArtifact[] plugins) => Runtime.SaveSelection(Profiles[index], new(true, plugins));
    public string ImportPluginSource(string assembly, string label) {
        string directory = Path.Combine(Workspace.Root, "incoming", label);
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "RuntimeFixture.Plugin.dll");
        File.Copy(assembly, target);
        return target;
    }
    public PluginArtifact Import(string assembly, string label = "v1") =>
        Runtime.Import([ImportPluginSource(assembly, label)]).Single();
    public JsonElement[] ReadEvents() => Helpers.Events(Events);
}

sealed class FixtureProcess : IAsyncDisposable {
    readonly Task stdout, stderr;
    public Process Process { get; }
    readonly long started;
    readonly string executable;
    public FixtureProcess(RuntimeLaunch launch, string events, string evidenceRoot) {
        executable = Path.GetFullPath(launch.Executable);
        var info = new ProcessStartInfo(executable) {
            WorkingDirectory = launch.Directory, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.Environment["RUNTIME_FIXTURE_EVENTS"] = events;
        Process = Process.Start(info) ?? throw new IOException("Fixture did not start");
        started = Process.StartTime.ToUniversalTime().Ticks;
        stdout = Drain(Process.StandardOutput, Path.Combine(evidenceRoot, "fixture-" + Process.Id + ".stdout.log"));
        stderr = Drain(Process.StandardError, Path.Combine(evidenceRoot, "fixture-" + Process.Id + ".stderr.log"));
    }
    static async Task Drain(StreamReader source, string destination) {
        await using var output = File.CreateText(destination);
        while (await source.ReadLineAsync() is { } line) { await output.WriteLineAsync(line); await output.FlushAsync(); }
    }
    public async Task Send(string command) { await Process.StandardInput.WriteLineAsync(command); await Process.StandardInput.FlushAsync(); }
    public async ValueTask DisposeAsync() {
        if (!Process.HasExited) {
            try { await Send("/off"); await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
            catch {
                // Cleanup is limited to the exact process created by this fixture.
                var identity = new ProcessIdentity(Process.Id, started, executable);
                if (identity.IsCurrent()) Process.Kill(entireProcessTree: false);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        Process.Dispose();
    }
}

static class Helpers {
    public static JsonElement[] Events(string path) {
        if (!File.Exists(path)) return [];
        // A concurrent writer can leave its final line incomplete for one poll.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines.Select(line => {
            try { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }
            catch (JsonException) { return default; }
        }).Where(value => value.ValueKind == JsonValueKind.Object).ToArray();
    }
    public static async Task Until(Func<bool> ready, int milliseconds = 15000) {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { if (ready()) return; await Task.Delay(50); }
        throw new TimeoutException("Fixture observation timed out");
    }
    public static async Task<Reply> QueueIdle(HostClient client, int milliseconds = 25000) {
        var elapsed = Stopwatch.StartNew();
        Reply state;
        do {
            state = await client.Send(new("list"));
            if (state.StartupQueue?.Active != true && state.StopQueue?.Active != true) return state;
            await Task.Delay(100);
        } while (elapsed.ElapsedMilliseconds < milliseconds);
        throw new TimeoutException("Host queue did not settle");
    }
    public static string[] Dlls(string root) {
        var files = new List<string>();
        void Visit(string directory) {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string file in Directory.GetFiles(directory, "*.dll"))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) files.Add(file);
            foreach (string child in Directory.GetDirectories(directory)) Visit(child);
        }
        Visit(root); return files.ToArray();
    }
    public static string[] PluginFiles(RuntimeLaunch launch) =>
        new[] { "plugins", "ServerPlugins" }.Select(folder => Path.Combine(launch.Directory, folder))
            .Where(Directory.Exists).SelectMany(Dlls).ToArray();
    public static string FindPlugin(RuntimeLaunch launch, PluginArtifact artifact) =>
        PluginFiles(launch).Single(path => string.Equals(Path.GetFileName(path), artifact.FileName, StringComparison.OrdinalIgnoreCase)
            && Hashes.FileSha(path) == artifact.Sha256);
    public static bool Rejected(Action action, out string reason) {
        try { action(); reason = "accepted"; return false; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            reason = e.GetType().Name + ": " + e.Message; return true;
        }
    }
    public static bool SamePath(string? left, string? right) => left != null && right != null &&
        string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public static string[] ProcessesUnder(string root) {
        var matches = new List<string>();
        foreach (var process in Process.GetProcesses()) using (process) {
            try {
                string? path = process.MainModule?.FileName;
                if (!process.HasExited && path != null && path.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    matches.Add(process.Id + ":" + path);
            } catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        return matches.ToArray();
    }
}

sealed record FileIdentity(uint Volume, uint IndexHigh, uint IndexLow, uint Links) {
    public bool SameFile(FileIdentity other) => Volume == other.Volume && IndexHigh == other.IndexHigh && IndexLow == other.IndexLow;
}
static class NativeFiles {
    [StructLayout(LayoutKind.Sequential)] struct Information {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    public static FileIdentity Id(string path) {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.Volume, info.IndexHigh, info.IndexLow, info.Links);
    }
}

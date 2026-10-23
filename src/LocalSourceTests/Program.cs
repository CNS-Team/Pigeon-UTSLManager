using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Pigeon.Manager;

if (args.Length is < 3 or > 4 || args[1] is not ("prepare" or "run" or "inspect" or "stop" or "link-desktop")
    || (args[1] == "link-desktop") != (args.Length == 4)) {
    Console.Error.WriteLine("Manager.LocalSourceTests <marked-local-manager-root> <prepare|run|inspect|stop|link-desktop> <new-evidence-directory> [empty-desktop-manager-root]");
    return 2;
}
string root = Path.GetFullPath(args[0]), evidence = Path.GetFullPath(args[2]);
if (!File.Exists(Path.Combine(root, ".local-source-test")) || root.StartsWith(@"\\") || Directory.Exists(evidence))
    throw new IOException("Only explicitly marked local copies and a new evidence directory are accepted");
Directory.CreateDirectory(evidence);
var workspace = new Workspace(root);
var profiles = workspace.Load();
if (profiles.Count != 2 || profiles.Any(p => p.ExistingDirectory != null || p.AutoStart
    || p.Id is not ("local-pigeongame" or "local-pigeonlive")
    || !p.Ports.All(n => n is 22020 or 22021 or 27877 or 27878))) throw new IOException("Local source roster mismatch");
var client = new HostClient(workspace, Path.Combine(workspace.App, "host", "Pigeon.UTSLManager.Host.exe"));
if (args[1] != "link-desktop") await client.EnsureStarted();
var checks = new List<object>();
int failed = 0;
void Check(string name, bool pass, object? detail = null) {
    checks.Add(new { name, pass, detail }); if (!pass) failed++;
    Console.WriteLine((pass ? "PASS " : "FAIL ") + name);
}
void Write(string name, object value) => File.WriteAllText(Path.Combine(evidence, name), Json.Encode(value));
async Task<Reply> Request(Request request) {
    var reply = await client.Send(request, 120_000);
    return reply;
}
async Task<Reply> Settled(int seconds) {
    var timer = Stopwatch.StartNew(); long last = -15;
    while (true) {
        var state = await Request(new("list"));
        if (timer.Elapsed.TotalSeconds - last >= 15) {
            last = (long)timer.Elapsed.TotalSeconds;
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} " + string.Join(" | ", state.Servers!.Select(s => s.Profile.Id + ":" + s.State)));
        }
        if (state.StartupQueue?.Active != true && state.StopQueue?.Active != true) return state;
        if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Local lifecycle queue timeout");
        await Task.Delay(500);
    }
}
async Task<JsonElement> Get(ServerProfile profile, string path) {
    using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.ServerDirectory(profile), "config", "TShockAPI", "config.json")));
    var settings = config.RootElement;
    if (settings.TryGetProperty("Settings", out var nested)) settings = nested;
    int port = settings.GetProperty("Rest的端口").GetInt32();
    string token = settings.GetProperty("Rest外部应用令牌字典").EnumerateObject().First().Name;
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    // Never persist request URLs containing the local authentication token.
    using var response = await http.GetAsync($"http://127.0.0.1:{port}/{path}{(path.Contains('?') ? '&' : '?')}token={Uri.EscapeDataString(token)}");
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return document.RootElement.Clone();
}
Write("inputs.json", new { at = DateTimeOffset.Now, root, action = args[1], profiles,
    core = new { path = typeof(Workspace).Assembly.Location, sha256 = Hashes.FileSha(typeof(Workspace).Assembly.Location) },
    scope = "LOCAL_COPIED_UTSL_FILES_AND_HTTP_CONSOLES_ONLY_NOT_GAME_CLIENT_E2E" });
try {
    if (args[1] == "prepare") {
        foreach (var profile in profiles) {
            var plugins = await Request(new("runtime-import-current", profile.Id));
            Write(profile.Id + "-plugin-import.json", plugins);
            Check(profile.Id + "/plugins-imported", plugins.Ok, plugins.Message);
            if (!plugins.Ok) continue;
            var engine = await Request(new("engine-import-current", profile.Id));
            Write(profile.Id + "-engine-import.json", engine);
            Check(profile.Id + "/engine-imported", engine.Ok, engine.Message);
            if (!engine.Ok) continue;
            var selection = plugins.RuntimeSelection! with { Enabled = true, EngineRevision = engine.Path };
            var saved = await Request(new("runtime-selection-save", profile.Id, Json.Encode(selection)));
            Check(profile.Id + "/selection-pinned", saved.Ok, saved.Message);
            var preview = await Request(new("runtime-preview", profile.Id));
            Write(profile.Id + "-prepare.json", preview);
            Check(profile.Id + "/sandbox-prepared", preview.Ok, preview.Message);
            if (!preview.Ok) await Request(new("runtime-selection-save", profile.Id, Json.Encode(selection with { Enabled = false })));
        }
    } else if (args[1] == "link-desktop") {
        var target = new Workspace(args[3]);
        if (target.Root.StartsWith(@"\\") || target.Load().Count != 0
            || !File.Exists(Path.Combine(target.Root, "UTSl-Manager.exe")))
            throw new IOException("Only an empty local desktop manager roster may be linked");
        var desktop = new HostClient(target, Path.Combine(target.App, "host", "Pigeon.UTSLManager.Host.exe"));
        await desktop.EnsureStarted();
        void RequireLink(string name, bool pass, object? detail = null) {
            Check(name, pass, detail);
            if (!pass) throw new IOException(name);
        }
        bool SamePath(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
            && Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        bool SameSelection(RuntimeSelection? actual, RuntimeSelection expected) => actual is { Plugins: not null }
            && actual.Enabled == expected.Enabled && actual.EngineRevision == expected.EngineRevision
            && actual.Plugins.OrderBy(p => p.Sha256, StringComparer.Ordinal).SequenceEqual(
                expected.Plugins.OrderBy(p => p.Sha256, StringComparer.Ordinal));
        bool MatchesFile(string path, long bytes, string sha256) => File.Exists(path)
            && new FileInfo(path).Length == bytes && Hashes.FileSha(path) == sha256;
        var prepared = new List<RuntimeLaunch>();
        foreach (var profile in profiles) {
            string source = workspace.ServerDirectory(profile);
            var registered = await desktop.Send(new("register-existing", Value: source), 120_000);
            Check(profile.Id + "/desktop-reference-registered", registered.Ok);
            if (!registered.Ok) continue;
            var linked = registered.Servers!.Single(s => s.Profile.ExistingDirectory == source).Profile;
            linked = linked with { Name = profile.Name, Arguments = profile.Arguments, Ports = profile.Ports,
                HeadlessWorlds = profile.HeadlessWorlds, AutoStart = false, StartupTimeoutSeconds = profile.StartupTimeoutSeconds };
            var savedProfile = await desktop.Send(new("save-profile", linked.Id, Profile: linked), 120_000);
            Check(profile.Id + "/desktop-profile-saved", savedProfile.Ok);
            if (!savedProfile.Ok) continue;
            var plugins = await desktop.Send(new("runtime-import-current", linked.Id), 120_000);
            var engine = await desktop.Send(new("engine-import-current", linked.Id), 120_000);
            Check(profile.Id + "/desktop-code-imported", plugins.Ok && engine.Ok);
            if (!plugins.Ok || !engine.Ok) continue;
            var selection = plugins.RuntimeSelection! with { Enabled = true, EngineRevision = engine.Path };
            var saved = await desktop.Send(new("runtime-selection-save", linked.Id, Json.Encode(selection)), 120_000);
            RequireLink(profile.Id + "/desktop-selection-roundtrip", saved.Ok && selection.EngineRevision is not null
                && SameSelection(saved.RuntimeSelection, selection));
            var preview = await desktop.Send(new("runtime-preview", linked.Id), 120_000);
            Check(profile.Id + "/desktop-sandbox-ready", saved.Ok && preview.Ok);
            if (!preview.Ok) continue;
            RequireLink(profile.Id + "/desktop-preview-path", !string.IsNullOrWhiteSpace(preview.Path)
                && Path.IsPathFullyQualified(preview.Path));
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(preview.Path!));
            string generation = Path.GetFileName(directory);
            string expectedDirectory = Workspace.Under(target.Root, "Runtime/" + linked.Id + "/" + generation);
            RequireLink(profile.Id + "/desktop-generation-isolated", Guid.TryParseExact(generation, "N", out _)
                && SamePath(directory, expectedDirectory) && !SamePath(directory, source)
                && prepared.All(p => p.GenerationId != generation && !SamePath(p.Directory, directory)), directory);
            string manifestPath = Workspace.Under(directory, "runtime-manifest.json");
            var manifest = Json.Decode<RuntimeManifest>(File.ReadAllText(manifestPath));
            RequireLink(profile.Id + "/desktop-manifest-identity", manifest.GenerationId == generation
                && manifest.ProfileId == linked.Id && SamePath(manifest.ServerDirectory, source)
                && manifest.Executable == linked.Executable);
            RequireLink(profile.Id + "/desktop-manifest-selection", manifest.EngineRevision == selection.EngineRevision
                && SameSelection(new RuntimeSelection(true, manifest.Plugins, manifest.EngineRevision), selection));
            RequireLink(profile.Id + "/desktop-manifest-unique-paths", manifest.Files.Select(f =>
                Workspace.Under(directory, f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == manifest.Files.Length);
            var engines = new EngineLibrary(target);
            var selectedEngine = engines.Resolve(selection.EngineRevision!);
            var engineManifest = engines.Manifest(selection.EngineRevision!);
            RequireLink(profile.Id + "/desktop-selected-engine-directory", SamePath(manifest.EngineDirectory, selectedEngine.Directory)
                && engineManifest.Revision == selection.EngineRevision && engineManifest.Executable == linked.Executable);
            RequireLink(profile.Id + "/desktop-selected-engine-payload", engineManifest.Files.All(expected => {
                var files = manifest.Files.Where(f => f.Path.Equals(expected.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
                return files.Length == 1 && files[0].Bytes == expected.Bytes && files[0].Sha256 == expected.Sha256
                    && SamePath(files[0].Source, Workspace.Under(selectedEngine.Directory, expected.Path))
                    && MatchesFile(Workspace.Under(directory, expected.Path), expected.Bytes, expected.Sha256);
            }));
            var selectedFiles = new List<RuntimeFile>();
            foreach (var artifact in selection.Plugins) {
                string stored = Workspace.Under(target.Library, ".store/" + artifact.Sha256 + "/" + artifact.FileName);
                var files = manifest.Files.Where(f => SamePath(f.Source, stored)).ToArray();
                RequireLink(profile.Id + "/desktop-selected-plugin/" + artifact.Name, files.Length == 1);
                var file = files[0];
                string[] parts = file.Path.Replace('\\', '/').Split('/');
                RequireLink(profile.Id + "/desktop-plugin-path-and-bytes/" + artifact.Name,
                    parts.Length is 2 or 3 && parts[0].Equals("plugins", StringComparison.OrdinalIgnoreCase)
                    && parts[^1].Equals(artifact.FileName, StringComparison.OrdinalIgnoreCase)
                    && file.Bytes == artifact.Bytes && file.Sha256 == artifact.Sha256
                    && file.Mode is "verified-hardlink" or "verified-copy"
                    && MatchesFile(stored, artifact.Bytes, artifact.Sha256)
                    && MatchesFile(Workspace.Under(directory, file.Path), artifact.Bytes, artifact.Sha256), file);
                selectedFiles.Add(file);
            }
            RequireLink(profile.Id + "/desktop-persistent-mappings-own-source", manifest.Mappings.All(mapping =>
                SamePath(mapping.Target, Workspace.Under(source, mapping.Path))));
            prepared.Add(new RuntimeLaunch(directory, Workspace.Under(directory, linked.Executable), manifestPath, generation));
            Write(profile.Id + "-desktop-link.json", new { profile = linked, engine = engine.Path, selection, saved, preview,
                manifestPath, manifestSha256 = Hashes.FileSha(manifestPath), manifest, selectedFiles });
        }
        var state = await desktop.Send(new("list"));
        Write("desktop-state.json", state);
        Check("desktop/two-distinct-prepared-generations", prepared.Count == profiles.Count
            && prepared.Select(p => p.GenerationId).Distinct(StringComparer.Ordinal).Count() == profiles.Count);
        Check("desktop/two-local-references-and-no-start", state.Ok && state.Servers?.Length == profiles.Count
            && state.StartupQueue?.Active != true && state.StopQueue?.Active != true
            && state.Servers.All(s => s.State == "STOPPED" && s.Identity == null && !s.Profile.AutoStart)
            && profiles.All(p => state.Servers.Count(s => SamePath(workspace.ServerDirectory(p), s.Profile.ExistingDirectory)) == 1));
    } else if (args[1] == "stop") {
        var stop = await Request(new("stop-all")); Write("stop-request.json", stop);
        var stopped = await Settled(240); Write("stopped.json", stopped);
        Check("local/all-stopped", stopped.Servers!.All(s => s.Identity == null));
        Check("local/host-can-exit", (await Request(new("shutdown-host"))).Ok);
    } else {
        if (args[1] == "run") {
            var start = await Request(new("start-all")); Write("start-request.json", start);
            Check("local/start-accepted", start.Ok);
            var ready = await Settled(680); Write("ready.json", ready);
            Check("local/both-servers-ready", ready.Servers!.All(s => s.State == "RUNNING" && s.Identity?.IsCurrent() == true));
        }
        var initial = await Request(new("list"));
        foreach (var state in initial.Servers!) {
            Check(state.Profile.Id + "/exact-process-alive", state.State == "RUNNING" && state.Identity?.IsCurrent() == true);
            if (state.State != "RUNNING") continue;
            string id = state.Profile.Id;
            var worlds = await Get(state.Profile, "world/list"); Write(id + "-worlds.json", worlds);
            var worldEntries = worlds.GetProperty("worlds").EnumerateArray().ToArray();
            Check(id + "/three-running-worlds", worlds.GetProperty("status").ToString() == "200" && worldEntries.Length == 3 && worldEntries.All(w => w.GetProperty("running").GetBoolean()));
            foreach (var item in worldEntries) {
                string name = item.GetProperty("name").GetString()!;
                var status = await Get(state.Profile, "v2/server/status?server=" + Uri.EscapeDataString(name));
                Write(id + "-world-" + item.GetProperty("index") + ".json", status);
                Check(id + "/status/" + name, status.GetProperty("status").ToString() == "200" && status.GetProperty("name").GetString() == name);
            }
            var health = await Get(state.Profile, "pgameapi/health"); Write(id + "-health.json", health);
            var admission = await Get(state.Profile, "pgameapi/admission/status"); Write(id + "-admission.json", admission);
            Check(id + "/health-200", health.GetProperty("status").ToString() == "200");
            var consoles = await Request(new("world-consoles", id)); Write(id + "-consoles.json", consoles);
            Check(id + "/three-world-consoles", consoles.Ok && consoles.Worlds?.Length == 3);
            foreach (var world in consoles.Worlds ?? []) {
                var before = await Request(new("world-consoles", id, World: world.Name));
                long cursor = before.Log?.Cursor ?? 0;
                var sent = await Request(new("world-send", id, "/help", World: world.Name));
                Check(id + "/help-accepted/" + world.Name, sent.Ok);
                var timer = Stopwatch.StartNew();
                Reply log;
                do {
                    await Task.Delay(200);
                    log = await Request(new("world-consoles", id, Cursor: cursor, World: world.Name));
                } while (timer.ElapsedMilliseconds < 10_000 && log.Log?.Chunks.Any(c => c.Sequence > cursor && !string.IsNullOrWhiteSpace(c.Text)) != true);
                Write(id + "-help-" + world.Index + ".json", log);
                Check(id + "/new-help-output/" + world.Name, log.Ok && log.Log?.Cursor > cursor
                    && log.Log.Chunks.Any(c => c.Sequence > cursor && !string.IsNullOrWhiteSpace(c.Text)));
            }
        }
        if (args[1] == "run" && initial.Servers!.All(s => s.State == "RUNNING")) {
            Console.WriteLine("Independent local same-identity observation: 65 seconds");
            await Task.Delay(65_000);
            var later = await Request(new("list")); Write("after-65-seconds.json", later);
            foreach (var state in initial.Servers!) Check(state.Profile.Id + "/same-identity-after-65s",
                later.Servers!.Single(s => s.Profile.Id == state.Profile.Id).Identity == state.Identity && state.Identity!.IsCurrent());
        }
    }
} catch (Exception error) {
    Check("unexpected-exception", false, error.GetType().Name);
    // Exception URLs can include REST tokens. Persist only the type here.
}
Write("result.json", new { at = DateTimeOffset.Now, action = args[1], passed = checks.Count - failed, failed, checks,
    gameClientE2E = "UNVERIFIED", productionWrites = 0, productionStarts = 0, productionStops = 0, gameConnections = 0 });
Console.WriteLine($"RESULT {checks.Count - failed}P/{failed}F: {evidence}");
return failed == 0 ? 0 : 1;

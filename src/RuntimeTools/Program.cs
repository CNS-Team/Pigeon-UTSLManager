using Pigeon.Manager;
using System.Text;

// The Host pipe and persisted JSON are UTF-8. Redirected stdout must use
// the same encoding instead of the machine's legacy Chinese OEM code page.
Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length < 2) {
    Console.Error.WriteLine("Manager.RuntimeTools <manager-root> <catalog|inspect|import-current|enable|disable|prepare> [profile-id]");
    return 2;
}
try {
    var workspace = new Workspace(args[0]);
    var runtime = new SharedRuntime(workspace);
    var engines = new EngineLibrary(workspace);
    // Display-label recovery changes no process, runtime selection, launch option,
    // config or game data. Exact current/backup hashes prevent replay or guessing.
    if (args[1] == "restore-profile-name" && args.Length == 6) {
        string id = args[2], source = Path.GetFullPath(args[3]);
        string current = Path.Combine(workspace.Data, "servers.json");
        string expectedCurrent = args[4], expectedBackup = args[5];
        if (Hashes.FileSha(current) != expectedCurrent || Hashes.FileSha(source) != expectedBackup) throw new IOException("PROFILE_REPAIR_INPUT_HASH_CHANGED");
        var repairClient = new HostClient(workspace, Path.Combine(workspace.App, "host", "Pigeon.UTSLManager.Host.exe"));
        var before = await repairClient.Send(new("list"));
        if (!before.Ok || before.Servers is null || before.StartupQueue?.Active == true || before.StopQueue?.Active == true) throw new IOException("PROFILE_REPAIR_HOST_NOT_IDLE");
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(current, Encoding.UTF8))!.AsArray();
        var baseline = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(source, Encoding.UTF8))!.AsArray();
        var target = document.Single(p => p?["Id"]?.GetValue<string>() == id)!;
        string name = baseline.Single(p => p?["Id"]?.GetValue<string>() == id)!["Name"]!.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\uFFFD') || name.Any(char.IsControl)) throw new IOException("PROFILE_REPAIR_BACKUP_NAME_INVALID");
        string oldName = target["Name"]!.GetValue<string>();
        if (oldName == name) { Console.WriteLine(Json.Encode(new { ok = true, unchanged = true, name })); return 0; }
        string oldJson = document.ToJsonString();
        target["Name"] = name;
        string updated = document.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        target["Name"] = oldName;
        if (document.ToJsonString() != oldJson) throw new IOException("PROFILE_REPAIR_UNEXPECTED_SEMANTIC_CHANGE");
        string backupDirectory = Path.Combine(workspace.Backups, "Profile-name-utf8-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backupDirectory);
        string backup = Path.Combine(backupDirectory, "servers-before.json");
        string temp = current + ".name-utf8-" + Guid.NewGuid().ToString("N");
        try {
            File.WriteAllText(temp, updated, new UTF8Encoding(false));
            if (Hashes.FileSha(current) != expectedCurrent || Hashes.FileSha(source) != expectedBackup) throw new IOException("PROFILE_REPAIR_CONCURRENT_UPDATE");
            File.Replace(temp, current, backup);
            if (Hashes.FileSha(backup) != expectedCurrent) throw new IOException("PROFILE_REPAIR_BACKUP_MISMATCH");
        } finally { if (File.Exists(temp)) File.Delete(temp); }
        var after = await repairClient.Send(new("list"));
        if (!after.Ok || after.Servers?.Length != before.Servers.Length || after.Servers.Single(s => s.Profile.Id == id).Profile.Name != name) throw new IOException("PROFILE_REPAIR_READBACK_FAILED");
        foreach (var server in before.Servers)
            if (after.Servers.Single(s => s.Profile.Id == server.Profile.Id).Identity != server.Identity) throw new IOException("PROFILE_REPAIR_PROCESS_CHANGED");
        var result = new { ok = true, status = "PROFILE_LABEL_RESTORED_UTF8", id, oldName, name, backup,
            backupSha256 = Hashes.FileSha(backup), serversSha256 = Hashes.FileSha(current),
            processesUnchanged = true, serverStarts = 0, serverStops = 0, configsChanged = 0, pluginReplacements = 0,
            servers = after.Servers.Select(s => new { s.Profile.Id, s.Profile.Name, s.Identity }) };
        workspace.Audit("profile-name-restored-utf8", id, result);
        Console.WriteLine(Json.Encode(result)); return 0;
    }
    void RequireOffline() {
        if (workspace.Load().Any(p => p.AutoStart)) throw new IOException("OFFLINE_TOOL_REQUIRES_AUTOSTART_FALSE");
        foreach (var process in System.Diagnostics.Process.GetProcesses()) using (process) {
            try {
                if (process.MainModule?.FileName is { } exe && exe.StartsWith(workspace.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    && workspace.Load().Any(p => Path.GetFileName(exe).Equals(p.Executable, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("OFFLINE_TOOL_GAME_PROCESS_PRESENT");
            } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
        }
    }
    if (args[1] == "verify-layout" && args.Length == 2) {
        using var owner = new FileStream(Path.Combine(workspace.Data, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RequireOffline();
        var launches = workspace.Load().Select(p => new { p.Id, launch = runtime.Prepare(p) }).ToArray();
        Console.WriteLine(Json.Encode(new { ok = true, status = "LAYOUT_VERIFIED_STOPPED_NOT_STARTED", launches })); return 0;
    }
    if (args[1] == "clean-engine" && args.Length == 4) {
        // Explicit offline recovery only; cannot race a live Host or game queue.
        using var owner = new FileStream(Path.Combine(workspace.Data, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RequireOffline();
        var target = workspace.Load().Single(p => p.Id == args[2]);
        if (target.AutoStart) throw new IOException("CLEAN_ENGINE_REQUIRES_AUTOSTART_FALSE");
        var artifact = engines.Import(target, args[3]);
        runtime.SaveSelection(target, runtime.GetSelection(target) with { EngineRevision = artifact.Revision });
        Console.WriteLine(Json.Encode(new { ok = true, artifact })); return 0;
    }
    if (args[1] == "world-plan") { Console.WriteLine(Json.Encode(new { ok = true, worlds = new WorkspaceMigration(workspace).PlanWorlds() })); return 0; }
    if (args[1] == "migrate-layout") { Console.WriteLine(Json.Encode(new WorkspaceMigration(workspace).Apply())); return 0; }
    if (args[1] is "request" or "control" && args.Length == 3) {
        var control = new HostClient(workspace, Path.Combine(workspace.App, "host", "Pigeon.UTSLManager.Host.exe"));
        var reply = await control.Send(args[1] == "control" ? new Request(args[2]) : Json.Decode<Request>(args[2]), 600_000);
        Console.WriteLine(Json.Encode(reply)); return reply.Ok ? 0 : 1;
    }
    if (args[1] == "catalog") {
        Console.WriteLine(Json.Encode(new { ok = true, plugins = runtime.Catalog(), engines = engines.Catalog() })); return 0;
    }
    if (args.Length != 3) throw new IOException("此操作需要明确的服务器 ID");
    var profile = workspace.Load().SingleOrDefault(p => p.Id == args[2]) ?? throw new IOException("服务器不存在");
    if (args[1] == "inspect") {
        Console.WriteLine(Json.Encode(new { ok = true, profile, selection = runtime.GetSelection(profile) })); return 0;
    }
    // All mutations use the live Host gates and exact process ownership checks.
    // The CLI never opens a game process or bypasses the manager lifecycle.
    var client = new HostClient(workspace, Path.Combine(workspace.App, "host", "Pigeon.UTSLManager.Host.exe"));
    if (args[1] == "import-current") {
        var plugins = await client.Send(new("runtime-import-current", profile.Id), 600_000);
        if (!plugins.Ok) throw new IOException(plugins.Message);
        var engine = await client.Send(new("engine-import-current", profile.Id), 600_000);
        if (!engine.Ok) throw new IOException(engine.Message);
        var selection = plugins.RuntimeSelection! with { EngineRevision = engine.Path };
        var saved = await client.Send(new("runtime-selection-save", profile.Id, Json.Encode(selection)), 600_000);
        Console.WriteLine(Json.Encode(saved)); return saved.Ok ? 0 : 1;
    }
    if (args[1] is "enable" or "disable") {
        var selection = await client.Send(new("runtime-selection", profile.Id));
        if (!selection.Ok || selection.RuntimeSelection == null) throw new IOException(selection.Message);
        var saved = await client.Send(new("runtime-selection-save", profile.Id,
            Json.Encode(selection.RuntimeSelection with { Enabled = args[1] == "enable" })), 600_000);
        Console.WriteLine(Json.Encode(saved)); return saved.Ok ? 0 : 1;
    }
    if (args[1] == "prepare") {
        var reply = await client.Send(new("runtime-preview", profile.Id), 600_000);
        Console.WriteLine(Json.Encode(reply)); return reply.Ok ? 0 : 1;
    }
    throw new IOException("未知操作");
} catch (Exception e) {
    Console.WriteLine(Json.Encode(new { ok = false, error = e.GetType().Name, message = e.Message })); return 1;
}

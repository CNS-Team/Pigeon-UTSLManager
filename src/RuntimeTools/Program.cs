using Pigeon.Manager;

if (args.Length < 2) {
    Console.Error.WriteLine("Manager.RuntimeTools <manager-root> <catalog|inspect|import-current|enable|disable|prepare> [profile-id]");
    return 2;
}
try {
    var workspace = new Workspace(args[0]);
    var runtime = new SharedRuntime(workspace);
    var engines = new EngineLibrary(workspace);
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

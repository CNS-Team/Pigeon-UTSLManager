using System.Diagnostics;
using Pigeon.Manager;

namespace RuntimeChecks;
sealed class LocalHost : IAsyncDisposable {
    public HostClient Client { get; }
    public Process Process { get; }
    readonly string executable;
    readonly long started;
    public LocalHost(Workspace workspace, string host, string events) {
        executable = host;
        var start = new ProcessStartInfo(host) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(host)!
        };
        start.ArgumentList.Add("--root"); start.ArgumentList.Add(workspace.Root);
        start.Environment["RUNTIME_FIXTURE_EVENTS"] = events;
        Process = Process.Start(start)!; started = Process.StartTime.ToUniversalTime().Ticks;
        Client = new(workspace, host);
    }
    public async Task Ready() {
        for (int attempt = 0; attempt < 60; attempt++) {
            if (Process.HasExited) throw new IOException("Host exited before ready");
            try { if ((await Client.Send(new("ping"), 300)).Ok) return; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Local Host not ready");
    }
    public async ValueTask DisposeAsync() {
        if (!Process.HasExited) {
            try {
                var state = await Helpers.QueueIdle(Client);
                foreach (var server in state.Servers ?? [])
                    if (server.State != "EXTERNAL" && server.Identity != null) await Client.Send(new("stop", server.Profile.Id), 35000);
                var reply = await Client.Send(new("shutdown-host"));
                if (!reply.Ok) throw new IOException("Host shutdown refused: " + reply.Message);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            } finally {
                var identity = new ProcessIdentity(Process.Id, started, executable);
                if (identity.IsCurrent()) Process.Kill(false);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Process.Dispose();
    }
}

static class HostScenarios {
    public static async Task Run(Evidence e, string output, string host, string package, string v1, string v2) {
        await e.Scenario("host/two-servers", async () => {
            var f = new ServerFixture(Path.Combine(output, "host-two-servers"), package);
            Directory.CreateDirectory(Path.Combine(f.Source(0), "plugins"));
            File.Copy(v1, Path.Combine(f.Source(0), "plugins", "RuntimeFixture.Plugin.dll"));
            await using var running = new LocalHost(f.Workspace, host, f.Events);
            await running.Ready(); var client = running.Client;
            var imported = await client.Send(new("runtime-import-current", f.Profiles[0].Id));
            e.Require("host/import-current-default-disabled", imported.Ok && imported.RuntimeSelection?.Enabled == false && imported.RuntimeSelection.Plugins.Length == 1, imported);
            var added = await client.Send(new("shared-library-import", Files: [f.ImportPluginSource(v2, "host-v2")]));
            e.Require("host/library-import", added.Ok && added.Artifacts?.Length == 2, added);
            var library = await client.Send(new("shared-library-list"));
            e.Check("host/library-list-all-versions", library.Ok && library.Artifacts?.Length == 2);
            var one = library.Artifacts!.Single(x => x.Sha256 == Hashes.FileSha(v1));
            var two = library.Artifacts!.Single(x => x.Sha256 == Hashes.FileSha(v2));
            for (int index = 0; index < 2; index++) {
                var selection = new RuntimeSelection(true, [index == 0 ? one : two]);
                var saved = await client.Send(new("runtime-selection-save", f.Profiles[index].Id, Json.Encode(selection)));
                var read = await client.Send(new("runtime-selection", f.Profiles[index].Id));
                e.Require("host/selection-roundtrip-" + index, saved.Ok && read.Ok &&
                    read.RuntimeSelection?.Enabled == true && read.RuntimeSelection.Plugins.Single().Sha256 == selection.Plugins[0].Sha256);
                var engine = await client.Send(new("engine-import-current", f.Profiles[index].Id));
                e.Check("host/engine-import-current-" + index, engine.Ok && engine.Engines?.Length >= 1);
            }
            var preview = await client.Send(new("runtime-preview", f.Profiles[0].Id), 15000);
            e.Check("host/preview-no-process", preview.Ok && Directory.Exists(preview.Path) && f.ReadEvents().Length == 0
                && Helpers.ProcessesUnder(f.Workspace.Root).Length == 0, preview);
            e.Require("host/start-all-accepted", (await client.Send(new("start-all"))).Ok);
            var state = await Helpers.QueueIdle(client);
            e.Require("host/two-sandboxes-running", state.Servers?.Length == 2 && state.Servers.All(s => s.State == "RUNNING" && s.Identity?.IsCurrent() == true), state);
            var a = state.Servers![0]; var b = state.Servers[1];
            foreach (var server in state.Servers) {
                e.Check("host/correct-generation-exe-" + server.Profile.Id,
                    Helpers.SamePath(server.Identity!.Executable, Path.Combine(server.RuntimeDirectory!, server.Profile.Executable))
                    && !Helpers.SamePath(server.Identity.Executable, f.Workspace.ServerExecutable(server.Profile))
                    && File.Exists(server.RuntimeManifest), server);
                var start = f.ReadEvents().Single(x => x.GetProperty("action").GetString() == "start" && x.GetProperty("pid").GetInt32() == server.Identity.Pid);
                e.Check("host/correct-working-directory-" + server.Profile.Id, Helpers.SamePath(start.GetProperty("cwd").GetString(), server.RuntimeDirectory));
                string expectedVersion = server.Profile.Id == f.Profiles[0].Id ? "v1:A:1" : "v2:B:1";
                e.Check("host/actual-selected-DLL-executed-" + server.Profile.Id, start.GetProperty("loaded").EnumerateArray().Single().GetProperty("result").GetString() == expectedVersion, start);
            }
            e.Check("host/duplicate-owned-start-rejected", !(await client.Send(new("start", a.Profile.Id))).Ok);
            e.Check("host/running-selection-refused", !(await client.Send(new("runtime-selection-save", a.Profile.Id, Json.Encode(new RuntimeSelection(true, [two]))))).Ok);
            e.Check("host/running-preview-refused", !(await client.Send(new("runtime-preview", a.Profile.Id))).Ok);
            e.Check("host/exact-A-command-accepted", (await client.Send(new("send", a.Profile.Id, "only-host-A\r", Identity: a.Identity))).Ok);
            await Helpers.Until(() => f.ReadEvents().Any(x => x.GetProperty("action").GetString() == "command" && x.GetProperty("command").GetString() == "only-host-A"));
            e.Check("host/command-does-not-reach-B", !f.ReadEvents().Any(x => x.GetProperty("action").GetString() == "command"
                && x.GetProperty("server").GetString() == "B" && x.GetProperty("command").GetString() == "only-host-A"));
            e.Check("host/wrong-identity-stop-rejected", !(await client.Send(new("force-stop", a.Profile.Id, Identity: b.Identity))).Ok && a.Identity!.IsCurrent() && b.Identity!.IsCurrent());
            var stopA = await client.Send(new("stop", a.Profile.Id), 35000);
            e.Check("host/stop-A-preserves-B", stopA.Ok && !a.Identity!.IsCurrent() && b.Identity!.IsCurrent(), stopA);
            var beforeB = f.ReadEvents().Count(x => x.GetProperty("action").GetString() == "save" && x.GetProperty("server").GetString() == "B");
            e.Check("host/stop-A-does-not-save-B", beforeB == 0);
            e.Check("host/B-command-after-A-stopped", (await client.Send(new("send", b.Profile.Id, "B-still-alive\r", Identity: b.Identity))).Ok);
            await Helpers.Until(() => f.ReadEvents().Any(x => x.GetProperty("action").GetString() == "command" && x.GetProperty("command").GetString() == "B-still-alive"));
            e.Check("host/stop-B", (await client.Send(new("stop", b.Profile.Id), 35000)).Ok && !b.Identity!.IsCurrent());
            e.Check("host/start-after-stop-new-generation", (await client.Send(new("start", a.Profile.Id))).Ok);
            var restarted = (await Helpers.QueueIdle(client)).Servers!.Single(s => s.Profile.Id == a.Profile.Id);
            e.Check("host/restart-new-PID-and-generation", restarted.State == "RUNNING" && restarted.Identity!.Pid != a.Identity!.Pid
                && restarted.RuntimeDirectory != a.RuntimeDirectory);
            e.Check("host/restart-persistent-writes", File.ReadAllText(Path.Combine(f.Source(0), "config", "fixture-A.txt")) == "2"
                && File.ReadAllText(Path.Combine(f.Source(0), "Data", "fixture-A.txt")) == "2" && File.ReadAllText(Path.Combine(f.Source(0), "world", "fixture-A.wld")) == "2");
            await client.Send(new("stop", a.Profile.Id), 35000);
            e.Write("host-states.json", new { first = state, restarted });
        });

        await e.Scenario("host/prepare-failure", async () => {
            var f = new ServerFixture(Path.Combine(output, "host-prepare-failure"), package);
            var artifact = f.Import(v1); f.Select(0, artifact); f.Select(1, artifact);
            string stored = Helpers.Dlls(f.Workspace.Library).Single(path => Hashes.FileSha(path) == artifact.Sha256);
            File.AppendAllText(stored, "tampered");
            await using var running = new LocalHost(f.Workspace, host, f.Events); await running.Ready();
            e.Check("host/failed-preview-refused", !(await running.Client.Send(new("runtime-preview", f.Profiles[0].Id))).Ok);
            e.Require("host/failed-start-request-admitted", (await running.Client.Send(new("start-all"))).Ok);
            var state = await Helpers.QueueIdle(running.Client);
            e.Check("host/prepare-failure-no-process-and-no-second-server", state.Servers!.All(s => s.Identity == null)
                && f.ReadEvents().Length == 0 && Helpers.ProcessesUnder(f.Workspace.Root).Length == 0
                && !File.Exists(Path.Combine(f.Workspace.Data, "runs", f.Profiles[0].Id + ".json")), state);
            e.Check("host/prepare-failure-audited", Helpers.Events(Path.Combine(f.Workspace.Data, "actions.jsonl"))
                .Any(x => x.GetProperty("action").GetString() == "startup-queue-failed"));
        });

        await e.Scenario("host/external-sandbox", async () => {
            var f = new ServerFixture(Path.Combine(output, "host-external"), package, 1);
            f.Select(0, f.Import(v1)); var launch = f.Prepare(0);
            await using var running = new LocalHost(f.Workspace, host, f.Events); await running.Ready();
            await using (var external = new FixtureProcess(launch, f.Events, output)) {
                await Helpers.Until(() => f.ReadEvents().Any(x => x.GetProperty("action").GetString() == "start"));
                await Task.Delay(1000);
                var state = (await running.Client.Send(new("list"))).Servers!.Single();
                e.Check("host/external-generation-recognized", state.State == "EXTERNAL" && state.Identity?.Pid == external.Process.Id
                    && Helpers.SamePath(state.Identity.Executable, launch.Executable), state);
                e.Check("host/external-start-refused", !(await running.Client.Send(new("start", f.Profiles[0].Id))).Ok);
                e.Check("host/external-stop-not-owned", !(await running.Client.Send(new("stop", f.Profiles[0].Id), 10000)).Ok && !external.Process.HasExited);
                e.Check("host/external-selection-refused", !(await running.Client.Send(new("runtime-selection-save", f.Profiles[0].Id, Json.Encode(new RuntimeSelection(false, []))))).Ok);
                e.Check("host/external-preview-refused", !(await running.Client.Send(new("runtime-preview", f.Profiles[0].Id))).Ok);
                e.Check("host/external-no-extra-starts", f.ReadEvents().Count(x => x.GetProperty("action").GetString() == "start") == 1);
            }
        });

        await e.Scenario("host/cross-workspace-source-owner", async () => {
            var f = new ServerFixture(Path.Combine(output, "host-owner-A"), package, 1);
            f.Select(0, f.Import(v1));
            var other = new Workspace(Path.Combine(output, "host-owner-B"));
            var registered = other.RegisterExisting(f.Source(0));
            await using var first = new LocalHost(f.Workspace, host, f.Events); await first.Ready();
            await using var second = new LocalHost(other, host, Path.Combine(other.Root, "fixture-events.jsonl")); await second.Ready();
            e.Require("owner/first-workspace-start", (await first.Client.Send(new("start", f.Profiles[0].Id))).Ok);
            var owned = (await Helpers.QueueIdle(first.Client)).Servers!.Single();
            e.Require("owner/first-running", owned.State == "RUNNING" && owned.Identity?.IsCurrent() == true, owned);
            await Task.Delay(1000);
            var external = (await second.Client.Send(new("list"))).Servers!.Single();
            e.Check("owner/second-sees-sandbox-as-EXTERNAL", external.State == "EXTERNAL" && external.Identity == owned.Identity, external);
            e.Check("owner/second-cannot-start", !(await second.Client.Send(new("start", registered.Id))).Ok);
            e.Check("owner/second-cannot-stop", !(await second.Client.Send(new("stop", registered.Id), 10000)).Ok && owned.Identity!.IsCurrent());
            e.Check("owner/second-cannot-save-selection", !(await second.Client.Send(new("runtime-selection-save", registered.Id, Json.Encode(new RuntimeSelection(true, []))))).Ok);
            e.Check("owner/second-cannot-preview", !(await second.Client.Send(new("runtime-preview", registered.Id))).Ok);
            var all = await second.Client.Send(new("stop-all"));
            await Helpers.QueueIdle(second.Client);
            e.Check("owner/stop-all-does-not-stop-other-workspace", owned.Identity!.IsCurrent());
            e.Check("owner/marker-full-identity", File.Exists(Path.Combine(f.Source(0), ".manager-runtime-process.json")), owned.Identity);
            e.Check("owner/first-remains-commandable", (await first.Client.Send(new("send", f.Profiles[0].Id, "owner-A-alive\r", Identity: owned.Identity))).Ok);
            var stopped = await first.Client.Send(new("stop", f.Profiles[0].Id), 35000);
            e.Check("owner/only-owner-stops", stopped.Ok && !owned.Identity.IsCurrent());
            e.Check("owner/no-duplicate-process-started", f.ReadEvents().Count(x => x.GetProperty("action").GetString() == "start") == 1);
        });
    }
}

using System.Diagnostics;
using Pigeon.Manager;
if (args.Length is not (3 or 4)) throw new ArgumentException("tests <new-evidence-dir> <host-exe> <probe-output-dir> [ui-exe]");
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("拒绝覆盖旧证据");
Directory.CreateDirectory(output);
var workspace = new Workspace(Path.Combine(output, "隔离管理器"));
var results = new List<object>(); int passed = 0, failed = 0;
void Check(string name, bool ok) { results.Add(new { name, pass = ok }); if (ok) passed++; else failed++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); }
bool Reject(Action action) { try { action(); return false; } catch { return true; } }
Check("root-layout", new[] { "Plugins", "Servers", "Maps", "Backups", "Data" }.All(x => Directory.Exists(Path.Combine(workspace.Root, x))));
Check("path-traversal-rejected", Reject(() => Workspace.Under(workspace.Servers, "../escape")));
Check("absolute-path-rejected", Reject(() => Workspace.Under(workspace.Servers, output)));
Check("new-roster-empty-no-example-production", workspace.Load().Count == 0);
string probeOutput = Path.GetFullPath(args[2]);
string source = Path.Combine(output, "只读原始包"); Directory.CreateDirectory(source);
foreach (string file in Directory.GetFiles(probeOutput)) File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
File.Copy(Path.Combine(probeOutput, "Manager.ConsoleProbe.exe"), Path.Combine(source, "UnifierTSL.exe"));
Directory.CreateDirectory(Path.Combine(source, "config")); File.WriteAllText(Path.Combine(source, "config", "config.json"), "{\"launcher\":{\"listenPort\":29989}}");
var before = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hashes.FileSha);
var p1 = workspace.Import(source); var p2 = workspace.Import(source);
Check("same-source-two-separate-packages", p1.Folder != p2.Folder && workspace.Load().Count == 2);
Check("server-folders-zero-based-numbered", p1.Folder.StartsWith("0.") && p2.Folder.StartsWith("1."));
Check("original-package-never-mutated", before.All(p => Hashes.FileSha(p.Key) == p.Value));
Check("config-port-read-without-edit", p1.Ports.SequenceEqual([29989]));
Check("import-contained-root-rejected", Reject(() => workspace.Import(workspace.ServerDirectory(p1))));
var client = new HostClient(workspace, Path.GetFullPath(args[1])); await client.EnsureStarted();
Check("named-pipe-host-ping", (await client.Send(new("ping"))).Ok);
Check("host-no-autostart", (await client.Send(new("list"))).Servers!.All(s => s.Identity == null));
async Task<Reply> Call(string action, string? id = null, string? value = null, string[]? files = null, ProcessIdentity? identity = null) => await client.Send(new(action, id, value, Files: files, Identity: identity), 30_000);
var started1 = await Call("start", p1.Id); Check("first-owned-conpty-start", started1.Ok);
var started2 = await Call("start", p2.Id); Check("second-same-exename-independent-start", started2.Ok);
Check("duplicate-start-rejected", !(await Call("start", p1.Id)).Ok);
async Task<string> WaitText(string id, string expected) {
    string text = "";
    for (int i = 0; i < 100; i++) { var r = await client.Send(new("read", id)); text = string.Concat(r.Log!.Chunks.Select(c => c.Text)); if (text.Contains(expected)) return text; await Task.Delay(100); }
    return text;
}
string initial = await WaitText(p1.Id, "CONPTY_READY");
Check("actual-console-window-api-under-conpty", initial.Contains("CONPTY_READY") && initial.Contains("140x40"));
Check("utf8-chinese-output", initial.Contains("中文控制台"));
Check("ansi-retained-not-stripped", initial.Contains('\u001b'));
await Call("send", p1.Id, "hello-甲\r"); string echo = await WaitText(p1.Id, "ECHO:hello-甲");
Check("actual-console-stdin-roundtrip", echo.Contains("ECHO:hello-甲"));
string other = string.Concat((await client.Send(new("read", p2.Id))).Log!.Chunks.Select(c => c.Text));
Check("commands-never-broadcast-to-second-server", !other.Contains("hello-甲"));
var state1 = (await Call("list")).Servers!.Single(s => s.Profile.Id == p1.Id);
var state2 = (await Call("list")).Servers!.Single(s => s.Profile.Id == p2.Id);
Check("two-different-pids-owned", state1.Identity != null && state2.Identity != null && state1.Identity.Pid != state2.Identity.Pid);
Check("exact-process-path-and-creation-verified", state1.Identity!.IsCurrent());
var referenced = new Workspace(Path.Combine(output, "existing-reference-manager"));
var refProfile = referenced.RegisterExisting(workspace.ServerDirectory(p1));
Check("existing-registration-no-copy", referenced.ServerDirectory(refProfile) == workspace.ServerDirectory(p1) && !File.Exists(Path.Combine(referenced.Servers, refProfile.Folder, "UnifierTSL.exe")));
Check("existing-registration-duplicate-rejected", Reject(() => referenced.RegisterExisting(workspace.ServerDirectory(p1) + Path.DirectorySeparatorChar)));
Check("existing-registration-root-rejected", Reject(() => referenced.RegisterExisting(workspace.Root)));
var refClient = new HostClient(referenced, Path.GetFullPath(args[1])); await refClient.EnsureStarted();
var external = (await refClient.Send(new("list"))).Servers!.Single();
Check("existing-running-server-is-external", external.State == "EXTERNAL" && external.Identity == state1.Identity);
Check("external-duplicate-start-refused", !(await refClient.Send(new("start", refProfile.Id))).Ok);
Check("external-stop-refused", !(await refClient.Send(new("stop", refProfile.Id))).Ok);
Check("external-force-stop-refused", !(await refClient.Send(new("force-stop", refProfile.Id, Identity: state1.Identity))).Ok && state1.Identity.IsCurrent());
Check("stale-pid-identity-rejected", !new ProcessIdentity(state1.Identity.Pid, state1.Identity.StartedUtcTicks - 1, state1.Identity.Executable).IsCurrent());
Check("path-identity-mismatch-rejected", !new ProcessIdentity(state1.Identity.Pid, state1.Identity.StartedUtcTicks, state2.Identity!.Executable).IsCurrent());
workspace.AddPlugins([typeof(Workspace).Assembly.Location]); Check("plugin-metadata-without-load", workspace.Plugins().Length == 1);
Check("plugin-update-running-server-rejected", !(await Call("apply-plugins", p1.Id, files: ["Pigeon.UTSLManager.Core.dll"])).Ok);
var newClient = new HostClient(workspace, Path.GetFullPath(args[1])); await newClient.EnsureStarted();
Check("client-reconnect-keeps-same-pid", (await newClient.Send(new("list"))).Servers!.Single(s => s.Profile.Id == p1.Id).Identity == state1.Identity);
if(args.Length==4) {
    for(int cycle=1;cycle<=2;cycle++) {
        var psi=new ProcessStartInfo(Path.GetFullPath(args[3])){UseShellExecute=false};
        psi.ArgumentList.Add("--root");psi.ArgumentList.Add(workspace.Root);
        using var ui=Process.Start(psi)!;
        for(int i=0;i<100 && !ui.HasExited && ui.MainWindowHandle==IntPtr.Zero;i++){await Task.Delay(100);ui.Refresh();}
        await Task.Delay(700);
        Check("actual-ui-open-cycle-"+cycle,!ui.HasExited && ui.MainWindowHandle!=IntPtr.Zero);
        Check("ui-open-keeps-server-identity-"+cycle,(await newClient.Send(new("list"))).Servers!.Single(s=>s.Profile.Id==p1.Id).Identity==state1.Identity);
        bool close=ui.CloseMainWindow();
        using var limit=new CancellationTokenSource(10000);
        try{await ui.WaitForExitAsync(limit.Token);}catch(OperationCanceledException){close=false;}
        Check("actual-ui-close-cycle-"+cycle,close&&ui.HasExited);
        Check("actual-ui-close-keeps-both-servers-"+cycle,state1.Identity.IsCurrent()&&state2.Identity!.IsCurrent());
    }
    await newClient.Send(new("send",p1.Id,"after-ui-reopen-中文\r"));
    Check("terminal-usable-after-actual-ui-reopen",(await WaitText(p1.Id,"ECHO:after-ui-reopen-中文")).Contains("ECHO:after-ui-reopen-中文"));
}
Check("force-stop-wrong-generation-refused", !(await Call("force-stop", p1.Id, identity: state1.Identity with { StartedUtcTicks = 1 })).Ok);
var stop1 = await Call("stop", p1.Id); Check("single-graceful-off-exits", stop1.Ok && !state1.Identity.IsCurrent());
Check("other-instance-survives-stop", state2.Identity!.IsCurrent());
await Task.Delay(300);
var applied = await Call("apply-plugins", p1.Id, files: ["Pigeon.UTSLManager.Core.dll"]); Check("plugin-copy-to-stopped-only", applied.Ok);
Check("copied-plugin-exact-sha", Hashes.FileSha(Path.Combine(workspace.ServerDirectory(p1), "Plugins", "Pigeon.UTSLManager.Core.dll")) == Hashes.FileSha(typeof(Workspace).Assembly.Location));
Check("backup-manifest-recorded", Directory.GetFiles(workspace.Backups, "manifest.json", SearchOption.AllDirectories).Length == 1);
Check("cannot-stop-host-with-live-server", !(await Call("shutdown-host")).Ok);
Check("second-server-graceful-stop", (await Call("stop", p2.Id)).Ok);
Check("reference-host-shutdown-after-external-stopped", (await refClient.Send(new("shutdown-host"))).Ok);
Check("explicit-autostart-profile-save", (await client.Send(new("save-profile", p1.Id, Profile: p1 with { AutoStart = true }))).Ok);
Check("all-original-files-still-unchanged", before.All(p => Hashes.FileSha(p.Key) == p.Value));
Check("host-shutdown-only-when-no-running-servers", (await Call("shutdown-host")).Ok);
await Task.Delay(800); await client.EnsureStarted();
var autoStates = (await Call("list")).Servers!;
var auto = autoStates.Single(s => s.Profile.Id == p1.Id);
Check("explicit-autostart-only-selected-profile", auto.State == "RUNNING" && auto.Identity?.IsCurrent() == true && autoStates.Single(s => s.Profile.Id == p2.Id).Identity == null);
Check("autostart-console-usable", (await WaitText(p1.Id, "CONPTY_READY")).Contains("CONPTY_READY"));
using (var duplicate = Process.Start(new ProcessStartInfo(Path.GetFullPath(args[1])) { UseShellExecute = false, CreateNoWindow = true, Arguments = "--root \"" + workspace.Root + "\"" })!) {
    using var deadline = new CancellationTokenSource(10000); await duplicate.WaitForExitAsync(deadline.Token);
    Check("duplicate-host-refused-by-root-lock", duplicate.ExitCode == 0 && (await Call("list")).Servers!.Single(s => s.Profile.Id == p1.Id).Identity == auto.Identity);
}
Check("autostart-server-stopped", (await Call("stop", p1.Id)).Ok);
Check("autostart-host-clean-shutdown", (await Call("shutdown-host")).Ok);
Json.Save(Path.Combine(output, "result.json"), new { passed, failed, results, scope = "real local ConPTY processes + file boundaries; not real UTSL gameplay", productionTouched = false });
Console.WriteLine($"RESULT {passed}P/{failed}F"); return failed == 0 ? 0 : 1;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Pigeon.Manager;

string output=Path.GetFullPath(args[0]), host=Path.GetFullPath(args[1]), probe=Path.GetFullPath(args[2]);
if(Directory.Exists(output))throw new IOException("Do not overwrite evidence");Directory.CreateDirectory(output);
var checks=new List<object>();int passed=0,failed=0;
void Check(string name,bool pass){checks.Add(new{name,pass});if(pass)passed++;else failed++;Console.WriteLine((pass?"PASS ":"FAIL ")+name);}
int FreePort(){var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();int p=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();return p;}
async Task Wait(HostClient client){for(int i=0;i<200;i++){if((await client.Send(new("list"))).StartupQueue?.Active!=true)return;await Task.Delay(100);}throw new IOException("Fixture queue stuck");}
foreach(string scenario in new[]{"ordered","timeout-status","early-exit","autostart"}){
    var ws=new Workspace(Path.Combine(output,scenario));var profiles=new List<ServerProfile>();
    foreach(int index in new[]{0,1}){
        string dir=Path.Combine(ws.Servers,index==0?"0.PigeonGame":"1.PigeonLive");Directory.CreateDirectory(dir);
        foreach(string file in Directory.GetFiles(probe))File.Copy(file,Path.Combine(dir,Path.GetFileName(file)));
        File.Copy(Path.Combine(probe,"Manager.ConsoleProbe.exe"),Path.Combine(dir,"UnifierTSL.exe"));
        Directory.CreateDirectory(Path.Combine(dir,"config","TShockAPI"));
        File.WriteAllText(Path.Combine(dir,"config","config.json"),Json.Encode(new{launcher=new{autoStartServers=new[]{new{name="fixture·西"},new{name="fixture"},new{name="fixture·东"}}}}));
        File.WriteAllText(Path.Combine(dir,"config","TShockAPI","config.json"),Json.Encode(new{RestApiPort=FreePort(),ApplicationRestTokens=new Dictionary<string,string>{["LOCAL_TEST_ONLY"]="fixture"}}));
        File.WriteAllText(Path.Combine(dir,"probe-delay-ms.txt"),index==0?"1800":"0");
        if(index==0&&scenario=="timeout-status"){File.WriteAllText(Path.Combine(dir,"probe-delay-ms.txt"),"0");File.WriteAllText(Path.Combine(dir,"probe-missing-status.txt"),"fixture");}
        if(index==0&&scenario=="early-exit")File.WriteAllText(Path.Combine(dir,"probe-exit-before-ready.txt"),"fixture");
        profiles.Add(new(){Id="s"+index,Folder=Path.GetFileName(dir),Name=index==0?"流光城":"生存服",AutoStart=scenario=="autostart",StartupTimeoutSeconds=scenario=="timeout-status"?2:10});
    }
    ws.Save(profiles.AsEnumerable().Reverse().ToList());
    Check(scenario+"-numeric-roster-order",ws.Load().Select(p=>p.Id).SequenceEqual(new[]{"s0","s1"}));
    var client=new HostClient(ws,host);await client.EnsureStarted();
    try{
        if(scenario!="autostart")Check(scenario+"-queue-accepted",(await client.Send(new("start-all"))).Ok);
        await Task.Delay(450);
        var loading=await client.Send(new("list"));
        Check(scenario+"-ipc-responsive",(await client.Send(new("ping"))).Ok);
        if(scenario is "ordered" or "autostart"){
            Check(scenario+"-first-loading-second-not-created",loading.Servers![0].Identity!=null&&loading.Servers[0].State=="STARTING"&&loading.Servers[1].Identity==null);
            Check(scenario+"-duplicate-all-refused",!(await client.Send(new("start-all"))).Ok);
            Check(scenario+"-single-start-cannot-bypass-queue",!(await client.Send(new("start","s1"))).Ok);
        }
        await Wait(client);var final=await client.Send(new("list"));
        if(scenario is "ordered" or "autostart"){
            Check(scenario+"-both-worlds-ready",final.Servers!.All(s=>s.State=="RUNNING"&&s.Identity?.IsCurrent()==true));
            var events=File.ReadAllLines(Path.Combine(ws.Data,"actions.jsonl")).Select(line=>JsonDocument.Parse(line)).ToArray();
            try{
                int ready=Array.FindIndex(events,e=>e.RootElement.GetProperty("action").GetString()=="server-worlds-ready"&&e.RootElement.GetProperty("id").GetString()=="s0");
                int next=Array.FindIndex(events,e=>e.RootElement.GetProperty("action").GetString()=="start-owned-conpty"&&e.RootElement.GetProperty("id").GetString()=="s1");
                Check(scenario+"-next-created-after-first-ready",ready>=0&&next>ready);
            }finally{foreach(var e in events)e.Dispose();}
            var worlds=await client.Send(new("world-consoles","s0"));Check(scenario+"-three-worlds-same-process",worlds.Ok&&worlds.Worlds?.Length==3);
            Check(scenario+"-exact-world-command",(await client.Send(new("world-send","s0","isolated-中文",World:"fixture·西"))).Ok);
            Check(scenario+"-unknown-world-rejected",!(await client.Send(new("world-send","s0","version",World:"missing"))).Ok);
            Check(scenario+"-multiline-command-rejected",!(await client.Send(new("world-send","s0","version\r/off",World:"fixture"))).Ok);
            foreach(string name in new[]{"fixture·西","fixture","fixture·东"}){
                var log=await client.Send(new("world-consoles","s0",World:name));string text=string.Concat(log.Log!.Chunks.Select(c=>c.Text));
                Check(scenario+"-output-isolation-"+name,text.Contains("isolated-中文")== (name=="fixture·西"));
            }
            var other=await client.Send(new("world-consoles","s1",World:"fixture·西"));Check(scenario+"-other-server-not-contaminated",!string.Concat(other.Log!.Chunks.Select(c=>c.Text)).Contains("isolated-中文"));
            var identities=final.Servers!.Select(s=>s.Identity).ToArray();
            Check(scenario+"-already-ready-all-rechecked",(await client.Send(new("start-all"))).Ok);await Wait(client);
            Check(scenario+"-already-ready-not-restarted",(await client.Send(new("list"))).Servers!.Select(s=>s.Identity).SequenceEqual(identities));
        }else{
            Check(scenario+"-failure-pauses-queue",final.StartupQueue?.Active==false&&final.Servers![1].Identity==null&&final.StartupQueue.Message.Contains("暂停")||final.StartupQueue?.Active==false&&final.Servers![1].Identity==null&&final.StartupQueue.Message.Contains("超时"));
            Check(scenario+"-no-second-start-event",!File.ReadAllLines(Path.Combine(ws.Data,"actions.jsonl")).Any(l=>l.Contains("start-owned-conpty")&&l.Contains("\"id\":\"s1\"")));
        }
    }finally{
        await Wait(client);foreach(var state in (await client.Send(new("list"))).Servers!)if(state.Identity!=null)await client.Send(new("stop",state.Profile.Id),30_000);
        Check(scenario+"-clean-host-shutdown",(await client.Send(new("shutdown-host"))).Ok);
    }
}
// Stop fixtures exercise the real IPC host, REST saves and exact ConPTY processes.
(Workspace Ws, List<ServerProfile> Profiles, HostClient Client) StopFixture(string name, int count = 2) {
    var ws = new Workspace(Path.Combine(output, name));
    var profiles = new List<ServerProfile>();
    for (int i = 0; i < count; i++) {
        string dir = Path.Combine(ws.Servers, i + ".Fixture"); Directory.CreateDirectory(dir);
        foreach (string file in Directory.GetFiles(probe)) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        File.Copy(Path.Combine(probe, "Manager.ConsoleProbe.exe"), Path.Combine(dir, "UnifierTSL.exe"));
        Directory.CreateDirectory(Path.Combine(dir, "config", "TShockAPI"));
        Json.Save(Path.Combine(dir, "config", "config.json"), new { launcher = new { autoStartServers = new[] { new { name = "fixture·西" }, new { name = "fixture" }, new { name = "fixture·东" } } } });
        Json.Save(Path.Combine(dir, "config", "TShockAPI", "config.json"), new { RestApiPort = FreePort(), ApplicationRestTokens = new Dictionary<string, string> { ["LOCAL_TEST_ONLY"] = "fixture" } });
        profiles.Add(new() { Id = "s" + i, Folder = Path.GetFileName(dir), Name = "Fixture " + i, StartupTimeoutSeconds = 60 });
    }
    ws.Save(profiles);
    return (ws, profiles, new HostClient(ws, host));
}
void Flag(Workspace ws, ServerProfile p, string name, string value = "fixture") => File.WriteAllText(Path.Combine(ws.ServerDirectory(p), name), value);
JsonElement[] Events(string path) => File.Exists(path) ? File.ReadAllLines(path).Select(l => { using var d = JsonDocument.Parse(l); return d.RootElement.Clone(); }).ToArray() : [];
JsonElement[] ProbeEvents(Workspace ws, ServerProfile p) => Events(Path.Combine(ws.ServerDirectory(p), "probe-events.jsonl"));
async Task<Reply> WaitStop(HostClient client) {
    for (int i = 0; i < 1200; i++) { var state = await client.Send(new("list")); if (state.StopQueue?.Active != true) return state; await Task.Delay(100); }
    throw new IOException("Fixture stop queue stuck");
}
async Task<Reply> WaitFirst(HostClient client) {
    for (int i = 0; i < 100; i++) { var state = await client.Send(new("list")); if (state.Servers![0].Identity != null) return state; await Task.Delay(50); }
    throw new IOException("Fixture first process never created");
}
async Task CleanupStop(Workspace ws, HostClient client, string name) {
    await WaitStop(client);
    await client.Send(new("stop-all")); var end = await WaitStop(client);
    Check(name + "-cleanup-no-owned-process", end.Servers!.All(s => s.Identity == null));
    Check(name + "-clean-host-shutdown", (await client.Send(new("shutdown-host"))).Ok);
}
foreach (bool all in new[] { true, false }) {
    string name = all ? "cancel-all" : "cancel-single";
    var (ws, profiles, client) = StopFixture(name);
    Flag(ws, profiles[0], "probe-delay-ms.txt", "60000");
    Flag(ws, profiles[0], "probe-off-delay-ms.txt", "900");
    await client.EnsureStarted();
    try {
        Check(name + "-start-accepted", (await client.Send(new("start-all"))).Ok);
        var loading = await WaitFirst(client);
        // Wait until REST is listening while the worlds themselves remain stopped.
        await Task.Delay(300);
        var watch = Stopwatch.StartNew();
        var stop = await client.Send(new(all ? "stop-all" : "stop", all ? null : "s0"), 30_000);
        Check(name + "-stop-accepted", stop.Ok);
        if (all) {
            Check(name + "-ipc-immediate", watch.ElapsedMilliseconds < 1500 && stop.StopQueue?.Active == true);
            Check(name + "-start-blocked-during-stop", !(await client.Send(new("start-all"))).Ok);
        }
        var end = await WaitStop(client);
        Check(name + "-startup-cancelled", end.StartupQueue?.Active == false && end.StartupQueue.Message.Contains("取消"));
        Check(name + "-second-never-created", !Events(Path.Combine(ws.Data, "actions.jsonl")).Any(e => e.GetProperty("action").GetString() == "start-owned-conpty" && e.GetProperty("id").GetString() == "s1"));
        Check(name + "-first-exited", loading.Servers![0].Identity?.IsCurrent() == false);
        Check(name + "-not-running-not-saved", !ProbeEvents(ws, profiles[0]).Any(e => e.GetProperty("action").GetString() == "save")
            && end.StopQueue!.Message.Contains("NO_RUNNING_WORLD"));
        Check(name + "-off-once", ProbeEvents(ws, profiles[0]).Count(e => e.GetProperty("action").GetString() == "off") == 1);
        Check(name + "-no-running-world-not-labelled-saved", !Events(Path.Combine(ws.Data, "actions.jsonl")).Any(e => e.GetProperty("action").GetString() == "stop-world-save" && e.GetProperty("detail").GetProperty("outcome").GetString() == "SAVED"));
        if (all) {
            Flag(ws, profiles[0], "probe-delay-ms.txt", "0");
            Check(name + "-restart-all-accepted", (await client.Send(new("start-all"))).Ok); await Wait(client);
            var restarted = await client.Send(new("list"));
            Check(name + "-restart-all-ready", restarted.Servers!.All(s => s.State == "RUNNING" && s.Identity?.IsCurrent() == true));
            Check(name + "-restart-new-generation", restarted.Servers![0].Identity != loading.Servers![0].Identity);
            Check(name + "-stale-terminal-reply-refused", !(await client.Send(new("send", "s0", "old-frame\r", Identity: loading.Servers[0].Identity))).Ok);
            Check(name + "-current-terminal-reply-accepted", (await client.Send(new("send", "s0", "new-frame\r", Identity: restarted.Servers[0].Identity))).Ok);
            Check(name + "-ordinary-send-still-accepted", (await client.Send(new("send", "s0", "ordinary\r"))).Ok);
            Check(name + "-flood-accepted", (await client.Send(new("send", "s0", "flood\r"))).Ok);
            bool flood = false;
            for (int i = 0; i < 100; i++) {
                var log = await client.Send(new("read", "s0"));
                if (string.Concat(log.Log!.Chunks.Select(c => c.Text)).Contains("flood-end")) { flood = true; break; }
                await Task.Delay(50);
            }
            Check(name + "-ansi-chinese-flood-completed", flood);
            Check(name + "-ping-after-flood", (await client.Send(new("ping"))).Ok);
        }
    } finally { await CleanupStop(ws, client, name); }
}
{
    const string name = "residual-and-hanging-off";
    var (ws, profiles, client) = StopFixture(name, 3);
    Flag(ws, profiles[0], "probe-off-residual.txt");
    Flag(ws, profiles[1], "probe-off-hang.txt");
    Flag(ws, profiles[0], "probe-save-mode.txt", "numeric");
    var externalStart = new ProcessStartInfo(ws.ServerExecutable(profiles[2])) { WorkingDirectory = ws.ServerDirectory(profiles[2]), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true };
    using var external = Process.Start(externalStart)!;
    var externalIdentity = new ProcessIdentity(external.Id, external.StartTime.ToUniversalTime().Ticks, ws.ServerExecutable(profiles[2]));
    Check(name + "-external-fixture-ready", (await external.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))?.Contains("CONPTY_READY") == true);
    await client.EnsureStarted();
    try {
        Check(name + "-start-accepted", (await client.Send(new("start-all"))).Ok); await Wait(client);
        var initial = await client.Send(new("list"));
        Check(name + "-two-owned-one-external", initial.Servers!.Take(2).All(s => s.State == "RUNNING") && initial.Servers![2].State == "EXTERNAL");
        Check(name + "-wrong-force-generation-refused", !(await client.Send(new("force-stop", "s0", Identity: initial.Servers![0].Identity! with { StartedUtcTicks = 1 }))).Ok);
        var watch = Stopwatch.StartNew(); var accepted = await client.Send(new("stop-all"));
        Check(name + "-stop-ipc-immediate", accepted.Ok && accepted.StopQueue?.Active == true && watch.ElapsedMilliseconds < 1500);
        var duplicates = await Task.WhenAll(client.Send(new("stop-all")), client.Send(new("stop", "s0")));
        Check(name + "-concurrent-stops-rejected", duplicates.All(r => !r.Ok));
        Check(name + "-start-during-stop-rejected", !(await client.Send(new("start", "s1"))).Ok);
        watch.Restart(); Check(name + "-ipc-responsive-while-off-waits", (await client.Send(new("ping"))).Ok && watch.ElapsedMilliseconds < 1000);
        Check(name + "-host-shutdown-blocked", !(await client.Send(new("shutdown-host"))).Ok);
        var end = await WaitStop(client);
        Check(name + "-both-owned-exited", initial.Servers!.Take(2).All(s => s.Identity?.IsCurrent() == false));
        Check(name + "-external-other-directory-survives", externalIdentity.IsCurrent());
        Check(name + "-external-skip-listed", end.StopQueue!.Message.Contains("s2: EXTERNAL_SKIPPED"));
        Check(name + "-external-no-save-or-off", ProbeEvents(ws, profiles[2]).Length == 0);
        var audit = Events(Path.Combine(ws.Data, "actions.jsonl"));
        var kills = audit.Where(e => e.GetProperty("action").GetString() == "stop-residual-single-pid").ToArray();
        Check(name + "-exact-two-residual-kills", kills.Length == 2 && kills.Select(e => e.GetProperty("detail").GetProperty("Pid").GetInt32()).SequenceEqual(initial.Servers.Take(2).Select(s => s.Identity!.Pid)));
        int firstKill = Array.FindIndex(audit, e => e.GetProperty("action").GetString() == "stop-residual-single-pid");
        int nextSave = Array.FindIndex(audit, e => e.GetProperty("action").GetString() == "stop-world-save" && e.GetProperty("id").GetString() == "s1");
        Check(name + "-stops-are-sequential", firstKill >= 0 && nextSave > firstKill);
        foreach (var p in profiles.Take(2)) {
            var events = ProbeEvents(ws, p);
            Check(name + "-three-saves-before-one-off-" + p.Id, events.Count(e => e.GetProperty("action").GetString() == "save") == 3 && events.Count(e => e.GetProperty("action").GetString() == "off") == 1 && events[^1].GetProperty("action").GetString() == "off");
            string log = File.ReadAllText(Path.Combine(ws.Data, "logs", p.Id + ".ansi.log"));
            Check(name + "-exit-log-flushed-" + p.Id, log.Contains("进程已退出"));
        }
    } finally {
        if (externalIdentity.IsCurrent()) { await external.StandardInput.WriteLineAsync("/off"); await external.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        await CleanupStop(ws, client, name);
    }
}
foreach (string mode in new[] { "status-fail", "body-fail", "nested-fail", "http-fail" }) {
    string name = "save-" + mode;
    var (ws, profiles, client) = StopFixture(name, 1);
    Flag(ws, profiles[0], "probe-save-mode.txt", mode);
    await client.EnsureStarted();
    try {
        await client.Send(new("start-all")); await Wait(client);
        var initial = (await client.Send(new("list"))).Servers![0];
        var stopped = await client.Send(new("stop", "s0"), 30_000);
        var state = (await client.Send(new("list"))).Servers![0];
        Check(name + "-failed-save-keeps-process", !stopped.Ok && initial.Identity?.IsCurrent() == true);
        Check(name + "-failure-diagnosable", state.State == "STOP_SAVE_FAILED" && state.Error?.Contains("SAVE_FAILED") == true && stopped.StopQueue?.Message.Contains("失败") == true);
        Check(name + "-no-off", !ProbeEvents(ws, profiles[0]).Any(e => e.GetProperty("action").GetString() == "off"));
        Check(name + "-no-kill-or-false-saved", !Events(Path.Combine(ws.Data, "actions.jsonl")).Any(e => e.GetProperty("action").GetString() is "stop-residual-single-pid" or "graceful-stop-once" || e.GetProperty("action").GetString() == "stop-world-save" && e.GetProperty("detail").GetProperty("outcome").GetString() == "SAVED"));
        Flag(ws, profiles[0], "probe-save-mode.txt", "ok");
        Flag(ws, profiles[0], "probe-save-delay-ms.txt", "350");
        var retry = client.Send(new("stop", "s0"), 30_000); await Task.Delay(100);
        Check(name + "-duplicate-retry-rejected", !(await client.Send(new("stop", "s0"))).Ok);
        Check(name + "-explicit-retry-after-repair-exits", (await retry).Ok && !initial.Identity!.IsCurrent());
        Check(name + "-only-one-off-after-retry", ProbeEvents(ws, profiles[0]).Count(e => e.GetProperty("action").GetString() == "off") == 1);
    } finally { await CleanupStop(ws, client, name); }
}
Json.Save(Path.Combine(output,"result.json"),new{passed,failed,checks,scope="actual manager/ConPTY processes with local REST/world-console fixtures; not native world load, UI dispatcher latency or player E2E",productionTouched=false});Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;

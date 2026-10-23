using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Text;
namespace Pigeon.Manager.Host;

sealed class ServerManager(Workspace workspace) {
    sealed class Session {
        public ConPty? Terminal;
        public readonly Queue<LogChunk> Chunks = new();
        public readonly object Sync = new();
        public readonly object FileSync = new();
        public long Sequence;
        public int Characters;
        public string? Error;
        public string State = "STOPPED";
        public string? WorldPipe;
        public Task Exit = Task.CompletedTask;
        public Task Cleanup = Task.CompletedTask;
        public bool OffSent;
        public string? StopSaveDetail;
    }
    readonly ConcurrentDictionary<string, Session> sessions = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    readonly object filesGate = new();
    readonly SemaphoreSlim startupGate = new(1);
    // Admission, cancellation and the process creation commit share one lock.
    // startupGate spans the entire start/stop operation; never wait for it here.
    readonly object lifecycleSync = new();
    CancellationTokenSource? startupCancellation;
    Task startupTask = Task.CompletedTask;
    volatile StartupQueueState startupQueue = new(false);
    volatile StartupQueueState stopQueue = new(false);
    readonly object externalCacheSync = new();
    Dictionary<string, ProcessIdentity> externalCache = new(StringComparer.OrdinalIgnoreCase);
    long externalCacheAt;
    public ServerState[] Snapshot() {
        var profiles = workspace.Load();
        var externalProcesses = DisplayExternal(profiles);
        return profiles.Select(p => {
        if (sessions.TryGetValue(p.Id, out var s) && s.Terminal is { } t && t.Identity.IsCurrent()) {
            long memory = 0; try { t.Process.Refresh(); memory = t.Process.WorkingSet64; } catch { }
            return new ServerState(p, s.State, t.Identity, memory, s.Error);
        }
        externalProcesses.TryGetValue(workspace.ServerExecutable(p), out var external);
        // Display cache entries can outlive an exit, but may never confer ownership.
        if (external != null && !external.IsCurrent()) external = null;
        return new ServerState(p, external != null ? "EXTERNAL" : s?.State ?? "STOPPED", external, 0, s?.Error);
    }).ToArray();
    }
    Dictionary<string, ProcessIdentity> DisplayExternal(IEnumerable<ServerProfile> profiles) {
        lock (externalCacheSync) {
            if (externalCacheAt != 0 && Environment.TickCount64 - externalCacheAt < 900) return externalCache;
            var paths = profiles.Select(workspace.ServerExecutable).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = new Dictionary<string, ProcessIdentity>(StringComparer.OrdinalIgnoreCase);
            foreach (var proc in Process.GetProcesses()) using (proc) {
                try {
                    string? exe = proc.MainModule?.FileName;
                    if (!proc.HasExited && exe != null && paths.Contains(exe)) found.TryAdd(exe, new(proc.Id, proc.StartTime.ToUniversalTime().Ticks, exe));
                } catch { }
            }
            externalCache = found; externalCacheAt = Environment.TickCount64; return found;
        }
    }
    ProcessIdentity? External(ServerProfile p) {
        string exe = workspace.ServerExecutable(p);
        foreach (var proc in Process.GetProcesses()) using (proc) {
            try { if (!proc.HasExited && string.Equals(proc.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return new(proc.Id, proc.StartTime.ToUniversalTime().Ticks, exe); } catch { }
        }
        return null;
    }
    public async Task<Reply> Handle(Request request) {
        if (request.Action == "ping") return new(true, "后台就绪");
        if (request.Action == "list") return new(true, Servers: Snapshot(), StartupQueue: startupQueue, StopQueue: stopQueue);
        if (request.Action is "start" or "start-all" or "autostart") {
            var profiles = workspace.Load().Where(p => request.Action == "start" ? p.Id == request.Id : request.Action == "start-all" || p.AutoStart).ToArray();
            if (profiles.Length == 0) return new(false, "没有待启动的服务器");
            if (request.Action == "start" && Snapshot().Any(s => s.Profile.Id == request.Id && s.Identity != null)) return new(false, "同目录服务端已运行，拒绝重复启动");
            lock (lifecycleSync) {
                if (stopQueue.Active) return new(false, "停止队列正在运行", StartupQueue: startupQueue, StopQueue: stopQueue);
                if (!startupGate.Wait(0)) return new(false, "启动队列正在运行，请等待当前服务器全部世界就绪", StartupQueue: startupQueue);
                var cancellation = startupCancellation = new();
                startupQueue = new(true, Message: "按编号顺序启动；前一服全部世界就绪后继续", Order: profiles.Select(p => p.Id).ToArray());
                startupTask = Task.Run(() => RunStartupQueue(profiles, cancellation));
                return new(true, startupQueue.Message, StartupQueue: startupQueue, StopQueue: stopQueue);
            }
        }
        if (request.Action is "stop-all" or "stop" or "force-stop") {
            var profiles = workspace.Load().Where(p => request.Action == "stop-all" || p.Id == request.Id).ToArray();
            if (request.Action != "stop-all" && profiles.Length == 0) return new(false, "服务器不存在");
            Task<Reply> operation;
            lock (lifecycleSync) {
                if (stopQueue.Active) return new(false, "停止队列正在运行，拒绝重复停止", StopQueue: stopQueue);
                if (request.Action == "force-stop" && (request.Identity == null
                    || !sessions.TryGetValue(profiles[0].Id, out var s) || s.Terminal is not { } t
                    || request.Identity != t.Identity || !t.Identity.IsCurrent())) return new(false, "精确进程身份不匹配，拒绝停止");
                stopQueue = new(true, Message: "取消启动队列后，逐服保存并停止所属进程；外部进程将跳过", Order: profiles.Select(p => p.Id).ToArray());
                startupCancellation?.Cancel();
                var pendingStartup = startupTask;
                operation = Task.Run(() => RunStopQueue(profiles, pendingStartup, request.Action == "stop-all"));
                // stop-all must not hold an IPC connection through world saves or /off.
                if (request.Action == "stop-all") return new(true, stopQueue.Message, StartupQueue: startupQueue, StopQueue: stopQueue);
            }
            return await operation;
        }
        if (request.Action == "plugins") return new(true, Plugins: workspace.Plugins());
        if (request.Action == "import") { lock (filesGate) { var p = workspace.Import(request.Value ?? ""); return new(true, "服务端包已复制，原目录未修改", Servers: Snapshot(), Path: workspace.ServerDirectory(p)); } }
        if (request.Action == "register-existing") { lock(filesGate){var p=workspace.RegisterExisting(request.Value??"");return new(true,"已登记原目录；未复制、未启动、未接管当前进程",Servers:Snapshot(),Path:workspace.ServerDirectory(p));} }
        if (request.Action == "library-add") { lock (filesGate) workspace.AddPlugins(request.Files ?? []); return new(true, "已导入插件库", Plugins: workspace.Plugins()); }
        if (request.Action == "shutdown-host") {
            if (startupQueue.Active || stopQueue.Active || Snapshot().Any(s => s.Identity != null)) return new(false, "仍有服务端或启停队列在运行，不能关闭后台");
            return new(true, "HOST_CAN_EXIT");
        }
        var profile = workspace.Load().SingleOrDefault(p => p.Id == request.Id) ?? throw new IOException("服务器不存在");
        var session = sessions.GetOrAdd(profile.Id, _ => new());
        if (request.Action is "world-consoles" or "world-send") {
            if (session.Terminal is not { } worldTerminal || !worldTerminal.Identity.IsCurrent() || session.WorldPipe is null)
                return new(false, "没有所属的世界控制台；不会接管外部进程");
            if (request.Action == "world-send" && session.State != "RUNNING") return new(false, "全部世界尚未就绪，暂不发送世界命令");
            try {
                using var cancel = new CancellationTokenSource(2000);
                using var pipe = new NamedPipeClientStream(".", session.WorldPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(cancel.Token);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
                await writer.WriteLineAsync(Json.Encode(new { Action = request.Action == "world-send" ? "send" : "read", request.World, request.Value, request.Cursor }).AsMemory(), cancel.Token);
                var reply = Json.Decode<Reply>(await reader.ReadLineAsync(cancel.Token) ?? throw new IOException("世界控制台已断开"));
                if (!worldTerminal.Identity.IsCurrent() || !ReferenceEquals(session.Terminal, worldTerminal)) return new(false, "世界控制台进程代际已变化");
                if (request.Action == "world-send" && reply.Ok) workspace.Audit("send-exact-world", profile.Id, new { worldTerminal.Identity, request.World });
                return reply;
            } catch (Exception e) when (e is IOException or OperationCanceledException) { return new(false, "世界控制台接口尚未连接；可查看宿主控制台"); }
        }
        if(request.Action=="read-config") {string path=Workspace.Under(workspace.ServerDirectory(profile),"config/config.json");return File.Exists(path)?new(true,File.ReadAllText(path)):new(false,"config/config.json 不存在");}
        if (request.Action == "read") { lock (session.Sync) return new(true, Log: new(session.Sequence, request.Cursor > session.Sequence || (session.Chunks.Count > 0 && request.Cursor < session.Chunks.Peek().Sequence - 1), session.Chunks.Where(c => c.Sequence > request.Cursor || request.Cursor > session.Sequence).ToArray())); }
        await gates.GetOrAdd(profile.Id, _ => new(1)).WaitAsync();
        try {
            switch (request.Action) {
                case "resize":
                    if(session.Terminal is not {} resize || !resize.Identity.IsCurrent())return new(false,"没有所属控制台");
                    string[] dimensions=(request.Value??"").Split('x');if(dimensions.Length!=2 || !int.TryParse(dimensions[0],out int cols) || !int.TryParse(dimensions[1],out int rows) || cols<30 || cols>240 || rows<10 || rows>100)throw new IOException("终端尺寸无效");
                    resize.Resize(cols,rows);return new(true);
                case "save-config":
                    RequireStopped(profile,session);
                    string content=request.Value??"";using(var document=System.Text.Json.JsonDocument.Parse(content)){if(document.RootElement.ValueKind!=System.Text.Json.JsonValueKind.Object)throw new IOException("配置必须为JSON对象");}
                    string configPath=Workspace.Under(workspace.ServerDirectory(profile),"config/config.json");
                    string configBackup=Workspace.Under(workspace.Backups,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-config-"+profile.Id[..6]);Directory.CreateDirectory(configBackup);
                    if(File.Exists(configPath)){File.Copy(configPath,Path.Combine(configBackup,"config.json"),false);if(Hashes.FileSha(configPath)!=Hashes.FileSha(Path.Combine(configBackup,"config.json")))throw new IOException("配置备份校验失败");}
                    string configTemp=configPath+".tmp-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);File.WriteAllText(configTemp,content);File.Move(configTemp,configPath,true);workspace.Audit("native-config-saved-stopped-only",profile.Id,new{configPath,configBackup,sha256=Hashes.FileSha(configPath)});return new(true,"已备份并保存，尚未启动");
                case "send":
                    if (session.Terminal is not { } sender || !sender.Identity.IsCurrent()) return new(false, "没有可写入的所属控制台");
                    if (request.Identity != null && request.Identity != sender.Identity) return new(false, "控制台进程代际已变化");
                    string text = request.Value ?? "";
                    if (text.Length > 8192 || text.Contains('\0')) throw new IOException("命令过长或包含非法字符");
                    sender.Write(text); return new(true);
                case "save-profile":
                    RequireStopped(profile, session);
                    var updated = request.Profile ?? throw new IOException("缺少设置");
                    if (updated.Id != profile.Id || updated.Folder != profile.Folder || updated.Executable != profile.Executable || updated.ExistingDirectory != profile.ExistingDirectory) throw new IOException("禁止变更目录或程序身份");
                    if (updated.Ports.Any(p => p < 1 || p > 65535) || updated.Arguments.Any(a => a.Contains('\0')) || updated.StartupTimeoutSeconds is < 1 or > 3600) throw new IOException("设置无效");
                    lock (filesGate) { var profiles = workspace.Load(); profiles[profiles.FindIndex(p => p.Id == profile.Id)] = updated; workspace.Save(profiles); }
                    workspace.Audit("profile-update", profile.Id, updated); return new(true, "启动配置已保存；未改服务端原配置", Servers: Snapshot());
                case "apply-plugins":
                    RequireStopped(profile, session); lock (filesGate) workspace.ApplyPlugins(profile, request.Files ?? []); return new(true, "已备份并同步到停止的服务端，尚未加载");
                default: return new(false, "未知操作");
            }
        } finally { gates[profile.Id].Release(); }
    }
    async Task RunStartupQueue(ServerProfile[] profiles, CancellationTokenSource cancellation) {
        string? current = null;
        var cancel = cancellation.Token;
        try {
            workspace.Audit("startup-queue-begin", null, new { order = profiles.Select(p => p.Id).ToArray() });
            foreach (var profile in profiles) {
                cancel.ThrowIfCancellationRequested();
                current = profile.Id;
                startupQueue = startupQueue with { CurrentId = current, Message = "等待全部世界就绪：" + profile.Name };
                var session = sessions.GetOrAdd(current, _ => new());
                // Validate readiness configuration before creating a process.
                using var readiness = new WorldReadiness(workspace.ServerDirectory(profile));
                if (profile.StartupTimeoutSeconds is < 1 or > 3600) throw new IOException("启动就绪超时范围必须为1–3600秒");
                var gate = gates.GetOrAdd(current, _ => new(1));
                ConPty terminal;
                await gate.WaitAsync(cancel);
                try {
                    lock (lifecycleSync) {
                    cancel.ThrowIfCancellationRequested();
                    if (session.State is "STOPPING" or "STOP_TIMEOUT") throw new IOException("此服已请求停止，启动队列暂停");
                    if (session.Terminal is { } running && running.Identity.IsCurrent()) terminal = running;
                    else {
                        if (External(profile) != null) throw new IOException("外部服务端正在运行，拒绝接管或重复启动");
                        cancel.ThrowIfCancellationRequested();
                        terminal = Launch(profile, session, cancel);
                    }
                    session.State = "STARTING";
                    }
                } finally { gate.Release(); }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(profile.StartupTimeoutSeconds));
                while (true) {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(session.Terminal, terminal) || !terminal.Identity.IsCurrent()
                        || session.State is "STOPPING" or "STOP_TIMEOUT") throw new IOException("就绪前进程已退出或已请求停止，后续服未启动");
                    var check = await readiness.Check(timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    session.Error = check.Detail;
                    if (check.Ready) {
                        await gate.WaitAsync(timeout.Token);
                        try {
                            lock (lifecycleSync) {
                            timeout.Token.ThrowIfCancellationRequested();
                            if (!ReferenceEquals(session.Terminal, terminal) || !terminal.Identity.IsCurrent() || session.State != "STARTING")
                                throw new IOException("就绪核验期间进程状态变化，后续服未启动");
                            session.State = "RUNNING"; session.Error = null;
                            workspace.Audit("server-worlds-ready", current, new { terminal.Identity, detail = check.Detail });
                            }
                        } finally { gate.Release(); }
                        break;
                    }
                    startupQueue = startupQueue with { Message = profile.Name + " · " + check.Detail };
                    await Task.Delay(500, timeout.Token);
                }
            }
            cancel.ThrowIfCancellationRequested();
            startupQueue = startupQueue with { Active = false, CurrentId = null, Message = "启动队列完成，所有目标服的世界已就绪" };
            workspace.Audit("startup-queue-complete", null, startupQueue);
        } catch (Exception e) {
            string message = cancel.IsCancellationRequested ? "启动队列已取消；后续服务器未启动"
                : e is OperationCanceledException ? "世界启动等待超时；后续服务器未启动，请查看当前服控制台" : "启动队列暂停：" + e.Message;
            if (current != null && sessions.TryGetValue(current, out var session)) { if (session.State == "STARTING") session.State = cancel.IsCancellationRequested ? "START_CANCELLED" : "START_FAILED"; session.Error = message; }
            startupQueue = startupQueue with { Active = false, Message = message };
            workspace.Audit(cancel.IsCancellationRequested ? "startup-queue-cancelled" : "startup-queue-failed", current, new { message });
        } finally {
            lock (lifecycleSync) {
                if (ReferenceEquals(startupCancellation, cancellation)) startupCancellation = null;
                cancellation.Dispose(); startupGate.Release();
            }
        }
    }
    async Task<Reply> RunStopQueue(ServerProfile[] profiles, Task pendingStartup, bool all) {
        var results = new List<string>();
        bool ok = true, acquired = false;
        try {
            await pendingStartup;
            await startupGate.WaitAsync(); acquired = true;
            workspace.Audit("stop-queue-begin", null, new { all, order = profiles.Select(p => p.Id).ToArray() });
            foreach (var profile in profiles) {
                stopQueue = stopQueue with { CurrentId = profile.Id, Message = "保存并停止：" + profile.Name };
                var gate = gates.GetOrAdd(profile.Id, _ => new(1));
                await gate.WaitAsync();
                try {
                    var session = sessions.GetOrAdd(profile.Id, _ => new());
                    if (session.Terminal is not { } terminal || !terminal.Identity.IsCurrent()) {
                        var external = External(profile); // Always fresh for control decisions.
                        string reason = external != null ? "EXTERNAL_SKIPPED" : "ALREADY_STOPPED";
                        results.Add(profile.Id + ": " + reason);
                        workspace.Audit("stop-skipped", profile.Id, new { reason, external });
                        if (!all) ok = false;
                        continue;
                    }
                    try {
                        string detail = await StopOwned(profile, session, terminal);
                        results.Add(profile.Id + ": " + detail);
                    } catch (Exception e) {
                        ok = false;
                        // REST exception text can contain a token. Keep diagnostics structural.
                        string detail = e is IOException ? e.Message : "STOP_FAILED：" + e.GetType().Name;
                        session.Error = detail;
                        if (session.State is not ("STOP_SAVE_FAILED" or "STOP_IDENTITY_CHANGED")) session.State = "STOP_FAILED";
                        results.Add(profile.Id + ": " + session.State + " " + detail);
                        workspace.Audit("stop-failed", profile.Id, new { terminal.Identity, session.State, detail });
                    }
                } finally { gate.Release(); }
            }
        } catch (Exception e) {
            ok = false; results.Add("STOP_QUEUE_FAILED：" + e.GetType().Name);
        } finally {
            lock (lifecycleSync) {
                // Publish the final queue state only after releasing its admission gate.
                if (acquired) startupGate.Release();
                stopQueue = stopQueue with { Active = false, CurrentId = null,
                    Message = (ok ? "停止队列完成：" : "停止队列存在失败：") + string.Join("；", results) };
            }
        }
        workspace.Audit("stop-queue-complete", null, new { ok, results });
        return new(ok, stopQueue.Message, Servers: Snapshot(), StartupQueue: startupQueue, StopQueue: stopQueue);
    }
    async Task<string> StopOwned(ServerProfile profile, Session session, ConPty terminal) {
        bool Current() => ReferenceEquals(session.Terminal, terminal) && terminal.Identity.IsCurrent();
        void RequireCurrent() {
            if (!Current()) { session.State = "STOP_IDENTITY_CHANGED"; throw new IOException("STOP_IDENTITY_CHANGED：拒绝操作已变化的所属终端或进程"); }
        }
        RequireCurrent();
        var exited = session.Exit;
        var cleanup = session.Cleanup;
        if (!session.OffSent) {
            session.State = "SAVING"; session.Error = null;
            try {
                using var readiness = new WorldReadiness(workspace.ServerDirectory(profile));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                session.StopSaveDetail = await readiness.SaveRunning(Current, (world, outcome) => {
                    workspace.Audit("stop-world-save", profile.Id, new { terminal.Identity, world, outcome });
                    stopQueue = stopQueue with { Message = profile.Name + " · " + world + " · " + outcome };
                }, deadline.Token);
                RequireCurrent();
            } catch (Exception e) {
                session.State = "STOP_SAVE_FAILED";
                session.Error = "SAVE_FAILED：保存未全部确认，保留进程且未发送 /off；" + (e is IOException ? e.Message : e.GetType().Name);
                workspace.Audit("stop-save-failed-preserved", profile.Id, new { terminal.Identity, detail = session.Error });
                throw new IOException(session.Error);
            }
        }
        session.State = "STOPPING";
        var deadlineOff = Task.Delay(TimeSpan.FromSeconds(20));
        if (!session.OffSent) {
            RequireCurrent(); session.OffSent = true; // Before scheduling the potentially blocking pipe write.
            workspace.Audit("graceful-stop-once", profile.Id, terminal.Identity);
            _ = Task.Run(() => {
                try { if (Current()) terminal.Write("/off\r"); }
                catch (Exception e) { workspace.Audit("stop-off-write-failed", profile.Id, new { terminal.Identity, error = e.GetType().Name }); }
            });
        }
        if (await Task.WhenAny(exited, deadlineOff) != exited) {
            // Never tree-kill or find a process by name. Recheck the very same
            // terminal object and complete identity immediately before Kill(false).
            lock (lifecycleSync) {
                if (!exited.IsCompleted) {
                    RequireCurrent();
                    terminal.Process.Kill(entireProcessTree: false);
                    workspace.Audit("stop-residual-single-pid", profile.Id, terminal.Identity);
                }
            }
        }
        await exited.WaitAsync(TimeSpan.FromSeconds(10));
        await cleanup.WaitAsync(TimeSpan.FromSeconds(10)); // Includes draining and flushing the last log chunk.
        session.State = "STOPPED"; session.Error = null;
        return "STOPPED; " + session.StopSaveDetail;
    }
    ConPty Launch(ServerProfile profile, Session session, CancellationToken cancel) {
        string exe = workspace.ServerExecutable(profile);
        if (!File.Exists(exe)) throw new IOException("服务端程序缺失");
        foreach (int port in profile.Ports) {
            if (port < 1 || port > 65535) throw new IOException("端口范围错误");
            var listener = new TcpListener(IPAddress.Any, port); try { listener.Start(); } finally { listener.Stop(); }
        }
        session.State = "STARTING"; session.Error = null;
        session.OffSent = false; session.StopSaveDetail = null;
        ConPty terminal;
        session.WorldPipe = "UTSLWorlds_" + Guid.NewGuid().ToString("N");
        cancel.ThrowIfCancellationRequested();
        try { terminal = new(exe, workspace.ServerDirectory(profile), profile.Arguments, profile.HeadlessWorlds, session.WorldPipe); }
        catch { session.State = "ERROR"; throw; }
        session.Terminal = terminal;
        workspace.Audit("start-owned-conpty", profile.Id, terminal.Identity);
        Json.Save(Path.Combine(workspace.Data, "runs", profile.Id + ".json"), terminal.Identity);
        var drain = terminal.Drain(text => Append(profile.Id, session, text));
        session.Exit = terminal.Process.WaitForExitAsync();
        var exited = session.Exit;
        session.Cleanup = Task.Run(async () => {
            await exited;
            await Task.Run(terminal.Dispose); await drain;
            Append(profile.Id, session, "\r\n[manager] 进程已退出\r\n");
            lock (lifecycleSync) {
                if (ReferenceEquals(session.Terminal, terminal)) { session.Terminal = null; session.WorldPipe = null; session.State = "STOPPED"; }
            }
        });
        return terminal;
    }
    void RequireStopped(ServerProfile profile, Session session) { if (session.Terminal?.Identity.IsCurrent() == true || External(profile) != null) throw new IOException("必须先停止目标服，拒绝修改运行中的服务端"); }
    void Append(string id, Session s, string text) {
        lock (s.Sync) {
            s.Chunks.Enqueue(new(++s.Sequence, text)); s.Characters += text.Length;
            while (s.Characters > 1_000_000 && s.Chunks.Count > 1) s.Characters -= s.Chunks.Dequeue().Text.Length;
        }
        // The terminal drain is the writer; its exit marker follows await drain.
        // File.AppendAllText flushes and closes each chunk, outside the IPC read lock.
        lock (s.FileSync) {
            string path = Path.Combine(workspace.Data, "logs", id + ".ansi.log"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 20_000_000) File.Move(path, path + ".1", true);
            File.AppendAllText(path, text);
        }
    }
}

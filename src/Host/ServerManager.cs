using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
namespace Pigeon.Manager.Host;

sealed class ServerManager(Workspace workspace) {
    sealed class Session {
        public ConPty? Terminal;
        public readonly Queue<LogChunk> Chunks = new();
        public readonly object Sync = new();
        public long Sequence;
        public int Characters;
        public string? Error;
        public string State = "STOPPED";
    }
    readonly ConcurrentDictionary<string, Session> sessions = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    readonly object filesGate = new();
    public ServerState[] Snapshot() => workspace.Load().Select(p => {
        if (sessions.TryGetValue(p.Id, out var s) && s.Terminal is { } t && t.Identity.IsCurrent()) {
            long memory = 0; try { t.Process.Refresh(); memory = t.Process.WorkingSet64; } catch { }
            return new ServerState(p, s.State, t.Identity, memory, s.Error);
        }
        var external = External(p);
        return new ServerState(p, external != null ? "EXTERNAL" : (s?.State == "STOP_TIMEOUT" ? "STOPPED" : s?.State ?? "STOPPED"), external, 0, s?.Error);
    }).ToArray();
    ProcessIdentity? External(ServerProfile p) {
        string exe = workspace.ServerExecutable(p);
        foreach (var proc in Process.GetProcesses()) using (proc) {
            try { if (!proc.HasExited && string.Equals(proc.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return new(proc.Id, proc.StartTime.ToUniversalTime().Ticks, exe); } catch { }
        }
        return null;
    }
    public async Task<Reply> Handle(Request request) {
        if (request.Action == "ping") return new(true, "后台就绪");
        if (request.Action == "list") return new(true, Servers: Snapshot());
        if (request.Action == "plugins") return new(true, Plugins: workspace.Plugins());
        if (request.Action == "import") { lock (filesGate) { var p = workspace.Import(request.Value ?? ""); return new(true, "服务端包已复制，原目录未修改", Servers: Snapshot(), Path: workspace.ServerDirectory(p)); } }
        if (request.Action == "register-existing") { lock(filesGate){var p=workspace.RegisterExisting(request.Value??"");return new(true,"已登记原目录；未复制、未启动、未接管当前进程",Servers:Snapshot(),Path:workspace.ServerDirectory(p));} }
        if (request.Action == "library-add") { lock (filesGate) workspace.AddPlugins(request.Files ?? []); return new(true, "已导入插件库", Plugins: workspace.Plugins()); }
        if (request.Action == "shutdown-host") {
            if (Snapshot().Any(s => s.Identity != null)) return new(false, "仍有服务端在运行，不能关闭后台");
            return new(true, "HOST_CAN_EXIT");
        }
        var profile = workspace.Load().SingleOrDefault(p => p.Id == request.Id) ?? throw new IOException("服务器不存在");
        var session = sessions.GetOrAdd(profile.Id, _ => new());
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
                case "start":
                    if (session.Terminal?.Identity.IsCurrent() == true || External(profile) != null) return new(false, "同目录服务端已运行，拒绝重复启动");
                    string exe = workspace.ServerExecutable(profile);
                    if (!File.Exists(exe)) throw new IOException("服务端程序缺失");
                    foreach (int port in profile.Ports) {
                        if (port < 1 || port > 65535) throw new IOException("端口范围错误");
                        var listener = new TcpListener(IPAddress.Any, port); try { listener.Start(); } finally { listener.Stop(); }
                    }
                    session.State = "STARTING"; session.Error = null;
                    ConPty terminal;
                    try { terminal = new(exe, workspace.ServerDirectory(profile), profile.Arguments, profile.HeadlessWorlds); }
                    catch { session.State = "ERROR"; throw; }
                    session.Terminal = terminal; session.State = "RUNNING";
                    workspace.Audit("start-owned-conpty", profile.Id, terminal.Identity);
                    Json.Save(Path.Combine(workspace.Data, "runs", profile.Id + ".json"), terminal.Identity);
                    var drain = terminal.Drain(text => Append(profile.Id, session, text));
                    _ = Task.Run(async () => {
                        await terminal.Process.WaitForExitAsync();
                        // ClosePseudoConsole must run off the reader thread, while output is still draining.
                        await Task.Run(terminal.Dispose); await drain;
                        if (ReferenceEquals(session.Terminal, terminal)) { session.Terminal = null; session.State = "STOPPED"; }
                        Append(profile.Id, session, "\r\n[manager] 进程已退出\r\n");
                    });
                    return new(true, "已启动 · 宿主存活不等于世界就绪", Servers: Snapshot());
                case "send":
                    if (session.Terminal is not { } sender || !sender.Identity.IsCurrent()) return new(false, "没有可写入的所属控制台");
                    string text = request.Value ?? "";
                    if (text.Length > 8192 || text.Contains('\0')) throw new IOException("命令过长或包含非法字符");
                    sender.Write(text); return new(true);
                case "stop":
                    if(session.State is "STOP_TIMEOUT" or "STOPPING")return new(false,"已发送过 /off；不自动重试停止",Servers:Snapshot());
                    if (session.Terminal is not { } owned || !owned.Identity.IsCurrent()) return new(false, "没有所属运行进程；不会接管外部进程");
                    session.State = "STOPPING"; owned.Write("/off\r"); workspace.Audit("graceful-stop-once", profile.Id, owned.Identity);
                    for (int i = 0; i < 200 && owned.Identity.IsCurrent(); i++) await Task.Delay(100);
                    if (owned.Identity.IsCurrent()) { session.State = "STOP_TIMEOUT"; session.Error = "/off 20秒未退出；未强杀、未重试"; return new(false, session.Error, Servers: Snapshot()); }
                    return new(true, "进程已退出；世界保存结果请核对控制台", Servers: Snapshot());
                case "force-stop":
                    if (request.Identity == null || session.Terminal is not { } force || request.Identity != force.Identity || !force.Identity.IsCurrent()) return new(false, "精确进程身份不匹配，拒绝停止");
                    force.Process.Kill(entireProcessTree: false); workspace.Audit("user-confirmed-single-pid-stop", profile.Id, force.Identity); return new(true, "已仅停止确认的单PID");
                case "save-profile":
                    RequireStopped(profile, session);
                    var updated = request.Profile ?? throw new IOException("缺少设置");
                    if (updated.Id != profile.Id || updated.Folder != profile.Folder || updated.Executable != profile.Executable || updated.ExistingDirectory != profile.ExistingDirectory) throw new IOException("禁止变更目录或程序身份");
                    if (updated.Ports.Any(p => p < 1 || p > 65535) || updated.Arguments.Any(a => a.Contains('\0'))) throw new IOException("设置无效");
                    lock (filesGate) { var profiles = workspace.Load(); profiles[profiles.FindIndex(p => p.Id == profile.Id)] = updated; workspace.Save(profiles); }
                    workspace.Audit("profile-update", profile.Id, updated); return new(true, "启动配置已保存；未改服务端原配置", Servers: Snapshot());
                case "apply-plugins":
                    RequireStopped(profile, session); lock (filesGate) workspace.ApplyPlugins(profile, request.Files ?? []); return new(true, "已备份并同步到停止的服务端，尚未加载");
                default: return new(false, "未知操作");
            }
        } finally { gates[profile.Id].Release(); }
    }
    void RequireStopped(ServerProfile profile, Session session) { if (session.Terminal?.Identity.IsCurrent() == true || External(profile) != null) throw new IOException("必须先停止目标服，拒绝修改运行中的服务端"); }
    void Append(string id, Session s, string text) {
        lock (s.Sync) {
            s.Chunks.Enqueue(new(++s.Sequence, text)); s.Characters += text.Length;
            while (s.Characters > 1_000_000 && s.Chunks.Count > 1) s.Characters -= s.Chunks.Dequeue().Text.Length;
            string path = Path.Combine(workspace.Data, "logs", id + ".ansi.log"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 20_000_000) File.Move(path, path + ".1", true);
            File.AppendAllText(path, text);
        }
    }
}

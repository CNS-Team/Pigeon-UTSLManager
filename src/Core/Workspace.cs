using System.Reflection;
using System.Text.Json;
namespace Pigeon.Manager;

public sealed class Workspace {
    public string Root { get; }
    public string Servers => Path.Combine(Root, "服务器");
    public string Library => Path.Combine(Root, "插件库");
    public string Data => Path.Combine(Root, "数据");
    public string Backups => Path.Combine(Root, "备份");
    public string Maps => Path.Combine(Root, "地图");
    public string Pipe => "PigeonUTSL_" + Hashes.RootId(Root);
    public Workspace(string root) {
        Root = Path.GetFullPath(root);
        foreach (var path in new[] { Root, Servers, Library, Data, Backups, Maps }) {
            EnsureNoLinks(path); Directory.CreateDirectory(path);
        }
    }
    public static void EnsureNoLinks(string path) {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Directory.GetParent(current)?.FullName)
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不允许符号链接 / 重解析点：" + current);
    }
    public static string Under(string root, string relative) {
        if (Path.IsPathRooted(relative)) throw new IOException("只允许相对路径");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("路径越界");
        EnsureNoLinks(full); return full;
    }
    public List<ServerProfile> Load() => File.Exists(Path.Combine(Data, "servers.json")) ? Json.Decode<List<ServerProfile>>(File.ReadAllText(Path.Combine(Data, "servers.json"))) : [];
    public void Save(List<ServerProfile> profiles) => Json.Save(Path.Combine(Data, "servers.json"), profiles);
    public string ServerDirectory(ServerProfile profile) => Under(Servers, profile.Folder);
    public string ServerExecutable(ServerProfile profile) => Under(ServerDirectory(profile), profile.Executable);
    public ServerProfile Import(string source) {
        source = Path.GetFullPath(source); EnsureNoLinks(source);
        if (!File.Exists(Path.Combine(source, "UnifierTSL.exe"))) throw new IOException("目录根部缺少 UnifierTSL.exe，请选择完整 UTSL 服务端包");
        if (Root.Equals(source, StringComparison.OrdinalIgnoreCase) || Root.StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || source.StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("导入目录不能与管理器目录相互包含");
        // Preflight every entry before writing, so recursive links cannot escape the import.
        var files = new List<(string Path, string Relative)>();
        void Visit(string dir) {
            EnsureNoLinks(dir);
            foreach (var item in Directory.EnumerateFileSystemEntries(dir)) {
                EnsureNoLinks(item);
                if (Directory.Exists(item)) Visit(item); else files.Add((item, Path.GetRelativePath(source, item)));
            }
        }
        Visit(source);
        string id = Guid.NewGuid().ToString("N");
        string label = Path.GetFileName(source.TrimEnd('\\'));
        string folder = string.Concat(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + "_" + id[..6];
        string stage = Under(Servers, ".import-" + id); Directory.CreateDirectory(stage);
        try {
            foreach (var file in files) { string dest = Under(stage, file.Relative); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file.Path, dest, false); }
            string target = Under(Servers, folder); Directory.Move(stage, target);
            int[] ports = [];
            string config = Path.Combine(target, "config", "config.json");
            if (File.Exists(config)) {
                try { using var d = JsonDocument.Parse(File.ReadAllText(config)); if (d.RootElement.TryGetProperty("launcher", out var launcher) && launcher.TryGetProperty("listenPort", out var p) && p.TryGetInt32(out int port)) ports = [port]; } catch (JsonException) { }
            }
            var profile = new ServerProfile { Id = id, Name = label, Folder = folder, Ports = ports };
            var list = Load(); list.Add(profile); Save(list);
            Audit("server-import", profile.Id, new { source, target, files = files.Count, copiedOnly = true }); return profile;
        } catch { Audit("server-import-failed", id, new { stage, preserved = true }); throw; }
    }
    public PluginInfo[] Plugins() => Directory.GetFiles(Library, "*.dll").Select(Describe).OrderBy(p => p.FileName).ToArray();
    public static PluginInfo Describe(string path) {
        EnsureNoLinks(path); var identity = AssemblyName.GetAssemblyName(path); var file = new FileInfo(path);
        return new(identity.Name ?? file.Name, file.Name, identity.Version?.ToString() ?? "?", file.Length, Hashes.FileSha(path));
    }
    public void AddPlugins(IEnumerable<string> sources) {
        var inputs = sources.Select(p => (Path: Path.GetFullPath(p), Info: Describe(p))).ToArray();
        foreach (var (source, info) in inputs) {
            string dest = Under(Library, info.FileName);
            if (File.Exists(dest)) {
                if (Hashes.FileSha(dest) == info.Sha256) continue;
                throw new IOException("插件库已有不同版本，请先改名保存旧件：" + info.FileName);
            }
            File.Copy(source, dest, false); Audit("plugin-import", null, info);
        }
    }
    public void ApplyPlugins(ServerProfile profile, string[] fileNames) {
        if (fileNames.Length == 0) throw new IOException("未选择插件");
        var plan = fileNames.Distinct(StringComparer.OrdinalIgnoreCase).Select(name => {
            if (Path.GetFileName(name) != name || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new IOException("非法插件名");
            string source = Under(Library, name); var info = Describe(source); return (Source: source, Info: info);
        }).ToArray();
        string target = Under(ServerDirectory(profile), "plugins"); EnsureNoLinks(target); Directory.CreateDirectory(target);
        string backup = Under(Backups, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + profile.Id[..6]); Directory.CreateDirectory(backup);
        var manifest = new List<object>();
        foreach (var input in plan) {
            string dest = Under(target, input.Info.FileName);
            string? before = File.Exists(dest) ? Hashes.FileSha(dest) : null;
            if (before == input.Info.Sha256) continue;
            if (before != null) { string copy = Under(backup, input.Info.FileName); File.Copy(dest, copy, false); if (Hashes.FileSha(copy) != before) throw new IOException("备份 SHA 不一致"); }
            manifest.Add(new { file = input.Info.FileName, before, after = input.Info.Sha256 });
            Json.Save(Path.Combine(backup, "manifest.json"), new { profile.Id, items = manifest, status = "IN_PROGRESS" });
            string temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.Copy(input.Source, temp, false);
            if (Hashes.FileSha(temp) != input.Info.Sha256) throw new IOException("候选 SHA 不一致");
            File.Move(temp, dest, true);
            if (Hashes.FileSha(dest) != input.Info.Sha256) throw new IOException("落盘 SHA 不一致");
        }
        Json.Save(Path.Combine(backup, "manifest.json"), new { profile.Id, items = manifest, status = "COPIED_TO_STOPPED_SERVER_NOT_LOADED" });
        Audit("plugins-applied-stopped-only", profile.Id, new { backup, count = manifest.Count });
    }
    public void Audit(string action, string? id, object detail) {
        File.AppendAllText(Path.Combine(Data, "actions.jsonl"), Json.Encode(new { at = DateTimeOffset.Now, action, id, detail }) + Environment.NewLine);
    }
}

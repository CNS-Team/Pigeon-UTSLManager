using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Pigeon.Manager;

// Explicit, stopped-only, one-time conversion. Normal starts never run migration
// scripts and never guess which files may be deleted.
public sealed class WorkspaceMigration(Workspace workspace) {
    public sealed record FileRecord(string Source, string BackupPath, long Bytes, string Sha256);
    public sealed record WorldRecord(string Profile, string Source, string Name, long Bytes, string Sha256);
    static IEnumerable<string> Files(string directory) {
        Workspace.EnsureNoLinks(directory);
        if (!Directory.Exists(directory)) yield break;
        foreach (string entry in Directory.GetFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase)) {
            Workspace.EnsureNoLinks(entry);
            if (Directory.Exists(entry)) { foreach (string file in Files(entry)) yield return file; }
            else yield return entry;
        }
    }
    static void Verify(string file, long bytes, string sha) {
        Workspace.EnsureNoLinks(file);
        if (!File.Exists(file) || new FileInfo(file).Length != bytes || Hashes.FileSha(file) != sha)
            throw new IOException("MIGRATION_BYTE_MISMATCH: " + file);
    }
    public WorldRecord[] PlanWorlds() {
        var result = new List<WorldRecord>();
        foreach (var profile in workspace.Load()) {
            string source = workspace.ServerDirectory(profile);
            foreach (string name in new[] { "Worlds", "world", "Core/Worlds", "Core/world" }) {
                string directory = Workspace.Under(source, name);
                foreach (string file in Files(directory)) {
                    string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                    string target = Workspace.Under(workspace.Worlds, relative);
                    var value = new WorldRecord(profile.Id, file, relative, new FileInfo(file).Length, Hashes.FileSha(file));
                    var other = result.FirstOrDefault(w => w.Name.Equals(relative, StringComparison.OrdinalIgnoreCase));
                    if (other != null && (other.Profile != value.Profile || other.Sha256 != value.Sha256))
                        throw new IOException("WORLD_NAME_COLLISION: " + relative + "；请先为不同服务器的地图使用不同文件名");
                    if (File.Exists(target)) Verify(target, value.Bytes, value.Sha256);
                    result.Add(value);
                }
            }
        }
        return result.ToArray();
    }
    static void AssertStopped(Workspace workspace, ServerProfile[] profiles) {
        if (profiles.Any(p => p.AutoStart)) throw new IOException("MIGRATION_REQUIRES_AUTOSTART_FALSE");
        foreach (var process in Process.GetProcesses()) using (process) {
            try {
                if (process.HasExited || process.MainModule?.FileName is not { } path) continue;
                string prefix = Path.TrimEndingDirectorySeparator(workspace.Root) + Path.DirectorySeparatorChar;
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && profiles.Any(p => Path.GetFileName(path).Equals(p.Executable, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("MIGRATION_REQUIRES_ALL_SERVERS_STOPPED: " + process.Id);
                if (profiles.Any(p => path.Equals(workspace.ServerExecutable(p), StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("MIGRATION_REQUIRES_EXTERNAL_SERVER_STOPPED: " + process.Id);
            } catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }
    }
    public object Apply() {
        // The same lock as Host: there must be no live control plane or startup queue.
        using var hostLock = new FileStream(Path.Combine(workspace.Data, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var profiles = workspace.Load().ToArray();
        if (profiles.Length == 0 || profiles.Any(p => p.ExistingDirectory != null)) throw new IOException("MIGRATION_REQUIRES_MANAGED_SERVER_COPIES");
        AssertStopped(workspace, profiles);
        if (workspace.SharedWorlds) throw new IOException("LAYOUT_ALREADY_MIGRATED");
        var worlds = PlanWorlds(); // Full conflict check before the first mutation.
        string backup = Workspace.Under(workspace.Backups, "Readable-layout-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup);
        string resultPath = Path.Combine(backup, "migration-result.json");
        string stage = "backup";
        var records = new List<FileRecord>();
        try {
            var roots = profiles.Select(p => (Path: workspace.ServerDirectory(p), Label: "Server/" + p.Folder)).ToList();
            foreach (string relative in new[] { "Plugins", "Data/runtime-selections", "Core/.store" })
                if (Directory.Exists(Workspace.Under(workspace.Root, relative))) roots.Add((Workspace.Under(workspace.Root, relative), relative));
            foreach (var root in roots)
                foreach (string file in Files(root.Path)) records.Add(new(file, root.Label + "/" + Path.GetRelativePath(root.Path, file).Replace('\\', '/'), new FileInfo(file).Length, Hashes.FileSha(file)));
            string servers = Path.Combine(workspace.Data, "servers.json");
            records.Add(new(servers, "Data/servers.json", new FileInfo(servers).Length, Hashes.FileSha(servers)));
            string archive = Path.Combine(backup, "before.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) {
                foreach (var record in records) {
                    Verify(record.Source, record.Bytes, record.Sha256);
                    zip.CreateEntryFromFile(record.Source, record.BackupPath, CompressionLevel.Fastest);
                }
            }
            using (var zip = ZipFile.OpenRead(archive)) {
                if (zip.Entries.Count != records.Count) throw new IOException("BACKUP_ENTRY_COUNT_MISMATCH");
                foreach (var record in records) {
                    var entry = zip.GetEntry(record.BackupPath) ?? throw new IOException("BACKUP_MISSING: " + record.BackupPath);
                    using var stream = entry.Open();
                    if (entry.Length != record.Bytes || Convert.ToHexString(SHA256.HashData(stream)) != record.Sha256) throw new IOException("BACKUP_SHA_MISMATCH");
                    Verify(record.Source, record.Bytes, record.Sha256);
                }
            }
            Json.Save(Path.Combine(backup, "backup-manifest.json"), new { archive, sha256 = Hashes.FileSha(archive), files = records });
            stage = "import-exact-current-libraries";
            var runtime = new SharedRuntime(workspace); var engines = new EngineLibrary(workspace);
            var selections = new Dictionary<string, RuntimeSelection>();
            foreach (var profile in profiles) {
                var selection = runtime.GetSelection(profile);
                if (selection.Plugins.Length == 0) throw new IOException("MIGRATION_REQUIRES_EXPLICIT_PLUGIN_SELECTION: " + profile.Id);
                string source = workspace.ServerDirectory(profile);
                var paths = selection.Plugins.Select(a => {
                    string plain = Workspace.Under(source, "plugins/" + a.FileName);
                    string module = Workspace.Under(source, "plugins/" + a.Name + "/" + a.FileName);
                    string path = File.Exists(plain) ? plain : module;
                    Verify(path, a.Bytes, a.Sha256); return path;
                }).ToArray();
                var imported = runtime.Import(paths);
                if (!imported.SequenceEqual(selection.Plugins)) throw new IOException("CURRENT_SELECTION_IDENTITY_CHANGED");
                var engine = engines.Import(profile);
                var final = selection with { Enabled = true, EngineRevision = engine.Revision };
                runtime.SaveSelection(profile, final);
                runtime.DetachSupportToLibrary(profile);
                selections.Add(profile.Id, final);
            }
            // Preserve historical selectable plugin versions too, but not hash folders.
            string legacyPlugins = Workspace.Under(workspace.Library, ".store");
            if (Directory.Exists(legacyPlugins)) runtime.Import(Files(legacyPlugins).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray());
            runtime.MigrateLegacySupport();
            stage = "centralize-worlds";
            Directory.CreateDirectory(workspace.Worlds);
            foreach (var world in worlds) {
                string target = Workspace.Under(workspace.Worlds, world.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(world.Source, target, false);
                Verify(target, world.Bytes, world.Sha256); Verify(world.Source, world.Bytes, world.Sha256);
            }
            stage = "remove-verified-redundant-source-files";
            void DeleteBackedFile(string path) {
                var record = records.Single(r => r.Source.Equals(path, StringComparison.OrdinalIgnoreCase));
                Verify(path, record.Bytes, record.Sha256);
                File.SetAttributes(path, FileAttributes.Normal); File.Delete(path);
            }
            void RemoveEmpty(string directory) {
                if (!Directory.Exists(directory)) return;
                Workspace.EnsureNoLinks(directory);
                foreach (string child in Directory.GetDirectories(directory)) RemoveEmpty(child);
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            foreach (var profile in profiles) {
                string source = workspace.ServerDirectory(profile);
                var engine = engines.Resolve(selections[profile.Id].EngineRevision!);
                foreach (var code in engines.Manifest(engine.Revision).Files) {
                    string file = Workspace.Under(source, code.Path);
                    Verify(Workspace.Under(engine.Directory, code.Path), code.Bytes, code.Sha256);
                    DeleteBackedFile(file);
                }
                foreach (string file in Files(Workspace.Under(source, "plugins"))) DeleteBackedFile(file);
                foreach (var world in worlds.Where(w => w.Profile == profile.Id)) DeleteBackedFile(world.Source);
                foreach (string file in Directory.GetFiles(source))
                    if (file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) == ".manager-runtime-process.json") DeleteBackedFile(file);
                // Keep mutable config/data/log directories, even when empty.
                foreach (string dir in Directory.GetDirectories(source)) {
                    string name = Path.GetFileName(dir);
                    if (EngineLibrary.IsCodeDirectory(name) || name.Equals("Core", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("plugins", StringComparison.OrdinalIgnoreCase) || name.Equals("Worlds", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("world", StringComparison.OrdinalIgnoreCase)) RemoveEmpty(dir);
                }
                string target = Workspace.Under(workspace.Servers, profile.Folder);
                if (!source.Equals(target, StringComparison.OrdinalIgnoreCase)) Directory.Move(source, target);
            }
            foreach (string relative in new[] { "Plugins/.store", "Plugins/.support", "Plugins/.module-support", "Core/.store" }) {
                string directory = Workspace.Under(workspace.Root, relative);
                foreach (string file in Files(directory)) DeleteBackedFile(file);
                RemoveEmpty(directory);
            }
            string oldServers = Workspace.Under(workspace.Root, "Servers"); RemoveEmpty(oldServers);
            Json.Save(Path.Combine(workspace.Data, "shared-worlds.json"), new { schema = 1, directory = "Server/World", backup, worlds });
            stage = "verify-sandboxes";
            AssertStopped(workspace, profiles);
            var previews = profiles.Select(p => new { p.Id, launch = runtime.Prepare(p), selection = runtime.GetSelection(p) }).ToArray();
            foreach (string entry in Directory.GetFileSystemEntries(workspace.Library))
                if (Directory.Exists(entry) || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new IOException("VISIBLE_PLUGINS_NOT_DLL_ONLY");
            var result = new { ok = true, status = "MIGRATED_STOPPED_NOT_STARTED", backup, worlds = worlds.Length, worldDirectory = workspace.Worlds, profiles = previews };
            Json.Save(resultPath, result); workspace.Audit("readable-layout-migration", null, result); return result;
        } catch (Exception error) {
            Json.Save(resultPath, new { ok = false, stage, error = error.ToString(), backup, productionStarted = false, automaticRollback = false });
            throw new IOException("迁移已停止；禁止猜测启动。保留备份与阶段记录：" + resultPath, error);
        }
    }
}

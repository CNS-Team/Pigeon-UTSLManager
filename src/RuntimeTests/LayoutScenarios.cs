using Pigeon.Manager;

namespace RuntimeChecks;
static class LayoutScenarios {
    public static async Task Run(Evidence e, string output, string package, string v1, string v2) {
        await e.Scenario("layout/shared-world-name-conflict", () => {
            var f = new ServerFixture(Path.Combine(output, "layout-conflict"), package);
            foreach (int i in new[] { 0, 1 }) File.WriteAllText(Path.Combine(f.Source(i), "world", "duplicate.wld"), "server-" + i);
            e.Check("layout/same-name-different-bytes-rejected-before-write", Helpers.Rejected(() => new WorkspaceMigration(f.Workspace).PlanWorlds(), out string reason) && reason.Contains("WORLD_NAME_COLLISION"), reason);
            e.Check("layout/conflict-originals-retained", !Directory.EnumerateFileSystemEntries(f.Workspace.Worlds).Any() && Enumerable.Range(0, 2).All(i => File.ReadAllText(Path.Combine(f.Source(i), "world", "duplicate.wld")) == "server-" + i));
            return Task.CompletedTask;
        });
        await e.Scenario("layout/host-exclusive-migration-guard", () => {
            var f = new ServerFixture(Path.Combine(output, "layout-host-lock"), package);
            using var owner = new FileStream(Path.Combine(f.Workspace.Data, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            e.Check("layout/live-host-lock-blocks-migration", Helpers.Rejected(() => new WorkspaceMigration(f.Workspace).Apply(), out string reason), reason);
            e.Check("layout/locked-migration-no-backup-no-delete", !Directory.EnumerateFileSystemEntries(f.Workspace.Backups).Any() && File.Exists(f.Workspace.ServerExecutable(f.Profiles[0])));
            return Task.CompletedTask;
        });
        await e.Scenario("layout/legacy-to-readable-stopped-migration", () => {
            var f = new ServerFixture(Path.Combine(output, "layout-migrate"), package);
            var first = f.Import(v1); var second = f.Import(v2, "v2");
            f.Select(0, first); f.Select(1, second);
            foreach (int i in new[] { 0, 1 }) {
                var a = i == 0 ? first : second;
                Directory.CreateDirectory(Path.Combine(f.Source(i), "plugins"));
                File.Copy(i == 0 ? v1 : v2, Path.Combine(f.Source(i), "plugins", a.FileName));
                File.WriteAllText(Path.Combine(f.Source(i), "world", "fixture-" + (i == 0 ? "A" : "B") + ".wld.bak"), "backup-" + i);
            }
            string engineSaves = Path.Combine(f.Source(0), "lib/Saves/Worlds");
            Directory.CreateDirectory(engineSaves);
            File.WriteAllText(Path.Combine(engineSaves, "stale-copy.wld"), "must-not-be-engine-code");
            var cleanEngine = new EngineLibrary(f.Workspace).Import(f.Profiles[0]);
            e.Check("layout/FNA-lib-Saves-excluded-from-engine-library", new EngineLibrary(f.Workspace).Manifest(cleanEngine.Revision).Files.All(file => !EngineLibrary.IsEngineDataPath(file.Path)));
            File.Delete(Path.Combine(engineSaves, "stale-copy.wld"));
            var configBefore = f.Profiles.Select(p => Hashes.FileSha(Path.Combine(f.Workspace.ServerDirectory(p), "config/config.json"))).ToArray();
            Directory.Move(f.Workspace.Servers, Path.Combine(f.Workspace.Root, "Servers"));
            var w = new Workspace(f.Workspace.Root);
            e.Check("layout/legacy-server-root-readable-before-migration", w.ServerDirectory(f.Profiles[0]).Contains("Servers"));
            var planned = new WorkspaceMigration(w).PlanWorlds();
            var worldHashes = planned.ToDictionary(v => v.Name, v => v.Sha256);
            new WorkspaceMigration(w).Apply();
            e.Check("layout/maps-centralized-into-Server-World", Helpers.SamePath(w.Worlds, Path.Combine(w.Root, "Server", "World")) && worldHashes.Count == 4
                && worldHashes.All(pair => Hashes.FileSha(Path.Combine(w.Worlds, pair.Key)) == pair.Value));
            e.Check("layout/legacy-Servers-removed-after-verified-backup", !Directory.Exists(Path.Combine(w.Root, "Servers")));
            e.Check("layout/only-DLLs-in-visible-Plugins", Directory.GetDirectories(w.Library).Length == 0 && Directory.GetFiles(w.Library).Length == 2
                && Directory.GetFiles(w.Library).All(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
            e.Check("layout/config-byte-exact-and-local", f.Profiles.Select((p, i) => Hashes.FileSha(Path.Combine(w.ServerDirectory(p), "config/config.json")) == configBefore[i]).All(v => v));
            e.Check("layout/redundant-source-code-and-plugins-removed", f.Profiles.All(p => !File.Exists(w.ServerExecutable(p)) && !Directory.Exists(Path.Combine(w.ServerDirectory(p), "plugins"))));
            e.Check("layout/exact-per-server-plugin-pins", new SharedRuntime(w).GetSelection(f.Profiles[0]).Plugins.Single() == first && new SharedRuntime(w).GetSelection(f.Profiles[1]).Plugins.Single() == second);
            var launches = f.Profiles.Select(p => new SharedRuntime(w).Prepare(p)).ToArray();
            var manifests = launches.Select(l => Json.Decode<RuntimeManifest>(File.ReadAllText(l.ManifestPath))).ToArray();
            e.Check("layout/readable-runtime-paths", launches.Select((l, i) => l.Directory.StartsWith(w.RuntimeRoot(f.Profiles[i]) + Path.DirectorySeparatorChar) && Path.GetFileName(l.Directory).StartsWith("run-")).All(v => v));
            e.Check("layout/all-world-mappings-point-to-single-store", manifests.All(m => m.Mappings.Where(x => x.Path.EndsWith("Worlds", StringComparison.OrdinalIgnoreCase) || x.Path.EndsWith("world", StringComparison.OrdinalIgnoreCase)).All(x => x.Target == w.Worlds)));
            e.Check("layout/FNA-save-alias-also-maps-to-single-world-store", manifests.All(m => m.Mappings.Any(x => x.Path == "lib/Saves/Worlds" && x.Target == w.Worlds)));
            e.Check("layout/persistent-config-data-isolated", manifests.Select((m, i) => m.Mappings.Where(x => x.Path == "config" || x.Path == "Data").All(x => x.Target.StartsWith(w.ServerDirectory(f.Profiles[i]) + Path.DirectorySeparatorChar))).All(v => v));
            e.Check("layout/selected-engine-roundtrip-after-prune", manifests.All(m => m.EngineRevision != null && File.Exists(Path.Combine(m.EngineDirectory, "engine-manifest.json"))));
            e.Check("layout/no-hash-or-guid-operational-directories", NoOpaqueDirectories(w.Root));
            string backup = Directory.GetDirectories(w.Backups).Single();
            e.Check("layout/auditable-byte-verified-backup-and-result", File.Exists(Path.Combine(backup, "before.zip")) && File.Exists(Path.Combine(backup, "backup-manifest.json")) && File.ReadAllText(Path.Combine(backup, "migration-result.json")).Contains("MIGRATED_STOPPED_NOT_STARTED"));
            e.Check("layout/no-source-or-generation-process-started", Helpers.ProcessesUnder(w.Root).Length == 0);
            Directory.CreateDirectory(Path.Combine(w.ServerDirectory(f.Profiles[0]), "Worlds"));
            File.WriteAllText(Path.Combine(w.ServerDirectory(f.Profiles[0]), "Worlds", "unmigrated.wld"), "preserve-me");
            e.Check("layout/new-unmigrated-world-fails-closed-not-silently-ignored", Helpers.Rejected(() => new SharedRuntime(w).Prepare(f.Profiles[0]), out string reason) && reason.Contains("BLOCKED_WORLD_MIGRATION_REQUIRED"), reason);
            return Task.CompletedTask;
        });
    }
    static bool NoOpaqueDirectories(string root) {
        foreach (string path in Directory.GetDirectories(root)) {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            string name = Path.GetFileName(path);
            if ((name.Length is 32 or 64) && name.All(char.IsAsciiHexDigit)) return false;
            if (!NoOpaqueDirectories(path)) return false;
        }
        return true;
    }
}

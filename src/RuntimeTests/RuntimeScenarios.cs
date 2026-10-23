using System.Text.Json;
using Pigeon.Manager;

namespace RuntimeChecks;
static class RuntimeScenarios {
    public static async Task Run(Evidence e, string output, string package, string v1, string v2) {
        await e.Scenario("runtime/two-servers", async () => {
            var f = new ServerFixture(Path.Combine(output, "two-servers"), package);
            var artifact = f.Import(v1);
            e.Check("import/metadata-and-sha", artifact.Bytes == new FileInfo(v1).Length && artifact.Sha256 == Hashes.FileSha(v1)
                && artifact.Name == "RuntimeFixture.Plugin" && artifact.Version.StartsWith("1."));
            e.Check("import/deduplicated", f.Runtime.Import([f.ImportPluginSource(v1, "duplicate")]).Single() == artifact
                && f.Runtime.Catalog().Length == 1);
            f.Select(0, artifact); f.Select(1, artifact);
            RuntimeLaunch a = f.Prepare(0), b = f.Prepare(1);
            e.Require("prepare/independent-directories", !Helpers.SamePath(a.Directory, b.Directory)
                && !Helpers.SamePath(a.Directory, f.Source(0)) && !Helpers.SamePath(b.Directory, f.Source(1)));
            e.Check("prepare/correct-executable-and-manifest", Helpers.SamePath(a.Executable, Path.Combine(a.Directory, f.Profiles[0].Executable))
                && Helpers.SamePath(b.Executable, Path.Combine(b.Directory, f.Profiles[1].Executable)) && File.Exists(a.ManifestPath) && File.Exists(b.ManifestPath));
            string aDll = Helpers.FindPlugin(a, artifact), bDll = Helpers.FindPlugin(b, artifact);
            var aId = NativeFiles.Id(aDll); var bId = NativeFiles.Id(bDll);
            string[] store = Helpers.Dlls(f.Workspace.Library).Where(path => Hashes.FileSha(path) == artifact.Sha256).ToArray();
            e.Require("sharing/exactly-one-stored-version", store.Length == 1, store);
            var storeId = NativeFiles.Id(store[0]);
            string manifestA = File.ReadAllText(a.ManifestPath), manifestB = File.ReadAllText(b.ManifestPath);
            bool linked = aId.SameFile(bId) && aId.SameFile(storeId);
            bool copied = !aId.SameFile(bId) && manifestA.Contains("copy", StringComparison.OrdinalIgnoreCase) && manifestB.Contains("copy", StringComparison.OrdinalIgnoreCase);
            e.Check("sharing/file-ID-or-recorded-copy", linked || copied, new { aDll, aId, bDll, bId, stored = store[0], storeId, linked, copied });
            var parsedA = Json.Decode<RuntimeManifest>(manifestA); var parsedB = Json.Decode<RuntimeManifest>(manifestB);
            var recordA = parsedA.Files.Single(file => file.Path.Equals(Path.GetRelativePath(a.Directory, aDll).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            var recordB = parsedB.Files.Single(file => file.Path.Equals(Path.GetRelativePath(b.Directory, bDll).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            e.Check("sharing/manifest-mode-matches-real-file-ID", recordA.Mode == (aId.SameFile(storeId) ? "verified-hardlink" : "verified-copy")
                && recordB.Mode == (bId.SameFile(storeId) ? "verified-hardlink" : "verified-copy") && recordA.Sha256 == artifact.Sha256 && recordB.Sha256 == artifact.Sha256);
            e.Check("persistent/manifest-maps-only-own-server", parsedA.Mappings.All(mapping => mapping.Target.StartsWith(f.Source(0) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                && parsedB.Mappings.All(mapping => mapping.Target.StartsWith(f.Source(1) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            e.Write("file-sharing.json", new { aDll, aId, bDll, bId, store = store[0], storeId, linked, copied, manifestA, manifestB });

            await using (var pa = new FixtureProcess(a, f.Events, output))
            await using (var pb = new FixtureProcess(b, f.Events, output)) {
                await Helpers.Until(() => f.ReadEvents().Count(x => x.GetProperty("action").GetString() == "start") == 2);
                var starts = f.ReadEvents().Where(x => x.GetProperty("action").GetString() == "start").ToArray();
                foreach (var start in starts) {
                    string server = start.GetProperty("server").GetString()!;
                    var loaded = start.GetProperty("loaded").EnumerateArray().ToArray();
                    e.Check("ALC/actual-execution-" + server, loaded.Length == 1 && loaded[0].GetProperty("result").GetString() == "v1:" + server + ":1"
                        && !string.IsNullOrEmpty(loaded[0].GetProperty("alc").GetString()) && loaded[0].GetProperty("sha256").GetString() == artifact.Sha256, start);
                }
                e.Check("process/two-distinct-PIDs", starts.Select(x => x.GetProperty("pid").GetInt32()).Distinct().Count() == 2);
                await pa.Send("only-A");
                await Helpers.Until(() => f.ReadEvents().Any(x => x.GetProperty("action").GetString() == "command" && x.GetProperty("command").GetString() == "only-A"));
                e.Check("process/commands-isolated", !f.ReadEvents().Any(x => x.GetProperty("action").GetString() == "command"
                    && x.GetProperty("server").GetString() == "B" && x.GetProperty("command").GetString() == "only-A"));
            }
            foreach ((int index, string server) in new[] { (0, "A"), (1, "B") })
                foreach (string folder in new[] { "config", "Data", "world" })
                    e.Check("persistent/source-write-" + server + "-" + folder,
                        File.ReadAllText(Path.Combine(f.Source(index), folder, "fixture-" + server + (folder == "world" ? ".wld" : ".txt"))) == "1");
            e.Check("persistent/no-cross-server-files", !File.Exists(Path.Combine(f.Source(0), "Data", "fixture-B.txt"))
                && !File.Exists(Path.Combine(f.Source(1), "world", "fixture-A.wld")));
            RuntimeLaunch a2 = f.Prepare(0);
            e.Check("generation/new-launch-ID", a.GenerationId != a2.GenerationId && !Helpers.SamePath(a.Directory, a2.Directory));
            await using (var restart = new FixtureProcess(a2, f.Events, output))
                await Helpers.Until(() => f.ReadEvents().Count(x => x.GetProperty("action").GetString() == "start" && x.GetProperty("server").GetString() == "A") == 2);
            foreach (string folder in new[] { "config", "Data", "world" })
                e.Check("persistent/restart-preserves-" + folder,
                    File.ReadAllText(Path.Combine(f.Source(0), folder, "fixture-A" + (folder == "world" ? ".wld" : ".txt"))) == "2");
            e.Check("persistent/other-server-not-restarted", File.ReadAllText(Path.Combine(f.Source(1), "Data", "fixture-B.txt")) == "1");

            string oldHash = Hashes.FileSha(aDll), oldManifest = Hashes.FileSha(a.ManifestPath);
            var newer = f.Import(v2, "v2");
            e.Check("version/same-filename-two-hashes-retained", newer.FileName == artifact.FileName && newer.Sha256 != artifact.Sha256 && f.Runtime.Catalog().Length == 2);
            e.Check("version/import-does-not-change-selections", f.Runtime.GetSelection(f.Profiles[0]).Plugins.Single().Sha256 == artifact.Sha256
                && f.Runtime.GetSelection(f.Profiles[1]).Plugins.Single().Sha256 == artifact.Sha256);
            f.Select(1, newer);
            e.Check("selection/per-profile-isolation", f.Runtime.GetSelection(f.Profiles[0]).Plugins.Single().Sha256 == artifact.Sha256
                && f.Runtime.GetSelection(f.Profiles[1]).Plugins.Single().Sha256 == newer.Sha256);
            RuntimeLaunch b2 = f.Prepare(1);
            await using (var process = new FixtureProcess(b2, f.Events, output))
                await Helpers.Until(() => f.ReadEvents().Any(x => x.GetProperty("action").GetString() == "start" &&
                    x.GetProperty("loaded").EnumerateArray().Any(l => l.GetProperty("result").GetString() == "v2:B:2")));
            e.Check("generation/old-payload-and-manifest-immutable", Hashes.FileSha(aDll) == oldHash && Hashes.FileSha(a.ManifestPath) == oldManifest);
            e.Check("generation/old-version-still-runnable", File.Exists(Helpers.FindPlugin(a2, artifact)));
            f.Select(0);
            RuntimeLaunch empty = f.Prepare(0);
            e.Check("selection/enabled-empty-no-library-leak", Helpers.PluginFiles(empty).Length == 0);
            await using (var process = new FixtureProcess(empty, f.Events, output)) {
                await Helpers.Until(() => f.ReadEvents().Any(x => x.GetProperty("action").GetString() == "start"
                    && x.GetProperty("pid").GetInt32() == process.Process.Id));
                var start = f.ReadEvents().Single(x => x.GetProperty("action").GetString() == "start" && x.GetProperty("pid").GetInt32() == process.Process.Id);
                e.Check("selection/empty-actually-loads-zero-plugins", start.GetProperty("loaded").GetArrayLength() == 0);
            }
            f.Runtime.SaveSelection(f.Profiles[0], new(false, [newer]));
            RuntimeLaunch disabled = f.Prepare(0);
            e.Check("selection/disabled-keeps-original-directory", Helpers.SamePath(disabled.Directory, f.Source(0)));
            e.Check("selection/disabled-does-not-inject-selected-plugin", Helpers.PluginFiles(disabled).Length == 0);
            await using (var process = new FixtureProcess(disabled, f.Events, output)) {
                await Helpers.Until(() => f.ReadEvents().Any(x => x.GetProperty("action").GetString() == "start" && x.GetProperty("pid").GetInt32() == process.Process.Id));
                var start = f.ReadEvents().Single(x => x.GetProperty("action").GetString() == "start" && x.GetProperty("pid").GetInt32() == process.Process.Id);
                e.Check("selection/disabled-actually-loads-zero-library-plugins", start.GetProperty("loaded").GetArrayLength() == 0);
            }
        });

        await e.Scenario("runtime/tamper", () => {
            var f = new ServerFixture(Path.Combine(output, "plugin-tamper"), package);
            var artifact = f.Import(v1); f.Select(0, artifact);
            string file = Helpers.Dlls(f.Workspace.Library).Single(path => Hashes.FileSha(path) == artifact.Sha256);
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.Read)) { stream.Position = stream.Length - 1; stream.WriteByte(0xEF); }
            bool rejected = Helpers.Rejected(() => f.Prepare(0), out string reason);
            e.Check("tamper/plugin-hash-refused-before-process", rejected && Helpers.ProcessesUnder(f.Workspace.Root).Length == 0, reason);
            return Task.CompletedTask;
        });
        await EngineScenarios.Run(e, output, package, v1, v2);
    }
}

using Pigeon.Manager;

namespace RuntimeChecks;
static class EngineScenarios {
    static void AddDependencies(string root, string assembly) {
        File.Copy(assembly, Path.Combine(root, "Fixture.Core.dll"));
        foreach (string path in new[] { "lib/Fixture.Dependency.dll", "runtimes/win-x64/native/native-fixture.dll" }) {
            string target = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(assembly, target);
        }
        Directory.CreateDirectory(Path.Combine(root, "i18n"));
        File.WriteAllText(Path.Combine(root, "i18n", "fixture.json"), "{\"fixture\":\"code-resource\"}");
        Directory.CreateDirectory(Path.Combine(root, "config", "private"));
        File.WriteAllText(Path.Combine(root, "config", "private", "do-not-share.dll"), "persistent-config");
        File.WriteAllText(Path.Combine(root, "Data", "account.db"), "persistent-player");
        File.WriteAllText(Path.Combine(root, "world", "private.wld"), "persistent-world");
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        File.Copy(assembly, Path.Combine(root, "plugins", "source-plugin.dll"));
        foreach (string path in new[] { "Core/bin/Fixture.Legacy.dll", "Core/app/fixture.deps.json", "Core/i18n/fixture.json", "Core/runtimes/win-x64/native/legacy-native.dll" }) {
            string target = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) File.Copy(assembly, target);
            else File.WriteAllText(target, "{}");
        }
        Directory.CreateDirectory(Path.Combine(root, "Core/x64"));
        foreach (string path in new[] { "Core/config/private.dll", "Core/Data/private.dll", "Core/Worlds/private.dll" }) {
            string target = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "per-server-only");
        }
    }
    public static async Task Run(Evidence e, string output, string package, string v1, string v2) {
        await e.Scenario("engine/revisions", async () => {
            var f = new ServerFixture(Path.Combine(output, "engine-revisions"), package);
            AddDependencies(f.Source(0), v1); AddDependencies(f.Source(1), v1);
            var library = new EngineLibrary(f.Workspace);
            var first = library.Import(f.Profiles[0]); var same = library.Import(f.Profiles[1]);
            e.Check("engine/same-core-two-servers-deduplicated", first.Revision == same.Revision && first.Directory == same.Directory && library.Catalog().Length == 1);
            var manifest = library.Manifest(first.Revision);
            string[] paths = manifest.Files.Select(file => file.Path.Replace('\\', '/')).ToArray();
            string[] required = { "FixtureServer.exe", "Manager.RuntimeFixture.dll", "Manager.RuntimeFixture.deps.json",
                "Manager.RuntimeFixture.runtimeconfig.json", "Fixture.Core.dll", "lib/Fixture.Dependency.dll",
                "runtimes/win-x64/native/native-fixture.dll", "i18n/fixture.json" };
            e.Check("engine/multi-file-dependency-manifest", required.All(path => paths.Contains(path, StringComparer.OrdinalIgnoreCase)), paths);
            e.Check("engine/manifest-bytes-hashes-complete", manifest.Files.All(file => {
                string path = Workspace.Under(first.Directory, file.Path);
                return File.Exists(path) && new FileInfo(path).Length == file.Bytes && Hashes.FileSha(path) == file.Sha256;
            }) && manifest.Files.Sum(file => file.Bytes) == first.Bytes && manifest.Files.Length == first.FileCount);
            e.Check("engine/persistent-excluded", !paths.Any(path => path.StartsWith("config/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("world/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) || path == "private-root.json"), paths);
            string[] corePaths = { "Core/bin/Fixture.Legacy.dll", "Core/app/fixture.deps.json", "Core/i18n/fixture.json", "Core/runtimes/win-x64/native/legacy-native.dll" };
            var reloaded = new EngineLibrary(f.Workspace);
            e.Check("engine/Core-code-roundtrip-import-resolve-manifest", reloaded.Resolve(first.Revision) == first
                && Json.Encode(reloaded.Manifest(first.Revision)) == Json.Encode(manifest)
                && corePaths.All(path => paths.Contains(path, StringComparer.Ordinal)));
            e.Check("engine/Core-persistent-excluded-even-code-extensions", !paths.Any(path => new[] { "Core/config/", "Core/Data/", "Core/Worlds/" }
                .Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                && new[] { "config", "Data", "Worlds" }.All(name => !Directory.Exists(Path.Combine(first.Directory, "Core", name))));
            File.Copy(v2, Path.Combine(f.Source(1), "Fixture.Core.dll"), true);
            File.Copy(v2, Path.Combine(f.Source(1), "Core/bin/Fixture.Legacy.dll"), true);
            var second = library.Import(f.Profiles[1]);
            e.Check("engine/different-core-revisions-retained", first.Revision != second.Revision && library.Catalog().Length == 2);
            e.Check("engine/old-revision-still-intact", library.Resolve(first.Revision).Revision == first.Revision
                && Hashes.FileSha(Path.Combine(first.Directory, "Fixture.Core.dll")) == Hashes.FileSha(v1));
            f.Runtime.SaveSelection(f.Profiles[0], new(true, [], second.Revision));
            f.Runtime.SaveSelection(f.Profiles[1], new(true, [], first.Revision));
            var a = f.Prepare(0); var b = f.Prepare(1);
            e.Require("engine/prepared-code-follows-selected-core", Hashes.FileSha(Path.Combine(a.Directory, "Fixture.Core.dll")) == Hashes.FileSha(v2)
                && Hashes.FileSha(Path.Combine(b.Directory, "Fixture.Core.dll")) == Hashes.FileSha(v1));
            e.Check("engine/Core-prepared-code-follows-selected-revision", Hashes.FileSha(Path.Combine(a.Directory, "Core/bin/Fixture.Legacy.dll")) == Hashes.FileSha(v2)
                && Hashes.FileSha(Path.Combine(b.Directory, "Core/bin/Fixture.Legacy.dll")) == Hashes.FileSha(v1));
            e.Check("engine/Core-empty-x64-does-not-block-selected-revision", !Directory.EnumerateFiles(Path.Combine(f.Source(0), "Core/x64"), "*", SearchOption.AllDirectories).Any()
                && File.Exists(a.ManifestPath) && File.Exists(b.ManifestPath));
            foreach (var item in new[] { (launch: a, index: 0), (launch: b, index: 1) }) {
                var runtimeManifest = Json.Decode<RuntimeManifest>(File.ReadAllText(item.launch.ManifestPath));
                e.Check("engine/Core-persistent-mappings-own-server-" + item.index, new[] { "config", "Data", "Worlds" }.All(name =>
                    runtimeManifest.Mappings.Any(mapping => mapping.Path == "Core/" + name
                        && Helpers.SamePath(mapping.Target, Path.Combine(f.Source(item.index), "Core", name)))));
            }
            e.Check("engine/source-plugins-not-accidentally-loaded", Helpers.PluginFiles(a).Length == 0 && Helpers.PluginFiles(b).Length == 0);
            await using (var pa = new FixtureProcess(a, f.Events, output))
            await using (var pb = new FixtureProcess(b, f.Events, output)) {
                await Helpers.Until(() => f.ReadEvents().Count(x => x.GetProperty("action").GetString() == "start") == 2);
                var starts = f.ReadEvents().Where(x => x.GetProperty("action").GetString() == "start").ToArray();
                e.Check("engine/selected-core-actually-executed-not-original-core", starts.Single(x => x.GetProperty("server").GetString() == "A").GetProperty("core").GetString() == "v2"
                    && starts.Single(x => x.GetProperty("server").GetString() == "B").GetProperty("core").GetString() == "v1", starts);
            }
            f.Runtime.SaveSelection(f.Profiles[0], new(true, [], first.Revision));
            var aSame = f.Prepare(0);
            var aId = NativeFiles.Id(Path.Combine(aSame.Directory, "Fixture.Core.dll"));
            var bId = NativeFiles.Id(Path.Combine(b.Directory, "Fixture.Core.dll"));
            bool linked = aId.SameFile(bId);
            bool copiesRecorded = File.ReadAllText(aSame.ManifestPath).Contains("copy", StringComparison.OrdinalIgnoreCase)
                && File.ReadAllText(b.ManifestPath).Contains("copy", StringComparison.OrdinalIgnoreCase);
            e.Check("engine/shared-files-file-ID-or-recorded-copy", linked || copiesRecorded, new { aId, bId });
            e.Write("engine-manifest.json", manifest);
        });
        foreach (string scenario in new[] { "changed-dependency", "missing-dependency", "extra-file", "manifest-corruption" }) {
            await e.Scenario("engine/" + scenario, () => {
                var f = new ServerFixture(Path.Combine(output, "engine-" + scenario), package, 1);
                AddDependencies(f.Source(0), v1);
                var library = new EngineLibrary(f.Workspace);
                var artifact = library.Import(f.Profiles[0]);
                string target = Path.Combine(artifact.Directory, "lib", "Fixture.Dependency.dll");
                if (scenario == "changed-dependency") File.AppendAllText(target, "tampered");
                if (scenario == "missing-dependency") File.Delete(target);
                if (scenario == "extra-file") File.WriteAllText(Path.Combine(artifact.Directory, "unexpected.dll"), "extra");
                if (scenario == "manifest-corruption") {
                    string path = Path.Combine(artifact.Directory, "engine-manifest.json");
                    var manifest = Json.Decode<EngineManifest>(File.ReadAllText(path));
                    Json.Save(path, manifest with { Files = manifest.Files.Skip(1).ToArray() });
                }
                e.Check("engine/" + scenario + "-resolve-rejected", Helpers.Rejected(() => library.Resolve(artifact.Revision), out string reason), reason);
                f.Runtime.SaveSelection(f.Profiles[0], new(true, [], artifact.Revision));
                e.Check("engine/" + scenario + "-prepare-rejected", Helpers.Rejected(() => f.Prepare(0), out reason), reason);
                return Task.CompletedTask;
            });
        }
    }
}

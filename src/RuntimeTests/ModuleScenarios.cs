using System.Diagnostics;
using Pigeon.Manager;
namespace RuntimeChecks;
static class ModuleScenarios {
    public static async Task Run(Evidence e, string output, string package, string v1, string actualCore, string tester, string moduleCore, string moduleChild) {
        await e.Scenario("actual-UTSL-module-loader", async () => {
            var f = new ServerFixture(Path.Combine(output, "actual-module-loader"), package, 1);
            string source = f.Source(0);
            foreach (string file in Directory.GetFiles(source).Where(file => EngineLibrary.IsCodeFile(Path.GetFileName(file)))) File.Delete(file);
            var inputs = ActualInputs.CopyCode(actualCore, source);
            e.Write("actual-core-inputs.json", inputs.Select(pair => new { path = pair.Key, sha256 = pair.Value }).ToArray());
            f.Profiles[0] = f.Profiles[0] with { Executable = "UnifierTSL.exe" };
            f.Workspace.Save(f.Profiles.ToList());
            var engines = new EngineLibrary(f.Workspace);
            var core = engines.Import(f.Profiles[0]);
            const string moduleName = "RuntimeFixture.CoreModule", supportName = "RuntimeFixture.ModuleSupport.dll";
            string incoming = Path.Combine(f.Workspace.Root, "incoming", moduleName);
            Directory.CreateDirectory(Path.Combine(incoming, "lib"));
            string incomingOwner = Path.Combine(incoming, Path.GetFileName(moduleCore));
            File.Copy(moduleCore, incomingOwner);
            File.Copy(Path.Combine(Path.GetDirectoryName(moduleCore)!, supportName), Path.Combine(incoming, "lib", supportName));
            Json.Save(Path.Combine(incoming, "dependencies.json"), new {
                EnableAggressiveCleanUp = false,
                Dependencies = new Dictionary<string, object> {
                    ["RuntimeFixture.ModuleSupport"] = new { Name = "RuntimeFixture.ModuleSupport", Version = "1.0.0", Manifests = new[] {
                        new { FilePath = "lib/" + supportName, Version = "1.0.0", Obsolete = false }
                    } }
                }
            });
            var artifacts = f.Runtime.Import([v1, incomingOwner, moduleChild]);
            f.Runtime.SaveSelection(f.Profiles[0], new(true, artifacts, core.Revision));
            var launch = f.Prepare(0);
            string[] pluginFiles = Helpers.PluginFiles(launch);
            e.Require("actual-loader/three-selected-modules-plus-lib-prepared", pluginFiles.Length == 4
                && artifacts.All(artifact => pluginFiles.Count(path => Path.GetFileName(path) == artifact.FileName) == 1), pluginFiles);
            var second = f.Prepare(0);
            var manifest = Json.Decode<RuntimeManifest>(File.ReadAllText(launch.ManifestPath));
            var support = manifest.Files.Where(file => file.Path.StartsWith("plugins/" + moduleName + "/", StringComparison.OrdinalIgnoreCase)
                && (file.Path.EndsWith("/dependencies.json", StringComparison.OrdinalIgnoreCase) || file.Path.Contains("/lib/", StringComparison.OrdinalIgnoreCase))).ToArray();
            e.Require("actual-loader/side-support-pinned-in-manifest", support.Length == 2, support);
            foreach (var file in support) {
                string firstPath = Path.Combine(launch.Directory, file.Path), secondPath = Path.Combine(second.Directory, file.Path);
                var firstId = NativeFiles.Id(firstPath); var secondId = NativeFiles.Id(secondPath); var storeId = NativeFiles.Id(file.Source);
                e.Check("actual-loader/private-side-copy-" + Path.GetFileName(file.Path), file.Mode == "verified-copy"
                    && !firstId.SameFile(storeId) && !firstId.SameFile(secondId) && !secondId.SameFile(storeId)
                    && Hashes.FileSha(firstPath) == file.Sha256 && Hashes.FileSha(secondPath) == file.Sha256,
                    new { file, firstId, secondId, storeId });
            }
            string result = Path.Combine(output, "actual-module-loader-result.json");
            var start = new ProcessStartInfo(tester) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = launch.Directory, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(launch.Directory); start.ArgumentList.Add(Path.Combine(launch.Directory, "plugins")); start.ArgumentList.Add(result);
            using var process = Process.Start(start)!;
            var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, tester);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            finally {
                if (identity.IsCurrent()) { process.Kill(false); await process.WaitForExitAsync(); }
                File.WriteAllText(Path.Combine(output, "actual-module-loader.stdout.log"), await stdout);
                File.WriteAllText(Path.Combine(output, "actual-module-loader.stderr.log"), await stderr);
            }
            e.Require("actual-loader/independent-process-succeeded", process.ExitCode == 0 && File.Exists(result),
                new { process.ExitCode, stdout = await stdout, stderr = await stderr });
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(result));
            e.Check("actual-loader/component-checks-all-pass", document.RootElement.GetProperty("failed").GetInt32() == 0,
                document.RootElement.Clone());
            foreach (var artifact in artifacts) {
                var files = Helpers.Dlls(f.Workspace.Library).Where(path => Path.GetFileName(path) == artifact.FileName).ToArray();
                e.Check("actual-loader/store-unmodified-" + artifact.Name, files.Length == 1 && Hashes.FileSha(files[0]) == artifact.Sha256);
                var runtimeFile = Helpers.PluginFiles(launch).Single(path => Path.GetFileName(path) == artifact.FileName);
                e.Write("actual-module-file-" + artifact.Name + ".json", new { artifact, stored = files, runtimeFile,
                    runtimeId = NativeFiles.Id(runtimeFile), storedId = NativeFiles.Id(files[0]) });
            }
            foreach (var file in support) {
                string firstPath = Path.Combine(launch.Directory, file.Path), secondPath = Path.Combine(second.Directory, file.Path);
                e.Check("actual-loader/load-preserves-side-store-" + Path.GetFileName(file.Path), Hashes.FileSha(file.Source) == file.Sha256
                    && Hashes.FileSha(secondPath) == file.Sha256);
                File.AppendAllText(firstPath, "\nfixture-private-write");
                e.Check("actual-loader/side-write-isolated-" + Path.GetFileName(file.Path), Hashes.FileSha(file.Source) == file.Sha256
                    && Hashes.FileSha(secondPath) == file.Sha256 && Hashes.FileSha(firstPath) != file.Sha256);
            }
            var dependency = support.Single(file => file.Path.EndsWith("/lib/" + supportName, StringComparison.OrdinalIgnoreCase));
            File.AppendAllText(dependency.Source, "fixture-store-tamper");
            e.Check("actual-loader/tampered-side-store-prepare-refused", Helpers.Rejected(() => f.Prepare(0), out string reason), reason);
        });
    }
}

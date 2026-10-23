using System.Diagnostics;
using Pigeon.Manager;

namespace RuntimeChecks;
static class ProviderScenarios {
    static readonly string[] Modules = ["TShockAPI", "CustomPlayer"];
    public static async Task Run(Evidence e, string output, string package, string tester, string actualSource) {
        string label = Path.GetFileName(actualSource);
        await e.Scenario("actual-provider/" + label, async () => {
            Workspace.EnsureNoLinks(actualSource);
            var f = new ServerFixture(Path.Combine(output, "actual-provider-" + label), package, 1);
            string source = f.Source(0);
            foreach (string file in Directory.GetFiles(source).Where(file => EngineLibrary.IsCodeFile(Path.GetFileName(file)))) File.Delete(file);
            var inputHashes = ActualInputs.CopyCode(actualSource, source);
            foreach (string module in Modules) ActualInputs.CopyTree(Path.Combine(actualSource, "plugins", module), Path.Combine(source, "plugins", module), inputHashes);
            f.Profiles[0] = f.Profiles[0] with { Executable = "UnifierTSL.exe" };
            f.Workspace.Save(f.Profiles.ToList());
            var artifacts = f.Runtime.Import(Modules.Select(module => Path.Combine(source, "plugins", module, module + ".dll")).ToArray());
            var engine = new EngineLibrary(f.Workspace).Import(f.Profiles[0]);
            var selection = new RuntimeSelection(true, artifacts, engine.Revision);
            f.Runtime.SaveSelection(f.Profiles[0], selection);
            var first = f.Prepare(0); var second = f.Prepare(0);
            var manifest = Json.Decode<RuntimeManifest>(File.ReadAllText(first.ManifestPath));
            var otherManifest = Json.Decode<RuntimeManifest>(File.ReadAllText(second.ManifestPath));
            e.Require("provider/" + label + "/only-selected-owners", artifacts.Length == 2 && Modules.All(module =>
                manifest.Files.Count(file => file.Path.Equals("plugins/" + module + "/" + module + ".dll", StringComparison.Ordinal)) == 1));
            var support = manifest.Files.Where(file => file.Path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)
                && !Modules.Any(module => file.Path.Equals("plugins/" + module + "/" + module + ".dll", StringComparison.OrdinalIgnoreCase))).ToArray();
            var identities = support.Select(file => new { file, first = NativeFiles.Id(Path.Combine(first.Directory, file.Path)),
                second = NativeFiles.Id(Path.Combine(second.Directory, file.Path)), store = NativeFiles.Id(file.Source) }).ToArray();
            e.Require("provider/" + label + "/all-support-private-copy", support.Length > 2 && identities.All(value => value.file.Mode == "verified-copy"
                && !value.first.SameFile(value.second) && !value.first.SameFile(value.store) && !value.second.SameFile(value.store)));
            e.Write("provider-" + label + "-binding.json", new { actualSource, artifacts, engine, first, second, identities,
                inputs = inputHashes.Select(pair => new { path = pair.Key, sha256 = pair.Value }).ToArray() });
            var libraryBefore = Snapshot(f.Workspace.Library);
            string result = Path.Combine(output, "provider-" + label + "-result.json");
            var start = new ProcessStartInfo(tester) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = first.Directory, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { first.Directory, Path.Combine(first.Directory, "plugins"), result, "provider" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, tester);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            finally {
                if (identity.IsCurrent()) { process.Kill(false); await process.WaitForExitAsync(); }
                File.WriteAllText(Path.Combine(output, "provider-" + label + ".stdout.log"), await stdout);
                File.WriteAllText(Path.Combine(output, "provider-" + label + ".stderr.log"), await stderr);
            }
            e.Check("provider/" + label + "/real-loader-process", process.ExitCode == 0 && File.Exists(result),
                new { process.ExitCode, stdout = await stdout, stderr = await stderr });
            if (File.Exists(result)) {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(result));
                e.Check("provider/" + label + "/real-provider-checks", document.RootElement.GetProperty("failed").GetInt32() == 0, document.RootElement.Clone());
            }
            e.Check("provider/" + label + "/content-library-unmodified", Equal(libraryBefore, Snapshot(f.Workspace.Library)));
            e.Check("provider/" + label + "/other-generation-unmodified", otherManifest.Files.All(file =>
                File.Exists(Path.Combine(second.Directory, file.Path)) && Hashes.FileSha(Path.Combine(second.Directory, file.Path)) == file.Sha256));
            e.Check("provider/" + label + "/loaded-code-unmodified", manifest.Files.Where(file => !file.Path.EndsWith("/dependencies.json", StringComparison.OrdinalIgnoreCase))
                .All(file => File.Exists(Path.Combine(first.Directory, file.Path)) && Hashes.FileSha(Path.Combine(first.Directory, file.Path)) == file.Sha256));
            e.Check("provider/" + label + "/retrieved-inputs-unmodified", inputHashes.All(pair => Hashes.FileSha(pair.Key) == pair.Value));
            string config = Path.Combine(source, "plugins/TShockAPI/dependencies.json");
            byte[] originalConfig = File.ReadAllBytes(config);
            File.AppendAllText(config, "\n");
            f.Runtime.SaveSelection(f.Profiles[0], selection);
            e.Check("provider/" + label + "/unverified-support-combination-blocked", Helpers.Rejected(() => f.Prepare(0), out string supportReason)
                && supportReason.Contains("BLOCKED_OPEN_DYNAMIC_MODULE_DEPENDENCIES", StringComparison.Ordinal), supportReason);
            File.WriteAllBytes(config, originalConfig);
            f.Runtime.SaveSelection(f.Profiles[0], selection);
            string wrongOwner = Path.Combine(source, "plugins/CustomPlayer/CustomPlayer.dll");
            File.AppendAllText(wrongOwner, "fixture-unverified-owner");
            var changedOwner = f.Runtime.Import([wrongOwner]).Single();
            f.Runtime.SaveSelection(f.Profiles[0], selection with { Plugins = artifacts.Select(artifact => artifact.Name == "CustomPlayer" ? changedOwner : artifact).ToArray() });
            e.Check("provider/" + label + "/unverified-owner-blocked", Helpers.Rejected(() => f.Prepare(0), out string ownerReason)
                && ownerReason.Contains("BLOCKED_OPEN_DYNAMIC_MODULE_DEPENDENCIES", StringComparison.Ordinal), ownerReason);
            e.Check("provider/" + label + "/source-executable-never-started", Helpers.ProcessesUnder(actualSource).Length == 0);
        });
    }
    static Dictionary<string, string> Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(directory, file), Hashes.FileSha, StringComparer.OrdinalIgnoreCase);
    static bool Equal(Dictionary<string, string> left, Dictionary<string, string> right) => left.Count == right.Count && left.All(pair => right.GetValueOrDefault(pair.Key) == pair.Value);
}

using System.Diagnostics;
using Pigeon.Manager;

namespace RuntimeChecks;
static class BoundaryScenarios {
    public static async Task Run(Evidence e, string output, string package, string v1) {
        await e.Scenario("boundary/paths-IDs-artifacts", () => {
            var f = new ServerFixture(Path.Combine(output, "boundary-inputs"), package);
            var artifact = f.Import(v1);
            foreach (string path in new[] { "config/settings.json:stream", "CON.txt", "folder. /file.txt" })
                e.Check("boundary/Workspace.Under-" + path, Helpers.Rejected(() => Workspace.Under(f.Workspace.Root, path), out string reason), reason);
            foreach (string id in new[] { "", "../escape", "..\\escape", "A/B", "A:B", "A.", "A " }) {
                var profile = f.Profiles[0] with { Id = id };
                e.Check("boundary/selection-ID-" + id, Helpers.Rejected(() => f.Runtime.SaveSelection(profile, new(true, [artifact])), out string reason), reason);
                e.Check("boundary/prepare-ID-" + id, Helpers.Rejected(() => f.Runtime.Prepare(profile), out reason), reason);
            }
            foreach (string file in new[] { "../escape.dll", "..\\escape.dll", "A/B.dll", "A:B.dll", "C:\\escape.dll", "bad.exe" }) {
                var forged = artifact with { FileName = file };
                bool rejected = Helpers.Rejected(() => { f.Runtime.SaveSelection(f.Profiles[0], new(true, [forged])); f.Prepare(0); }, out string reason);
                e.Check("boundary/plugin-file-" + file, rejected, reason);
            }
            foreach (string sha in new[] { "", "../escape", new string('G', 64), new string('0', 64) }) {
                var forged = artifact with { Sha256 = sha };
                e.Check("boundary/plugin-hash-" + sha, Helpers.Rejected(() => {
                    f.Runtime.SaveSelection(f.Profiles[0], new(true, [forged])); f.Prepare(0);
                }, out string reason), reason);
            }
            var falseSize = artifact with { Bytes = artifact.Bytes + 1 };
            e.Check("boundary/forged-plugin-length", Helpers.Rejected(() => {
                f.Runtime.SaveSelection(f.Profiles[0], new(true, [falseSize])); f.Prepare(0);
            }, out string lengthReason), lengthReason);
            var duplicate = artifact with { FileName = "duplicate.dll" };
            e.Check("boundary/duplicate-assembly-selection", Helpers.Rejected(() => {
                f.Runtime.SaveSelection(f.Profiles[0], new(true, [artifact, duplicate])); f.Prepare(0);
            }, out string duplicateReason), duplicateReason);
            f.Select(0, artifact);
            foreach (string path in new[] { "..\\..\\escape", "../escape", "C:\\escape" }) {
                var profile = f.Profiles[0] with { Folder = path };
                e.Check("boundary/server-folder-" + path, Helpers.Rejected(() => f.Runtime.Prepare(profile), out string reason), reason);
            }
            foreach (string path in new[] { "..\\..\\escape.exe", "../escape.exe", "C:\\escape.exe" }) {
                var profile = f.Profiles[0] with { Executable = path };
                e.Check("boundary/executable-" + path, Helpers.Rejected(() => f.Runtime.Prepare(profile), out string reason), reason);
            }
            var library = new EngineLibrary(f.Workspace);
            foreach (string revision in new[] { "", "..\\escape", new string('G', 64), new string('0', 64) })
                e.Check("boundary/engine-revision-" + revision, Helpers.Rejected(() => library.Resolve(revision), out string reason), reason);
            e.Check("boundary/no-process-created", Helpers.ProcessesUnder(f.Workspace.Root).Length == 0);
            return Task.CompletedTask;
        });

        await e.Scenario("boundary/unknown-root-mutable", () => {
            var f = new ServerFixture(Path.Combine(output, "unknown-root-mutable"), package, 1);
            f.Select(0, f.Import(v1));
            string path = Path.Combine(f.Source(0), "private-root.json");
            File.WriteAllText(path, "persistent-root-data");
            e.Check("boundary/unknown-root-file-fails-closed", Helpers.Rejected(() => f.Prepare(0), out string reason), reason);
            e.Check("boundary/unknown-root-file-preserved", File.ReadAllText(path) == "persistent-root-data" && Helpers.ProcessesUnder(f.Workspace.Root).Length == 0);
            return Task.CompletedTask;
        });

        foreach (string location in new[] { "source-config", "source-code", "runtime-root", "library-store", "selection-directory" }) {
            await e.Scenario("boundary/reparse-" + location, () => {
                var f = new ServerFixture(Path.Combine(output, "reparse-" + location), package, 1);
                var artifact = f.Import(v1);
                f.Select(0, artifact);
                string external = Path.Combine(f.Workspace.Root, "outside");
                Directory.CreateDirectory(external);
                File.WriteAllText(Path.Combine(external, "sentinel.txt"), "DO_NOT_CHANGE");
                string link;
                if (location == "source-config") {
                    link = Path.Combine(f.Source(0), "config");
                    Directory.Move(link, Path.Combine(f.Source(0), "config-original"));
                } else if (location == "source-code") link = Path.Combine(f.Source(0), "lib");
                else if (location == "runtime-root") link = Path.Combine(f.Workspace.Root, "Runtime");
                else if (location == "library-store") {
                    link = Path.Combine(f.Workspace.Library, ".store");
                    if (Directory.Exists(link)) Directory.Move(link, link + "-original");
                } else {
                    link = Path.Combine(f.Workspace.Data, "runtime-selections");
                    if (Directory.Exists(link)) Directory.Move(link, link + "-original");
                }
                CreateJunction(link, external);
                bool rejected = Helpers.Rejected(() => {
                    if (location == "library-store") f.Runtime.Import([f.ImportPluginSource(v1, "reparse-import")]);
                    else f.Prepare(0);
                }, out string reason);
                e.Check("boundary/reparse-" + location + "-rejected", rejected, reason);
                e.Check("boundary/reparse-" + location + "-outside-preserved", File.ReadAllText(Path.Combine(external, "sentinel.txt")) == "DO_NOT_CHANGE");
                return Task.CompletedTask;
            });
        }
        await e.Scenario("boundary/import-source-link", () => {
            var f = new ServerFixture(Path.Combine(output, "reparse-import-source"), package, 1);
            string external = Path.Combine(f.Workspace.Root, "outside"); Directory.CreateDirectory(external);
            File.Copy(v1, Path.Combine(external, "RuntimeFixture.Plugin.dll"));
            string link = Path.Combine(f.Workspace.Root, "incoming-link"); CreateJunction(link, external);
            e.Check("boundary/import-source-reparse-rejected",
                Helpers.Rejected(() => f.Runtime.Import([Path.Combine(link, "RuntimeFixture.Plugin.dll")]), out string reason), reason);
            return Task.CompletedTask;
        });
    }
    static void CreateJunction(string path, string target) {
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Junction path must be new");
        // These are fixture-created local paths; no delete/move crosses a shell boundary.
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J"); start.ArgumentList.Add(path); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Fixture junction failed: " + output + error);
    }
}

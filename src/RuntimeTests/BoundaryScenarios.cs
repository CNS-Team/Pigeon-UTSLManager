using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
                else if (location == "runtime-root") link = Path.Combine(f.Workspace.Root, "Core", "Runtime");
                else if (location == "library-store") {
                    link = Path.Combine(f.Workspace.Data, "Library", "Plugins");
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Exercise the reparse guard without introducing a shell in fixtures.
        Directory.CreateDirectory(path);
        byte[] substitute = Encoding.Unicode.GetBytes("\\??\\" + Path.GetFullPath(target));
        byte[] display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        int pathBytes = checked(substitute.Length + display.Length + 4);
        byte[] buffer = new byte[16 + pathBytes];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)(8 + pathBytes))).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)display.Length)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); display.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
            throw new IOException("Fixture junction failed", new Win32Exception(Marshal.GetLastWin32Error()));
        string? actual = new DirectoryInfo(path).LinkTarget;
        if (actual == null || !Helpers.SamePath(actual, target)) throw new IOException("Fixture junction verification failed");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputBytes, byte[]? output, int outputBytes, out int returned, IntPtr overlapped);
}

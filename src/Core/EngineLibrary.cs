using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Pigeon.Manager;

public sealed record EngineArtifact(string Revision, string Name, string Version, long Bytes, int FileCount, string Directory);
public sealed record EngineFile(string Path, long Bytes, string Sha256);
public sealed record EngineManifest(string Revision, string Name, string Version, string Executable, EngineFile[] Files);

// The shared engine contains code and its runtime dependencies. Per-server
// configuration, plugins, worlds and player databases never enter this store.
public sealed class EngineLibrary(Workspace workspace) {
    static readonly HashSet<string> CodeDirectories = new(StringComparer.OrdinalIgnoreCase) {
        "lib", "runtimes", "bin", "x64", "x86", "arm64", "i18n", "locales", "localization", "languages", "refs"
    };
    // Retrieved UTSL packages keep additional native/managed code here. Never
    // include the entire Core directory: its config/data remain per server.
    static readonly HashSet<string> CoreCodeDirectories = new(StringComparer.OrdinalIgnoreCase) {
        "bin", "app", "i18n", "runtimes", "x64"
    };
    public static bool IsCodeDirectory(string name) => CodeDirectories.Contains(name);
    public static bool IsCodeFile(string name) => new[] { ".dll", ".exe", ".pdb", ".so", ".dylib", ".deps.json", ".runtimeconfig.json", ".runtimeconfig.dev.json" }
        .Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    string Store {
        get {
            string path = Workspace.Under(workspace.Root, "Core/.store");
            Directory.CreateDirectory(path); return path;
        }
    }
    public EngineArtifact[] Catalog() => Directory.GetDirectories(Store)
        .Where(d => !Path.GetFileName(d).StartsWith('.'))
        .Select(d => Read(Path.GetFileName(d), verifyFiles: false).Artifact)
        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Revision, StringComparer.Ordinal).ToArray();

    public EngineArtifact Resolve(string revision) => Read(revision, verifyFiles: true).Artifact;
    public EngineManifest Manifest(string revision) => Read(revision, verifyFiles: true).Manifest;

    static void ValidateRevision(string revision) {
        if (revision.Length != 64 || revision.Any(c => !char.IsAsciiHexDigit(c)) || revision != revision.ToUpperInvariant())
            throw new IOException("非法的核心版本标识");
    }
    static string Revision(EngineManifest manifest) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Json.Encode(new { manifest.Executable, Files = manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray() }))));
    (EngineArtifact Artifact, EngineManifest Manifest) Read(string revision, bool verifyFiles) {
        ValidateRevision(revision);
        string directory = Workspace.Under(Store, revision);
        string manifestPath = Workspace.Under(directory, "engine-manifest.json");
        var manifest = Json.Decode<EngineManifest>(File.ReadAllText(manifestPath));
        if (manifest.Revision != revision || Revision(manifest) != revision || manifest.Files.Length == 0)
            throw new IOException("核心版本清单不一致：" + revision);
        if (manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length)
            throw new IOException("核心版本清单存在重复路径");
        string executable = Workspace.Under(directory, manifest.Executable);
        if (!manifest.Files.Any(f => string.Equals(Workspace.Under(directory, f.Path), executable, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("核心版本清单缺少启动程序");
        foreach (var file in manifest.Files) {
            string path = Workspace.Under(directory, file.Path);
            string[] components = file.Path.Split(['/', '\\'], StringSplitOptions.None);
            bool code = components.Length == 1 ? IsCodeFile(components[0])
                : IsCodeDirectory(components[0]) || (components.Length >= 3
                    && components[0].Equals("Core", StringComparison.OrdinalIgnoreCase)
                    && CoreCodeDirectories.Contains(components[1]));
            if (!code)
                throw new IOException("核心版本清单包含非代码路径：" + file.Path);
            if (file.Bytes < 0 || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new IOException("核心版本文件记录无效");
            if (verifyFiles && (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || Hashes.FileSha(path) != file.Sha256))
                throw new IOException("核心版本文件 SHA / 长度不一致：" + file.Path);
        }
        if (verifyFiles) {
            var expected = manifest.Files.Select(f => f.Path.Replace('/', Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Enumerate(directory)) {
                string relative = Path.GetRelativePath(directory, path);
                if (relative != "engine-manifest.json" && !expected.Contains(relative)) throw new IOException("核心版本目录存在未索引文件：" + relative);
            }
        }
        return (new(revision, manifest.Name, manifest.Version, manifest.Files.Sum(f => f.Bytes), manifest.Files.Length, directory), manifest);
    }

    public EngineArtifact Import(ServerProfile profile) {
        string source = workspace.ServerDirectory(profile);
        string executable = workspace.ServerExecutable(profile);
        if (!File.Exists(executable)) throw new IOException("原服核心启动程序不存在");
        var paths = new List<string>();
        foreach (string file in Directory.GetFiles(source)) if (IsCodeFile(Path.GetFileName(file))) { Workspace.EnsureNoLinks(file); paths.Add(file); }
        foreach (string directory in Directory.GetDirectories(source)) {
            string directoryName = Path.GetFileName(directory);
            if (IsCodeDirectory(directoryName)) paths.AddRange(Enumerate(directory));
            else if (directoryName.Equals("Core", StringComparison.OrdinalIgnoreCase)) {
                Workspace.EnsureNoLinks(directory);
                foreach (string child in Directory.GetDirectories(directory))
                    if (CoreCodeDirectories.Contains(Path.GetFileName(child))) paths.AddRange(Enumerate(child));
            }
        }
        if (!paths.Contains(executable, StringComparer.OrdinalIgnoreCase)) throw new IOException("启动程序必须是根部可执行文件");
        var files = paths.Select(p => new EngineFile(Path.GetRelativePath(source, p).Replace('\\', '/'), new FileInfo(p).Length, Hashes.FileSha(p)))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        string name = "UnifierTSL", version = "unknown";
        string assembly = Path.Combine(source, "UnifierTSL.dll");
        if (File.Exists(assembly)) {
            var identity = AssemblyName.GetAssemblyName(assembly); name = identity.Name ?? name; version = identity.Version?.ToString() ?? version;
        }
        var pending = new EngineManifest("", name, version, profile.Executable, files);
        string revision = Revision(pending);
        if (Directory.Exists(Workspace.Under(Store, revision))) return Resolve(revision);
        string stage = Workspace.Under(Store, ".stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try {
            foreach (var file in files) {
                string original = Workspace.Under(source, file.Path);
                string target = Workspace.Under(stage, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(original, target, false);
                if (new FileInfo(target).Length != file.Bytes || Hashes.FileSha(target) != file.Sha256 || Hashes.FileSha(original) != file.Sha256)
                    throw new IOException("收录过程中核心输入变化：" + file.Path);
            }
            Json.Save(Path.Combine(stage, "engine-manifest.json"), pending with { Revision = revision });
            string final = Workspace.Under(Store, revision);
            try { Directory.Move(stage, final); }
            catch (IOException) when (Directory.Exists(final)) { return Resolve(revision); }
            var artifact = Resolve(revision);
            workspace.Audit("engine-import", profile.Id, new { artifact.Revision, artifact.Bytes, artifact.FileCount, source });
            return artifact;
        } catch {
            workspace.Audit("engine-import-failed", profile.Id, new { stage, preserved = true }); throw;
        }
    }
    static IEnumerable<string> Enumerate(string directory) {
        Workspace.EnsureNoLinks(directory);
        foreach (string entry in Directory.GetFileSystemEntries(directory)) {
            Workspace.EnsureNoLinks(entry);
            if (Directory.Exists(entry)) foreach (string child in Enumerate(entry)) yield return child;
            else yield return entry;
        }
    }
}

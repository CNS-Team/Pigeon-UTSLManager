using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pigeon.Manager;

// Generations separate processes and data paths; they are not an OS security boundary.
public sealed partial class SharedRuntime(Workspace workspace) {
    static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    object Gate => Gates.GetOrAdd(Path.TrimEndingDirectorySeparator(workspace.Root), _ => new());
    static readonly HashSet<string> PersistentDirectories = new(StringComparer.OrdinalIgnoreCase) {
        "config", "Data", "Worlds", "world", "logs", "backups", "crash-reports", "dumps", "Core", "GenerateMap"
    };
    static bool IgnoredRoot(string name) => name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".manager-runtime-process.json", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".manager-runtime-owner.lock", StringComparison.OrdinalIgnoreCase);
    sealed record SupportFile(string Module, string OwnerSha256, string Path, long Bytes, string Sha256, string? Source = null);
    sealed record SupportBinding(string Module, string OwnerSha256, SupportFile[] Files);
    sealed record SelectionDocument(bool Enabled, PluginArtifact[] Plugins, string? EngineRevision = null,
        SupportFile[]? Support = null, int SchemaVersion = 1);
    sealed record CodeFile(string Path, string Source, long Bytes, string Sha256, bool Share);
    sealed record PreparedPlugin(PluginArtifact Artifact, string Source, string Destination, ModuleMetadata Metadata);

    string Store => CreateDirectory(Safe(workspace.Data, "Library/Plugins"));
    ReadableLibrary PluginStore => new(Store);
    static string PluginKey(PluginArtifact a) => a.Sha256 + "/" + a.FileName.ToUpperInvariant();
    string SelectionPath(ServerProfile profile) {
        ValidateId(profile.Id);
        return Safe(CreateDirectory(Safe(workspace.Data, "runtime-selections")), profile.Id + ".json");
    }
    SelectionDocument ReadSelection(ServerProfile profile) {
        string path = SelectionPath(profile);
        var result = File.Exists(path) ? Json.Decode<SelectionDocument>(File.ReadAllText(path)) : new(false, []);
        if (result.Plugins is null || result.SchemaVersion != 1) throw new IOException("BLOCKED_SELECTION_SCHEMA");
        ValidateSelection(result.Plugins);
        return result;
    }
    public RuntimeSelection GetSelection(ServerProfile profile) {
        lock (Gate) {
            var value = ReadSelection(profile);
            return new(value.Enabled, value.Plugins, value.EngineRevision);
        }
    }
    public PluginArtifact[] Catalog() {
        lock (Gate) {
            var artifacts = new List<PluginArtifact>();
            foreach (var entry in PluginStore.Entries()) {
                string file = PluginStore.Find(entry.Key)!;
                var artifact = Describe(file);
                if (PluginKey(artifact) != entry.Key) throw new IOException("BLOCKED_STORE_SHA: " + file);
                artifacts.Add(artifact);
            }
            foreach (string file in Directory.EnumerateFiles(workspace.Library, "*.dll")) {
                var artifact = Describe(file);
                // Visible aliases of pinned variants must not become new module identities.
                if (!artifacts.Any(a => a.Sha256 == artifact.Sha256 && a.Name == artifact.Name)) artifacts.Add(artifact);
            }
            return artifacts.DistinctBy(a => (a.Sha256, a.FileName.ToUpperInvariant()))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Version, StringComparer.Ordinal)
                .ThenBy(a => a.Sha256, StringComparer.Ordinal).ToArray();
        }
    }
    public PluginArtifact[] Import(string[] paths) {
        ArgumentNullException.ThrowIfNull(paths);
        lock (Gate) {
            // Inspect all input before publishing any content-addressed artifact.
            var inputs = paths.Select(p => (Path: Path.GetFullPath(p), Artifact: Describe(p))).ToArray();
            foreach (var input in inputs) {
                Publish(input.Path, input.Artifact);
                ImportModuleSupport(input.Path, input.Artifact);
            }
            return inputs.Select(p => p.Artifact).ToArray();
        }
    }
    public void SaveSelection(ServerProfile profile, RuntimeSelection selection) {
        ArgumentNullException.ThrowIfNull(selection);
        lock (Gate) {
            string path = SelectionPath(profile);
            ValidateSelection(selection.Plugins);
            foreach (var artifact in selection.Plugins) ResolvePlugin(artifact);
            // Engine revisions are checked by Prepare, so a failed revision can still be inspected in the UI.
            if (selection.EngineRevision is { } revision) ValidateHash(revision);
            var support = PinSupport(profile, selection.Plugins);
            Json.Save(path, new SelectionDocument(selection.Enabled, selection.Plugins, selection.EngineRevision, support));
        }
    }
    // Explicit migration only: retain exact module owner/support bytes but drop
    // the obsolete source path after the source plugin tree has been archived.
    internal void DetachSupportToLibrary(ServerProfile profile) {
        lock (Gate) {
            var value = ReadSelection(profile);
            foreach (var file in value.Support ?? []) Verify(SupportObject(file), file.Bytes, file.Sha256);
            Json.Save(SelectionPath(profile), value with { Support = (value.Support ?? []).Select(f => f with { Source = null }).ToArray() });
        }
    }
    internal void MigrateLegacySupport() {
        lock (Gate) {
            string old = Safe(workspace.Library, ".module-support");
            if (!Directory.Exists(old)) return;
            foreach (string path in Directory.GetFiles(old, "*.json")) {
                NoLinks(path);
                var binding = Json.Decode<SupportBinding>(File.ReadAllText(path));
                if (Path.GetFileNameWithoutExtension(path) != binding.OwnerSha256) throw new IOException("BLOCKED_LEGACY_BINDING_OWNER");
                foreach (var file in binding.Files) Verify(SupportObject(file), file.Bytes, file.Sha256);
                BindingPath(binding.OwnerSha256);
            }
        }
    }
    public RuntimeLaunch Prepare(ServerProfile profile) {
        lock (Gate) {
            ValidateId(profile.Id);
            ValidateLeaf(profile.Executable);
            string source = workspace.ServerDirectory(profile);
            NoLinks(source);
            var selection = ReadSelection(profile);
            string originalExe = Safe(source, profile.Executable);
            if (!selection.Enabled) return new(source, originalExe, "", "");
            string generation = Guid.NewGuid().ToString("N");
            string parent = workspace.RuntimeRoot(profile); CreateDirectory(parent);
            int run = 1; string directory;
            do { directory = Safe(parent, "run-" + run++.ToString("D4")); } while (Directory.Exists(directory));
            Directory.CreateDirectory(directory);
            try {
                var plugins = PlanPlugins(selection.Plugins);
                string engineRoot = source;
                var code = new List<CodeFile>();
                if (selection.EngineRevision is { } revision) {
                    var library = new EngineLibrary(workspace);
                    engineRoot = library.Resolve(revision).Directory;
                    var manifest = library.Manifest(revision);
                    if (!manifest.Executable.Equals(profile.Executable, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("BLOCKED_ENGINE_EXECUTABLE_MISMATCH: " + manifest.Executable);
                    foreach (var file in manifest.Files) {
                        ValidateRelative(file.Path);
                        string rootName = file.Path.Split('/', '\\')[0];
                        if (!IsRuntimeCodePath(file.Path))
                            throw new IOException("BLOCKED_ENGINE_NONCODE: " + file.Path);
                        code.Add(new(file.Path, Safe(engineRoot, file.Path), file.Bytes, file.Sha256, true));
                    }
                }
                var persistent = new List<(string Name, string Target)>();
                var ignored = new List<string>();
                foreach (string entry in Directory.EnumerateFileSystemEntries(source)) {
                    string name = Path.GetFileName(entry);
                    // Do not open Host's exclusive owner lock or copy historical process identity.
                    if (IgnoredRoot(name)) { ignored.Add(name); continue; }
                    NoLinks(entry);
                    if (Directory.Exists(entry)) {
                        if (name.Equals("plugins", StringComparison.OrdinalIgnoreCase)) continue;
                        if (name.Equals("Core", StringComparison.OrdinalIgnoreCase) && (HasCoreCode(entry) || code.Any(f => f.Path.StartsWith("Core/", StringComparison.OrdinalIgnoreCase)))) {
                            PlanCoreDirectory(entry, engineRoot, selection.EngineRevision is not null, code, persistent);
                        } else if (EngineLibrary.IsCodeDirectory(name)) {
                            if (name.Equals("lib", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Safe(source, "lib/Saves"))) {
                                string saves = Safe(source, "lib/Saves"); ValidatePersistentTree(saves);
                                if (workspace.SharedWorlds && Directory.EnumerateFileSystemEntries(saves).Any())
                                    throw new IOException("BLOCKED_LEGACY_ENGINE_SAVES_RECONCILIATION_REQUIRED");
                                if (!workspace.SharedWorlds) persistent.Add(("lib/Saves", saves));
                            }
                            if (selection.EngineRevision is null) AddCodeTree(entry, name, code);
                        } else if (PersistentDirectories.Contains(name)) {
                            ValidatePersistentTree(entry);
                            persistent.Add((name, WorldTarget(name, entry)));
                        } else throw new IOException("BLOCKED_OPEN_ROOT_DIRECTORY_POLICY: " + name);
                    } else if (EngineLibrary.IsCodeFile(name)) {
                        if (selection.EngineRevision is null) code.Add(InspectCode(entry, name));
                    } else throw new IOException("BLOCKED_OPEN_ROOT_MUTABLE_FILE_POLICY: " + name);
                }
                if (code.Any(f => f.Path.StartsWith("Core/", StringComparison.OrdinalIgnoreCase))
                    && !persistent.Any(p => p.Name.StartsWith("Core/", StringComparison.OrdinalIgnoreCase)))
                    PlanCoreDirectory(CreateDirectory(Safe(source, "Core")), engineRoot, selection.EngineRevision is not null, code, persistent);
                // First-run writes to these well-known locations must also reach the source.
                foreach (string name in PersistentDirectories) {
                    if (name.Equals("Core", StringComparison.OrdinalIgnoreCase) && code.Any(f => f.Path.StartsWith("Core/", StringComparison.OrdinalIgnoreCase))) continue;
                    if (persistent.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    string target = WorldTarget(name, Safe(source, name));
                    CreateDirectory(target);
                    persistent.Add((name, target));
                }
                var configPathChecks = ValidatePersistentConfigPaths(source);
                if (workspace.SharedWorlds) persistent.Add(("lib/Saves/Worlds", workspace.Worlds));
                ValidateDynamicProviders(plugins, selection, code);
                if (!code.Any(f => f.Path.Equals(profile.Executable, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("BLOCKED_MISSING_EXECUTABLE: " + profile.Executable);
                var files = new List<RuntimeFile>();
                foreach (var file in code) files.Add(Materialize(directory, file));
                CreateDirectory(Safe(directory, "plugins"));
                foreach (var plugin in plugins) {
                    var a = plugin.Artifact;
                    files.Add(Materialize(directory, new(plugin.Destination, plugin.Source, a.Bytes, a.Sha256, true)));
                }
                MaterializeSupport(directory, source, selection, plugins, files);
                ValidateModuleReferences(directory, plugins, files);
                var mappings = new List<RuntimeMapping>();
                foreach (var item in persistent) {
                    string link = Safe(directory, item.Name);
                    CreateDirectory(Path.GetDirectoryName(link)!);
                    CreateJunction(link, item.Target);
                    mappings.Add(new(item.Name, item.Target, "junction-server-persistent"));
                }
                // Hash every final immutable payload before handing a launch path to Host.
                foreach (var file in files) Verify(Safe(directory, file.Path), file.Bytes, file.Sha256);
                string manifestPath = Safe(directory, "runtime-manifest.json");
                var runtimeManifest = new RuntimeManifest(generation, profile.Id, source, engineRoot, selection.EngineRevision,
                    profile.Executable, selection.Plugins, files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(), mappings.ToArray());
                WriteNewJson(manifestPath, runtimeManifest);
                WriteNewJson(Safe(directory, "runtime-policy.json"), new { ignoredRootEntries = ignored, configPathChecks, originalCode = "verified-copy", sharedCode = "verified-hardlink-or-copy", moduleSupport = "private-verified-copy", dynamicProviders = "exact-tested-code-owner-support-fingerprints", optionalSupportApis = "UNVERIFIED: stock Drawing/EventLog/Windows.Extensions APIs; no dependency borrowed from another module lib", unknownRootMutable = "BLOCKED", securityIsolation = false });
                return new(directory, Safe(directory, profile.Executable), manifestPath, generation);
            } catch (Exception error) {
                string diagnostic = Path.Combine(directory, "prepare-failed.json");
                try { WriteNewJson(diagnostic, new { generation, profile.Id, source, error = error.ToString(), status = "BLOCKED", preserved = true }); }
                catch (Exception diagnosticError) { error.Data["DiagnosticWriteError"] = diagnosticError.Message; }
                throw new IOException("Runtime 准备被阻断；保留诊断：" + diagnostic + "；" + error.Message, error);
            }
        }
    }
    string WorldTarget(string name, string original) {
        if (!workspace.SharedWorlds || !(name.Equals("Worlds", StringComparison.OrdinalIgnoreCase) || name.Equals("world", StringComparison.OrdinalIgnoreCase))) return original;
        if (Directory.Exists(original) && Directory.EnumerateFileSystemEntries(original).Any())
            throw new IOException("BLOCKED_WORLD_MIGRATION_REQUIRED: " + original);
        ValidatePersistentTree(workspace.Worlds);
        return workspace.Worlds;
    }
    static void ValidateId(string id) {
        ValidateLeaf(id);
        if (id.Length > 100 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new IOException("BLOCKED_INVALID_PROFILE_ID");
    }
    static void ValidateLeaf(string name) {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl) || name.EndsWith('.') || name.EndsWith(' '))
            throw new IOException("BLOCKED_INVALID_PATH_COMPONENT: " + name);
        string stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
            throw new IOException("BLOCKED_DEVICE_PATH: " + name);
    }
    static void ValidateRelative(string relative) {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new IOException("BLOCKED_ROOTED_PATH");
        foreach (string part in relative.Split('/', '\\')) ValidateLeaf(part);
    }
    static string Safe(string root, string relative) {
        ValidateRelative(relative);
        string path = Workspace.Under(root, relative);
        NoLinks(path); return path;
    }
    static void NoLinks(string path) {
        for (string? item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item)) {
            try { if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("BLOCKED_REPARSE: " + item); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    static string CreateDirectory(string path) { NoLinks(path); Directory.CreateDirectory(path); NoLinks(path); return path; }
    static void ValidateHash(string hash) {
        if (hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || hash != hash.ToUpperInvariant())
            throw new IOException("BLOCKED_INVALID_SHA256");
    }
    static void ValidateSelection(PluginArtifact[] plugins) {
        if (plugins is null) throw new IOException("BLOCKED_NULL_PLUGIN_SELECTION");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins) {
            if (plugin is null) throw new IOException("BLOCKED_NULL_PLUGIN");
            ValidateLeaf(plugin.FileName); ValidateLeaf(plugin.Name); ValidateHash(plugin.Sha256);
            if (!plugin.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || plugin.Bytes <= 0 || string.IsNullOrEmpty(plugin.Version))
                throw new IOException("BLOCKED_INVALID_ARTIFACT");
            if (!names.Add(plugin.Name) || !filenames.Add(plugin.FileName)) throw new IOException("BLOCKED_DUPLICATE_ASSEMBLY: " + plugin.Name);
        }
    }
    static PluginArtifact Describe(string path) {
        path = Path.GetFullPath(path); NoLinks(path);
        string name = Path.GetFileName(path); ValidateLeaf(name);
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new IOException("BLOCKED_NOT_PLUGIN_DLL: " + name);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly) throw new IOException("BLOCKED_NOT_MANAGED_ASSEMBLY: " + name);
        var reader = pe.GetMetadataReader(); var definition = reader.GetAssemblyDefinition();
        var result = new PluginArtifact(name, reader.GetString(definition.Name), definition.Version.ToString(), stream.Length, Hashes.FileSha(path));
        ValidateSelection([result]); return result;
    }
    string Publish(string source, PluginArtifact artifact) {
        string destination = PluginStore.Reserve(PluginKey(artifact), artifact.Name + "-" + artifact.Version, artifact.FileName);
        if (!File.Exists(destination)) {
            string temp = Safe(Path.GetDirectoryName(destination)!, ".import-" + Guid.NewGuid().ToString("N"));
            try {
                CopyVerified(source, temp, artifact.Bytes, artifact.Sha256);
                try { File.Move(temp, destination, false); }
                catch (IOException) when (File.Exists(destination)) { Verify(destination, artifact.Bytes, artifact.Sha256); }
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        if (Describe(destination) != artifact) throw new IOException("BLOCKED_ARTIFACT_METADATA_OR_SHA: " + destination);
        // A TSM-style visible library: DLLs only, with readable variant names.
        string visible = Safe(workspace.Library, artifact.FileName);
        int variant = 1;
        while (File.Exists(visible) && Hashes.FileSha(visible) != artifact.Sha256)
            visible = Safe(workspace.Library, Path.GetFileNameWithoutExtension(artifact.FileName) + "-variant-" + variant++.ToString("D2") + ".dll");
        if (!File.Exists(visible)) {
            if (!(OperatingSystem.IsWindows() && CreateHardLinkW(visible, destination, IntPtr.Zero))) CopyVerified(destination, visible, artifact.Bytes, artifact.Sha256);
            Verify(visible, artifact.Bytes, artifact.Sha256);
        }
        return destination;
    }
    string ResolvePlugin(PluginArtifact artifact) {
        ValidateSelection([artifact]);
        string? destination = PluginStore.Find(PluginKey(artifact));
        if (destination != null && File.Exists(destination)) {
            if (Describe(destination) != artifact) throw new IOException("BLOCKED_ARTIFACT_METADATA_OR_SHA: " + destination);
            return destination;
        }
        string old = Safe(workspace.Library, ".store/" + artifact.Sha256 + "/" + artifact.FileName);
        if (File.Exists(old)) { if (Describe(old) != artifact) throw new IOException("BLOCKED_LEGACY_PLUGIN_SHA"); return Publish(old, artifact); }
        string legacy = Safe(workspace.Library, artifact.FileName);
        if (!File.Exists(legacy) || Describe(legacy) != artifact) throw new IOException("BLOCKED_ARTIFACT_MISSING: " + artifact.FileName);
        return Publish(legacy, artifact);
    }
    static CodeFile InspectCode(string path, string relative) { NoLinks(path); return new(relative, path, new FileInfo(path).Length, Hashes.FileSha(path), false); }
    static void AddCodeTree(string directory, string relative, List<CodeFile> code) {
        NoLinks(directory);
        foreach (string item in Directory.EnumerateFileSystemEntries(directory)) {
            string path = relative + "/" + Path.GetFileName(item);
            if (EngineLibrary.IsEngineDataPath(path)) continue;
            NoLinks(item);
            ValidateRelative(path);
            if (Directory.Exists(item)) AddCodeTree(item, path, code); else code.Add(InspectCode(item, path));
        }
    }
    static void ValidatePersistentTree(string directory) {
        // Never enumerate an unrecognized junction, even when nested below Worlds.
        NoLinks(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
            NoLinks(entry);
            if (Directory.Exists(entry)) ValidatePersistentTree(entry);
        }
    }
    static void Verify(string path, long bytes, string sha) {
        NoLinks(path); ValidateHash(sha);
        if (!File.Exists(path) || new FileInfo(path).Length != bytes || Hashes.FileSha(path) != sha)
            throw new IOException("BLOCKED_FILE_SHA_OR_LENGTH: " + path);
    }
    static void CopyVerified(string source, string destination, long bytes, string sha) {
        Verify(source, bytes, sha); NoLinks(destination);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
        Verify(destination, bytes, sha);
    }
    static RuntimeFile Materialize(string generation, CodeFile file) {
        Verify(file.Source, file.Bytes, file.Sha256);
        string target = Safe(generation, file.Path);
        CreateDirectory(Path.GetDirectoryName(target)!);
        string mode = "verified-copy";
        if (file.Share && OperatingSystem.IsWindows() && CreateHardLinkW(target, file.Source, IntPtr.Zero)) mode = "verified-hardlink";
        else CopyVerified(file.Source, target, file.Bytes, file.Sha256);
        Verify(target, file.Bytes, file.Sha256);
        return new(file.Path.Replace('\\', '/'), file.Source, file.Bytes, file.Sha256, mode);
    }
    static void WriteNewJson<T>(string path, T value) {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        byte[] bytes = Encoding.UTF8.GetBytes(Json.Encode(value)); stream.Write(bytes); stream.Flush(true);
    }
    static void CreateJunction(string path, string target) {
        if (!OperatingSystem.IsWindows()) throw new IOException("BLOCKED_JUNCTION_REQUIRES_WINDOWS");
        NoLinks(path); NoLinks(target);
        if (!Directory.Exists(target) || File.Exists(path) || Directory.Exists(path)) throw new IOException("BLOCKED_JUNCTION_PATH: " + path);
        Directory.CreateDirectory(path);
        string substitute = "\\??\\" + Path.GetFullPath(target);
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute), printBytes = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        int pathBytes = checked(substituteBytes.Length + printBytes.Length + 4);
        byte[] buffer = new byte[16 + pathBytes];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)(8 + pathBytes))).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substituteBytes.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substituteBytes.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)printBytes.Length)).CopyTo(buffer, 14);
        substituteBytes.CopyTo(buffer, 16); printBytes.CopyTo(buffer, 18 + substituteBytes.Length);
        using var handle = CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
            throw new IOException("BLOCKED_JUNCTION_CREATE: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        string? actual = new DirectoryInfo(path).LinkTarget;
        if (actual is null || !Path.GetFullPath(actual).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("BLOCKED_JUNCTION_VERIFY: " + path);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateHardLinkW(string path, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputBytes, byte[]? output, int outputBytes, out int returned, IntPtr overlapped);
}

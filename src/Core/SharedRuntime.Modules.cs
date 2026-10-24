using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Pigeon.Manager;

public sealed partial class SharedRuntime {
    sealed record ModuleMetadata(string Name, bool Core, bool DynamicDependencies, string? RequiresCore) {
        public string? Directory => Core || DynamicDependencies ? Name : RequiresCore;
    }
    static readonly HashSet<string> SupportDirectories = new(StringComparer.OrdinalIgnoreCase) { "lib", "runtimes", "native", "x64", "x86", "arm64", "resources" };
    static ModuleMetadata ReadModule(string path) {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader(); var assembly = reader.GetAssemblyDefinition();
        string name = reader.GetString(assembly.Name); ValidateLeaf(name);
        bool core = false, dependencies = false; string? requires = null; bool hasRequires = false;
        foreach (var handle in assembly.GetCustomAttributes()) {
            var attribute = reader.GetCustomAttribute(handle);
            EntityHandle type = attribute.Constructor.Kind switch {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default
            };
            if (type.Kind == HandleKind.TypeSpecification) {
                var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)type).Signature);
                if (blob.ReadByte() != 0x15 || blob.ReadByte() is not (0x12 or 0x11)) continue;
                type = blob.ReadTypeHandle();
            }
            (string Namespace, string Name) identity = type.Kind switch {
                HandleKind.TypeReference => (reader.GetString(reader.GetTypeReference((TypeReferenceHandle)type).Namespace), reader.GetString(reader.GetTypeReference((TypeReferenceHandle)type).Name)),
                HandleKind.TypeDefinition => (reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)type).Namespace), reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)type).Name)),
                _ => ("", "")
            };
            if (identity.Namespace != "UnifierTSL.Module") continue;
            switch (identity.Name) {
                case "CoreModuleAttribute":
                    if (core) throw new IOException("BLOCKED_DUPLICATE_CORE_ATTRIBUTE: " + name);
                    core = true; break;
                case "ModuleDependenciesAttribute`1":
                case "ModuleDependenciesAttribute":
                    if (dependencies) throw new IOException("BLOCKED_DUPLICATE_DEPENDENCIES_ATTRIBUTE: " + name);
                    dependencies = true; break;
                case "RequiresCoreModuleAttribute":
                    if (hasRequires) throw new IOException("BLOCKED_DUPLICATE_REQUIRES_CORE_ATTRIBUTE: " + name);
                    var blob = reader.GetBlobReader(attribute.Value);
                    if (blob.ReadUInt16() != 1) throw new IOException("BLOCKED_INVALID_REQUIRES_CORE_ATTRIBUTE: " + name);
                    requires = blob.ReadSerializedString(); ValidateLeaf(requires!); hasRequires = true; break;
            }
        }
        if (hasRequires && (core || dependencies)) throw new IOException("BLOCKED_CONFLICTING_MODULE_ATTRIBUTES: " + name);
        if (dependencies && Path.GetFileNameWithoutExtension(path) != name)
            throw new IOException("BLOCKED_DEPENDENCY_MODULE_FILENAME: " + name);
        return new(name, core, dependencies, requires);
    }
    PreparedPlugin[] PlanPlugins(PluginArtifact[] artifacts) {
        ValidateSelection(artifacts);
        var result = artifacts.Select(a => {
            string source = ResolvePlugin(a); var metadata = ReadModule(source);
            string destination = "plugins/" + (metadata.Directory is { } module ? module + "/" : "") + a.FileName;
            ValidateRelative(destination);
            return new PreparedPlugin(a, source, destination, metadata);
        }).ToArray();
        foreach (var plugin in result) {
            if (plugin.Metadata.RequiresCore is { } required && !result.Any(p => p.Metadata.Core && p.Metadata.Name == required))
                throw new IOException("BLOCKED_MISSING_REQUIRED_CORE_MODULE: " + plugin.Artifact.Name + " -> " + required);
        }
        if (result.Select(p => p.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new IOException("BLOCKED_MODULE_PATH_COLLISION");
        return result;
    }
    string SupportStore => CreateDirectory(Safe(workspace.Data, "Library/Dependencies"));
    string SupportObject(SupportFile file) {
        ValidateHash(file.Sha256); ValidateRelative(file.Path);
        var store = new ReadableLibrary(SupportStore);
        string key = file.Sha256 + "/" + Path.GetFileName(file.Path).ToUpperInvariant();
        string target = store.Find(key) ?? store.Reserve(key, Path.GetFileNameWithoutExtension(file.Path), Path.GetFileName(file.Path));
        string old = Safe(workspace.Library, ".support/" + file.Sha256 + "/" + Path.GetFileName(file.Path));
        if (!File.Exists(target) && File.Exists(old)) CopyVerified(old, target, file.Bytes, file.Sha256);
        return target;
    }
    void StoreSupport(string source, SupportFile file) {
        string target = SupportObject(file); CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) { Verify(target, file.Bytes, file.Sha256); return; }
        string temporary = Safe(Path.GetDirectoryName(target)!, ".import-" + Guid.NewGuid().ToString("N"));
        try {
            CopyVerified(source, temporary, file.Bytes, file.Sha256);
            try { File.Move(temporary, target, false); }
            catch (IOException) when (File.Exists(target)) { Verify(target, file.Bytes, file.Sha256); }
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    string BindingPath(string sha) {
        ValidateHash(sha);
        var store = new ReadableLibrary(CreateDirectory(Safe(workspace.Data, "Library/Modules")));
        string path = store.Find(sha) ?? store.Reserve(sha, "Module", "binding.json");
        string old = Safe(workspace.Library, ".module-support/" + sha + ".json");
        if (!File.Exists(path) && File.Exists(old)) {
            var binding = Json.Decode<SupportBinding>(File.ReadAllText(old));
            if (binding.OwnerSha256 != sha) throw new IOException("BLOCKED_LEGACY_SUPPORT_BINDING");
            WriteNewJson(path, binding);
        }
        return path;
    }
    void ImportModuleSupport(string dll, PluginArtifact artifact) {
        var metadata = ReadModule(dll);
        if (metadata.Directory is not { } module || metadata.RequiresCore is not null) return;
        string directory = Path.GetDirectoryName(dll)!;
        if (!Directory.EnumerateFileSystemEntries(directory).Any(p => SupportDirectories.Contains(Path.GetFileName(p))
            || Path.GetFileName(p).Equals("dependencies.json", StringComparison.OrdinalIgnoreCase))) return;
        var files = CaptureSupport(directory, artifact, module, bindSource: false);
        string bindingPath = BindingPath(artifact.Sha256);
        var binding = new SupportBinding(module, artifact.Sha256, files);
        if (File.Exists(bindingPath)) {
            var existing = Json.Decode<SupportBinding>(File.ReadAllText(bindingPath));
            if (Json.Encode(existing) != Json.Encode(binding)) throw new IOException("BLOCKED_MODULE_SUPPORT_ALREADY_PINNED: " + artifact.Name);
        } else WriteNewJson(bindingPath, binding);
    }
    SupportFile[] CaptureSupport(string directory, PluginArtifact owner, string module, bool bindSource) {
        NoLinks(directory);
        var result = new List<SupportFile>();
        void Add(string source, string relative) {
            ValidateRelative(relative); NoLinks(source);
            var file = new SupportFile(module, owner.Sha256, relative.Replace('\\', '/'), new FileInfo(source).Length,
                Hashes.FileSha(source), bindSource ? source : null);
            StoreSupport(source, file); result.Add(file);
        }
        void Visit(string path, string relative) {
            NoLinks(path);
            foreach (string child in Directory.EnumerateFileSystemEntries(path)) {
                NoLinks(child);
                string childRelative = relative + "/" + Path.GetFileName(child);
                ValidateRelative(childRelative);
                if (Directory.Exists(child)) Visit(child, childRelative); else Add(child, childRelative);
            }
        }
        foreach (string item in Directory.EnumerateFileSystemEntries(directory)) {
            string name = Path.GetFileName(item);
            if (SupportDirectories.Contains(name)) {
                NoLinks(item);
                if (!Directory.Exists(item)) throw new IOException("BLOCKED_MODULE_SUPPORT_DIRECTORY: " + item);
                Visit(item, name);
            } else if (name.Equals("dependencies.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals(Path.ChangeExtension(owner.FileName, ".pdb"), StringComparison.OrdinalIgnoreCase)) Add(item, name);
            // Other module-level DLLs belong to independent selection, never to support.
            else if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                throw new IOException("BLOCKED_OPEN_MODULE_SUPPORT_POLICY: " + item);
        }
        ValidateDependenciesConfig(directory, result.ToArray(), ReadModule(ResolvePlugin(owner)).DynamicDependencies);
        return result.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }
    SupportFile[] PinSupport(ServerProfile profile, PluginArtifact[] artifacts) {
        var plugins = PlanPlugins(artifacts);
        string serverRoot = workspace.ServerDirectory(profile);
        var result = new List<SupportFile>();
        foreach (var plugin in plugins.Where(p => p.Metadata.Directory != null && p.Metadata.RequiresCore is null)) {
            string module = plugin.Metadata.Directory!;
            string directory = Safe(serverRoot, "plugins/" + module);
            string ownerPath = Safe(directory, plugin.Artifact.FileName);
            bool hasSupport = Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory)
                .Any(p => SupportDirectories.Contains(Path.GetFileName(p)) || Path.GetFileName(p).Equals("dependencies.json", StringComparison.OrdinalIgnoreCase));
            if (File.Exists(ownerPath) && Describe(ownerPath) == plugin.Artifact) {
                result.AddRange(CaptureSupport(directory, plugin.Artifact, module, bindSource: true));
                continue;
            }
            string bindingPath = BindingPath(plugin.Artifact.Sha256);
            if (File.Exists(bindingPath)) {
                var binding = Json.Decode<SupportBinding>(File.ReadAllText(bindingPath));
                if (binding.OwnerSha256 != plugin.Artifact.Sha256 || binding.Module != module || binding.Files is null)
                    throw new IOException("BLOCKED_INVALID_MODULE_SUPPORT_BINDING: " + module);
                foreach (var file in binding.Files) Verify(SupportObject(file), file.Bytes, file.Sha256);
                result.AddRange(binding.Files);
            } else if (hasSupport || plugin.Metadata.DynamicDependencies) {
                throw new IOException("MISSING_MODULE_SUPPORT: " + module + "；所选 owner SHA 与原服不符或 support 缺失，请从完整模块目录导入 DLL");
            }
        }
        return result.ToArray();
    }
    void MaterializeSupport(string directory, string serverRoot, SelectionDocument selection, PreparedPlugin[] plugins, List<RuntimeFile> files) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in selection.Support ?? []) {
            ValidateLeaf(file.Module); ValidateHash(file.OwnerSha256); ValidateRelative(file.Path);
            var owner = plugins.SingleOrDefault(p => p.Metadata.RequiresCore is null && p.Metadata.Directory == file.Module && p.Artifact.Sha256 == file.OwnerSha256)
                ?? throw new IOException("BLOCKED_MODULE_SUPPORT_OWNER: " + file.Module);
            bool root = file.Path.IndexOfAny(['/', '\\']) < 0;
            if (root ? !(file.Path.Equals("dependencies.json", StringComparison.OrdinalIgnoreCase) || file.Path.Equals(Path.ChangeExtension(owner.Artifact.FileName, ".pdb"), StringComparison.OrdinalIgnoreCase))
                : !SupportDirectories.Contains(file.Path.Split('/', '\\')[0])) throw new IOException("BLOCKED_MODULE_SUPPORT_LOAD_PATH: " + file.Path);
            string relative = "plugins/" + file.Module + "/" + file.Path;
            if (!seen.Add(relative)) throw new IOException("BLOCKED_DUPLICATE_SUPPORT_FILE: " + relative);
            if (file.Source is { } source) {
                string expected = Safe(serverRoot, relative);
                if (!Path.GetFullPath(source).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("BLOCKED_SUPPORT_SOURCE_BINDING: " + relative);
                Verify(expected, file.Bytes, file.Sha256);
                string currentOwner = Safe(serverRoot, "plugins/" + file.Module + "/" + owner.Artifact.FileName);
                Verify(currentOwner, owner.Artifact.Bytes, owner.Artifact.Sha256);
            }
            // UTSL rewrites dependencies.json and may restore support: every support file is a private copy.
            files.Add(Materialize(directory, new(relative, SupportObject(file), file.Bytes, file.Sha256, false)));
        }
        foreach (var plugin in plugins.Where(p => p.Metadata.Directory != null && p.Metadata.RequiresCore is null)) {
            string module = plugin.Metadata.Directory!;
            var support = (selection.Support ?? []).Where(f => f.Module == module).ToArray();
            string moduleRoot = Safe(directory, "plugins/" + module);
            ValidateDependenciesConfig(moduleRoot, support, plugin.Metadata.DynamicDependencies);
        }
    }
    static void ValidateDependenciesConfig(string moduleRoot, SupportFile[] files, bool required) {
        var config = files.SingleOrDefault(f => f.Path.Equals("dependencies.json", StringComparison.OrdinalIgnoreCase));
        if (config is null) {
            if (required || files.Any(f => f.Path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith("native/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("MISSING_MODULE_SUPPORT: " + moduleRoot + "/dependencies.json");
            return;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(Safe(moduleRoot, config.Path)));
        if (!document.RootElement.TryGetProperty("Dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
            throw new IOException("BLOCKED_DEPENDENCIES_SCHEMA: " + moduleRoot);
        if (document.RootElement.TryGetProperty("EnableAggressiveCleanUp", out var aggressive) && aggressive.GetBoolean())
            throw new IOException("BLOCKED_OPEN_AGGRESSIVE_DEPENDENCY_CLEANUP: " + moduleRoot);
        foreach (var dependency in dependencies.EnumerateObject()) {
            if (!dependency.Value.TryGetProperty("Manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
                throw new IOException("BLOCKED_DEPENDENCIES_SCHEMA: " + dependency.Name);
            foreach (var item in manifests.EnumerateArray()) {
                string relative = item.GetProperty("FilePath").GetString() ?? "";
                ValidateRelative(relative);
                if (relative.IndexOfAny(['/', '\\']) < 0 || !SupportDirectories.Contains(relative.Split('/', '\\')[0]))
                    throw new IOException("BLOCKED_DEPENDENCY_CAN_OVERWRITE_MODULE: " + relative);
                bool obsolete = item.TryGetProperty("Obsolete", out var value) && value.GetBoolean();
                var pinned = files.SingleOrDefault(f => f.Path.Equals(relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (!obsolete && pinned is null) throw new IOException("MISSING_MODULE_SUPPORT: " + moduleRoot + "/" + relative);
                if (!obsolete) Verify(Safe(moduleRoot, relative), pinned!.Bytes, pinned.Sha256);
            }
        }
        // The provider is executable plugin code. Its restore behaviour still needs real-core validation;
        // private copies ensure a restore cannot modify the content store or another generation's support.
    }
    static void ValidateModuleReferences(string directory, PreparedPlugin[] plugins, List<RuntimeFile> files) {
        var framework = Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assemblies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) {
            string path = Safe(directory, file.Path);
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var reader = pe.GetMetadataReader(); if (!reader.IsAssembly) continue;
            string name = reader.GetString(reader.GetAssemblyDefinition().Name);
            if (!assemblies.TryGetValue(name, out var list)) assemblies.Add(name, list = []);
            list.Add(file.Path.Replace('\\', '/'));
        }
        foreach (var file in files.Where(f => f.Path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) {
            using var stream = File.OpenRead(Safe(directory, file.Path)); using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var reader = pe.GetMetadataReader(); if (!reader.IsAssembly) continue;
            var owner = plugins.FirstOrDefault(p => p.Destination == file.Path || (p.Metadata.Directory != null && file.Path.StartsWith("plugins/" + p.Metadata.Directory + "/", StringComparison.OrdinalIgnoreCase)));
            if (owner is null) throw new IOException("BLOCKED_MODULE_DEPENDENCY_OWNER: " + file.Path);
            string moduleRoot = Path.GetDirectoryName(owner.Destination)!.Replace('\\', '/');
            using var ownerStream = File.OpenRead(Safe(directory, owner.Destination));
            using var ownerPe = new PEReader(ownerStream);
            var ownerReader = ownerPe.GetMetadataReader();
            var ownerReferences = ownerReader.AssemblyReferences.Select(h => ownerReader.GetString(ownerReader.GetAssemblyReference(h).Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var handle in reader.AssemblyReferences) {
                string name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                if (framework.Contains(name)) continue;
                bool resolved = assemblies.TryGetValue(name, out var paths) && paths.Any(p => !p.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)
                    || p.Equals(moduleRoot + "/lib/" + name + ".dll", StringComparison.OrdinalIgnoreCase)
                    || plugins.Any(selected => selected.Artifact.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    // LoadedModule.TryProxyLoad exposes lib only when the requester's
                    // entry assembly explicitly references that selected module.
                    || plugins.Any(exporter => ownerReferences.Contains(exporter.Artifact.Name)
                        && p.Equals(Path.GetDirectoryName(exporter.Destination)!.Replace('\\', '/') + "/lib/" + name + ".dll", StringComparison.OrdinalIgnoreCase)));
                // This exact stock support payload was loaded with each verified
                // complete provider combination; ValidateDynamicProviders runs first.
                // Drawing APIs remain UNVERIFIED; this is not permission to omit pinned files.
                if (!resolved && VerifiedOptionalSupportReference(file, name)
                    && plugins.Any(p => p.Artifact.Sha256 is
                        "5A443304C68C97F2ABAD251DA1688EF5EB62D1F36B515582DEC19E21A5F31794" or
                        "045B13ED39F4F7D0D8E891E1EDC523E09AD58E43DEB4E15BD476FB552A8A030E" or
                        "781A83BC49508CF568528303759E2990426443C56CC5665647BB0590C249A48F")
                    && plugins.Any(p => p.Artifact.Sha256 == "1553B4B3BFD332A79148B53EEDCBB8B8E8EC6301AE51F921B8E4D3666BBF67F3")) continue;
                if (!resolved) throw new IOException("MISSING_MODULE_SUPPORT: " + file.Path + " -> " + name);
            }
        }
    }
    static bool VerifiedOptionalSupportReference(RuntimeFile file, string reference) => (file.Path, file.Sha256, reference) is
        ("plugins/TShockAPI/lib/System.Windows.Extensions.dll", "878F568D394CBBA6E52404891A24E462C2E5BCFE9F33E0F3C1F30DE8CA6D8348", "System.Drawing.Common") or
        ("plugins/CustomPlayer/lib/System.Configuration.ConfigurationManager.dll", "89D52B4A10B13BF98698847A4DAF5BE995226550B18F85808DBF4E80BA714676", "System.Diagnostics.EventLog") or
        ("plugins/CustomPlayer/lib/System.Security.Permissions.dll", "EC27C1EEA2DDD18929B07FC6094DD374CE38BC25D110A21DCC77B6373346F4EA", "System.Windows.Extensions");
}

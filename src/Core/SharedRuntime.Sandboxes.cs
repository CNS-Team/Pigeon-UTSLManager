using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pigeon.Manager;

public sealed partial class SharedRuntime {
    const string SandboxOwnerFile = ".manager-sandbox.json";
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> SandboxPreparations = new(StringComparer.OrdinalIgnoreCase);
    sealed class SandboxPreparation(string key) : IDisposable {
        public void Dispose() => SandboxPreparations.TryRemove(key, out _);
    }
    IDisposable EnterSandboxPreparation(ServerProfile profile) {
        ValidateId(profile.Id);
        string key = workspace.Root + "/" + profile.Id;
        if (!SandboxPreparations.TryAdd(key, 0)) throw new IOException("BLOCKED_SANDBOX_PREPARE_BUSY");
        return new SandboxPreparation(key);
    }
    sealed record SandboxOwner(int Schema, string Workspace, string ProfileId, string Folder,
        string ServerDirectory, string GenerationId, string ManifestSha256, string PolicySha256);
    sealed record SandboxIndex(int Schema, string Workspace, string ProfileId, string Folder,
        string ServerDirectory, SandboxOwner? Current, SandboxOwner? Previous);
    sealed record SandboxPaths(string Current, string Previous, string Pending, string Index, SandboxIndex State);
    string SandboxSlotRoot(ServerProfile profile) => Safe(workspace.Root, "Core/Runtime/.manager-slots/" + profile.Id);
    string SandboxIndexPath(ServerProfile profile) { ValidateId(profile.Id); return Safe(workspace.Data, "runtime-slots/" + profile.Id + ".json"); }
    public bool NamedSandboxesEnabled(ServerProfile profile) => File.Exists(SandboxIndexPath(profile));
    void AssertNoSandboxTransaction(ServerProfile profile) {
        string slots = SandboxSlotRoot(profile);
        if (File.Exists(Safe(slots, "publish-intent.json")) || File.Exists(Safe(slots, "legacy-migration.pending.json")))
            throw new IOException("BLOCKED_SANDBOX_TRANSACTION_PRESERVED: " + slots);
        if (!NamedSandboxesEnabled(profile) && File.Exists(Safe(workspace.RuntimeRoot(profile), SandboxOwnerFile)))
            throw new IOException("BLOCKED_SANDBOX_INDEX_MISSING");
    }
    public void EnableNamedSandboxes(ServerProfile profile) {
        using var preparation = EnterSandboxPreparation(profile);
        ValidateLeaf(profile.Folder); ValidateLeaf(profile.Executable);
        string source = workspace.ServerDirectory(profile);
        using var ownerLease = LockSandboxSource(source);
        lock (Gate) {
            using var libraryLease = LockPluginLibrary();
            AssertNoSandboxTransaction(profile); AssertSandboxStopped(profile, source);
            if (NamedSandboxesEnabled(profile)) throw new IOException("BLOCKED_SANDBOX_ALREADY_ENABLED");
            string current = workspace.RuntimeRoot(profile), slots = SandboxSlotRoot(profile);
            if (Directory.Exists(current) || File.Exists(current) || Directory.Exists(slots) || File.Exists(slots))
                throw new IOException("BLOCKED_SANDBOX_EXPLICIT_LEGACY_MIGRATION_REQUIRED");
            if (profile.Folder.Equals(".manager-slots", StringComparison.OrdinalIgnoreCase)) throw new IOException("BLOCKED_SANDBOX_RESERVED_FOLDER");
            string index = SandboxIndexPath(profile); CreateDirectory(Path.GetDirectoryName(index)!);
            WriteNewJson(index, new SandboxIndex(1, workspace.Root, profile.Id, profile.Folder, source, null, null));
        }
    }

    static FileStream LockSandboxSource(string source) {
        try {
            return new FileStream(Safe(source, ".manager-runtime-owner.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        } catch (IOException e) { throw new IOException("BLOCKED_SANDBOX_EXTERNAL_OWNER", e); }
    }
    static bool SamePath(string a, string b) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(a))
        .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    static bool InsidePath(string file, string root) => Path.GetFullPath(file).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    void AssertSandboxStopped(ServerProfile profile, string source) {
        string markerPath = Safe(source, ".manager-runtime-process.json");
        if (File.Exists(markerPath)) {
            try {
                var marker = Json.Decode<RuntimeProcessMarker>(File.ReadAllText(markerPath));
                if (!SamePath(marker.ServerDirectory, source) || marker.Identity is not { Pid: > 0, StartedUtcTicks: > 0 }
                    || marker.Launch is null || !Path.IsPathFullyQualified(marker.Identity.Executable)
                    || !SamePath(marker.Identity.Executable, marker.Launch.Executable)
                    || !SamePath(marker.Launch.Directory, Path.GetDirectoryName(marker.Launch.Executable)!))
                    throw new IOException("BLOCKED_SANDBOX_MARKER_MISMATCH");
                Process? process = null;
                try { process = Process.GetProcessById(marker.Identity.Pid); } catch (ArgumentException) { }
                using (process) {
                    if (process is not null && !process.HasExited && process.StartTime.ToUniversalTime().Ticks == marker.Identity.StartedUtcTicks) {
                        // Same PID + start time but a mismatched/unreadable path is unknown, never stopped.
                        if (!SamePath(process.MainModule?.FileName ?? throw new IOException("BLOCKED_SANDBOX_PROCESS_UNKNOWN"), marker.Identity.Executable))
                            throw new IOException("BLOCKED_SANDBOX_MARKER_PROCESS_PATH");
                        throw new IOException("BLOCKED_SANDBOX_RUNNING_MARKER");
                    }
                }
            } catch (Exception e) when (e is not IOException) { throw new IOException("BLOCKED_SANDBOX_MARKER_UNREADABLE", e); }
        }
        // Fresh native scan, including old run-N/GUID generations without a marker.
        string named = workspace.RuntimeRoot(profile);
        string old = Safe(workspace.Root, "Runtime/" + profile.Id);
        string slots = Safe(workspace.Root, "Core/Runtime/.manager-slots/" + profile.Id);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(profile.Executable))) using (process) {
            try {
                if (process.HasExited) continue;
                string exe = process.MainModule?.FileName ?? throw new IOException("BLOCKED_SANDBOX_PROCESS_UNKNOWN");
                if (SamePath(exe, Path.Combine(source, profile.Executable)) || InsidePath(exe, named)
                    || InsidePath(exe, old) || InsidePath(exe, slots))
                    throw new IOException("BLOCKED_SANDBOX_RUNNING_NATIVE: " + process.Id);
            } catch (InvalidOperationException) when (process.HasExited) { }
            catch (System.ComponentModel.Win32Exception e) { throw new IOException("BLOCKED_SANDBOX_PROCESS_UNREADABLE", e); }
        }
    }
    SandboxPaths CreateReadableRuntime(ServerProfile profile, string source, string generation) {
        ValidateLeaf(profile.Folder);
        if (profile.Folder.Equals(".manager-slots", StringComparison.OrdinalIgnoreCase))
            throw new IOException("BLOCKED_SANDBOX_RESERVED_FOLDER");
        string current = workspace.RuntimeRoot(profile);
        string slots = Safe(workspace.Root, "Core/Runtime/.manager-slots/" + profile.Id);
        string previous = Safe(slots, "previous"), pending = Safe(slots, "pending");
        string index = Safe(workspace.Data, "runtime-slots/" + profile.Id + ".json");
        var state = new SandboxIndex(1, workspace.Root, profile.Id, profile.Folder, source, null, null);
        if (File.Exists(index)) {
            state = Json.Decode<SandboxIndex>(File.ReadAllText(index));
            if (state.Schema != 1 || !SamePath(state.Workspace, workspace.Root) || state.ProfileId != profile.Id
                || state.Folder != profile.Folder || !SamePath(state.ServerDirectory, source))
                throw new IOException("BLOCKED_SANDBOX_INDEX_OWNER");
        }
        if (Directory.Exists(pending) || File.Exists(pending)) throw new IOException("BLOCKED_SANDBOX_PENDING_PRESERVED: " + pending);
        var paths = new SandboxPaths(current, previous, pending, index, state);
        CheckSandboxSlot(profile, source, current, state.Current);
        CheckSandboxSlot(profile, source, previous, state.Previous);
        CreateDirectory(Path.GetDirectoryName(current)!);
        CreateDirectory(pending);
        // An occupied pending slot is never reused, even if preparation was interrupted.
        WriteNewJson(Safe(pending, "prepare-intent.json"), new { schema = 1, workspace = workspace.Root, profile.Id, profile.Folder, source, generation });
        return paths;
    }
    void CheckSandboxSlot(ServerProfile profile, string source, string path, SandboxOwner? owner) {
        if (owner is null) {
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("BLOCKED_SANDBOX_UNOWNED: " + path);
            return;
        }
        using var snapshot = InspectSandbox(profile, source, path, owner);
    }
    sealed class SandboxSnapshot : IDisposable {
        public readonly Dictionary<string, SafeFileHandle> Handles = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Files = [], Directories = [], Links = [];
        public void Pin(string path, bool directory, bool deletable = true) => Handles.Add(path, PinSandboxEntry(path, directory, deletable));
        public Stream Read(string path) => new FileStream(new SafeFileHandle(Handles[path].DangerousGetHandle(), false), FileAccess.Read);
        public string Text(string path) { using var stream = Read(path); stream.Position = 0; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
        public void VerifyFile(string path, long? bytes, string sha) {
            ValidateHash(sha);
            using var stream = Read(path); stream.Position = 0;
            if ((bytes is not null && stream.Length != bytes) || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)) != sha)
                throw new IOException("BLOCKED_SANDBOX_FILE_SHA_OR_LENGTH: " + path);
        }
        public void Delete(string path) {
            int delete = 1;
            if (!SetFileInformationByHandle(Handles[path], 4, ref delete, sizeof(int)))
                throw new IOException("BLOCKED_SANDBOX_UNLINK: " + path, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            Handles[path].Dispose();
        }
        public void Dispose() { foreach (var handle in Handles.Values) handle.Dispose(); }
    }
    static SafeFileHandle PinSandboxEntry(string path, bool directory, bool deletable = true) {
        // No delete sharing: a pinned ancestor cannot be renamed/replaced by a junction.
        uint access = directory ? 0x80u : 0x80000000u;
        if (deletable) access |= 0x10000u;
        var handle = CreateFileW(path, access, directory ? 3u : 1u,
            IntPtr.Zero, 3, directory ? 0x02200000u : 0x00200000u, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("BLOCKED_SANDBOX_PIN: " + path, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())); }
        return handle;
    }
    SandboxSnapshot InspectSandbox(ServerProfile profile, string source, string root, SandboxOwner owner, bool legacy = false) {
        var snapshot = new SandboxSnapshot();
        try {
            NoLinks(root);
            if (!Directory.Exists(root) || owner.Schema != 1 || !SamePath(owner.Workspace, workspace.Root)
                || owner.ProfileId != profile.Id || owner.Folder != profile.Folder || !SamePath(owner.ServerDirectory, source)
                || !Guid.TryParseExact(owner.GenerationId, "N", out _)) throw new IOException("BLOCKED_SANDBOX_OWNER");
            // Pin every ancestor down to the volume, including the slot root, before enumeration.
            var ancestors = new Stack<string>();
            for (string? path = root; path != null; path = Path.GetDirectoryName(path)) ancestors.Push(path);
            foreach (string path in ancestors) snapshot.Pin(path, true, SamePath(path, root));
            NoLinks(root);
            var physicalFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var physicalDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
            var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string directory) {
                foreach (string path in Directory.GetFileSystemEntries(directory)) {
                    string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                    ValidateRelative(relative);
                    var attributes = File.GetAttributes(path);
                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    snapshot.Pin(path, isDirectory);
                    if (File.GetAttributes(path) != attributes) throw new IOException("BLOCKED_SANDBOX_ENTRY_CHANGED");
                    if ((attributes & FileAttributes.ReparsePoint) != 0) {
                        // Only manager-created mount-point junctions, never symlinks or unknown tags.
                        if (!isDirectory) throw new IOException("BLOCKED_SANDBOX_REPARSE_FILE");
                        byte[] reparse = new byte[16384];
                        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A8, [], 0, reparse, reparse.Length, out int bytes, IntPtr.Zero)
                            || bytes < 8 || BitConverter.ToUInt32(reparse) != 0xA0000003u)
                            throw new IOException("BLOCKED_SANDBOX_REPARSE_TAG");
                        links.Add(relative, new DirectoryInfo(path).LinkTarget ?? throw new IOException("BLOCKED_SANDBOX_LINK_TARGET"));
                        snapshot.Links.Add(path);
                    } else if (isDirectory) {
                        physicalDirectories.Add(relative); Visit(path); snapshot.Directories.Add(path);
                    } else { physicalFiles.Add(relative, path); snapshot.Files.Add(path); }
                }
            }
            Visit(root);
            string ownerPath = Safe(root, SandboxOwnerFile);
            if (!legacy && Json.Decode<SandboxOwner>(snapshot.Text(ownerPath)) != owner) throw new IOException("BLOCKED_SANDBOX_OWNER_MISMATCH");
            snapshot.VerifyFile(Safe(root, "runtime-manifest.json"), null, owner.ManifestSha256);
            snapshot.VerifyFile(Safe(root, "runtime-policy.json"), null, owner.PolicySha256);
            var manifest = Json.Decode<RuntimeManifest>(snapshot.Text(Safe(root, "runtime-manifest.json")));
            if (manifest.GenerationId != owner.GenerationId || manifest.ProfileId != profile.Id
                || !SamePath(manifest.ServerDirectory, source) || manifest.Executable != profile.Executable
                || manifest.Files is null || manifest.Mappings is null || manifest.Plugins is null)
                throw new IOException("BLOCKED_SANDBOX_MANIFEST_IDENTITY");
            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SandboxOwnerFile, "runtime-manifest.json", "runtime-policy.json" };
            if (legacy) expectedFiles.Remove(SandboxOwnerFile);
            var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "plugins" };
            void Parents(string relative) {
                for (string? parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    expectedDirectories.Add(parent.Replace(Path.DirectorySeparatorChar, '/'));
            }
            foreach (var file in manifest.Files) {
                ValidateRelative(file.Path); string relative = file.Path.Replace(Path.DirectorySeparatorChar, '/');
                if (!expectedFiles.Add(relative) || file.Mode is not ("verified-copy" or "verified-hardlink"))
                    throw new IOException("BLOCKED_SANDBOX_MANIFEST_FILE");
                Parents(relative); snapshot.VerifyFile(Safe(root, relative), file.Bytes, file.Sha256);
            }
            var expectedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in manifest.Mappings) {
                ValidateRelative(mapping.Path); string relative = mapping.Path.Replace(Path.DirectorySeparatorChar, '/'); Parents(relative);
                bool world = workspace.SharedWorlds && (relative.Equals("Worlds", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals("world", StringComparison.OrdinalIgnoreCase) || relative.Equals("Core/Worlds", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals("Core/world", StringComparison.OrdinalIgnoreCase) || relative.Equals("lib/Saves/Worlds", StringComparison.OrdinalIgnoreCase));
                string target = world ? workspace.Worlds : Safe(source, relative);
                if (mapping.Mode != "junction-server-persistent" || !SamePath(mapping.Target, target)
                    || !expectedLinks.Add(relative) || !links.TryGetValue(relative, out string? actual) || !SamePath(actual, target))
                    throw new IOException("BLOCKED_SANDBOX_MAPPING");
                NoLinks(target); // Never enumerate the persistent target.
            }
            // UTSL native startup can create an unmanifested, empty top-level x64.
            // Server/x64 may be absent; if present it must also be real and empty.
            // Never exempt other roots,
            // nested directories, payload files, or reparse points. Applies to both
            // stopped legacy migration and named slot inspection after a native run.
            if (physicalDirectories.Contains("x64") && !expectedDirectories.Contains("x64")
                && IsEmptyNativeX64(snapshot, root, source)) expectedDirectories.Add("x64");
            if (!expectedFiles.SetEquals(physicalFiles.Keys) || !expectedDirectories.SetEquals(physicalDirectories) || !expectedLinks.SetEquals(links.Keys))
                throw new IOException("BLOCKED_SANDBOX_UNKNOWN_CONTENT");
            snapshot.Directories.Add(root);
            return snapshot;
        } catch { snapshot.Dispose(); throw; }
    }
    static bool IsEmptyNativeX64(SandboxSnapshot snapshot, string root, string source) {
        string runtimeX64 = Safe(root, "x64"), sourceX64 = Safe(source, "x64");
        if (!Directory.Exists(runtimeX64) || File.Exists(sourceX64)) return false;
        // Hold the source and its ancestors against rename/reparse replacement.
        var ancestors = new Stack<string>();
        for (string? path = Directory.Exists(sourceX64) ? sourceX64 : source; path != null; path = Path.GetDirectoryName(path)) ancestors.Push(path);
        foreach (string path in ancestors) {
            if (snapshot.Handles.ContainsKey(path)) continue;
            NoLinks(path); snapshot.Pin(path, true, false); NoLinks(path);
        }
        NoLinks(sourceX64);
        return !Directory.EnumerateFileSystemEntries(runtimeX64).Any()
            && (Directory.Exists(sourceX64) ? !Directory.EnumerateFileSystemEntries(sourceX64).Any() : !File.Exists(sourceX64));
    }
    void RemoveExpiredSandbox(ServerProfile profile, string source, string path, SandboxOwner owner) {
        using var snapshot = InspectSandbox(profile, source, path, owner);
        AssertSandboxStopped(profile, source);
        // Delete the exact pinned objects, bottom-up. No path reopen and no recursive delete;
        // junction handles use OPEN_REPARSE_POINT, so only the link itself is unlinked.
        foreach (string entry in snapshot.Links) snapshot.Delete(entry);
        foreach (string entry in snapshot.Files) snapshot.Delete(entry);
        foreach (string entry in snapshot.Directories) snapshot.Delete(entry);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, uint infoClass, ref int information, uint size);
    RuntimeLaunch PublishReadableRuntime(ServerProfile profile, string source, SandboxPaths paths, string generation) {
        string manifest = Safe(paths.Pending, "runtime-manifest.json"), policy = Safe(paths.Pending, "runtime-policy.json");
        var owner = new SandboxOwner(1, workspace.Root, profile.Id, profile.Folder, source, generation, Hashes.FileSha(manifest), Hashes.FileSha(policy));
        // Intent is diagnostic metadata for incomplete preparations, not runtime payload.
        File.Delete(Safe(paths.Pending, "prepare-intent.json"));
        WriteNewJson(Safe(paths.Pending, SandboxOwnerFile), owner);
        using (InspectSandbox(profile, source, paths.Pending, owner)) { }
        CheckSandboxSlot(profile, source, paths.Current, paths.State.Current);
        CheckSandboxSlot(profile, source, paths.Previous, paths.State.Previous);
        AssertSandboxStopped(profile, source);
        string intent = Safe(Path.GetDirectoryName(paths.Pending)!, "publish-intent.json");
        WriteNewJson(intent, new { status = "PUBLISH_IN_PROGRESS", paths.State, candidate = owner, paths.Current, paths.Previous, paths.Pending });
        if (paths.State.Current is not null && paths.State.Previous is { } previous) RemoveExpiredSandbox(profile, source, paths.Previous, previous);
        bool movedCurrent = false, movedPending = false, committed = false;
        try {
            if (paths.State.Current is not null) { Directory.Move(paths.Current, paths.Previous); movedCurrent = true; }
            Directory.Move(paths.Pending, paths.Current); movedPending = true;
            NoLinks(paths.Index); CreateDirectory(Path.GetDirectoryName(paths.Index)!);
            Json.Save(paths.Index, paths.State with { Current = owner, Previous = paths.State.Current ?? paths.State.Previous });
            committed = true;
            File.Delete(intent);
        } catch (Exception error) {
            // Once the index commits, directory rollback would contradict its owners.
            // Keep the committed layout and intent; the next Prepare fails closed.
            if (committed) { error.Data["SandboxIndexCommitted"] = true; throw; }
            try {
                if (movedPending) Directory.Move(paths.Current, paths.Pending);
                if (movedCurrent) Directory.Move(paths.Previous, paths.Current);
            } catch (Exception rollbackError) { error.Data["SandboxRollbackError"] = rollbackError.ToString(); }
            throw;
        }
        return new(paths.Current, Safe(paths.Current, profile.Executable), Safe(paths.Current, "runtime-manifest.json"), generation);
    }
}

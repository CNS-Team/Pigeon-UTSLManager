using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pigeon.Manager;

// This is deliberately separate from legacy migration: it moves only a failed
// Prepare diagnostic, never a runtime payload, manifest, or persistent mapping.
public sealed record FailedRuntimeDiagnosticArchive(string SourceDirectory, string ArchiveDirectory,
    string GenerationId, long Bytes, string Sha256);

public sealed partial class SharedRuntime {
    sealed record FailedRuntimeDiagnosticPlan(string SourceDirectory, string SourceFile, string ArchiveDirectory,
        string ArchiveFile, string GenerationId, long Bytes, string Sha256);

    // Explicit stopped-only maintenance for legacy runNNNN diagnostic-only folders.
    // Normal Prepare never calls this, so an un-migrated workspace remains legacy.
    public FailedRuntimeDiagnosticArchive[] ArchiveFailedRuntimeDiagnostics(ServerProfile profile) {
        using var preparation = EnterSandboxPreparation(profile);
        ValidateId(profile.Id); ValidateLeaf(profile.Folder); ValidateLeaf(profile.Executable);
        string source = workspace.ServerDirectory(profile);
        using var ownerLease = LockSandboxSource(source);
        lock (Gate) {
            AssertNoSandboxTransaction(profile);
            AssertSandboxStopped(profile, source);
            if (NamedSandboxesEnabled(profile)) throw new IOException("BLOCKED_FAILED_RUNTIME_REQUIRES_LEGACY_LAYOUT");

            string root = workspace.RuntimeRoot(profile);
            NoLinks(root);
            if (File.Exists(root)) throw new IOException("BLOCKED_FAILED_RUNTIME_ROOT_FILE: " + root);
            if (!Directory.Exists(root)) return [];
            using var snapshot = new SandboxSnapshot();
            PinFailedRuntimeAncestors(snapshot, root);

            // Preflight every archiveable diagnostic before moving any file. A bad
            // candidate therefore leaves all candidate diagnostics in place.
            var plans = new List<FailedRuntimeDiagnosticPlan>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(root)) {
                NoLinks(entry);
                if (!Directory.Exists(entry)) continue;
                snapshot.Pin(entry, true); NoLinks(entry);
                string diagnostic = Safe(entry, "prepare-failed.json");
                if (!File.Exists(diagnostic)) continue;
                if (!LegacyFailedRunName(Path.GetFileName(entry)))
                    throw new IOException("BLOCKED_FAILED_RUNTIME_RUN_NAME: " + entry);
                snapshot.Pin(diagnostic, false); NoLinks(diagnostic);
                plans.Add(InspectFailedRuntimeDiagnostic(profile, source, entry, diagnostic, snapshot));
            }
            foreach (var plan in plans)
                if (File.Exists(plan.ArchiveDirectory) || Directory.Exists(plan.ArchiveDirectory))
                    throw new IOException("BLOCKED_FAILED_RUNTIME_ARCHIVE_EXISTS: " + plan.ArchiveDirectory);

            var archived = new List<FailedRuntimeDiagnosticArchive>();
            foreach (var plan in plans) {
                // File/ancestor handles deny write/delete sharing throughout inspection
                // and rename: paths cannot be swapped after hashing.
                var fresh = InspectFailedRuntimeDiagnostic(profile, source, plan.SourceDirectory, plan.SourceFile, snapshot);
                if (fresh != plan) throw new IOException("BLOCKED_FAILED_RUNTIME_CHANGED: " + plan.SourceDirectory);
                PinFailedRuntimeAncestors(snapshot, Path.GetDirectoryName(plan.ArchiveDirectory)!, create: true);
                AssertSandboxStopped(profile, source);
                // Exclusive reservation; a collision or interrupted empty archive blocks
                // retry at the same name instead of creating accumulating suffixes.
                if (!CreateFailedRuntimeArchiveDirectory(plan.ArchiveDirectory, IntPtr.Zero))
                    throw new IOException("BLOCKED_FAILED_RUNTIME_ARCHIVE_RESERVATION: " + plan.ArchiveDirectory,
                        new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                PinFailedRuntimeAncestors(snapshot, plan.ArchiveDirectory);
                RenameFailedRuntimeDiagnostic(snapshot.Handles[plan.SourceFile], plan.ArchiveFile);
                // Same original handle now refers to the archived original file. No
                // delete/copy fallback: any failure leaves diagnostic at source or archive.
                snapshot.VerifyFile(plan.SourceFile, plan.Bytes, plan.Sha256);
                NoLinks(plan.ArchiveFile);
                if (!File.Exists(plan.ArchiveFile)) throw new IOException("BLOCKED_FAILED_RUNTIME_ARCHIVE_NAME: " + plan.ArchiveFile);
                NoLinks(plan.SourceDirectory);
                if (Directory.EnumerateFileSystemEntries(plan.SourceDirectory).Any())
                    throw new IOException("BLOCKED_FAILED_RUNTIME_SOURCE_CHANGED_AFTER_MOVE: " + plan.SourceDirectory);
                snapshot.Delete(plan.SourceDirectory); // non-recursive; kernel refuses a nonempty directory
                archived.Add(new(plan.SourceDirectory, plan.ArchiveDirectory, plan.GenerationId, plan.Bytes, plan.Sha256));
            }
            return archived.ToArray();
        }
    }

    static void PinFailedRuntimeAncestors(SandboxSnapshot snapshot, string directory, bool create = false) {
        var ancestors = new Stack<string>();
        for (string? path = directory; path != null; path = Path.GetDirectoryName(path)) ancestors.Push(path);
        foreach (string path in ancestors) {
            if (snapshot.Handles.ContainsKey(path)) continue;
            NoLinks(path);
            if (create && !Directory.Exists(path)) CreateDirectory(path);
            snapshot.Pin(path, true, false); NoLinks(path);
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateFailedRuntimeArchiveDirectory(string path, IntPtr securityAttributes);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RenameFailedRuntimeHandle(SafeFileHandle handle, uint infoClass, IntPtr information, uint size);
    static void RenameFailedRuntimeDiagnostic(SafeFileHandle handle, string destination) {
        byte[] name = Encoding.Unicode.GetBytes(destination);
        int lengthOffset = IntPtr.Size * 2, nameOffset = lengthOffset + sizeof(uint);
        byte[] buffer = new byte[nameOffset + name.Length + sizeof(char)]; // NUL-terminated; length excludes NUL
        BitConverter.GetBytes(name.Length).CopyTo(buffer, lengthOffset); name.CopyTo(buffer, nameOffset);
        IntPtr data = Marshal.AllocHGlobal(buffer.Length);
        try {
            Marshal.Copy(buffer, 0, data, buffer.Length);
            if (!RenameFailedRuntimeHandle(handle, 3, data, checked((uint)buffer.Length)))
                throw new IOException("BLOCKED_FAILED_RUNTIME_ARCHIVE_RENAME: " + destination,
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        } finally { Marshal.FreeHGlobal(data); }
    }
    FailedRuntimeDiagnosticPlan InspectFailedRuntimeDiagnostic(ServerProfile profile, string source, string directory, string diagnostic, SandboxSnapshot snapshot) {
        NoLinks(directory); NoLinks(diagnostic);
        string[] entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        if (entries.Length != 1 || !SamePath(entries[0], diagnostic))
            throw new IOException("BLOCKED_FAILED_RUNTIME_NOT_DIAGNOSTIC_ONLY: " + directory);
        foreach (string entry in entries) NoLinks(entry);

        using var stream = snapshot.Read(diagnostic); stream.Position = 0;
        using var content = new MemoryStream(); stream.CopyTo(content); byte[] bytes = content.ToArray();
        string generation;
        try {
            using var document = JsonDocument.Parse(bytes);
            generation = ValidateFailedRuntimeDiagnostic(document.RootElement, profile, source);
        } catch (JsonException error) { throw new IOException("BLOCKED_FAILED_RUNTIME_DIAGNOSTIC_JSON: " + diagnostic, error); }

        string name = Path.GetFileName(directory);
        string archiveDirectory = Safe(workspace.Data, "runtime-failures/" + profile.Folder + "/" + name);
        string archiveFile = Safe(archiveDirectory, "prepare-failed.json");
        return new(directory, diagnostic, archiveDirectory, archiveFile, generation, bytes.LongLength,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    static bool LegacyFailedRunName(string name) {
        string digits = name.StartsWith("run-", StringComparison.Ordinal) ? name[4..]
            : name.StartsWith("run", StringComparison.Ordinal) ? name[3..] : "";
        return digits.Length != 0 && digits.All(char.IsAsciiDigit) && digits.Any(c => c != '0');
    }

    static string ValidateFailedRuntimeDiagnostic(JsonElement root, ServerProfile profile, string source) {
        if (root.ValueKind != JsonValueKind.Object) throw new IOException("BLOCKED_FAILED_RUNTIME_DIAGNOSTIC_SHAPE");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject()) if (!fields.Add(property.Name))
            throw new IOException("BLOCKED_FAILED_RUNTIME_DIAGNOSTIC_DUPLICATE: " + property.Name);
        string[] expected = ["generation", "Id", "source", "error", "status", "preserved"];
        if (fields.Count != expected.Length || expected.Any(field => !fields.Contains(field)))
            throw new IOException("BLOCKED_FAILED_RUNTIME_DIAGNOSTIC_FIELDS");
        string? Value(string name) {
            JsonElement value = root.GetProperty(name);
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        string? generation = Value("generation");
        if (generation is null
            || generation.Length != 32 || !Guid.TryParseExact(generation, "N", out _))
            throw new IOException("BLOCKED_FAILED_RUNTIME_GENERATION");
        if (Value("Id") is not { } id || id != profile.Id)
            throw new IOException("BLOCKED_FAILED_RUNTIME_PROFILE");
        if (Value("source") is not { } recordedSource || !Path.IsPathFullyQualified(recordedSource) || !SamePath(recordedSource, source))
            throw new IOException("BLOCKED_FAILED_RUNTIME_SOURCE");
        if (Value("error") is not { } error || string.IsNullOrWhiteSpace(error))
            throw new IOException("BLOCKED_FAILED_RUNTIME_ERROR");
        if (Value("status") != "BLOCKED")
            throw new IOException("BLOCKED_FAILED_RUNTIME_STATUS");
        if (root.GetProperty("preserved").ValueKind != JsonValueKind.True)
            throw new IOException("BLOCKED_FAILED_RUNTIME_PRESERVED");
        return generation;
    }
}

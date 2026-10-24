namespace Pigeon.Manager;

public sealed partial class SharedRuntime {
    sealed record LibraryDocument(int SchemaVersion, PluginArtifact[] Current, PluginArtifact[]? ExpectedAliases = null);
    sealed record LibraryImportInput(string Path, PluginArtifact Artifact);
    sealed record LibraryImportDocument(int SchemaVersion, LibraryImportInput[] Inputs, PluginArtifact[] Promote, bool Ready);
    sealed class LibraryLease : IDisposable {
        [ThreadStatic] static Dictionary<string, (FileStream Stream, int Depth)>? held;
        readonly string key;
        public LibraryLease(string root) {
            key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
            held ??= new(StringComparer.OrdinalIgnoreCase);
            if (held.TryGetValue(key, out var existing)) { held[key] = (existing.Stream, existing.Depth + 1); return; }
            string directory = Workspace.Under(root, "Data/Library");
            Directory.CreateDirectory(directory);
            string path = Workspace.Under(directory, "plugin-library.lock");
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (true) {
                Workspace.EnsureNoLinks(path);
                try { held[key] = (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), 1); return; }
                catch (IOException) when (deadline.Elapsed < TimeSpan.FromSeconds(60)) { Thread.Sleep(25); }
            }
        }
        public void Dispose() {
            var lease = held![key];
            if (lease.Depth > 1) held[key] = (lease.Stream, lease.Depth - 1);
            else { held.Remove(key); lease.Stream.Dispose(); }
        }
    }
    IDisposable LockPluginLibrary() => new LibraryLease(workspace.Root);
    string LibraryIndex => Safe(CreateDirectory(Safe(workspace.Data, "Library")), "plugin-library.json");
    string LibraryPending => Safe(CreateDirectory(Safe(workspace.Data, "Library")), "plugin-library.pending.json");
    string LibraryImportPending => Safe(CreateDirectory(Safe(workspace.Data, "Library")), "plugin-library.import.json");
    string LibraryRejected => Safe(CreateDirectory(Safe(workspace.Data, "Library")), "plugin-library.rejected.json");
    string[] RejectedLibraryKeys() => File.Exists(LibraryRejected)
        ? Json.Decode<string[]>(File.ReadAllText(LibraryRejected)) : [];
    public bool ManagedLibraryEnabled => File.Exists(LibraryIndex) || File.Exists(LibraryPending);
    string ArchiveDirectory => CreateDirectory(Safe(workspace.Library, "Archive"));

    // Catalog() deliberately retains every exact revision for old selections and
    // rollback. The normal UI and physical Plugins root use Current only.
    public PluginLibraryState LibraryState() {
        lock (Gate) {
            using var lease = LockPluginLibrary();
            var all = Catalog();
            var current = File.Exists(LibraryIndex) ? ReadLibraryDocument(LibraryIndex).Current : InitialCurrent(all);
            foreach (var a in current) ResolvePlugin(a);
            return State(all, current);
        }
    }
    public PluginLibraryState OrganizeLibrary() {
        lock (Gate) {
            using var lease = LockPluginLibrary();
            ResumeLibraryImport();
            return OrganizeLibraryCore([]);
        }
    }
    // Explicit maintenance exception for components owned by another maintainer.
    // Capture the canonical aliases under the same library lock; never switch
    // them even transiently while organizing other history revisions.
    public PluginLibraryState OrganizeLibraryPreservingAliases(string[] fileNames) {
        ArgumentNullException.ThrowIfNull(fileNames);
        lock (Gate) {
            using var lease = LockPluginLibrary();
            if (File.Exists(LibraryPending) || File.Exists(LibraryImportPending))
                throw new IOException("BLOCKED_LIBRARY_PRESERVE_PENDING_TRANSACTION");
            var preserved = fileNames.Select(name => {
                ValidateLeaf(name);
                if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("BLOCKED_LIBRARY_PRESERVE_FILENAME");
                var artifact = Describe(Safe(workspace.Library, name));
                if (!name.Equals(CanonicalPluginFileName(artifact), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("BLOCKED_LIBRARY_PRESERVE_NOT_CANONICAL");
                return artifact;
            }).ToArray();
            ValidateSelection(preserved);
            return OrganizeLibraryCore([], preserveAliases: preserved);
        }
    }
    public PluginLibraryState SetLibraryCurrent(PluginArtifact artifact) {
        lock (Gate) {
            using var lease = LockPluginLibrary();
            ResumeLibraryImport();
            ValidateSelection([artifact]);
            ResolvePlugin(artifact);
            OrganizeLibraryCore([]);
            return OrganizeLibraryCore([], artifact);
        }
    }
    public PluginLibraryState CancelLibraryImport() {
        lock (Gate) {
            using var lease = LockPluginLibrary();
            if (File.Exists(LibraryImportPending)) {
                NoLinks(LibraryImportPending);
                var plan = Json.Decode<LibraryImportDocument>(File.ReadAllText(LibraryImportPending));
                if (plan.SchemaVersion != 1 || plan.Ready || File.Exists(LibraryPending))
                    throw new IOException("BLOCKED_LIBRARY_IMPORT_COMMIT_STARTED");
                // Before Ready no current aliases were touched. Preserve the exact
                // failed input plan for diagnosis and release only this uncommitted intent.
                string rejected = Safe(CreateDirectory(Safe(workspace.Data, "Library/RejectedImports")),
                    "import-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
                Json.Save(LibraryRejected, RejectedLibraryKeys().Concat(plan.Promote.Select(PluginKey)).Distinct().ToArray());
                File.Move(LibraryImportPending, rejected, false);
            }
            return LibraryState();
        }
    }
    void ResumeLibraryImport() {
        if (!File.Exists(LibraryImportPending)) return;
        NoLinks(LibraryImportPending);
        var plan = Json.Decode<LibraryImportDocument>(File.ReadAllText(LibraryImportPending));
        if (plan.SchemaVersion != 1 || plan.Inputs is null || plan.Promote is null
            || plan.Promote.Any(a => !plan.Inputs.Any(i => i.Artifact == a)))
            throw new IOException("BLOCKED_LIBRARY_IMPORT_SCHEMA");
        if (!plan.Ready) {
            // Retain the intent before publishing: a failure during support import
            // must not make a retry mistake the new revision for old history.
            foreach (var input in plan.Inputs) {
                if (!Path.IsPathFullyQualified(input.Path) || Describe(input.Path) != input.Artifact)
                    throw new IOException("BLOCKED_LIBRARY_IMPORT_SOURCE_CHANGED");
            }
            foreach (var input in plan.Inputs) {
                Publish(input.Path, input.Artifact);
                ImportModuleSupport(input.Path, input.Artifact);
            }
            plan = plan with { Ready = true };
            Json.Save(LibraryImportPending, plan);
        }
        foreach (var input in plan.Inputs) ResolvePlugin(input.Artifact);
        var accepted = plan.Inputs.Select(i => PluginKey(i.Artifact)).ToHashSet(StringComparer.Ordinal);
        Json.Save(LibraryRejected, RejectedLibraryKeys().Where(k => !accepted.Contains(k)).ToArray());
        OrganizeLibraryCore(plan.Promote);
        File.Delete(LibraryImportPending);
    }
    LibraryDocument ReadLibraryDocument(string path) {
        NoLinks(path);
        var document = Json.Decode<LibraryDocument>(File.ReadAllText(path));
        if (document.SchemaVersion != 1) throw new IOException("BLOCKED_LIBRARY_SCHEMA");
        ValidateSelection(document.Current);
        return document;
    }
    void ValidateManagedAliases() {
        if (!File.Exists(LibraryIndex)) return;
        var allowed = ReadLibraryDocument(LibraryIndex).Current;
        foreach (var path in Directory.EnumerateFiles(workspace.Library, "*.dll"))
            if (!allowed.Contains(Describe(path)))
                throw new IOException("BLOCKED_LIBRARY_UNMANAGED_ALIAS: " + path);
    }
    PluginLibraryState State(PluginArtifact[] all, PluginArtifact[] current) {
        var keys = current.Select(PluginKey).ToHashSet(StringComparer.Ordinal);
        return new(current.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            all.Where(a => !keys.Contains(PluginKey(a))).ToArray(), workspace.Library, ArchiveDirectory);
    }
    static int CompareVersion(PluginArtifact a, PluginArtifact b) =>
        Version.TryParse(a.Version, out var av) && Version.TryParse(b.Version, out var bv)
            ? av.CompareTo(bv) : StringComparer.OrdinalIgnoreCase.Compare(a.Version, b.Version);
    static string CanonicalPluginFileName(PluginArtifact artifact) {
        string stem = Path.GetFileNameWithoutExtension(artifact.FileName);
        string prefix = artifact.Name + "-variant-";
        return stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(stem[prefix.Length..], out int variant) && variant > 0
                ? artifact.Name + ".dll" : artifact.FileName;
    }

    PluginArtifact[] InitialCurrent(PluginArtifact[] all) {
        var selected = workspace.Load().SelectMany(p => ReadSelection(p).Plugins).ToArray();
        // On migration honor a unanimous, exact saved selection. AssemblyVersion
        // is often unchanged between fixes; revision order or an old timestamp
        // must never silently revert a successfully deployed common component.
        var order = PluginStore.Entries().Select((e, i) => (e.Key, i)).ToDictionary(x => x.Key, x => x.i);
        return all.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(group => {
            var active = selected.Where(a => a.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(PluginKey).ToArray();
            if (active.Length == 1 && group.Any(a => a == active[0])) return active[0];
            return group.Aggregate((a, b) => CompareVersion(a, b) > 0 ? a
                : CompareVersion(a, b) < 0 ? b
                : order.GetValueOrDefault(PluginKey(a), -1) > order.GetValueOrDefault(PluginKey(b), -1) ? a : b);
        }).ToArray();
    }
    PluginLibraryState OrganizeLibraryCore(PluginArtifact[] imported, PluginArtifact? promote = null, PluginArtifact[]? preserveAliases = null) {
        // Recover the committed intent before processing another import. No old
        // alias is removed until its exact bytes have been verified in the archive.
        if (File.Exists(LibraryPending)) {
            var interrupted = ReadLibraryDocument(LibraryPending);
            Reconcile(interrupted.Current, interrupted.ExpectedAliases
                ?? throw new IOException("BLOCKED_LIBRARY_PENDING_ALIAS_SNAPSHOT"));
        }
        ValidateManagedAliases();
        var all = Catalog();
        // Adopt unmanaged flat DLLs into the immutable store before touching aliases.
        foreach (var a in all) {
            if (PluginStore.Find(PluginKey(a)) is not { } stored || !File.Exists(stored)) {
                string path = Safe(workspace.Library, a.FileName);
                if (Describe(path) != a) throw new IOException("BLOCKED_LIBRARY_INPUT_CHANGED");
                Publish(path, a);
            }
        }
        all = Catalog();
        var current = File.Exists(LibraryIndex) ? ReadLibraryDocument(LibraryIndex).Current : InitialCurrent(all);
        if (File.Exists(LibraryIndex)) {
            foreach (var old in current) {
                string alias = Safe(workspace.Library, old.FileName);
                if (File.Exists(alias) && Describe(alias) != old)
                    throw new IOException("BLOCKED_LIBRARY_CURRENT_ALIAS_TAMPERED: " + old.FileName);
            }
        }
        var chosen = current.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var group in all.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            if (!chosen.ContainsKey(group.Key)) chosen[group.Key] = InitialCurrent(group.ToArray()).Single();
        foreach (var a in imported) {
            if (!chosen.TryGetValue(a.Name, out var old) || CompareVersion(a, old) >= 0) chosen[a.Name] = a;
        }
        if (promote is not null) chosen[promote.Name] = promote;
        foreach (var preserved in preserveAliases ?? []) chosen[preserved.Name] = preserved;
        var desired = chosen.Values.Select(a => {
            string filename = CanonicalPluginFileName(a);
            if (filename == a.FileName) return a;
            var canonical = a with { FileName = filename };
            Publish(ResolvePlugin(a), canonical);
            return canonical;
        }).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        ValidateSelection(desired); // Includes filename collisions across different assemblies.
        foreach (var a in desired) ResolvePlugin(a);
        Reconcile(desired);
        return State(all, desired);
    }
    void Reconcile(PluginArtifact[] desired, PluginArtifact[]? recoveryAliases = null) {
        ValidateSelection(desired);
        if (recoveryAliases is not null) {
            // Only our old aliases or already-committed desired bytes are valid
            // during recovery. Do not silently adopt and overwrite a foreign DLL.
            foreach (var path in Directory.EnumerateFiles(workspace.Library, "*.dll")) {
                var observed = Describe(path);
                if (!recoveryAliases.Contains(observed) && !desired.Contains(observed))
                    throw new IOException("BLOCKED_LIBRARY_PENDING_ALIAS_CHANGED: " + path);
            }
        }
        var all = Catalog();
        var currentKeys = desired.Select(PluginKey).ToHashSet(StringComparer.Ordinal);
        var immutable = all.ToDictionary(PluginKey, a => ResolvePlugin(a), StringComparer.Ordinal);
        foreach (var a in desired)
            if (!immutable.ContainsKey(PluginKey(a))) throw new IOException("BLOCKED_LIBRARY_CURRENT_MISSING");
        var aliases = Directory.EnumerateFiles(workspace.Library, "*.dll").Select(path => (Path: path, Artifact: Describe(path))).ToArray();
        foreach (var alias in aliases)
            if (!all.Any(a => a.Name == alias.Artifact.Name && a.Sha256 == alias.Artifact.Sha256))
                throw new IOException("BLOCKED_LIBRARY_UNKNOWN_ALIAS: " + alias.Path);
        var archive = new ReadableLibrary(ArchiveDirectory);
        foreach (var a in all.Where(a => !currentKeys.Contains(PluginKey(a)))) {
            string destination = archive.Reserve(PluginKey(a), a.Name + "-" + a.Version, a.FileName);
            if (!File.Exists(destination)) AtomicLibraryCopy(immutable[PluginKey(a)], destination, a, false);
            if (Describe(destination) != a) throw new IOException("BLOCKED_ARCHIVE_METADATA_OR_SHA");
        }
        foreach (var a in desired) {
            // Verify every history revision first, then persist intent. A crash
            // during alias replacement is completed from this same plan next time.
            Verify(immutable[PluginKey(a)], a.Bytes, a.Sha256);
        }
        var doc = new LibraryDocument(1, desired, recoveryAliases ?? aliases.Select(a => a.Artifact).ToArray());
        Json.Save(LibraryPending, doc);
        foreach (var a in desired) {
            string destination = Safe(workspace.Library, a.FileName);
            var expected = aliases.SingleOrDefault(p => p.Path.Equals(destination, StringComparison.OrdinalIgnoreCase));
            if (File.Exists(destination) && (expected.Path is null || Describe(destination) != expected.Artifact))
                throw new IOException("BLOCKED_LIBRARY_ALIAS_CHANGED: " + destination);
            if (!File.Exists(destination) || Hashes.FileSha(destination) != a.Sha256)
                AtomicLibraryCopy(immutable[PluginKey(a)], destination, a, true);
            if (Describe(destination) != a) throw new IOException("BLOCKED_LIBRARY_CURRENT_SHA");
        }
        var names = desired.Select(a => a.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases.Where(a => !names.Contains(Path.GetFileName(a.Path)))) {
            // A foreign writer changing an alias is a conflict, never a reason
            // to remove their new bytes. Every removal has a verified pinned copy.
            NoLinks(alias.Path);
            if (Describe(alias.Path) != alias.Artifact) throw new IOException("BLOCKED_LIBRARY_ALIAS_CHANGED");
            File.Delete(alias.Path);
        }
        var actual = Directory.EnumerateFiles(workspace.Library, "*.dll").Select(Describe).ToArray();
        if (actual.Length != desired.Length || desired.Any(a => !actual.Contains(a)))
            throw new IOException("BLOCKED_LIBRARY_RECONCILIATION_INCOMPLETE");
        Json.Save(LibraryIndex, doc);
        File.Delete(LibraryPending);
    }
    static void AtomicLibraryCopy(string source, string destination, PluginArtifact artifact, bool replace) {
        NoLinks(destination);
        string temporary = Safe(Path.GetDirectoryName(destination)!, ".library-" + Guid.NewGuid().ToString("N"));
        try {
            CopyVerified(source, temporary, artifact.Bytes, artifact.Sha256);
            // Rename replaces the directory entry, not bytes behind an existing
            // hardlink. Already-running generations retain their original bytes.
            File.Move(temporary, destination, replace);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

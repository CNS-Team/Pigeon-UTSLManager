namespace Pigeon.Manager;

// Hashes identify bytes, not directory names. Keep the operator-facing layout
// readable while retaining exact revision pinning in the catalog.
internal sealed class ReadableLibrary(string root) {
    internal sealed record Entry(string Key, string FileName, string Folder);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    object Gate => Gates.GetOrAdd(Path.GetFullPath(root), _ => new());
    string Index => Workspace.Under(root, "catalog.json");
    public Entry[] Entries() {
        Workspace.EnsureNoLinks(root);
        if (!File.Exists(Index)) return [];
        var entries = Json.Decode<Entry[]>(File.ReadAllText(Index));
        if (entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != entries.Length
            || entries.Select(e => e.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new IOException("BLOCKED_LIBRARY_DUPLICATE_INDEX");
        foreach (var e in entries) { Workspace.Under(root, e.Folder); Workspace.Under(root, e.Folder + "/" + e.FileName); }
        return entries;
    }
    public string? Find(string key) {
        lock (Gate) {
            var entry = Entries().SingleOrDefault(e => e.Key == key);
            return entry is null ? null : Workspace.Under(root, entry.Folder + "/" + entry.FileName);
        }
    }
    public string Reserve(string key, string label, string filename) {
        lock (Gate) {
            var entries = Entries();
            var existing = entries.SingleOrDefault(e => e.Key == key);
            if (existing != null) {
                if (existing.FileName != filename) throw new IOException("BLOCKED_LIBRARY_FILENAME_BINDING");
                return Workspace.Under(root, existing.Folder + "/" + existing.FileName);
            }
            string safe = string.Concat(label.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')).TrimEnd('.', ' ');
            if (safe.Length > 70) safe = safe[..70];
            if (safe.Length == 0) safe = "Item";
            int revision = 1; string folder;
            do { folder = safe + "-r" + revision++.ToString("D4"); }
            while (Directory.Exists(Workspace.Under(root, folder)) || entries.Any(e => e.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)));
            string directory = Workspace.Under(root, folder); Directory.CreateDirectory(directory);
            string path = Workspace.Under(directory, filename);
            Json.Save(Index, entries.Append(new Entry(key, filename, folder)).ToArray());
            return path;
        }
    }
}

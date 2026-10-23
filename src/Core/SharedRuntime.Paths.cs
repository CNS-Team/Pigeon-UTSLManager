using System.Text.Json;

namespace Pigeon.Manager;

public sealed partial class SharedRuntime {
    sealed record ConfigPathCheck(string File, string Field, string State, string Basis);
    static ConfigPathCheck[] ValidatePersistentConfigPaths(string serverRoot) {
        var checks = new List<ConfigPathCheck>();
        string configRoot = Safe(serverRoot, "config");
        if (!Directory.Exists(configRoot)) return [];
        void CheckPath(string file, string key, string location, string value) {
            string field = key.ToLowerInvariant();
            bool pathField = field.Contains("path") || field.Contains("directory") || field.Contains("folder")
                || field is "worldfile" or "worldfilename" or "databasefile" or "savefile";
            bool worldFile = value.EndsWith(".wld", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".twld", StringComparison.OrdinalIgnoreCase);
            if ((!pathField && !worldFile) || string.IsNullOrWhiteSpace(value)) return;
            // Module-relative database/log paths (e.g. TShock's tshock.sqlite)
            // have their own base. Do not invent cwd semantics for arbitrary keys.
            bool knownCwdWorldPath = file.Replace('\\', '/').Equals("config/config.json", StringComparison.OrdinalIgnoreCase)
                && (field is "worldfile" or "worldpath" or "worldfilename")
                && (location.StartsWith("$.launcher.", StringComparison.OrdinalIgnoreCase) || location == "$." + key);
            if (!knownCwdWorldPath) {
                checks.Add(new(file, location, "UNVERIFIED", "module-specific path base; configuration preserved"));
                return;
            }
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile && value.Contains("://", StringComparison.Ordinal)) return;
            string normalized = value.Replace('/', Path.DirectorySeparatorChar);
            if (normalized.Contains('%') || normalized.Contains('$'))
                throw new IOException("BLOCKED_OPEN_CONFIG_PATH_EXPANSION: " + file + " # " + key + " = " + value);
            string resolved = Path.GetFullPath(normalized, serverRoot);
            string prefix = Path.TrimEndingDirectorySeparator(serverRoot) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("BLOCKED_CONFIG_PATH_OUTSIDE_SERVER: " + file + " # " + key + " = " + value + "；代际目录改变相对路径语义，未改写配置");
            string relative = Path.GetRelativePath(serverRoot, resolved);
            string first = relative.Split('/', '\\')[0];
            if (!PersistentDirectories.Contains(first))
                throw new IOException("BLOCKED_OPEN_CONFIG_PERSISTENT_PATH: " + file + " # " + key + " = " + value + "；路径未落入已知持久目录");
            Safe(serverRoot, relative);
            checks.Add(new(file, location, "VERIFIED_PERSISTENT", "launcher cwd/server root"));
        }
        void Walk(JsonElement value, string file, string key, string location) {
            if (value.ValueKind == JsonValueKind.Object) {
                foreach (var property in value.EnumerateObject()) Walk(property.Value, file, property.Name, location + "." + property.Name);
            } else if (value.ValueKind == JsonValueKind.Array) {
                int index = 0;
                foreach (var item in value.EnumerateArray()) Walk(item, file, key, location + "[" + index++ + "]");
            } else if (value.ValueKind == JsonValueKind.String) CheckPath(file, key, location, value.GetString()!);
        }
        void Visit(string directory) {
            NoLinks(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                NoLinks(entry);
                if (Directory.Exists(entry)) Visit(entry);
                else if (entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
                    using var document = JsonDocument.Parse(File.ReadAllText(entry), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    Walk(document.RootElement, Path.GetRelativePath(serverRoot, entry), "", "$");
                }
            }
        }
        Visit(configRoot);
        return checks.ToArray();
    }
}

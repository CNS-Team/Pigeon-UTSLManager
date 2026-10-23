using Pigeon.Manager;

namespace RuntimeChecks;
static class ActualInputs {
    static readonly string[] CoreCode = ["bin", "app", "i18n", "runtimes", "x64"];
    public static Dictionary<string, string> CopyCode(string from, string to) {
        Workspace.EnsureNoLinks(from);
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in Directory.GetFileSystemEntries(from)) {
            Workspace.EnsureNoLinks(entry); string name = Path.GetFileName(entry);
            if (Directory.Exists(entry)) {
                if (EngineLibrary.IsCodeDirectory(name)) CopyTree(entry, Path.Combine(to, name), inputs);
                else if (name.Equals("Core", StringComparison.OrdinalIgnoreCase))
                    foreach (string child in Directory.GetDirectories(entry).Where(directory => CoreCode.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)))
                        CopyTree(child, Path.Combine(to, name, Path.GetFileName(child)), inputs);
            } else if (EngineLibrary.IsCodeFile(name)) {
                inputs.Add(entry, Hashes.FileSha(entry)); File.Copy(entry, Path.Combine(to, name));
            }
        }
        return inputs;
    }
    public static void CopyTree(string from, string to, Dictionary<string, string> inputs) {
        Workspace.EnsureNoLinks(from); Directory.CreateDirectory(to);
        foreach (string entry in Directory.GetFileSystemEntries(from)) {
            Workspace.EnsureNoLinks(entry); string target = Path.Combine(to, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, target, inputs);
            else { inputs.Add(entry, Hashes.FileSha(entry)); File.Copy(entry, target); }
        }
    }
}

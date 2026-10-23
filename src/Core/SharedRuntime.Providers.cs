using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pigeon.Manager;

public sealed partial class SharedRuntime {
    // Production packages retrieved read-only on 2026-10-23 use these exact code
    // subdirectories. Core as a whole is not code: its other known data stays per server.
    static readonly HashSet<string> CoreCodeDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "app", "i18n", "runtimes", "x64" };
    static bool HasCoreCode(string core) => Directory.EnumerateFileSystemEntries(core).Any(p => CoreCodeDirectories.Contains(Path.GetFileName(p)));
    static bool IsRuntimeCodePath(string relative) {
        if (EngineLibrary.IsEngineDataPath(relative)) return false;
        string[] parts = relative.Split('/', '\\');
        return parts.Length == 1 ? EngineLibrary.IsCodeFile(parts[0]) : EngineLibrary.IsCodeDirectory(parts[0])
            || (parts.Length > 2 && parts[0].Equals("Core", StringComparison.OrdinalIgnoreCase) && CoreCodeDirectories.Contains(parts[1]));
    }
    void PlanCoreDirectory(string serverCore, string engineRoot, bool selectedEngine, List<CodeFile> code, List<(string Name, string Target)> persistent) {
        NoLinks(serverCore);
        foreach (string entry in Directory.EnumerateFileSystemEntries(serverCore)) {
            NoLinks(entry); string name = Path.GetFileName(entry);
            if (Directory.Exists(entry)) {
                if (CoreCodeDirectories.Contains(name)) {
                    string relative = "Core/" + name;
                    if (code.Any(f => f.Path.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase))) continue;
                    // File manifests do not index empty directories. Prove emptiness;
                    // enumeration/access failures must propagate, never imply empty.
                    if (!Directory.EnumerateFileSystemEntries(entry).Any()) continue;
                    if (selectedEngine) throw new IOException("BLOCKED_ENGINE_MISSING_LEGACY_CORE_CODE: " + relative + "；所选核心清单未包含此代码目录，不混入原服代码");
                    // Legacy Core code is copied and hashed; it never remains behind
                    // a whole-Core junction and cannot silently supplement a selected engine.
                    AddCodeTree(entry, relative, code);
                } else if (PersistentDirectories.Contains(name)) {
                    ValidatePersistentTree(entry); persistent.Add(("Core/" + name, WorldTarget(name, entry)));
                } else throw new IOException("BLOCKED_OPEN_CORE_DIRECTORY_POLICY: " + name);
            } else if (EngineLibrary.IsCodeFile(name)) {
                string relative = "Core/" + name;
                throw new IOException("BLOCKED_OPEN_CORE_ROOT_CODE_POLICY: " + relative);
            } else if (!IgnoredRoot(name)) throw new IOException("BLOCKED_OPEN_CORE_MUTABLE_FILE_POLICY: " + name);
        }
        foreach (string name in new[] { "config", "Data", "Worlds", "world", "logs" }) {
            string relative = "Core/" + name;
            if (persistent.Any(p => p.Name.Equals(relative, StringComparison.OrdinalIgnoreCase))) continue;
            string target = CreateDirectory(WorldTarget(name, Safe(serverCore, name)));
            persistent.Add((relative, target));
        }
    }
    static string PayloadFingerprint(IEnumerable<(string Path, long Bytes, string Sha256)> entries) {
        var content = new StringBuilder();
        foreach (var file in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
            content.Append(file.Path.Replace('\\', '/')).Append('\0').Append(file.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\0').Append(file.Sha256).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
    }
    sealed record VerifiedProvider(string Name, string OwnerSha256, string SupportFingerprint);
    static readonly VerifiedProvider[] VerifiedProviders = [
        new("TShockAPI", "5A443304C68C97F2ABAD251DA1688EF5EB62D1F36B515582DEC19E21A5F31794", "BF65DB0922BAF0EF1141181525F93D5ED48889D3E3CA3961BE4C105E73376284"),
        new("CustomPlayer", "1553B4B3BFD332A79148B53EEDCBB8B8E8EC6301AE51F921B8E4D3666BBF67F3", "16A5E82F7179D8D4AE4AB54121EABF3E58C8156BF19089CB4933D0802B445A79")
    ];
    // Filled only with complete initial code-tree fingerprints from independent
    // old-loader processes. A new build or dependency set needs a fresh validation.
    static readonly HashSet<string> VerifiedProviderCode = new(StringComparer.Ordinal) {
        "8671696C08B0567DDD60F1798D7E0D0305F8820DA1D56394449E0DC6E01CA606",
        "A0D5C4D653E701A06FBE6A73D4412D0D52277E17750E69B7D990D9435B592F14"
    };
    static void ValidateDynamicProviders(PreparedPlugin[] plugins, SelectionDocument selection, List<CodeFile> code) {
        var dynamicPlugins = plugins.Where(p => p.Metadata.DynamicDependencies).ToArray();
        if (dynamicPlugins.Length == 0) return;
        if (dynamicPlugins.Length != VerifiedProviders.Length || VerifiedProviders.Any(v => !dynamicPlugins.Any(p => p.Artifact.Name == v.Name && p.Artifact.Sha256 == v.OwnerSha256)))
            throw new IOException("BLOCKED_OPEN_DYNAMIC_MODULE_DEPENDENCIES: 仅验证过 TShockAPI + CustomPlayer 的精确 provider 组合");
        string codeFingerprint = PayloadFingerprint(code.Select(f => (f.Path, f.Bytes, f.Sha256)));
        foreach (var plugin in dynamicPlugins) {
            var verified = VerifiedProviders.SingleOrDefault(p => p.Name == plugin.Artifact.Name && p.OwnerSha256 == plugin.Artifact.Sha256);
            var support = (selection.Support ?? []).Where(f => f.Module == plugin.Metadata.Directory && f.OwnerSha256 == plugin.Artifact.Sha256).ToArray();
            string fingerprint = PayloadFingerprint(support.Select(f => (f.Path, f.Bytes, f.Sha256)));
            if (verified is null || !VerifiedProviderCode.Contains(codeFingerprint) || fingerprint != verified.SupportFingerprint)
                throw new IOException("BLOCKED_OPEN_DYNAMIC_MODULE_DEPENDENCIES: " + plugin.Artifact.Name + "; code=" + codeFingerprint + "; owner=" + plugin.Artifact.Sha256 + "; support=" + fingerprint + "；仅放行独立旧 loader 实核过的完整组合");
        }
    }
}

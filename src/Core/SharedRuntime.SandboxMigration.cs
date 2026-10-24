using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pigeon.Manager;

public sealed record LegacySandboxEntry(string Directory, int Run, string GenerationId, string ManifestSha256, string PolicySha256);
public sealed record LegacySandboxPlan(string ProfileId, string ServerDirectory, string Current, string Previous,
    LegacySandboxEntry[] Runs, string PlanSha256, string? RetainGeneration = null);

public sealed partial class SharedRuntime {
    // Explicit, local, stopped-only conversion. Never invoked by Prepare or normal startup.
    public LegacySandboxPlan PlanLegacyRuntimeMigration(ServerProfile profile, string? preferredGeneration = null) {
        using var preparation = EnterSandboxPreparation(profile);
        string source = workspace.ServerDirectory(profile);
        using var ownerLease = LockSandboxSource(source);
        lock (Gate) { using var libraryLease = LockPluginLibrary(); return InspectLegacyRuntime(profile, source, preferredGeneration); }
    }
    LegacySandboxPlan InspectLegacyRuntime(ServerProfile profile, string source, string? preferredGeneration) {
        ValidateLeaf(profile.Folder); ValidateLeaf(profile.Executable);
        if (preferredGeneration is not null && !Guid.TryParseExact(preferredGeneration, "N", out _))
            throw new IOException("BLOCKED_LEGACY_MIGRATION_RETAIN_GENERATION");
        AssertSandboxStopped(profile, source);
        if (profile.Folder.Equals(".manager-slots", StringComparison.OrdinalIgnoreCase)) throw new IOException("BLOCKED_SANDBOX_RESERVED_FOLDER");
        string current = workspace.RuntimeRoot(profile);
        string slots = Safe(workspace.Root, "Core/Runtime/.manager-slots/" + profile.Id);
        string previous = Safe(slots, "previous");
        string index = Safe(workspace.Data, "runtime-slots/" + profile.Id + ".json");
        if (File.Exists(index) || Directory.Exists(slots) || File.Exists(slots)) throw new IOException("BLOCKED_LEGACY_MIGRATION_SLOTS_OCCUPIED");
        if (!Directory.Exists(current)) throw new IOException("BLOCKED_LEGACY_MIGRATION_MISSING_ROOT");
        var runs = new List<LegacySandboxEntry>();
        foreach (string path in Directory.GetFileSystemEntries(current)) {
            NoLinks(path);
            string name = Path.GetFileName(path);
            string number = name.StartsWith("run-", StringComparison.Ordinal) ? name[4..]
                : name.StartsWith("run", StringComparison.Ordinal) ? name[3..] : "";
            if (!Directory.Exists(path) || number.Length == 0 || !number.All(char.IsAsciiDigit)
                || !int.TryParse(number, out int run) || run < 1 || runs.Any(r => r.Run == run))
                throw new IOException("BLOCKED_LEGACY_MIGRATION_UNKNOWN_ENTRY: " + path);
            string manifestPath = Safe(path, "runtime-manifest.json"), policyPath = Safe(path, "runtime-policy.json");
            var manifest = Json.Decode<RuntimeManifest>(File.ReadAllText(manifestPath));
            string manifestSha = Hashes.FileSha(manifestPath), policySha = Hashes.FileSha(policyPath);
            var owner = new SandboxOwner(1, workspace.Root, profile.Id, profile.Folder, source, manifest.GenerationId, manifestSha, policySha);
            // Byte identity, complete inventory and exact persistent link targets first.
            using (InspectSandbox(profile, source, path, owner, legacy: true)) { }
            if (!SamePath(manifest.EngineDirectory, source) && !InsidePath(manifest.EngineDirectory, Path.Combine(workspace.Root, "Core/Engines"))
                && !InsidePath(manifest.EngineDirectory, Path.Combine(workspace.Root, "Core/.store")))
                throw new IOException("BLOCKED_LEGACY_MIGRATION_ENGINE_OWNER");
            foreach (var file in manifest.Files) {
                bool plugin = file.Path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase);
                if (!(plugin ? InsidePath(file.Source, source) || InsidePath(file.Source, workspace.Library)
                    || InsidePath(file.Source, Path.Combine(workspace.Data, "Library"))
                    : IsRuntimeCodePath(file.Path) && SamePath(file.Source, Safe(manifest.EngineDirectory, file.Path))))
                    throw new IOException("BLOCKED_LEGACY_MIGRATION_FILE_OWNER");
            }
            using var policy = JsonDocument.Parse(File.ReadAllText(policyPath));
            if (policy.RootElement.GetProperty("securityIsolation").GetBoolean()
                || policy.RootElement.GetProperty("unknownRootMutable").GetString() != "BLOCKED")
                throw new IOException("BLOCKED_LEGACY_MIGRATION_POLICY");
            runs.Add(new(path, run, manifest.GenerationId, manifestSha, policySha));
        }
        if (runs.Count == 0 || runs.Select(r => r.GenerationId).Distinct(StringComparer.Ordinal).Count() != runs.Count)
            throw new IOException("BLOCKED_LEGACY_MIGRATION_GENERATIONS");
        var ordered = runs.OrderBy(r => r.Run).ToArray();
        string retained = preferredGeneration ?? ordered[^1].GenerationId;
        if (!ordered.Any(run => run.GenerationId == retained))
            throw new IOException("BLOCKED_LEGACY_MIGRATION_RETAIN_GENERATION_NOT_FOUND");
        var plan = new LegacySandboxPlan(profile.Id, source, current, previous, ordered, "", retained);
        return plan with { PlanSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Encode(plan)))) };
    }
    public LegacySandboxPlan MigrateNamedSandboxes(ServerProfile profile, string expectedPlanSha256, string? preferredGeneration = null) {
        using var preparation = EnterSandboxPreparation(profile);
        ValidateHash(expectedPlanSha256);
        string source = workspace.ServerDirectory(profile);
        using var ownerLease = LockSandboxSource(source);
        lock (Gate) {
            using var libraryLease = LockPluginLibrary();
            var plan = InspectLegacyRuntime(profile, source, preferredGeneration);
            if (plan.PlanSha256 != expectedPlanSha256) throw new IOException("BLOCKED_LEGACY_MIGRATION_PLAN_CHANGED");
            // Durable plan exists before destructive work; an interrupted migration never auto-retries.
            string index = Safe(workspace.Data, "runtime-slots/" + profile.Id + ".json");
            string slots = Path.GetDirectoryName(plan.Previous)!; CreateDirectory(slots);
            string evidence = Safe(slots, "legacy-migration.pending.json"); WriteNewJson(evidence, new { status = "IN_PROGRESS", plan });
            try {
                var latest = plan.Runs.Single(run => run.GenerationId == plan.RetainGeneration);
                var kept = new SandboxOwner(1, workspace.Root, profile.Id, profile.Folder, source, latest.GenerationId, latest.ManifestSha256, latest.PolicySha256);
                WriteNewJson(Safe(latest.Directory, SandboxOwnerFile), kept);
                Directory.Move(latest.Directory, plan.Previous);
                foreach (var run in plan.Runs.Where(run => run.GenerationId != plan.RetainGeneration)) {
                    var owner = new SandboxOwner(1, workspace.Root, profile.Id, profile.Folder, source, run.GenerationId, run.ManifestSha256, run.PolicySha256);
                    using var snapshot = InspectSandbox(profile, source, run.Directory, owner, legacy: true);
                    AssertSandboxStopped(profile, source);
                    foreach (string path in snapshot.Links) snapshot.Delete(path);
                    foreach (string path in snapshot.Files) snapshot.Delete(path);
                    foreach (string path in snapshot.Directories) snapshot.Delete(path);
                }
                NoLinks(plan.Current); Directory.Delete(plan.Current, false);
                CreateDirectory(Path.GetDirectoryName(index)!);
                WriteNewJson(index, new SandboxIndex(1, workspace.Root, profile.Id, profile.Folder, source, null, kept));
                WriteNewJson(Safe(slots, "legacy-migration.json"), new { status = "MIGRATED_STOPPED_NOT_STARTED", plan });
                File.Delete(evidence);
                return plan;
            } catch (Exception error) {
                Json.Save(evidence, new { status = "BLOCKED_PRESERVED_NO_AUTO_RETRY", plan, error = error.ToString() }); throw;
            }
        }
    }
}

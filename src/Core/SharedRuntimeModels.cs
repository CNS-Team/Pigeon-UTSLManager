namespace Pigeon.Manager;

public sealed record PluginArtifact(string FileName, string Name, string Version, long Bytes, string Sha256);

public sealed record RuntimeSelection(bool Enabled, PluginArtifact[] Plugins, string? EngineRevision = null);

// Directory/Executable always identify the launch location. Disabled selections
// return the original server paths and empty ManifestPath/GenerationId strings.
public sealed record RuntimeLaunch(string Directory, string Executable, string ManifestPath, string GenerationId);

public sealed record RuntimeProcessMarker(string ServerDirectory, ProcessIdentity Identity, RuntimeLaunch Launch);

public sealed record RuntimeFile(string Path, string Source, long Bytes, string Sha256, string Mode);
public sealed record RuntimeMapping(string Path, string Target, string Mode);
public sealed record RuntimeManifest(string GenerationId, string ProfileId, string ServerDirectory, string EngineDirectory,
    string? EngineRevision, string Executable, PluginArtifact[] Plugins, RuntimeFile[] Files, RuntimeMapping[] Mappings);

# Independent shared runtime fixtures

This project owns only this directory. It does not modify the engine, desktop,
or existing Tests/StartupTests/CoreTests. All traffic is local loopback HTTP
and current-user named pipes; no SSH or live server is used.

Run from the repository with PowerShell 7:

    ./src/RuntimeTests/Run.ps1 -RunName r-next

For the final frozen Release run, supply the two retrieved local source
directories with `-ProviderSources @('<source-A>','<source-B>') -Scope all`.
Provider scenarios copy only code and complete TShockAPI/CustomPlayer module
directories into fresh fixture sources. They keep generated fixture config and
never start either retrieved source executable. A dedicated loader process
executes both real dependency providers, checks private support copies and
content-store/other-generation hashes, and rejects unverified owner/support
combinations. Input module hashes and resulting manifests are recorded.
ActualCore may point at a full retrieved server directory: marker scenarios
copy code only, including the five recognized Core code subtrees and empty
directories, and retain generated fixture config. No real config is copied or
executed. Engine tests cover Core code roundtrip, selected revisions and
exclusion of Core/config, Core/Data and Core/Worlds even for DLL-like names.

Wait for the engine worker's freeze before running. The script verifies source
hashes before/after build and execution, refuses a mixed source build, and
requires Host, Core and runner Core DLL SHA256 values to match. Release binary
bindings are written to binary-binding.json. Scope defaults to all and does
not inherit a previous RUNTIME_TEST_SCOPE setting.

The default artifact root is the workspace sibling directory
_tmp_build/utsl-manager-shared-runtime-20261023. Override ArtifactRoot or
ActualCore when the offline core package is elsewhere. Existing run directories
and evidence are never overwritten. NuGet sources are cleared; all builds,
intermediates, generated NuGet lock files, logs and fixture directories use the
run directory. A build failure writes failure.json with its stage. Executed
tests write evidence/result.json; RESULT-LOCATION.json points to the result.

The fixture consists of two independent managed processes, two builds of one
plugin assembly with different versions/hashes, actual Windows file ID queries,
and separate CoreModule/RequiresCoreModule marker assemblies. The marker
assemblies reference only the already-built offline UnifierTSL.dll. They do not
compile or modify UnifierTSL.

Coverage:

- Content-addressed import/catalog, same file names with different hashes,
  exact per-profile selections, empty selection and disabled original mode.
- Actual DLL execution in distinct AssemblyLoadContexts and processes;
  manifest link/copy modes checked against volume/file IDs.
- Fresh generation IDs, immutable old code and manifests after library import.
- Shared core revisions and their multi-file code/dependency manifests;
  core selection overrides the source server's different core.
- Config, Data and world writes persist in each server's own source directories
  and survive a new generation. Unknown root mutable files fail closed.
- Path traversal, invalid IDs, ADS/device/ambiguous Windows paths, bogus hashes,
  reparse points and corrupted or incomplete code/dependency manifests.
- Real Host APIs, preview without process creation, exact executable/CWD,
  command/stop isolation, restart persistence, preparation failure before process
  creation, external sandbox recognition and cross-workspace source ownership.
- Actual ModuleAssemblyLoader from the offline core in a dedicated process:
  root marker execution, CoreModule placement, RequiresCoreModule placement,
  shared ALC/static state and duplicate-load prevention; a real module-local
  lib dependency executes in that module's ALC. Pinned dependencies.json/lib
  support must use private copies with independent file IDs; modifying one
  generation must preserve the content store and other generation.

Cross-process fixture events use a named mutex derived from the event-file
path and handles shared for concurrent reads. This is fixture synchronization;
it does not add retries or change the Host's command protocol or timeouts.

For isolated diagnosis, set RUNTIME_TEST_SCOPE to host, runtime, boundary or
module before launching Manager.RuntimeTests.exe directly. The full script
defaults to all suites. The runner accepts:

    <new-evidence-directory> <Host.exe> <fixture-package> <plugin-v1.dll> <plugin-v2.dll>
    [<actual-core-directory> <RuntimeModuleTests.exe> <core-module.dll> <child-module.dll> [provider-source...]]

RuntimeCoreAssembly can be supplied as an MSBuild property to compile the test
runner against an already-built manager Core DLL instead of building Core.
This is useful while another worker is modifying the product implementation.
Record the exact Core/Host hashes for such a run.

A positive fixture result is component evidence. It does not assert complete
UTSL startup, third-party plugin dependency compatibility, real player login,
world loading, transfer or client rendering. Those game E2E checks are not run.

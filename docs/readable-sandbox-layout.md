# TSM-style readable UTSL layout

```
UTSl-Manager.exe
Plugins/                       # DLLs only; readable -variant-01 aliases if needed
Server/
  World/                       # ALL servers' world files and world backups
  0.PigeonGame/config/          # per-server persistent configuration/data/logs
  1.PigeonLive/config/
Core/
  Engines/UnifierTSL-0.PigeonGame-r0001/
  Runtime/0.PigeonGame/run-0001/ # runnable private generation, real module layout
Data/Library/                  # catalogs and readable dependency revision folders
Backups/Readable-layout-.../    # verified pre-migration ZIP + manifest + result
```

Hashes remain inside catalogs/selections/manifests, never as new directory names.
The DLL projection in Plugins and sandbox immutable DLLs use verified NTFS hard
links where available, verified copies otherwise. Module support remains private
per-generation copies because the UTSL loader can rewrite dependencies.json.
Variants are still pinned per server; Game and Live are not merged or upgraded.

Worlds/world/Core/Worlds in a running sandbox map directly to Server/World.
Existing launcher world paths, world names, default world and configs retain their
original bytes. Different servers may NOT use the same world filename: migration
fails closed before copying rather than overwrite another server's world. A
newly imported unmigrated world tree also fails closed instead of being ignored.
The map generation UI uses the same Server/World output base.
The FNA alias lib/Saves/Worlds also maps to that same store. lib/Saves is
explicitly mutable data, never an engine-code artifact; stale copied worlds
there must be classified and archived rather than allowed into shared code.

Migration is an explicit C# operation: Manager.RuntimeTools ROOT world-plan then
migrate-layout. It requires Host's exclusive lock, all managed servers stopped
and AutoStart=false. It hashes every backup ZIP entry before pruning only backed
source code/plugin copies; configuration, player databases and worlds survive.
No game process is started by migration. Failure preserves stage diagnostics and
backup; an operator must stop and inspect, never blindly repeat the migration.

After migration the existing C# Host startup queue launches Game 0 before Live 1
and waits for all worlds of each server. Existing owner locks, PID/path/creation
time checks, unknown-root policy, reparse guards and actual provider fingerprints
remain enforced. This is process/data separation, NOT a hostile-code security
sandbox. Neither TSM nor its two RPG services are controlled by this workspace.
There is no PowerShell/cmd/bat dependency in any product launch/runtime path.

Rollback (stopped-only): close Manager, request Host shutdown, verify no game
processes/queue, inspect migration-result.json and backup-manifest.json, verify
before.zip SHA, restore its exact Server entries to the original Servers paths
and Data/servers.json + runtime-selections; remove the shared-world marker only
after old world paths and exact old selections are restored. Never restore over
worlds/config/SQLite changed since migration: preserve new data separately first.

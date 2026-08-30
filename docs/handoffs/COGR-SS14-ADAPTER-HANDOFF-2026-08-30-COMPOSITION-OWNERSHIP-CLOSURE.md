# COGR SS14 Adapter Handoff — Composition Ownership Closure

**Date:** August 30, 2026  
**Adapter repository:** `stoffeldaniel96/COGR-SS14-Adapter`  
**Adapter pre-handoff head:** `228c4a6d9cb86d75e4b256e3623659b76fafbc55`  
**Initial core source extraction:** `02ae73ebce15a6bebf24001b58fdeda290b999e1`  
**Legacy Station reference:** `stoffeldaniel96/COGR-Station@947b7462235f95bc3f9d48d834e6485af1557a91`  
**Recorded upstream SS14 base:** `3aa6d7bf211fe7eaf7f3b9c96c455290db7fd29c`

## State

The initial server/shared adapter extraction is committed and public, but a direct legacy-Station-vs-upstream comparison showed that the reusable adapter ownership boundary was not yet closed.

The adapter manifest now declares the additional additive COGR-owned Station surfaces:

- `Content.Client/COGR/**`;
- `Content.Shared/CCVar/CCVars.COGR.cs`;
- `Content.Tests/COGR/**`;
- `Content.Tests/Server/COGR/**`;
- `Resources/Locale/en-US/cogr/**`;
- `Resources/Locale/en-US/commands/show-cogr-spatial-command.ftl`.

The importer is now manifest-driven, and synchronization supports both tree and single-file mappings.

## Adapter installation wiring

`adapter-manifest.json` now owns the reusable install requirements for:

- `Grpc.Net.Client` `2.67.0`;
- `Google.Protobuf` `3.29.3`;
- `Microsoft.Extensions.Logging.Abstractions` `10.0.6`;
- `COGR.Core.dll`;
- `COGR.Contracts.dll`;
- `COGR.Transport.Grpc.dll`;
- `COGR.SS14Bridge.dll`.

`scripts/install-wiring.ps1` installs/verifies those declarations against a materialized Station checkout. It fails on conflicting existing package versions or assembly-reference paths instead of silently overwriting changed upstream structure.

`scripts/install-to-station.ps1` composes source synchronization + install wiring + verification.

The PowerShell scripts remain Windows PowerShell 5.1 compatible; do not reintroduce `System.IO.Path.GetRelativePath` or nested-script `exit` behavior.

## COGR-Station target architecture

The future COGR-Station should be a thin composition/acceptance repository, not a long-lived hand-edited fork.

Target inputs are exact immutable revisions of:

1. upstream Space Station 14;
2. COGR-SS14-Adapter;
3. locally accepted COGR runtime/bridge source or package artifacts;
4. COGR-Station-owned additive testbed overlay; and
5. explicit minimal upstream compatibility patches.

A lockfile-driven materializer should create a disposable Station checkout under an ignored workspace/cache, initialize upstream recursive submodules, install the adapter, materialize runtime artifacts, apply testbed overlay and compatibility patches, then build/test/live-gate.

Primary Git submodules were rejected as the composition mechanism because adapter installation would intentionally dirty the SS14 submodule and SS14 already nests RobustToolbox. Local Git caches/worktrees remain acceptable implementation details.

Migration design/audit is recorded on `COGR-Station` branch `chore/composition-workspace` in:

- `docs/COGR-STATION-COMPOSITION-WORKSPACE.md`;
- `docs/COGR-STATION-COMPOSITION-OWNERSHIP-AUDIT.md`;
- `composition.migration-baseline.json`.

## Legacy fork ownership audit

### Keep as explicit compatibility patch unless/until upstreamed

- `Content.Shared/Speech/ListenEvent.cs`;
- `Content.Shared/Speech/EntitySystems/ListeningSystem.cs`.

These preserve actor-valid delivered-speech metadata and host obfuscation fidelity. They are host delivery semantics, not COGR cognition. Prefer a generalized upstream SS14 contribution eventually.

### Remove instead of carrying forward

- `Content.Server/Spawners/EntitySystems/SpawnPointSystem.cs` COGR overlap diagnostic instrumentation;
- `Resources/ConfigPresets/Build/development.toml` non-semantic encoding/BOM drift.

### Convert to additive testbed prototype if still needed

The three COGR tags currently inserted into upstream `Resources/Prototypes/tags.yml`:

- `COGRAgentAnchor`;
- `COGRObstacle`;
- `COGRTestMovement`.

They should live in a COGR-owned testbed prototype rather than modify the upstream shared tag registry.

## Immediate owner/local step

Pull current adapter `main`, then rerun the manifest-driven extraction against the existing local COGR-Station checkout:

```powershell
git pull --ff-only

.\scripts\import-from-station.ps1 `
    -StationRepoPath "C:\path\to\COGR-Station"

.\scripts\verify-public-readiness.ps1
git status --short
```

Expected result: the already-extracted server/shared trees may be rewritten identically, while the newly declared client/CVar/tests/localization paths and updated `extraction-provenance.json` appear as changes/additions. The known `dev-token-f05` configuration warning remains non-fatal and must not be silently changed during ownership migration.

Review `git status --short` before committing.

## After second extraction commit

1. Treat the resulting expanded adapter commit as the candidate reusable source authority.
2. Use the adapter installer against a clean Station checkout at the recorded SS14 base.
3. Create the explicit speech compatibility patch from the legacy fork delta.
4. Move required COGR acceptance maps/prototypes/config into a COGR-Station integration overlay and remove shared upstream tag edits.
5. Select the exact **current locally accepted COGR runtime commit** for the composition lock; do not use legacy `lib/COGR-VERSION.json` commit `b3d7bf...` as current authority.
6. Implement the COGR-Station materializer/update scripts.
7. Materialize from scratch and request a Station dependency/build/local acceptance gate.
8. Only after GREEN should the full development fork be retired as the default product shape.

## Public repository recommendation

Do not make the complete historical COGR-Station development fork the long-term public product by default. Once the thin composition workspace is GREEN, the cleanest publication boundary is to preserve the old full fork privately as legacy/migration evidence and publish the thin composition repository under the COGR-Station product name. This avoids exposing stale generated artifacts, developer-machine provenance, and the entire upstream asset/history surface merely to distribute the COGR integration workspace.

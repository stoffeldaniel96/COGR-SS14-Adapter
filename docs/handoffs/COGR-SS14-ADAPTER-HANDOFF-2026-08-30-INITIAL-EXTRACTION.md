# COGR SS14 Adapter Handoff — Initial Standalone Extraction

**Date:** August 30, 2026  
**Repository:** `stoffeldaniel96/COGR-SS14-Adapter`  
**Branch:** `main`  
**Current adapter tooling head:** `2dd96ce6f2c7d82df8eacce1016c7a4a12ab6747`

## Completed

The standalone adapter repository exists publicly and has been initialized with:

- MIT licensing;
- `AGENTS.md` cross-agent steering;
- `docs/ARCHITECTURE.md` adapter/runtime/Station ownership boundaries;
- `docs/PROVENANCE.md` extraction provenance;
- `CONTRIBUTING.md` contribution/licensing policy;
- `adapter-manifest.json` declaring only the two initial adapter-owned Station roots;
- deterministic `scripts/import-from-station.ps1`;
- bounded `scripts/verify-public-readiness.ps1`; and
- deterministic `scripts/sync-to-station.ps1` with exact drift verification.

COGR licensing was separately merged to `stoffeldaniel96/COGR` `master` at:

```text
758d81cb04f83ab2a4e3b4f80a04679d453d2673
```

COGR is now Apache-2.0 on its default branch. The standalone SS14 adapter is MIT. `COGR-Station` is not relicensed as a COGR product and remains the full upstream-derived SS14 integration/testbed checkout.

## Initial source authority

The first adapter extraction is intentionally pinned to the latest accepted reusable adapter state on COGR-Station `main`:

```text
947b7462235f95bc3f9d48d834e6485af1557a91
```

Branch ancestry was checked before choosing this commit:

- `fix/perception-log-verbosity` is already behind `main`;
- `agent/perception-item-classification-diagnostics-20260822` diverges from an older baseline and is not current authority; and
- `test/cogr-cognitive-acceptance-station` is four commits ahead of `main`, but those changes are acceptance map/config/documentation only, not reusable adapter source.

The extracted source roots are exactly:

```text
Content.Server/COGR/
Content.Shared/COGR/
```

They become:

```text
overlay/Content.Server/COGR/
overlay/Content.Shared/COGR/
```

Do not expand the extraction by repository search or a whole-Station recursive tree migration. Maps, prototypes, `lib`, Station-wide configuration, assets, and arbitrary scripts remain excluded unless explicitly classified later.

## Local extraction step

From the local `COGR-SS14-Adapter` checkout:

```powershell
git pull

.\scripts\import-from-station.ps1 `
    -StationRepoPath "C:\path\to\COGR-Station"

.\scripts\verify-public-readiness.ps1

git status --short
```

The import script creates a detached worktree at the pinned Station commit, copies only the declared source roots, removes the worktree, and writes deterministic `extraction-provenance.json` using the source commit timestamp rather than wall-clock extraction time.

The public-readiness script fails on actual credential/private-path signatures in the extracted adapter source. The known literal `dev-token-f05` is reported as configuration debt rather than treated as a leaked secret because changing the currently accepted handshake during packaging could alter live behavior. Move launch-token configuration to proper Station configuration in a separately gated adapter change.

If the import/readiness gate is clean, commit and push the extracted overlay:

```powershell
git add overlay extraction-provenance.json
git commit -m "Extract SS14 adapter source from COGR-Station"
git push
```

## Next repository work

After the extracted source is pushed:

1. review the public adapter snapshot directly in this repository;
2. add/adjust adapter-focused public CI without hiding Station coupling behind mocks;
3. make `COGR-SS14-Adapter` the canonical source and synchronize it back into a current COGR-Station checkout with:

   ```powershell
   .\scripts\sync-to-station.ps1 -StationRepoPath "C:\path\to\COGR-Station"
   ```

4. inspect the generated `COGR-ADAPTER-VERSION.json` and Station diff;
5. run the normal Station adapter build/local gate; and
6. only after that gate is GREEN declare the extraction/synchronization boundary accepted.

For the Station dependency/integration validation, request/report:

```text
local-gate <branch/commit>
```

Do not claim packaging extraction GREEN from source-copy success alone.

## Known Station public-readiness debt

Before making the full `COGR-Station` fork public, clean or consciously accept at least:

- `lib/COGR-VERSION.json` currently records absolute machine-local build source paths;
- `scripts/update-cogr-dependencies.ps1` currently generates those absolute `sourcePath` values;
- the adapter currently has a hard-coded development launch-token configuration pattern (`dev-token-f05`), which is not a private credential but should not be treated as production authentication configuration; and
- SS14 assets retain individual upstream licenses, including non-commercial assets where applicable.

The full Station fork retains upstream MIT code licensing and asset provenance; it does not need a new COGR-wide license.

## Invariants

- COGR owns cognition; Station owns environment truth and native mechanics.
- The adapter translates bounded evidence/actions and must not become a cognitive shortcut layer.
- Raw Station identity/coordinates/path truth do not become privileged durable Coggent truth.
- The standalone adapter is canonical after synchronization acceptance; COGR-Station becomes the integration mirror/testbed.
- Do not reintroduce reusable adapter changes only in the Station mirror after the boundary is accepted.

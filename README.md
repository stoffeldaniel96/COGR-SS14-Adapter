# COGR SS14 Adapter

COGR SS14 Adapter is the Space Station 14 / Robust Toolbox environment adapter for [COGR](https://github.com/stoffeldaniel96/COGR), the environment-neutral cognitive runtime.

This repository owns the reusable Station-specific integration boundary. It does **not** own COGR cognition and it is not a fork of the complete Space Station 14 content repository.

## Status

**Repository:** public  
**License:** MIT  
**Initial extraction source:** `stoffeldaniel96/COGR-Station`  
**Initial extraction source commit:** `947b7462235f95bc3f9d48d834e6485af1557a91`  
**Initial core extraction commit:** `02ae73ebce15a6bebf24001b58fdeda290b999e1`

The core server/shared adapter source has been extracted. A direct comparison against the recorded upstream SS14 base identified several additional COGR-owned additive surfaces (client diagnostics, CVar definitions, tests, and localization) which are now declared in `adapter-manifest.json` and must be included before the extraction boundary is called closed.

Until that ownership closure and the synchronized Station gate are complete, the legacy `COGR-Station` full fork remains the accepted live reference checkout. The long-term COGR-Station target is a thin composition/acceptance repository that materializes exact pinned upstream SS14 + adapter + COGR runtime revisions rather than maintaining a hand-edited copy of the full Station tree.

## Responsibility

The adapter translates between Station-authoritative world mechanics and COGR's bounded environment contracts.

```text
COGR cognition
    ↕ environment-neutral contracts
COGR SS14 Adapter
    ↕ Station / Robust APIs
Space Station 14 authoritative world
```

COGR proposes; Station disposes.

The adapter may own:

- Coggent body registration and authority binding;
- COGR connection/transport hosting inside Station;
- actor-relative bounded perception projection;
- opaque environment-reference lifecycle;
- Station-native action execution and result translation;
- passive cue and semantic-replica delivery;
- event-driven regional routing used by the adapter;
- embodiment-support projection;
- adapter diagnostics, configuration, commands, tests, and localization; and
- reusable adapter installation/synchronization tooling.

The adapter must **not** own Coggent cognition, memory, goals, Concerns, procedure policy, target selection, host-independent navigation cognition, or other runtime mental state.

## Repository shape

Canonical adapter-owned Station source is stored under `overlay/` using destination-compatible paths. Current declared mappings include COGR-owned server/shared/client code, COGR CVar definitions, adapter tests, and adapter localization.

`adapter-manifest.json` is authoritative for the declared reusable overlay boundary. The import and synchronization tools consume that manifest rather than carrying a second hard-coded ownership list.

Station project/package dependency wiring is intentionally **not** hidden inside the source overlay. The future COGR-Station composition/install layer must apply that wiring explicitly and fail closed when upstream project structure changes.

Acceptance maps, Station-wide launch configuration, bundled COGR runtime binaries, general SS14 assets, and testbed-only prototypes are not adapter source by default.

## Upstream patches

A reusable adapter should minimize edits to upstream-owned SS14 files. The legacy integration currently contains speech-delivery changes outside COGR-owned paths. Those changes must be either:

1. accepted upstream by SS14;
2. eliminated through a supported native extension point; or
3. carried as an explicit minimal compatibility patch by the COGR-Station composition layer.

Unrelated diagnostic/testbed fork edits should not become permanent adapter requirements merely because they existed in the legacy fork.

## Related repositories

- [`stoffeldaniel96/COGR`](https://github.com/stoffeldaniel96/COGR) — environment-neutral cognitive runtime; Apache-2.0.
- [`stoffeldaniel96/COGR-Station`](https://github.com/stoffeldaniel96/COGR-Station) — current integration/acceptance repository; migrating from a complete development fork toward a thin lockfile-driven Station composition workspace.

## Development rules

Read [`AGENTS.md`](AGENTS.md), [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), and [`docs/PROVENANCE.md`](docs/PROVENANCE.md) before material changes.

Adapter behavior should remain ordinary Station integration. Do not solve runtime cognition failures with adapter-authored knowledge, hidden world truth, path cursors, target-selection policy, or other cognitive shortcuts.

Changes that affect the materialized Station integration require a synchronized COGR-Station build/local gate before being declared integration-green.

## License

COGR SS14 Adapter is licensed under the [MIT License](LICENSE).

Third-party and substantially derived material retains its own notices and provenance. This repository's MIT license does not relicense COGR runtime binaries or SS14 assets.

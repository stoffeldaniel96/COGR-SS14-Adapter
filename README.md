# COGR SS14 Adapter

COGR SS14 Adapter is the Space Station 14 / Robust Toolbox environment adapter for [COGR](https://github.com/stoffeldaniel96/COGR), the environment-neutral cognitive runtime.

This repository owns the reusable Station-specific integration boundary. It does **not** own COGR cognition and it is not a fork of the complete Space Station 14 content repository.

## Status

**Repository:** public  
**License:** MIT  
**Initial extraction source:** `stoffeldaniel96/COGR-Station`  
**Initial extraction source commit:** `947b7462235f95bc3f9d48d834e6485af1557a91`

The adapter is currently being extracted from the full `COGR-Station` integration/testbed fork. Until the extraction and synchronization gate is complete, `COGR-Station` remains the authoritative live build checkout while this repository becomes the canonical home for reusable adapter source.

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
- adapter diagnostics, configuration, and commands; and
- reusable adapter installation/synchronization tooling.

The adapter must **not** own Coggent cognition, memory, goals, Concerns, procedure policy, target selection, host-independent navigation cognition, or other runtime mental state.

## Repository shape

The intended source overlay is:

```text
overlay/
  Content.Server/COGR/
  Content.Shared/COGR/
docs/
scripts/
adapter-manifest.json
```

The overlay paths deliberately match their destination inside a Station checkout. A deterministic synchronization/install tool will copy only declared adapter-owned paths into `COGR-Station` or another compatible SS14 fork and record the adapter source revision.

Acceptance maps, Station-wide configuration, bundled COGR runtime binaries, and SS14 assets are not adapter source by default.

## Related repositories

- [`stoffeldaniel96/COGR`](https://github.com/stoffeldaniel96/COGR) — environment-neutral cognitive runtime; Apache-2.0.
- [`stoffeldaniel96/COGR-Station`](https://github.com/stoffeldaniel96/COGR-Station) — complete SS14 integration/testbed fork retaining upstream Station licensing and asset provenance.

## Development rules

Read [`AGENTS.md`](AGENTS.md), [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), and [`docs/PROVENANCE.md`](docs/PROVENANCE.md) before material changes.

Adapter behavior should remain ordinary Station integration. Do not solve runtime cognition failures with adapter-authored knowledge, hidden world truth, path cursors, target-selection policy, or other cognitive shortcuts.

Changes that affect the mirrored Station integration require a synchronized `COGR-Station` build/local gate before being declared integration-green.

## License

COGR SS14 Adapter is licensed under the [MIT License](LICENSE).

Third-party and substantially derived material retains its own notices and provenance. This repository's MIT license does not relicense COGR runtime binaries or SS14 assets.

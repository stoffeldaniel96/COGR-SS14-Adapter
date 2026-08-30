# COGR SS14 Adapter — Cross-Agent Steering

This repository owns the reusable Space Station 14 / Robust Toolbox adapter for COGR.

Read first:

1. `docs/ARCHITECTURE.md`
2. `docs/PROVENANCE.md`
3. `adapter-manifest.json`
4. the current COGR `docs/ARCHITECTURE.md`, `docs/STATUS.md`, and relevant handoff when a change crosses the runtime boundary.

## Hard ownership boundary

> COGR owns cognition. Station owns environment truth and native mechanics. This adapter translates between them.

Do not move cognition into the adapter. Do not expose privileged Station truth as Coggent belief merely because the host knows it.

The adapter may translate bounded actor-relative evidence, authority, native action attempts/results, body support, and lifecycle events. It must not own goals, Concerns, Working Memory, durable Coggent memory, cognitive route choice, social policy, task policy, or scenario-specific behavior.

## Environment authority

Station remains authoritative for bodies, world state, visibility, navigation mechanics, collision, access, inventory, interactions, UI mechanics, timing, and physical outcomes.

Raw `EntityUid`, prototype/component identity, exact hidden coordinates, route paths, inaccessible contents, private UI state, antagonist information, or equivalent privileged host truth must not bypass the adapter boundary into generic cognition.

Opaque references are transient scoped action/evidence handles, not durable cognitive identity.

## Source ownership

`overlay/Content.Server/COGR/` and `overlay/Content.Shared/COGR/` are the canonical reusable adapter source locations after extraction.

`COGR-Station` is the complete integration/testbed checkout. Reusable adapter changes should be authored here first and synchronized into Station. Testbed-only maps/configuration may remain in `COGR-Station`.

Do not copy arbitrary Station files into this repository. Any source outside the declared manifest requires an explicit ownership/provenance decision first.

## Licensing and provenance

This repository is MIT-licensed. Preserve upstream copyright/license notices on copied or substantially derived code. SS14 assets and Apache-2.0 COGR runtime binaries are not relicensed by this repository.

Do not add third-party material without clear redistribution rights and provenance.

## Validation

Adapter changes are not integration-green merely because the source compiles in isolation.

For changes affecting Station integration:

1. synchronize the adapter into the pinned/target `COGR-Station` checkout;
2. build the relevant Station server/shared projects;
3. run adapter-focused tests/conformance where available; and
4. for dependency/runtime updates, request `local-gate <branch/commit>` from the project owner when local Station verification is required.

Do not hide Station coupling behind mocks that bypass the native integration being tested.

## Design posture

Prefer long-term clean adapter boundaries over compatibility shims. The project is pre-production; obsolete integration debt should be removed instead of migrated merely because it exists.

Do not implement cognitive cheats to make live acceptance pass. Adapter code may be authoritative over host truth, but Coggents should receive only evidence/actions that can be explained through the environment boundary.

# COGR SS14 Adapter Architecture

**Status:** Initial standalone extraction boundary  
**Date:** August 30, 2026

## Purpose

COGR SS14 Adapter is the environment-specific integration layer between the environment-neutral COGR cognitive runtime and Space Station 14 / Robust Toolbox.

The adapter exists so COGR can remain ignorant of Station-native world representation while Station remains authoritative over its own mechanics.

> **COGR proposes; Station disposes.**

## Responsibility split

### COGR owns

- Coggent cognition and current cognitive state;
- Agent Memory Graph and learned knowledge;
- Cognitive Activation, Working Memory, Concerns, procedures, faculties, and Intent;
- cognition-owned spatial reasoning, remembered navigation, language, learning, and policy;
- environment-neutral contracts and transport semantics; and
- interpretation of bounded environmental evidence into cognitive state.

### Adapter owns

- mapping a live Station body into COGR body/authority contracts;
- connection lifecycle and transport hosting inside Station;
- bounded actor-relative perception projection;
- opaque environment-reference lifecycle;
- translating runtime action attempts into ordinary Station mechanics;
- returning authoritative action disposition/progress/results;
- passive cue and semantic-replica delivery from Station events;
- embodiment-support evidence derived from current native body state;
- adapter-local routing, diagnostics, configuration, and compatibility glue; and
- deterministic source installation/synchronization into a Station checkout.

### Station owns

- native entity/world state;
- physics and collision;
- pathfinding and native steering mechanics;
- visibility/occlusion authority;
- inventory, access, interactions, UI mechanics, and game rules;
- authoritative simulation timing; and
- whether physical actions actually succeed.

## Evidence boundary

Station truth is not Coggent knowledge.

```text
authoritative Station state
        ↓
bounded actor-relative adapter projection
        ↓
COGR environmental evidence contract
        ↓
perception / cognition-owned belief
```

The adapter must not export raw native identity or hidden world truth merely because it is available to server code.

Exact native coordinates may be used internally to implement Station mechanics and bounded projection. They must not become a privileged durable COGR cognitive map.

## Action boundary

```text
COGR Candidate / Intent
        ↓
environment action attempt + authority lease
        ↓
SS14 adapter validation
        ↓
ordinary Station-native action execution
        ↓
authoritative disposition/progress/result
        ↓
COGR fresh evidence / verification / learning
```

Action completion is not automatically proof of a desired world state. COGR decides whether fresh evidence is needed for cognitive completion.

## Opaque references

Environment references are scoped transient handles used to ground currently exposed evidence/actions. They are not durable Coggent identity and must fail closed when connection, body, generation, visibility, or other required authority becomes stale.

## Spatial behavior

The adapter may use native Station steering/pathfinding to realize a bounded locomotor action. It must not choose remembered destinations, construct cognitive routes, retain adapter-side route cursors, or inject hidden path truth into cognition.

Native pathfinding answers **how the current physical action can be executed locally**, not **where the Coggent should decide to go**.

## Population behavior

Adapter work should be event-driven and bounded. Avoid ambient full-world/full-population scans or per-agent polling when native Station events and regional invalidation can provide the same authority.

Population/interleaving may affect throughput; it must not change a focal Coggent's cognitive semantics through adapter shortcuts.

## Source integration model

Canonical adapter source lives in this repository under paths mirroring the Station destination:

```text
overlay/Content.Server/COGR/
overlay/Content.Shared/COGR/
```

`adapter-manifest.json` defines the declared ownership/synchronization boundary.

A deterministic synchronization tool should copy declared source into a compatible Station checkout, verify drift, and record the adapter revision consumed by that checkout.

This source-overlay design is packaging infrastructure. It must not introduce a second runtime protocol or hidden loader semantics.

## Testbed boundary

`COGR-Station` remains the complete integration checkout and live acceptance testbed. Acceptance maps, Station-specific launch presets, runtime binary bundles, and full-fork patches stay there unless separately classified as reusable adapter material.

After extraction is green, reusable adapter behavior authored only in the Station mirror should be treated as drift.

## Compatibility

The adapter should record compatible Station/Robust and COGR contract revisions explicitly. Compatibility metadata is not cognitive state.

When an upstream API change requires a fork-level patch, keep that patch explicit rather than smuggling generalized Station implementation into the adapter.

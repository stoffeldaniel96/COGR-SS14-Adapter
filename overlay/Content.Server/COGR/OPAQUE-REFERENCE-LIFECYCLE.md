# COGR Opaque Reference Lifecycle

Station owns the mapping from opaque COGR environment references to native SS14 entities. Raw `EntityUid` values never cross the adapter boundary.

## Issuance

Actor-relative projection issues references under the exact active:

- connection;
- agent;
- body;
- body-authority generation; and
- native target entity.

The adapter stores this ownership metadata beside the core reference registry so later invalidation uses the original scope rather than reconstructing ownership from current authority.

## Invalidation

References are invalidated when:

- an exposed world entity terminates;
- a controlled body shuts down;
- an existing body is rebound and its authority generation rotates;
- the owning connection ends or is replaced; or
- the adapter performs an orderly shutdown.

Projector cache entries are removed in the same operation.

When the original duplex stream remains writable, Station emits a canonical `ReferenceInvalidationMessage` containing only the owning agent ID, opaque references, and a bounded lifecycle reason. If the stream is already unavailable, Station still invalidates every local mapping; stream closure is then the remote signal that connection-scoped references are stale.

## Ordering invariant

Body-scoped references are invalidated before Station increments or revokes the corresponding authority generation. References therefore cannot survive into a later embodiment epoch, and invalidation retains the provenance required to address the original owner correctly.

## Action authority

An opaque reference is actionable only within the exact authority/observer scope that issued it. A passive/heard source reference does not become interaction authority merely because Station can attribute the cue to a source.

Resolution must fail closed for stale, wrong-connection, wrong-agent, wrong-body, or wrong-generation references. A stale reference must never resolve to a later entity by reuse or coincidence.

## Validation

Reference lifecycle validation should prove:

1. repeated observations of a still-valid target retain stable opaque reference identity within the issuing scope;
2. raw Station entity identity is absent from cognition-visible payloads;
3. entity termination invalidates the corresponding reference;
4. body/authority replacement invalidates the old embodiment's references before the new generation is usable;
5. connection teardown clears only that connection's references; and
6. a previously invalidated reference cannot authorize a later action.

Live operator commands may exercise these invariants, but command names are diagnostic tooling rather than part of the reference contract.
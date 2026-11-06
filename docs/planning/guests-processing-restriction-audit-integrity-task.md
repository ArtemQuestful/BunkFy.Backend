# Guests Processing-Restriction Audit Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make the Guests-owned processing-restriction aggregate, effective projection,
and immutable transition receipt fail closed when a provider defect, direct
database action, unsafe restore, or future persistence path attempts to store a
shape the domain cannot produce.

This slice does not change restriction policy, permissions, Data Rights case
approval, public contracts, tenant termination, or downstream projection
protocols.

## Audit Result

The application flow is coherent and bounded. Apply and release commands use a
tenant-local Guest operation lock, recheck the exact approved Data Rights case,
Guest version, restriction version, and projection revision, then persist the
aggregate transition, effective projection, and append-only receipt in one
transaction. Missing or unsupported projection state fails closed, and the
existing query indexes avoid tenant-wide scans.

The remaining gap is below that flow. PostgreSQL currently permits:

- empty restriction, projection, receipt, idempotency, case, event, property,
  or Guest coordinates and blank tenant scope;
- blank or non-normalized actor evidence and default timestamps;
- a terminal restriction version or projection revision/count combination the
  domain cannot produce;
- a restriction whose tenant/property/Guest coordinate has no effective
  projection; and
- an immutable receipt whose tenant/property/Guest coordinates do not match
  the referenced restriction.

## Ownership

- Guests owns restriction lifecycle, effective state, immutable owner receipts,
  enforcement, and their durable database contract.
- Data Rights owns the approved case, directive, and decision revision. It does
  not write Guests persistence.
- GMA owns tenant scoping, command transactions, optimistic persistence
  primitives, and projection-rebuild infrastructure. No Guests policy or audit
  relationship belongs in the framework.
- The Guests PostgreSQL migrations project owns the concrete schema change and
  provider proof.

## Invariants

1. Restriction, projection, receipt, idempotency, case, event, property, and
   Guest identifiers are non-empty; persisted tenant scope is non-blank.
2. Apply/release and receipt actors are non-empty and stored in the trimmed
   form produced by the domain.
3. Apply, release, projection-transition, and receipt timestamps cannot use the
   domain's rejected default value; release remains at or after apply.
4. Active restrictions are version one. Released restrictions contain complete
   release evidence and are version two, matching the terminal domain model.
5. A projection has a positive generated ordinal. Its revision, active count,
   effective flag, and apply/release parity form a state reachable from the
   zero-state projection through domain transitions.
6. Every restriction references one effective projection with the same tenant,
   property, and Guest coordinate.
7. Every receipt references one restriction with the same tenant, property,
   and Guest coordinate. Receipt action/version rules remain explicit and
   release receipts report terminal restriction version two.
8. Existing bounded operational, idempotency, approval, export, and destruction
   indexes remain intact. New indexes exist only where required to enforce the
   two evidence relationships.

## Delivery

1. Add named coordinate, audit-text, timestamp, lifecycle, ordinal, and
   projection-state constraints to the three Guests configurations.
2. Add tenant-qualified restriction-to-projection and receipt-to-restriction
   relationships with stable provider names.
3. Extend focused model metadata tests for the complete durable contract.
4. Generate and review the additive BunkFy PostgreSQL migration.
5. Add one PostgreSQL 16 scenario that preserves valid zero/active/released and
   multi-restriction state, rejects representative malformed writes with exact
   provider names, and proves downgrade plus re-upgrade.
6. Run focused checks while editing, followed by one provider scenario and one
   consolidated backend gate at the finished slice boundary.

## Deployment Safety

The migration does not rewrite or discard Guest data. The new constraints and
foreign keys validate existing rows, so malformed legacy state stops deployment
instead of being silently repaired. A hosted rollout needs an exact-release
preflight and an operator-approved repair or projection rebuild for any finding;
disposable PostgreSQL proof does not establish production-data cleanliness.

## Deliberately Deferred

- Do not use a trigger to compare `ActiveRestrictionCount` with a live count of
  aggregate rows on every write. The tenant-local operation lock and transaction
  remain the authoritative write boundary; a trigger would add contention and
  duplicate domain behavior.
- Do not add action-specific computed columns and two wide foreign-key graphs
  solely to bind a receipt's generic case fields to either apply or release
  columns. Approval is rechecked before mutation and receipts are append-only;
  exact case binding can accompany a future versioned receipt schema if an
  external audit requirement justifies that storage cost.
- Do not move Guests-specific restriction semantics into GMA.
- Hosted migration, production preflight, repair execution, and deployed proof
  remain release-admission work.

## Completion Criteria

- valid existing restriction, projection, and receipt rows upgrade unchanged;
- impossible coordinates, actors, timestamps, lifecycle versions, projection
  states, and tenant evidence relationships fail at PostgreSQL;
- focused model, domain, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the completed slice boundary; and
- GMA and the web application remain unchanged.

## Verification

- The focused Guests restriction/model slice passes 26/26, including the
  distinct apply/release case rule, exact terminal receipt version, complete
  provider constraints, both tenant-qualified relationships, and removal of
  the redundant receipt alternate key.
- The PostgreSQL 16 migration scenario passes 1/1 in 12 seconds. It preserves
  zero-state, two-active-restriction, and released histories, rejects malformed
  aggregate/projection/receipt coordinates, actors, timestamps, versions, and
  relationship evidence by exact named constraints, then completes downgrade
  plus re-upgrade without losing rows.
- The consolidated backend verifier passes solution synchronization and source-
  package guards, a serialized build with zero warnings and zero errors, all 21
  migration-drift checks, and all 5,711 configured non-Docker tests.
- Guests passes 210/210, Operations Notifications 104/104, Architecture
  112/112, and non-Docker Integration 65/65.
- GMA and the web application are unchanged.

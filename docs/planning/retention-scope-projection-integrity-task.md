# Retention Scope Projection Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make Retention's rebuildable organization and property scope projections fail
closed when a malformed integration event, direct database write, unsafe
restore, or future persistence path attempts to store a scope shape that the
module cannot schedule safely.

This slice covers only the local tenant and property projections and corrects
the Retention development note to describe the already-versioned retry-request
tenant-export stream. It does not change schedule ownership, execution or
retry lifecycles, owner-module retention behavior, Task Runtime, tenant
termination activation, or evidence deletion periods.

## Audit Finding

Retention's authoritative execution, schedule-health, and durable retry state
already has a shared aggregate and PostgreSQL contract. Its rebuildable scope
projections remain materially weaker:

- constructors and merge methods accept empty coordinates or non-positive
  source versions;
- an equal source version with different organization, lifecycle, or policy
  facts is silently treated as an ordinary replay;
- PostgreSQL permits an empty property or organization id, a topology-unknown
  row marked active, and an `IsKnown` value that contradicts both source
  streams; and
- the nullable retention-policy version can evade the current policy check
  because PostgreSQL check constraints accept an unknown expression.

The tenant-termination contract itself is healthy: export schema version 2,
catalog version 3, metadata, implementation, and deterministic replay tests
all include executions, schedule states, and durable run-retry requests. The
module README still says that only two streams exist and is stale.

## Ownership

- Organizations owns organization identity, lifecycle, and source version.
- Properties owns property identity, topology lifecycle, processing policy,
  and the independent source versions for those streams.
- Retention owns only the local rebuildable facts needed to discover active
  tenant and property retention schedules.
- The Retention EF model owns the durable relational shape; the BunkFy
  PostgreSQL migrations project owns its concrete migration.
- GMA remains unchanged. Scope normalization, scoped persistence,
  transaction-key locking, inbox idempotency, and Task Runtime already provide
  the generic primitives.

## Invariants

### Tenant Projection

1. Tenant scope is canonical and non-blank; organization id is non-empty.
2. Source version is positive.
3. Organization identity is immutable for the tenant coordinate.
4. An older event is ignored, an exact equal-version replay is idempotent, and
   an equal-version event with different lifecycle evidence fails closed.

### Property Projection

1. Tenant scope is canonical and non-blank; property id is non-empty.
2. Topology and policy source versions are non-negative, while every applied
   event carries a positive source version.
3. Before topology is known, the property cannot be active.
4. Before policy is known, processing is disabled and no retention-policy
   version exists. A known policy always has a positive policy version.
5. `IsKnown` is true exactly when at least one independent source stream has
   been projected.
6. An older stream event is ignored, an exact equal-version replay is
   idempotent, and conflicting equal-version topology or policy evidence fails
   closed.

## Migration Safety

The migration adds or strengthens named checks without rewriting projection
data. Existing malformed rows stop migration at the constraint that found
them; a hosted rollout therefore needs a preflight against the exact release
database and an operator-approved repair or projection rebuild. Disposable
PostgreSQL proof cannot establish that hosted data is clean.

## Delivery

1. Validate and canonicalize projection coordinates and incoming source
   versions in the persistence-owned projection models.
2. Detect conflicting equal-version organization, topology, and policy facts
   while retaining idempotent replay and monotonic stale-event behavior.
3. Declare complete named tenant/property projection constraints in the EF
   model and assert their exact model ownership.
4. Generate and review the additive/strengthening PostgreSQL migration.
5. Add focused model behavior tests and one PostgreSQL 16 upgrade scenario
   that preserves valid topology-first, policy-first, and complete rows while
   rejecting contradictory writes by stable constraint name.
6. Correct the Retention tenant-export stream documentation and close with
   focused checks followed by one coherent end-of-slice gate.

## Deferred

- Retention execution and retry history cleanup remains policy-gated. Cleanup
  must preserve each schedule's current `LastExecutionId`, actionable retry
  requests, legal holds, tenant-export evidence, and approved operational
  evidence windows. Those periods and hold semantics require product/legal
  approval and are not invented in this slice.
- Generic Task Runtime history retention remains GMA-owned and is not reused as
  BunkFy Retention evidence policy.
- Hosted projection preflight/rebuild, migration application, rollback
  rehearsal, and same-release deployment evidence remain deployment admission
  work.

## Completion Criteria

- valid tenant, topology-first, policy-first, and fully known projection rows
  upgrade unchanged;
- malformed coordinates, versions, known-state, topology-state, and
  policy-state writes fail at PostgreSQL with stable names;
- stale events remain no-ops, exact replays remain idempotent, and conflicting
  equal-version evidence is rejected before persistence;
- focused Retention, architecture, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the finished slice boundary; and
- no GMA repository change is required.

## Verification Evidence

- The official Retention migration workflow generated
  `20260821172809_AddRetentionScopeProjectionIntegrity`; its build completed
  with 0 warnings and 0 errors.
- Focused projection behavior and EF-model checks passed 11/11; the complete
  Retention module suite passed 83/83.
- `Integration.Tests` built with 0 warnings and 0 errors. The targeted
  PostgreSQL 16 upgrade scenario passed 1/1 in 7 seconds, preserving valid
  topology-first, policy-first, and complete rows while rejecting malformed
  rows by stable constraint name.
- All GMA and BunkFy migration providers passed drift checks.
- The serial backend solution build passed with 0 warnings and 0 errors. The
  finished no-build consolidated gate passed source-package checks, solution
  synchronization, migration drift, and all 5,671 non-Docker tests.
- `git diff --check` and the final solution synchronization check passed. No
  GMA repository was changed.

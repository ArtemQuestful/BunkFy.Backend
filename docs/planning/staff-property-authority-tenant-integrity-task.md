# Staff Property Authority Tenant Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make Staff's local Properties projection tenant-safe and fail closed. Staff uses
this rebuildable evidence to authorize property assignments, shape directory
visibility, and remove tenant-owned data during workspace destruction. A row
from another tenant must never authorize, reveal, overwrite, or be destroyed by
the active workspace.

This slice changes only Staff property-projection persistence and the consumers
that rely on its tenant boundary. Properties remains authoritative for property
identity, lifecycle, name, and source version. It does not change Staff profile,
employment, assignment, authorization-policy, or tenant-termination product
semantics.

## Audit Findings

`StaffPropertyProjection` persists `ScopeId` and has a composite tenant key, but
does not implement `IScopedEntity`. GMA therefore installs neither its tenant
query filter nor scoped-write validation for this row.

The missing classification affects several Staff paths:

- `IsActiveAsync` and `AreAllActiveAsync` can authorize a local assignment from
  a same-id or foreign-only active property row;
- Staff directory queries can expose assignments by treating a foreign active
  projection as local property authority;
- projection updates lock by tenant and property but load by property id only,
  so they can mutate a same-id row from another tenant; and
- Staff tenant destruction iterates the projection set without an explicit
  predicate, relying on the model filter that is currently absent, so one
  workspace can delete another workspace's property evidence.

The projection also accepts malformed coordinates, lifecycle state, source
version, and active payload. It silently ignores every equal-version event,
including a conflicting fact.

## Ownership

- Properties owns property identity, lifecycle, name, and source version.
- Staff owns its local rebuildable property evidence and the decisions that use
  it for assignment authorization and directory visibility.
- Staff tenant termination owns deletion of only the active tenant's local
  projection rows.
- Staff persistence owns tenant filtering, monotonic merge behavior, and
  relational defense in depth for these rows.
- The BunkFy PostgreSQL migrations project owns the concrete migration.
- GMA already owns `IScopedEntity`, canonical tenant identifiers, named query
  filters, and scoped-write validation. No Staff policy belongs in GMA.

## Invariants

### Tenant Boundary

1. Every non-owned Staff model with a persisted `ScopeId` is explicitly
   classified as tenant scoped.
2. Ordinary projection queries and writes use the active tenant filter and
   scoped-write guard.
3. Projection repository operations require an enabled, canonical active
   scope.
4. Two tenants may use the same property id without observing, authorizing
   from, mutating, or deleting each other's row.

### Property Authority Projection

1. Tenant scope is canonical and non-blank; property id is non-empty.
2. Source version is positive and status is Active or Retired.
3. Active state has a normalized non-empty property name. A payloadless Retired
   event preserves the last projected name, including a first observed
   retirement for which no name is known.
4. An older event is a no-op, an exact equal-version replay is idempotent, and a
   conflicting equal-version event fails closed.
5. `IsActiveAsync` and `AreAllActiveAsync` consider only current-tenant rows;
   empty property identifiers fail closed.

### Staff Consumers

1. Assignment commands cannot use foreign property evidence.
2. Directory queries cannot reveal assignments through foreign property
   evidence.
3. Tenant destruction removes local projection rows while preserving every
   foreign tenant row, including rows with the same property id.

## Migration Safety

The migration adds named checks without rewriting rows. Existing malformed rows
stop migration at the detecting constraint. A hosted rollout therefore requires
an exact-release preflight and an operator-approved repair or projection rebuild
before migration; disposable PostgreSQL proof cannot establish hosted-data
cleanliness.

## Delivery

1. Classify the projection as `IScopedEntity` and normalize its coordinates.
2. Require the active canonical tenant for projection reads and writes, retain
   tenant-qualified transaction-key serialization, and use tenant-qualified
   lookup.
3. Add stale, exact-replay, and conflicting-replay semantics.
4. Add named relational checks for coordinates, lifecycle state, text, and
   source version.
5. Add a Staff model guard covering every mapped non-owned `ScopeId` type.
6. Add focused repository and tenant-destruction regression proof.
7. Generate and review the official PostgreSQL migration, then add one
   PostgreSQL 16 upgrade scenario covering same-id tenants, filter behavior,
   constraints, and downgrade.
8. Run focused checks followed by one consolidated end-of-slice backend gate.

## Deferred

- Add a generic GMA EF model guard requiring every persisted `ScopeId` property
  to be explicitly classified as scoped, global, or deliberately unfiltered.
  Remaining BunkFy domains must first be audited and aligned domain by domain.
- Hosted projection preflight/rebuild, migration application, rollback
  rehearsal, and same-release deployment evidence remain deployment-admission
  work.
- Changes to Staff roles, permission policies, onboarding, employment, or
  assignment behavior are outside this persistence-integrity slice.

## Completion Criteria

- active-tenant assignment and directory decisions cannot be satisfied by a
  same-id or foreign-only property row;
- local projection updates cannot mutate a same-id foreign row;
- Staff tenant destruction preserves all foreign projection rows;
- canonical scoped writes remain valid and unscoped, mismatched, malformed, or
  invalid-coordinate operations fail closed;
- stale events remain no-ops, exact replays remain idempotent, and conflicting
  equal-version facts are rejected before persistence;
- valid same-id multi-tenant rows upgrade unchanged and malformed rows fail
  under PostgreSQL with stable constraint names;
- focused Staff, architecture, migration-drift, and PostgreSQL proofs pass
  before one consolidated backend gate; and
- no GMA repository change is required for this slice.

## Verification

- Generated and reviewed migration
  `20260821191504_AddStaffPropertyAuthorityTenantIntegrity`; it adds only the
  three new projection checks described above, retains the existing positive
  version check, and has a symmetric downgrade.
- Seven focused projection, repository, model-classification, directory, and
  tenant-destruction tests pass.
- The complete Staff suite passes: 281 tests.
- `Integration.Tests` builds with zero warnings and zero errors.
- The focused PostgreSQL 16 migration scenario passes: 1 test in 9 seconds. It
  proves same-id tenant isolation, foreign-only authorization failure,
  tenant-local mutation, all four projection constraints, and downgrade.
- The consolidated backend verifier passes solution and source-package checks,
  a serialized zero-warning build, all 21 migration-drift checks, every
  configured non-Docker test project, Staff 281/281, Architecture 112/112, and
  Integration 65/65.
- GMA and the web application remain unchanged. Hosted data preflight,
  migration rehearsal, and same-release deployment evidence remain deferred as
  deployment-admission work.

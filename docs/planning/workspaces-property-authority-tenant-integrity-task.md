# Workspaces Property Authority Tenant Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make the Workspaces-owned Properties projection and access-plan property rows
tenant-safe and fail closed. Workspaces uses the projection as authorization
evidence before assigning property-scoped staff access. A same-id row from
another tenant, a conflicting replay, or malformed persistence state must never
authorize access in the active workspace.

This slice changes only Workspaces projection persistence, access-plan property
scope classification, relational constraints, and focused proof. Properties
remains authoritative for property identity and lifecycle. It does not change
staff onboarding, invitation, enrollment, access-profile, or tenant-termination
product behavior.

## Audit Findings

`WorkspacePropertyProjection` persists a `ScopeId` and uses a tenant-qualified
primary key, but does not implement `IScopedEntity`. GMA therefore installs
neither its tenant query filter nor scoped-write validation. The repository:

- counts active rows by property id without an active tenant predicate, so a
  foreign active row can satisfy a missing local property and same-id rows can
  distort the count; and
- locks by tenant and property but then loads by property id only, so it can
  mutate a same-id row from another tenant.

The projection also silently ignores every equal-version event, including a
conflicting event, and accepts malformed coordinates, source versions, status,
and active payload. PostgreSQL currently checks only that the version is
positive.

`WorkspaceStaffAccessPlanProperty` is a separately mapped tenant-owned child
with `ScopeId`, `PlanId`, and `PropertyId`, but is also not explicitly scoped.
Its composite same-scope foreign key protects normal aggregate traversal, while
direct queries and writes do not receive the framework tenant filter or guard.

## Ownership

- Properties owns property identity, lifecycle, name, and source version.
- Workspaces owns its local rebuildable property evidence and the decision that
  every requested staff property assignment refers to an active property.
- Workspaces owns access-plan property membership and its same-tenant relation
  to the access-plan aggregate.
- Workspaces persistence owns tenant filtering, monotonic merge behavior, and
  relational defense in depth for these rows.
- The BunkFy PostgreSQL migrations project owns the concrete migration.
- GMA already owns `IScopedEntity`, canonical tenant identifiers, named query
  filters, and scoped-write validation. No Workspaces policy belongs in GMA.

## Invariants

### Tenant Boundary

1. Every non-owned Workspaces model with a persisted `ScopeId` is explicitly
   classified as tenant scoped.
2. Ordinary projection and access-plan property queries use the active tenant
   filter, and ordinary writes use the scoped-write guard.
3. Projection repository operations require an enabled, canonical active scope.
4. Two tenants may use the same property id without observing, authorizing from,
   or mutating each other's row.

### Property Authority Projection

1. Tenant scope is canonical and non-blank; property id is non-empty.
2. Source version is positive and status is Active or Retired.
3. Active state has a normalized non-empty property name. A payloadless Retired
   event may preserve the last projected name, including when retirement is the
   first observed event and no name is known.
4. An older event is a no-op, an exact equal-version replay is idempotent, and a
   conflicting equal-version event fails closed.
5. `AreAllActiveAsync` returns true only when every distinct requested id has an
   active row in the current tenant. Invalid identifiers fail closed.

### Access-Plan Property Rows

1. Tenant scope is canonical and non-blank; plan and property ids are non-empty.
2. The child scope equals its parent plan scope through the existing composite
   foreign key.
3. Tenant destruction remains an explicit maintenance path using
   `IgnoreQueryFilters` plus an exact tenant predicate.

## Migration Safety

The migration strengthens named checks without rewriting rows. Existing
malformed rows stop migration at the detecting constraint. A hosted rollout
therefore requires an exact-release preflight and an operator-approved repair or
projection rebuild before migration; disposable PostgreSQL proof cannot prove
hosted-data cleanliness.

## Delivery

1. Classify the projection and access-plan property child as `IScopedEntity` and
   normalize their coordinates.
2. Require the active canonical tenant for projection reads and writes, then
   retain tenant-qualified transaction-key serialization and filtered lookup.
3. Add stale, exact-replay, and conflicting-replay semantics to the projection.
4. Add named relational checks for projection coordinates, lifecycle state,
   text, and access-plan property coordinates.
5. Add a Workspaces model guard covering every mapped non-owned `ScopeId` type.
6. Generate and review the official PostgreSQL migration.
7. Add focused model/repository proof and one PostgreSQL 16 upgrade scenario
   covering same-id tenants, filter behavior, constraints, and downgrade.
8. Run focused checks followed by one consolidated end-of-slice backend gate.

## Deferred

- Add a generic GMA EF model guard requiring every persisted `ScopeId` property
  to be explicitly classified as scoped, global, or deliberately unfiltered.
  Remaining BunkFy domains must first be audited and aligned domain by domain.
- Hosted projection preflight/rebuild, migration application, rollback rehearsal,
  and same-release deployment evidence remain deployment-admission work.
- Changes to Workspaces product roles, onboarding flows, or Properties domain
  semantics are outside this persistence-integrity slice.

## Completion Criteria

- an active tenant cannot read, authorize from, or mutate a same-id foreign
  projection;
- missing local properties cannot be satisfied by foreign active rows;
- canonical scoped writes remain valid and unscoped, mismatched, malformed, or
  invalid-coordinate operations fail closed;
- stale events remain no-ops, exact replays remain idempotent, and conflicting
  equal-version facts are rejected before persistence;
- valid same-id multi-tenant projection and access-plan rows upgrade unchanged;
- malformed projection and access-plan property rows fail under PostgreSQL with
  stable constraint names;
- focused Workspaces, architecture, migration-drift, and PostgreSQL proofs pass
  before one consolidated backend gate; and
- no GMA repository change is required for this slice.

## Verification

- Generated and reviewed migration
  `20260821184307_AddWorkspacePropertyAuthorityTenantIntegrity`; it adds only
  the four named checks described above and has a symmetric downgrade.
- The six focused projection, repository, and model-classification tests pass.
- The complete Workspaces suite passes: 372 tests.
- `Integration.Tests` builds with zero warnings and zero errors.
- The focused PostgreSQL 16 migration scenario passes: 1 test. It proves
  same-id tenant isolation, foreign-only authorization failure, direct child
  filtering, tenant-local mutation, all four named constraints, and downgrade.
- The consolidated backend verifier passes solution and source-package checks,
  a serialized zero-warning build, all 21 migration-drift checks, every
  configured non-Docker test project, Architecture 112/112, and Integration
  65/65.
- GMA and the web application remain unchanged. Hosted data preflight,
  migration rehearsal, and same-release deployment evidence remain deferred as
  deployment-admission work.

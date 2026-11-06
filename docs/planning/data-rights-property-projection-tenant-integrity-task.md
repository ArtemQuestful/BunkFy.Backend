# Data Rights Property Projection Tenant Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make the Data Rights-owned Properties projection tenant-safe and fail closed.
The projection supplies property lifecycle, time-zone, and governance-policy
evidence to response-deadline and destructive-approval decisions. A same-id
row from another tenant, a conflicting replay, or a malformed restore must
never become valid privacy-policy evidence.

This slice changes only the Data Rights projection, its repository, relational
shape, and focused proof. Properties remains authoritative for every projected
fact. It does not change Data Rights case, export, correction, restriction,
anonymisation, or tenant-termination workflows.

## Audit Finding

`DataRightsPropertyProjection` persists a `ScopeId` and uses a tenant-qualified
primary key, but it does not implement `IScopedEntity`. The existing GMA scope
conventions therefore install neither a tenant query filter nor scoped-write
validation for this entity. Repository reads query only by property id, and a
write can load a same-id row from another tenant before applying an event.

The projection's stream integrity is also weaker than the policy decisions it
supports:

- empty tenant/property coordinates and non-positive incoming versions are
  accepted by the model;
- equal source versions are silently ignored even when topology or governance
  facts differ;
- PostgreSQL does not bind `IsKnown`, topology status/payload, policy source
  version, or policy acknowledgement shape; and
- raw SQL, an unsafe restore, or a future persistence path can store malformed
  policy keys, country codes, or digests that application code would otherwise
  reject.

## Ownership

- Properties owns property identity, lifecycle, time zone, processing status,
  governance policy, and source versions.
- Data Rights owns only its local rebuildable evidence used for privacy
  deadlines and approval gates.
- Data Rights persistence owns tenant filtering, monotonic merge behavior, and
  relational defense in depth for this projection.
- The BunkFy PostgreSQL migrations project owns the concrete migration.
- GMA already owns `IScopedEntity`, canonical tenant identifiers, query
  filters, and scoped-write validation. No Data Rights policy belongs in GMA.

## Invariants

### Tenant Boundary

1. Tenant scope is canonical and non-blank; property id is non-empty.
2. Every ordinary projection query and write is filtered and validated by the
   active tenant context.
3. Two tenants may use the same property id without observing or mutating each
   other's row.

### Topology Stream

1. The topology source version is non-negative; every applied event uses a
   positive version.
2. Version zero is an unknown placeholder. A positive version carries a known
   Active or Retired status.
3. Active topology has a normalized non-empty property name and time zone.
   A Retired event may omit payload and preserve previously projected values.
4. An older event is a no-op, an exact equal-version replay is idempotent, and
   conflicting equal-version topology fails closed.

### Policy Stream

1. The policy source version is non-negative; every applied policy or rebuild
   fact uses a positive version.
2. Unconfigured processing has no governance binding. Enabled or Suspended
   processing has one complete, contract-valid binding.
3. Policy keys, uppercase country code, lowercase SHA-256, positive revisions,
   bounded times, and acknowledgement key/version rows retain the Properties
   contract shape in PostgreSQL.
4. An older policy fact is a no-op, an exact equal-version replay is
   idempotent independent of acknowledgement order, and a conflicting replay
   fails closed.

### Combined State

`IsKnown` is true exactly when topology or policy evidence has a positive
source version. Policy-first and topology-first delivery remain valid; deadline
and destructive-approval policies continue to require the exact streams they
consume.

## Migration Safety

The migration adds or strengthens named checks without rewriting projection
rows. Existing malformed rows stop migration at the detecting constraint. A
hosted rollout therefore requires an exact-release preflight and an
operator-approved repair or projection rebuild before migration; disposable
PostgreSQL proof cannot establish hosted-data cleanliness.

## Delivery

1. Classify the projection as `IScopedEntity` and normalize coordinates.
2. Require an active canonical tenant for repository reads and writes, then
   normalize write coordinates before lock and lookup while retaining the
   existing transaction-key serialization.
3. Add exact replay/conflict semantics for topology and policy streams.
4. Add complete model-owned constraints for coordinates, stream state, known
   state, policy content, and acknowledgement rows.
5. Generate and review the PostgreSQL migration.
6. Add focused model/repository tests and one PostgreSQL 16 upgrade scenario
   covering valid policy-first, topology-first, same-id multi-tenant, and
   malformed raw-write cases.
7. Align Data Rights documentation, then run focused checks followed by one
   coherent end-of-slice backend gate.

## Deferred

- Add a generic GMA EF model guard requiring every persisted `ScopeId` property
  to be explicitly classified as scoped, global, or deliberately unfiltered.
  Enabling that guard currently exposes additional legacy BunkFy models in
  other domains; they must be audited and aligned domain by domain instead of
  receiving blanket annotations in this slice.
- Hosted projection preflight/rebuild, migration application, rollback
  rehearsal, and same-release deployment evidence remain deployment-admission
  work.
- Country-policy content, legal decisions, and operating-country activation
  remain Properties/product governance responsibilities.

## Completion Criteria

- active tenant contexts cannot read or mutate a same-id foreign projection;
- canonical scoped writes remain valid and unnormalized/mismatched writes fail;
- stale facts remain no-ops, exact replays remain idempotent, and conflicting
  equal-version facts are rejected before persistence;
- valid topology-first, policy-first, complete, and same-id multi-tenant rows
  upgrade unchanged;
- malformed coordinates, topology, policy, known state, formats, and
  acknowledgement rows fail under PostgreSQL with stable constraint names;
- focused Data Rights, architecture, migration-drift, and PostgreSQL proofs
  pass before one consolidated backend gate; and
- no GMA repository change is required for this slice.

## Verification

- Official migration generation through `eng/add-migration.ps1` produced
  `20260821181217_AddDataRightsPropertyProjectionTenantIntegrity`; review
  confirmed constraint-only upgrade and reversible downgrade behavior.
- Focused projection and model tests passed 22/22, and the complete Data Rights
  suite passed 572/572.
- `Integration.Tests` built with zero warnings and zero errors.
- The targeted PostgreSQL 16 migration scenario passed 1/1 in 11 seconds,
  including valid partial/complete state preservation, same-id tenant
  isolation, exact named-constraint rejection, and downgrade to the previous
  migration.
- The consolidated backend gate passed solution synchronization, source-package
  checks, a serial zero-warning solution build, all 21 migration-drift checks,
  and 5,675 non-Docker tests including 112/112 architecture guards and 65/65
  non-Docker integration tests.
- The GMA framework and module repositories remained unchanged. Hosted-row
  preflight, migration rehearsal against hosted data, and deployment evidence
  remain deferred admission work rather than repository proof.

# Properties Governance Revision Tenant Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make Properties' append-only governance revision history tenant-safe and fail
closed. A revision records the actor, decision, and policy coordinates behind a
property-processing transition; another workspace must never read, write,
export, or destroy that evidence through the active tenant's persistence path.

This slice changes only the governance-revision persistence boundary and its
focused consumers. Properties remains authoritative for property processing and
governance state. Country-policy evaluation, legal policy content, and product
behavior do not change.

## Audit Finding

`PropertyGovernanceRevision` persists `ScopeId` but is the only mapped
scope-shaped Properties entity that does not implement `IScopedEntity`. GMA
therefore installs neither its tenant query filter nor its scoped-write guard
for this append-only evidence table.

The current tenant-termination export and destruction paths add explicit tenant
predicates, but ordinary `GovernanceRevisions` queries remain unfiltered and a
future consumer can silently cross the boundary. The entity constructor also
copies an arbitrary write model without normalizing scope or rejecting empty
coordinates, malformed policy evidence, inconsistent action/evidence shape,
invalid actor/reason text, or a default timestamp. PostgreSQL currently checks
only the property version and action range.

## Ownership

- Properties owns property governance transitions and their append-only
  revision evidence.
- Data Rights and tenant termination may export or remove only the active
  tenant's Properties-owned evidence.
- Properties persistence owns tenant filtering, scoped-write validation,
  evidence-shape validation, append-only enforcement, and relational defense in
  depth.
- The BunkFy PostgreSQL migrations project owns the concrete migration.
- GMA already owns `IScopedEntity`, canonical tenant identifiers, named query
  filters, and scoped-write validation. No Properties-specific rule belongs in
  GMA.

The revision id is generated inside the trusted Properties command path and is
not a provider or client idempotency coordinate. It remains the global primary
key; tenant qualification belongs on access and ownership boundaries rather
than in an unnecessary key migration.

## Invariants

### Tenant Boundary

1. Every non-owned Properties model with a persisted `ScopeId` is explicitly
   classified as tenant scoped.
2. Scope is canonical and every ordinary revision query is filtered by the
   active tenant.
3. Added revisions must match the active tenant; disabled, malformed, and
   mismatched scoped writes fail before persistence.
4. Export and tenant destruction process only the requested active tenant and
   preserve foreign governance evidence.

### Revision Evidence

1. Revision and property ids are non-empty and property version is at least two,
   matching the first processing transition.
2. Action is one of Activated, Rebound, Reactivated, or Suspended.
3. Activated evidence has no previous coordinates and has complete current
   coordinates. Every later action has complete previous and current
   coordinates.
4. Country codes, policy keys, positive policy versions, and lowercase SHA-256
   values retain the Properties domain contract shape.
5. Decision reason and actor are normalized, bounded, non-empty text without
   control characters.
6. Occurrence time is non-default and stored in UTC.
7. Revisions remain append-only.

## Migration Safety

The migration adds named detecting constraints only; it does not rewrite
business evidence or change revision identity. Valid rows upgrade unchanged.
Malformed legacy evidence stops at the constraint that detects it, so a hosted
rollout still requires exact-release preflight and operator-approved repair
before migration. Downgrade removes only the new constraints.

## Delivery

1. Classify and validate `PropertyGovernanceRevision` as `IScopedEntity`.
2. Add complete model-owned constraints for coordinates, action/evidence shape,
   policy content, actor/reason text, and occurrence time.
3. Add a Properties model-wide scope-classification guard and direct query/write
   isolation proof.
4. Add focused export and actual tenant-destruction proof that foreign revision
   evidence is preserved.
5. Generate and review the official PostgreSQL migration.
6. Add one PostgreSQL 16 upgrade scenario covering valid preservation, tenant
   filtering/write enforcement, malformed raw writes, and downgrade.
7. Run focused checks followed by one consolidated end-of-slice backend gate.

## Deferred

- A generic GMA EF model guard requiring every persisted `ScopeId` property to
  be classified as scoped, global, or deliberately unfiltered. Remaining
  BunkFy domains must first be audited and aligned domain by domain.
- Hosted-row preflight, migration application, rollback rehearsal, and
  same-release deployment evidence remain deployment-admission work.
- Changes to country-policy content, operator approval semantics, or the
  Properties management UI are outside this persistence-integrity slice.

## Completion Criteria

- active tenants cannot query or persist another tenant's revision evidence;
- malformed revision coordinates and policy evidence fail before or at stable
  named PostgreSQL constraints;
- valid canonical revisions remain append-only and upgrade unchanged;
- export and actual destruction remain tenant-local and preserve foreign rows;
- the Properties model guard finds no unclassified scope-shaped entity;
- focused Properties and PostgreSQL proofs pass, followed by one consolidated
  backend gate; and
- GMA and the web application remain unchanged.

## Verification

- Generated and reviewed migration
  `20260821202459_AddPropertyGovernanceRevisionTenantIntegrity`; it adds only
  the five named governance-revision checks described above and has a symmetric
  downgrade.
- Eighteen focused entity, model, query/write isolation, export, and actual
  tenant-destruction tests pass.
- The complete Properties suite passes: 312 tests.
- `Integration.Tests` builds with zero warnings and zero errors.
- The focused PostgreSQL 16 migration scenario passes: 1 test in 9 seconds. It
  proves valid preservation through the canonical time-zone provenance
  migration, tenant filtering, all named constraints, downgrade, and re-upgrade.
- The consolidated backend verifier passes solution and source-package checks,
  a serialized zero-warning build, all 21 migration-drift checks, and all 5,705
  configured non-Docker tests, including Properties 312/312, Architecture
  112/112, and Integration 65/65.
- GMA and the web application remain unchanged. Hosted data preflight,
  migration rehearsal, and same-release deployment evidence remain deferred as
  deployment-admission work.

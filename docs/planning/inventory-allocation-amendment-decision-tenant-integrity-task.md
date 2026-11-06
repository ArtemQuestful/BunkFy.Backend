# Inventory Allocation Amendment Decision Tenant Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make Inventory's durable allocation-amendment decisions tenant-safe and fail
closed. A decision is idempotency evidence for one tenant-owned amendment
request; another workspace must never replay, export, anonymise, collide with,
or destroy that evidence.

This slice changes only the persistence boundary and consumers of amendment
decision evidence. Reservations remains authoritative for reservation changes,
Inventory remains authoritative for allocation feasibility and outcomes, and no
allocation or reservation product semantics change.

## Audit Findings

`InventoryAllocationAmendmentDecision` persists `ScopeId` but does not implement
`IScopedEntity`. GMA therefore installs neither its tenant query filter nor its
scoped-write guard for this table.

The missing classification affects several paths:

- replay lookup uses only `AmendmentRequestId`, so a foreign decision can be
  replayed as the current tenant's confirmed or rejected outcome;
- the globally keyed request id lets one tenant's row collide with another
  tenant's otherwise valid idempotency coordinate;
- Guest Data Rights export can include foreign decisions sharing a property and
  allocation coordinate;
- allocation anonymisation can remove or reason from foreign decisions sharing
  those coordinates; and
- tenant destruction iterates the unfiltered decision set and can delete another
  workspace's evidence.

The entity constructor also accepts malformed scope, empty coordinates,
non-canonical fingerprints, inconsistent outcome fields, and invalid decision
timestamps until PostgreSQL rejects only a subset of those shapes.

## Ownership

- Reservations owns the amendment request id and requested reservation change.
- Inventory owns the tenant-local decision, allocation outcome, replay behavior,
  and lifecycle of this durable evidence.
- Guest Data Rights, anonymisation, and tenant termination may consume or remove
  only the active tenant's Inventory evidence.
- Inventory persistence owns tenant filtering, scoped-write validation,
  tenant-local identity, and relational defense in depth.
- The BunkFy PostgreSQL migrations project owns the concrete migration.
- GMA already owns `IScopedEntity`, canonical tenant identifiers, named query
  filters, and scoped-write validation. No Inventory-specific rule belongs in
  GMA.

## Invariants

### Tenant Boundary

1. Every non-owned Inventory model with a persisted `ScopeId` is explicitly
   classified as tenant scoped.
2. The durable decision identity is `(ScopeId, AmendmentRequestId)`; two tenants
   may use the same request id without collision or observation.
3. Repository reads and writes require an enabled, canonical active scope and
   explicitly qualify the tenant coordinate.
4. Unscoped, mismatched, malformed, and empty-coordinate operations fail closed
   before persistence; accepted tenant identifiers are stored canonically.

### Durable Decision

1. Scope is canonical and all request, allocation, reservation, and property ids
   are non-empty.
2. The request fingerprint is exactly 64 lowercase hexadecimal characters.
3. A confirmed decision has no rejection reason and a positive allocation
   version. A rejected decision has a defined rejection reason and no allocation
   version.
4. Decision time is non-default and normalized to UTC.
5. An exact current-tenant replay returns the stored result; a different request
   fingerprint returns `RequestMismatch`; foreign evidence is invisible.

### Consumers

1. Guest Data Rights export includes only current-tenant amendment decisions.
2. Allocation anonymisation removes and verifies only current-tenant decisions.
3. Tenant destruction removes local decisions while preserving every foreign
   row, including one with the same amendment request id.

## Migration Safety

The migration replaces the global primary key with the tenant-local composite
key and strengthens the existing coordinates constraint. Existing valid rows
upgrade without rewriting business facts. Malformed legacy rows stop at the
named detecting constraint.

Downgrade to the global-key schema is safe only when no request id is shared by
multiple tenants. The migration must detect that condition and fail with an
operator-readable error instead of partially changing the schema. Hosted rollout
still requires exact-release preflight and operator-approved repair for any
malformed legacy data.

## Delivery

1. Classify and validate the decision entity as `IScopedEntity`.
2. Require and explicitly query the current canonical scope in the repository.
3. Change the relational identity to `(ScopeId, Id)` and strengthen named checks.
4. Add an Inventory model-wide scope-classification guard.
5. Add focused replay, repository, Data Rights, anonymisation, and actual tenant
   destruction isolation proof.
6. Generate and review the official PostgreSQL migration, including guarded
   downgrade behavior.
7. Add one PostgreSQL 16 upgrade scenario covering valid legacy preservation,
   same-id tenants, filter/write behavior, constraints, and downgrade.
8. Run focused checks followed by one consolidated end-of-slice backend gate.

## Deferred

- A generic GMA EF model guard requiring every persisted `ScopeId` property to be
  classified as scoped, global, or deliberately unfiltered. Remaining BunkFy
  domains must first be audited and aligned domain by domain.
- Hosted data preflight, migration application, rollback rehearsal, and
  same-release deployment evidence remain deployment-admission work.
- Changes to reservation amendment UX, retry policy, or provider-specific
  ingestion behavior are outside this persistence-integrity slice.

## Completion Criteria

- a foreign decision cannot be replayed, exported, removed by anonymisation, or
  destroyed by the active tenant;
- same amendment request ids can coexist for different tenants;
- valid canonical records persist and invalid records fail before or at the
  named database boundary;
- the Inventory model guard finds no unclassified scope-shaped entity;
- the official migration upgrades valid state, rejects malformed state, and
  guards unsafe downgrade;
- focused Inventory and PostgreSQL proofs pass, followed by one consolidated
  backend gate; and
- GMA and the web application remain unchanged.

## Verification

- Generated and reviewed migration
  `20260821195120_AddInventoryAllocationAmendmentDecisionTenantIntegrity`; it
  changes only the amendment-decision primary key and named coordinate/time
  checks, and guards unsafe downgrade before changing the schema.
- Forty-eight focused entity, repository, model, Data Rights, anonymisation, and
  actual tenant-destruction tests pass.
- The complete Inventory suite passes: 169 tests.
- `Integration.Tests` builds with zero warnings and zero errors.
- The focused PostgreSQL 16 migration scenario passes: 1 test in 8 seconds. It
  proves valid legacy upgrade, tenant-filtered same-id records, both new checks,
  guarded unsafe downgrade, safe downgrade after duplicate removal, and re-upgrade.
- The consolidated backend verifier passes solution and source-package checks,
  a serialized zero-warning build, all 21 migration-drift checks, every configured
  non-Docker test project, Inventory 169/169, Architecture 112/112, and
  Integration 65/65.
- GMA and the web application remain unchanged. Hosted data preflight, migration
  rehearsal, and same-release deployment evidence remain deferred as
  deployment-admission work.

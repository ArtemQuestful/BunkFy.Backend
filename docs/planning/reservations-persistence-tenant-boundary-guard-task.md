# Reservations Persistence Tenant Boundary Guard Task

Status: complete
Date: 2026-08-21

## Goal

Lock the verified Reservations persistence boundary into an executable model
guard. A future Reservations table must not silently omit GMA tenant scoping or
introduce a relationship whose database foreign key can cross workspaces.

This is a regression-protection slice. It does not change reservation behavior,
schema, identity, export, destruction, or policy.

## Audit Result

The current model is correctly tenant shaped:

- every mapped, non-owned Reservations entity with `ScopeId` implements
  `IScopedEntity` and receives a declared GMA query filter;
- every relationship between two tenant-owned Reservations entities carries
  `ScopeId` on both the dependent foreign key and principal key; and
- tenant termination enumerates every current owner table, verifies final
  absence, preserves only its documented lifecycle/receipt rows, and has
  focused export, destruction, replay, and foreign-tenant proofs.

The remaining gap is that existing model tests sample selected entity types.
They do not fail automatically when a future contributor adds a new
scope-shaped entity or an unqualified tenant relationship.

GMA inbox and outbox rows are deliberately outside this classification. They
are shared messaging infrastructure with nullable scope coordinates and global
worker access; Reservations separately admits tenant-owned message mutations
and filters them explicitly in tenant termination.

## Ownership

- Reservations owns the classification of its entities and relationships.
- GMA owns `IScopedEntity`, tenant query filters, and scoped-write validation.
- A generic framework model guard remains deferred until every BunkFy domain
  has classified its mixed global, scoped, and owned persistence types.

No Reservations-specific model policy belongs in GMA.

## Invariants

1. Every non-owned BunkFy Reservations model type with a persisted `ScopeId`
   implements `IScopedEntity`.
2. Every such type has a declared tenant query filter.
3. Every foreign key between two scoped Reservations types includes `ScopeId`
   in both the dependent and principal coordinates.
4. The guard is model-wide and discovers future mapped types automatically.
5. Existing tenant export, destruction, global projection ordinals, and
   internally generated global operation identifiers remain unchanged.

## Delivery

1. Add one model-wide scope classification and query-filter guard.
2. Add one model-wide tenant-qualified relationship guard.
3. Run the focused model and tenant-termination proofs, the complete
   Reservations suite, and one consolidated non-Docker backend gate.

## Deferred

- Generic GMA enforcement before all consuming domains have explicit
  classifications.
- Schema or identity changes where the current model already uses trusted
  global identifiers or global identity ordinals deliberately.
- Hosted deployment evidence; this slice has no runtime or migration change.

## Completion Criteria

- both model-wide guards pass over the complete Reservations model;
- existing tenant export and actual destruction proofs remain green;
- the complete Reservations suite and consolidated backend verifier pass; and
- no migration, GMA, or web change is produced.

## Verification

- A runtime audit of the design model classified all 33 mapped BunkFy
  Reservations tenant entities; every one implements `IScopedEntity`, has a
  declared query filter, and every tenant-to-tenant foreign key is scope
  qualified.
- The focused Reservations model suite passes: 19 tests.
- Existing tenant export and actual destruction proofs pass: 10 tests.
- The complete Reservations suite passes: 377 tests.
- The consolidated backend verifier passes solution and source-package checks,
  a serialized zero-warning build, all 21 migration-drift checks, and all 5,707
  configured non-Docker tests, including Reservations 377/377, Architecture
  112/112, and Integration 65/65.
- No migration or Docker scenario is required because persistence shape and
  runtime behavior are unchanged. GMA and the web application remain unchanged.

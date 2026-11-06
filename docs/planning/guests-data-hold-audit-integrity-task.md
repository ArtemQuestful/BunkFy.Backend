# Guests Data-Hold Audit Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make the Guests-owned data-hold record and its immutable transition receipt
fail closed when a provider defect, direct database action, unsafe restore, or
future persistence path attempts to store evidence the domain cannot produce.
At the same time, lock the verified Guests tenant model boundary into an
executable regression guard.

This slice does not change hold policy, permissions, Data Rights orchestration,
retention eligibility, public contracts, or tenant-termination behavior.

## Audit Result

The Guests persistence boundary is correctly tenant shaped:

- all 21 mapped, non-owned Guests entities with `ScopeId` implement
  `IScopedEntity` and receive a declared GMA query filter;
- every relationship between tenant-owned Guests entities includes `ScopeId`
  on both the dependent foreign key and principal key; and
- tenant export and destruction enumerate the complete current owner graph and
  already prove foreign-tenant isolation, resumability, replay, legal holds,
  append-only evidence, and final absence.

Existing tests sample selected entity types, so a future mapped type or
relationship could weaken that boundary without failing a model-wide guard.

The data-hold aggregate and receipt are constructed transactionally with
internally generated global identifiers, normalized tenant and text values,
non-empty coordinates, versioned lifecycle transitions, and append-only
receipt enforcement. PostgreSQL currently protects only lifecycle/version
shape and the receipt-to-hold tenant coordinate. A malformed direct write can
still persist empty identifiers, blank or non-normalized audit text, default
timestamps, or receipt property/Guest/reason coordinates that disagree with
the referenced hold.

## Ownership

- Guests owns hold policy evidence, lifecycle, immutable transition receipts,
  and their database contract.
- Data Rights owns case approval and orchestration; it does not write Guests
  persistence.
- GMA owns `IScopedEntity`, tenant query filters, and scoped-write validation.
  No Guests-specific receipt or policy invariant belongs in GMA.
- The BunkFy Guests PostgreSQL migrations project owns the concrete schema
  migration and provider proof.

## Invariants

1. Every non-owned BunkFy Guests model type with a persisted `ScopeId`
   implements `IScopedEntity` and has a declared tenant query filter.
2. Every foreign key between two scoped Guests types carries `ScopeId` in both
   dependent and principal coordinates.
3. Hold and receipt tenant, identity, property, Guest, and idempotency
   coordinates cannot be empty; persisted tenant scope cannot be blank.
4. Hold reason and actor evidence remains non-empty and in the normalized shape
   produced by the aggregate. Release evidence is either wholly absent or
   non-empty according to the existing lifecycle state.
5. Hold and receipt timestamps cannot use the domain's rejected default value;
   release time remains at or after placement.
6. A receipt's tenant, hold, property, Guest, and reason coordinates must match
   one authoritative hold row.
7. Existing globally generated primary identifiers and the receipt's current
   query indexes remain unchanged. No redundant `(ScopeId, Id)` receipt index
   is added merely for symmetry.

## Delivery

1. Add model-wide Guests scope and tenant-relationship guards.
2. Add named hold and receipt coordinate, audit-text, and timestamp constraints.
3. Strengthen the existing receipt foreign key with immutable hold coordinates
   and reason evidence.
4. Generate and review the additive BunkFy PostgreSQL migration.
5. Add one PostgreSQL 16 migration scenario that preserves valid place/release
   evidence and rejects representative malformed writes with exact provider
   constraint or foreign-key names.
6. Run focused checks during implementation, then one coherent provider and
   consolidated backend gate at the finished slice boundary.

## Deployment Safety

The migration does not rewrite or discard Guest data. New constraints and the
stronger foreign key validate existing rows, so malformed legacy evidence
stops deployment instead of being silently repaired. A hosted rollout needs a
preflight against the exact release database and an operator-approved repair
path; disposable PostgreSQL evidence does not prove production data is clean.

## Deferred

- A generic GMA model guard until every consuming product domain has explicitly
  classified its scoped, global, owned, inbox, and outbox persistence types.
- Database triggers that attempt to encode action-specific place/release actor
  and timestamp joins. The domain transaction and append-only ledger remain
  authoritative for those transition details.
- Hosted migration, production-data preflight, and deployed release evidence.

## Completion Criteria

- model-wide tenant guards cover the complete Guests model;
- valid hold and receipt rows upgrade unchanged;
- malformed coordinates, audit text, timestamps, and cross-row evidence fail at
  PostgreSQL with stable names;
- existing domain, export, destruction, and append-only behavior remains green;
- Guests migration drift, focused provider proof, and one consolidated backend
  gate pass; and
- GMA and the web application remain unchanged.

## Verification

- The focused Guests model suite passes 11/11, including both whole-model
  tenant guards and the composite hold-evidence relationship.
- The PostgreSQL 16 migration scenario passes 1/1 in 10 seconds. It preserves
  active and released holds plus place/release receipts, rejects malformed hold
  and receipt coordinates, audit text, and timestamps by exact named checks,
  rejects mismatched receipt evidence by the named composite foreign key, and
  completes downgrade plus re-upgrade without losing rows.
- The consolidated backend verifier passes solution synchronization and source-
  package guards, a serialized build with zero warnings and zero errors, all 21
  migration-drift checks, and all 5,709 configured non-Docker tests.
- The complete Guests suite passes 208/208, including existing export,
  destruction, append-only, replay, and foreign-tenant proofs. Operations
  Notifications passes 104/104, Architecture 112/112, and Integration 65/65.
- GMA and the web application are unchanged.

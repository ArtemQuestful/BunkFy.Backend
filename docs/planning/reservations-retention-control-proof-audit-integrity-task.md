# Reservations Retention Control And Proof Audit Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make the Reservations-owned retention execution, fair-scan checkpoint, and
immutable anonymisation receipt fail closed when direct database writes,
unsafe restores, or future persistence paths attempt to create state the
retention workflow cannot produce.

This slice does not change retention periods, scheduling, eligibility,
candidate batching, reservation mutation, public contracts, or tenant
termination.

## Audit Result

The runtime ownership and retry model are sound. Retention owns generic
schedule reconciliation and contributor dispatch. Reservations starts or
resumes one owner execution transactionally, re-evaluates each candidate under
the reservation operation lock, and atomically writes the anonymised
reservation graph, retention receipt, authority-bound tombstone, affected
count, and PII-free event. A separate Reservations transaction terminalizes
the execution and advances the checkpoint only for non-failed outcomes.

Provider enforcement is incomplete. Direct writes can persist empty
coordinates, uppercase data-class keys, default timestamps, unreachable
checkpoint shapes, duplicate checkpoint-execution markers, malformed SHA-256
values, duplicate receipt events, or receipts without the matching retention
tombstone. Several indexes and foreign keys also rely on provider-generated
names that PostgreSQL truncates.

## Ownership

- Retention owns generic schedules, attempts, deadlines, and contributor
  dispatch. It does not write Reservations persistence.
- Reservations owns candidate selection, policy and hold evaluation, operation
  locking, mutation, execution history, fair-scan state, receipt, tombstone,
  and PostgreSQL schema.
- GMA owns transactions, tenant scoping, locks, outbox delivery, and generic
  runtime primitives. Reservation-specific retention semantics do not belong
  in GMA.

## Invariants

1. Execution, checkpoint, receipt, property, reservation, event, and
   referenced execution identifiers are non-empty; persisted tenant scope is
   nonblank.
2. Data-class keys use the domain's normalized lowercase ASCII key grammar.
   Outcome codes retain the domain's bounded ASCII code grammar.
3. Executions have a non-default start, a later deadline, nonnegative counts,
   and only reachable running or terminal field shapes. A terminal execution
   has version at least two.
4. A new checkpoint has cursor zero, no last execution, and version one. An
   advanced checkpoint has a non-empty last execution and version at least
   two. A legacy-retry-prepared checkpoint has no last execution and version
   at least three; its cursor may be the failed execution's nonzero start.
5. Checkpoint timestamps are non-default. One execution can advance at most
   one checkpoint in a tenant, while one tenant/data-class/policy coordinate
   still has exactly one checkpoint.
6. Receipt attribution is exactly `system:retention`; terminal, deadline, and
   completion times follow the domain chronology; both SHA-256 fields are
   lowercase hexadecimal; and one event appears in at most one retention
   receipt per tenant.
7. Every receipt references its same-tenant execution, reservation, and
   anonymisation tombstone. One reservation can have only one retention
   receipt.
8. Only execution retains `(ScopeId, Id)` as an alternate key because the
   receipt foreign key depends on it. Unused receipt and checkpoint alternate
   keys are removed.

## Delivery

1. Add named coordinate, normalized-key, timestamp, version, lifecycle,
   digest, and actor checks to the three Reservations EF configurations.
2. Add stable history, data-class, last-execution, reservation, execution, and
   event index names.
3. Add stable same-tenant execution, reservation, and tombstone foreign-key
   names and the missing receipt-to-tombstone relationship.
4. Extend focused EF metadata proof for the complete retention owner graph and
   all reachable checkpoint shapes.
5. Generate and review one additive Reservations PostgreSQL migration.
6. Add one PostgreSQL 16 scenario preserving running, terminal, initial,
   advanced, and retry-prepared evidence; rejecting representative malformed
   rows by exact provider name; and proving downgrade plus re-upgrade.
7. Run focused checks while editing, then one provider scenario and one
   consolidated backend gate at the finished slice boundary.

## Deployment Safety

The migration does not rewrite or discard retention evidence. New checks,
uniqueness, and foreign keys validate existing rows and intentionally stop a
deployment when legacy state is malformed, duplicated, or orphaned. A hosted
release needs an exact-release preflight and an operator-approved repair for
any finding; disposable PostgreSQL evidence does not establish production data
quality.

## Deferred

- A checkpoint-to-last-execution foreign key is intentionally absent. The
  persisted tenant-destruction protocol removes retention executions before
  sweep checkpoints, and `LastExecutionId` is an idempotency marker rather
  than the checkpoint's lifecycle owner.
- A checkpoint may continue to name the previous successful execution while a
  later execution fails. The unique filtered marker prevents duplicate
  advancement without pretending the marker owns the current attempt.
- The tombstone has polymorphic Data Rights or Retention ownership. The direct
  receipt-to-tombstone relationship proves the same terminal reservation
  exists; exact authority, owner-receipt id, canonical digest, versions, and
  completion-time matching remain in domain replay checks rather than a
  cross-table trigger system.
- Canonical hash recomputation remains a domain responsibility. PostgreSQL
  enforces bounded lowercase representation and immutable storage.
- Hosted migration, production repair, and deployed execution proof remain
  release-admission work. Retention, GMA, and the web application remain
  unchanged.

## Completion Criteria

- valid running, terminal, initial, advanced, and retry-prepared shapes upgrade
  unchanged;
- malformed control or proof state fails at PostgreSQL with stable names;
- focused domain, model, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the finished slice boundary; and
- no Retention, GMA, or web change is required.

## Verification

- Focused Reservations retention domain and EF model proof: 14/14 passed.
- PostgreSQL 16 migration scenario: 1/1 passed in 11 seconds, including
  upgrade, initial/advanced/legacy-retry state preservation, malformed-row
  rejection by exact provider artifact, downgrade, and re-upgrade.
- `eng/update-solutions.ps1`: the backend solution is synchronized.
- `eng/verify.ps1 -SkipRestore`: source/package checks passed; build completed
  with zero warnings and zero errors; all 21 migration contexts had no drift;
  5,715 non-Docker tests passed. This includes Reservations 377/377,
  Operations Notifications 104/104, Architecture 112/112, and Integration
  65/65.
- `git diff --check`: passed; existing line-ending normalization notices are
  informational and do not report whitespace errors.

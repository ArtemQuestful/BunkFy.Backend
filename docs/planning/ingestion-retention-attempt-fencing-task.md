# Ingestion Retention Attempt Fencing Task

Status: complete
Date: 2026-08-22

## Goal

Make both Ingestion-owned automatic-retention contributors safe when a newer
Retention task attempt supersedes a still-running worker. Fence raw-source
evidence purge and sensitive-history redaction before owner discovery,
source-graph locking, mutation, affected-count recording, and terminalization,
without moving Ingestion policy or storage semantics into Retention or GMA.

This slice does not change retention periods, legal-hold behavior, candidate
eligibility, manual TaskRuntime jobs, public HTTP contracts, or object-store
deletion semantics.

## Audit Result

The generic Retention request already carries a monotonic attempt and the
Ingestion execution row persists it. The coordinate is not currently carried
through owner mutation or completion commands, however, and attempt advance
does not share a transaction lock with those commands. A stale worker can
therefore claim raw evidence, complete a purge, redact normalized history,
increment the affected count, or complete the execution after a newer attempt
has started.

The existing owner state remains suitable:

- raw payload purge is a recoverable `Available` / `Purging` / `Purged`
  two-phase flow with a stable execution claim id;
- sensitive-history redaction is a set-based idempotent mutation; and
- an exception leaves the execution `Running`, so a higher attempt can resume
  accumulated work without inventing a second failure ledger.

## Ownership

- Retention owns schedules, attempts, deadlines, leases, and contributor
  dispatch. It does not read or write Ingestion persistence.
- Ingestion owns its execution row, exact-attempt admission, candidate
  discovery, source-graph ordering, raw-object lifecycle, redaction, affected
  count, and terminal result.
- GMA owns generic transactions, tenant scoping, task leases, and the
  provider-neutral transaction-key lock primitive. No Ingestion-specific
  change belongs in GMA.

## Invariants

1. Begin, raw claim, raw completion, sensitive redaction, and execution
   completion serialize on one tenant-qualified execution lock whose key is
   stable across attempts.
2. The execution lock is always acquired before candidate discovery or any
   source-graph lock. A stale attempt cannot inspect owner candidates or begin
   owner locking.
3. Automatic-retention mutation commands carry both execution id and attempt;
   both coordinates are absent for a legacy/manual TaskRuntime operation or
   both are valid and present.
4. Only the exact current `Running` attempt and expected data class may perform
   owner work, record affected rows, or complete the execution.
5. A retry may advance only to a higher attempt, with a start time not earlier
   than the current attempt and a deadline after that start. It preserves the
   cumulative affected count.
6. If an older worker deleted a raw object after a valid claim but is fenced
   before database completion, the receipt remains `Purging`. The newer
   attempt reuses the stable execution claim, treats the missing object as an
   idempotent delete, and completes the receipt. External deletion is never
   falsely rolled back.
7. Terminal replay is valid only for the exact attempt and exact result.

## Delivery

1. Add a stable retention-execution lock operation to the existing Ingestion
   execution-lock port and PostgreSQL implementation.
2. Add an application coordinator that acquires that lock, reloads the scoped
   execution, and validates attempt and data-class ownership.
3. Carry execution id and attempt through raw claim, raw completion,
   sensitive redaction, and terminal completion while preserving coordinate-
   free manual TaskRuntime callers.
4. Reorder raw and sensitive handlers so attempt admission precedes discovery
   and source-graph locking.
5. Fence retry start-time regression and terminal completion in the aggregate.
6. Extend focused domain, contributor, handler-ordering, lock, catalogue, and
   PostgreSQL serialization proof.
7. Run cheap focused checks during implementation, then one Docker provider
   scenario and one consolidated backend gate at the completed slice boundary.

## Deployment Safety

No schema change is expected: the execution already stores attempt and
optimistic version state. The new lock key is transaction-scoped and
tenant-qualified. It serializes only operations for the same retention
execution, so unrelated tenants, executions, connections, and source graphs
remain concurrent.

## Deferred

- Manual TaskRuntime purge and redaction jobs remain coordinate-free and rely
  on their existing task-attempt lock and owner idempotency.
- There is no generic framework abstraction for owner-specific mutation
  admission. Generalization requires another independent module to prove the
  same policy-free contract.
- Hosted concurrency proof and production object-store recovery remain release
  admission work; disposable PostgreSQL proof is not production evidence.

## Completion Criteria

- stale attempts fail before owner discovery or source locking;
- begin and all automatic-retention owner mutations serialize across attempts;
- a superseded raw deletion remains recoverable by the current attempt;
- legacy/manual jobs continue to operate without an owner execution coordinate;
- focused proof, one PostgreSQL lock scenario, and one consolidated backend
  gate pass; and
- no Retention, GMA, web, or database-migration change is required.

## Verification

- Focused retention, execution-lock, and personal-data catalogue proof passed
  30/30 tests.
- The full Ingestion module suite passed 352/352 tests.
- The PostgreSQL execution-lock provider scenario passed 1/1 test in 11
  seconds, proving same-execution serialization while unrelated executions
  remain concurrent.
- The consolidated backend gate synchronized the solution, passed source-
  package checks, built with zero warnings and zero errors, found every
  migration context drift-free, and passed every configured test assembly.
  Its final PostgreSQL integration assembly passed 65/65 tests in 44 seconds.
- `git diff --check` passed; only Git's existing CRLF-to-LF working-copy
  notices were emitted.

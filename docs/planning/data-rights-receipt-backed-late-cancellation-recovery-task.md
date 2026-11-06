# Data Rights Receipt-Backed Late-Cancellation Recovery Task

Status: completed
Date: 2026-08-22

## Goal

Finish the Data Rights cancellation audit at extension points where an external
durable effect can complete before central transactional state is committed:
processing-ledger append, tenant-termination replay-journal append, and expired
export-object deletion.

## Audit Finding

These effects intentionally precede their database completion command. The
ordering prevents central state from claiming completion before the protected
ledger delta is durable or the expired object is gone, but it creates an
expected crash and late-cancellation window.

The existing Data Rights protocols already make that window recoverable:

- ledger identity, sequence, digest, replay envelope, and append receipt are
  deterministic for a work item; the external store re-acknowledges the exact
  delta and rejects a conflicting one;
- tenant-termination intent, dispatch, and result journal entries have stable
  logical identities and are re-acknowledged without invoking an owner twice;
- a tenant-scoped processing-ledger lock serializes sequence construction, and
  the same durable Task Runtime run retries finalization until the database
  ledger, work item, and outbox commit together;
- export deletion is idempotent, its deletion state is bound to the durable
  task run, and a retry reissues deletion before completing central state; and
- GMA command dispatch and unit-of-work fencing prevent a cancelled attempt
  from committing the database completion.

The missing behavior is immediate local observation after a provider ignores
its token and returns normally. Continuing to stage database changes or invoke
the completion command is unnecessary once cancellation is known.

## Ownership Boundary

- Data Rights owns deterministic ledger and replay-journal construction,
  append receipts, export deletion state, and the retry semantics of these
  product workflows.
- Storage adapters own durable, idempotent append/delete behavior under their
  Data Rights ports.
- GMA owns generic command transaction fencing and durable task lease, retry,
  timeout, and cancellation mechanics.
- No BunkFy receipt, ledger, object, or case semantics belong in GMA.

## Invariants

1. A cancellation-ignoring ledger store cannot cause database ledger, work
   item, or outbox mutation to continue after its append returns.
2. The durable append remains recoverable: retry reconstructs the exact same
   delta, receives the matching receipt, and commits central completion once.
3. Tenant-termination intent, owner dispatch, and owner result do not continue
   unless the returned receipt matches the exact durable journal entry.
4. A cancellation-ignoring replay store cannot advance process or owner state
   after its append returns; retry resumes from the protected entry.
5. A cancellation-ignoring object store cannot dispatch deletion completion
   after its delete call returns.
6. Retrying the same cleanup run may delete the already-missing object and then
   commit the `Deleted` state exactly once.
7. Cancellation never changes external-before-central ordering, suppresses
   GMA's transaction fence, or introduces a distributed transaction.

## Delivery

1. Add an immediate cancellation checkpoint after protected ledger append.
2. Validate and fence tenant-termination intent, dispatch, and result receipts.
3. Add immediate cancellation checkpoints after all expired-object deletions.
4. Add focused cancellation-ignoring provider tests proving no central
   continuation on the cancelled attempt and exact successful retry.
5. Run the complete Data Rights assembly and one consolidated non-Docker gate
   at the completed slice boundary.

## Verification

- focused late-cancellation and retry regressions;
- complete Data Rights test assembly;
- consolidated serial repository verification; and
- no Docker/provider gate because no provider implementation, schema, public
  contract, broker topology, or GMA code changes.

## Completion Evidence

- The widened focused set passed `30/30`, covering protected-ledger retry,
  tenant-termination intent/result replay, invalid durability receipts, and
  ordinary plus tenant-termination export cleanup.
- The complete Data Rights assembly passed `605/605`.
- `pwsh eng/verify.ps1 -SkipRestore` passed solution synchronization,
  source-package ownership, and the serial solution build with `0` warnings and
  `0` errors.
- Every configured PostgreSQL and SQL Server migration model is drift-free.
- The complete non-Docker matrix passed, including GMA Framework `1147/1147`,
  Architecture `112/112`, Host Migrations `26/26`, Service Defaults `64/64`,
  and Integration `65/65`.
- No Docker/provider, hosted CI, schema, public contract, broker topology, or
  GMA change was required for this BunkFy-owned recovery slice.

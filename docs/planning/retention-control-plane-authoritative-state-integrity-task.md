# Retention Control Plane Authoritative State Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make Retention's execution, schedule-health, and durable operator-retry records
fail closed when direct database access, a defective contributor, an unsafe
restore, or a future persistence path attempts to store workflow state the
module cannot safely interpret.

This slice strengthens the existing control plane. It does not change retention
periods, legal-hold policy, contributor ownership, Task Runtime semantics,
tenant termination, or owner-module deletion behavior.

## Audit Finding

The Retention domain already validates normal execution and retry transitions,
and PostgreSQL already has useful version, target, time, and partial lifecycle
checks. The current relational contract still permits:

- empty execution, run, property, and tenant coordinates;
- malformed owner, data-class, outcome, and failure codes that normal domain
  creation would reject or normalize;
- terminal executions whose completion predates their start, or successful and
  blocked executions that complete after their deadline;
- running schedule rows with terminal evidence and terminal schedule rows with
  no completion evidence;
- completed or blocked schedule rows retaining failure counters, failed rows
  with no failure count, and hold-review evidence on the wrong state; and
- property schedule target keys that have the right length but do not identify
  the stored property.

Those shapes make health, retry, and incident decisions ambiguous. They also
allow a restore or direct write to bypass invariants already assumed by the
application layer.

## Ownership

- Retention owns central execution attempts, schedule-health state, and durable
  operator retry requests.
- Owner modules own the records being retained and their immutable terminal
  contribution evidence.
- Task Runtime owns task runs, leases, retry budgets, and lease generation;
  Retention stores only the pinned coordinates needed to invoke and reconcile
  an owner contribution.
- Retention's EF model owns its durable relational contract; the BunkFy
  PostgreSQL migration project owns the concrete migration.
- GMA remains unchanged. Scoped persistence, transaction-key locking, CQRS,
  Task Runtime, and messaging already provide the generic primitives.

## Invariants

### Executions

1. Execution, tenant, and optional property coordinates are non-empty, target
   kind and property presence agree, and owner/data-class keys remain bounded
   lower-case ASCII identifiers.
2. Policy version, attempt, and aggregate version are positive; deadline is
   strictly after start.
3. Running rows carry no completion, count, outcome, or hold-review evidence.
4. Terminal rows carry non-negative counts, affected does not exceed scanned,
   completion is not before start, and outcome code is a bounded ASCII code.
5. Completed and blocked executions finish by their deadline; failed execution
   may finish after its deadline so timeout evidence can still be recorded.
6. Only blocked executions carry hold-review evidence, and terminal transitions
   cannot overflow the aggregate version.

### Schedule State

1. Tenant, owner, data-class, last-execution, and optional property coordinates
   are valid; the target key is exactly `tenant` or the stored property's
   lower-case `N` GUID representation.
2. Policy and schedule versions are positive, failure count is non-negative,
   and next due is after the latest start.
3. Running rows carry no terminal result evidence.
4. Completed and blocked rows carry a complete result, have zero consecutive
   failures, and only blocked rows carry hold-review evidence.
5. Failed rows carry a complete result, have at least one consecutive failure,
   and carry no hold-review evidence.
6. Result counts and outcome-code shape match execution evidence.

### Durable Retry Requests

1. Request, task run, tenant, and optional property coordinates are non-empty;
   owner/data-class keys and target shape match execution coordinates.
2. Policy, evidence, attempt, and aggregate versions are positive, and terminal
   records have advanced beyond their initial version.
3. Scheduling and completion never predate the active request timestamp.
4. Pending and applied requests carry no failure code; failed requests carry a
   bounded normalized lower-case ASCII failure code.
5. Retry and terminal transitions cannot overflow the aggregate version or
   attempt counter.

## Migration Compatibility

- The migration replaces existing checks with stricter model-owned definitions;
  it adds no columns and rewrites no Retention data.
- Valid running, completed, blocked, failed, retried, tenant-targeted, and
  property-targeted records must upgrade unchanged.
- Any malformed hosted row stops the migration at a stable named constraint and
  requires an operator-approved repair. Disposable PostgreSQL proof cannot
  establish that hosted data is clean.
- Downgrade restores the exact previous weaker checks; no destructive rollback
  transformation is required.

## Delivery

1. Align domain outcome validation and overflow guards with the public
   contributor contract.
2. Declare complete named relational constraints for executions, schedule
   state, and durable retry requests.
3. Add focused domain and model-metadata proofs.
4. Generate and review the Retention PostgreSQL migration.
5. Add one PostgreSQL 16 upgrade scenario that preserves representative valid
   states and rejects malformed writes by exact constraint name.
6. Align the Retention development note and close with focused checks followed
   by one coherent end-of-slice gate.

## Deferred

- Retention execution-history cleanup is a separate Retention slice because it
  changes lifecycle and scheduling behavior rather than row validity.
- Rebuildable tenant/property projection hardening remains a separate audit;
  those projections are not authoritative Retention history.
- Tenant-termination operation, receipt, and revision integrity remains owned by
  the completed termination slice and is not changed here.
- Hosted data preflight, migration application, rollback rehearsal, and
  same-release deployment evidence remain deployment admission work.

## Completion Criteria

- representative valid execution, schedule, and retry states upgrade unchanged;
- malformed coordinates, keys, lifecycle shapes, counts, outcome/failure codes,
  timestamps, and target keys fail at PostgreSQL with stable names;
- focused Retention, architecture, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the completed slice boundary; and
- no GMA repository change is required.

## Verification Evidence

- Official migration generation through `eng/add-migration.ps1` completed with
  zero warnings and zero errors.
- The complete Retention unit and model suite passed: 78/78.
- `Integration.Tests` built with zero warnings and zero errors.
- The focused PostgreSQL 16 migration scenario passed: 1/1 in 8 seconds,
  including valid-state preservation, exact named-constraint rejection, and
  downgrade to the previous migration.
- `eng/verify.ps1 -SkipRestore` passed at the completed slice boundary: the
  solution graph was synchronized, source-package checks passed, the serial
  solution build completed with zero warnings and zero errors, every configured
  migration-drift check passed, and all 5,666 eligible non-Docker tests passed.
- `git diff --check` passed. GMA remained pinned and unchanged.

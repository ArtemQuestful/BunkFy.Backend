# Staff Retention Control And Proof Audit Integrity Task

Status: complete
Date: 2026-08-22

## Goal

Make the Staff-owned automatic-retention workflow safe under task retries,
stale workers, direct database writes, unsafe restores, and future persistence
paths while preserving Staff's ownership of employee data and irreversible
anonymisation proof.

This slice does not change retention periods, eligibility, country-policy
resolution, Workspaces prerequisites, public contracts, candidate batching, or
tenant termination.

## Audit Result

The module boundary is correct. Retention owns generic schedule reconciliation,
lease attempts, deadlines, and contributor dispatch. Staff owns candidate
selection, prerequisite verification, operation locking, mutation, execution
history, fair-scan state, receipt, tombstone, and PostgreSQL schema. GMA's
generic task and transaction primitives are sufficient.

The audit found two Staff-owned integrity gaps:

- the active Retention attempt is not carried into Staff mutation or
  completion, so a late worker can mutate or terminalize a newer attempt; and
- a failed completion advances the fair-scan checkpoint. The next task attempt
  therefore cannot resume the failed page with exact accumulated affected
  count semantics, despite the original delivery contract claiming that
  behavior.

Provider enforcement was also incomplete. Direct writes could persist empty
coordinates, uppercase data-class keys, default timestamps, unreachable
checkpoint shapes, duplicate checkpoint-execution markers, malformed SHA-256
values, duplicate receipt events, or receipts without their matching
retention-authority tombstone. Several indexes and foreign keys rely on
provider-generated names that PostgreSQL truncates.

## Ownership

- Retention owns generic schedules, attempts, deadlines, and contributor
  dispatch. It does not read or write Staff persistence.
- Staff owns attempt fencing at its boundary, candidate and prerequisite
  evaluation, operation locking, irreversible mutation, execution history,
  fair-scan state, receipt, tombstone, and provider schema.
- Workspaces continues to implement the narrow prerequisite declared by
  `Staff.Contracts`; this slice does not widen that contract.
- GMA owns generic transactions, tenant scoping, task leases, outbox delivery,
  and runtime primitives. Staff-specific retry and proof semantics do not
  belong in GMA.

## Runtime Invariants

1. The Retention request attempt is carried through every Staff mutation and
   completion command. Only the currently running execution with the exact
   attempt may mutate or terminalize.
2. `Failed` is terminal evidence for one attempt. Replaying that exact attempt
   returns its persisted result; a higher attempt may reopen it only with a
   start at or after the failed completion and a later deadline.
3. A retry preserves cumulative affected count, clears only current-attempt
   terminal fields, and resumes from the failed execution's starting cursor.
4. A newly failed completion does not advance the fair-scan checkpoint.
   Compatibility recovery rewinds a legacy checkpoint that was advanced by a
   failed execution, but only when execution, tenant, data class, policy,
   cursor, and timestamps prove the relationship exactly.
5. An old worker cannot mutate, add proof, increment affected count, complete,
   or advance the checkpoint after a newer attempt has started.

## Persistence Invariants

1. Execution, checkpoint, receipt, Staff member, event, and referenced
   execution identifiers are non-empty; persisted tenant scope is nonblank.
2. Data-class keys use the domain's normalized lowercase ASCII key grammar.
   Outcome codes retain the bounded ASCII code grammar.
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
6. Receipt attribution is exactly `system:retention`; departed, deadline, and
   completion times follow domain chronology; both SHA-256 fields are
   lowercase hexadecimal; and one event appears in at most one retention
   receipt per tenant.
7. Every receipt references its same-tenant execution, Staff member, and
   anonymisation tombstone. One Staff member can have only one retention
   receipt.
8. Only execution retains `(ScopeId, Id)` as an alternate key because the
   receipt foreign key depends on it. Unused receipt and checkpoint alternate
   keys are removed.

## Delivery

1. Carry attempt through Staff contributor mutation and completion commands;
   reject stale attempts before candidate discovery or operation locking.
2. Add failed-attempt reopening, exact terminal replay, failed-completion
   checkpoint behavior, and proven legacy checkpoint rewind.
3. Add focused domain and command-handler proof for failed retry, stale
   completion, stale mutation, and legacy rewind.
4. Add named coordinate, normalized-key, timestamp, version, lifecycle,
   digest, and actor checks to the three Staff EF configurations.
5. Add stable history, data-class, last-execution, Staff-member, execution,
   and event index names.
6. Add stable same-tenant execution and Staff-member EF relationships plus a
   provider-owned receipt-to-tombstone foreign key with a stable name.
7. Extend focused EF metadata proof for the complete retention owner graph and
   all reachable checkpoint shapes.
8. Generate and review one additive Staff PostgreSQL migration.
9. Add one PostgreSQL 16 scenario preserving running, terminal, initial,
   advanced, and retry-prepared evidence; rejecting representative malformed
   rows by exact provider name; and proving downgrade plus re-upgrade.
10. Run focused checks while editing, then one provider scenario and one
    consolidated backend gate at the finished slice boundary.

## Deployment Safety

The migration must not rewrite or discard retention evidence. New checks,
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
- The operation lock is mutable serialization state. A receipt records the
  selected and resulting revisions as immutable evidence, but does not own the
  current lock row and therefore does not foreign-key to it.
- The tombstone foreign key is intentionally PostgreSQL-migration owned rather
  than represented as a second required EF relationship over the receipt's
  existing `(ScopeId, StaffMemberId)` columns. Two required EF relationships
  sharing those columns cause change tracking to preempt Staff's append-only
  tombstone guard. PostgreSQL still rejects orphan proof, and the persisted
  tenant-destruction order removes receipts before tombstones.
- The tombstone has polymorphic Data Rights or Retention ownership. The direct
  receipt-to-tombstone relationship proves the same terminal Staff member
  exists; exact authority, receipt id, canonical digest, versions, and
  completion-time matching remain in domain replay checks rather than a
  cross-table trigger system.
- Canonical hash recomputation remains a domain responsibility. PostgreSQL
  enforces bounded lowercase representation and immutable storage.
- Hosted migration, production repair, and deployed execution proof remain
  release-admission work. Retention, GMA, Workspaces, and the web application
  remain unchanged.

## Completion Criteria

- failed executions retry from the exact failed cursor and stale attempts fail
  before mutation or terminalization;
- valid running, terminal, initial, advanced, and retry-prepared shapes upgrade
  unchanged;
- malformed control or proof state fails at PostgreSQL with stable names;
- focused domain, handler, model, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the finished slice boundary; and
- no GMA, Retention, Workspaces, or web change is required.

## Verification

- `dotnet test src/Modules/Staff/tests/BunkFy.Modules.Staff.Tests/BunkFy.Modules.Staff.Tests.csproj --no-restore --filter "FullyQualifiedName~StaffRetention" --verbosity minimal`: 34 passed.
- `dotnet test src/Modules/Staff/tests/BunkFy.Modules.Staff.Tests/BunkFy.Modules.Staff.Tests.csproj --no-restore --verbosity minimal`: 284 passed.
- `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-restore --filter "FullyQualifiedName=Integration.Tests.StaffRetentionControlProofAuditIntegrityMigrationIntegrationTests.Migration_preserves_reachable_control_and_proof_states_and_rejects_malformed_rows" --verbosity minimal`: 1 PostgreSQL 16 scenario passed in 9 seconds.
- `pwsh -NoLogo -NoProfile -File eng/update-solutions.ps1`: solution synchronized.
- `pwsh -NoLogo -NoProfile -File eng/verify.ps1 -SkipRestore`: source-package checks passed; build passed with zero warnings and zero errors; every PostgreSQL and SQL Server context was migration-drift free; 5,718 non-Docker tests passed.
- `git diff --check`: passed; Git reported only expected line-ending normalization notices for existing CRLF files.

The first consolidated run exposed that modeling the receipt-to-tombstone
relationship as a second required EF relationship preempted Staff's
append-only deletion guard. The relationship was moved to the PostgreSQL
migration, the full Staff suite then passed, and the final consolidated run
proved the corrected design.

# Guests Retention Control And Proof Audit Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make the Guests-owned retention execution, fair-scan checkpoint, and immutable
anonymisation receipt fail closed when direct database writes, unsafe restores,
or future persistence paths attempt to create state the retention workflow
cannot produce.

This slice does not change country policy, retention periods, scheduling,
eligibility, mutation batching, public contracts, or tenant termination.

## Audit Result

The runtime ownership and transaction boundaries are sound. Retention owns the
generic schedule and dispatch contract. Guests starts or resumes its execution
in one transaction, re-evaluates each candidate under the Guest operation
lock, and atomically writes the scrubbed profile, retention receipt, tombstone,
affected count, and PII-free event. Execution completion and cursor advancement
share a second Guests transaction. A cancelled batch remains `Running`; retry
rescans from the durable cursor while exact receipt/tombstone replay prevents a
second mutation.

A later cross-owner parity audit found that this persistence-focused slice did
not fence mutation and completion to the active task attempt or preserve the
failed page cursor. That runtime correction is tracked by
[Guests retention attempt fencing and failed retry](guests-retention-attempt-fencing-and-failed-retry-task.md).

Provider enforcement was incomplete. Empty coordinates, malformed normalized
keys, default timestamps, non-system receipt attribution, duplicate event or
checkpoint-execution evidence, and unreachable initial/advanced checkpoint
shapes could be written directly. Receipt foreign keys and indexes also had
provider-generated truncated names. The original retention migration used
ASCII outcome and lowercase SHA-256 regex checks, while later generated model
metadata recorded weaker trim or length checks.

## Ownership

- Retention owns generic schedules, attempts, deadlines, and contributor
  dispatch. It does not write Guests persistence.
- Guests owns execution history, fair-scan state, candidate revalidation,
  terminal profile mutation, receipt, tombstone, and PostgreSQL schema.
- GMA owns transactions, tenant scoping, locks, outbox delivery, and generic
  runtime primitives. BunkFy retention semantics do not belong in GMA.

## Invariants

1. Execution, checkpoint, receipt, Guest, event, and referenced execution
   identifiers are non-empty; persisted tenant scope is nonblank.
2. Data-class keys use the domain's normalized lowercase ASCII key grammar.
   Outcome codes use the domain's bounded ASCII code grammar.
3. Running executions require the current policy family and have no terminal
   result fields. Historical terminal policy-v1 executions remain readable.
4. Start, deadline, completion, retention-deadline, and checkpoint timestamps
   cannot use the domain's rejected default value; existing chronology and
   count rules remain enforced.
5. A new checkpoint has cursor zero, no last execution, and version one. An
   advanced checkpoint has one non-empty last execution and version at least
   two. One execution can advance at most one checkpoint in a tenant.
6. Receipt attribution is exactly `system:retention`, both SHA-256 fields are
   lowercase hexadecimal, and one event appears in at most one retention
   receipt per tenant.
7. Every receipt references its same-tenant execution, Guest profile, and
   anonymisation tombstone. One Guest can have only one retention receipt.
8. Only execution retains `(ScopeId, Id)` as an alternate key because receipt
   foreign keys depend on it. Unused receipt and checkpoint alternate keys are
   removed.

## Delivery

1. Align the receipt domain actor invariant with its automatic-retention
   meaning and add focused domain proof.
2. Add named coordinate, key, timestamp, lifecycle, actor, and digest checks
   to the three Guests EF configurations.
3. Add stable history, event, execution, Guest, and checkpoint index names;
   add stable execution/profile/tombstone foreign-key names.
4. Extend focused model metadata proof for the complete retention owner graph.
5. Generate and review one additive Guests PostgreSQL migration.
6. Add one PostgreSQL 16 scenario preserving running and historical/current
   terminal evidence, rejecting representative malformed rows by exact name,
   and proving downgrade plus re-upgrade.
7. Run focused checks while editing, then one provider scenario and one
   consolidated backend gate at the finished slice boundary.

## Deployment Safety

The migration does not rewrite or discard retention evidence. New checks,
uniqueness, and foreign keys validate existing rows and intentionally stop a
deployment when legacy state is malformed or orphaned. A hosted release needs
an exact-release preflight and an operator-approved repair for any finding;
disposable PostgreSQL evidence does not establish production data quality.

## Deferred

- A checkpoint-to-last-execution foreign key is intentionally absent. The
  persisted tenant-destruction protocol removes retention executions before
  sweep checkpoints, and `LastExecutionId` is an idempotency marker rather
  than the checkpoint's lifecycle owner. Changing that durable stage contract
  is not justified by this integrity slice.
- The tombstone has polymorphic Data Rights or Retention ownership. The direct
  receipt-to-tombstone relationship proves the same terminal Guest exists;
  exact canonical-digest and completion-time matching remains in the domain
  replay check rather than a second cross-table trigger system.
- Canonical hash recomputation remains a domain responsibility. PostgreSQL
  enforces bounded lowercase representation and immutable storage.
- Reservations and Staff retention ledgers require their own owner audits;
  this slice does not copy Guests-specific rules across modules.
- Hosted migration, production repair, and deployed execution proof remain
  release-admission work. GMA and the web application remain unchanged.

## Completion Criteria

- valid running, historical terminal, current terminal, initial checkpoint,
  advanced checkpoint, and v1/v2 receipt shapes upgrade unchanged;
- malformed control or proof state fails at PostgreSQL with stable names;
- focused domain, model, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the finished slice boundary; and
- no Retention, GMA, or web change is required.

## Verification

- Focused Guests retention domain and EF model proof: 11/11 passed.
- PostgreSQL 16 migration scenario: 1/1 passed in 12 seconds, including
  upgrade, malformed-row rejection by exact constraint name, downgrade, and
  re-upgrade.
- `eng/update-solutions.ps1`: the backend solution is synchronized.
- `eng/verify.ps1 -SkipRestore`: source/package checks passed; build completed
  with zero warnings and zero errors; all 21 migration contexts had no drift;
  5,715 non-Docker tests passed. This includes Guests 214/214, Operations
  Notifications 104/104, Architecture 112/112, and Integration 65/65.
- `git diff --check`: passed; existing line-ending normalization notices are
  informational and do not report whitespace errors.

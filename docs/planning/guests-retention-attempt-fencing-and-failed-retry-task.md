# Guests Retention Attempt Fencing And Failed Retry Task

Status: complete
Date: 2026-08-22

## Goal

Make the Guests-owned automatic-retention workflow safe when a Retention task
attempt fails, is retried, or continues running after a newer attempt has
started. Preserve exact affected counts, fair-scan position, and immutable
Guest anonymisation proof without moving Guest semantics into Retention or
GMA.

This slice does not change retention periods, eligibility, country-policy or
time-zone evidence, mutation batching, public contracts, tenant termination,
or the generic Retention schedule.

## Audit Result

The module and transaction boundaries are correct, but attempt ownership is
not enforced at every Guest command boundary:

- mutation and completion commands do not carry the active Retention attempt,
  so a stale worker can reach Guest discovery, locking, or terminalization
  after a newer attempt has started;
- a failed completion advances the fair-scan checkpoint even though the
  execution is intended to retry the same page; and
- only a `Running` execution can reopen, so persisted `Failed` evidence cannot
  resume under a higher task attempt with its accumulated affected count.

The earlier control/proof integrity slice correctly hardened provider proof,
but its statement that the runtime retry model was complete did not cover
these attempt and failed-cursor cases.

## Ownership

- Retention owns generic schedules, attempts, deadlines, leases, and
  contributor dispatch. It does not read or write Guests persistence.
- Guests owns attempt fencing at its mutation and completion boundaries,
  candidate revalidation, operation locking, execution history, fair-scan
  state, irreversible mutation, receipt, tombstone, and PostgreSQL schema.
- GMA owns generic transactions, tenant scoping, task leases, outbox delivery,
  and runtime primitives. No Guest-specific retry rule belongs in GMA.

## Invariants

1. The Retention request attempt is carried through every Guest mutation and
   completion command. Only the exact currently running attempt may mutate or
   terminalize.
2. `Failed` is terminal evidence for the current attempt. Exact same-attempt
   replay returns that result; only a higher attempt starting at or after the
   failed completion, with a later deadline, may reopen it and supersede that
   current-row result.
3. Reopening preserves the cumulative affected count and original starting
   cursor, clears only terminal fields, and increments the execution version.
4. A newly failed completion does not advance the checkpoint. The next attempt
   resumes from the persisted execution's starting cursor.
5. Compatibility recovery rewinds a checkpoint advanced by the old failed
   behavior only when execution id, tenant, data class, cursor, completion
   timestamp, and retry timestamp prove the relationship exactly.
6. A retry-prepared checkpoint has no last-execution marker, version at least
   three, and may retain a nonzero cursor. Initial and advanced checkpoint
   shapes remain unchanged.
7. A stale attempt fails before candidate discovery or the Guest operation
   lock and cannot add proof, increment affected count, complete the execution,
   or advance the checkpoint.

## Delivery

1. Add `Attempt` to Guest mutation and completion commands and propagate it
   from the contributor.
2. Fence the attempt before receipt lookup, then require a running current
   coordinate before candidate discovery and operation locking; fence terminal
   replay and completion in the execution aggregate.
3. Add failed-attempt validation and reopening while preserving affected count
   and resetting terminal fields.
4. Leave checkpoints unchanged on new failed completions and add proven legacy
   checkpoint rewind on a higher attempt.
5. Extend the checkpoint lifecycle constraint for the retry-prepared shape and
   generate one additive Guests PostgreSQL migration.
6. Add focused domain, command-handler, contributor, mutation-order, and model
   proof.
7. Add one PostgreSQL 16 upgrade/downgrade scenario that preserves all
   reachable checkpoint shapes and rejects malformed retry-prepared state by
   the stable lifecycle constraint name.
8. Run focused checks while editing, then one provider scenario and one
   consolidated backend gate at the finished slice boundary.

## Deployment Safety

The migration replaces one checkpoint lifecycle check and does not rewrite or
discard data. It accepts the additional state produced by a proven legacy
failed-execution rewind while continuing to reject empty markers, version-two
markerless cursors, and malformed initial or advanced rows. Hosted rollout
still requires exact-release preflight and operator review of any constraint
failure; disposable PostgreSQL proof is not production-data evidence.

## Deferred

- The execution id remains stable across task attempts. The owner stores one
  current execution row and cumulative affected count per generic Retention
  run; a higher attempt supersedes the prior failed operational result. GMA
  task history remains the generic attempt record, while immutable Guest
  anonymisation receipts and tombstones are never rewritten.
- A checkpoint-to-execution foreign key remains absent because
  `LastExecutionId` is an idempotency marker and Guests tenant destruction
  removes execution history before checkpoints.
- This correction does not generalize Guest or Staff semantics into GMA. A
  reusable abstraction would need multiple independent product owners to prove
  the same policy-free contract first.
- Hosted migration, production repair, and deployed retry proof remain
  release-admission work. Retention, GMA, Workspaces, and the web application
  remain unchanged.

## Completion Criteria

- failed executions retry from their exact starting cursor with accumulated
  affected count;
- stale mutation and completion attempts fail before owner work;
- new failure leaves the checkpoint untouched and proven legacy failure can be
  rewound;
- initial, advanced, and retry-prepared checkpoint states upgrade unchanged;
- focused model/runtime proof, one PostgreSQL scenario, and one consolidated
  backend gate pass; and
- no Retention, GMA, Workspaces, or web change is required.

## Verification

- Focused Guests retention command, contributor, domain, model, and catalogue
  proof: 47/47 passed.
- Full Guests module: 217/217 passed, including the personal-data catalogue and
  deterministic inventory guard for the new attempt coordinates.
- PostgreSQL 16 migration scenario: 1/1 passed in 9 seconds, including upgrade,
  reachable-state preservation, malformed retry-state rejection by exact
  constraint name, downgrade, old-schema rejection, and re-upgrade.
- `eng/update-solutions.ps1`: the backend solution is synchronized.
- `eng/verify.ps1 -SkipRestore`: source/package checks passed; build completed
  with zero warnings and zero errors; all 21 migration contexts had no drift;
  5,721 non-Docker tests passed. This includes Guests 217/217, Reservations
  377/377, Staff 284/284, Operations Notifications 104/104, Architecture
  112/112, and Integration 65/65.
- `git diff --check`: passed.

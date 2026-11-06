# Inventory Retirement Cancellation Recovery Task

Status: complete
Date: 2026-08-11

## 2026-09-05 additive context-read amendment

The historical completion below covers cancellation commands, not fresh-device
process discovery. The [context-read task](inventory-retirement-context-read-task.md)
adds exact-target active-first/latest retained attempt GETs and the shared
in-place room/bed operator flow. Existing request/retry/cancel operation
identities, expected versions, active partial uniqueness and by-ID history
remain unchanged. Local/CI/preview/hosted evidence for this addition is pending.

## Goal

Let authorized staff withdraw a room or bed retirement while it is still
draining, restore sellability safely, and preserve a durable audit/replay
history. Cancellation must lose deterministically once terminal Properties
finalization has been requested.

## Audit Findings

- `Draining` is the only reversible state. Entering `FinalizationRequested`
  raises a domain event whose integration event is committed to the Inventory
  outbox, so the terminal Properties request may already be in flight.
- Claim and block release handlers can automatically advance a draining process.
  A cancel command that only checks state would race that advancement and could
  reopen inventory after terminal finalization was committed.
- Existing per-target unique indexes allow only one lifetime retirement process.
  Deleting a canceled process would break exact replay because committed request
  operations point to that process. Reusing it would make old operations observe
  a later, unrelated retirement attempt.
- Unit-definition publication reads tracked retirement state. It must exclude a
  locally canceled process before writing the sellable definition to the outbox.
- The existing `inventory.retire` permission describes request, inspection, and
  recovery of retirement processes. No additional permission or GMA primitive
  is needed.

## Ownership

- Inventory owns cancellation policy, state, audit fields, exact replay,
  serialization, active-attempt uniqueness, availability restoration, and
  public/Admin/CLI contracts.
- The web owns explicit operator confirmation, cancellation reason capture, and
  rendering the canceled terminal state.
- PostgreSQL migrations own the project-specific partial unique indexes that
  enforce one active process per room or bed while retaining historical canceled
  attempts.
- GMA remains unchanged. Its transaction-scoped keyed lock, CQRS transaction,
  scoped permission, and operation-journal building blocks are sufficient.

## Invariants

1. Cancellation requires a caller-owned operation id, expected process version,
   explicit confirmation, normalized reason, and actor reference.
2. Only `Draining` can transition to `Canceled`. `FinalizationRequested`,
   `FinalizedAwaitingTopology`, `Rejected`, `Completed`, and `Canceled` reject a
   new cancellation intent.
3. Cancellation and claim-release auto-advance acquire the same retirement lock.
   After waiting, auto-advance reloads the process before checking `Draining`.
4. A cancellation takes the retirement lock before the room lock, matching retry
   lock order. A finalization transition that wins first makes cancellation fail;
   a cancellation that wins first makes auto-advance observe `Canceled`.
5. Canceling increments the process version and records cancellation reason,
   actor, and timestamp in the durable process row.
6. Exact replay returns the same historical process. Reusing an operation id for
   different cancellation coordinates, version, or reason returns conflict.
7. Canceled processes no longer fence availability. Their room definitions are
   republished in the cancellation transaction and downstream consumers receive
   a higher unit version.
8. Canceled attempts remain queryable by process id. Active-target lookups ignore
   them, allowing a later retirement with a new process and operation identity.
9. PostgreSQL permits historical attempts but enforces at most one active state
   (`Draining`, `FinalizationRequested`, `FinalizedAwaitingTopology`, or
   `Rejected`) for a target.
10. Cancellation uses `inventory.retire` across public API, Admin API, and Admin
    CLI. It does not require a new recent-auth challenge because it cannot start
    terminal topology work, but every caller must confirm intent explicitly.

## Delivery

1. Add the `Canceled` state, cancellation audit fields, domain transition, DTO
   fields, errors, validators, commands, mappings, and operation fingerprints.
2. Extend the Inventory management journal with exact bed/room cancellation
   records and replay pointers.
3. Add cancel handlers that lock retirement then room, enforce version/state,
   restore availability, republish room definitions, and record the operation.
4. Serialize automatic drain advancement on the same retirement lock and reload
   after lock acquisition.
5. Change active-target repositories to exclude historical canceled attempts;
   add PostgreSQL columns and partial active-target uniqueness migrations.
6. Add public, Admin API, and Admin CLI cancellation surfaces under
   `inventory.retire`, including required confirmation and CLI `--yes`.
7. Add the web cancellation confirmation/reason flow only for `Draining`, stop
   polling on `Canceled`, refresh affected inventory, and preserve operation ids
   across transport retries.
8. Update executable personal-data/export metadata for cancellation reason,
   actor, and timestamp.
9. Prove transition, replay, permission, migration/model, republishing, and
   cancel-versus-auto-advance behavior with focused tests, then run one
   consolidated non-Docker backend/web gate and contract drift check.

## Deferred

- Cancellation after Properties finalization has been requested;
- compensating recreation of topology after a completed retirement;
- forced retirement that moves or cancels reservations;
- generic process-cancellation abstractions in GMA before a second product
  demonstrates the same state and recovery contract.

## Completion Criteria

- a confirmed, version-matched `Draining` cancellation restores sellability and
  persists durable audit data;
- every terminal or in-flight finalization state rejects cancellation without
  republishing or journaling a new operation;
- claim release and cancellation have deterministic lock-ordered outcomes;
- exact replay and operation-id conflict behavior are covered;
- a fresh retirement can follow a canceled historical attempt while active
  duplicate attempts remain database-constrained;
- public/Admin/CLI/web surfaces and generated contracts agree;
- personal-data/export metadata and PostgreSQL migrations are current;
- focused and consolidated non-Docker gates pass; and
- GMA submodules remain unchanged.

## Verification

- `eng/verify.ps1 -SkipRestore` passed with a zero-warning solution build,
  migration drift checks, 159 focused Inventory tests, 100 Operations
  Notifications tests, 102 architecture tests, and 60 non-Docker integration
  tests.
- `pnpm verify` passed with type checking, lint, 267 web tests, and the
  production build; `pnpm contracts:check` confirmed the OpenAPI snapshot and
  generated TypeScript contract are current.
- The focused PostgreSQL migration scenario passed and proved historical
  canceled attempts, partial active-attempt uniqueness, ledger constraints,
  and downgrade refusal against PostgreSQL 16.
- The consolidated Docker run passed 113 of 115 scenarios and exposed two
  stale cross-domain test requests. After aligning those requests with the
  existing Ingestion confirmation and Staff account-link contracts, both
  affected scenarios passed together (2 of 2); the other 113 scenarios and
  production behavior were unchanged.
- All GMA submodules remained clean and pinned; this slice required no GMA
  changes.

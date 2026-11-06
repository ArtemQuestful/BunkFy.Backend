# Data Rights Bounded Owner-Call Fencing Task

Status: completed
Date: 2026-08-22

## Goal

Apply one exact execution-window rule to every remaining Data Rights owner call
that already has a domain deadline, so a cancellation-ignoring contributor
cannot return normally and advance case state outside the admitted attempt.

## Audit Finding

Tenant-termination execution now uses a shared Data Rights deadline executor,
but three older bounded workflows still implement linked cancellation locally:

- anonymisation owner and Staff prerequisite calls accept a normal return
  without observing the trusted clock after the await;
- restriction execution has the same gap and accepts a completed owner proof at
  the exact deadline; and
- restriction release-target resolution derives cancellation from an earlier
  timestamp but has no trusted post-return deadline or caller-cancellation
  observation.

Anonymisation proof validation also permits completion exactly at the deadline.
These are BunkFy Data Rights execution semantics, not generic Task Runtime
policy.

## Ownership Boundary

- Data Rights owns its owner-call deadlines, proof timestamps, retry mapping,
  and whether a case may advance.
- Each owner module remains authoritative for its idempotent mutation, local
  state, receipt, and release-target catalogue.
- Task Runtime owns the outer handler timeout, lease, cancellation delivery,
  and retry scheduling.
- GMA remains unchanged. The deadline helper is an internal Data Rights
  application primitive, not a framework contract.

## Invariants

1. A result is accepted only when the trusted post-return observation is
   strictly before the request deadline.
2. Caller cancellation observed after a contributor ignores its token still
   prevents central mutation or result recording.
3. Anonymisation and restriction completion proof timestamps are no later than
   the trusted return observation and strictly before the request deadline.
4. Late anonymisation calls fail the task for safe idempotent retry; late
   restriction calls map to the existing retry-required result.
5. Late restriction-target resolution cannot select, freeze, or return a stale
   target set.
6. Existing owner contracts, task payloads, persistence models, and module
   boundaries do not change.

## Delivery

1. Move the shared deadline executor to the Data Rights application boundary.
2. Route anonymisation owner and prerequisite calls through it and tighten
   owner-proof time validation.
3. Route restriction execution and target resolution through it while
   preserving existing retry and caller-cancellation semantics.
4. Add focused regression tests for normal returns at the exact deadline,
   caller cancellation ignored by an owner, and exact-deadline proofs.
5. Run the complete Data Rights assembly and one consolidated non-Docker gate
   after the slice is coherent.

## Deferred

- Subject discovery, ordinary subject export, final-artifact generation, and
  anonymisation restore calls have no honest domain execution deadline. Their
  cancellation/continuation model needs a separate audit rather than borrowing
  an arbitrary owner timeout.
- Remote adapter isolation and hard process termination remain deployment and
  adapter-runtime concerns; in-process cancellation cannot forcibly stop code
  that never returns.

## Verification

- focused tests cover anonymisation owner and prerequisite late returns;
- focused tests cover restriction execution and target-resolution late returns;
- focused tests preserve external caller cancellation and reject proof at the
  exact deadline;
- the complete Data Rights assembly passes; and
- one consolidated non-Docker repository gate passes at the slice boundary.

Docker/provider and hosted CI checks are deferred because this slice changes no
schema, provider behavior, broker topology, or GMA code.

## Completion Evidence

- Focused deadline, proof-timestamp, target-observation, and ignored-
  cancellation coverage passed `32/32`.
- The complete Data Rights test assembly passed `586/586`.
- `pwsh eng/verify.ps1 -SkipRestore` passed solution synchronization,
  source-package ownership, a serial build with `0` warnings and `0` errors,
  every configured migration drift check, and every non-Docker test project.
- The repository gate included Architecture `112/112`, Host Migrations
  `26/26`, Service Defaults `64/64`, and Integration `65/65`.
- `BunkFy.slnx` changed only to include this task document. GMA source and
  submodule pointers remain unchanged.

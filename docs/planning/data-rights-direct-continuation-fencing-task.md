# Data Rights Direct Continuation Fencing Task

Status: completed
Date: 2026-08-22

## Goal

Stop Data Rights orchestration immediately when caller cancellation is observed
after a direct owner or provider returns normally, without inventing arbitrary
timeouts or accepting work outside the caller's admitted attempt.

## Audit Finding

GMA CQRS now rejects pre-cancelled dispatch and results returned after caller
cancellation, and its command unit of work will not save or commit a cancelled
attempt. Data Rights also invokes product owners and persistence providers
directly inside queries, policies, export assembly, recovery, and startup gates.
Several of those calls can ignore their token, return normally, and allow the
orchestrator to invoke another owner, assemble more plaintext, replay another
delta, or mark startup ready before the outer framework boundary is reached.

These calls have no honest domain deadline. The correction is a cancellation
checkpoint immediately after each normal return and before accepting its result
or beginning the next operation, not a fabricated timeout or abandoned
in-process task.

## Ownership Boundary

- Data Rights owns contributor ordering, result acceptance, protected-export
  cleanup, restore replay, and recovery-readiness semantics.
- Owner modules remain responsible for idempotent owner mutations and replayable
  proof when a cancellation is observed after their work completed.
- GMA owns request dispatch, command transaction fencing, and Task Runtime's
  outer cancellation, lease, retry, and timeout behavior.
- GMA remains unchanged. Cancellation observation at these product extension
  points is BunkFy orchestration policy.

## Invariants

1. A direct owner or provider result is not inspected or accepted after caller
   cancellation is observed.
2. Cancellation between owner calls prevents every later owner call and central
   aggregate mutation in that attempt.
3. Subject discovery, companion expansion, approval evaluation, and export
   assembly do not continue after a cancellation-ignoring contributor returns.
4. Restore reads, prerequisites, owner replay, scope enumeration, and readiness
   checks cannot continue or mark the host ready after observed cancellation.
5. A protected export object written by a cancellation-ignoring storage call is
   deleted through the existing unverified-object cleanup path before
   cancellation escapes.
6. No arbitrary deadline, detached `WaitAsync` work, schema change, public
   contract change, or cross-module data ownership change is introduced.

## Delivery

1. Add post-return checkpoints to subject discovery and selection, required
   companion expansion, approval policies, and guest response-deadline policy.
2. Fence ordinary export contributors and cleanup-safe protected object writes.
3. Fence restore page reads, prerequisites, owner calls, scope enumeration,
   reconciliation, startup readiness, and production-readiness probes.
4. Add focused cancellation-ignoring fakes proving no subsequent owner,
   mutation, object acceptance, checkpoint advance, or readiness transition.
5. Run the complete Data Rights test assembly and one consolidated non-Docker
   repository gate after the slice is coherent.

## Deliberate Exceptions

- The protected ledger delta append already returns durability proof and is
  idempotently replayable. A separate recovery design is required before
  changing how a successful append is reported after late cancellation.
- Expired-object deletion is idempotent and is followed by a GMA-fenced command;
  cancellation leaves the central deletion workflow retryable.
- In-process cancellation cannot terminate code that never returns. Hard
  isolation and process termination belong to adapter/deployment boundaries.

## Verification

- focused tests use collaborators that cancel the supplied token and still
  return a normal value;
- later contributors, mutations, checkpoint commands, and ready-state changes
  are absent after cancellation;
- protected-write cancellation proves cleanup with `CancellationToken.None`;
- the complete Data Rights assembly passes; and
- one consolidated non-Docker repository gate passes at the slice boundary.

Docker/provider and hosted CI checks are deferred because the slice changes no
schema, external provider implementation, broker topology, or GMA code.

## Completion Evidence

- The focused cancellation-ignoring regression set passed `13/13`, including
  two restore stages from one theory.
- The complete Data Rights assembly passed `599/599`.
- `pwsh eng/verify.ps1 -SkipRestore` passed solution synchronization,
  source-package ownership, and the serial solution build with `0` warnings and
  `0` errors.
- Every configured PostgreSQL and SQL Server migration model is drift-free.
- The consolidated non-Docker sweep passed, including GMA Framework `1147/1147`,
  Architecture `112/112`, Host Migrations `26/26`, Service Defaults `64/64`,
  and Integration `65/65`.
- No Docker/provider, hosted CI, schema, public contract, or GMA change was
  required for this BunkFy-owned application and persistence hardening slice.

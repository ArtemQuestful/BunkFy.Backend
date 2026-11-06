# Data Rights Tenant-Termination Deadline Result Integrity Task

Status: complete
Date: 2026-08-22

## Goal

Keep every tenant-termination owner result and terminal verification result
inside the exact Data Rights execution window, including when an owner adapter
ignores cancellation and returns normally at or after its deadline.

## Audit Finding

The ownership split remains coherent. Data Rights owns the process deadline,
owner catalogue, protected replay, and terminal proof. Task Runtime owns the
outer task budget, retries, leases, and cancellation delivery. Owner modules
remain responsible for their own idempotent mutations and local proof.

Cancellation alone is not sufficient evidence that an owner call completed in
time. The normal owner executor, protected-export executor, and terminal
verification executor all create a linked cancellation token but accept a
normal result without observing the trusted clock after the await. An adapter
that ignores cancellation can therefore return outside the admitted attempt.
Replay proof also permits contribution timestamps exactly at the dispatch
deadline and does not bind its protection timestamp to that deadline.

## Ownership Boundary

- Data Rights enforces the exact owner-call deadline and authenticates replay
  timestamps against it.
- Owner modules continue to enforce their local request and result contracts.
- Task Runtime remains unchanged; its handler timeout is an outer recovery
  boundary rather than BunkFy domain proof.
- GMA remains unchanged because tenant-termination timing and replay semantics
  are BunkFy policy.

## Invariants

1. A normal owner or verification return is accepted only when the trusted
   clock is still strictly before the request deadline.
2. A contribution recorded at or after its dispatch deadline is not valid
   replay evidence.
3. A replay result protected at or after its dispatch deadline is not valid
   replay evidence.
4. Protected export owner results use the same strict deadline boundary.
5. A late normal return creates no result journal entry and cannot advance the
   central work item or terminal receipt.
6. Dispatch durability remains required before an irreversible owner call, and
   result durability remains required before central terminalization. Journal
   flush completion is not redefined as owner execution time.

## Delivery

1. Observe the trusted clock immediately after each awaited tenant-termination
   owner, export-generator, and verification call.
2. Tighten replay and export result proof to treat the deadline itself as
   outside the attempt.
3. Add focused regression tests for cancellation-ignoring calls that return at
   the exact deadline and for exact-boundary replay timestamps.
4. Run the Data Rights test assembly and one consolidated non-Docker repository
   gate at the slice boundary.

## Verification

- focused tests prove late normal returns cannot append or centrally commit;
- focused tests prove terminal verification cannot seal a receipt from a late
  live owner result;
- proof tests reject contribution and protection timestamps at the deadline;
- the complete Data Rights assembly passes; and
- one consolidated non-Docker repository gate passes after the slice is
  coherent.

Docker/provider and hosted CI checks are deferred because this slice changes no
schema, provider behavior, broker topology, or GMA code.

## Completion Evidence

- The focused deadline, replay-proof, export, and verification regression set
  passes 27/27.
- The complete Data Rights assembly passes 577/577.
- The synchronized solution and source-package checks pass, and the serial
  solution build completes with zero warnings and zero errors.
- Every configured PostgreSQL and SQL Server migration model is drift-free.
- The consolidated non-Docker repository sweep passes, including Architecture
  112/112, Migrations Host 26/26, Service Defaults 64/64, and Integration 65/65.
- No Docker/provider, hosted CI, or GMA run was added for this schema-free
  BunkFy application hardening slice.

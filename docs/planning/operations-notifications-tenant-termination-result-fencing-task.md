# Operations Notifications Tenant-Termination Result Fencing Task

Status: complete
Date: 2026-08-22

## Goal

Keep Operations Notifications tenant-termination export and destruction bound
to the exact live orchestration window and frozen workspace fence, including
when an awaited GMA lifecycle operation ignores cancellation or completes at a
deadline boundary.

## Audit Finding

The extension's ownership split is coherent. BunkFy owns notification audience,
typed product references, tenant-export mapping, and tenant-termination
adaptation. GMA Notifications owns generic notification persistence, scope
admission, bounded export, history closure, and scope destruction.

Audience construction remains least privilege and fails before persistence:
active property or owner candidates are narrowed by organization membership,
the destination domain's property-scoped read permission, and one active Staff
correlation. GMA also admits transport inbox work through the generic scope
lifecycle, so a closed scope suppresses late messages without product-specific
changes.

One adapter-level gap remains:

- export can detect an expired deadline and then create a retry result whose
  recorded time is outside the replay contract;
- destroy can accept an old completion receipt returned after the current
  orchestration deadline; and
- destroy validates the workspace termination fence only before calling the
  lifecycle, so it can claim completion after the selected frozen fence changes.

The Data Rights executor usually contains these cases through cancellation and
replay-proof validation, but the owner adapter should never emit or accept an
invalid contribution in the first place.

## Ownership Boundary

- Operations Notifications validates its request window, workspace fence, and
  the GMA lifecycle result it adapts.
- Data Rights continues to own dispatch, cancellation, replay protection, and
  result recording.
- Workspaces continues to own the termination fence.
- GMA Notifications remains unchanged; no hospitality or orchestration policy
  moves into the generic lifecycle.

## Invariants

1. Every awaited export, sink, snapshot, fence, and destroy boundary is checked
   against the contribution deadline.
2. A timestamp at or after the deadline is outside the attempt. The adapter
   throws a timeout instead of fabricating a replay-invalid retry result.
3. Destroy progress and receipts must have their latest proof timestamp strictly
   before the contribution deadline.
4. Destroy reads the frozen workspace fence before and after the GMA lifecycle
   call. Process id, termination epoch, frozen state, and fence version must stay
   unchanged before completion is claimed.
5. A changed or temporarily unavailable final fence yields a retryable result;
   GMA's idempotent destruction receipt makes the next attempt safe.
6. Export fragment writes remain lifecycle-owned staging. A timeout or failed
   owner result is not published as a completed tenant export fragment.

## Verification

- focused tests cover export and destroy responses that cross the exact deadline;
- focused tests reject progress and receipts timestamped at the deadline;
- focused tests cover a changed final workspace fence after GMA destruction;
- the complete Operations Notifications test assembly passes; and
- one consolidated non-Docker repository gate runs at the slice boundary.

Docker/provider and hosted CI checks are deferred because this slice changes no
schema, provider behavior, broker topology, or GMA code.

## Completion Evidence

- The focused tenant-termination contributor set passes 16/16.
- The complete Operations Notifications assembly passes 107/107.
- The synchronized solution and source-package checks pass, and the serial
  solution build completes with zero warnings and zero errors.
- Every configured PostgreSQL and SQL Server migration model is drift-free.
- The consolidated non-Docker repository sweep passes, including Architecture
  112/112, Migrations Host 26/26, Service Defaults 64/64, and Integration 65/65.
- No Docker/provider or hosted CI run was spent on this schema-free adapter
  hardening slice.

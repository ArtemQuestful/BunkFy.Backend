# Operations Notifications Recipient Convergence Task

Status: complete
Date: 2026-08-22

## Goal

Keep operational notification fan-out available when a candidate legitimately
stops being an active Staff recipient between audience discovery and final
projection. Valid recipients must still receive their copies without weakening
tenant, membership, permission, or Staff-identity integrity.

## Finding

Operations Notifications currently requires the Staff recipient resolver to
return every authorized candidate. A workspace owner without a current active
Staff identity, or a concurrent suspension or processing restriction, therefore
poisons the complete event. Valid recipients receive nothing, and replay can
later deliver a stale copy after eligibility has changed again.

## Ownership Boundary

- BunkFy Staff owns current active and unrestricted recipient correlation.
- Access Control and Organizations remain the membership and permission
  authorities used before Staff resolution.
- Operations Notifications owns fan-out convergence and decides that a
  legitimate current-state omission narrows the audience.
- GMA Notifications continues to own generic, single-user inbox persistence;
  it does not learn BunkFy Staff status or partial-audience policy.

No GMA source change is required.

## Invariants

- only an active organization member with the required property permission and
  one current active, unrestricted Staff identity can receive a copy;
- a candidate omitted by the current Staff resolver is skipped and cannot
  suppress copies for other valid recipients;
- a resolver exception, unexpected subject, duplicate subject, empty Staff id,
  malformed subject, or direct-recipient Staff-id mismatch fails closed before
  any copy is projected;
- direct Staff notifications revalidate the same exact Staff identity and are
  quiet when that identity is no longer eligible;
- actor exclusion, deterministic notification ids, bounded membership,
  authorization and Staff batches, ordering, and tenant scope remain unchanged;
- one bounded warning records only notification type and skipped count per
  affected fan-out. It must not contain tenant, property, Staff, subject, or
  payload data.

## Delivery

1. Allow the Staff resolver to return a valid subset while preserving strict
   validation of every row it does return.
2. Revalidate direct Staff notifications through the same current-recipient
   resolver and require the resolved Staff id to match the source event.
3. Emit one source-generated warning for legitimate omissions.
4. Add focused tests for partial and total omission, direct-recipient races,
   malformed resolver output, resolver failure, and existing batch limits.
5. Run the focused extension suite during development and one consolidated
   non-Docker repository gate at slice completion.

## Deferred

- External delivery-time authorization remains required before email, SMS, or
  push operational tags are enabled.
- Exact hosted replay and dead-letter rehearsal belongs to the release
  candidate; this slice changes no schema, broker, provider, or host topology.

## Evidence

- Focused Operations Notifications tests passed 116/116, including partial and
  total current-recipient omission, direct-recipient revalidation, malformed,
  unexpected and duplicate mappings, resolver failure, actor exclusion, and
  bounded batching.
- `pwsh ./eng/verify.ps1 -SkipRestore` passed synchronized solution and source
  checks, a full build with zero warnings and zero errors, every migration-drift
  check, and all non-Docker suites. The affected Staff, Workspaces, Operations
  Notifications, Architecture, and integration suites passed 285, 397, 116,
  112, and 65 tests respectively.
- No Docker gate was run because this slice changes only in-process fan-out
  convergence and introduces no schema, provider, broker, migration, or host
  topology behavior.
- GMA source remained unchanged; generic Notifications persistence and the
  existing BunkFy Staff resolver contract were sufficient.

# Workspace Owner Access Transition Integrity Task

Status: complete
Date: 2026-08-22

## Goal

Make workspace ownership transfer converge safely across Organizations,
AccessControl, Staff, and Workspaces. A stale membership event must not restore
an obsolete owner wildcard, and a demoted active owner must become an ordinary
assignable Staff member without a privilege-retention window.

## Ownership

- GMA Organizations owns membership role, status, and version. It exposes a
  generic exact-membership reader; it does not know BunkFy roles or Staff.
- BunkFy Staff owns the linked employment status and exposes a minimal current
  operational-identity snapshot.
- BunkFy Workspaces owns Owner, Front desk, membership-marker, profile, and
  property-access policy plus transition convergence.
- GMA AccessControl keeps generic roles, profiles, anti-escalation, history,
  and assignment mechanics unchanged.

## Invariants

- Membership events are triggers, not authoritative state. Reconciliation
  reads the current Organizations membership before changing access.
- An active Organizations owner has the protected BunkFy owner assignment and
  no ordinary membership marker or operational profile assignments.
- A current ordinary member has no owner assignment. Front desk is restored
  only when the linked Staff record is currently active.
- Missing, suspended, departed, or anonymised Staff remains denied.
- Owner demotion removes broad authority before ordinary access is restored.
  Owner promotion grants broad authority before ordinary access is removed.
- Staff lifecycle changes and membership-role changes serialize on the
  immutable workspace-plus-subject coordinate before taking an optional Staff
  aggregate coordinate. The global order is subject, then Staff.
- A current Organizations non-owner cannot use a stale AccessControl owner
  wildcard while asynchronous reconciliation is pending.
- Generic role-assignment commands cannot create a BunkFy Owner grant outside
  the exact workspace root or unless Organizations confirms the target is the
  current active owner.
- Owners cannot be targeted by raw or product ordinary-profile assignment.

## Delivery

1. Add a provider-neutral exact membership reader to GMA Organizations
   Contracts and Persistence with PostgreSQL-compatible query semantics.
2. Add a minimal BunkFy Staff operational-identity reader over the owning
   Staff repository.
3. Expand the stable Workspaces membership handler into authoritative access
   reconciliation without changing its subscription identity.
4. Add a BunkFy access-decision provider that only participates when an owner
   assignment exists and denies it unless Organizations still reports an
   active owner.
5. Guard generic Owner assignment commands with the same authoritative
   membership state.
6. Remove the duplicate extension-owned membership assignment handler and
   reject owners in the ordinary access-profile assignment policy.
7. Extend the Workspaces Staff-access transaction lock so reconciliation can
   serialize even before a linked Staff row exists. Process and Staff-version
   paths discover their subject coordinate without tracking, then reload the
   aggregate after the lock is held.

## Verification

- stale promotion and demotion events converge to the current membership;
- promotion grants Owner before clearing ordinary access;
- demotion removes Owner before restoring Front desk for active Staff;
- inactive or missing Staff receives no ordinary access;
- concurrent Staff lifecycle and ownership transitions share the subject lock,
  while unrelated subjects continue independently;
- stale owner wildcard authorization is denied immediately;
- raw profile assignment rejects owner targets;
- GMA Organizations, Workspaces, extension, architecture, and focused
  PostgreSQL tests pass before one consolidated repository gate.

## Evidence

- GMA Organizations unit tests: 326 passed.
- BunkFy Staff unit tests: 285 passed.
- BunkFy Workspaces unit tests: 397 passed.
- BunkFy Workspaces extension tests: 105 passed.
- Focused PostgreSQL Organizations snapshot scenario: 1 passed.
- Focused PostgreSQL Workspaces subject-serialization scenario: 1 passed.
- `pwsh ./eng/verify.ps1 -SkipRestore`: synchronized solution and source
  checks, zero-warning build, migration-drift checks, architecture tests, and
  all non-Docker test projects passed.

## Deferred

- Hosted ownership-transfer browser rehearsal belongs to the exact release
  candidate, not this source slice.
- Multi-workspace account UX and company approval remain deployment and product
  gates.

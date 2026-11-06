# Workspace Access Administration Task

Status: complete
Date: 2026-08-04
Updated: 2026-08-12

## Goal

Make workspace invitations and custom operational roles usable by explicitly
delegated managers while preserving Organizations ownership and GMA Access
Control anti-escalation.

## Ownership

- GMA Access Control owns generic profile authorization, exact delegation
  checks, assignment scope validation, and the non-overwriting `Ensure`
  contract.
- BunkFy Workspaces owns which generic profile capabilities are available in
  the product, its protected seed definitions, seed upgrades, and the UI
  capability split.
- Organizations membership listing, lifecycle, and ownership transfer remain
  owner-only. This slice does not infer those rights from Staff permissions.

## Invariants

- A delegated actor can create or assign only permissions they currently hold
  at the exact target scope; GMA remains the final anti-escalation authority.
- Built-in BunkFy profile keys cannot be created, edited, or archived through
  raw generic or product APIs.
- The BunkFy system provisioner may reconcile an active built-in profile to
  the exact current seed definition during bootstrap.
- Profile reads, profile mutation, profile assignment, and Staff enrollment
  stay distinct capabilities.
- Invites require both Staff enrollment management and profile visibility.
  Roles are read-only without profile-management permission. Members remain
  owner-only until a product-facing member-governance boundary is designed.

## Delivery

1. [Complete] Add profile read, manage, and assign capabilities to the BunkFy permission
   catalogue with explicit dependencies and sensitivity.
2. [Complete] Upgrade the Manager seed and internal provisioner grant, then reconcile
   existing active seeds through the generic profile manager.
3. [Complete] Extend the existing Workspaces mutation-admission policy to protect seed
   keys across raw GMA and BunkFy routes.
4. [Complete] Drive Workspace Settings tabs and actions from evaluated permissions.
5. [Complete] Verify focused seed, policy, access-management, and web behavior before the
   full slice gates.

## Outcome

- BunkFy opts the generic profile read, manage, and assign permissions into
  its product catalogue. The Manager seed receives all three; company support
  receives read only.
- Seed version 2 reconciles active built-in profiles to the exact BunkFy
  definition. Reconciliation failures stop bootstrap, and admin status reports
  active-but-drifted seeds explicitly.
- The Workspaces admission extension denies raw create, update, or archive
  attempts for protected seed keys. Only the exact system provisioner may
  reconcile them through the admitted bootstrap path.
- Workspace Settings performs one batched permission evaluation for non-owner
  members. Members remain owner-only, Roles supports read-only and management
  modes, and Invites requires both `staff.manage` and profile visibility.
- GMA Access Control was not changed. Its generic exact-scope delegation,
  anti-escalation, profile mutation, and assignment contracts already own the
  reusable behavior needed by this slice.

## Verification Cadence

Use focused unit and web tests while editing. Run the complete Workspaces and
Workspaces extension suites, backend architecture guards, web typecheck/tests,
and production build once when the slice is coherent. No Docker rerun is
needed unless persistence behavior changes.

## Verification

- focused Workspaces seed and catalogue tests: 23 passed;
- Workspaces tests: 286 passed;
- Workspaces extension tests: 83 passed;
- backend architecture tests: 83 passed;
- Integration.Tests host-composition build: succeeded with 0 warnings and 0
  errors;
- web typecheck and lint: passed;
- web tests: 21 files and 138 tests passed;
- Vite production build: passed.

No Docker scenario was rerun because the slice changes neither persistence
shape nor broker/restart behavior.

The later exact-release Preview rehearsal
`preview-workspace-access-0257a44` passed 22 trusted-HTTPS browser checks and
proved this administration path with separate owner/member accounts. The seed
version 2 wording above records what this original slice introduced; the current
code-owned BunkFy seed is version 4, whose estate-wide activation evidence
is now complete for Preview. Exact release
`preview-workspace-access-estate-651107f` bootstrapped all 3 active Preview
workspaces from one drifted seed to version 4 convergence and observed zero
legacy members. This does not retire the separate estate gate for future or
hosted deployment classes.

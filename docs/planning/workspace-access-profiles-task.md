# Workspace Access Profiles Task

Status: backend and owner-facing least-privilege cutover implemented;
default/custom-profile browser and Preview seed-estate proofs complete
Date: 2026-07-22
Updated: 2026-08-12

## Goal

Let authorized workspace managers configure named access profiles and assign
them to members without exposing GMA Admin APIs or allowing one workspace to
affect another workspace's role definitions.

## Current Baseline

- Workspace owners receive the protected owner wildcard role.
- Organizations membership is admission and governance only; it grants no ordinary BunkFy operation permissions by itself.
- New ordinary members receive a permission-free membership marker plus the versioned Front desk access profile only after Staff onboarding succeeds.
- Existing members retain the legacy front-desk compatibility grant until the explicit workspace bootstrap safely installs profiles and removes that grant member by member.
- Ordinary members cannot configure properties or inventory sales, manage integrations, administer Staff lifecycle, archive guests, or manage workspace membership.
- Staff self-service profile reads and writes are identity-bound and do not require broad `staff.manage` access.

## Production Audit

The backend now implements the target least-privilege model:

- Organizations membership remains the admission and governance fact (`Owner` or ordinary member).
- A new BunkFy ordinary-member role is a permission-free membership marker used by assignment policy. The old role remains a temporary compatibility grant only for unmigrated tenants.
- Workspace owners keep the protected owner wildcard assignment.
- Every active ordinary member receives at least one tenant-scoped access profile. A versioned BunkFy Front desk seed is the onboarding default.
- Effective permissions are the union of assigned profiles. The current BunkFy operator workflow intentionally reconciles one operational profile per member; any future additive-profile workflow must show the resulting combined capabilities explicitly.
- Profile replacement and offboarding are deny-first, transactional or durably recoverable. A partial failure may temporarily deny access, but must never retain broader authority than the requested result.
- Existing member assignments require an explicit, idempotent backfill before their compatibility grant is removed.

Seed version `4` is code-owned and its keys are protected from product and raw
generic mutation routes. GMA's generic `Ensure` operation remains
non-overwriting; BunkFy bootstrap explicitly detects drift and reconciles an
active built-in profile through the generic profile manager as the exact system
provisioner. Archived seeds and failed reconciliation stop bootstrap safely.
Admin status reports missing, archived, and active-but-drifted seeds.

The Admin CLI and private Admin API expose tenant-required status and confirmation-gated bootstrap operations under the dedicated `workspaces.access.bootstrap` permission. Bootstrap drains the legacy assignment set in bounded pages, preserves every existing custom profile, adds Front desk, installs the permission-free marker, and removes the legacy assignment last. A failed profile reconciliation therefore retains prior access for a safe retry.

The BunkFy provisioner has a tenant-scoped internal role containing exactly the product's delegable permissions. Bootstrap establishes that assignment before profile writes; GMA anti-escalation remains unchanged, and the internal principal never receives the owner wildcard.

The global legacy role definition must not be changed or deleted until every deployed workspace reports zero legacy assignments. BunkFy deliberately has no unsafe global finalizer or inferred tenant enumeration.

## Delivered Generic GMA Seams

GMA continues to own only domain-neutral mechanics. AccessControl now exposes a narrow Contracts facade that can:

- create a tenant-scoped profile only when its product-owned key is absent and return the existing profile without overwriting customer edits;
- inspect profiles and assignments by exact owner scope and stable key;
- reconcile the complete profile-assignment set for one subject and owner scope atomically, with existing delegation and product assignment policies applied to every target profile;
- preserve immutable assignment history and optimistic concurrency during reconciliation.

Organizations now exposes a Contracts-only membership lifecycle facade so a product-owned Staff offboarding process can activate, suspend, remove, or restore the exact ordinary membership idempotently. Owner memberships are protected and invalid transitions are reported without writes. The generic facade owns no Staff status, reason vocabulary, property plan, or BunkFy role name.

These seams belong in GMA because cross-module extensions must not reference another reusable module's Application implementation or call its HTTP API internally. Seed definitions, migration policy, default selection, and offboarding decisions remain BunkFy-owned.

## Delivered GMA Capability

GMA AccessControl retains global compatibility roles and now separately owns tenant-scoped access profiles. Profiles have an immutable id and owner scope, a tenant-local key, display metadata, bounded permission definitions, optimistic versions, immutable change history, archive behavior, and scoped assignments.

The reusable module now provides:

- immutable profile id plus owning scope;
- display name and description separated from the internal role key;
- explicit permission allowlist supplied by the composing product;
- actor anti-escalation for every delegated permission;
- a Contracts policy seam for product-owned assignment eligibility;
- seed-safe ensure-by-key that never overwrites an existing customer-edited profile;
- exact-scope profile and subject-assignment inspection;
- atomic exact-set assignment reconciliation with policy validation before writes;
- exact-scope, transactional assignment cleanup with immutable history;
- scoped assignment, update concurrency, and history facts;
- list/create/update/archive/assign APIs authorized in the owning scope;
- no dependency on Organizations, BunkFy modules, or product role names.

BunkFy supplies the explicit front-desk permission allowlist. A profile target must be a user with an active owner or member compatibility assignment in the same exact workspace scope. Suspension and removal first revoke those compatibility assignments and then remove every custom profile assignment through AccessControl Contracts, so a concurrent new assignment cannot survive offboarding.

## BunkFy Responsibility

- define seed profiles such as Owner, Manager, Front desk, Housekeeping, and Viewer;
- decide which BunkFy permission codes each profile may contain;
- prevent delegation above the caller's authority;
- present role visibility and action summaries in Workspace settings;
- coordinate invitation profile selection and later access changes;
- preserve access denial while assignments are provisioning or being removed.

The backend allowlist, workspace/property-scoped assignment eligibility policy,
versioned seeds, default onboarding assignment, per-workspace migration tool,
membership-driven cleanup, Staff-driven offboarding orchestration,
invitation-time profile/property plans, product facade, and capability-driven
workspace-admin UI are complete. The Preview deployment estate now has current
version 4 status/bootstrap evidence. The deployed default Front desk onboarding,
custom-profile administration, and post-onboarding assignment paths are also
browser-proven with separate accounts. Other deployment classes still require
their own explicit estate proof before the global legacy role can be retired.

## Delivery Order

1. [Complete] Prepare and land the two generic Contracts facades in their owning GMA modules, with provider-neutral behavior, PostgreSQL/SQL Server coverage where applicable, concurrency tests, and no BunkFy vocabulary.
2. [Complete] Pull the released GMA revisions through GMA-Skeleton and BunkFy without local framework forks.
3. [Complete for Preview; other deployment classes deferred] Add versioned BunkFy seed definitions and an idempotent workspace access bootstrap/backfill process. Prove existing members retain the intended Front desk access before removing their compatibility grants. Run current version 4 status/bootstrap for every deployed workspace before globally retiring the legacy role definition.
4. [Complete] Add a BunkFy workspace-role facade and permission catalogue for the web. Reconciliation is exact-scope, anti-escalating, and deny-first.
5. [Complete] Extend invitation/enrollment coordination with server-owned profile and property-assignment plans. Applicants may review but never author authority-bearing fields.
6. [Complete] Add durable Staff suspension/departure orchestration that denies membership first, revokes profile assignments, preserves unrelated workspaces/global Auth, and supports explicit recovery.
7. [Complete for the current single-role product workflow] Build workspace-admin role/profile UI, permission summaries, assignment controls, and invitation-time selection.
8. [Complete] Keep the PostgreSQL/NATS concurrency and Worker restart proof green, then cover custom profile administration and reassignment with separate owner/member accounts.

## Completion Proof

- two workspaces may use the same display name with different permissions and no cross-workspace reads or updates;
- a workspace owner can configure and assign allowed profiles without platform-admin access;
- ordinary members cannot grant owner or integration/security administration privileges;
- role changes update permission-filtered navigation and API authorization consistently;
- PostgreSQL, API contract, concurrency, isolation, and browser workflow tests pass.

The exact-release Preview rehearsal `preview-workspace-access-0257a44` passed 22
trusted-HTTPS checks. Its custom-role slice proved create, property-scoped
assignment, live permission update, exact reassignment, archive, and exclusion
from active assignment controls. The later exact-release estate rehearsal
`preview-workspace-access-estate-651107f` inspected all 3 active Preview
workspaces through authorized contracts, bootstrapped all 3 from one drifted
seed to version 4 convergence, observed zero legacy members, and retained only
aggregate counts plus one-way workspace fingerprints. Production classes
beyond Preview remain a separate estate gate.

The root production-admission boundary now requires a distinct private
`workspace-access-seed-estate` record for the exact hosted candidate. The
Preview-only evidence cannot satisfy it. Global legacy-role retirement remains
blocked until every production deployment has an accepted current estate
record.

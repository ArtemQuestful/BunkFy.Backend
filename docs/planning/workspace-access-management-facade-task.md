# Workspace Access Management Facade Task

Status: backend and capability-driven UX implemented; default/custom-profile
multi-account browser and Preview seed-estate proofs complete
Date: 2026-07-22
Updated: 2026-08-12

## Goal

Give authorized workspace actors one BunkFy API for operational access
profiles, member assignments, and Staff join sources without exposing GMA
module administration APIs or leaking BunkFy policy into reusable modules.

## Ownership

- GMA AccessControl owns generic profile state, optimistic versions, allowed permission registration, assignment history, anti-escalation, and exact-scope reconciliation.
- GMA Organizations owns invitation and enrollment-link secrets, lifecycle, owner authorization, expiry, claim limits, and membership governance.
- BunkFy Workspaces owns product role names, permission labels and dependencies, protected seeds, delegable permission policy, property plans, join-source summaries, and operator workflows.
- Staff remains the employment authority. The facade may correlate existing public contracts but does not copy Staff profile state into Workspaces.

## Invariants

- The ambient workspace scope is authoritative. Route or body organization ids cannot select another tenant.
- The authenticated subject is resolved on the server and passed to every generic management contract.
- The product permission catalogue is the intersection of active GMA permissions and BunkFy's explicit delegable allowlist.
- Profile writes reject unknown, non-delegable, or dependency-incomplete permission sets before dispatch.
- Seed profile keys remain present, identifiable, and code-owned. Product and
  raw generic APIs cannot create, edit, or archive them; authorized managers
  use custom profiles for workspace-specific permission sets.
- Assignment replacement is exact-scope and anti-escalating. The target must remain an ordinary member of the same workspace; owner governance access is never edited through this flow.
- Join-source list responses contain lifecycle facts and sanitized BunkFy plan summaries, never token digests or plaintext secrets.
- Revocation and replacement are deny-first. A replacement source is minted only after the previous source is denied or confirmed unusable, and uses a caller-supplied idempotency id through the existing issuance facade.
- Plaintext join tokens are returned only by the successful creation call. Exact retries never mint another source and never replay the token; a lost token requires an explicit new replacement.
- Organizations membership listing, lifecycle, and ownership remain owner-only.
  Exact BunkFy Staff join-source and claim operations may be delegated through
  the Workspaces policy, but operational custom profiles never grant broader
  Organizations governance implicitly.

## Delivery

1. Add product DTOs and a grouped permission catalogue with labels, descriptions, risk hints, and dependencies.
2. Add a Contracts-only Workspaces application facade for profile list/create/update/archive and exact member assignment reads/reconciliation.
3. Add paged invitation/enrollment list, revoke/disable, and deny-first replacement operations composed from Organizations Contracts and Workspaces plans.
4. Expose tenant-required BunkFy endpoints with server-derived actor and stable product error mapping.
5. Generate OpenAPI contracts and replace raw Organizations/AccessControl usage in Workspace settings.
6. Add owner-facing profile, assignment, invitation, QR, revoke, and replacement UX with mobile coverage.

## Verification

- two workspaces can use the same profile display name without cross-scope reads or writes;
- a non-owner and an owner from another workspace are denied;
- profile permissions cannot exceed the product catalogue or omit required dependencies;
- protected seeds cannot be archived and assigned profiles obey generic concurrency checks;
- member reconciliation cannot target owners, removed members, services, or another workspace;
- list operations batch Workspaces plan reads and remain bounded by page size;
- revoked/disabled sources cannot be replaced before denial succeeds;
- retries cannot mint duplicate replacement sources or replay a previously returned one-time token;
- unit, architecture, PostgreSQL, OpenAPI, web, and multi-account browser workflows pass.

## Implementation Checkpoint

- BunkFy exposes product permission catalogue, profile lifecycle, exact member assignments, and paged join-source management without exposing raw GMA administration APIs.
- Invitation and enrollment-link replacement reuses the source's BunkFy access plan, denies first, and resumes safely after a completed denial by using a caller-supplied replacement id.
- GMA Organizations adds only owner-authorized exact source lookups; replacement policy and access-plan composition remain in BunkFy.
- Focused Workspaces tests cover permission metadata, deny-before-inspect behavior, owner protection, exact assignment reconciliation, batched plan reads, replacement ordering, retry behavior, and one-time token semantics.
- Generated contracts and the owner-facing Workspace settings UX now cover protected/custom roles, member assignments, recipient-aware invitations, reusable team QR sources, lifecycle review, revoke, and deny-first replacement.
- Desktop and 390px mobile preview checks pass. Legacy Organizations invitations without a BunkFy access plan render as lifecycle-only records instead of failing the page.
- Current seed version `4` protects and reconciles built-ins, exposes drift in
  bootstrap status, and includes profile read/manage/assign in the Manager seed.
  Preview now has fresh version 4 activation evidence for every active
  workspace; other deployment classes require their own estate evidence.
- The Workspace settings UX is permission-driven: Members remains owner-only,
  Roles supports read-only and management modes, and Invites requires both
  `staff.manage` and profile visibility.
- The trusted-HTTPS Preview browser rehearsal now proves separate owner and
  applicant accounts, Front desk assignment, property-scoped allow/deny,
  permission-filtered management denial, replay stability, and Worker-restart
  convergence. Exact release `preview-workspace-access-0257a44` additionally
  proves custom-profile create/edit/assignment, live permission convergence,
  exact reassignment, archive, and exclusion from active assignment controls.
- Exact release `preview-workspace-access-estate-651107f` then enumerated the
  authorized Preview catalog, bootstrapped all 3 active workspaces, and proved
  each one converged on seed version 4 without retaining raw tenant identity.
  Hosted or future deployment estates remain separately admitted.
- Final production admission now requires a distinct private Workspace Access
  estate reference bound to the exact hosted candidate. The public bundle
  validates and retains only the non-secret reference; hosted execution,
  authenticity, and approval remain deployment-owned.

# Workspaces, Identity, And Staff Onboarding

Status: accepted direction
Date: 2026-07-14

## Decision

BunkFy uses one global Auth identity per person and a separate organization membership for each workspace the person can enter. A newly registered account has no PMS access until it creates a workspace or accepts an invitation.

The reusable domain name is `Organization`. BunkFy presents an organization as a `Workspace`. The organization id is also the immutable tenant/scope id used to partition BunkFy product data. A workspace may contain multiple properties.

This separates five facts that must not be inferred from one another:

- Auth identity proves who authenticated.
- Organization membership proves which workspaces the subject may enter.
- AccessControl grants decide which operations the subject may perform in tenant/property scopes.
- Staff records describe employment and operational assignments.
- Property records describe the managed hostel/site topology.

Guests remain PMS records only. Public BunkFy registration creates a potential operator identity, never a guest account and never implicit product access.

## Ubiquitous Language

- **Account**: one global GMA Auth member identity, credentials, external identities, sessions, verification state, and MFA state.
- **Organization**: the reusable GMA-owned membership and tenancy boundary.
- **Workspace**: BunkFy's product label for an organization.
- **Tenant/scope**: the technical isolation context whose id equals the organization id.
- **Membership**: the lifecycle relationship between an Auth subject and an organization.
- **Owner**: a governance relationship protected by a last-owner invariant. It is not the platform administrator.
- **Invitation**: a revocable, expiring, normally single-use offer for one person to join an organization.
- **Enrollment link**: a separately governed reusable join link, optionally rendered as a QR code.
- **Staff member**: BunkFy employment/operations data that may exist before, during, or after an Auth account link.
- **Access profile**: a BunkFy-owned safe assignment template mapped to GMA AccessControl roles and scopes.

## Ownership

### GMA Framework

Framework owns only generic mechanics:

- ambient operation scope and partition primitives;
- authenticated subject resolution;
- a small extensible scope-activation/access gate;
- transactional command, outbox, inbox, retry, and observability foundations.

Framework must not know organizations, invitations, Staff, properties, owner roles, or BunkFy access profiles. If global and ambient module data scopes need a reusable selection seam, that seam belongs in Framework and remains domain-neutral.

### GMA Auth

Auth owns global identity and authentication:

- public password registration;
- external identity registration and linking through optional provider adapters;
- email ownership and verification;
- sessions, refresh rotation, sign-out, account disablement, and later MFA;
- a generic controlled account-provisioning/admission seam for composition extensions.

Auth exposes a narrow active-member admission snapshot to trusted in-process consumers. The snapshot proves current activity in one explicit Auth scope and may include the preferred verified email; it does not expose credentials, sessions, roles, or disablement details. Auth's separate contact reader intentionally remains usable for Auth-owned security delivery after account disablement.

Auth does not own workspaces, memberships, invitations, Staff profiles, roles, or tenant/property permissions. `AuthProfile.ScopeAware()` remains supported for projects that intentionally want separate identities per scope. BunkFy uses a global identity profile that can coexist with enabled tenant resolution.

### GMA Organizations

A new reusable `Gma.Modules.Organizations` module owns:

- organization identity, display name, slug, lifecycle, and optimistic version;
- owner/member governance and the last-active-owner invariant;
- membership lifecycle and subject uniqueness within an organization;
- invitation lifecycle, recipient constraints, token digest, expiry, revocation, and acceptance;
- enrollment-link lifecycle, claim limits, approval policy, and audit facts;
- cross-tenant membership discovery for the authenticated subject;
- public, admin API, and admin CLI contracts appropriate to each operation.

The organization catalog is global authority and cannot be stored behind the ambient tenant query filter. Organization-owned rows include explicit organization ids and enforce authorization in their own queries and commands.

Organizations does not own product permissions or employment data. Membership alone never grants a BunkFy operation.

The Organizations join-admission contract distinguishes `Allowed`, `Denied`,
and `Unavailable`. BunkFy maps a deliberately restricted workspace to denial,
but maps missing or invalid authoritative workspace state to unavailable so the
join fails closed with retryable semantics instead of appearing permanently
rejected.

### GMA Tenancy

Tenancy continues to resolve and propagate the active resource scope. In BunkFy the selected organization id becomes the tenant id. An Organizations/Tenancy extension validates that the organization exists, is active, and that a user subject has an active membership before ordinary product endpoints execute.

The tenant header or route value is selection input, not authority. Service, system, platform-admin, and adapter paths require explicit policies rather than inheriting user membership behavior accidentally.

### GMA AccessControl

AccessControl remains generic scoped RBAC. It owns role definitions, permission grants, subject assignments, and effective decisions. Organization ownership and membership are not encoded as Auth claims.

Reusable Organizations/AccessControl composition may install and remove governance assignments. BunkFy owns product access profiles and delegation rules. Ordinary staff invitations cannot create owners. Ownership grant/transfer is a separate confirmation-gated operation.

### GMA Extensions

Cross-module behavior belongs in opt-in extensions:

- Auth/Organizations preserves invitation intent across password or external registration and completes membership acceptance for new or existing accounts.
- Organizations/Tenancy supplies organization existence, lifecycle, and membership activation policy.
- Organizations/AccessControl provisions owner governance access and removes or disables scoped access during membership lifecycle changes.
- Organizations/Notifications may deliver invitation and membership security messages without coupling either source module to Notifications.

Extensions reference only public module contracts. Generic cross-module process state may belong to the extension that coordinates it. Product-specific state and policy, such as a proposed BunkFy Staff profile, belong to a BunkFy module even when that module coordinates several GMA authorities.

### BunkFy

BunkFy owns the product policy and experience:

- workspace terminology and navigation;
- whether workspace creation is enabled for public accounts in a deployment;
- applicant-proposed and manager-pre-provisioned Staff profile data;
- property assignment choices;
- named access profiles and which profiles an inviter may delegate;
- coordinated Staff/Auth/access onboarding and offboarding process state;
- setup progress, retries, operator-visible failures, and audit presentation.

The existing Staff aggregate remains the source of employment truth. The BunkFy Workspaces module owns the temporary enrollment process: it holds the applicant's proposed profile until Organizations accepts or rejects the join, then provisions Staff and a constrained AccessControl baseline through public contracts. Terminal applicant PII is redacted. Future manager-pre-provisioned profiles, property assignment plans, and named access profiles extend this product-owned process without changing GMA Organizations.

## Registration And Workspace Creation

1. A visitor registers one global account or signs in through an enabled external provider.
2. Password confirmation is a client concern; Auth remains the authoritative password-policy and account-creation boundary.
3. The Auth member must still be active and verifies a usable email before creating a workspace or accepting an email-bound invitation. A provider-verified email can satisfy the same Auth-owned fact.
4. An account with no active memberships sees only onboarding, account-security, sign-out, invitation acceptance, and workspace-creation surfaces.
5. Workspace creation creates the organization and first owner membership in the Organizations transaction.
6. Durable BunkFy composition provisions tenant-scoped owner access and the BunkFy owner Staff profile idempotently.
7. Product access remains deny-by-default while provisioning is incomplete. The client shows setup progress and can safely retry/resume.
8. The first property is created as a subsequent BunkFy setup step. It is not part of the organization transaction.

Workspace creation policy is deployment configuration. BunkFy enables authenticated self-service creation by default; private installations may choose platform-admin-only or invitation-only creation without changing the domain model.

The platform administrator and workspace owner are separate principals. Existing AccessControl CLI bootstrap protects the platform control plane; it must not be reused as workspace creation.

## Workspace Selection

After authentication the client lists memberships from the global Organizations module. If exactly one active workspace exists it may be selected automatically. Otherwise the client shows a workspace picker.

The active workspace should be represented in the route or tab-local session state. Switching workspace changes the tenant header/resource scope and invalidates tenant/property query caches. It does not create a new Auth account or require a new password login.

The current frontend keeps workspace and property selection in one `WorkspaceProvider` while the flow is small. Property selection is keyed by workspace id; split it into a dedicated `PropertyProvider` when independent workspace-level surfaces make the combined context materially harder to maintain.

## Staff Invitation Flow

1. An authorized manager creates a single-use invitation or a separately governed reusable enrollment link.
2. The recipient opens the link or QR and sees a sanitized preview: workspace, inviter, enrollment mode, and expiry.
3. An existing account signs in; a new person registers a global account. Invitation intent survives redirects in tab-scoped storage while the URL fragment is scrubbed immediately.
4. The authenticated applicant submits the original token plus a proposed Staff profile. BunkFy derives the organization/source identifiers and the current active-member/verified-email facts from Auth on the server.
5. Organizations admits only the exact subject and source for which the BunkFy process is ready. Automatic enrollment may create membership immediately; approval enrollment first creates a pending claim bound to the process.
6. An owner or explicitly authorized Staff-onboarding manager approves or rejects a bound claim. Organizations atomically creates or restores membership only on acceptance.
7. Accepted invitation/claim facts drive the Workspaces process to provision one idempotent Staff identity and then install the constrained member assignment.
8. The process completes only after both product authorities acknowledge success. Until then membership alone grants no BunkFy operation; failures are visible and owner-retryable.
9. A later access-profile slice may add manager-selected Staff/property/profile plans, but those plans remain server-owned BunkFy state and cannot broaden reusable links into owner access.

Email values are never used to discover and silently link an existing Staff profile. The current correlation is the server-inspected Organizations source plus authenticated subject; email is only a recipient constraint and display/contact fact verified against Auth-owned state.

Organizations keeps recognized join-source metadata available across terminal
states for its bounded retention window. Workspaces may use that metadata only
to locate and return the existing application for the same currently admitted
subject. A terminal source cannot create or mutate an application or reactivate
an access plan. While an application is still `Submitted` and the source is
active, a repeat submission may update the applicant draft; after the process
advances, retries return its current outcome without retaining a
PII-derived request fingerprint.

## Invitation And Enrollment Security

- Invitation and enrollment secrets use at least 256 bits from a cryptographically secure generator.
- Only a purpose-separated digest is stored; plaintext appears only in the one-time URL response.
- Tokens are expiring, revocable, rate-limited, redacted from logs, and compared in constant time.
- Link origins come from an allowlist/configuration, never an untrusted Host header.
- The join page uses a strict referrer policy and removes secrets from browser-visible history after capture.
- Preview is read-only and sanitized; acceptance, revocation, approval, and resend are explicit state-changing operations.
- Exact retry by the same subject is idempotent. A claim by another subject fails without revealing sensitive invitation state.
- Role names, permissions, organization ids, subject ids, and property ids supplied by the browser or encoded in a QR are never trusted authority. Server-owned invitation/onboarding state is authoritative.
- Named email invitations require the same verified Auth email. Changing the target email supersedes the previous invitation.
- Owner creation/transfer, reusable enrollment, and high-privilege access require separate permissions and confirmation.
- BunkFy operator onboarding requires `workspaces.staff-onboarding.manage` plus access-profile visibility; its source mutations and provisioning retries require configured privileged-operation assurance. Applicant self-service remains bound to the exact authenticated subject and source.
- Audit records retain bounded lifecycle facts but never token plaintext, password material, OAuth tokens, or unrestricted PII.

A QR code is a transport representation, not a separate authority mechanism. A one-person QR contains a normal single-use invitation URL. A reusable shared QR uses an `EnrollmentLink` with expiry, maximum uses, optional approval, and a low-privilege access profile. Reusable links can never grant ownership automatically.

## Lifecycle And Offboarding

- Organization suspension blocks ordinary member access while preserving data and platform-admin recovery.
- Removing a membership revokes workspace access but does not disable the global Auth account or other workspace memberships.
- Staff suspension/departure triggers an explicit BunkFy offboarding process that suspends/removes the organization membership and scoped grants according to product policy.
- Ordinary product and rehearsal offboarding uses that Staff lifecycle; direct
  Organizations membership mutation is denied so it cannot bypass Staff and
  access convergence.
- Access removal takes effect before background cleanup is considered complete. Retried events cannot restore a newer suspension/removal decision.
- Owner departure requires ownership transfer or another active owner. The last owner cannot be removed, suspended, or downgraded accidentally.
- Sessions remain global. High-risk account compromise uses Auth session revocation; ordinary workspace offboarding uses membership/grant revocation.
- Historical Staff, membership, invitation, assignment, and audit records follow separate retention/redaction policies rather than cascading hard deletes.
- Organizations invitation and enrollment retention is bounded but disabled in the API and Worker baseline. After product policy is approved, enable it on exactly one long-running maintenance host, normally the Worker.

## Consistency And Failure Recovery

No workflow uses a cross-module database transaction. Each authority commits its own state and publishes versioned facts through its outbox. Extension handlers use inbox deduplication and optimistic state transitions.

A module-owned EF inbox already provides the transaction for its handler. Module repositories invoked by that handler participate in the current transaction instead of opening a nested transaction on the same connection. Standalone command paths still create their own transaction where atomic safety checks require one.

Workspace creation and invitation acceptance are durable processes with explicit states such as `Pending`, `Provisioning`, `Completed`, `Failed`, `Revoked`, and `Superseded`. Process steps are replay-safe. The client can poll/read current status and never treats an HTTP timeout as proof that a command failed.

The membership gate and AccessControl both fail closed. A partially provisioned membership without grants cannot enter PMS operations. Removing membership blocks access even if stale role assignments still await cleanup.

## Migration Direction

BunkFy's current composition uses global Auth identities and no workspace-id login field. Any deployment upgrading legacy tenant-scoped Auth data still requires an explicit reconciliation path:

- preserve Auth member ids where possible so Staff subject links remain valid;
- preflight duplicate usernames and external identities across legacy tenant scopes;
- never auto-merge identities by matching email;
- require operator reconciliation for collisions;
- revoke legacy sessions during the profile transition;
- move retained accounts to the configured global Auth partition;
- verify the global membership path before removing legacy workspace-id login compatibility;
- document a reset-only path for disposable local/preview data separately from production upgrades.

The migration and the steady-state schema must be covered against PostgreSQL. Provider-specific migration code remains outside Auth domain/application behavior.

The deferred Staff claim withdrawal migration is appended as
`20261102220000_AddWorkspaceStaffDeferredClaimWithdrawals`, after the published
Workspaces recovery migration. Its earlier, unpublished PR identifier
`20260811044039_AddWorkspaceStaffDeferredClaimWithdrawals` was never part of the
canonical dev history. The application migration host requires the applied
history to be an exact prefix of its loaded catalogue. Keep that guard intact:
both fresh databases and the nonempty published history use the appended
migration; a database that applied the unpublished identifier requires a
separately inspected recovery path and is refused automatically. Do not rename,
insert or delete applied history rows to make admission pass. The new migration's
`Down` also refuses to discard nonempty durable withdrawal observations.

## Rejected Shapes

- Put workspace lifecycle in Tenancy: mixes domain registry behavior into scope plumbing.
- Put invitations in Auth: couples identity to one membership product and prevents independent reuse.
- Put memberships in AccessControl: conflates belonging with permission grants and weakens offboarding semantics.
- Put invitations in Staff only: prevents non-Staff owners/admins and reuse by other GMA products.
- Keep tenant-scoped accounts: duplicates identities, makes multi-workspace switching awkward, and leaks workspace ids into login.
- Trust the tenant header after global login: lets selection masquerade as authorization.
- Store roles in invitation JWTs: makes revocation, single-use claims, delegation policy, and server-side corrections unsafe.
- Treat reusable QR as a normal invitation: hides materially different risk and lifecycle rules.

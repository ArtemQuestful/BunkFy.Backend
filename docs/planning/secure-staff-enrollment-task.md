# Secure Staff Enrollment Task

Status: implementation, deployed API, and trusted-HTTPS Preview browser proof
complete; real-provider and hosted-orchestrator proof pending
Date: 2026-07-21
Updated: 2026-08-12

## Goal

Make single-use invitations and reusable link/QR enrollment safe for production without moving BunkFy Staff or access-profile concepts into GMA.

An authenticated applicant must provide a verified account identity and a proposed Staff profile before Organizations may create a membership. A pending or partially provisioned membership receives no BunkFy operation permission. Approval and automatic enrollment converge through the same replay-safe provisioning path.

## Ownership Boundary

### GMA Organizations

Organizations remains authoritative for:

- invitation and enrollment secrets, digests, expiry, revocation, rotation, capacity, and replay protection;
- recipient constraints and membership creation/restoration;
- pending claim, approval, rejection, and optimistic claim versioning;
- generic invitation, claim, and resolution events.

Organizations adds only an additive product admission hook. The hook receives opaque organization, invitation/link, claim, subject, and operation context and returns allowed, denied, or temporarily unavailable. It contains no Staff fields, profile names, property assignments, or BunkFy policy.

### BunkFy Workspaces

The BunkFy Workspaces product module owns durable coordination state:

- source kind and opaque Organizations source id;
- applicant Auth subject and preferred verified account email;
- proposed Staff profile while a decision is pending;
- claim correlation/version when approval is required;
- provisioning step, retry-safe Staff reference, failure code, and terminal audit facts.

Pending PII is redacted after rejection or successful provisioning. Long-term employment data remains in Staff.

### BunkFy Staff

Staff remains authoritative for the employment profile. It exposes a narrow idempotent onboarding provisioner through Contracts. The provisioner creates or safely resumes the Staff member for the accepted subject and rejects departed/conflicting records. It does not create memberships or AccessControl assignments.

### GMA AccessControl

AccessControl remains authoritative for roles and assignments. BunkFy grants its existing constrained member baseline only after Staff provisioning succeeds. Organization membership alone does not grant a BunkFy operation. Suspension/removal revokes access before cleanup is considered complete.

## Workflow

1. The browser previews the GMA token and removes it from reusable browser history.
2. After authentication, the applicant submits the original opaque token and a proposed Staff profile to the BunkFy endpoint. The server inspects the token through Organizations contracts and resolves the authenticated subject and preferred verified Auth email; browser-supplied organization, source, subject, or email values are never trusted.
3. The Organizations admission hook permits the exact invitation/link and subject only while the BunkFy application is ready. A restricted workspace denies the transition; unavailable authoritative state returns a retryable failure without mutation. Possession of the original token is still required by Organizations.
4. A manual claim becomes pending. Its integration event binds the Organizations claim id/version to the BunkFy process, and workspace owners read an enriched, minimal applicant summary from BunkFy.
5. Approval or automatic claim creates/restores membership in Organizations. No membership handler grants ordinary-member permissions.
6. The accepted invitation/claim event asks Staff to provision the exact applicant profile. Only after that succeeds does Workspaces install the constrained member baseline in AccessControl and mark the process complete.
7. Any failure before the assignment leaves the subject denied. A failure after assignment is replayed idempotently and converges to completed state.
8. Rejection records a terminal result and redacts proposed profile/contact data. Expired, disabled, rotated, capacity-exhausted, duplicate, and replayed tokens remain Organizations decisions.

Claim observations are monotonic. The accepted claim fact is stored before any
Staff or AccessControl work begins, stale requested/rejected deliveries cannot
regress a newer accepted version, and exact duplicates are idempotent. An owner
retry is valid only after an authoritative invitation or claim acceptance has
opened the provisioning path. A manual-approval profile becomes immutable when
the claim is requested, so approval cannot provision fields changed after owner
review.

## Security Invariants

- Browser-supplied role, permission, property, workspace membership, subject, and verified-email claims are never authority.
- Applicant endpoints are authenticated, no-store, rate-limited, and identity-bound.
- Owner review exposes only the proposed operational profile and preferred verified account email needed for the decision.
- Raw Organizations invitation/enrollment endpoints cannot bypass the BunkFy readiness policy.
- Exact retries by the same subject are idempotent; another subject cannot adopt an application.
- Active membership without a completed onboarding process has no ordinary BunkFy role.
- Owner provisioning and owner transfer remain separate governance paths.
- Event handlers may observe different module commit order and must therefore be replay-safe and fail closed.
- Operator retry cannot substitute for Organizations acceptance or provision a
  submitted/pending application.
- The Staff proposal reviewed for a pending manual claim is frozen until that
  claim reaches a terminal decision.

## Delivery Slices

1. Add and publish the generic GMA Organizations join-admission hook with unit and contract tests.
2. Add BunkFy durable onboarding state, PostgreSQL migration, applicant/owner APIs, and Staff provisioning contract.
3. Replace ordinary membership auto-provisioning with accepted-source orchestration and denied-by-default membership handling.
4. Align the web join and workspace-review flows with generated contracts.
5. Prove the persistence, policy, replay, partial-failure, and frontend continuation boundaries locally; complete the multi-account token lifecycle smoke in a deployed environment before launch.

## Deferred Follow-Ups

- Named seed access profiles and exact property assignment plans now replace the original single constrained member baseline. Product-facing plan summaries and management UX remain in the access-profile slice.
- Retention policy will set the bounded lifetime for terminal coordination audit rows; this slice redacts terminal applicant PII immediately.
- Natural source expiry needs one generic GMA Organizations follow-up.
  Organizations currently computes expiry during reads/admission and therefore
  has no terminal expiry event for consumers. GMA should own durable expiry of
  invitations/links and resolution of expired pending claims, then publish a
  versioned payload-free lifecycle fact. Workspaces can consume that fact to
  supersede its active plan/application and redact copied applicant PII. A
  BunkFy timer must not infer another module's authoritative state.
- Email delivery, reminder, and abuse-operations dashboards belong to deployment/notification work, not Organizations.

## Completion Evidence

- GMA tests prove the hook is absent-by-default, additive, receives sanitized authoritative context, and cannot mutate Organizations state after denial.
- BunkFy tests prove pending and failed workflows have no member role, successful retries create one Staff identity and one assignment, and rejection redacts PII.
- Focused Workspaces tests prove acceptance-before-request delivery, stale and
  duplicate claim handling, authoritative claim-version capture, and denied
  owner retry before source acceptance.
- PostgreSQL tests prove uniqueness, optimistic transitions, tenant isolation, restart recovery, and migration shape.
- API, application, and event-handler tests cover the admission, claim binding,
  denial, retry, and partial-failure boundaries. The deployed Preview rehearsal
  now proves invitation and QR token lifecycle behavior with separate accounts,
  durable broker delivery, approval/rejection, property isolation, replay, and
  cleanup on the hardened runtime.
- Architecture guards prove GMA has no BunkFy reference and BunkFy composition reaches Organizations through public contracts plus the explicit admission seam.

## Remaining Deployment Gate

The trusted-HTTPS Preview rehearsal now covers separate owner and applicant
accounts, browser continuation, invitation and QR rendering, pending denial,
and a guarded Preview Worker restart. Before production launch, repeat the
environment-owned portions against the hosted candidate: every enabled external
identity provider, real-provider mail delivery, hosted orchestrator restart,
and operator recovery. Expiry, capacity, rotation, replay, suspension, and
failed-provisioning recovery remain covered by deterministic repository tests
and must be sampled again where the private release policy requires it. This
evidence boundary is not a reason to move product policy into GMA.

# Workspace Invitation Access Plans Task

Status: backend, deployed API, and trusted-HTTPS Preview browser proof complete;
real-provider and hosted-orchestrator rehearsal pending
Date: 2026-07-21
Updated: 2026-08-12

## Goal

Let an authorized workspace manager attach a server-owned Staff/property/access plan to a named invitation or reusable enrollment link. Applicants may review the plan but can never author or broaden authority-bearing fields.

## Ownership

- Organizations owns invitation/enrollment token security, recipient constraints, acceptance, approval, expiry, revocation, rotation, and membership creation.
- AccessControl owns profiles and exact assignment reconciliation.
- Staff owns Staff profiles and property assignments.
- Workspaces owns BunkFy plan state, product validation, provisioning order, retries, and API DTOs.
- GMA Organizations needs a narrow Contracts issuance facade for generic invitation/enrollment creation. It must contain no Staff, property, profile, hostel, or BunkFy vocabulary.

## Plan Rules

- A plan is tenant-scoped and bound to one Organizations source id.
- It stores one active delegable profile id and zero or more active property ids. Reusable links are restricted to configured low-privilege profiles.
- The manager actor must currently possess every delegated permission and property-management authority.
- Applicants receive a sanitized read-only summary. Submission accepts only personal Staff fields.
- Missing, superseded, archived, cross-workspace, or no-longer-delegable plans fail closed.
- Acceptance without a valid plan may create Organizations membership, but membership alone grants no BunkFy operation. Provisioning remains blocked and recoverable.
- Ordinary invitations and reusable links can never grant owner or platform-administration authority.

## Delivery Shape

1. [Complete] Add the provider-neutral Organizations issuance facade with idempotency, recipient/expiry/approval constraints, and one-time token return semantics.
2. [Complete] Add Staff Contracts provisioning for an exact property-assignment plan.
3. [Complete] Persist Workspaces invitation/enrollment plans and bind them to Organizations source ids. Preparation and activation commit before Organizations may mint the one-time token, so there is no fallible BunkFy write after token issuance. An active plan without a corresponding Organizations source grants nothing and is safely retryable.
4. [Complete] Extend Staff onboarding to revalidate and apply Staff data, exact property assignments, the permission-free membership marker, and the selected profile. Missing, inactive, or superseded plans fail closed.
5. [Complete] Invitation and enrollment-link creation endpoints are tenant-scoped and require `staff.manage`. Product-facing list, revoke, replacement, and retry surfaces preserve the Workspaces access-plan boundary; delegated Organizations operations require active membership, operational workspace admission, `staff.manage`, profile visibility, and exact source or claim correlation. Generic raw issuance, reissue, and rotation remain owner-only. Workspace Settings exposes these flows to exactly the evaluated delegated capability without exposing raw Organizations administration.
6. Add sanitized applicant preview and approval summaries.

## Verification

- cross-workspace profile/property ids are rejected;
- actor anti-escalation is rechecked at creation and provisioning time;
- token issuance or plan persistence failure cannot grant product access;
- retry and broker redelivery do not duplicate Staff/property/profile assignments;
- revoked, rotated, expired, and superseded sources cannot provision;
- reusable-link maximum-use and approval races retain one authoritative outcome;
- deployed owner/applicant browser smoke covers password and external registration plus link and QR entry.

Local verification covers aggregate lifecycle and exact replay, owner and delegated-manager anti-escalation, low-privilege reusable-link restrictions, prepare/activate/issue ordering, one-time token replay, endpoint authorization metadata, Staff/property/access retry behavior, migration drift, a PostgreSQL upgrade from the prior Workspaces migration, and the delegated web capability matrix.

The root deployed invitation and QR enrollment verifiers now cover the public
API boundary with separate identities, property isolation, approval/rejection,
capacity, replay, and exact owner-visible access. The trusted-HTTPS Preview
browser rehearsal now also proves password registration and redirect
continuity, invitation and Team QR rendering, captured verification delivery
through private Mailpit, pending denial, and a guarded Worker restart on the
hardened runtime. Enabled external-provider behavior, real-provider delivery,
and hosted orchestrator control remain the explicit private release gates.

## Not In This Slice

- custom role editor UX, delivered separately by `workspace-access-administration-task.md`;
- ownership invitations;
- public guest access;
- adapter/service credentials;
- global Auth account administration.

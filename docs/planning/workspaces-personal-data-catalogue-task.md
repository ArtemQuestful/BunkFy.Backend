# Workspaces Personal-Data Catalogue Task

Status: published; local and exact-commit production proof complete

## Goal

Apply company-readiness control SP-001 to the Workspaces module without moving
product policy into GMA. The checked-in catalogue must make undocumented
personal data fail verification across Workspaces persistence, onboarding and
access-management contracts, and the Organizations-owned invitation boundary.

## Ownership Boundary

- Workspaces owns staff-onboarding proposals, verified-account email snapshots,
  Auth subject correlation, staff access-process history, access-plan actor
  attribution, and the Workspaces projections derived for access decisions.
- Staff remains authoritative for the durable employment profile after
  onboarding completes. Workspaces redacts the copied applicant profile when an
  application completes, is rejected, or is superseded.
- GMA Organizations remains authoritative for invitation and enrollment-link
  claims. Workspaces catalogues only the claim data it deliberately accepts,
  returns, or uses to prepare product access.
- GMA owns generic transport, tenancy, authorization, and invitation mechanics.
  Hospitality purposes, readers, retention keys, and rights behavior stay in
  BunkFy.

## Catalogue Coverage

The v1 catalogue must classify:

- every mapped member of `WorkspaceStaffOnboarding`,
  `WorkspaceStaffAccessProcess`, `WorkspaceStaffAccessProfileSnapshot`,
  `WorkspaceStaffAccessPlan`, and `WorkspaceStaffAccessPlanProperty`;
- onboarding profile fields, verified email, subject and actor identifiers,
  source/claim linkage, lifecycle state, bounded failure codes, and timestamps;
- application commands and queries, public API inputs, admin inputs, and public
  response DTOs that contain or link to a person;
- Organizations claim and source data crossing the Workspaces boundary;
- property/profile assignments only where they are linked to an identifiable
  staff member or invitation source.

Pure permission catalogue metadata, module metadata, property topology
projections, infrastructure inbox/checkpoint state, pagination counters, and
boolean command confirmations are outside the personal-data catalogue unless
they become linked to a data subject.

## Executable Guards

1. Every catalogue binding resolves to a real public member.
2. Every selected mapped persistence member is classified.
3. Every selected Workspaces command, query, API/admin request, response, and
   cross-module boundary member is classified.
4. Direct identifiers, contact data, free text, and structured payloads cannot
   enter integration events, notifications, logs, metrics, traces, or support
   bundles.
5. Terminal onboarding states continue to redact copied applicant profile data.
6. The generated Markdown inventory is deterministic and checked in.

## Verification

- Focused Workspaces catalogue and domain tests.
- Workspaces persistence and API security tests.
- Architecture and module-boundary tests.
- Solution synchronization, warning-free build, migration drift checks, and the
  complete non-Docker suite.
- Docker integration coverage and exact-commit CI before the slice is declared
  published.

Local evidence:

- 64 field definitions resolve 235 concrete bindings across five owned
  persistence types, public/application boundaries, admin responses, and five
  consumed cross-module event contracts.
- All 83 Workspaces tests and 58 architecture tests pass.
- The synchronized warning-free build, every PostgreSQL and GMA migration drift
  check, and all 2,312 non-Docker tests pass.
- All 33 Docker integration tests pass against the durable PostgreSQL, NATS,
  API, and Worker composition, including onboarding restart/convergence proof.
- The published backend commit `7bbbf034e27784a4cf7602c3145daa5db4a43cd0`
  passed its exact-commit GitHub validation workflow.

## Deferred

- SP-002 cross-module export, correction, restriction, and erasure orchestration.
- SP-003 automatic retention schedules, legal holds, and deletion evidence.
- Final legal approval of purposes, retention periods, controller allocation,
  and country-policy activation.

## 2026-08-25 Recovery-Receipt Amendment

Backend checkpoint 3 retains the append-only staff-onboarding recovery receipt
and accepted Organizations membership binding while tightening proof,
chronology, retry, and default-off admission. This local amendment does not
alter the historical published evidence above and is not yet exact-commit CI or
hosted proof.

- Every receipt actor role that can identify a person (applicant, plan creator,
  and recovery actor) is discoverable. Subject export minimizes cross-subject
  disclosure: an onboarding/Staff-linked selection includes only the
  applicant's raw subject binding, while a direct multi-role receipt selection
  nulls every raw actor field. This redaction is incomplete protection, not a
  production-ready Data Rights contract: the generic request is not bound to a
  verified requester identity and matched actor role. Full raw plan-creator and
  recovery-operator attribution remains in the internal receipt and authorized
  tenant-termination/controller export. The effective system actor is a
  non-personal constant.
- Request, target-set, Staff-state, Staff-property-set, and Access-facts hashes
  are classified as linked personal metadata. No raw property ids, permissions,
  profile PII, exception text, or tokens are stored in the receipt.
- Recovery receipts inherit onboarding retention and are exported before the
  existing tenant-termination onboarding-receipt destruction stage deletes and
  counts them.
- There is no historical membership-binding backfill. Legacy partial records
  remain `ReviewRequired`.
- Raw recovery-actor retention/pseudonymisation and verified subject/role-bound
  Data Rights selection/export are explicit production activation blockers.
  They are not solved by receipt field minimization or local export tests.

The implementation and catalogue tests are repository evidence only. SP-001,
SP-002, SP-003, and SP-008 still require an exact candidate, exact-commit CI,
hosted lifecycle proof, and company privacy/legal approval before production
activation; those evidence classes remain separate.

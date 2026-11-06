# Workspace Staff-Onboarding Recovery Audit Task

Status: backend checkpoint 3 implemented locally and default-off; not published, deployed, or production-admitted
Date: 2026-08-25

## Purpose

Provide a fail-closed, operator-attributed recovery attempt for a failed
Workspaces staff-onboarding application only when current Organizations, Staff,
and Access Control facts prove the intended result already exists exactly. The
recovery endpoint never creates, reconciles, revokes, or broadens Staff,
membership, role, or profile state.

The proof is observational across three independently committed modules. It is
not a distributed transaction and must not be described as one. A successful
attempt collects `O1 -> S1 -> A1 -> O2 -> S2 -> A2`: each Organizations,
Staff, and Access Control proof is internally revision-fenced, each second
proof is `Equivalent`, and each second revision, digest, and exact fact set
matches its first proof. Those unchanged intervals overlap between A1 and O2,
which establishes a common observational interval; they do not freeze live
authority after A2. Concurrent, unavailable, or drifted facts return
`RetryLater` or `ReviewRequired` while leaving the application unchanged.

## Exact-Recovery Contract

- The application is a failed partial with a persisted Staff member id and the
  Organizations membership id/version accepted during its original processing.
- Organizations is open and the same subject has the same active `Member`
  membership id/version in O1 and O2. The second observation must match the
  first revision and complete fact set. The revision is proof-fence evidence,
  not a value that live authority must retain after the attempt.
- Staff proves, through no-tracking projections and a stable tenant-revision
  fence, the exact successful onboarding operation, active non-anonymised and
  unrestricted member, subject, and current property set. The original request
  fingerprint remains inside Staff and is included only in the proof digest.
- Access Control returns a read-only, management-revision-fenced v2 snapshot
  bound to the subject, workspace root, and a bounded exact target-scope set.
  Lifecycle evaluation covers global/root ancestors and every exact target's
  ancestors; unrelated descendants and malformed unrelated lifecycle rows do
  not close the requested proof. Grant collection still retains every relevant
  global, ancestor, root, target, and descendant fact. The only role grant is
  the permanent, permissionless workspace membership marker. Profile grants
  match the current active plan profile id, key, version, permissions, and
  exact workspace/property targets, with no global, ancestor, descendant,
  legacy, custom, support, provisioner, owner, missing, or extra grant.
- "Profile version" means the current profile version verified at recovery
  time. Workspaces does not persist the profile version issued with historical
  access plans, so this is not proof of an issuance-time version.
- Only this exact stable state may complete and redact the application. The
  decision/completion time and receipt `RecordedAtUtc` are captured after A2.
  Any partial or drifted state stays failed for manual review; there is no
  Staff/Organizations/Access mutation, grant change, broad backfill, or
  destructive reconciliation. Live authority remains authoritative after the
  observational proof ends.

Legacy partial applications without the accepted membership binding are not
guessed or backfilled. The old retry endpoint rejects partial failed records
with a Staff member id, preventing a mixed-version client from bypassing the
receipt path.

## Receipt And Idempotency

`POST /api/workspace-staff-enrollment/applications/{id}/recovery-attempts`
accepts an operation id and expected application version. The recovery actor is
resolved server-side. A tenant/operation transaction lock precedes source and
application locks. The same operation and request fingerprint replays the
stored terminal response without Organizations, Staff, or Access reads; this
same-id replay is for an unknown transport outcome. Changed reuse is an
idempotency conflict. When a caller has received a `RetryLater` response, the
response carries `RetryDirective=NewOperationAfterDelay`,
`RetryAfterSeconds=5`, and HTTP `Retry-After: 5`; after the delay the caller
must use a fresh operation id with the still-current application version.

`Recovered`, `ReviewRequired`, and `RetryLater` outcomes are append-only
Workspaces receipts. Review and retry-later receipts commit while the application
status, version, and failure remain unchanged. The public response and latest
attempt query expose only the opaque operation reference, bounded outcome and
reason, application status/version, and recorded time. Receipts store no names,
emails, phones, raw property ids, permission lists, exception text, tokens, or
credentials. Hashes are linked personal metadata. Domain and PostgreSQL
constraints require expected version = selected version, selected status =
`Failed`, compatible outcome/reason/result status and version, compatible stage
verification and proof presence, and every observed time <= `RecordedAtUtc`.
Unobserved legacy/restricted stages remain `Unknown` and carry no invented
source timestamp.

Recovery telemetry uses meter
`bunkfy.workspaces.staff_onboarding_recovery` and counter
`bunkfy.workspaces.staff_onboarding_recovery.outcomes`. Its only dimensions are
the bounded `outcome`, `reason`, and `replay` values; tenant, subject, actor,
property, token, operation id, and raw exception values are prohibited.

## Privacy And Lifecycle

Recovery receipts inherit staff-onboarding retention. Data Rights discovery
finds the receipt for the applicant, plan creator, and recovery actor. The
subject export is deliberately minimized because the generic export request
does not carry the verified discovery identity: an onboarding/Staff-linked
selection includes the applicant's own subject binding but nulls the plan
creator, recovery operator, and system actor; a direct receipt selection nulls
all raw actor identifiers. The full attribution remains in the internal audit
receipt and authorized tenant-termination/controller export. Raw actor
retention/pseudonymisation is not yet lifecycle-complete, and the generic Data
Rights selection/export path is not yet cryptographically bound to the verified
requester and matched actor role. Both are production activation blockers;
field redaction alone is not proof of requester-authorized disclosure.
Tenant-termination
export includes receipts before the existing onboarding-receipt destruction
stage deletes and counts them. The append-only table may be deleted only by the
governed tenant lifecycle; its migration downgrade refuses when receipts or
membership bindings exist.

Final lawful-purpose, retention, correction limitation, and jurisdictional
approval remain company/legal evidence, not a repository claim.

## Production And SP Impact

This checkpoint changes the evidence surface for SP-001, SP-002, SP-003,
SP-006, SP-007, SP-008, SP-009, SP-011, SP-012, and SP-013. Repository
implementation, local tests, exact-commit CI, hosted proof, and operational,
security, privacy/legal, support, backup/restore, monitoring, rollout, and
rollback evidence are separate gates.

`BunkFy:WorkspaceStaffOnboardingRecovery:PostEnabled` defaults to `false`.
When disabled, POST returns a no-store 404 before actor resolution or dispatch;
the authorized latest-receipt GET remains available. The public product
capability `WorkspaceStaffOnboardingRecoveryEnabled` is derived directly from
that server option so a future connected UI must require an explicit `true`
before exposing recovery. The current Production host refuses startup when the
option is true while the two privacy blockers remain open.

Roll out only as expand -> disabled deploy -> exact migration and release
composition verification -> isolated QA. Production activation is prohibited
until the privacy blockers, authorized opaque-operation lookup/support runbook,
monitoring, and admission evidence close. Rollback disables POST while retaining
the additive schema, immutable receipts, and latest GET. Do not remove the
table, downgrade through non-empty data, or backfill historical membership
bindings.

Required remaining admission evidence includes exact-commit CI, hosted API and
Worker composition, migration plan/apply/restore proof, alert and support
runbooks for each bounded reason, Data Rights and tenant-termination rehearsal,
legal retention approval, activation authorization, and a rollback exercise.

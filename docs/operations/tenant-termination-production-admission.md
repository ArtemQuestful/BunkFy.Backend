# Tenant Termination Production Admission

This runbook admits BunkFy's destructive tenant-termination Worker. It does not
approve a termination case, expose an operator command, replace a backup, or
prove that an external protected replay provider is durable.

Repository defaults are deliberately blocked:

- `ExecutionEnabled` is `false`;
- approval and evidence references are absent;
- the owner-catalogue digest is absent; and
- the local encrypted replay provider is development-only and rejected in
  Production.

The tenant-termination operator permissions, Admin API, Admin CLI, bounded
status, and exact-intent recovery controls are complete. Do not enable
execution in Production until the private approval package, external replay
provider, key material, backup/restore evidence, and exact candidate catalogue
have all been admitted under this runbook.

## Required Candidate

The admitted Worker must compose:

- Data Rights and Task Runtime;
- all 12 required BunkFy owner modules;
- `Tasks:Worker:Enabled=true`; and
- the `tenant-termination-workers` task group.

Startup recomputes the complete contributor and export catalogue, validates its
dependency graph and unique terminal Workspaces owner, and compares its exact
SHA-256 with the approved digest. Optional contributor drift changes the digest
and blocks startup too.

Production must register an external `ITenantTerminationReplayStore` whose
readiness reports both ready and production grade. The Data Rights replay
envelope and provider admission remain independently fail closed; local files
and the repository development key are not Production evidence.

Apply the Data Rights PostgreSQL migrations before starting the candidate.

## Approval Evidence

Keep a private approval package containing:

- exact source commit and deployable artifact;
- exact owner-catalogue SHA-256 captured from the candidate composition;
- approved termination procedure and rollback owner;
- backup identity and restore point;
- successful restore-drill evidence; and
- operator assurance and review evidence.

Configuration references are bounded evidence identifiers. They must not
contain tenant identifiers, names, descriptions, credentials, URLs with
tokens, or other secrets.

## Configuration

Set the declaration only on the dedicated execution Worker:

```text
BunkFy__TenantTermination__ProductionAdmission__ExecutionEnabled=true
BunkFy__TenantTermination__ProductionAdmission__ApprovalState=Approved
BunkFy__TenantTermination__ProductionAdmission__ApprovalReference=<evidence-id>
BunkFy__TenantTermination__ProductionAdmission__OwnerCatalogSha256=<approved-lowercase-sha256>
BunkFy__TenantTermination__ProductionAdmission__BackupEvidenceReference=<evidence-id>
BunkFy__TenantTermination__ProductionAdmission__RestoreDrillEvidenceReference=<evidence-id>
BunkFy__TenantTermination__ProductionAdmission__OperatorAssuranceReference=<evidence-id>
Tasks__Worker__Enabled=true
Tasks__Worker__WorkerGroups__7=tenant-termination-workers
```

The group may use another array index when deployment configuration replaces
the whole list. It must occur exactly as a configured Worker group.

Configure `DataRights:TenantTerminationReplay` with the registered external
provider and configure non-development replay-envelope key material through the
deployment secret store. Admission validates the live implementation rather
than trusting configuration text alone.

## Startup Evidence

An admitted Worker emits one bounded startup record with:

- owner and export-owner counts;
- terminal owner key; and
- exact catalogue SHA-256.

The record contains no tenant or case data. Missing admission, catalogue drift,
an incomplete module topology, a missing task group, or a non-production replay
store blocks startup.

## Pause And Recovery

If catalogue, replay, key, backup, restore, or operator evidence becomes
uncertain:

1. redeploy with `ExecutionEnabled=false`;
2. preserve PostgreSQL, protected replay, logs, and deployment evidence;
3. do not edit process, work-item, receipt, task, or replay rows manually;
4. inspect the authorized recovery/status surface; and
5. repeat catalogue, restore, and admission proof before resuming.

Disabling execution prevents new destructive work from being admitted. It does
not reverse owner work already protected by the replay journal; recovery must
converge that recorded operation before another termination may proceed.

## Shared-station ownership amendment (28 September 2026)

A Stations-enabled candidate must add its actual owner/catalogue entries and
prove export/classification followed by dependency-safe destruction or approved
irreversible severance of registrations, station/browser/actor sessions, staff
credentials/grants, setup grants, operation/activity receipts and lifecycle
state. Include the Reservations station-attribution child. Prove retry and
absence after completion; do not assume cascade or ordinary reservation export
provides this coverage. Staff provenance must stay out of guest exports.
This is an unresolved activation gate, not a claim that owner implementation
or hosted termination proof exists. See
[Shared-station PIN admission](stations-production-admission.md).

## Staff-Onboarding Recovery Receipts

The Workspaces termination export schema includes staff-onboarding recovery
receipts before destruction. The existing onboarding-receipt destruction stage
then explicitly deletes and counts those receipts; no stage is renumbered and
foreign-key cascade alone is not accepted as export/destruction evidence.

Any candidate containing this schema must recompute and approve the exact owner
catalogue, rehearse export completeness and destruction counts, and retain the
protected export before activation. Recovery receipt hashes are linked personal
metadata, and applicant, plan creator, and recovery actor identifiers remain
subject to the documented Data Rights and retention policy. Repository tests do
not establish external replay durability, lawful retention approval, or a
hosted termination rehearsal. Export/destruction coverage also does not solve
raw recovery-actor retention/pseudonymisation or bind a Data Rights release to
the verified requester and matched actor role. Those remain recovery-feature
activation blockers even after a termination rehearsal passes.

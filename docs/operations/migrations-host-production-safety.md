# Migrations Host Production Safety

## Guest language collection rollout qualification

The additive Guest language migration retains legacy scalar bytes and old correction receipt meanings. Enable collection-writing clients only after a homogeneous compatible API/worker release and migration have been verified. Mixed old API peers ignore the new JSON collection. Web rollback comes first; keep the additive schema and collection-aware backend and forward-fix it. Old scalar writes can diverge from the collection, and old anonymisation cannot clear it. A backend rollback therefore needs separately approved write suspension and tested restore/repair evidence, not merely a retained column. See [the bounded contract task](../planning/guest-multilanguage-contract-task.md). No local test or preview receipt grants production Apply admission.

`BunkFy.Host.Migrations` is the only BunkFy executable that advances the
product's PostgreSQL schemas. Module assemblies still own their migrations; the
host owns cross-module deployment safety.

## Modes

`Migrations:Mode` accepts:

- `Plan`: acquire the product migration lock, inspect every module, validate
  history compatibility, report hashes/counts, and exit without mutation;
- `Apply`: perform the same locked inspection, apply pending migrations in the
  fixed product order, verify every module reached its target, and exit.

Development defaults to `Apply` for Aspire/local startup. Production `Apply`
fails closed until its admission is complete. Use `Plan` first for every
Production target.

## Plan Evidence

A Production plan requires the exact deployment profile, runtime, source commit,
and a non-secret database identity. A container also requires its immutable OCI
digest. The plan reports only non-secret evidence:

- SHA-256 of the configured non-secret database alias, without logging the
  operator-facing alias itself;
- database-target SHA-256, derived from the connected data source and database
  name without logging either value;
- target-catalogue SHA-256 over the fixed module order and all loaded migration
  identifiers;
- current-state and pending-plan SHA-256 values;
- per-module target, applied, and pending counts.

The target-catalogue digest is stable while the same release advances or resumes.
The state and pending digests change as migrations commit.

Example environment keys:

```text
DOTNET_ENVIRONMENT=Production
Migrations__Mode=Plan
Migrations__ProductionAdmission__DeploymentProfile=Hosted
Migrations__ProductionAdmission__Runtime=Container
Migrations__ProductionAdmission__SourceCommitSha=<exact-lowercase-commit>
Migrations__ProductionAdmission__ContainerImageDigest=sha256:<exact-image-digest>
Migrations__ProductionAdmission__DatabaseIdentity=prod-primary-eu1
ConnectionStrings__PostgreSql=<secret-provider-value>
```

Do not store the connection string or real infrastructure identifiers in this
repository.

## Apply Admission

Run `Apply` with the same immutable artifact and target after retaining the plan,
review, backup/restore, and rollback/forward-repair evidence. Production requires:

```text
Migrations__Mode=Apply
Migrations__ProductionAdmission__ApprovalState=Approved
Migrations__ProductionAdmission__ApprovalReference=<non-secret-change-reference>
Migrations__ProductionAdmission__ApprovedDatabaseTargetSha256=<plan-database-hash>
Migrations__ProductionAdmission__TargetCatalogVersion=1
Migrations__ProductionAdmission__ApprovedTargetCatalogSha256=<plan-target-hash>
Migrations__ProductionAdmission__BackupEvidenceReference=<non-secret-evidence-reference>
Migrations__ProductionAdmission__RollbackEvidenceReference=<non-secret-evidence-reference>
Migrations__ProductionAdmission__ExistingHistoryDisposition=ApplyCompatiblePrefix
```

The release identity and database identity required for `Plan` remain required
for `Apply`. Evidence references are identifiers, never credentials or evidence
payloads.

## Existing Inventory convergence history

The published `20260812082324_AddInventoryManualBlockGroupConvergence`
migration sorts before the two canonical Inventory integrity migrations dated
21 August. A database that already applied the complete 15-migration Inventory
catalogue from `315acadb49e4842ed63529a26bb47efb6643bfc9` consequently needs
an explicit, qualified history transition. The ordinary exact-prefix mode
refuses this shape before applying any module. Direct EF upgrade evidence
alone does not qualify the application migration host.

For this transition, select the following disposition for both `Plan` and
`Apply` on the migration job:

```text
Migrations__ProductionAdmission__ExistingHistoryDisposition=ApplyInventoryConvergenceV1
```

The host recognizes only the exact canonical `inventory` module name, all 15
published predecessor IDs in their original order, and the exact 16-ID target
catalogue. Its pending plan contains only the convergence migration and reports
`inventory-convergence-v1`. Missing or unknown history, another module, reordered
IDs, or a different target catalogue still fail before mutation. The host does
not edit, rename, delete, or synthesize any previously applied history record.

Use the same immutable release and database target throughout the operation.
Quiesce API, Worker, CLI and maintenance writers; rehearse against an isolated
restored database and retain actual backup/restore and forward-recovery
evidence. Production still requires every release, target, approval, backup and
rollback admission value above. Selecting this disposition supplies no approval
and changes no runtime-host configuration or access-profile assignment.

After `Apply`, verify the target digest is unchanged, no migrations remain
pending, old history records and retained block rows are preserved, both
canonical integrity constraints remain, and the unrelated tenants' revisions
are unchanged. Reacquire affected tenants' export coordinates. Repeating
`Apply` reaches the same complete history without further work. Return to
`ApplyCompatiblePrefix` for ordinary future releases. If a later Inventory
catalogue needs this old history reconciled, first use the qualified 16-ID
transition release; this fixed adapter does not admit arbitrary future gaps.

## Shared-station PIN qualification (28 September 2026)

Select `Stations:Http:Enabled=true` explicitly in the migration executable to
compose Stations and its PostgreSQL assembly, while leaving serving APIs
disabled during expansion. Plan must include the Stations catalogue and the
Reservations attribution migration. Verify exact hashes, backup/restore,
constraints, indexes and applied history before enabling runtime HTTP.
After use, roll back feature entry points, not durable credentials or audit
schemas. Restored device authority must fail closed under an advanced external
epoch. Pepper-version and Data Protection continuity are separate required
proofs. See [Shared-station PIN admission](stations-production-admission.md).
Local migration tests are not target-specific Apply admission.

## Concurrency And Recovery

One session-scoped PostgreSQL advisory lock covers inspection and all module
migrations. A competing host waits only for the configured acquisition budget.
The host explicitly unlocks the exact key before returning a pooled connection;
physical session termination remains PostgreSQL's fallback after process or
connection failure.

Each module migration commits independently. There is deliberately no fictional
cross-schema transaction. If a run stops after earlier modules commit, rerun the
same approved release and configuration. Exact-prefix validation accepts that
forward progress and applies only the remaining migrations.

Never respond to a partial run by starting an older migration image. Unknown,
reordered, or ahead-of-release history fails before mutation. Use the approved
forward repair or restore procedure when the same release cannot continue.

## Budgets

- `LockAcquireTimeoutSeconds`: total wait for the product advisory lock;
- `LockRetryDelayMilliseconds`: bounded retry interval;
- `OperationTimeoutSeconds`: total process operation budget;
- `CommandTimeoutSeconds`: per-database-command budget.

SIGTERM/host shutdown and the total operation budget cancel inspection and EF
migration work. Raising a budget requires an explicit deployment configuration
change; it does not weaken catalogue or target admission.

## Private Evidence

Before promotion, private operations still owns:

- exact artifact signature/attestation and source-to-image provenance;
- plan log and approval record;
- target-specific backup and tested restore evidence;
- rollback or forward-repair decision and owner;
- maintenance-window, replica-drain, compatibility, and post-deploy checks.

This repository does not prove those external controls merely because their
references pass startup validation.

## Staff-Onboarding Recovery Expansion

The Workspaces recovery-audit migration is additive: it adds nullable accepted
membership coordinates and an append-only recovery-receipt table. It does not
backfill historical applications. Its guarded downgrade refuses when either a
receipt or a membership binding exists, because silently discarding audit or
authority evidence is not an acceptable rollback.

The unpublished pre-release migration was replaced locally before any hosted
upgrade evidence existed. Its database constraints enforce the same recovery
truth table as the domain: expected version equals selected version; selected
status is `Failed`; outcome/reason/result status and version are compatible;
Organizations, Staff, and Access verification stages have compatible proof
presence; unobserved stages carry no invented observation; and every observed
time is no later than `RecordedAtUtc`. This local replacement is not hosted
migration history and authorizes no historical membership-binding backfill.

Deploy in this order: expand -> keep the recovery endpoint disabled -> run the
exact `Plan`/`Apply` and post-migration model/constraint verification -> verify
the exact API/Worker release composition -> isolated QA. The switch is
`BunkFy:WorkspaceStaffOnboardingRecovery:PostEnabled`; Production currently
refuses startup when it is true while privacy blockers remain open. A rollback
disables POST and retains the schema, receipts, and latest-receipt GET; it must
not run the guarded downgrade against populated data. Capture backup/restore
and forward-repair proof for the exact candidate. Local migration tests are
repository evidence, not proof that a hosted database has been migrated safely.

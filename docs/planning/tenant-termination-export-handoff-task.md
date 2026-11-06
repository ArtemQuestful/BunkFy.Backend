# Tenant Termination Export Handoff Task

Status: complete
Date: 2026-08-15

## Goal

Make an export-requested tenant termination stop at a durable, reviewable
handoff before irreversible destruction. An authorized operator must be able
to inspect status, download the exact protected export, and explicitly confirm
the frozen export proof before destruction can begin.

This slice also closes two recovery gaps discovered in that path: expired
tenant-export objects have no cleanup or regeneration path, and protected
start recovery currently requires the original executor to remain available.
It does not enable production tenant termination, alter owner destruction
contracts, or weaken the existing production-admission gate.

## Ownership

- Data Rights owns the tenant-termination process, frozen export proof,
  explicit destruction gate, protected export lifecycle, and bounded operator
  contracts.
- Each contributing module continues to own its export fragment contents,
  schema, proof revision, and destructive mutation.
- GMA Administration owns generic authenticated operation execution,
  permissions, recent-authentication assurance, and operation audit.
- GMA Task Runtime owns generic scheduling, retry attempts, leases, and task
  execution. Existing framework contracts are sufficient.
- Protected object storage remains behind the Data Rights persistence adapter;
  storage keys, encryption material, and fragment payloads never enter admin
  status contracts.
- No BunkFy tenant-termination phase, permission, artifact type, or recovery
  rule belongs in GMA.

## Findings

- Completing tenant export generation immediately calls `ConfirmExport` as
  `system:tenant-termination` and enqueues reconciliation. A completed export
  can therefore advance into destruction without operator review.
- Re-entering the artifact-generation task repeats that implicit confirmation,
  so removing only the completion call would not close the gate.
- Admin status exposes only the case, process, and owner work. There is no
  bounded artifact receipt, download operation, or explicit export-confirm
  operation in the Admin API or CLI.
- Tenant export artifacts and fragments model expiry and deletion states, but
  no task schedules or executes their protected-object cleanup.
- Tenant-scoped cleanup would be canceled and removed when the Task Runtime
  termination owner closes that tenant scope. A future expiry task must
  therefore survive as bounded global control-plane work or protected objects
  can be stranded after tenant destruction.
- Once an artifact or pre-assembly fragment expires or fails, the current
  process has no bounded way to create a fresh export operation without
  restarting unrelated lifecycle work.
- Protected start recovery compares the current recovery actor with the
  original intent executor. This prevents an authorized successor operator
  from restoring or re-signalling an otherwise valid execution.

## Decisions

### Explicit irreversible gate

Artifact generation marks the assembled artifact `Available` and stops. It
must not confirm the process or enqueue destruction. Reconciliation may
request missing artifact generation, but an available unconfirmed artifact is
a quiescent operator-wait state.

Confirmation is a separate Admin API and Admin CLI operation with its own
permission. It requires:

1. the exact tenant, case, process, artifact, and export operation;
2. expected process and artifact versions;
3. the exact frozen-revision and fragment-set SHA-256 values shown in status;
4. an available, unexpired artifact whose protected coordinates still match
   the process; and
5. an explicit confirmation flag at the adapter boundary.

The authenticated operator becomes `ExportConfirmedBy`. The system executor
is rejected from this operator command. Successful confirmation emits one
durable coordination signal; only then may normal reconciliation complete the
Export phase and begin Destroy.

### Bounded status and download

Operator status adds one current export handoff receipt containing only:

- artifact id, state, operation revision, version, availability and expiry;
- fragment and record counts;
- frozen-revision and fragment-set digests; and
- confirmation state and bounded confirmation attribution.

It never exposes storage keys, plaintext hashes, encryption-key versions,
tenant identifiers, fragment paths, or exported owner records.

Download is a second independently authorized operation. The application
revalidates tenant scope, case/process/artifact coordinates, current proof,
state, and expiry before opening and cryptographically verifying the protected
object. The Admin API streams a ZIP attachment with no-store, no-sniff,
sandbox, same-origin, and no-range protections. The CLI writes atomically to
an explicit existing directory and refuses overwrite unless requested.

### Expiry, deletion, and regeneration

Artifact and fragment creation schedule deterministic global cleanup tasks at
their own immutable expiry timestamps. Each bounded payload carries the exact
tenant, process, object, operation-revision, and expiry coordinates. The
BunkFy Worker establishes tenant context only for those exact global Data
Rights registrations and rejects scoped or mismatched leases. This lets the
retention task survive Task Runtime tenant-scope closure without weakening
ordinary tenant task admission.

Cleanup performs a database transition before object deletion and a terminal
transition afterward, making task retries idempotent even when the object is
already absent. Retention validates each old object against the process's
immutable frozen proof, not the process's current phase revision, so cleanup
continues after regeneration, cancellation, or progression into destruction.

If the current unconfirmed artifact expires while Export is running, cleanup
blocks the process with the stable `export-artifact-expired` outcome before
deleting the object. Fragment cleanup does the same with
`export-fragment-expired` when no usable assembled artifact exists. The
existing tenant-termination retry operation gains one specific recovery branch
for failed or expired current export material. It moves Export back to
`Pending`; normal phase start then increments the operation revision and
regenerates fresh owner fragments and an artifact from the frozen process
proof. It does not replay Freeze or any destructive work.

A protected fragment or artifact generation failure is recoverable through
the same explicit branch. Active generation and still-valid artifacts remain
ineligible, preventing duplicate export operations.

### Operator succession

The protected replay intent remains authoritative for the original execution
actor and reconstructed `CreatedBy` attribution. A current recovery operator
must still be a valid authenticated actor and is audited by the Admin API or
CLI operation runner, but does not have to equal the original executor.

Recovery cannot alter the tenant, case, process, approval evidence, catalog
digest, epoch, idempotency key, export choice, or original execution time.
Existing-process recovery and missing-process reconstruction both validate
against the same protected intent.

## Efficiency

- Status adds one indexed artifact lookup by tenant, process, and export
  operation; it does not enumerate artifact history or task history.
- Download opens one exact artifact and one exact process, then streams through
  the existing chunked protected-object reader and temporary-file limits.
- Confirmation acquires the process row once and reads one indexed artifact;
  no owner records or fragments are loaded.
- Cleanup uses one deterministic task per protected object. It derives storage
  keys from trusted aggregate coordinates and never scans an object prefix.
  The global task contains only bounded coordinates and restores tenant
  context at execution; it does not retain tenant-owned Task Runtime state.
- Explicit regeneration reads at most the bounded current fragment set only
  when the current artifact is not itself recoverable.
- Export regeneration creates a new operation revision and bounded owner work
  set only after explicit retry; there is no polling or automatic regeneration
  loop.

## Delivery

1. [x] Remove both implicit confirmation paths and keep available artifacts
   quiescent.
2. [x] Add bounded status, protected download, explicit confirmation, and
   separate administration permissions in the application, Admin API, and
   Admin CLI.
3. [x] Add deterministic artifact and fragment cleanup tasks with idempotent
   protected-object deletion and lifecycle audit.
4. [x] Add the expired/failed export regeneration branch to the existing retry
   operation.
5. [x] Allow authorized successor recovery while preserving original intent
   attribution and immutable coordinates.
6. [x] Add focused domain, application, adapter, persistence, and task tests;
   regenerate contracts only if the external API contract changes.
7. [x] Run one complete non-Docker backend gate and one PostgreSQL migration
   scenario at the coherent slice boundary, then rebuild Preview from the
   exact candidate.

## Verification Cadence

Use focused Data Rights tests while editing. Do not run Docker or the complete
repository gate after each change. At the coherent slice boundary, run the
full non-Docker backend gate once. The export-audit scope constraint requires
one additive migration so tenant-termination lifecycle facts can be stored;
run the Data Rights PostgreSQL scenario once to verify upgrade compatibility.
GitHub Actions remain publication evidence, not an inner development loop.

## Verification

- `pwsh -NoProfile -File eng/verify.ps1 -SkipRestore` passed on 2026-08-20:
  solution and package checks, clean build, all migration drift checks, and
  5,537 non-Docker tests passed with zero failures.
- The focused PostgreSQL migration scenario
  `TenantTerminationPersistenceIntegrationTests.Migration_fences_one_active_process_and_exact_owner_coordinates`
  passed once against its Docker test container (1/1).
- Preview is rebuilt and smoke-tested from the exact root candidate before
  publication; GitHub validation is observed after publication.

## Deferred

- Production activation and private replay-provider admission.
- A product-facing workspace UI for tenant termination; this remains a
  privileged Admin API and CLI workflow.
- Long-term archive transfer, legal custody, or a retention period beyond the
  configured protected-export lifetime.
- Historical tenant-export listing or bulk download.
- Cross-tenant or organization-level termination orchestration.

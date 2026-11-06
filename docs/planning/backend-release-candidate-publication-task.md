# Backend Release-Candidate Publication Task

Status: published; exact backend, web, and product candidate verification complete
Date: 2026-08-05

## 2026-09-05 unpublished retirement-context candidate amendment

The [retirement context-read task](inventory-retirement-context-read-task.md)
adds exact-target read contracts and shared inline web recovery. Prior published
commits and CI below do not cover this dirty successor candidate. Record exact
backend/web diffs, generated OpenAPI/client, targeted/full checks, PostgreSQL
index/plan evidence and preview fingerprint separately. Additive backend-first
rollout and web rollback preserve all retirement history; hosted/support/
monitoring/backup/privacy/legal and exact-commit CI approval remain pending.

## Scope

Publish the completed backend and reusable GMA slices as one coherent
source-first graph. Product behavior stays in BunkFy; only reusable mechanics
and generic module capabilities are published from GMA repositories.

This task does not claim that a hosted deployment is production-ready. Private
infrastructure, secrets, legal policy, external observability, backup/restore,
and deployed multi-account evidence remain deployment-owned.

## Local Candidate Evidence

- Operations Notifications contributor and lifecycle coverage: 99 tests pass.
- `eng/verify.ps1 -SkipRestore`: synchronized solution and source packages,
  zero-warning build, zero migration drift, and 4,377 non-Docker tests pass.
- Repository security and release-evidence checks pass.
- NuGet audit reports zero projects with vulnerable direct or transitive
  packages.
- `git diff --check` passes for BunkFy Backend, each changed GMA repository,
  and GMA-Skeleton. Existing line-ending normalization notices are non-errors.
- Every local repository base matched its remote `dev` head before publication
  preparation.
- The exported BunkFy source set resolves every reusable dependency to a clean,
  published commit; only the expected uncommitted BunkFy root is dirty before
  its candidate commit.

The changed persistence paths already have exact relational evidence in their
own slice ledgers. The final compatibility and documentation edits do not add a
new persistence behavior, so another full Docker run would duplicate evidence
without reducing risk. Exact candidate CI remains required after publication.

## Exact Candidate Evidence

- BunkFy Backend `9b776a5` passed
  [validation](https://github.com/SadPossum/BunkFy.Backend/actions/runs/31026806385),
  [security](https://github.com/SadPossum/BunkFy.Backend/actions/runs/31026806198),
  [Docker integration](https://github.com/SadPossum/BunkFy.Backend/actions/runs/31046144477),
  and [release evidence](https://github.com/SadPossum/BunkFy.Backend/actions/runs/31046151810).
- BunkFy Web `e09f4e6` passed
  [validation](https://github.com/SadPossum/BunkFy.Web/actions/runs/31047540673)
  and [security](https://github.com/SadPossum/BunkFy.Web/actions/runs/31047540850).
  Its generated OpenAPI contracts match backend `9b776a5`; the local coherent
  gate passed type checking, lint, 150 tests, and the production build.
- BunkFy root `74a334e` records those exact backend and web commits and passed
  [validation](https://github.com/SadPossum/BunkFy/actions/runs/31047724330),
  [security](https://github.com/SadPossum/BunkFy/actions/runs/31047724323),
  [CodeQL](https://github.com/SadPossum/BunkFy/actions/runs/31047725182),
  [source release evidence](https://github.com/SadPossum/BunkFy/actions/runs/31047867616),
  and [unpublished OCI image evidence](https://github.com/SadPossum/BunkFy/actions/runs/31047869364).
  The image workflow built both candidates, generated SBOMs, scanned them, and
  attested the closed evidence set; it did not publish or deploy an image.

## Publication Order

1. [x] Publish GMA Framework at `0d84c22`.
2. [x] Publish Access Control `32b33cb`, Administration `24803d7`, Auth
   `7d67e11`, Files `a861666`, Notifications `1511fad`, Organizations
   `2e97b22`, Task Runtime `930e6a8`, and Tenancy `039dd5d`.
3. [x] Publish GMA-Skeleton at `3c77808` against that exact reusable source set;
   its generated-selection matrix, zero-warning build, migration drift, and
   2,385 non-Docker tests pass.
4. [x] Publish BunkFy Backend `9b776a5` and BunkFy Web `e09f4e6`, then collect
   their exact GitHub Actions evidence.
5. [x] Publish product root `74a334e` with those exact pointers without staging
   or rewriting the user-owned root solution, README, or private
   company-readiness material.

Use GitHub Actions once per exact candidate. Do not use remote CI as an
edit-by-edit development loop.

## Publication Acceptance

- Each source repository is clean, based on its current remote `dev`, and has
  security/release checks green at the exact commit.
- GMA-Skeleton generates and builds every supported selection against the
  published source set, including Notifications Admin CLI composition.
- BunkFy Backend records only published submodule commits and passes validation
  plus the exact Docker workflow at its candidate commit.
- The product superproject records the published backend and web candidates
  without unrelated root-file churn.

## Deployment-Owned Follow-Ups

- hosted exporter destination, access, retention, and canary evidence;
- durable-runtime and tenant-termination production activation approvals;
- deployed owner/applicant multi-account onboarding and access-profile smoke;
- production migration plan/apply, edge, object-store, key continuity, backup,
  restore, alert routing, and rollback evidence; and
- market-specific legal and retention policy approval.

## 2026-09-28 staff PIN successor (separate evidence denominator)

First-job source checkpoint `746c75fe60d4600cf9f1e5fff173da9730514081` was
committed/pushed on `codex/staff-pin-first-job-20260928`. Later P4 HTTP/UI
composition remains a local candidate; its 17 focused host/HTTP/PostgreSQL tests
are not exact-commit CI, a published web/root pointer, deployed migrations or
operator acceptance. Preserve failed/superseded receipts. Record backend, web,
root/gitlinks and generated contract fingerprints separately before publication.
Require exact CI and immutable image/SBOM/scan evidence before promotion;
follow disabled deployment -> migration admission -> isolated HTTPS/operator
checks -> canary -> bounded rollout. Roll back entry points while retaining
schemas and attribution. [Station admission](../operations/stations-production-admission.md)
defines outstanding operational/privacy/support/restore gates; no older release
status is changed by this local amendment.

## 2026-08-25 Unpublished Recovery Checkpoint

The staff-onboarding recovery-audit checkpoint is later local work and is not
part of the published commits or evidence listed above. It adds Staff and Access
Control read-only proof contracts, Workspaces recovery attempts/receipts, a
Workspaces migration, Data Rights catalogue/export changes, and tenant-
termination export/destruction coverage.

Checkpoint 3 collects `O1 -> S1 -> A1 -> O2 -> S2 -> A2` and requires exact
first/second proof equality, binds Access lifecycle proof to bounded target
scopes, exposes terminal same-operation replay versus a machine-readable
new-operation retry directive, enforces receipt truth and chronology in the
domain and PostgreSQL, and emits only bounded outcome/reason/replay telemetry.
The recovery POST and its product capability are default-off; disabled POST is
no-store and the latest-receipt GET remains available.

For a future candidate, separately record repository implementation, local
verification, exact-commit CI, hosted proof, and operational/privacy/legal
admission. The Access profile semantics are the current active profile version
observed at recovery time, not the unpersisted issuance-time version; the
cross-module proof is observational and not distributed-atomic.

Promotion order is expand -> disabled deploy -> exact migration and release
composition verification -> isolated QA. Production activation remains
prohibited: raw recovery-actor retention/pseudonymisation and verified
subject/role-bound Data Rights release are unresolved blockers, and there is no
exact-commit CI or hosted proof for this local checkpoint. Rollback disables
POST while retaining the additive schema, receipts, and latest GET. No
historical accepted-membership binding backfill is authorized. The impact set
includes SP-002, SP-003, and SP-008 in addition to SP-001, SP-006, SP-007,
SP-009, SP-011, SP-012, and SP-013.

# Automatic Retention Control Plane Task

Status: published; local and exact-candidate verification complete

## Outcome

Complete company-readiness control SP-003. Every active workspace and governed
property automatically receives the applicable versioned retention schedules.
Operators can see which PII-minimized data class is healthy, due, overdue,
blocked by a legal hold, or repeatedly failing without inspecting payloads or
another module's database.

The first execution slice integrates the two mature Ingestion retention paths:
raw source-evidence purge and sensitive reservation-history redaction. Later
slices add the remaining approved data classes without weakening owner-module
boundaries.

## Boundary

- Retention is a BunkFy product module. It owns active tenant/property scope
  projections, schedule definitions, PII-minimized execution attempts and receipts,
  due/overdue health, and operator-facing retention status.
- Each owner module owns retention deadlines, legal holds, candidate
  selection, mutation, tombstones, and exact idempotency for its records.
- Retention invokes owners only through a versioned Contracts contributor.
  It never reads an owner database or receives personal data.
- Workspaces and Properties remain authoritative for tenant/property lifecycle
  and processing-policy facts. Retention stores only rebuildable projections.
- GMA owns generic scoped scheduling, task leases, heartbeats, retries,
  cancellation, terminal task history, and worker execution.
- Country-policy packs and the personal-data catalogue define which product
  retention policies are allowed. Private/legal owners approve actual policy
  values and enabled markets.
- No BunkFy policy, data-class key, legal-hold rule, or owner contract belongs
  in GMA. The current GMA task contracts are sufficient for this slice.

## Execution Model

1. Retention projects active Organizations and processing-enabled Properties
   from versioned integration events.
2. Owner modules register bounded `IRetentionExecutionContributor`
   descriptors: owner key, data-class key, target scope kind, execution-policy
   version, interval, worker group, and maximum attempts.
3. One Retention `ITaskScheduleProvider` combines active scope projections with
   contributor descriptors and emits deterministic scoped schedules.
4. A scheduled Retention task freezes the descriptor version, target scope,
   occurrence, run id, attempt, and deadline into a PII-minimized execution attempt.
5. The exact contributor performs bounded owner-local work through its normal
   repositories/commands and returns counts plus stable policy/outcome codes.
6. Retention stores the terminal receipt. A failure is recorded before the
   task is rethrown to GMA for its normal retry policy.
7. Health derives from the persisted attempt/receipt and deterministic next
   occurrence. Missed schedules, repeated failure, backlog, and overdue hold
   review use bounded stable codes rather than payload details.

Schedule identity is deterministic by tenant, optional property, owner,
data-class, and execution-policy version. A changed policy version creates a
new identity; an exact retry reuses the same occurrence and owner idempotency
key.

## Security And Privacy

- Requests, task payloads, receipts, logs, metrics, events, and status DTOs
  contain only tenant/property ids, bounded data-class and policy keys,
  versions, counts, timestamps, and stable outcome codes.
- Tenant and property coordinates are classified as pseudonymous or linked
  operational data in Retention's checked-in personal-data catalogue.
- No names, contacts, provider payloads, free text, object names, source ids,
  file ids, or owner record ids cross into Retention.
- Tenant scope is mandatory. A property target must belong to the projected
  tenant and have a current processing policy.
- Owner contributors fail closed on an unknown policy version, stale scope,
  invalid deadline, unsupported data class, or active legal hold.
- Status reads require a dedicated retention-read permission. Retry and
  reconciliation require separate management permissions and recent
  privileged authentication.
- Error messages exposed through management surfaces are bounded codes. Owner
  details remain in owner-authorized operational views.

## Efficiency

- Schedule enumeration uses indexed active-scope projections and a small
  in-memory descriptor set; it never scans owner data.
- One execution claims bounded owner batches. Continued backlog is handled by
  the next deterministic occurrence or a privileged retry, not an unbounded
  loop.
- Retention keeps one PII-minimized current execution per deterministic run and
  fences retries by monotonic attempt. GMA task history retains generic attempt
  evidence; Retention execution rows receive their own bounded retention policy
  after operational evidence requirements are approved.
- Health queries use tenant-first indexes on schedule identity, occurrence,
  status, and next due time.
- GMA task history remains generic execution evidence. Retention receipts add
  only product facts GMA cannot know, such as affected counts and hold state.

## Delivery Slices

1. [x] Add Retention Contracts, domain, persistence, PostgreSQL migrations,
   module composition, organization/property projections, and architecture
   guards.
2. [x] Add deterministic schedule reconciliation and the generic execution
   task with exact replay, failure recording, and bounded status queries.
3. [x] Adapt Ingestion raw-payload purge and sensitive-history redaction behind
   two Retention contributors while preserving existing owner commands and
   direct task compatibility.
4. [x] Add public management read status plus Admin API/CLI status and retry
   surfaces with separate permissions and assurance. GMA's scheduler performs
   continuous reconciliation, so there is no duplicate manual reconcile
   endpoint.
5. [x] Add deterministic schedule, due/overdue health, retry/replay,
   legal-hold release, owner evidence, and tenant-isolation tests, including
   one real PostgreSQL/Worker control-plane proof.
6. [x] Add the operator retention-health view and focused frontend tests.
7. [x] Run complete non-Docker gates once, Docker once, and prepare the exact
   candidate for publication.

## Verification

Verified on 2026-07-28:

- `eng/verify.ps1` passed the complete non-Docker build, migration-drift,
  module, architecture, host, and non-Docker integration gates.
- Retention's focused suite passed 14 tests after the final persistence fixes.
- The focused PostgreSQL/Worker control-plane proof passed tenant isolation,
  legal-hold pause/release, rerun, and convergence.
- `eng/test-docker.ps1 -NoBuild` passed all 64 Docker integration tests in
  7 minutes 35 seconds.
- The web application passed contract drift, 20 test files with 130 tests,
  and its production build.
- Replacement backend commit `5c98f9c1d2377aac2bf931bd47f0b9149ed6d3af`
  passed exact-candidate validation in GitHub Actions run `30310570086` and
  the corrected Docker gate in run `30310569950`.

## Acceptance

- A newly created active organization receives every tenant-scoped schedule
  without Admin CLI intervention.
- A processing-enabled property receives every property-scoped schedule; a
  suspended/retired property cannot start new ordinary retention work.
- Policy-version changes reconcile deterministically without replaying an old
  policy as current.
- Focused Retention tests prove deterministic schedule identity, next due,
  overdue health, and exact retry. GMA remains authoritative for generic
  scheduler restart and lease recovery.
- Ingestion raw purge and sensitive-history redaction remain bounded,
  resumable, legal-hold aware, and owner-idempotent.
- Operations can list overdue tenant/data-class coordinates and stable failure
  codes without personal data.
- PostgreSQL/Worker proof covers two tenants, a legal-hold pause/release,
  explicit rerun, owner receipts, and no cross-tenant mutation. NATS is not
  part of this local scheduler/contributor path.
- Architecture tests prove Retention references owner modules only through
  Contracts and no owner references Retention Application, Domain, or
  Persistence.

## Deferred

- Final production periods, enabled country packs, alert routing, and legal
  approval.
- Backup/object-store lifecycle configuration in a real hosted environment.
- Tenant-termination deletion orchestration and evidence.
- Invitation, notification, task-history, message-journal, and other owner
  data classes until the first Ingestion slice proves the control-plane shape.
- A generic GMA retention module or product-policy abstraction.

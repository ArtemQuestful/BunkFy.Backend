# Retention Module

Status: retention scheduling and execution control plane implemented;
tenant-termination export and destruction owner implemented; production
termination activation remains deferred; online mutations serialized by
target, execution, and schedule coordinates

Retention owns BunkFy's schedule state and execution history for invoking
retention work in the modules that own the affected data. It does not own the
business records being retained, anonymised, or deleted.

The module persists three authoritative tenant-owned record families:

- `RetentionExecution`, the bounded attempt and outcome history for one owner,
  data class, policy version, and tenant or property target; and
- `RetentionScheduleState`, the current scheduling and health state for that
  same coordinate; and
- `RetentionRunRetryRequest`, the durable, evidence-pinned state of one
  operator-requested Task Runtime retry.

Organization and property records in Retention are rebuildable projections.
The inbox is transport state. Neither is part of Retention's tenant portability
fragment.

The projection models normalize tenant coordinates, require non-empty owner
identities and positive incoming source versions, preserve monotonic stale-event
handling, and reject conflicting equal-version facts. PostgreSQL independently
enforces the tenant coordinate and the property's topology-first,
policy-first, known-state, and policy-version shapes. See the
[Retention Scope Projection Integrity Task](../../../docs/planning/retention-scope-projection-integrity-task.md).

## Operational Surfaces

Schedule health is a derived, tenant-scoped snapshot over the registered owner
coordinates and Retention's scalar schedule-state projection. The public and
Admin lists use stable ordering, normalized page bounds, and one-record
lookahead for truthful `HasMore`; they do not issue an exact-count query or load
execution aggregates. Each page carries a whole-snapshot summary so operator
metrics do not change meaning while paging.

The health contract exposes the nullable `LastRunId` already persisted in
schedule state. That value is the GMA Task Runtime coordinate used by the Admin
retry facade; Retention does not duplicate run or retry lifecycle ownership.
Public and Admin responses are non-cacheable, Admin outcomes have explicit
contracts, and the CLI supports the same paging and run coordinate.

Contributor ordering and duplicate-coordinate validation are shared by health
reads and schedule discovery and fail closed before either surface proceeds.
Generic Task Runtime provider streaming, bounded scheduler memory, and
Retention execution-history cleanup remain separate follow-up concerns.

## Task Lease And Owner Recovery

Task Runtime `Attempt` remains the retry-budget counter within the current task
run. An operator `RetryAsync` starts that budget again, so Retention does not
use the resettable value as an execution or owner idempotency fence. Every task
claim also increments the persisted `LeaseGeneration`; that generation is
monotonic across ordinary retry, lease reclaim, timeout recovery, and operator
`RetryAsync`. Retention persists and forwards `LeaseGeneration` in its
execution and owner-contract field named `Attempt`. A stale lease therefore
cannot complete the Retention execution or owner attempt opened by a newer
claim even when Task Runtime's budget counter has restarted.

If a reclaimed lease finds the central Retention execution already `Completed`
or `Blocked`, begin returns the persisted terminal state with dispatch disabled.
The task converges without invoking the owner again. If the owner committed its
terminal completion but the worker failed before central Retention committed
that result, the newer lease generation opens a non-regressing central recovery
window and asks the owner to replay. The owner's immutable terminal evidence is
not rewritten. When its persisted completion predates the new central start,
Retention records the same terminal counts, status, and outcome with the new
window's start as the central completion time, keeping the central aggregate's
time invariant without changing owner proof.

Recovery windows may only move forward. A retry of a still-running central
execution cannot start before its previous start, and a `Failed` execution
cannot restart before its persisted completion. `Completed` and `Blocked`
executions are terminal and do not open another owner window.

The aggregate and PostgreSQL model share one authoritative state contract for
executions, schedule health, and durable retry requests. Empty coordinates,
malformed bounded codes, contradictory running/terminal evidence, mismatched
property target keys, invalid failure counters, and successful work completed
after its deadline fail closed at the owner model. Failed work may finish after
its deadline so timeout evidence remains recordable. See the
[Retention Control Plane Authoritative State Integrity Task](../../../docs/planning/retention-control-plane-authoritative-state-integrity-task.md).

## Tenant Termination

Retention is a mandatory `Export` and `Destroy` contributor after Ingestion.
Its versioned tenant export contains exactly three deterministic streams:

1. retention executions; and
2. retention schedule states; and
3. durable run-retry requests.

The export deliberately excludes tenant and property projections, inbox
messages, and the local tenant revision row. Tenant and property identifiers
remain elevated coordinates owned authoritatively by Organizations and
Properties.

Destruction closes local admission and removes outbox and inbox messages,
run-retry requests, schedule state, execution history, property projections,
and tenant projections in foreign-key-safe stages. One invocation removes at
most one non-empty batch of 500 records. Completion retains only the closed
lifecycle row and one
immutable, PII-free destruction receipt with a versioned SHA-256 proof chain.
Personal-data catalogue version 3 classifies every retained proof member under
the dedicated tenant-destruction policy.

Relational writes use the shared BunkFy tenant-mutation transaction key, while
export selection and lifecycle transitions use its exclusive counterpart.
Every operational save re-reads the Workspaces termination fence under that key
and fails closed when admission is unavailable. Successful units of work
advance one scope-filtered monotonic tenant revision. Export uses a
repeatable-read transaction, validates the exact frozen process and epoch before
and after streaming, and returns only an unchanged revision as proof. Closing
and closed scopes are excluded from schedule discovery and inbox admission.

Online decisions also use Retention-owned transaction-key coordinates before
loading mutable state. Scheduled starts acquire shared tenant/property target
fences, then exclusive execution and schedule fences; completion acquires the
execution fence before deriving and acquiring its schedule coordinate.
Organization and property inbox consumers acquire the corresponding exclusive
target fence before merging source versions. This prevents stale aggregate
decisions and first-use projection races without adding lock rows or changing
Task Runtime ownership. The tenant revision still serializes only the final
short save/commit needed for coherent export evidence.

The Retention task handler declares a one-hour-and-one-minute GMA registration
timeout. Contributor descriptors remain bounded to one hour and enforce their
own exact deadline; the final minute is reserved for recording the terminal
Retention receipt. Other task handlers continue to use their own registration
timeout or the worker-wide fallback.

Migration `AddRetentionTenantExportRevision` adds the local revision table.
Migration `AddRetentionTenantDestructionLifecycle` adds lifecycle state,
resumable destruction operation state, and the receipt ledger. PostgreSQL also
enforces receipt immutability with an append-only trigger.

All 83 Retention tests pass and migration-drift checks cover the current model.
The exact PostgreSQL 16 mutation scenario proves same-schedule and
same-property writers wait, reload the committed winner, preserve independent
projection streams, and leave unrelated coordinates available. A separate
PostgreSQL 16 upgrade scenario preserves valid execution, schedule, and retry
states and rejects malformed control-plane writes by stable constraint name.
The scope-projection upgrade scenario preserves topology-first, policy-first,
and fully known rows and rejects malformed projection writes by stable
constraint name. The destruction scenario passed on 2026-08-04 with migration,
shared/exclusive lock drain, bounded 500-row removal, schedule suppression,
deterministic replay and conflict, raw-SQL append-only enforcement, closed-scope
rejection, and tenant isolation. The earlier export scenario remains unchanged.

Production termination task and route registration remain disabled until
remaining product owners, terminal orchestration, protected replay, operator
controls, and final production admission are complete.

See [Tenant Termination Control Plane](../../../docs/planning/tenant-termination-control-plane-task.md).

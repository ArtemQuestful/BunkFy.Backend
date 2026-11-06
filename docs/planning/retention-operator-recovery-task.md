# Retention Operator Recovery Task

Status: complete
Date: 2026-08-15

## Goal

Make automatic-retention health truthful and recoverable for authorized
workspace operators. A failed schedule must identify the exact current task
run that can be retried, while a never-run, unknown, blocked, or stale schedule
must remain visibly actionable instead of falling outside every health metric.

This slice continues
[`retention-operational-surface-hardening-task.md`](retention-operational-surface-hardening-task.md).
It does not change retention periods, owner mutation rules, legal-hold policy,
or generic task-runtime behavior.

## Ownership

- Retention owns schedule coordinates, current owner outcome, health
  classification, tenant-safe retry admission, and the product operator
  contract.
- Each owner module still owns candidate selection, legal holds, destructive
  mutation, exact owner idempotency, and recovery-specific outcome codes.
- GMA Task Runtime owns generic run state, retry eligibility, concurrency,
  scope closure, leases, and worker execution. Its existing
  `ITaskRunReader` and `ITaskRunController` contracts are sufficient.
- The web application presents permission-shaped recovery actions and never
  receives task payload JSON or owner records.
- No Retention data-class key, owner key, workspace permission, or recovery
  route belongs in GMA.

## Findings

- `retention.retry` and `LastRunId` are published product contracts, but only
  the platform Admin API and CLI can invoke retry. Workspace operators cannot
  use the permission through the public product surface.
- Admin API and CLI duplicate task ownership and tenant validation instead of
  sharing one Retention application operation.
- The health summary and web helper omit `NeverRun` and `Unknown` from both
  healthy and attention counts. A newly projected schedule can therefore make
  `Total` differ from every visible category while looking harmless.
- The web view prints stable owner outcome codes without a recovery
  explanation, exact run context, or a safe retry flow.
- Retrying by run id alone can target an old Retention run. A workspace retry
  must pin the current health-row coordinate and prove that the run remains the
  latest run for that schedule before dispatch.

## Decisions

### Durable recovery operation

The public API deliberately does not compose Task Runtime persistence or
control services. Retention therefore owns a durable, PII-free recovery
request instead of expanding the public host's database authority.

One Retention application command is used by the public API, Admin API, and
Admin CLI. It must:

1. resolve the ambient tenant and reject missing or mismatched scope;
2. acquire the exact Retention schedule lock;
3. require the current schedule to be failed and pin its owner, data class,
   property, policy version, run id, and evidence version;
4. create or replay one request for that failed evidence version;
5. emit a tenant-scoped outbox event without task payload JSON or actor data;
   and
6. return a PII-free typed receipt with pending, applied, or failed state.

The Worker consumes that request through Retention's inbox, validates the
target through Task Runtime Contracts, reacquires the exact schedule lock, and
revalidates the pinned current failed evidence immediately before delegation.
It delegates generic retry-state and concurrency behavior to Task Runtime. A
stable opaque request token makes a crash between Task Runtime mutation and
Retention acknowledgement reconcilable. Adapter rejection or unavailability
becomes a terminal, non-disclosing failed receipt; database and transaction
failures remain infrastructure failures eligible for inbox redelivery. Admin
surfaces use the same current-schedule recovery contract; older task history
remains available through GMA Task Runtime administration, not through a
second Retention policy path.

### Product API security

Add `POST /api/retention/runs/{runId}/retry` with:

- tenant scope and `retention.retry` permission;
- an explicit confirmation flag;
- exact expected schedule coordinates from the rendered health row;
- the exact health evidence version rendered with those coordinates;
- configurable recent-authentication assurance using the host's privileged
  operation policy;
- no-store response headers; and
- `202 Accepted` with deliberate 400, 404, 409, 423, and 503 mappings for
  invalid confirmation, unavailable runs, stale schedule evidence, closed
  scopes, and fail-closed admission infrastructure.

The request does not accept actor identity, payload JSON, owner record ids, or
free text. The authenticated HTTP or administration boundary remains
responsible for access and operation audit; Task Runtime receives only an
opaque recovery-request token.

### Health semantics

Classify `NeverRun` and `Unknown` as attention states. The complete summary
must partition every row into exactly one of healthy, running, or attention.
Attention rows sort before ordinary rows without changing the existing stable
coordinate order or bounded paging contract.

### Operator experience

- Show readable owner outcome guidance while retaining the stable code as
  support evidence.
- Show the short exact run reference and last evidence time.
- Offer retry only for a failed current run and only when the actor has
  `retention.retry`.
- Show pending, applied, and failed recovery state from the server so an
  accepted request never masquerades as completed work.
- Bind confirmation and password step-up to an immutable retry intent. A
  refresh or row change cannot silently retarget the confirmation.
- Refresh health after success or conflict so uncertainty converges to the
  server state.
- Blocked schedules explain hold review rather than offering a retry that Task
  Runtime or the owner cannot make meaningful.
- Property-scoped recovery may link to the existing property or integration
  surface, but this slice does not invent a cross-module mutation API.

## Efficiency

- Public request admission acquires the coordinate lock first, then performs
  one indexed current-schedule read and one indexed recovery-request lookup.
  It does not enumerate task history or owner data. Administration by run id
  performs the additional indexed run-to-schedule resolution it requires.
- Worker handling performs one locked current-schedule read, one exact task-run
  read, and at most one generic retry mutation. Current health joins only the
  recovery request matching the schedule's current run and evidence version;
  historical recovery rows are not loaded into the operator page.
- Health remains bounded by its existing page contract and whole-snapshot
  summary. Idle health refresh is five minutes; running or pending work refreshes
  at ten seconds; a newly accepted retry converges at two seconds for at most
  thirty seconds before falling back to the normal cadence.
- The UI keeps one retry mutation and one immutable intent instead of a query
  or mutation instance per row.

## Upgrade and lifecycle safety

- The PostgreSQL migration preserves every pre-existing tenant-destruction
  stage while inserting the new outbox and retry-request stages. In particular,
  the previous inbox stage maps to the new inbox stage rather than skipping it.
- Inbox handling and tenant destruction share the existing tenant lifecycle
  transaction lock. An admitted handler completes before destruction proceeds;
  a later handler observes the closing lifecycle and is suppressed.
- Destruction waits for an active outbox publication lease, then removes the
  Retention outbox, inbox, retry request, schedule, execution, and projection
  records through the existing bounded and resumable operation.
- A consuming Worker fails startup when Retention is enabled without the local
  Task Runtime recovery adapter. Non-consuming hosts remain independently
  composable.

## Delivery

1. [x] Add the durable recovery contract, aggregate, command, current-schedule
   evidence pin, outbox/inbox bridge, and focused tests.
2. [x] Route Admin API and CLI through the shared operation without weakening
   their separate global administration permissions.
3. [x] Add the tenant product endpoint, permission, assurance, error mapping,
   no-store policy, and API metadata tests.
4. [x] Correct backend and web attention classification and summary tests.
5. [x] Add the permission-shaped retry and recovery UX, immutable attempt
   helper, and focused web tests.
6. [x] Add the PostgreSQL migration, regenerate OpenAPI/contracts, and run the
   coherent slice gate once.

## Verification Cadence

Use focused Retention and web tests while editing. At the coherent slice
boundary, run one full non-Docker backend gate and one complete web
lint/test/typecheck/build/contracts gate. Run the Retention PostgreSQL Docker
scenario once because the final design adds an indexed recovery-request table
and outbox storage; do not repeat it after every code change.

## Verification

Completed locally on 2026-08-15:

- Retention module tests: 68 passed.
- Retention Task Runtime extension tests: 4 passed.
- Worker topology test: 1 passed.
- Complete backend non-Docker gate: passed with zero build warnings or errors.
- Complete web lint, 304 tests, typecheck, build, and contract drift gate:
  passed.
- Retention PostgreSQL upgrade scenario: 1 passed against a containerized
  database, including preservation of pre-existing destruction stages.
- Preview rebuild: all services healthy, all 228 PostgreSQL migrations current,
  the Worker executed Retention schedules, and unauthenticated access to the
  public health route returned 401.
- Desktop and narrow-width Preview login renders were inspected for a nonblank,
  correctly framed shell. Authenticated Retention behavior is covered by the
  focused permission, retry-intent, and component tests.

This is local candidate evidence. Hosted rollout, same-release deployed proof,
and the deferred external gates below remain separate work.

## Deferred

- Owner-specific mutation dashboards beyond bounded recovery destinations.
- Automatic alert routing and escalation policy.
- Cursor pagination and server-side health filtering until measured volume
  requires them.
- Retention-owned cleanup of terminal Retention evidence pending an approved
  evidence period and legal-hold policy.
- Hosted rollout, legal approvals, country-policy periods, and production
  recovery proof.

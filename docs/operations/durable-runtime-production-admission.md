# Durable Runtime Production Admission

BunkFy Production hosts fail closed until one Worker maintenance process and
the durable-runtime retention/replay policy are explicitly approved. GMA owns
the cleanup engines; this contract selects their BunkFy deployment topology and
values.

## Operational journal retention review — 2026-10-26

The processed inbox window remains 14 days, processed outbox retention remains
7 days, and one Worker runs bounded journal cleanup hourly (500 rows per batch,
at most 10 batches per store per cycle). Preview's managed JetStream history and
declared ordinary broker replay horizon are 7 days. The 14-day inbox window is
a host operational choice with margin over that broker window, not an industry
standard or a business-data retention policy.

The controlling rule is to retain deduplication evidence longer than messages
can be retried or replayed. Include delayed publication after an outage,
automatic redelivery, manual recovery and backup/restore replay in that review.
Particular's [outbox guidance](https://docs.particular.net/nservicebus/outbox/)
explicitly includes delayed and manual retries; its current
[SQL implementation](https://docs.particular.net/persistence/sql/outbox)
defaults to seven days and a one-minute purge interval. MassTransit's
[transactional outbox](https://masstransit.massient.com/configuration/middleware/outbox)
also makes the duplicate-detection window configurable and treats inbox cleanup
as an independently scheduled service. These are implementation defaults, not a
universal retention requirement. [JetStream's persisted messages and consumer
acknowledgements](https://docs.nats.io/concepts/jetstream) provide replay and
redelivery; they do not make application receipt retention unnecessary.

Do not claim indefinite duplicate suppression from a 14-day journal. Recovery
that can redeliver work after that window needs durable business idempotency or
an explicitly reviewed recovery procedure and retention horizon. Review that
before enabling older replays or extending supported outage/recovery windows.
Do not shorten the journal window below the supported replay/retry horizon to
reduce database size.

Cleanup removes only eligible, already processed inbox records strictly older
than the cutoff. Pending, processing and failed records are recovery work and
are not expired by this job. Closed-scope records and lifecycle tombstones stay
under the owning lifecycle's destruction and restore rules. Notification
content, guest/staff data, audit evidence and backups have separate product
policies; this review does not change them.

Monitor cleanup failures, oldest eligible processed age and deletion throughput.
Choose cadence and bounded batch limits to keep up with the measured arrival
rate. Increasing cleanup frequency is independent of changing the retention
window. Preserve one admitted maintenance owner and the scope-closure revision
checks when scaling the deployment.

## Required Declaration

### 2026-09-05 amendment: reservation-to-Guest-Record task consumer

Deployments exposing reservation Guest Record creation/linking must assign
`guest-record-link-workers` to an enabled Worker with Reservations, Guests and
TaskRuntime modules. Preserve the other configured worker groups. The default
Worker settings and AppHost now include this group at index8. A process in
`Ready` (2) is not completed: terminal outcomes are `Completed` (3) and
`NeedsReview` (4). Admission must verify a real created/link process reaches
Completed and the reservation references the intended existing guest, without
duplicate create/retry commands. Monitor pending age, failed/retrying task runs,
and NeedsReview; retain actor/version/audit checks in the existing handler.

UX recovery decision BFX-UX-DECISION-LEDGER-0032 records the local discovery:
18 new synthetic demo tasks were stranded by the missing assignment. The
isolated preview may recover through a configuration-only Worker recreation on
the same image; that is not proof of rebuilt source, exact-commit CI, hosted
deployment, security/privacy approval, or production admission. No API, schema,
migration or retention-policy change is introduced. Rollback must retain this
consumer or drain the queue and disable the dependent feature with explicit
support/monitoring ownership. Removing the group alone strands future work;
already completed durable guest links are not undone by configuration rollback.

Public API, Admin API, and every Worker process receive the same approved policy:

```json
{
  "BunkFy": {
    "DurableRuntime": {
      "ProductionAdmission": {
        "ApprovalState": "Approved",
        "ApprovalReference": "ops/runtime-policy-2026-08",
        "MaintenanceOwner": "Worker",
        "MaintenanceOwnerInstanceCount": 1,
        "CurrentProcessOwnsMaintenance": false,
        "ProcessedOutboxRetention": "7.00:00:00",
        "ProcessedInboxRetention": "14.00:00:00",
        "BrokerReplayHorizon": "7.00:00:00",
        "SucceededRunRetention": "30.00:00:00",
        "FailedRunRetention": "90.00:00:00",
        "CanceledRunRetention": "30.00:00:00",
        "TimedOutRunRetention": "90.00:00:00",
        "HandledControlRetention": "30.00:00:00",
        "FailedControlRetention": "90.00:00:00",
        "ExpiredControlRetention": "30.00:00:00"
      }
    }
  }
}
```

The values above are examples, not legal approval. Replace the reference and
windows with the deployment's reviewed policy. The reference is a non-secret
evidence identifier and must not contain credentials or personal data.

## Maintenance Owner

Exactly one Worker process profile sets:

```text
BunkFy__DurableRuntime__ProductionAdmission__CurrentProcessOwnsMaintenance=true
MessageJournalCleanup__Enabled=true
TaskRuntimeRetention__Enabled=true
Worker__Modules__TaskRuntime=true
```

Public API, Admin API, and every non-owner Worker keep both cleanup settings
false and set `CurrentProcessOwnsMaintenance=false`. The declared owner count is
validated by startup but must also be reconciled against the orchestrator's
actual replica count before traffic.

The owner must clean both processed outbox and inbox journals. The configured
journal and TaskRuntime windows must exactly match the approved declaration, and
inbox retention cannot be shorter than the broker replay horizon.

## Worker Release Identity

Every Production Worker also supplies `BunkFy:Deployment` with `Profile`,
`Runtime`, exact 40-character source commit, and an immutable OCI digest when
containerized. A Worker that composes Ingestion must additionally satisfy the
same object-storage service-account and hosted transport rules as the API.
HTTP edge, API topology, and Data Protection key-ring declarations do not apply
to the Worker process.

## Composition Failures

Startup rejects:

- pending approval, a missing evidence reference, or an owner count other than
  one;
- cleanup enabled on a non-owner or disabled on the selected owner;
- task execution without TaskRuntime persistence;
- task scheduling without both task execution and TaskRuntime;
- NATS consumers without NATS publishing;
- unapproved, mismatched, non-positive, or excessively long retention windows;
- a Production Worker without exact release identity.

### 2026-09-13 amendment: Reservations projection-writer ownership

Before activating a release that changes the Reservations property-projection
rebuild, retain composition evidence that its task handler resolves the
Reservations-owned writer and executes it with the Reservations transaction and
checkpoint store. A matching generic snapshot type does not establish writer
ownership: a Workspaces writer must not be selected for a Reservations task.
This is a required release/pre-activation check, not a newly implemented startup
validator or a declaration that all projection-writer collisions are repaired.

Use an isolated actual-PostgreSQL regression with both module-registration
orders and two synthetic tenants. Prove exact source-matching projections and
completed checkpoints, same-run replay and new-run rebuild, atomic rollback,
crossed-scope rejection, and unchanged Workspaces projections. Bind the test
receipt and source to the admitted Worker image; local tests alone do not prove
hosted execution. The regression is
`ReservationsPropertyRebuildWorkerIntegrationTests`; the diagnosed failure is
tracked as `WORKER-RESERVATIONS-0104-REPAIR`.

Preserve the original failed task IDs, attempts and error records. Record an
explicit scoped retry decision or create a newly correlated rebuild when safe
retry cannot be established. Retain before/after task states, backlog counts,
both tenants' projection/checkpoint coordinates, payload-free failure/recovery
signals, alert owner/destination, and rollback or forward-fix responsibility.
Do not erase failures or mutate checkpoints to manufacture success. The separate
Notifications cleanup admission defect is not covered by this repair.

## Activation Evidence

Before real tenant data, retain the approved policy, exact release/image,
orchestrator owner replica count, database and broker backup/restore results,
cleanup metrics, oldest-backlog alerts, and one failure/recovery drill. Do not
manually delete journal, task, control, or module-owned rows to force a clean
dashboard.

## Inbox Exhaustion And Recovery

Alert on every `gma.inbox.exhausted` measurement. The metric is payload-free
and carries only bounded module, handler, subject, and the fixed
`inbox-handler-failed` error-code dimension. It never copies a store-provided
error string into a metric or structured log. Inspect the durable inbox's
payload-free persisted classifier when the handler and subject require more
specific diagnosis.
Treat `gma.inbox.recovered` as evidence that a later broker delivery processed;
it does not by itself prove that the underlying product state is correct.

When the durable inbox classifier is
`inbox-handler-failed:ReservationAllocationUnitMismatchException`, follow
`docs/planning/reservations-allocation-mismatch-recovery-task.md`. Production
admission for that failure mode requires a hosted five-delivery exhaustion drill
and the Inventory-owned controlled recovery path. Operators must not delete
inbox rows, edit payloads or allocation data, or reset a durable consumer to
manufacture recovery.

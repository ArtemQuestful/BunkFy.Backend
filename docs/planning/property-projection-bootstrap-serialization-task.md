# Property Projection Bootstrap Serialization Task

Status: complete
Date: 2026-08-13

## Goal

Remove exception-driven convergence when independent Properties event
subscriptions first materialize the same local projection. Concurrent topology,
policy, room, or bed facts for one coordinate must wait, reload the committed
state, and apply monotonically without a PostgreSQL unique-key failure or NATS
redelivery.

## Production Finding

The exact Reservations and Inventory Preview rehearsal completed successfully,
but Worker logs showed simultaneous first inserts colliding on Reservations,
Ingestion, and Data Rights `property_projection` primary keys and on the Guests
property operation-lock unique key. NATS retried the failed handlers and the
public workflow eventually converged. That preserves correctness, but turns an
ordinary first-write race into database errors, noisy retries, and avoidable
latency.

The same shape is latent where other local topology consumers can create a
property, room, unit, or projection placeholder from independently delivered
event types.

## Ownership

- Each BunkFy module owns its projection coordinate, lock namespace, reload,
  version merge, and tests.
- Properties continues to publish independent versioned topology and policy
  facts. It does not coordinate consumer storage.
- GMA continues to own the provider-neutral `EfTransactionKeyLock` primitive,
  transaction boundary, inbox/outbox behavior, and transient delivery retry.
- PostgreSQL and SQL Server remain supported by the existing GMA primitive;
  non-relational test stores remain lock-free.
- No BunkFy projection vocabulary, event type, or resource key belongs in GMA.

## Invariants

1. Every first-write path acquires one exclusive, transaction-scoped key for
   the module, tenant, resource kind, and resource id before reading or adding
   the projection row.
2. Competing topology and policy handlers for the same property share the same
   module-local key. Unrelated tenants, properties, and modules do not contend;
   Inventory descendants intentionally serialize inside their parent property
   while topology is materialized.
3. Inventory acquires topology keys in fixed property, room, then bed order so
   placeholder creation and the matching concrete event cannot race or deadlock.
4. Guests acquires the advisory key before reading or creating its durable
   operation-lock row. The durable row and existing fail-closed behavior remain
   intact.
5. A relational call without the messaging/command transaction fails closed.
   In-memory unit tests do not require provider-specific locking.
6. After waiting, the contender queries authoritative committed state and
   relies on the existing source-version merge. Unique-key exceptions are not
   used as ordinary control flow.
7. Event contracts, projection schemas, migrations, module references, and GMA
   submodule pins do not change.

## Delivery

1. Serialize Reservations, Ingestion, Data Rights, and Guests property topology
   and policy bootstrap paths.
2. Serialize Inventory property, room, bed, and unit placeholder paths with a
   stable hierarchy.
3. Serialize Staff and Workspaces property projection first writes so rapid
   lifecycle events cannot collide.
4. Add an architecture guard enumerating every Properties projection writer
   that must use the existing transaction-key primitive; retain Retention's
   already-coordinated path as the reference implementation.
5. Run focused module and architecture tests while editing, one consolidated
   non-Docker backend gate at the end, then rebuild Preview once and repeat the
   exact public rehearsal with a clean Worker error scan.

## Deferred

- generic message partition metadata or module-wide handler serialization;
- provider-specific SQL upserts in otherwise provider-neutral repositories;
- changing NATS retry policy for genuine transient failures;
- a framework projection abstraction before another project demonstrates a
  stable common contract; and
- unrelated projection writers that cannot be reached by competing first-write
  event types.

## Acceptance

- affected repositories acquire narrow stable keys before first reads;
- architecture coverage prevents a writer from silently dropping its lock;
- existing focused module behavior remains green;
- the complete non-Docker backend gate and contract drift check pass once;
- the exact Preview lifecycle still passes with full cleanup and no projection
  unique-key or failed-handler log entries; and
- GMA runtime source, skeleton, and extensions remain unchanged; the framework
  solution index may be synchronized independently when its own guard detects
  metadata drift.

## Completion

Reservations, Ingestion, Data Rights, Staff, and Workspaces now acquire one
module-owned property-projection key before the first read. Inventory uses the
fixed property, room, then bed hierarchy, and Guests acquires an advisory key
before reading or creating its durable operation-lock row. Relational calls
still fail closed without an active transaction; in-memory test stores remain
provider neutral. No contracts, schemas, or migrations changed.

The seven focused module suites passed 2,041 tests, the new architecture guard
passed eight cases, and the Integration Tests project built with zero warnings.
The consolidated backend run passed solution and package guards, the complete
build, and every migration drift check. Its only late failure was the missing
index link for this task; after adding the link, the exact failed guard and the
fast test rerun passed.

Backend candidate `43733de` was deployed as root candidate `5c33c63` under
release `preview-projection-bootstrap-5c33c63`. The exact Preview onboarding
rehearsal passed invitation 8/8, enrollment 9/9, Reservations and Inventory
12/12, and umbrella 11/11 checks. API and Worker logs from the rebuilt-container
window through rehearsal contained no unique-key collision, failed handler,
retry, exception, or transaction-lock error. The minimized Reservations and
Inventory child SHA-256 is
`1aad28c1f3193adc14394405f782755df2834f633f62551647507ba9ab45ca0f`;
the umbrella SHA-256 is
`548eac4e70887445a21033fa8159c1762941ac9b7124f2610d8b97483cf468d0`.

GMA runtime behavior did not change. A separate metadata-only framework commit
`a488677` added its already-tracked method-aware rate-limit task to
`Gma.Framework.slnx` after the source-package guard exposed that stale index.

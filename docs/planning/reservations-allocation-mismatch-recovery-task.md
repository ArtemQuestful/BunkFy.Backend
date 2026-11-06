# Reservations Allocation-Mismatch Recovery Task

Task ID: `BFX-BE-REC-001`

## Decision

An Inventory allocation confirmation whose unit set differs from the original
Reservation request is an integrity failure, not a successful allocation and
not an operator-visible availability fact. Reservations rejects it before any
aggregate, projection, domain-event, outbox, or identifier mutation.

The repository slice supplies payload-free diagnosis and bounded retry
observability:

- the handler throws `ReservationAllocationUnitMismatchException` with no
  tenant, property, reservation, allocation, unit, guest, or actor values;
- the durable inbox persists the stable classifier
  `inbox-handler-failed:ReservationAllocationUnitMismatchException`;
- JetStream still uses the configured `MaxDeliver` value (currently five in
  BunkFy hosts); deliveries before the limit retain the existing delayed NAK;
- `gma.inbox.exhausted` records the final observed failed delivery with bounded
  module, handler, subject, and the fixed `inbox-handler-failed` error-code
  dimension; store-provided failure strings never enter metrics or logs;
- `gma.inbox.recovered` records a later successful redelivery without payload
  identifiers; and
- missing JetStream delivery metadata never causes the runtime to guess that a
  message is exhausted or recovered.

This changes no database schema and does not change the configured retry limit.

## Recovery Boundary

Repository diagnostics do not constitute controlled recovery. BunkFy has no
safe single-message replay or reconciliation control for this invariant breach.
Support must not delete inbox rows, mutate allocation data with SQL, change a
payload under its existing event ID, recreate a consumer to force broad replay,
or republish an arbitrary stored payload.

The smallest accepted follow-up is an Inventory-owned, authorized
`ReconcileAllocationOutcome` operation that:

1. loads current producer-owned allocation truth;
2. verifies Reservation and Inventory coordinates without accepting
   caller-supplied replacement units as truth;
3. requires an explicit support confirmation and reservation-management
   authority;
4. emits a new correlated outcome with a new event ID;
5. records an idempotent, payload-minimised audit receipt; and
6. proves convergence, retry, conflict, stale-state, permission-denied, and
   rollback behavior against PostgreSQL and JetStream.

Owner: backend/product operations. Next action: design and implement the
producer-owned reconciliation command before production admission for this
failure mode. Evidence needed: exact-commit CI plus a hosted five-delivery
exhaustion and authorized recovery drill.

## Acceptance Evidence

Repository acceptance requires:

- mismatch and pre-allocation-cancellation handler tests proving zero side
  effects;
- generic inbox-metric tests for attempt, exhaustion, recovery, payload-free
  dimensions, and missing-metadata fallback;
- a focused PostgreSQL/JetStream test proving no sixth automatic delivery after
  the configured limit; and
- the future producer-owned recovery integration test described above.

The first two bullets are repository checks for this slice. The final two are
release-admission evidence and remain open until captured. A green unit suite is
not deployed or operational proof.

## Production-Control Impact

- `SP-001`: no personal data is added to exception text, metrics, or logs.
- `SP-007`: configure and retain the exact broker retry limit and replay
  horizon for the release.
- `SP-008`: the future recovery action requires least-privilege support access.
- `SP-009`: alert on any `gma.inbox.exhausted` event, route by its bounded
  handler and subject, then inspect the durable inbox's stable classifier for
  incident triage.
- `SP-011`: retain exact-commit tests and generated release evidence.
- `SP-012`: rehearse exhaustion, diagnosis, authorized recovery, monitoring,
  rollout, and rollback in the hosted environment.

Classification: API/backend implementation change plus production-plan
adjustment. Production status remains unresolved until the hosted drill and
producer-owned recovery control exist.

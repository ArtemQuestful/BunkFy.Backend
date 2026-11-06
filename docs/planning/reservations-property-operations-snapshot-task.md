# Reservations Property Operations Snapshot Task

Status: implemented locally; deployment evidence is not part of this task

## Goal

Provide one authoritative Reservations read for the current operational state of
one property, coordinated by a property-local calendar date. The response gives
front-desk operators exact Reservation and guest counts, a disjoint attention
breakdown, and a small deterministic upcoming-arrivals list without requiring
clients to download and recompute the reservation directory.

## Ownership and Boundaries

- Reservations owns the read model, cohort definitions, ordinary-record
  visibility rules, authorization contract, and response minimization.
- Properties remains authoritative for property lifecycle and IANA time-zone
  data. Reservations uses its local property projection and fails closed when
  that projection is missing, inactive, or has an invalid time zone.
- Inventory remains authoritative for allocation and release. In particular,
  `CheckoutPending` remains physically in house until Inventory release
  succeeds.
- The snapshot performs no cross-module runtime calls and introduces no
  persistence migration.

## Calendar and Observation Contract

The handler captures `ObservedAtUtc` once. By default, the reader derives
`LocalDate` from that instant and the valid projected IANA `TimeZoneId`, and
returns `DateSource=PropertyTimeZone`. A caller may select an explicit
`localDate`, which returns `DateSource=Explicit`, but the property must still be
known, active, and have a valid projected IANA time zone.

An explicit past or future date changes only the calendar predicates evaluated
against current reservation state. It is not a historical as-of reconstruction.
`CurrentlyInHouse` is always a current-state cohort. Property, Guest restriction,
and reservation changes can be briefly delayed by projection lag; `ObservedAtUtc`
identifies when the snapshot was evaluated, not when every upstream fact was
committed.

## Exact Cohorts

Every count reports both `ReservationCount` and the exact sum of current
`GuestCount` values.

- `ConfirmedArrivalsOnLocalDate`: `Confirmed` and arrival equals `LocalDate`.
- `ScheduledDeparturesOnLocalDate`: `CheckedIn` or `CheckoutPending` and
  departure equals `LocalDate`.
- `CurrentlyInHouse`: `CheckedIn` or `CheckoutPending`.

The top-level cohorts are not mutually exclusive. A scheduled departure can be
currently in house, and a currently-in-house reservation can also need
attention.

Attention is an exact total of seven mutually exclusive categories:

- `PendingAllocation`
- `AllocationRejected`
- `CancellationPending`
- `NoShowPending`
- `CheckoutPending`
- `ArrivalBeforeLocalDateStillConfirmed`
- `DepartureBeforeLocalDateStillInHouse`

The date-relative category names are intentionally descriptive. With an
explicit past or future date they do not assert that an item is operationally
overdue.

## Upcoming Contract

Upcoming items include only `PendingAllocation` and `Confirmed` reservations
whose arrival is on or after `LocalDate`. They sort by arrival, then non-null
expected arrival time before null, then expected arrival time, then reservation
id. The default limit is 25 and the strict range is 0 through 50. A zero limit
returns counts only while `HasMoreUpcoming` still reports whether any matching
item exists. The reader uses one-item lookahead and never returns more than the
echoed `UpcomingLimit`.

Upcoming rows reuse the ordinary minimized reservation list DTO, including its
catalogued `PrimaryGuestName`. Anonymised reservations and reservations without
a current supported, unrestricted Guest processing-restriction projection are
excluded by the same fail-closed rule as ordinary directory reads.

## Surfaces and Failures

- Public API:
  `GET /api/reservations/properties/{propertyId}/operations-snapshot`, tenant
  and property scoped with `reservations.read`.
- Admin API:
  `GET /api/admin/reservations/properties/{propertyId}/operations-snapshot`,
  operation `reservations.operations-snapshot`, `reservations.read`, and the
  host's property resource scope.
- Admin CLI:
  `reservations operations-snapshot --property-id <id>
  [--local-date yyyy-MM-dd] [--upcoming-limit 0..50]` with equivalent
  authorization, audit, and property resource scope.

All responses on the exact public and Admin Reservations path boundaries,
including authentication, authorization, and binding failures before endpoint
execution, receive `Cache-Control: no-store`, `Pragma: no-cache`, and
`Expires: 0`.

Stable failures are `Reservations.PropertyNotFound` (404),
`Reservations.PropertyInactive` (409),
`Reservations.PropertyTimeZoneUnavailable` (503 when the projected time-zone
identifier is missing, invalid, or not an IANA identifier), and
`Reservations.OperationsSnapshotLimitInvalid` (400). The CLI also returns
`Reservations.OperationsSnapshotLocalDateInvalid` for a non-ISO date.

## Scale Contract

This endpoint is an authoritative property-local operational read, not a fleet
aggregate. A regional or fleet dashboard must use a dedicated bounded rollup or
projection. It must not fan out this personal-data response across hundreds of
properties or widen this endpoint into an unbounded multi-property export.

## Verification

- Contract and handler tests freeze field names, limits, observation capture,
  and stable error mapping.
- Reader tests prove time-zone validation, exact cohorts and disjoint attention
  totals, ordinary-record visibility, deterministic lookahead, and limit zero.
- API surface tests freeze public property permission, Admin parity, and
  no-store headers for 200, 400, 401, and 403 pipeline outcomes.
- Admin CLI tests cover the real executor, permission, property resource scope,
  audit operation, JSON parity, the complete seven-category table breakdown,
  and guest counts in upcoming rows.
- Personal-data catalogue v20 independently classifies public and Admin inputs,
  public and Admin outputs, the application query, every snapshot wrapper, and
  the nested ordinary upcoming DTO under the support boundary; the generated
  inventory is checked in and deterministically verified.

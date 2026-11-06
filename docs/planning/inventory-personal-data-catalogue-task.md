# Inventory Personal-Data Catalogue Task

Status: published; local and exact-commit production proof complete

## 2026-09-05 additive context-read amendment

The status above describes the historical published catalogue, not the new
[retirement context read](inventory-retirement-context-read-task.md).
Classify the new lifecycle summary and nullable impact transport/query surfaces
in the executable catalogue and regenerate its inventory. Existing bounded
reason/actor, timestamps and reservation-reference policies/retention remain;
no guest data or support-log disclosure is added. No-store and authorization
tests, generated contracts and exact-candidate release evidence are required.

## Goal

Make Inventory's personal-data boundary explicit and executable without
misclassifying the hostel's physical topology as personal data. The catalogue
must fail when a protected Inventory persistence or transport member is added,
renamed, removed, or moved to a broader boundary without a reviewed policy
entry.

This is the next one-domain slice of SP-001. It does not implement the later
cross-module rights or retention control planes.

## Ownership Boundary

- Inventory owns allocation, availability, manual-block, sales-mode, and
  topology projections.
- Reservations remains authoritative for the reservation and guest record.
  Inventory stores only the pseudonymous reservation linkage and operational
  stay/allocation data required to protect physical capacity.
- Auth and Staff remain authoritative for the person behind an actor subject.
  Inventory stores or transports only the bounded subject correlation required
  for attribution and self-notification suppression.
- Properties remains authoritative for property, room, bed, building, floor,
  label, and time-zone topology.
- BunkFy owns the hospitality purposes, access policy, country key, retention
  key, and rights behavior. GMA continues to own generic messaging, scoping,
  authorization, task, and persistence mechanics.

No Inventory-specific field or policy belongs in GMA.

## Classified Data Families

### Reservation-linked allocation data

Treat the following as guest-linked pseudonymous operational data while the
reservation relationship exists:

- reservation, allocation, request, release, and amendment identifiers;
- arrival/departure dates and lifecycle timestamps;
- property, room, bed, and inventory-unit identifiers in an allocation context;
- allocation status, rejection reason, version, request fingerprint, and unit
  membership;
- allocation identifiers exposed in availability responses;
- affected reservation identifiers returned by topology-impact workflows;
- allocation and availability projection exports; and
- allocation request/outcome integration events exchanged with Reservations.

These records must be available only to the tenant and property-authorized
operational audience or to explicitly bound module consumers.

### Staff-attributed operational changes

Treat the following as staff-linked operational or audit data:

- authenticated actor identifiers on room-mode and manual-block changes;
- `RequestedBy` on room and bed retirement processes;
- block and retirement reasons, which are unstructured and can accidentally
  contain personal data;
- change identifiers, target topology, lifecycle state, rejection codes,
  versions, and timestamps when tied to that attribution; and
- domain and integration events carrying the same change context.

Free-text reasons remain tenant-internal and must never enter notifications,
logs, metrics, traces, or support bundles.

## Explicit Non-Personal Boundary

The following are facility data when they are not joined to a reservation or
staff-attributed operation and therefore are not Inventory-owned personal-data
catalogue fields:

- property, room, bed, building, and floor topology;
- property/room/bed labels and time zone;
- room sales mode and sellability configuration without actor attribution;
- topology source versions, projection ordinals, and rebuild checkpoints; and
- aggregate counts that do not identify or expose a reservation or actor.

The executable tests use explicit protected type sets instead of classifying
every Inventory type. This keeps the distinction reviewable and prevents the
catalogue from becoming a meaningless inventory of all facility state.

## Minimisation Change

`ManualInventoryBlockCreatedIntegrationEvent` currently duplicates the
free-text block reason in the durable outbox and every subscribed inbox even
though no consumer reads it. Remove that member from newly published messages
and increment the event contract version. Keep the reason in Inventory's
aggregate and authorized API response, where it serves the operator workflow.

Keep the actor subject on room-mode and block events for the current
self-notification exclusion contract. It is pseudonymous, bounded, and must not
be resolved to profile data outside the authorized Staff boundary.

## Executable Guards

Add `src/Modules/Inventory/docs/personal-data-catalog.v1.json`, its deterministic
Markdown inventory, and Inventory unit tests that prove:

1. every checked-in binding resolves to an actual public member;
2. every non-shadow persistence member of allocation, amendment-decision,
   manual-block, room-retirement, and bed-retirement records is classified;
3. every member of selected personal API, command, query, export, domain-event,
   and integration-event types is classified;
4. integration-event metadata is covered except the static event name/version
   contract metadata handled by GMA;
5. unstructured reasons cannot enter integration events, notifications, logs,
   metrics, traces, or support bundles;
6. the block-created event no longer exposes the free-text reason;
7. affected reservation identifiers remain bounded by
   `InventoryImpactLimits.AffectedReservationSampleSize`; and
8. the checked-in Markdown exactly matches deterministic catalogue rendering.

## Verification

- focused Inventory unit tests;
- Operations Notifications and Reservations tests affected by the event version
  change;
- architecture tests and solution synchronization;
- warning-free solution build;
- SQL Server and PostgreSQL migration drift checks;
- complete non-Docker and Docker suites;
- direct and transitive package vulnerability audit; and
- exact-commit GitHub validation before marking the slice published.

Current evidence:

- five field definitions resolve 481 concrete member/surface bindings across
  owned persistence, application/API/admin boundaries, projection exports,
  domain events, and produced or consumed integration events;
- all eight Inventory catalogue guards, all 48 Inventory tests, all 53
  Reservations tests, all 21 Operations Notifications tests, and all 58
  architecture tests pass;
- solution synchronization, source-package boundaries, a zero-warning build,
  and every PostgreSQL and GMA migration drift check pass;
- all 2,331 non-Docker tests and all 33 Docker integration tests pass; and
- the direct and transitive package vulnerability audit reports no known
  vulnerable packages.

Published backend commit `c7db6b2e633e88442bad124c6c1f17b20589a2cd`
passed exact Windows and Ubuntu validation in GitHub Actions run
`29904293750`, and all 33 Docker integration tests passed in run
`29904293838`.

## Deferred

- SP-002 export, correction, restriction, erasure orchestration, and deletion
  receipts across Reservations and Inventory.
- SP-003 automatic retention execution, legal-hold state, and backup deletion
  consequences.
- Founder/counsel approval of purposes, country keys, retention periods, rights
  exceptions, and controller/processor allocation.
- Replacement of free-text operational reasons with configured reason codes if
  product research shows the note is not required.

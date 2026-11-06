# Guests Deployed Stay-History Proof Task

Status: implemented, repository verified, and exact-release Preview verified
Date: 2026-08-13

## Goal

Require release-bound evidence for the existing complete durable Guest path:
minimal profile management, a Reservations-owned primary-participant link,
Guests-owned monotonic stay history, terminal Inventory release, and Guest
archive.

## Architecture

- Guests remains authoritative for canonical profiles, management replay,
  archive state, property visibility, and its rebuildable stay projection.
- Reservations remains authoritative for participant links and booking
  lifecycle; Inventory remains authoritative for allocation.
- Cross-module behavior uses the existing PII-free public integration contracts.
  There are no cross-schema writes or foreign keys.
- The BunkFy root operations layer owns deployed orchestration, synthetic fixture
  cleanup, minimized evidence, and production-admission policy.
- GMA already provides the generic tenant, access, messaging, outbox/inbox, and
  API result primitives. No GMA source or pointer change is required.

## Delivery

The root verifier creates and versionedly updates one minimal Guest, proves
stable management replay and fail-closed conflicts, links one allocated
Reservation, observes `Confirmed`, `CheckedIn`, and `CheckedOut` stay states,
archives the Guest, and proves Inventory release. Its retained evidence excludes
personal data and all workspace, property, Inventory, Guest, Reservation, and
stay-date coordinates.

The deterministic fixture covers success, delayed projection convergence,
conflicting replay, stale writes, archive replay drift, cleanup, evidence
minimization, and insecure remote transport rejection. Production admission now
requires this exact-release child proof independently of the broader onboarding
umbrella.

## Verification Cadence

Use focused root fixtures while changing the verifier. Run the complete root
operations and repository-security gates once at the coherent slice boundary,
then run one exact-release Preview rehearsal across API, Worker, PostgreSQL,
NATS, and the module-owned databases. Do not repeat the backend Docker suite for
root-only operations-script changes.

## Deferred

- browser Guest creation and history workflows;
- deduplication, merge, consent, and communication workflows;
- concurrent participant replacement and overbooking contention; and
- hosted-production execution and private approval.

## Evidence

The root Operations gate passed with the new fixture and closed admission
schema. The exact-release VPS Preview run then passed 19 child checks across API,
Worker, PostgreSQL, NATS, Guests, Reservations, and Inventory for release
`preview-workspace-access-estate-651107f`. The minimized child SHA-256 is
`ddea07066941a2e54bc032028142dea2bb79baeb886cb073f1193504d142e18c`.

The first live attempt found a composition-only race: Inventory legitimately
returned `404` before the newly created room projection existed. The root shared
fixture now treats that state as bounded convergence and uses the same wait for
partial cleanup. The focused correction fixture and replacement live run pass;
no Guests, Reservations, Inventory, or GMA module source changed.

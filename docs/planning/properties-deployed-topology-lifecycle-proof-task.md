# Properties Deployed Topology Lifecycle Proof Task

Status: implemented and exact-release deployment verified
Date: 2026-08-13

## Goal

Require release-bound evidence for the existing Properties-owned topology path:
idempotent and optimistic property, room, and bed mutations; explicit Inventory
retirement coordination; and terminal retirement of the empty property.

## Architecture

- Properties remains authoritative for physical topology, versions, mutation
  journals, statuses, and receipts.
- Inventory remains authoritative for sellability, allocations, blocks, and the
  safe retirement process. Direct Properties bed and room retirement must stay
  unavailable to public callers.
- Workspaces owns workspace lifecycle admission. Access Control owns permission
  and scope decisions. Auth owns identity, sessions, and configured assurance.
- The BunkFy root operations layer owns deployed orchestration, synthetic
  cleanup, minimized evidence, Preview composition, and production-admission
  policy.
- GMA already supplies the generic framework boundaries required by this flow;
  no GMA source or pointer change is required.

## Delivery

The root verifier will create one synthetic property, one room, and two beds;
exercise exact replay, changed operation reuse, and stale versions; prove direct
topology retirement fails closed; retire one bed and then the room through
Inventory; and retire the empty property. Passing evidence will exclude all
identifiers, facility labels, operation coordinates, reasons, policy values,
credentials, and response bodies.

Preview composition will run the proof before invitation acceptance so a
distinct authenticated nonmember is available for the tenant-denial assertion.
The child will own complete retirement of its synthetic property and topology.
Production admission will require the minimized child independently of the
broader onboarding umbrella.

## Governance Boundary

The synthetic proof will not choose or activate a country policy. Production
activation requires separately approved country, region, transfer, retention,
and acknowledgement evidence. Preview's engineering/example policy remains
composition-only evidence where another contributor requires processing.

## Verification Cadence

Use focused root fixtures while changing the verifier. Run the complete root
operations gate once at the coherent slice boundary, then run one exact-release
Preview rehearsal across the edge, API, Worker, PostgreSQL, NATS, Properties,
and Inventory. Do not repeat the backend Docker suite for root-only operations
script changes.

## Deferred

- browser Properties workflows;
- approved country-policy activation, suspension, and rebinding;
- occupied or blocked topology drain and affected-reservation review;
- high-contention and large-directory performance rehearsal;
- tenant termination execution; and
- hosted-production execution and private approval.

## Acceptance

- Focused verifier and production-admission fixtures pass.
- The complete root operations gate passes once.
- One exact-release Preview rehearsal leaves its synthetic property, room, and
  beds retired and produces minimized child evidence.
- Loopback Preview evidence remains explicitly distinct from hosted-production
  proof.

## Completion

The BunkFy root now owns a 31-check public-API verifier, deterministic replay
fixture, Preview composition, closed production-admission source, static guards,
and operator documentation. Exact release
`preview-workspace-access-estate-651107f` passed the deployed topology workflow,
and the full root operations gate passed once afterward.

No backend or GMA source change was needed. The existing Properties contracts
already provide stable mutation receipts and optimistic versions, while
Inventory already owns asynchronous bed and room retirement and Properties
rejects direct public retirement. Country-policy selection remains deliberately
outside this synthetic proof.

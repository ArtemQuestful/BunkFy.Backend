# Staff Deployed Employment Lifecycle Proof Task

Status: complete
Date: 2026-08-13

## Goal

Require release-bound evidence for the existing Staff-owned employment path:
minimal unlinked profile management, optimistic and idempotent mutation,
property assignment, suspension and resume, and terminal departure with atomic
assignment closure.

## Architecture

- Staff remains authoritative for employment profiles, assignments, lifecycle
  state, versions, mutation operation records, and receipts.
- Workspace membership, auth subjects, sessions, roles, grants, and effective
  authorization remain outside Staff. The deployed invitation and enrollment
  workflows prove those boundaries independently.
- Staff consumes the Properties-owned availability projection and communicates
  through existing public contracts and integration events. There are no
  cross-schema writes or foreign keys.
- The BunkFy root operations layer owns deployed orchestration, synthetic
  fixture cleanup, minimized evidence, and production-admission policy.
- GMA already supplies the reusable tenancy, authorization, CQRS/result,
  messaging, outbox/inbox, task, and time primitives. No GMA source or pointer
  change is required.

## Delivery

The root verifier will create one synthetic profile without personal contact
data, prove exact replay and fail-closed operation conflicts, update it
versionedly, assign one property, suspend and resume it, and finally depart it.
The terminal assertion will require the assignment to be closed and absent from
the current property directory. Retained evidence will exclude all identifiers,
personal data, labels, reasons, dates, credentials, and response bodies.

Preview composition will run the proof before invitation acceptance so a
distinct authenticated nonmember is available for the tenant-denial assertion.
Production admission will require the minimized child evidence independently of
the broader onboarding umbrella.

## Verification Cadence

Use focused root fixtures while changing the verifier. Run the complete root
operations gate once at the coherent slice boundary, then run one exact-release
Preview rehearsal across the edge, API, Worker, PostgreSQL, NATS, and the
module-owned databases. Do not repeat the backend Docker suite for root-only
operations-script changes.

## Deferred

- account linking, membership offboarding, role grants, and session revocation;
- browser Staff-management workflows;
- governance, restrictions, holds, anonymisation, and retention;
- high-contention and large-directory performance rehearsal; and
- hosted-production execution and private approval.

## Acceptance

- Focused verifier and production-admission fixtures pass.
- The complete root operations gate passes once.
- One exact-release Preview rehearsal reaches a departed Staff record with no
  current property assignment and produces minimized child evidence.
- Loopback Preview evidence remains explicitly distinct from hosted-production
  proof.

## Evidence

The root superproject now carries a 21-check deployed verifier, deterministic
success and replay-drift fixtures, Preview composition, cleanup enforcement, and
production-admission parsing for the existing Staff public contracts. The
focused Staff and admission fixtures and the complete root operations gate
passed.

The exact-release Preview run passed on 2026-08-13 for
`preview-workspace-access-estate-651107f`: the synthetic unlinked profile ended
departed with one closed historical assignment and zero current assignments.
The minimized child SHA-256 is
`1ddecd9a9e699855c554b7d72813dd4a8208aa0ee0dacfe3762f08954485c54a`.
This is loopback deployment composition evidence and does not represent hosted
production approval.

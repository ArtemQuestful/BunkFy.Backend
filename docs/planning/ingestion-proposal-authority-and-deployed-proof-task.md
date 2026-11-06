# Ingestion Proposal Authority And Deployed Proof Task

Status: implemented and exact-release deployment verified
Date: 2026-08-13

## Goal

Make the newest admitted external reservation suggestion the only actionable
pending proposal for its source graph, then require exact-release evidence for
the complete adapter-baseline, staff-conflict, operator-decision lifecycle.

## Finding

Ingestion already dispatches automatic changes with the last accepted adapter
`DetailsRevision`, and Reservations converts a mismatched revision into a safe
proposal outcome. Proposal acceptance, rejection, exact replay, and
race-to-stale handling are covered. However, the existing `Superseded` state is
not reached when a newer ordered source observation creates another proposal,
so an older suggestion can remain actionable.

## Architecture

- Ingestion owns source ordering, source links, accepted operational baselines,
  dispatches, proposals, decisions, supersession, and retained sensitive
  history.
- Reservations owns the current reservation, staff edits, `DetailsRevision`,
  external-operation idempotency, final validation, and applied history.
- Inventory owns allocation feasibility and atomic allocation amendment.
- The existing Ingestion source-graph transaction lock serializes observation,
  outcome, and decision mutations for one source. Proposal supersession is an
  application invariant inside that boundary and requires no persistence schema
  or framework change.
- The root operations repository owns synthetic deployed orchestration,
  minimized evidence, Preview cleanup, and production admission.

## Delivery

1. Add a repository query for pending proposals scoped to connection and
   reservation.
2. Before creating a newer proposal, terminally supersede prior pending
   proposals with the existing retention policy and a fixed system reason.
3. Preserve applying and all terminal proposals; newer input remains deferred
   while a product operation is active.
4. Cover both direct review-policy proposal creation and reservation revision-
   conflict proposal creation, plus replay and decision behavior.
5. Expose the already-public superseded status in the web review history.
6. Add root exact-release proof without widening module contracts solely for
   testing.

## Verification Cadence

Use focused non-Docker Ingestion tests while editing. At the coherent boundary,
run the relevant backend/web gate once, the complete operations gate once, and
one exact-release Preview rehearsal. Do not use GitHub Actions as an
edit-by-edit verifier.

## Deferred

- field-level merge policy;
- a provider-specific or GMA-level proposal abstraction;
- production provider data and credentials;
- hosted-production approval; and
- load-test evidence beyond the serialized source graph and bounded projections.

## Acceptance

- Only the newest pending proposal remains actionable for a source graph.
- Superseded proposal history has terminal retention metadata and cannot be
  accepted or rejected.
- Staff state remains unchanged until an explicit successful accept operation.
- Existing automatic application and race-to-stale semantics remain intact.
- Exact-release deployed evidence proves the public workflow and terminal
  cleanup without retaining personal or secret data.

## Completion

Ingestion now supersedes every older pending proposal for the exact connection,
external source, and reservation graph before publishing a newer actionable
proposal. Both review-only dispatch and Reservation revision-conflict outcomes
use the same application service inside the existing source-graph transaction.
Superseded proposals retain the configured sensitive-history deadline and
cannot be decided; a distinct source that points at the same reservation remains
independent. No schema or GMA change was required.

The focused dispatch suite passed 14/14, the full Ingestion suite passed
339/339, and Architecture passed 103/103. Web verification passed 277 tests plus
type checking, lint, and production build. The root operations gate passed once
in 188.8 seconds, including the deterministic supersession-drift cleanup case.

Exact release `preview-ingestion-proposal-844f4c4` passed the 26-check public
conflict/proposal lifecycle. It preserved authority revisions Adapter 1,
Adapter 2, Staff 3, Adapter 4; retained one superseded, one rejected, and one
applied proposal with zero pending; then cancelled the reservation, revoked the
credential, and disabled the connection. This loopback Preview result proves
the committed BunkFy composition, not hosted production or provider and country
policy approval.

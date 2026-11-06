# Data Rights Mutation Continuity Task

Status: implemented and verified
Date: 2026-08-15

## Goal

Make processing-restriction execution and correction claims recoverable without
weakening their frozen approval coordinates, and give operators exact failure
and claim-ownership semantics.

## Problems Found

- Restriction and correction owner lookup currently collapses a missing owner,
  duplicate registration, malformed metadata, and incompatible contract
  registration into one `409` business conflict.
- Restriction owner exceptions, declared failures, malformed results, and
  genuine business blockers do not have distinct retry or server-fault
  semantics.
- An expired correction claim can be renewed only by its original actor. If
  that operator loses access or leaves the workspace, the case remains
  `Executing` indefinitely.
- The correction editor is shown to any operator with execution permission,
  even while a different actor owns the active claim.
- An owner mutation admitted just before claim expiry can commit just after the
  deadline. The owner data and receipt are durable, but Data Rights currently
  rejects that delayed completion proof and cannot converge the case.

## Boundary Decisions

### GMA Framework

No GMA change is required. CQRS transactions, scoped authorization, recent
authentication assurance, result mapping, and durable outbox delivery are
already generic and sufficient. Owner catalogues, privacy-case claims, and
operator takeover rules remain BunkFy Data Rights behavior.

### Data Rights Contracts

- Restriction contributor metadata and result envelopes are bounded and
  validated before use.
- Correction policy contributors are identified by exact normalized owner and
  record-type coordinates, with one current-version registration per pair.
- Correction execution details report whether the requesting actor owns the
  current claim. They do not grant access; owner endpoints continue to call the
  server-side execution gate immediately before mutation.
- The exposed actor reference is named `ClaimedBy`: it describes coordination
  ownership, not the actor attribution of a delayed owner receipt. Actual
  mutation attribution remains authoritative in the owning module.

### Owner Catalogue And Failure Taxonomy

- A well-formed catalogue without the required owner is unavailable (`503`).
- Duplicate, malformed, inaccessible, or incompatible registrations are an
  invalid server catalogue (`500`).
- A well-formed restriction blocker is an actionable state conflict (`409`).
- A well-formed declared owner failure is retry-required (`503`) and carries
  the bounded dependency `Retry-After` policy.
- An unexpected owner exception is retry-required (`503`) and carries the
  bounded dependency `Retry-After` policy.
- A malformed owner result or invalid proof is a server fault (`500`).
- Cancellation is never converted into an owner failure.
- Safe logs identify only the bounded owner key and exception type; they do not
  include subject coordinates, personal values, outcome text, or exception
  messages.

## Correction Claim Continuity

- An active claim remains exclusive to its recorded actor. Another operator
  cannot submit values, renew it, or take it over before expiry.
- The owning actor may replay the exact active claim without changing it.
- After expiry, any separately authorized operator with recent authentication
  may renew or take over the exact persisted claim by supplying its existing
  execution id and frozen selected-case version.
- Takeover changes only the current claim actor and time window. Case,
  approval, subject, field policy, selected versions, and execution id remain
  immutable.
- Serialization under the existing case mutation lock permits only one
  renewal or takeover winner.
- Owner receipt idempotency remains keyed by the unchanged execution id. If the
  previous actor already committed, equivalent replay returns that receipt and
  a different payload conflicts rather than mutating twice.

## Expiry And Completion Semantics

- Claim expiry fences admission of a new owner mutation; every owner must pass
  `IDataRightsCorrectionExecutionGate` before its authoritative transaction.
- A completion event is proof of an owner mutation already admitted by that
  gate. Delivery may occur after expiry, renewal, or takeover and must still
  converge when its case, approval, execution, subject, policy, record
  revision, receipt, and digest coordinates match.
- Completion still rejects proofs before the case execution began, proofs from
  the future beyond the bounded clock-skew policy, changed coordinates, changed
  receipt content, and duplicate delivery with different proof.

## Operator Experience

- The current claim owner sees the owner-specific correction editor.
- A different operator sees who owns the window only as an existing bounded
  actor reference and cannot edit while it is active.
- On expiry, the original actor is offered renewal and another authorized actor
  is offered takeover. Both actions reuse the displayed execution id and
  selected-case version.
- Dependency-unavailable, catalogue-invalid, stale/conflict, and permission or
  assurance failures remain distinguishable and do not discard the current
  retry coordinates.

## Verification Cadence

1. Add focused Data Rights domain, handler, endpoint, and contract tests while
   implementing the backend invariants.
2. Add focused web workflow and component tests for claim ownership, renewal,
   takeover, and error messaging.
3. Run one consolidated non-Docker backend and web gate after the slice is
   coherent.
4. Run Docker/provider verification only if persistence or provider behavior
   changes; otherwise retain the latest coherent migration evidence.
5. Publish backend, web, and root pointers only after the slice gate is green.

## Completion Criteria

- owner catalogue absence and defects have exact tested HTTP semantics;
- restriction blockers, retryable exceptions, and invalid proof are distinct;
- an active correction claim cannot be stolen;
- an expired correction claim can be recovered by another assured operator;
- delayed valid owner proof completes the case after expiry or takeover;
- the UI never presents another actor's active claim as editable;
- focused and consolidated guards pass; and
- no BunkFy-specific behavior is moved into GMA.

## Verification

- Focused restriction failure and contributor-catalogue tests passed (`9/9`).
- Focused correction continuity and catalogue tests passed (`24/24`).
- Focused web workflow and rendered-component tests passed (`9/9`), followed
  by changed-file lint and full type checking.
- `eng/verify.ps1 -SkipRestore -SkipSubmoduleFetch` passed on 2026-08-15,
  including the full non-Docker backend matrix, migration-drift checks, `534`
  Data Rights tests, generated OpenAPI contract verification, `294` web tests,
  lint, type checking, and the production web build.
- Docker/provider tests were intentionally not repeated because this slice did
  not change persistence mappings, migrations, or provider behavior.

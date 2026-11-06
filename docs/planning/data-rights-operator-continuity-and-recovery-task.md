# Data Rights Operator Continuity And Recovery Task

Status: implemented
Date: 2026-08-15

## Goal

Keep privacy-case discovery and review trustworthy when an operator edits search
criteria, a request completes out of order, or a required companion owner cannot
complete review preparation.

## Ownership

- Data Rights owns case orchestration, selected opaque coordinates, public API
  failure semantics, and the controller-facing workflow.
- Owner modules continue to own exact discovery, subject validation, personal
  records, required-companion contributions, and operation receipts.
- The web client may cancel obsolete reads and fence late responses, but it
  never treats a displayed candidate as authoritative. Selection and review
  remain server-revalidated against the latest case and owner revisions.
- GMA continues to provide generic CQRS, result-to-HTTP, access-control, and
  client error primitives. No BunkFy privacy vocabulary or retry policy moves
  into GMA in this slice.

## Findings

- Subject discovery currently closes over mutable form state. Editing an exact
  identifier does not invalidate the displayed candidates, and a late response
  from a prior request can replace the current result.
- Selection already fails closed: the Data Rights handler reacquires the case,
  checks its expected version, and asks the owner module to validate the opaque
  coordinate and record revision. The gap is operator continuity, not source
  authority or domain integrity.
- Review expands required companion coordinates atomically before changing the
  case. Its four companion failure modes currently fall through to HTTP 400,
  which incorrectly describes business blocks, temporary dependency failures,
  and invalid internal contributor results as request validation failures.
- The shared web API client already parses `Retry-After`; a bounded Data
  Rights-specific response header can therefore be added without changing GMA.

## Decisions

- Snapshot normalized discovery criteria into every request, including case ID
  and case version, and render a response only while its generation and
  fingerprint still match the current form and case.
- Abort the previous discovery request when criteria, case identity, or case
  version changes. Aborted and late requests must not surface candidates,
  empty-result messages, limits, or errors.
- Keep discovery criteria in component memory only. Do not put personal lookup
  values in URLs, storage, query keys, logs, or telemetry.
- Preserve server-side owner revalidation and optimistic case concurrency as
  the authority boundary.
- Map a companion business block to HTTP 409, unavailable or explicitly
  retryable companion processing to HTTP 503, and an invalid contributor result
  to HTTP 500.
- Add a short bounded `Retry-After` only for the explicit retry-required result.
  Missing composition is unavailable but not advertised as an immediate retry.
- Make no schema, broker, Docker, GMA, or owner-module implementation change.

## Delivery

- [x] Add explicit companion failure mappings and retry-header behavior.
- [x] Add focused API coverage for every companion failure class.
- [x] Add a pure discovery-attempt snapshot/fingerprint helper.
- [x] Fence and cancel obsolete discovery work in the privacy request UI.
- [x] Add focused helper and source-wiring coverage.
- [x] Align the Data Rights development note and complete one slice gate.

## Deferred

- Review and destructive-action confirmation content drift belongs to a
  separate operator-decision slice; it does not share this request-lifetime
  boundary.
- Automatic retries are intentionally excluded. Privacy operators must retain
  control over when review is attempted again after a dependency failure.
- A generic GMA retry-policy abstraction should be considered only after
  multiple projects demonstrate the same error taxonomy and header policy.

## Completion Criteria

- edited criteria and changed case versions hide prior candidates immediately;
- late or aborted requests cannot repopulate discovery state or errors;
- a current successful request alone may show candidates, limit state, or an
  empty result;
- all candidate selections are still revalidated by the owning module;
- companion blocks, outages, retry signals, and contract defects have distinct
  HTTP semantics, with `Retry-After` only on the explicit transient signal;
- focused Data Rights and web tests pass, followed by one coherent non-Docker
  backend/web gate at the slice boundary.

## Verification Cadence

Use focused Data Rights and web tests while editing. Run the repository-wide
non-Docker backend gate and the complete web gate once after the slice is
coherent. Do not run Docker or GitHub Actions for this slice because it changes
no schema, provider-specific behavior, broker contract, or release tooling.

## Completion Evidence

- Focused Data Rights API coverage passed 8/8, including all four companion
  failure mappings and bounded retry-header behavior.
- Focused discovery-attempt and frontend foundation coverage passed 20/20;
  targeted TypeScript and lint checks were clean.
- The consolidated backend gate synchronized the solution, passed
  source-package guards, built the complete graph with zero warnings and zero
  errors, and reported no migration drift.
- All 5,194 non-Docker backend tests passed, including Data Rights 483/483,
  Operations Notifications 104/104, Architecture 112/112, and Integration
  60/60.
- The complete web gate passed TypeScript, lint, all 54 test files and 281
  tests, and the production Vite build. The OpenAPI snapshot and generated web
  contracts are current.
- No Docker or GitHub Actions run was required because this slice changes no
  schema, provider-specific behavior, broker contract, or release tooling.

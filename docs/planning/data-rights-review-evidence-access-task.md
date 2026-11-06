# Data Rights Review Evidence Access Task

Status: implemented and repository-verified
Date: 2026-08-23

## Goal

Let an authorized privacy-case reviewer inspect the exact selected record scope
before moving or deciding a case, without granting the reviewer broader subject
discovery authority.

## Ownership

- Data Rights owns case lifecycle, selected opaque record coordinates, pinned
  record versions, review evidence, decisions, and controller-facing policy.
- Owner modules continue to own subject lookup indexes, personal records,
  record visibility, mutation, holds, and execution receipts.
- Workspaces owns configurable permission profiles and their dependency graph.
- GMA supplies generic exact-scope permission enforcement and CQRS/API
  primitives. No BunkFy privacy vocabulary or review policy moves into GMA.

## Finding

- `data-rights.review` intentionally does not imply `data-rights.discover`:
  reviewing selected scope is narrower than searching owner modules for people
  or records.
- The current selected-subject endpoint and web query both require Discover.
  A valid custom reviewer or decision-maker can therefore advance a case while
  seeing only a selected-record count, not the coordinates and versions being
  reviewed.
- Granting Discover to every reviewer would broaden access to subject lookup
  and selection mutation. The missing capability is a minimized, read-only
  review-evidence surface owned by Data Rights.

## Decisions

- Add property- and tenant-scoped `review-evidence` reads guarded by Review at
  the exact case scope.
- Reuse the existing selected-subject response contract: owner key, record
  type, record ID, pinned version, selection time, and current case version.
  Do not expose lookup criteria, candidate personal data, or selecting actor.
- Retain the existing Discover-guarded subject read, lookup, select, and
  unselect endpoints unchanged.
- In the web detail flow, use review evidence whenever Review is available and
  fall back to the existing selected-subject read for discovery-only operators.
- Treat required review evidence as fail closed: review and decision actions
  stay unavailable while the evidence is loading, stale, or failed, with a
  visible recovery path.
- Bind the evidence query to the current case version so lifecycle and
  selection changes cannot leave an apparently current review surface.
- Make no schema, owner-module, broker, Docker, or GMA change in this slice.

## Delivery

- [x] Add exact-scope review-evidence endpoints and API security coverage.
- [x] Align generated web contracts without hand-editing generated files.
- [x] Select the least-privileged evidence endpoint from current capabilities.
- [x] Gate review and decision actions on current evidence availability.
- [x] Add focused backend and web regression coverage.
- [x] Update the Data Rights development note and run one coherent slice gate.

## Invariants

- Review access never grants subject search, candidate disclosure, selection,
  or unselection authority.
- Property evidence cannot cross property or tenant scope; tenant evidence
  cannot escape the active tenant.
- Returned coordinates come only from the current Data Rights case aggregate
  and preserve the versions pinned during selection.
- Sensitive responses remain non-cacheable.
- Missing, forbidden, stale, or failed evidence cannot silently degrade into a
  count-only decision workflow.
- Existing optimistic concurrency, confirmation pinning, owner revalidation,
  and execution rules remain authoritative.

## Deferred

- Rich owner-specific summaries or personal-data previews. They require a
  separately minimized owner contract and privacy review; opaque coordinates
  are sufficient for this access-integrity correction.
- Changing the Workspaces permission dependency graph. Review remains a
  deliberately narrower capability than Discover.
- A generic framework concept for privacy review evidence. The policy and data
  shape are BunkFy-specific, while GMA already provides the needed permission
  enforcement primitive.

## Completion Criteria

- a Review-capable operator can inspect selected case coordinates without
  Discover;
- a Read-only operator cannot access review evidence;
- a Review-only operator still cannot discover, select, or unselect subjects;
- review and decision commands cannot be offered against unavailable or stale
  evidence;
- property and tenant routes expose equivalent minimized behavior and no-store
  headers;
- focused Data Rights/API/web tests and the consolidated non-Docker slice gate
  pass.

## Verification Cadence

Use focused Data Rights API and web workflow tests while editing. At the
coherent slice boundary, run the full non-Docker backend gate and web
lint/tests/typecheck/build/contracts check once. No Docker scenario is required
because this slice changes no schema, provider-specific behavior, or broker
contract.

## Completion Evidence

- The generated backend and root workspace solutions are synchronized, and all
  edited repositories pass `git diff --check`.
- The consolidated non-Docker backend gate passed its bootstrap, security,
  solution, operations-fixture, zero-warning build, generated-contract,
  source-package, migration-drift, and module/framework test stages. Its first
  pass correctly stopped on the new task page missing from the backend
  documentation index; after indexing the page, the focused documentation guard
  and the complete Architecture suite passed (113 tests).
- Data Rights passed 605 tests, Integration passed 65 tests, Host.Migrations
  passed 26 tests, and Host.ServiceDefaults passed 64 tests.
- The web gate passed type checking, lint, 59 test files with 307 tests, the
  production build, and generated-contract drift verification.
- Focused Data Rights API security coverage passed 16 tests while editing.
- No Docker scenario was run because the slice changes no schema,
  provider-specific behavior, broker contract, or deployment topology.

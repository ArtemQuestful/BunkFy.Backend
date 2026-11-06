# Data Rights Subject Owner Failure Taxonomy Task

Status: complete
Date: 2026-08-15

## Goal

Give privacy operators truthful, retry-safe failure semantics when Data Rights
discovers or revalidates records through an owning module.

## Ownership

- Data Rights owns contributor discovery, result validation, public error
  semantics, and whether an operator retry may be advertised.
- Owner modules own their records, scope projections, exact lookup and
  coordinate revalidation. They return only bounded Data Rights contracts.
- GMA continues to provide generic result-to-HTTP and error payload mechanics.
  BunkFy owner taxonomy and retry policy remain in the Data Rights module.

## Findings

- Missing, duplicate, and malformed subject contributors currently collapse to
  `SubjectOwnerUnavailable`, which the public API maps to HTTP 409.
- An owner exception escapes as an unclassified HTTP 500, while the contributor
  contract cannot explicitly request a retry.
- Null, contradictory, oversized, or malformed owner results are sometimes
  reported as `DiscoveryScopeUnavailable` or `SubjectCoordinateInvalid`, which
  incorrectly blames current scope or operator input.
- Selection already revalidates owner coordinates under the case mutation
  boundary, but it does not validate the incoming coordinate shape before
  choosing a contributor.
- Direct discovery query endpoints bypass the existing helper that emits a
  bounded `Retry-After` for explicit dependency retries.

## Decisions

- Preserve `DiscoveryScopeUnavailable` as HTTP 409 for a valid request whose
  authoritative owner projection cannot serve the requested scope.
- Treat a missing compatible owner as service unavailability and return HTTP
  503 without promising an immediate retry.
- Treat contributor exceptions and explicit retry results as HTTP 503 with one
  short bounded `Retry-After`; never retry privacy discovery automatically.
- Treat malformed or duplicate contributor metadata and invalid or
  contradictory contributor results as internal contract defects and return
  HTTP 500.
- Keep malformed incoming coordinates as HTTP 400, not owner failures.
- Log only owner key and exception type for failed owner calls. Never log
  lookup criteria, record coordinates, candidate previews, or other PII.
- Extend the Data Rights-owned contributor contract with explicit retry status;
  do not add BunkFy privacy vocabulary to GMA.

## Delivery

- [x] Add subject-owner catalogue, retry, and invalid-result errors.
- [x] Validate discovery and selection results by exact status/payload shape.
- [x] Catch non-cancellation owner failures with PII-minimal logging.
- [x] Validate incoming selection coordinates before contributor dispatch.
- [x] Apply bounded retry headers to both direct discovery queries and commands.
- [x] Add focused contract, handler, API, and architecture coverage.
- [x] Align Data Rights documentation and complete one backend-focused gate.

## Deferred

- Export contributor catalogue and asynchronous generation failure semantics
  remain a separate slice because they cross Task Runtime and protected object
  storage boundaries.
- Restriction execution and correction policy contributor semantics remain a
  separate slice because they cross recent-authentication and durable owner
  receipt boundaries.
- Automatic retry is intentionally excluded. The operator decides when to
  repeat a privacy lookup after the advertised delay.
- A generic GMA dependency taxonomy remains premature until multiple products
  prove identical contracts and retry behavior.

## Completion Criteria

- operator, dependency, and contributor-contract failures have distinct public
  status codes;
- only explicit/transient owner retry publishes `Retry-After`;
- cancellation still propagates and no sensitive lookup data is logged;
- invalid owner payloads cannot be mistaken for bad operator coordinates;
- selection mutation remains fail-closed and atomic;
- focused coverage passes before one coherent non-Docker backend gate.

## Verification Cadence

Use focused Data Rights contract, handler, and API tests while editing. Run one
complete non-Docker backend gate only after the slice is coherent. Do not run
the web, Docker, or GitHub Actions gates because this slice changes no web,
schema, provider, broker, or release-tool behavior.

## Verification

- focused subject discovery, review-closure, and API taxonomy suite: 48 passed;
- direct property- and tenant-route retry-header proof after real DTO
  serialization: 1 passed;
- `pwsh -NoProfile -File eng/verify.ps1 -SkipRestore`: solution and source
  guards passed, build completed with 0 warnings and 0 errors, every migration
  drift check passed, and all 5,217 non-Docker tests passed, including 506 Data
  Rights tests and 112 architecture tests;
- web, Docker, and GitHub Actions gates were intentionally not run under the
  slice boundary above.

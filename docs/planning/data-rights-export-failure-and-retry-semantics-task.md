# Data Rights Export Failure And Retry Semantics Task

Status: complete
Date: 2026-08-15

## Goal

Make protected access-export generation fail closed without wasting retries,
keep transient recovery automatic and visible, and make operator retry atomic,
version-pinned, and replay-safe.

## Problems Found

- Missing owners, malformed contributor catalogues, stale subjects, invalid
  owner results, and transient owner failures currently collapse into broad
  failure paths.
- Every generation failure is written to the artifact immediately and then
  retried by Task Runtime. The UI stops polling while an automatic retry is
  already pending.
- Repeated retry requests can publish distinct events and create competing task
  runs for one artifact.
- The owner export contract has no explicit retry-required result and does not
  require zero records for non-success outcomes.
- Unhandled owner exceptions are not logged with a safe operational identity.

## Boundary Decisions

### GMA Framework

GMA provides only generic terminal task failure and final-attempt context. It
does not know about Data Rights, owners, artifacts, exports, or operator retry.

### Data Rights Contracts

- `DataRightsSubjectExportStatus` gains explicit `RetryRequired`.
- Every non-success result must report zero records.
- Owner catalogues are validated before generation: owner key, supported case
  types, descriptor shape, bounded schema identifiers, and per-case owner
  uniqueness are exact contracts.

### Data Rights Application

- Missing required owner is unavailable; malformed or duplicate registration
  is an invalid catalogue.
- Explicit retry-required and unexpected owner exceptions are retryable.
- Stale/not-found/scope-unavailable, malformed output, limits, invalid
  coordinates, and catalogue defects are terminal for that approved snapshot.
- Retryable failures leave the artifact `Generating` while automatic attempts
  remain. Only the final attempt records `Failed`.
- Owner exception logs contain owner key and exception type, never subject
  coordinates, field values, or exception messages.

### Operator Retry

- Initial creation remains `POST .../{caseId}/export` with its creation
  idempotency key.
- Manual retry is a distinct artifact endpoint and command carrying exact case
  and artifact versions.
- `Failed -> Requested` is an aggregate transition. It clears previous
  generation coordinates and failure code, records the failed artifact version
  as `LastRetryBaseVersion`, and increments concurrency version.
- Replays carrying the same base version return the current artifact without a
  second event, even if the task has already started or failed again.
- A new retry after another terminal failure must carry that new artifact
  version. Expired, available, active, stale-case, or mismatched-artifact
  retries fail closed.
- Artifact update, audit fact, and outbox event remain in one module
  transaction.

## HTTP And UI Behavior

- Missing owner maps to `503`; invalid catalogue/result maps to `500`.
- Explicit synchronous retry-required responses carry the existing bounded
  `Retry-After` policy where applicable.
- The Privacy operator UI calls the retry endpoint with the displayed artifact
  version. A successful retry returns `Requested`, resumes polling, and
  disables duplicate retry action while work is active.
- Internal generation failure codes remain absent from the ordinary operator
  DTO.

## Persistence

- Add nullable `LastRetryBaseVersion` to the export artifact with a positive
  bounded check when present.
- Initial artifacts have no retry base. Manual retry resets generation fields
  to the same shape as an initial `Requested` artifact.
- Add provider migrations and preserve migration-drift verification.

## Verification Cadence

1. Focused GMA Tasks tests while implementing the generic dependency.
2. Focused Data Rights aggregate, handler, task, contributor, endpoint, and web
   tests while implementing the adopter slice.
3. One consolidated non-Docker repository gate after both halves are coherent.
4. One Docker/provider gate only if migration/runtime behavior is not already
   covered by the coherent end-of-slice gate.
5. Publish dependencies in order: GMA Framework, Skeleton pointer, BunkFy
   backend, web, then root candidate.

## Verification

- the synchronized product workspace builds with zero warnings and zero
  errors;
- migration drift is clean for every configured provider project, including
  Data Rights;
- all 524 Data Rights module tests pass;
- all 112 architecture guards and all 60 non-Docker product integration tests
  pass;
- the PostgreSQL upgrade test applies the latest Data Rights migration over an
  existing schema and passes;
- generated web contracts match the combined backend, and frontend typecheck,
  lint, all 291 tests, and the production build pass; and
- GMA Framework, Organizations, and the Skeleton pointer guard are published
  and aligned on their `dev` branches.

## Completion Criteria

- permanent failures execute once and become terminal;
- transient failures remain polling-visible and fail only after the final
  configured attempt;
- one artifact version can enqueue at most one operator retry;
- catalogue and owner-result taxonomy is exact and tested;
- PostgreSQL migration, frontend polling, architecture guards, and focused
  recovery tests pass; and
- no BunkFy-specific behavior leaks into GMA.

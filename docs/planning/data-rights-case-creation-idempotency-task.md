# Data Rights Case Creation Idempotency Task

Status: implemented
Date: 2026-08-15

## Goal

Prevent an ambiguous response or transient client failure from creating more
than one privacy case for the same operator submission.

## Ownership

- Data Rights owns case identity, creation intent, tenant isolation, actor
  attribution, and replay conflict semantics.
- The client owns one opaque operation identifier per normalized submission
  attempt. It may retry that attempt, but it cannot declare a replay valid.
- PostgreSQL transaction-scoped case locking remains the cross-instance
  serialization boundary. No process-local lock or separate idempotency table
  is introduced.
- GMA continues to provide generic transactional CQRS and keyed EF locking. No
  privacy-case vocabulary or BunkFy replay policy moves into the framework.

## Findings

- The property and tenant case-create routes currently allocate a new aggregate
  identifier on every request.
- The web mutation has no stable replay key. If the server commits and the
  response is lost, retrying creates a second case with the same intent.
- Data Rights already serializes all case writers with a tenant-qualified,
  transaction-scoped case lock and reloads authoritative state after locking.
- A Data Rights case already stores every immutable creation coordinate needed
  to validate a replay: scope, case kind, requested operations, restriction
  directive, requester relationship, and creator actor.

## Decisions

- Require a non-empty `operationId` on both public case-create routes and use it
  as the aggregate identifier.
- Validate the request before binding the operation, then acquire the existing
  case write lock and reload by that identifier.
- Return the current case DTO for an exact replay, even if the case has since
  advanced. This lets a client converge after losing the original response.
- Bind replay to the normalized creation request and the original creator. A
  reused identifier with changed intent or actor fails with HTTP 409.
- Resolve deadline policy only for a genuinely new case. Failed validation or
  policy-independent intake failure must not bind the operation identifier.
- Keep the browser attempt in component memory. Reuse it for a failed retry,
  allocate a new identifier when scope or intent changes, and clear it after a
  successful create or a fresh modal session.
- Make no schema, migration, Docker, broker, owner-module, or GMA change.

## Delivery

- [x] Add the operation identifier to API and application contracts.
- [x] Serialize create and replay through the existing case mutation boundary.
- [x] Add aggregate-owned normalized creation matching.
- [x] Add explicit invalid-operation and replay-conflict errors and HTTP maps.
- [x] Add focused handler, mutation-boundary, validator, API, and web tests.
- [x] Regenerate OpenAPI-derived web contracts and align module documentation.
- [x] Complete one coherent non-Docker backend/web gate at the slice boundary.

## Deferred

- Pinning destructive-action confirmation to the exact reviewed case revision
  is the next operator-continuity slice.
- Public dependency error taxonomy remains a separate audit because changing
  unavailable, blocked, and invalid-owner semantics has a broader API surface.
- Durable browser persistence across reloads is intentionally excluded. A modal
  retry after an ambiguous response reuses the key; a deliberately abandoned
  and restarted submission is a new operator attempt.

## Completion Criteria

- one normalized submission attempt creates at most one case across concurrent
  or sequential retries;
- an exact replay returns the authoritative current case without evaluating
  deadline policy or adding another aggregate;
- changed scope, intent, or creator cannot reuse an existing operation key;
- an empty key is rejected before lock or repository access;
- every online Data Rights case writer, including creation, uses the canonical
  mutation boundary;
- the client reuses the key only while the same submission attempt remains
  active;
- focused tests pass before one complete backend/web slice gate.

## Verification Cadence

Use focused Data Rights and web tests while editing. Run the repository-wide
non-Docker backend gate and the complete web gate once after the slice is
coherent. Do not run Docker or GitHub Actions because this slice changes no
schema, provider behavior, broker contract, or release tooling.

## Completion Evidence

- Focused Data Rights handler, validation, mutation-boundary, API, and
  persistence coverage passed 47/47. Focused web attempt/foundation coverage
  passed 19/19 with TypeScript and targeted lint clean.
- API contract generation completed from the real host with zero warnings and
  added only the required `operationId` field to the create request. The
  no-build OpenAPI and generated-TypeScript drift check is current.
- The consolidated backend gate synchronized the solution, passed source
  package guards, and checked every PostgreSQL and GMA SQL Server model with no
  migration drift.
- The first full-graph compile found one Integration test fixture still using
  the old coordinator constructor. After wiring the identity repository, the
  affected integration graph built with zero warnings and zero errors; the
  resumed gate then passed all 5,201 non-Docker tests.
- The backend matrix includes Data Rights 490/490, Operations Notifications
  104/104, Architecture 112/112, and Integration 60/60.
- The complete web gate passed TypeScript, lint, all 55 test files and 284
  tests, and the production Vite build.
- No Docker or GitHub Actions run was required because this slice changes no
  schema, provider-specific behavior, broker contract, or release tooling.

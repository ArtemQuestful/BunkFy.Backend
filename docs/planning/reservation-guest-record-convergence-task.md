# Reservation Guest Record Convergence Task

Status: completed

## Goal

Replace the browser-owned "create a Guest Record, then link it" sequence with a
durable BunkFy workflow. Once Guests commits a profile, Reservations must
eventually link it or expose a bounded, recoverable review state even if the
HTTP response, API process, broker delivery, or Worker attempt is interrupted.

The workflow must not use a distributed transaction, write another module's
schema, duplicate the Guest profile payload outside Guests, or compensate by
deleting a Guest Record.

## Boundary Decision

- Guests owns profile validation, normalization, creation idempotency, country
  policy, profile events, and the canonical Guest id.
- Reservations owns the durable link process because its outcome is a mutation
  of the Reservation's primary-Guest role. It stores only routing, actor, and
  lifecycle metadata; it never stores the requested Guest profile.
- `BunkFy.Extensions.ReservationGuestRecords` owns the public cross-module use
  case and references both modules only through their Contracts projects. It is
  stateless and owns no module data.
- GMA continues to own transactional command dispatch, outbox/inbox delivery,
  TaskRuntime leasing, retry, and tenant execution context. No BunkFy workflow
  vocabulary or new generic orchestration primitive belongs in GMA.

## Durable Flow

1. The first accepted caller operation id becomes both the process id and Guest
   id. A later authorized start for the same Reservation resumes that canonical
   process and follows the id returned by Reservations.
2. Reservations first prepares one tenant/property/reservation-scoped link
   process and returns its stable Guest-creation confirmation id. Preparing the
   process happens before Guests is called and contains no profile fields.
3. The extension invokes the Guests creation capability with the caller's
   profile, operation id, confirmation id, and authenticated actor.
4. A new Guest emits its confirmation id on the existing durable
   `GuestProfileCreatedIntegrationEvent`. An exact Guests replay remains
   event-free; the extension confirms the already-created Guest synchronously.
5. Reservations accepts confirmation only when operation, property, Guest,
   and confirmation coordinates match the prepared process. The first accepted
   confirmation emits one PII-minimal ready event from the Reservations outbox.
6. A Worker consumer idempotently enqueues one Reservations task whose payload
   contains process coordinates only.
7. The task rechecks current country policy, Reservation state, Guest
   projection, and the authoritative Guests restriction gate, then links the
   Guest and completes the process in Reservations-owned transactions.
8. A crash after linking but before process completion converges on retry: the
   state-setting Guest link recognizes the same current primary Guest and the
   process completes without another link event.

## Process Invariants

1. One Reservation can own at most one create-and-link process. Replacing an
   existing canonical Guest remains the separate explicit replacement flow.
2. Equivalent prepare and confirm calls replay. A fresh operation id for a
   Reservation that already owns a process resolves to the canonical process;
   reusing an operation id for a different tenant, property, Reservation, or
   confirmation fails closed.
3. A new process is prepared beneath the Reservation mutation coordinate so
   concurrent starts cannot create competing primary-Guest intents.
4. The prepared Reservation must exist at the requested property and have no
   current primary Guest. A same-Guest terminal replay returns completion; a
   different current primary Guest returns conflict.
5. The confirmation id is generated and persisted by Reservations before the
   Guests call. A delayed event from an older or unrelated Guest creation cannot
   authorize this process.
6. The Guest profile payload exists only in the request and Guests. It is not
   serialized into a process row, task payload, outbox event, log, metric,
   notification, or error.
7. Accepted work continues under the original authenticated actor attribution
   even if the browser disconnects. A retry that has not yet created the Guest
   passes current HTTP authorization and module policy again.
8. The task may rebase onto the current Reservation version only while the
   primary role is still empty. It never replaces another staff member's link.
9. Missing projection state is retried with TaskRuntime backoff. Archived,
   anonymised, restricted, wrong-property, occupied-role, or policy-denied
   outcomes become stable review reasons rather than infinite retries.
10. Task attempts are bounded. The last known process state remains queryable
    and an explicit authorized retry can emit a new task dispatch revision.
11. Completion never deletes or archives the Guest. Cancellation and failure
    affect only the Reservations-owned process.

## Contracts And Surfaces

- Add a narrow Guests creation capability to Guests Contracts and implement it
  in Guests Application by dispatching the existing create command.
- Add a narrow Reservations link-process capability, DTO, status/reason enums,
  ready event, and PII-free task payload to Reservations Contracts.
- Add an optional Guest-creation confirmation id to the Guests create command,
  domain event, and additive Guest-created integration contract.
- Map a public endpoint under the Reservation/property route that requires both
  `guests.create` and `reservations.manage-guests` in the same resolved property
  scope. Add a bounded status read and replay the same POST operation safely.
- Change the web create-and-link paths to call this endpoint and refresh the
  Reservation/Guests views while a process remains non-terminal.
- Keep direct selection/link and explicit replacement on the existing
  Reservations endpoint.

## Persistence And Lifecycle

- Add a provider-agnostic Reservations process entity plus PostgreSQL migration
  with tenant-first indexes and unique operation, confirmation, and
  Reservation coordinates.
- Store no legal name, display name, email, phone, birth date, nationality,
  language, notes, or normalized/fingerprinted derivative.
- Catalogue the operation, Guest, Reservation, property, actor, confirmation,
  status, reason, revision, and timestamps. Clear transient actor attribution
  after the canonical Reservation Guest-link audit owns it, and retain the
  minimal terminal process with the Reservation lifecycle for exact replay and
  support. Keep actor attribution while review remains actionable.
- Include the process in Reservations tenant portability and destruction, and
  remove it when Reservation anonymisation removes Guest links. A Guest
  anonymisation event terminates any still-active process before ordinary
  linking can resume.
- Keep processing restriction and country-policy decisions authoritative at
  execution time; process state is never an authorization bypass.

## Verification

- Domain/application tests cover prepare replay/conflict, matching and
  mismatched confirmation, concurrent starts, safe version rebase, same-link
  recovery, occupied-role review, bounded projection retry, terminal policy and
  restriction outcomes, and event/task idempotency.
- Persistence tests cover tenant scoping, indexes, process concurrency, outbox
  atomicity, migration upgrade/model drift, portability, anonymisation, and
  tenant destruction.
- Extension tests prove contracts-only dependencies, dual property permission
  enforcement, actor propagation, no profile persistence/transport leakage,
  and expected HTTP status mapping.
- Web tests prove one stable operation id, pending live refresh, completed
  convergence, recoverable review, and no fallback to the old two-request
  browser orchestration.
- Use focused non-Docker checks while editing. Run one consolidated module,
  architecture, migration, web, and targeted PostgreSQL/Worker gate at slice
  completion.

## Completion Evidence

- `eng/verify.ps1 -SkipRestore` passed solution/package checks, a zero-warning
  solution build, provider migration drift checks, all module/architecture
  tests, and the 54 non-Docker integration tests.
- The Reservations suite passed 257 tests and Architecture passed 94 tests
  after the final composition, request-layout, and portability guards.
- `pnpm verify` passed typecheck, lint, 181 web tests, and the production build;
  `pnpm contracts:check` confirmed the OpenAPI snapshot and generated client
  are current.
- The consolidated Docker run passed 98 of 104 tests and identified six stale
  integration fixtures. After aligning those fixtures with the production
  transaction, operation-lock, Guest identity, and export contracts, focused
  reruns passed all six findings.

## Deferred Follow-Up

- Admin API/CLI repair commands beyond replaying the same authorized operation.
- Automatic operator notifications for a process that reaches review state.
- Guest merge/split and entity-resolution behavior.
- Generalizing this process-manager pattern into GMA. Reconsider only after a
  materially different BunkFy workflow validates common semantics.

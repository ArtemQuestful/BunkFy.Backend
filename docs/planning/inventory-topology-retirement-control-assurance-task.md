# Inventory Topology Retirement Control Assurance Task

Status: complete
Date: 2026-08-11

## 2026-09-05 additive context-read amendment

The [context-read task](inventory-retirement-context-read-task.md) adds two
exact-target GETs using existing scoped inventory.retire, not Configure.
Request recent-authentication and exact pending replay remain unchanged;
status/retry/cancel acquire no new assurance policy. Shared inline confirmation
must explain that completed retirement is terminal with no reopen action in
the current Spaces workflow, and retain current authority/target checks.
This amendment does not re-award historical control or hosted evidence.

## Goal

Protect the rare, potentially immediate room and bed retirement decision without
adding friction to ordinary room-sales configuration or daily inventory-block
work. Keep the durable retirement workflow inside Inventory, reuse GMA's generic
authentication-assurance primitive, and preserve exact retry semantics across a
browser password step-up.

## Audit Findings

- A room or bed retirement request can move directly from `Draining` to
  `FinalizationRequested` when no active claim exists. The resulting Properties
  topology change is terminal.
- The current application commands do not carry explicit confirmation, so
  internal, Admin API, and CLI callers can start that workflow without proving
  destructive intent.
- The public request endpoints have no recent-authentication assurance even
  though the adjacent property-retirement control already has it.
- All retirement request, status, and retry routes currently reuse
  `inventory.configure`, whose published meaning is room sales-mode
  configuration. That gives custom roles a broader destructive authority than
  their permission label describes.
- Sales-mode changes already present bounded impact, reject active claims, bind
  an expected version, and serialize on the room coordinate. Manual blocks are
  frequent operational actions with retry-safe receipts. Neither belongs behind
  retirement assurance.
- Rejected-process retry resumes an existing confirmed process and cannot change
  its target or reason. A second assurance challenge would add friction without
  authorizing new intent.

## Ownership

- Inventory owns retirement intent, confirmation admission, permission codes,
  durable process state, replay, and recovery semantics.
- Workspaces owns BunkFy's configurable permission catalogue and protected seed
  role definitions.
- The public host maps BunkFy's privileged-operation assurance policy onto the
  Inventory request endpoints.
- The web owns explicit operator confirmation, exact password-step-up recovery,
  and permission-shaped affordances.
- GMA remains unchanged. Its generic authentication-assurance and scoped-access
  primitives already cover the reusable behavior.

## Invariants

1. `RequestBedRetirementCommand` and `RequestRoomRetirementCommand` reject an
   unconfirmed request before scope resolution, lock acquisition, journal
   inspection, repository access, or mutation.
2. Confirmation is an admission proof, not business intent; it is excluded from
   retirement fingerprints and does not change exact replay identity.
3. Public room and bed retirement requests require configured recent-auth
   assurance. Status reads, retries, room sales-mode writes, impact reads, and
   manual-block writes do not.
4. `inventory.retire` is a distinct scoped, sensitive permission. Retirement
   request, status, and retry surfaces require it across public API, Admin API,
   and Admin CLI.
5. The protected Manager seed receives `inventory.retire`; owner wildcard still
   covers it. Front desk, housekeeping, viewer, legacy membership, and company
   support do not receive it.
6. Existing custom profiles do not silently inherit retirement authority. A
   workspace administrator may grant the new permission together with its
   declared topology and inventory prerequisites.
7. Browser password step-up retries the same target, normalized reason, and
   caller-owned operation id, and sends `confirmed: true` on every request.

## Delivery

1. Add application confirmation and a stable `Inventory.ConfirmationRequired`
   error mapped to HTTP 400 on public and Admin APIs.
2. Add `inventory.retire` to Inventory metadata and admin contracts, then gate
   every retirement surface with it.
3. Add the permission to the Workspaces catalogue, delegable allowlist, Manager
   seed, and seed version while excluding it from support and operational roles.
4. Configure Inventory request assurance in the public host and add endpoint
   metadata/host-composition guards.
5. Update the web permission check, confirmed payload, and exact recent-auth
   retry experience.
6. Update Inventory's executable personal-data access policy and generated
   inventory because retirement reasons and actor references cross the new
   permission boundary.
7. Run focused Inventory, Workspaces, host-architecture, and web tests during
   implementation, then one consolidated non-Docker slice gate and contract
   drift check before publication.

## Deferred Recovery Slice

An accepted draining retirement currently cannot be withdrawn. Cancellation is
not added here because it needs an explicit state-machine and race design: it
must restore sellability only while Properties finalization has not been
requested, serialize with claim release and auto-advance, journal exact cancel
intent, republish unit definitions, and remain impossible after terminal
topology work starts. That recovery workflow will be audited as the next
Inventory slice rather than weakening this control boundary.

## Completion Criteria

- unconfirmed room and bed retirement requests produce no observable write-side
  interaction;
- the two public request endpoints carry the configured assurance metadata and
  no ordinary Inventory endpoint does;
- retirement authority is independently assignable and absent from lower-trust
  operational/support roles;
- Admin API and CLI callers must also confirm retirement intent;
- password step-up preserves exact retry identity and completes the original
  request;
- generated OpenAPI/web contracts and the personal-data inventory are current;
- focused tests and the consolidated non-Docker backend/web gates pass; and
- GMA submodules remain unchanged.

## Verification

- Inventory focused tests: 144 passed.
- Workspaces focused tests: 359 passed.
- Architecture tests: 102 passed.
- Operations Notifications checkpoint tests: 100 passed.
- `eng/verify.ps1 -SkipRestore`: passed, including synchronized solution,
  source-package guards, clean migration drift, the complete non-Docker test
  sweep, and 60 integration tests.
- `pnpm verify`: passed with type checking, lint, 265 web tests, and the
  production build.
- `pnpm contracts:check`: passed; OpenAPI and generated TypeScript contracts
  are current.
- `git diff --check`: passed, and all GMA submodules remained unchanged.

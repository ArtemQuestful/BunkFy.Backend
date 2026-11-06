# Data Rights Restriction Target Binding Task

Status: complete
Date: 2026-08-15

## Goal

Let an operator release one reviewed owner-local processing restriction when a
subject has several independent active restrictions, without copying owner
records into Data Rights or allowing execution to choose an obligation that was
not part of the approval.

## Problem Found

- Every apply case creates an independent owner restriction, which is the
  correct composed-state model.
- A release case currently succeeds only when the owner finds exactly one
  active restriction. Two valid obligations leave the case permanently
  blocked even though each owner already supports release by restriction id.
- The direct owner release commands accept a restriction id, but the Data
  Rights approval gate binds only the subject record and release directive. It
  does not prove that the selected owner obligation was reviewed.
- Treating release as "remove all" would silently collapse separate legal or
  operational obligations. Selecting an arbitrary active row during execution
  would move decision authority into the executor.
- A successful targeted release can leave processing effectively restricted by
  another obligation. That is a truthful composed-state result, not an owner
  failure.

## Boundary Decisions

### GMA

No GMA change is required. Generic CQRS, scoped authorization, optimistic
concurrency, transactions, and result mapping already cover the mechanics.
Restriction targets, privacy approvals, and composed processing state are
BunkFy product concepts.

### Data Rights

- Data Rights owns one bounded, PII-free target coordinate attached to a release
  case: owner key, owner operation id, owner operation version, selector, and
  selection time.
- A target is mutable only while the case is in discovery. Entering review
  freezes it; the decision revision therefore binds the exact target.
- A current-version release case requires exactly one selected subject and one
  matching target before review. Apply cases and non-restriction cases must not
  carry a target.
- Restriction is a standalone operation for every newly created case. Legacy
  audit rows keep their original operation value so the additive migration does
  not rewrite or reject historical records.
- Selecting or removing a subject clears any prior target so a target can never
  survive a subject-coordinate change.
- The operation approval gate compares the owner target supplied by a release
  command with the approved case. Owner commands cannot substitute another
  active restriction after approval.

### Owner Modules

- Guests, Staff, and Workspaces remain authoritative for active restriction
  discovery, target-to-subject validation, release concurrency, composed state,
  and durable receipts.
- The shared Data Rights contract exposes only bounded target metadata needed
  for review: owner operation id/version, source case id, and applied time. It
  does not expose personal values or copy an owner aggregate.
- Exact target validation loads the owner row by indexed id and checks active
  state, selected subject, and version. Execution repeats those checks inside
  the owner transaction.

## Contract And Catalogue Rules

- The existing restriction contributor becomes the single catalogue entry for
  target discovery, target validation, and execution. Separate catalogues must
  not drift independently.
- Target discovery returns at most 20 stably ordered active targets plus a
  conservative `LimitReached` flag from one-row lookahead.
- A missing compatible owner is unavailable (`503`). Duplicate, malformed, or
  incompatible registrations are an invalid catalogue (`500`).
- A well-formed owner business block is `409`; an explicit or exceptional
  transient owner failure is `503` with bounded `Retry-After`; malformed target
  results are `500`.
- Cancellation is propagated. Logs contain only the bounded owner key and
  exception type.
- The 30-second owner deadline is enforced with linked cancellation for target
  discovery, review revalidation, and execution; caller cancellation remains
  distinguishable from an owner timeout.

## Execution Semantics

- Current-version release execution carries the approved target id and version
  through Data Rights, the approval gate, and the owner command.
- Owner receipt replay remains keyed by the existing execution idempotency key
  and must match the same target.
- The release proof must identify the selected owner operation and advance its
  exact version. A changed target, stale target version, or mismatched proof
  fails closed.
- Apply still requires the resulting effective state to be restricted.
- Targeted release may return either effective state. `false` means the selected
  obligation was the last active restriction; `true` means another approved
  obligation remains active. Both results are shown explicitly.
- The Data Rights case completes only for the selected target. Releasing another
  obligation requires another reviewed release case.

## Upgrade Compatibility

- New release cases record the current target-binding contract version.
- Existing rows retain a null version. A previously approved legacy release may
  use the old exact-one-active-target path, preserving safe replay and upgrade
  continuity.
- Legacy cases never gain arbitrary target selection after approval. New
  discovery cases use the target-bound workflow.
- Nullable additive Data Rights columns carry the version marker and target;
  owner schemas do not change.
- PostgreSQL downgrade is permitted only while no target-bound case exists. The
  migration fails explicitly after adoption rather than reinterpreting a bound
  approval as an unsafe legacy release.

## Operator Experience

- After selecting a subject for a new release case, the operator sees a bounded
  list of active owner obligations identified by short source-case reference,
  applied time, and opaque target reference.
- Review remains unavailable until one current target is selected.
- Confirmation repeats the selected target reference and warns that another
  obligation may keep processing restricted.
- Completion distinguishes "restriction released" from "selected restriction
  released; another restriction remains active".
- The case response carries the PII-free durable execution result, so those
  completion states survive navigation and process restart rather than relying
  on transient mutation state.
- Empty, limited, stale, unavailable, and invalid-catalogue states are distinct
  and retain the selected subject and retry coordinates.

## Verification Cadence

1. Use focused Data Rights domain/application/API tests while changing the
   case and approval invariants.
2. Use focused owner contributor/command tests for Guests, Staff, and
   Workspaces, then focused web workflow/component tests.
3. Generate the additive PostgreSQL migration and run migration-drift checks.
4. Run one consolidated non-Docker backend/web gate when the slice is coherent.
5. Run the relevant Docker migration and cross-module scenarios once at the
   end because this slice changes persisted Data Rights state.
6. Publish backend, web, and root pointers only after both slice gates pass.

## Completion Criteria

- a new release case cannot enter review without one owner-validated target;
- approval and owner execution bind the same target id and version;
- multiple active restrictions no longer strand a targeted release;
- releasing one obligation cannot silently release another;
- effective restricted state remains truthful when another obligation exists;
- legacy exact-one release replay remains safe;
- all owner records remain owned by their modules; and
- no BunkFy restriction vocabulary moves into GMA.

## Completion Evidence

- Data Rights tests passed `547/547`; focused owner contributor tests passed
  Guests `9/9`, Staff `7/7`, and Workspaces `10/10`.
- The complete non-Docker backend matrix passed after the owner compatibility
  flag was classified in all three personal-data catalogues; architecture
  passed `112/112` and non-Docker integration passed `60/60`.
- PostgreSQL migration drift is clear for every GMA and BunkFy context. The
  generated OpenAPI snapshot and web contracts are current.
- Web typecheck and lint passed; the complete web suite passed `299/299`, and
  the production build completed.
- The Docker scenario
  `Targeted_release_completes_one_of_multiple_owner_restrictions` passed against
  PostgreSQL 16 and NATS. It exercised migration, scoped permissions, two apply
  cases, bounded target discovery, exact target selection, review-time
  validation, approval, targeted release, durable proof reload, and the
  remaining effective restriction.

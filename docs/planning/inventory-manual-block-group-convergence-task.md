# Inventory Manual Block Group Convergence Task

Status: implementation and local verification complete; draft publication pending
Date: 2026-08-12

## Goal

Make broad manual Inventory blocks a bounded, previewable, confirmable, and
recoverable operator workflow without weakening the existing atomic
availability contract.

## Ownership

- Inventory owns the block-group definition, resolved membership, maximum
  impact, selection digest, mutation versions, operation receipts, actor
  provenance, and tenant lifecycle.
- Properties remains authoritative for physical topology. Inventory previews
  and commits against its local versioned topology projection.
- Reservations continues to consume the existing per-child block events. This
  slice does not redefine a committed Inventory mutation as downstream
  projection convergence.
- The workflow is BunkFy-specific. GMA receives no Inventory block concepts.
- Failed transactional results rely on the provider-neutral rollback-reset hook
  from GMA Framework PR #24. The backend pins that exact framework commit so a
  later command in the same scope cannot persist entities or domain events left
  tracked by a rolled-back attempt; this is generic unit-of-work hygiene, not an
  Inventory concept in the framework.

## Frozen Contract

- A synchronous block group contains at most 500 child blocks. `500` succeeds;
  `501` returns a stable impact-limit error and creates no group, child,
  operation receipt, outbox message, or Inventory revision.
- For targets within the limit, preview returns the normalized target, exact
  affected count, maximum, bounded member evidence, and a deterministic digest
  of the selection and selection-affecting versions. Above the limit it returns
  `TooLarge`, a bounded lower count of at least 501, and no exact count or
  confirmation digest; it does not perform an unbounded count scan. A count
  alone never confirms membership.
- Create requires the preview digest, expected count, and explicit
  confirmation. The server resolves the target again under the mutation
  fences and rejects a stale preview atomically.
- A block group is a first-class, versioned parent. Its read model reports the
  original target, active/released/total counts, status, predecessor/successor
  relationship, timestamps, and actor provenance. Legacy parents identify
  their original target as unknown instead of inferring intent from mutable
  topology.
- Group definitions are immutable. Replace atomically releases the predecessor
  and creates a successor with a new group id, linked in both read models and
  in the receipt. A semantic no-op may retain the same group id and version.
- Replace and release bind the caller-observed group version and require
  explicit confirmation. Receipts report the observed predecessor and actual
  result, including released-now, already-released, remaining, and affected
  counts where applicable.
- Exact operation replay returns its committed receipt before mutable topology
  or group-state checks. Changed operation-id reuse is a conflict. Failed
  preview, confirmation, impact-limit, stale-version, or availability checks do
  not bind the operation id.

## Operator Surfaces

Public and Admin API expose the same Inventory contracts:

- `POST /properties/{propertyId}/block-groups/preview`;
- `GET /properties/{propertyId}/block-groups`;
- `GET /properties/{propertyId}/block-groups/{blockGroupId}`;
- `GET /properties/{propertyId}/block-groups/{blockGroupId}/members`;
- `POST /properties/{propertyId}/block-groups`;
- `PUT /properties/{propertyId}/block-groups/{blockGroupId}`;
- `POST /properties/{propertyId}/block-groups/{blockGroupId}/release`;
- `GET /properties/{propertyId}/block-group-create-operations/{operationId}`;
- `GET /properties/{propertyId}/block-groups/{blockGroupId}/operations/{operationId}`.

Group and member directories use opaque keyset cursors and bounded page sizes;
they never return an unbounded nested child collection. The create-operation
read is property-scoped because a client does not know the generated group id
after a lost response. With a success-only receipt journal, `404` means no
committed outcome is visible and does not assert that the request failed; the
safe recovery action is an exact retry with the same operation id and payload.

Every public and Admin response, including errors, remains `no-store`. Stable
problem codes distinguish invalid input/confirmation, not found, stale preview
or version, impact over 500, closed workspace, and unavailable admission. Human
messages are display text; clients branch and localize on the code.

Admin API and Admin CLI use distinct `inventory.block-groups.*` audit operation
names and propagate the authenticated actor into Inventory's durable evidence.
The new property-scoped, sensitive `inventory.block-groups.manage` permission is
granted to the Manager seed and is delegable with its prerequisites. It is not
silently added to legacy Front desk or Housekeeping access. CLI mutations
require `--yes` and support the same preview, reads, keyset traversal, create,
replace, release, and operation-recovery workflow as HTTP.

## Scale And Failure Semantics

The service does not silently split an over-limit request into active batches.
That would expose partially blocked availability and permit allocations to race
between batches. Operators narrow the target and preview again.

An asynchronous staged workflow is deferred. It would require inactive
prepared children, explicit progress/retry/cancel states, conflict revalidation,
an atomic activation gate, and downstream convergence semantics. Those are not
implicit in this synchronous contract.

## Completion Criteria

- unit and provider tests prove `0`, `500`, and `501`, deterministic digest and
  stale-selection behavior, atomic replace/release, exact replay, lost-response
  recovery, keyset traversal, tenant isolation, and no partial persistence;
- public, Admin API, and Admin CLI authorization tests prove the new permission,
  property scope, confirmation, actor attribution, stable errors, and no-store;
- generated OpenAPI and TypeScript contracts match the runtime endpoints;
- Inventory tenant export, destruction, and personal-data metadata include the
  first-class group and persisted actor evidence;
- the implementation is repository-verified; deployment and production
  readiness remain separate evidence gates.

## Current-source integration and demo preparation

The integration with backend `315acadb49e4842ed63529a26bb47efb6643bfc9`
retains the later retirement cancellation/context reads, Station permissions,
property time zones, and canonical integrity migrations. Access-profile seeds
are version **7**, combining the current permission set with block-group
management. The existing seed reconciliation path can update an existing
Manager profile when it runs; existing Front desk and Housekeeping seeds do
not gain block-group management. Inspect profile drift and exercise the normal
authorized reconciliation and assignment flows in the demo before exposing
the new management UI. Do not replace customized assignments with seed roles.

The combined Inventory owner catalogue is version **8**, the personal-data
catalogue is version **9**, and tenant export schema is version **6**. The
convergence migration advances affected tenant revisions, so a previously
captured export coordinate must be reacquired. The upgrade test exercises the
actual application coordinator with both later canonical integrity migrations
already applied and only the earlier-dated convergence migration pending.
Ordinary exact-prefix mode refuses this history. The explicit
`ApplyInventoryConvergenceV1` disposition recognizes only the exact published
15-ID history and 16-ID target, and uses the existing host lock and Production
admission. See [migration-host safety](../operations/migrations-host-production-safety.md#existing-inventory-convergence-history)
for the controlled Plan/Apply/replay procedure. The prior direct-EF test alone
did not establish this application-host compatibility.

Upgrade takes fail-fast exclusive locks and refuses inconsistent legacy rows
or an active tenant destruction. Rehearse it against an isolated restored
database with writers quiesced, retain a verified backup, and exercise exact
operation replay after restart. Native group or receipt history deliberately
prevents downgrade; recovery must preserve that history through restore or a
qualified forward repair. No production migration or profile reconciliation
is implied by merging the source.

PR provider CI partitions all 45 current test projects, including mounted GMA
projects, into eight jobs with at most three concurrent jobs. The aggregate
`Docker integration tests` check requires every group to succeed. Local
provider evidence and hosted checks remain separate from paired UI, restart,
and production-like demo acceptance.

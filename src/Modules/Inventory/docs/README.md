# Inventory

Inventory owns BunkFy's tenant- and property-scoped sellable-unit model. Properties remains the source of physical property, room, and bed topology; Inventory consumes versioned events into local projections and can repair them through the Properties topology rebuild source.

The module's executable personal-data contract is
[`personal-data-catalog.v1.json`](personal-data-catalog.v1.json). Its generated
[`personal-data-inventory.v1.md`](personal-data-inventory.v1.md) is checked by
reflection-backed tests against person-linked persistence, commands, queries,
APIs, admin output, projection exports, domain events, and integration events.
Pure facility topology remains explicitly outside that catalogue. The policy
values are engineering defaults until country, retention, and rights approval.

## Current Slice

- durable room and bed inventory-unit identities;
- explicit `Unconfigured`, `RoomLevel`, or `BedLevel` room sales mode;
- first-class, versioned manual half-open `[arrival, departure)` block groups
  targeting a property, configured building/floor, room, or unit, with a hard
  maximum of 500 child blocks;
- retry-safe manual-block creation and release with caller-owned operation ids,
  canonical request fingerprints, typed immutable receipts, and exact replay;
- retry-safe room and bed retirement requests, rejected-process retries, and
  draining-process cancellations with caller-owned operation ids, normalized
  intent fingerprints, expected process versions, compact journal pointers,
  and exact replay;
- database-enforced coordinate, range, outcome, lifecycle, and version
  invariants for authoritative allocations, amendment decisions, manual
  blocks, room configurations, retirement processes, and allocation locks;
- durable, idempotent multi-unit reservation allocations and releases with concurrent-claim serialization;
- exact-reservation Data Rights discovery/export plus terminal allocation
  anonymisation and restore-safe owner proof;
- date-range availability reads over the currently sellable units;
- scoped `inventory.read`, `inventory.configure`, `inventory.blocks.manage`,
  sensitive `inventory.block-groups.manage`, and sensitive `inventory.retire`
  permissions;
- public API, Admin API, Admin CLI, PostgreSQL migration, NATS handlers, and worker rebuild composition;
- versioned unit-definition, sales-mode, block, and allocation events plus `IInventoryAvailabilityProjectionExportSource` for downstream Reservations rebuilds.

Inventory does not own Properties topology, reservation lifecycle/contact data, temporary booking holds, rates, provider mappings, maintenance workflows, or housekeeping workflows.

Grouped blocks resolve against Inventory's local topology projection and persist
one correlated child block per selected sellable unit. An operator first
previews the exact count and selection digest when the target is within the
limit, then explicitly confirms the bounded mutation. An over-limit preview is
`TooLarge`, reports a bounded lower count of at least 501, and does not run an
unbounded exact-count scan. Create, immutable-definition replacement, and group
release are transactional, so a broad physical target cannot leave partially
blocked availability. Existing single-unit create/release contracts remain
available for compatibility.

## Operational Surfaces

Room and manual-block directories use deterministic, bounded pages with `HasMore`; repositories determine continuation with `pageSize + 1` lookahead and do not execute exact count queries. Callers should continue only while `HasMore` is true. Manual-block reads may also use an optional bounded half-open overlap window for schedule composition; both boundaries are required together and the window cannot exceed 42 days. Availability intentionally remains one complete property and date-range decision snapshot because partial availability would be unsafe for assignment decisions.

Block-group and group-member directories instead use opaque keyset cursors so
inserts and status changes do not make deep operator traversal depend on mutable
offsets. Group reads report total, active, and released counts without embedding
an unbounded member collection.

The synchronous group impact limit is exactly 500 children. For a target within
the limit, preview reports the exact affected count, maximum, bounded member
evidence, and a digest bound to the selected membership and
selection-affecting versions. Above the limit it reports `TooLarge`, an
`AtLeastAffectedBlockCount` of at least 501, and no exact count or confirmation
digest; it does not scan the entire broad target merely to count it. Create
requires the within-limit digest, expected count, and confirmation. A request
over 500 fails without mutation; Inventory never silently rolls it out as
active batches.

Group definitions are immutable. Replace binds the observed predecessor version
and atomically releases it and creates a linked successor; a semantic no-op may
retain the same group. Release also binds the observed version. Both require
explicit confirmation and return count- and version-bearing receipts that make
prior individual releases visible.

Ordinary sales-mode and manual-block writes return identity, status, version, and affected-count receipts instead of nested read models. API and web callers invalidate and refetch the authoritative room, block, or availability read after a successful write. Retirement operations keep their bounded process DTOs because their impact and retry state are part of the immediate operator decision.

Manual-block writes require one non-empty operation id per logical attempt.
Inventory serializes creates on the property-and-operation coordinate and
releases on the durable block or block-group coordinate. An exact retry returns
the original receipt before consulting mutable topology or block state; changed
reuse returns a stable conflict, and failed attempts do not consume the id. The
journal stores only normalized request fingerprints and compact typed results,
not a duplicate copy of the free-text reason.

After a lost create response, callers read the property-scoped create-operation
receipt by their operation id because the generated group id is not yet known.
Other group-operation receipts are read by group and operation id. A missing
success-only receipt means that no committed outcome is currently visible; it
does not prove failure. The safe recovery action is an exact retry with the same
operation id and payload.

Public API, Admin API, and Admin CLI expose the same preview, group and member
reads, create, immutable-definition replace, release, and operation-recovery
workflow. Admin mutations require `--yes` in CLI and propagate the authenticated
actor into Inventory evidence. Manager receives the new bulk-group permission;
legacy Front desk and Housekeeping roles keep single-block authority only.

The Admin CLI command group is `inventory block-groups`. Use `preview`, then
pass its `SelectionDigest` and exact affected count to `create` or `replace`
with `--yes`. `list` and `members` accept `--cursor` and print the next opaque
cursor when another page exists. `get-create-operation` recovers a create by
property and operation id after a lost response; `get-operation` recovers a
replace or release by property, group, and operation id. Table output keeps the
impact and reconciliation counts visible, while `--output json` preserves the
complete typed response for automation. CLI errors include the stable error
code before display text so scripts can log an actionable, localizable cause.

Room and bed retirement requests serialize on the room coordinate. Exact
request retries resolve the stored topology-change pointer and return the
current bounded process view, while a new operation id with the same normalized
reason adopts that process. Changed intent returns a stable conflict. Rejected
process retries additionally bind the caller-observed process version, acquire
the process and room fences in that order, and publish no duplicate finalization
request on exact replay.

A room or bed retirement can be canceled only while it is `Draining`.
Cancellation binds the observed process version, normalized reason, and actor;
acquires the process and room fences in that order; and records a compact replay
pointer before commit. It republishes the room definition in the same transaction
so sellability returns with a higher unit version. Canceled attempts remain
queryable as history but are excluded from active-target lookups. PostgreSQL
partial unique indexes allow later attempts while still enforcing at most one
active retirement per room or bed.

Public and Admin Inventory endpoints emit `Cache-Control: no-store`, `Pragma: no-cache`, and an expired response date. This prevents block reasons, staff references, claim identifiers, and current availability state from being retained by shared caches. Admin endpoints also declare explicit success response metadata so generated clients match runtime responses.

Authoritative operational rows are protected by named relational checks in
addition to aggregate guards. Nullable transition evidence is required
explicitly, so PostgreSQL three-valued logic cannot admit an incomplete
amendment, completion, rejection, or cancellation shape. Rebuildable topology
placeholders remain outside this contract because their valid out-of-order
state machine is deliberately different from operational authority.

## Tenant Termination

Inventory is a mandatory `Export` and `Destroy` owner. Export depends on
Properties and contains Inventory-owned unit identities, room sales
configuration, manual blocks, allocations and allocation units, amendment
decisions, anonymisation proof, bed or room retirement processes, and immutable
management-operation receipts including retirement topology-change pointers.
Replicated topology, transport journals, rebuild checkpoints, operation locks,
and the internal tenant revision are deliberately excluded from portability.

Destruction depends on Reservations so booking authority is removed before
allocations disappear. It closes local admission, waits for live outbox leases,
and removes all 18 tenant-owned record families in foreign-key-safe stages.
One invocation removes at most one non-empty batch of 500 physical rows;
allocation units are explicitly removed before allocations. Completion retains
only a closed lifecycle row and one immutable, PII-free destruction receipt
with a versioned SHA-256 proof chain. Personal-data catalogue version 7 binds
the retained proof to its dedicated tenant-destruction policy.

Relational writes and export selection share the tenant mutation transaction
key. Each operational save rechecks the authoritative Workspaces termination
fence and advances a module-local revision in the same transaction. Export runs
under repeatable-read, validates the exact frozen process, epoch, and fence
before and after streaming, and succeeds only when that local revision remains
unchanged. Closing also suppresses scoped inbox delivery and outbox claims.
Anonymisation receipts, restore receipts, and the final destruction receipt are
protected in EF and PostgreSQL; anonymisation tombstones remain updateable for
restore proof but cannot be deleted outside the exact destruction operation.

Migrations `AddInventoryTenantExportRevision`,
`AddInventoryTenantDestructionLifecycle`,
`AddInventoryManualBlockManagementOperations`,
`AddInventoryRetirementManagementOperations`, and
`AddInventoryRetirementCancellation`, and
`AddInventoryAuthoritativeStateIntegrity` add the revision/lifecycle state,
resumable operation, typed receipt ledger, cancellation audit state, partial
active-retirement uniqueness, and provider-side proof guards. Repository gates
cover EF model drift plus PostgreSQL lock drain, outbox suppression, bounded
graph removal, replay/conflict, concurrent block mutation, retained
cancellation history, active-attempt enforcement, valid legacy-row upgrade,
exact named integrity checks, trigger enforcement, closed admission,
downgrade refusal and tenant isolation. Use current gate results as release
evidence rather than a historical test count in this runbook.

Production execution remains disabled until every mandatory owner, terminal
orchestration, protected replay, operator controls, and final admission are
complete.

### Coordinated deployment and rollback

The first-class block-group schema requires a symmetric stop-and-drain rollout.
For `Up`, stop every Inventory API, Admin API, Admin CLI, worker, export, and
tenant-destruction writer. Drain in-flight units of work, outbox leases, and
tenant-destruction batches; take a verified database backup; then apply the
Inventory migration. Deploy the new binaries to every stopped process, verify
the migration, model, health, preview/read/recovery paths, and outbox state, and
only then resume traffic and workers. Old writers fail closed after `Up`; a
mixed-version writer fleet is not a supported operating mode.

For `Down`, stop and drain every new Inventory binary by the same boundary.
Require the migration downgrade evidence guard to prove that no first-class
block-group data would be lost, take a verified backup, and only then apply
`Down`. Deploy the predecessor binaries everywhere, verify the schema and
health and prove that no new binary remains active, and only then resume. If the
evidence guard refuses downgrade, keep the system stopped and restore or
remediate from the retained backup instead of bypassing the guard.

## Runtime

The API hosts expose management commands and reads. When the worker is enabled, compose Properties and Inventory; compose Reservations as well when allocation requests should be consumed. Enable NATS consumers and publishing, and enable the task worker when projection rebuild tasks should execute.

All inventory tables are in the `inventory` schema. No Inventory table or migration reaches into another module's schema.

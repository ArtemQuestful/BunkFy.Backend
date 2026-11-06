# Inventory Authoritative State Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make Inventory's durable operational facts fail closed when a provider defect,
manual database action, unsafe restore, or future persistence path attempts to
store a shape that the domain model cannot produce.

This slice strengthens database integrity only. It does not change availability,
allocation, block, sales-mode, retirement, or tenant-termination behavior.

## Ownership

- Inventory owns allocation, amendment-decision, manual-block, sales-mode,
  retirement-process, and allocation-lock invariants.
- Properties remains the physical-topology authority; Inventory projections stay
  rebuildable and deliberately tolerate valid out-of-order placeholder states.
- Reservations remains the reservation lifecycle and requested-stay authority.
- The provider-neutral Inventory model declares its durable state contract; the
  BunkFy PostgreSQL migrations project owns the concrete migration.
- GMA remains unchanged. No generic persistence primitive or product-neutral
  behavior is missing for this work.

## Invariants

1. Allocation coordinates are non-empty, stay ranges are half-open and valid,
   versions are positive, and active/rejected/released status agrees with the
   rejection and release evidence stored on the row.
2. Amendment decisions have non-empty coordinates, a fixed SHA-256 request
   fingerprint, and exactly one confirmed or rejected result shape.
3. Manual blocks have non-empty coordinates, a non-blank bounded reason, a valid
   stay range, and active/released state that agrees with version and release
   time.
4. Room configuration keeps a valid sales mode, positive versions,
   `AvailabilityMutationVersion >= Version`, and an initial-versus-configured
   timestamp shape the aggregate can produce.
5. Room and bed retirement rows have non-empty coordinates and actor/reason,
   positive versions, valid states, and rejection/completion/cancellation fields
   that agree exactly with their process state and time ordering.
6. Allocation operation locks retain the allocation coordinate they serialize,
   use the same id for the lock and allocation coordinate, and keep a positive
   revision. A foreign key is intentionally absent because restore proof may
   serialize an allocation coordinate that is absent from a backup.
7. Projection placeholders are not included in this slice. Their valid
   out-of-order state deserves a separate projection-specific contract if later
   measurements or incidents justify one.

## Delivery

1. Add named check constraints to the Inventory EF configurations.
2. Add focused model metadata tests for every authoritative table covered here.
3. Generate the BunkFy PostgreSQL migration and review it for additive,
   downgrade-safe behavior.
4. Extend the Inventory PostgreSQL migration scenario with valid legacy-row
   upgrade proof and direct invalid-write rejection for each constraint family.
5. Update the Inventory development note and close stale task status wording.
6. Run focused non-Docker checks during implementation, then one Inventory
   PostgreSQL gate and one consolidated backend slice gate.

## Deferred

- An immutable allocation-request decision journal. Existing request and
  reservation serialization prevents duplicate authority; preserving the exact
  original request shape after later amendments is a separate recovery contract.
- Provider-specific exclusion constraints or partitioning before production
  measurements justify them.
- Database constraints for rebuildable topology placeholders before their
  out-of-order state machine is specified independently.

## Completion Criteria

- the EF model and PostgreSQL migration expose the same named constraints;
- every currently valid aggregate state still persists;
- representative malformed allocation, amendment, block, configuration,
  retirement, and lock rows fail with PostgreSQL check violations;
- the migration upgrades current valid state without rewriting business facts;
- focused Inventory and migration tests pass, followed by one consolidated
  end-of-slice gate; and
- no GMA repository changes are required.

## Verification Evidence

- Inventory model and persistence tests: 160 passed;
- architecture guards: 112 passed;
- migration drift: every configured GMA and BunkFy provider passed;
- PostgreSQL 16 upgrade from `AddInventoryRetirementCancellation`: passed with
  valid rows retained across all covered authoritative tables; and
- PostgreSQL malformed-write proof: 18 isolated updates were rejected with the
  expected named check constraints.

The consolidated backend gate passed with solution synchronization,
source-package checks, a zero-warning serial build, migration drift checks, and
the complete fast-test suite.

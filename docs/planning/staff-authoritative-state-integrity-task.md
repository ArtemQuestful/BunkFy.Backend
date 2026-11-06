# Staff Authoritative State Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make Staff's durable employment and assignment facts fail closed when a
provider defect, direct database action, unsafe restore, or future persistence
path attempts to store a shape the aggregate cannot produce.

This slice strengthens database integrity only. It does not change profile,
account-link, lifecycle, assignment, authorization, retention, Data Rights, or
tenant-termination behavior.

## Audit Finding

Application locks, aggregate guards, optimistic versions, and idempotency
journals protect normal Staff mutations. PostgreSQL currently permits several
impossible states beneath those controls:

- empty member or assignment coordinates;
- personal values whose normalized search copy is missing, or vice versa;
- anonymised profiles that still retain Staff personal data;
- lifecycle timestamps outside the member's durable change timeline;
- assignment actors or timestamps that disagree with assignment state;
- an unassignment at the same version as its assignment; and
- duplicate current assignments or multiple current primary assignments for
  one Staff member.

## Ownership

- Staff owns employment profile, lifecycle, Auth-subject correlation, and
  assignment-history invariants.
- Properties remains the property identity and lifecycle authority.
- Workspaces and GMA Access Control remain the membership, role, grant, and
  effective-authorization authorities.
- The Staff EF model declares the durable contract; the BunkFy PostgreSQL
  migrations project owns the concrete migration.
- GMA remains unchanged. These invariants are product-domain facts, not generic
  persistence or authorization behavior.

## Invariants

1. Staff member and assignment ids are non-empty and tenant scope is non-blank.
2. Display-name search text is non-blank; optional personal values and their
   search copies are either both absent or both present and non-blank.
3. Optional operational profile values and Auth-subject ids cannot be blank.
4. An anonymised profile has the exact anonymised display identity and retains
   no legal name, contact data, employee number, job metadata, or Auth subject.
5. Member lifecycle timestamps fall between creation and the last durable
   change, with departure preceding anonymisation.
6. Assignment coordinates and assignment actors are valid, effective dates
   are ordered, and unassignment evidence follows assignment evidence in both
   time and Staff version.
7. One member has at most one current assignment for a property and at most one
   current primary assignment across properties.

## Delivery

1. Add named Staff-member and property-assignment constraints and filtered
   uniqueness indexes to the EF configuration.
2. Extend focused model metadata tests for the complete authoritative contract.
3. Generate and review the additive BunkFy PostgreSQL migration.
4. Add one PostgreSQL 16 migration scenario that upgrades valid active and
   anonymised legacy rows, then proves representative malformed writes and
   duplicate current assignments fail with exact provider constraint names.
5. Align the Staff development note and close with focused checks followed by
   one coherent end-of-slice gate.

## Deferred

- Payroll, scheduling, documents, organization charts, and other future Staff
  subdomains already listed in the Staff module task.
- Future-dated assignment and departure scheduling, which requires a separate
  business-date state machine.
- Cross-row temporal exclusion for historical assignments. The current domain
  supports immediate effective dates and serializes member mutations; a
  provider exclusion constraint should follow only if future scheduling widens
  that model.

## Completion Criteria

- valid current Staff aggregate states upgrade without rewriting business
  facts;
- malformed profile, lifecycle, and assignment states fail at PostgreSQL;
- duplicate current and current-primary assignments fail atomically;
- focused Staff, architecture, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the finished slice boundary; and
- no GMA repository change is required.

## Verification Evidence

- Staff model and domain tests: 274 passed;
- Staff PostgreSQL migration drift: no pending model changes;
- solution operational-file guard: passed;
- PostgreSQL 16 upgrade from `AddStaffOnboardingProvisioningOperations`:
  passed with valid active and anonymised rows retained; and
- PostgreSQL malformed-write proof: ten check violations and two unique-index
  violations were rejected with the expected provider constraint names; and
- consolidated backend gate: synchronized solution, source-package checks,
  zero-warning serial build, all configured migration-drift checks, Staff
  274/274, Architecture 112/112, and non-Docker Integration 65/65 passed.

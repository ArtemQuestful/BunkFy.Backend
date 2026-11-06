# Workspace Owner Transfer Staff Eligibility Task

Status: complete
Date: 2026-08-22

## Goal

Prevent ownership transfer from creating a BunkFy workspace owner whose
product identity is missing or operationally inactive. GMA Organizations owns
the generic ownership transaction; BunkFy decides that a transfer target must
already be an active Staff member in the same workspace.

## Boundary

- GMA Organizations keeps its generic transfer aggregate, command, and
  mutation-admission policy seam unchanged.
- BunkFy Staff remains the authority for the target subject's current
  employment identity and status.
- The BunkFy Workspaces extension evaluates the Staff requirement before GMA
  writes either membership role.
- Operations Notifications keeps its strict Staff-backed recipient and Data
  Rights correlation model; it must not silently drop or invent a Staff
  identity for an owner.

## Invariants

- an ownership transfer target must have one exact active Staff identity for
  the target organization and Auth subject;
- a missing, suspended, departed, anonymised, malformed, or cross-workspace
  identity is denied before the ownership transaction mutates state;
- initial organization creation is unchanged because its owner Staff identity
  is created by the existing resumable bootstrap after the organization fact;
- non-transfer organization mutations retain the existing operational
  admission decision;
- Staff-reader or admission-policy failure is unavailable and retryable, not
  treated as an authorization success;
- no Staff status, BunkFy role, or notification assumption is added to GMA.

## Delivery

1. Extend `WorkspaceOrganizationMutationAdmissionPolicy` with the public Staff
   operational-identity reader.
2. For `TransferOwnership`, validate and read the exact target subject in the
   organization scope and require `StaffStatus.Active`.
3. Preserve the existing operational-admission decision for every operation.
4. Add focused policy tests for allow, deny, tenant isolation, malformed
   targets, unavailable admission, and non-transfer behavior.
5. Run the extension and affected Workspaces/Staff/Operations Notifications
   suites, then one consolidated non-Docker repository gate at the slice end.

## Deferred

- Hosted ownership-transfer browser rehearsal belongs to the exact release
  candidate.
- Product copy may explain the active-Staff prerequisite more specifically;
  the generic Organizations API intentionally returns its product-policy
  rejection contract.

## Evidence

- Focused BunkFy Workspaces extension tests passed 119/119.
- `pwsh ./eng/verify.ps1 -SkipRestore` passed synchronized solution and source
  checks, a zero-warning build, all migration-drift checks, and every
  non-Docker suite. The affected Staff, Workspaces, Operations Notifications,
  Architecture, and integration suites passed 285, 397, 107, 112, and 65
  tests respectively.
- No Docker gate was run because the slice changes only an in-process product
  policy and introduces no schema, provider, broker, or host-topology behavior.
- GMA source remained unchanged; the existing generic Organizations mutation
  admission seam was sufficient.

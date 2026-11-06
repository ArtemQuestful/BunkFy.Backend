# Operations Notifications Least-Privilege Audience Task

Status: implemented; production activation remains blocked by the existing
notification-retention approval gate
Date: 2026-08-11

## Goal

Prevent a property assignment from being treated as authority to receive every
operational notification. Every property-scoped notification copy must require
the recipient's current read permission for the resource opened by that copy.

## Finding

The projector already required active organization membership, an active Staff
correlation, and exact property assignment or workspace ownership. Except for
Data Rights alerts, handlers did not require a domain permission. A staff member
with a narrow custom role could therefore receive a durable inbox summary for a
domain they could not open. Endpoint authorization protected the destination,
but it did not undo disclosure in the inbox copy.

## Ownership Boundary

- Source modules continue to own events and permission codes.
- Operations Notifications owns the mapping from a notification destination to
  the read permission required for its audience.
- Access Control remains the generic authorization authority and is queried in
  bounded batches at the exact property scope.
- GMA Notifications continues to own generic inbox persistence and delivery.
- Staff lifecycle and property-assignment contracts are unchanged.

No BunkFy policy is moved into GMA.

## Audience Policy

- property lifecycle copies require `properties.read`;
- inventory block and sales-mode copies require `inventory.read`;
- reservation lifecycle and arrival copies require `reservations.read`;
- provider-operation attention copies open Integrations and require
  `ingestion.read`;
- Data Rights deadline copies continue to require `data-rights.read`; and
- direct Staff lifecycle and assignment copies remain self-addressed and do not
  become a staff-directory broadcast.

The projector API requires a non-null `PermissionCode` for every property
fan-out. This makes omission a compile-time error for new handlers. Membership
filtering runs first, authorization narrows the candidates before exact Staff
correlation, and no copy is persisted until every returned recipient is
validated. A later current-state Staff omission may narrow the audience without
poisoning valid recipients; authority failures and malformed, unexpected, or
duplicate resolver output remain closed.

## Operational Notes

The current BunkFy operational tag catalogue requests only the durable web
inbox. Reading the inbox requires current workspace access, and opening the
destination is independently authorized by its module. If email, SMS, or push
tags are added later, delivery-time reauthorization must be designed before
those tags are enabled because a queued external delivery can outlive a role or
assignment change.

Existing pre-admission history is covered by the production gate's explicit
`ResetBeforeAdmission` or `VerifiedReferenceComplete` disposition. This slice
does not infer that historical copies were authorized or silently rewrite GMA
history.

## Verification

- normal property fan-out excludes candidates denied the mapped read
  permission;
- all authorization requirements use the exact property scope;
- each notification family maps to its owning domain read permission;
- authorization authority failures propagate before any copy is projected;
- active-membership, Staff-correlation, actor-exclusion, batching, typed
  payload, replay-id, and Data Rights tests remain green; and
- one consolidated non-Docker backend gate is run before publication.

Docker and hosted CI are deferred to the release boundary because this slice
changes in-process audience policy only and has no schema, broker, provider, or
deployment-topology change.

## Completion Evidence

- Operations Notifications focused tests pass 104/104.
- The synchronized solution and source-package checks pass; the full solution
  builds with zero warnings and zero errors.
- Every PostgreSQL and SQL Server migration model is drift-free.
- The non-Docker repository gate passed iteratively: all projects before the
  documentation index guard were green; after adding this task to the index,
  Architecture passes 102/102, Migrations Host 26/26, Service Defaults 63/63,
  and Integration 60/60.
- No Docker or hosted CI run was spent on this in-process, schema-free slice.

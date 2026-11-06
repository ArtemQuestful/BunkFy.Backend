# Staff Module

`Staff` owns BunkFy's operator employment profiles and property work assignments. It does not own credentials, sessions, roles, grants, or effective authorization.

## Boundaries

- GMA Auth owns accounts and authentication lifecycle.
- GMA AccessControl owns roles, permission grants, and scope evaluation.
- Staff owns `StaffMemberId`, profile data, employment lifecycle, optional Auth user-subject correlation, and assignment history.
- Properties owns property identity/lifecycle; Staff consumes a local monotonic projection.
- Linking an Auth subject or assigning a property never grants access.

Profiles may be `Active`, `Suspended`, `Departed`, or irreversibly
`Anonymised`. Suspension is explicitly reversible; departure closes current
assignments while retaining their history until Data Rights or automatic
retention performs the terminal scrub. A profile may be unlinked from Auth,
and an Auth user may exist without a Staff profile.

Account-link transitions preserve the Workspaces access boundary. Link a
subject directly only to an active, currently unlinked Staff profile. To move a
linked profile to another account, suspend the Staff member first so Workspaces
can deny the old subject's access, clear the link while suspended, resume the
unlinked profile, and then link the new subject. Linking the new subject does
not copy or grant membership, roles, or permissions.

Manual management creation always produces an unlinked profile. Operators use
the dedicated account-link mutation afterward; only workspace onboarding and
owner identity bootstrap may correlate their already-authorized subject while
creating or reconciling a profile.

Manual account-link changes require the dedicated
`staff.account-links.manage` capability and configured privileged-operation
assurance. The capability is available to custom roles but is absent from
ordinary operational seeds and the company-support ceiling. Workspace
onboarding remains the normal account-correlation path.

## Permissions

- `staff.read`
- `staff.sensitive-profile.read`
- `staff.create`
- `staff.manage`
- `staff.account-links.manage`
- `staff.assign-properties`
- `staff.manage-lifecycle`
- `staff.employment-governance.manage`
- `staff.data-holds.manage`

`staff.read` exposes the operational directory only: display name, job title,
department, status, and bounded current-assignment facts. Tenant directory pages
return a current-property count; property pages return only the assignment for
the requested active property. Both use deterministic offset paging with a
one-row look-ahead for `HasMore`. Full profile reads require
`staff.sensitive-profile.read`; the identity-bound self-service route remains
available to the current Staff subject. Profile, account-link, lifecycle, and
assignment writes return directory-safe results rather than the sensitive
profile they mutate. Profile editing and manual account correlation are
separate capabilities; the latter also requires sensitive-profile read access.
Canonical profile/create/update/lifecycle routes require tenant scope.
Property discovery and assignment routes require
`tenant/property` scope. Property grants do not satisfy tenant operations.

The self-service profile contract allowlists display name, legal name, work
email, work phone, job title, and department. Employee number remains
management-owned: self-service requests cannot supply it, and Staff preserves
the current value under the identity-bound member lock. Self-service retry
equivalence covers only that allowlist and is namespace-separated from manager
profile updates.

Ordinary profile creation, profile updates, account-link changes, employment
lifecycle transitions, and direct property-assignment changes use a
caller-owned operation id. Creation returns its original directory-safe result;
the other mutations return a Staff-owned, immutable minimal receipt. Equivalent
retries return the original result without another Staff version or event;
changed or cross-kind reuse conflicts. The shared Staff member-mutation journal
stores an explicit operation kind, canonical request fingerprint, and result
facts without duplicating profile, Auth-subject, assignment, or reason values.
It participates in Staff export and tenant lifecycle, and is removed when the
profile is anonymised. Workspace onboarding profile provisioning remains a
separate integration contract: it may create a missing profile or update an
active one, but it cannot alter employment lifecycle.

Authoritative Staff member and property-assignment rows are also protected by
named relational checks. They enforce valid coordinates, normalized profile
and search-field pairs, anonymised profile scrubbing, lifecycle and assignment
time ordering, and non-blank actor evidence. Filtered unique indexes permit at
most one current assignment per member and property and at most one current
primary assignment per member. These constraints complement aggregate guards,
member locks, optimistic versions, and idempotency journals; they do not move
property identity or access authority into Staff.

## Processing restrictions

Approved tenant-scoped Staff data-rights cases can apply or release processing restrictions. Staff owns the reference-counted effective state and append-only transition receipts; Data Rights owns approval and orchestration. Missing or future projection contracts fail closed.

Restricted staff members are excluded from directory/detail/self-service reads, profile and assignment writes, identity bootstrap, onboarding provisioning, and operational notification audiences. Data-rights discovery, export, correction, and restriction execution remain available. Suspend, depart, and unassign remain available as safety-reducing transitions; resume and access-link changes remain blocked. Restriction does not mutate Auth credentials or AccessControl grants.

## Data rights and retention

Staff is the tenant-scoped owner contributor for discovery, bounded export,
correction, and processing restriction. Data Rights owns cases, requester
verification, approval, orchestration, and central completion; it does not read
Staff persistence.

Staff also exposes two narrow cross-module contracts for operational
notifications: a bounded resolver from already-authorized Auth subjects to
active, unrestricted Staff records, and a minimal Staff Rights authority
snapshot containing only record id, version, and lifecycle state. Operations
Notifications uses them to maintain and govern a stable Staff inbox-history
companion without reading Staff persistence or placing Auth identifiers in Data
Rights coordinates.

The
[Staff anonymisation flow](../../../../docs/planning/staff-data-rights-anonymisation-task.md)
uses explicit employment-governance evidence, independently releasable Staff
holds, per-member operation serialization, immutable owner receipts, and an
authority-bound tombstone. Protected restore applies only to Data
Rights-authority tombstones.

Automatic retention uses the shared Retention control plane but keeps all
Staff identifiers and mutation decisions inside Staff. A tenant-scoped,
bounded sweep selects departed profiles by projection ordinal, evaluates the
exact `staff-employment` country-policy rule, observes holds, and revalidates
under the Staff operation lock. Before the irreversible scrub, a versioned
`Staff.Contracts` prerequisite requires Workspaces to prove the completed
departure access process and idempotently re-deny access. Staff then records
its own append-only receipt and a `Retention`-authority tombstone; Retention
receives only counts and stable outcome codes.

Staff carries the active Retention attempt through mutation and completion.
Failed attempts leave the fair-scan checkpoint untouched, later attempts
resume from the exact failed cursor, and stale workers cannot mutate or
terminalize newer work. PostgreSQL independently rejects unreachable control
state, malformed proof, duplicate event/cursor markers, and receipts missing
their same-tenant execution, Staff member, or tombstone.

The executable delivery contracts are
[Staff record automatic retention](../../../../docs/planning/staff-record-retention-task.md)
and
[Staff retention control and proof audit integrity](../../../../docs/planning/staff-retention-control-proof-audit-integrity-task.md).

## Tenant termination export

Staff is the mandatory `staff` export owner after Guests. Its tenant-wide
portability stream contains the authoritative profile and assignment history,
correction proof, processing restrictions and receipts, employment governance
and change proof, data holds and receipts, anonymisation receipts and
tombstones, restore proof, and retention execution and anonymisation proof.
Auth credentials and sessions, organization membership, roles, grants, and
effective authorization remain with their GMA owners.

Relational Staff writes and export selection share the BunkFy tenant-mutation
transaction key. Each ordinary unit of work rechecks the authoritative
Workspaces fence and advances one tenant-local revision; export holds the key
in a repeatable-read transaction and succeeds only when the exact process,
epoch, fence, and local revision remain unchanged. PostgreSQL independently
protects all seven immutable receipt ledgers and prevents tombstone deletion.

Property and restriction projections, inbox/outbox state, operation locks,
rebuild and retention sweep checkpoints, search copies, and the internal
revision row are excluded. The production owner task, API/download route, and
cleanup runner remain disabled until every mandatory owner and final
confirmation flow are complete.

## Tenant termination destruction

Staff owns destructive removal of its employment profiles, assignment history,
employment governance, legal holds, projections, journals, checkpoints, and
local proof. Auth accounts and sessions, organization memberships, and access
control remain separate GMA-owner responsibilities.

An active Staff legal hold blocks before progress. Otherwise the exact frozen
request closes local admission and removes one non-empty batch of at most 500
rows per call in foreign-key-safe order. Scoped inbox work and projection
writes are rejected, outbox claims exclude closing scopes, and active leases
must drain or expire. Exact retries resume or replay; changed coordinates
conflict.

Completion retains only the closed tenant lifecycle row and an immutable,
PII-free receipt with selected/resulting revision, counts, and a versioned
SHA-256 removal proof. PostgreSQL permits deletion of existing immutable
receipts and tombstones only under the matching transaction-local destruction
operation; updates and ordinary deletes remain blocked. Production execution
stays disabled until cross-owner preflight, protected replay, operator
controls, and final production admission are complete.

## Runtime

The module is composed in public API, Admin API, Admin CLI, and the optional worker group. PostgreSQL migrations live in `BunkFy.Modules.Staff.Persistence.PostgreSqlMigrations`. `AddStaffAuthoritativeStateIntegrity` adds the member and assignment fail-closed database contract. The worker consumes Properties lifecycle facts and runs `rebuild-staff-properties` to repair the local projection.

The root superproject's deployed Staff employment verifier, specified by the
[Staff deployment-proof task](../../../../docs/planning/staff-deployed-employment-lifecycle-proof-task.md),
exercises the public contracts for one minimal unlinked profile: idempotent and
optimistic profile changes, property assignment, suspension and resume, and
departure with atomic current-assignment closure. Its minimized evidence is a
required production-admission input. Account linking, membership, roles, and
session lifecycle remain independent invitation, enrollment, and access proofs.

General integration events carry only tenant, staff, lifecycle, Auth-correlation, assignment, effective-date, and version facts. They exclude names, contact details, employee numbers, job labels, departments, and reasons.

[`personal-data-catalog.v1.json`](personal-data-catalog.v1.json) is the executable Staff data contract. [`personal-data-inventory.v1.md`](personal-data-inventory.v1.md) is generated from it and checked by reflection tests against persistence, search copies, public/admin boundaries, cross-module requests, domain events, and integration events.

Broader deferred work is tracked in
[the Staff Profiles task](../../../../docs/planning/staff-profiles-module-task.md).

# Properties

Properties owns BunkFy's tenant-scoped physical accommodation topology:
properties, rooms, owned beds, facility labels, time zones, lifecycle state,
and immutable projection ordering. It publishes versioned topology facts for
consumer-owned projections and never writes another module's schema.

The module's executable personal-data contract is
[`personal-data-catalog.v1.json`](personal-data-catalog.v1.json). Its generated
[`personal-data-inventory.v1.md`](personal-data-inventory.v1.md) is checked by
reflection- and EF-model-backed tests. Property, room, building, floor, and bed
labels describe the facility and remain outside the personal-data catalogue.

Properties carries two kinds of person-linked coordinate: the transient
authenticated subject used to resolve visible property scopes, and a bounded,
server-derived actor reference for lifecycle governance and property time-zone
provenance. The subject is not stored. The actor is retained only in append-only
governance revisions and post-migration create/set time-zone operation records;
Admin API and CLI provenance uses the distinct `admin-api:` and `admin-cli:`
prefixes, while Public provenance remains derived from the authenticated
subject kind and id. Missing or overlength Admin provenance fails closed. The
actor is returned through the permissioned time-zone receipt/recovery Public and
Admin surfaces, included in authorized tenant export, and removed or
pseudonymized through the approved retention and tenant-destruction workflow.
It does not enter topology/list projections or arbitrary support output.
Catalogue policy values are engineering defaults until country, retention, and
rights approval.

Properties does not own guests, reservations, sellable inventory, staff
profiles, accounts, roles, grants, rates, provider mappings, maintenance,
housekeeping, or arbitrary notes. Consumers keep their own projections and do
not read Properties tables.

For tenant termination, Properties is the mandatory `properties` owner.
Export depends on Workspaces and emits the authoritative property, governance
acknowledgement, room, bed, and governance-revision records. Destruction waits
for Inventory, Operations Notifications, and Retention, then removes all
tenant-owned topology, governance history, and transport journals in explicit
scope-bound batches of at most 500 physical rows. It retains only closed local
lifecycle state and one immutable, PII-free destruction receipt. Derived
projections, inbox/outbox messages, and local consistency state are not
portable business data and remain excluded from export.

Relational operational writes and tenant export selection share the BunkFy
tenant-mutation transaction key. A write rechecks the Workspaces termination
fence under that key and fails closed when admission is unavailable; each
successful Properties unit of work advances one tenant-local revision. Export
holds the same key in a repeatable-read transaction and must return the same
selected and resulting revision. PostgreSQL also rejects updates or deletes to
property governance revisions independently of the application guard, except
for an exact live destruction operation bound to the local closing state.

## Operational surface

Property, room, and bed directories return minimized rows in stable
code/name/label-and-id order. Pages use one-row lookahead and report `HasMore`;
they do not run exact-count queries. Detail endpoints remain the authority for
full property and room state.

Ordinary topology writes return contract-owned receipts containing only the
affected identifiers, status, and concurrency versions. Clients must invalidate
and refetch the reads they display. Public and Admin HTTP routes emit no-store
headers, and Admin endpoints declare their success response shape explicitly.

Property creation is keyed by a caller-supplied operation id that becomes the
property id. Each attempt rechecks workspace admission and serializes that
tenant-scoped coordinate before reading or creating topology. An exact
normalized retry returns the current minimal receipt without another event;
reusing the id for different property details is a conflict. Property-code
uniqueness remains independently serialized, and no request payload is retained
for replay outside the prospective time-zone provenance described below.

Property details updates use a separate caller-supplied operation id scoped to
the property. Exact retries return the immutable receipt recorded by the first
successful attempt even when the property has since advanced; changed reuse is
a conflict. A normalized no-op records a successful receipt without advancing
the property version or publishing an event. Failed validation, stale-version,
and duplicate-code attempts do not reserve the operation id. The append-only
operation journal stores only coordinates, a canonical digest, and the minimal
result; it is included in tenant export and bounded destruction.
Generic update may omit the time zone and then preserves the exact persisted
value, including an alias or legacy identifier. A non-null value is accepted
only when it equals that current raw value; any different value returns
`Properties.TimeZoneDedicatedOperationRequired` and must use the dedicated
correction workflow. Explicit blank or control-character input remains invalid.
The v3 replay digest distinguishes omission from a supplied value while
retaining compatibility with the supported earlier fingerprints.

Public processing activation or policy rebinding and terminal property
retirement may be protected by host-configured authentication assurance.
BunkFy's public host requires a recent sign-in for both and the browser retries
the same idempotent lifecycle attempt after step-up. Processing suspension
remains immediately available so protective containment is never delayed.
Authentication policy stays outside the domain model; see
[Properties Sensitive-Control Assurance Task](../../../docs/planning/properties-sensitive-control-assurance-task.md).

Property reads classify the stored time-zone value against a versioned IANA
catalog and expose a resolved primary coordinate without making legacy rows
unreadable. For a canonical row that coordinate equals the stored identifier;
a UI should present it as a suggestion only when it differs. A recognized
non-TZDB legacy value has no resolved coordinate; a TZDB identifier whose
serving runtime cannot execute with UTC-offset rules compatible with the pinned
catalog retains its resolved primary coordinate. The same or another
runtime-incompatible write target fails closed.
Runtime availability is a behavioral check from the observation instant over a
bounded horizon of at least five years. The probe compares offsets at the union
of embedded and host interval boundaries and at every interval midpoint. It
applies only to the serving process, not the fleet; it fails closed and caches
by zone and UTC observation year.
An unsupported UTC clock value fails separately as
`Properties.TimeSourceUnavailable` before time-zone health evidence or a
time-zone-sensitive mutation can be reported. This is a 503 serving-time
failure, not evidence that the selected zone is incompatible with the pinned
catalog.
`CorrectionAllowed` means that corrective remediation
is applicable, not that the endpoint is authorized: it is false for a canonical
row even though an active property's exact no-op can be journaled, and it is
true for an active `RuntimeUnavailable` row because a confirmed move to a
different compatible primary remains possible. The same or another
runtime-incompatible target fails with 503 before mutation. It is always false
for a retired property. Authorized Managers
receive an explicit observation timestamp with detail/list health, each
compliance page, and the current state in recovery. That timestamp identifies
per-response runtime evidence and is not the immutable operation receipt's
completion time. They can page the tenant compliance view, choose a
runtime-available primary identifier from the country-aware catalog, correct
one property with optimistic concurrency, and recover an immutable receipt by
operation id. Known TZDB aliases remain compatible write inputs and are stored
as their primary identifier; Windows and unrecognized identifiers are rejected
as targets.
The canonical catalog exposes no tenant or property state and therefore uses
`properties.read`, so a delegated creator or reader can discover a valid
identifier without receiving correction authority. Tenant readers use the
unscoped catalog route. A property-scoped reader uses the explicit
`/{propertyId}/time-zones/catalog` route, or CLI catalog `--property-id`, so the
same discovery is authorized only at that property and remains forbidden for
another property. Tenant compliance, set, and operation recovery remain
protected by the sensitive
`properties.time-zones.manage` permission.
Explicit confirmation is required only when the target is semantically
different; an exact no-op or known TZDB alias canonicalization does not require
it. Exact replay returns the original receipt only when the expected version and
normalized requested identifier match. The workflow never issues a tenant-wide
repair or silently defaults CLI property writes to UTC. Its ledger is
prospective: existing rows get current health from detail/compliance and no
synthetic historical receipt; recovery applies only to post-migration create or
set operation ids. A recovery 404 means no committed receipt is currently
visible, which can mean the attempt did not commit or is still in flight; it is
not a durable failed result. No `Pending`, `Rejected`, or `Failed` state is
journaled. Operators safely retry the exact property id, operation id,
requested identifier, and expected version before recovering again. Every
successful receipt proves that the authoritative
Properties state and immutable ledger entry committed atomically.
`Canonicalized` and `Changed` also atomically enqueue both the existing
`PropertyUpdated` topology fact and the tenant-scoped, non-actor
`PropertyTimeZoneChanged` v1 fact. The dedicated fact is the stable seam for
future rollups and time-zone-specific consumers. `Unchanged` emits no event or
outbox row. Consumer projections converge asynchronously and have no
acknowledgement barrier, so state-changing operations require downstream
projection convergence checks before they are treated as globally effective;
no-op receipts need no such step. Public correction requires recent
authentication; all public
and Admin Properties pipeline outcomes receive
no-store headers before routing, authentication, authorization, or binding. See
[Properties Canonical Time Zones Task](../../../docs/planning/properties-canonical-time-zones-task.md).

A future catalog-version upgrade uses a stopped-and-drained maintenance window.
One transaction takes `ACCESS EXCLUSIVE` locks on both catalog entry and
resolution tables, drops their immutable triggers under those locks, and
appends a complete new version without rewriting older rows. The operator
verifies the new version's exact expected counts (the current executable seam
proves 340 entries and 597 resolutions), every resolution target, and runtime
compatibility for each serving cohort. Recreate both triggers before commit,
then deploy the binary that embeds and selects the same version. Historical
receipts retain their original catalog version and continue to resolve through
the preserved rows. A failed verification or mismatched binary version aborts
the upgrade before writers resume.

Multi-bed creation is one atomic room command. The complete label set is
validated before mutation, is limited to 100 beds, and publishes the existing
bed-added fact once per created bed. The single-bed command remains available
for compatibility and follows the same aggregate rules.

The topology projection rebuild export still pages property roots while
embedding each selected property's rooms and beds. Replacing it requires a
versioned protocol coordinated with every projection consumer; it must not be
changed as an incidental read-performance optimization.

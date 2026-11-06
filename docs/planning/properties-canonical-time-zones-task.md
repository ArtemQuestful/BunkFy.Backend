# Properties Canonical Time Zones Task

Status: implementation complete; deployment proof pending
Date: 2026-08-13

## Goal

Make canonical IANA time-zone identifiers the operable Properties contract while
keeping legacy data readable and giving authorized operators a bounded,
recoverable way to correct hundreds of properties without a tenant-wide write
or fleet fanout.

## Boundary

- Properties owns stored property time zones, health classification, correction
  eligibility, the correction ledger, optimistic concurrency, and receipts.
- The shared time-zone catalog owns canonical IANA data, aliases, country
  metadata, comments, current offsets, and its version. It never suggests a
  Windows identifier.
- Workspaces owns who may delegate or receive
  `properties.time-zones.manage`. The seeded Manager receives it; operational
  Front desk, Housekeeping, Viewer, legacy-member, and Company Support grants do
  not.
- The canonical catalog contains no tenant or property data, so discovery uses
  the existing `properties.read` permission. Compliance, correction, and
  receipt recovery retain the dedicated sensitive permission.
- The BunkFy public host owns recent-authentication policy for the public change
  route. Admin API and CLI retain their existing authenticated, permissioned,
  audited execution boundary.
- No reusable GMA change is required.

## Operable Contract

Public and Admin HTTP surfaces provide equivalent resources:

- `GET /api/properties/time-zones/catalog` and
  `GET /api/admin/properties/time-zones/catalog` list canonical identifiers with optional `search`
  and `countryCode`, a catalog-bound opaque cursor, country labels, comments,
  current UTC offsets, host runtime availability, and one `ObservedAtUtc` for
  the page. Operators must not select an entry whose `RuntimeAvailable` is
  false; the write route will fail closed with 503. Availability is behavioral
  compatibility for this serving process: its runtime UTC-offset rules must
  match the pinned embedded TZDB from the observed instant through a bounded
  horizon of at least five years. It is neither a loadability-only check nor a
  fleet-wide guarantee.
- Property-scoped readers use the equivalent
  `GET /api/properties/{propertyId}/time-zones/catalog` or
  `GET /api/admin/properties/{propertyId}/time-zones/catalog` route. It exposes
  the same non-tenant catalog but resolves `properties.read` at that property,
  so a bounded correction operator can discover targets for an authorized
  property without receiving tenant-wide topology access. The tenant route is
  not weakened, and another property's route remains forbidden.
- `GET /api/properties/time-zones/compliance` and
  `GET /api/admin/properties/time-zones/compliance` page property health and correction eligibility
  for the current tenant. Rows include topology and processing state so an
  operator can distinguish an active governed property from a retired or
  otherwise non-correctable one.
- `PUT /api/properties/{propertyId}/time-zone` and
  `PUT /api/admin/properties/{propertyId}/time-zone` require an operation id,
  expected property version, property scope, the dedicated permission, and
  server-derived actor provenance. A primary TZDB identifier is recommended;
  a known TZDB alias is accepted and stored as its primary identifier, while
  Windows and unrecognized identifiers are rejected as targets. Explicit
  confirmation is required only when the resolved primary target is
  semantically different from the currently resolved TZDB zone.
- `GET /api/properties/{propertyId}/time-zone/operations/{operationId}` and
  `GET /api/admin/properties/{propertyId}/time-zone/operations/{operationId}` recover the immutable
  receipt together with current property time-zone state after a lost response.
- Existing property list and detail responses expose the stored value, health,
  resolved primary coordinate when one exists, catalog version, and whether a
  corrective remediation is currently applicable. A UI may present the primary
  coordinate as a suggestion only when it differs from the stored value. Their
  `TimeZoneObservedAtUtc`, the compliance page `ObservedAtUtc`, and recovery's
  `CurrentTimeZoneObservedAtUtc` timestamp the per-response runtime-derived
  evidence; they are distinct from an immutable receipt's `CompletedAtUtc`.
- Existing generic property update accepts an omitted `timeZoneId` and then
  preserves the exact persisted value, including an alias or legacy value. A
  supplied value is compatibility-only: it must equal the current raw value;
  any different value returns 409
  `Properties.TimeZoneDedicatedOperationRequired` and must use the dedicated
  correction route. Explicit blank or control-character input remains invalid.

The Admin CLI mirrors these as `properties time-zones catalog`, `compliance`,
`set`, and `operation-get`. Catalog and compliance accept bounded cursors;
catalog accepts optional `--property-id` to authorize discovery at a delegated
property scope, while omission retains tenant-reader authorization;
`set` requires `--property-id`, `--operation-id`, `--time-zone`, and
`--expected-version`. It accepts `--yes`, which is required for a semantic
`Changed` operation; exact no-ops and known-alias canonicalization may omit it.
Property create also requires an explicit `--time-zone` and never silently
defaults to UTC. Generic property update may omit `--time-zone` to preserve the
exact persisted value; a different supplied value is rejected and must use
`properties time-zones set`.

## Scale And Recovery

Compliance uses tenant-scoped keyset pages rather than exact counts or a single
fleet mutation. An operator reads one bounded page, corrects only selected
properties with per-property optimistic versions, records each receipt, and
continues with the returned cursor. A stale version affects only that property.
An interrupted run resumes from the compliance cursor and recovers any uncertain
write by operation id; it does not replay a tenant-wide command.

An exact operation replay returns the immutable original receipt even if the
retry carries a different confirmation value or server-derived actor. The retry
must still match the original expected version and exact normalized requested
identifier; reusing the operation id for a different request fails with an
operation conflict.

Recovery exposes committed receipts only. A 404 means no committed receipt is
currently visible: the original attempt may not have committed or may still be
in flight. It is never evidence of a durable `Failed` outcome because this
ledger journals no `Pending`, `Rejected`, or `Failed` state. The safe action is
an exact retry with the same property id, operation id, requested identifier,
and expected version, followed by recovery when needed.

A successful set receipt proves that the Properties authoritative state and
immutable operation ledger committed atomically. `Canonicalized` and `Changed`
also atomically enqueue both the existing `PropertyUpdated` topology fact and
the tenant-scoped, non-actor `PropertyTimeZoneChanged` v1 fact. The dedicated
fact is the stable seam for future rollups and time-zone-specific consumers;
`Unchanged` records no event or outbox row because no consumer state changed. A
state-changing receipt is not a consumer acknowledgement barrier and must not
be described as globally applied: Reservations, Guests, rights/deadline flows,
and other consumer-owned projections converge asynchronously. After every
`Canonicalized` or `Changed` operation, operators must verify the relevant
consumer projection versions and time-zone state before treating downstream
behavior as converged.

Catalog cursors bind the catalog version, normalized search, normalized country
filter, and last canonical identifier. Compliance cursors bind stable property
projection order. A changed catalog or mismatched filter fails as an invalid
query rather than silently skipping or duplicating rows.

## Runtime Matrix

| Stored value | Health | `CanonicalTimeZoneId` | Read/list | Corrective remediation |
| --- | --- | --- | --- | --- |
| Canonical IANA id | `Canonical` | stored primary id | allowed | `false`; no correction is needed, though an exact no-op operation may be journaled when the property is active |
| IANA alias | `Alias` | resolved primary id | allowed | `true` when active; set the resolved primary id |
| Recognized non-TZDB legacy id | `Legacy` | none | allowed | `true` when active; operator selects a primary IANA id and no Windows suggestion is returned |
| Unknown id | `Unrecognized` | none | allowed | `true` when active; operator selects from the catalog |
| TZDB id whose serving runtime rules are incompatible with pinned TZDB | `RuntimeUnavailable` | resolved primary id | stored value remains visible | `true` when active; a confirmed move to a different compatible primary is allowed, while the same or another incompatible target fails 503 |
| Retired property | derived health above | as derived | allowed to authorized readers | `false`; mutation is prohibited |

`CorrectionAllowed` describes whether corrective remediation is applicable; it
is not an authorization decision and does not indicate whether the set endpoint
can journal an idempotent no-op.

`UTC` canonicalizes to `Etc/UTC`. Detail, list, compliance, receipt, and
recovery values are catalog-versioned so clients do not mistake a runtime guess
for a permanent fact. Across the bounded horizon, the production probe compares
UTC offsets at the union of embedded and host interval boundaries and at every
interval midpoint. It fails closed and caches results by zone and UTC
observation year. A positive result applies only to the process that performed
it; deployment readiness must verify every serving runtime cohort.

`Confirmed=true` is required only for `Changed`: the target differs
semantically from the current TZDB-resolved zone, including a legacy, Windows,
or unrecognized value being replaced by a canonical id. `Unchanged` and
`Canonicalized` are safe without confirmation; the latter covers a known TZDB
alias such as raw `UTC` becoming `Etc/UTC`.

## Security And Failure Semantics

- Catalog requires a tenant plus `properties.read`, allowing ordinary property
  creators and readers to discover valid identifiers without correction
  authority. The explicit property catalog route resolves that same read
  permission at the requested property and does not authorize another
  property. Compliance requires the dedicated sensitive permission; set and
  recovery require it at the resolved property scope.
- Public actor provenance is derived from the authenticated access subject.
  Admin provenance is exactly `admin-api:` or `admin-cli:` plus the
  authenticated `IAdminActorContext.Actor.Id`; a missing or overlength context
  fails closed, and the ledger therefore retains the originating surface.
- The public set route receives host-configured recent-authentication assurance.
  Reads and recovery do not, allowing diagnosis after a step-up failure.
- Every public and Admin Properties response receives `Cache-Control: no-store`,
  `Pragma: no-cache`, and `Expires: 0` from startup middleware before routing,
  authentication, authorization, or request binding. These headers therefore
  cover success, validation, 401, and 403 outcomes.
- Invalid queries and confirmation failures are 400; missing properties or
  operation receipts are 404; a dedicated-operation requirement or optimistic
  conflict is 409; an unsupported UTC observation instant and host-versus-TZDB
  behavioral incompatibility are distinct 503 failures. The former means the
  serving clock cannot produce a safe observation; the latter means the target
  zone's host offset rules do not match the pinned catalog horizon.
  Country-policy denials retain their existing conflict mapping.

## Deployment And Rollback

This slice supports PostgreSQL only. Before applying its migration or enabling
the new surfaces, stop and drain every old public API, Admin API, Admin CLI,
worker, migrations host, and tenant-destruction writer that can touch the
Properties schema. Confirm there is no in-flight tenant destruction operation.

The release is **NO-GO** until a readiness scan covers every active or
processing property, every projected consumer state, and every deployed custom
country-policy pack, not merely the database shape. For each row, record stored
time-zone health, resolved primary coordinate, runtime availability, operating-country
binding, processing status, current version, and whether the tightened
country-policy registry admits the zone. Validate every pack's
`AllowedTimeZoneIds` under the new canonical-primary-only rule; a TZDB alias in
a custom pack can fail startup/admission even when all property rows are clean.
Any active governed alias/legacy/unrecognized/runtime-unavailable value, invalid
pack entry, policy denial, consumer mismatch, incomplete scan, or in-flight
writer/destruction job blocks enablement until explicitly corrected or waived
through the approved launch process. A Preview scan is rehearsal evidence, not
production proof.

After the production readiness scan passes, take the normal pre-migration
backup, apply the migration once, deploy one coherent release, and only then
resume writers. The migration's structural checks do not replace this
application-level readiness evidence. During correction, every state-changing
receipt is followed by explicit convergence checks for each affected
Reservations, Guests, rights/deadline, and other projected consumer; a stalled
or divergent projection blocks further semantic changes and triggers the
documented operational response. `Unchanged` requires no consumer convergence
step because it publishes no event.

The migration creates the correction-operation ledger and related constraints;
it does not rewrite, canonicalize, or silently backfill any existing property
time-zone value. The ledger is prospective: migration emits no synthetic
`Created` receipt for an existing property because its original actor, request,
operation id, catalog version, and observed time cannot be reconstructed
truthfully. Existing rows expose current health through detail and compliance;
operation recovery exists only for post-migration create or dedicated set
operation ids. Legacy rows stay readable and appear in compliance until an
authorized per-property operation changes them; this slice does not claim full
historical time-zone provenance. `Down` intentionally refuses
to remove the immutable operation ledger because doing so would discard audit
and recovery evidence once any row exists; it also refuses while the added
tenant-destruction stage is in flight. An empty, unused ledger may technically
be removed, but that is not the operational rollback plan. Rollback criteria
are any failed migration or startup, policy/runtime mismatch, incorrect health
classification, broken consumer projection, authorization/assurance regression,
or lost receipt/recovery proof. Rollback means stop and drain all writers again,
restore the verified pre-migration backup, and deploy the previous release; do
not attempt an in-place downgrade after evidence has been recorded.

### Future catalog-version upgrade

A future embedded catalog change is a stopped-and-drained maintenance
operation, not an online row rewrite. Stop and drain every Properties writer,
then in one transaction take `ACCESS EXCLUSIVE` locks on both
`properties.property_time_zone_catalog_entries` and
`properties.property_time_zone_catalog_resolutions`. Drop their immutable
triggers only while both locks are held; append complete rows under the new
catalog version and never update or delete an older version. Before recreating
the triggers, verify the new version's exact expected entry and resolution
counts (the current executable seam proves 340 entries and 597 resolutions),
every resolution target, and runtime compatibility for every serving cohort.
Recreate both immutable triggers before commit, then deploy the binary that
embeds and selects that exact version. Existing receipts continue to reference
their original catalog-version rows. Any failed count, resolution, runtime,
trigger, or binary-version check aborts the upgrade while writers remain
drained.

## Verification Cadence

Focused contract, API-security, CLI, Workspaces seed, application, and
persistence tests run first. Real public/Admin runtime tests must then prove
headers, scope/permission denial, recent-authentication behavior, actor
attribution, paged compliance, per-property correction, immutable retry, and
operation recovery. Migration drift and the consolidated non-Docker backend
gate run once all lanes are coherent.

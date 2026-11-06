# Data Rights

Data Rights coordinates controller-managed privacy and tenant-termination work
without taking ownership of another module's records. The module's executable
personal-data contract is
[`personal-data-catalog.v1.json`](personal-data-catalog.v1.json), with a
deterministically generated
[`personal-data-inventory.v1.md`](personal-data-inventory.v1.md).

## Current Slice

- tenant- and property-scoped, optimistic-concurrency case lifecycle;
- idempotent case intake that uses an operator-attempt UUID as the aggregate
  identity, serializes through the tenant-qualified case lock, and accepts a
  replay only when immutable scope, request, and creator coordinates match;
- explicit requester-verification and controller-routing gates before discovery;
- calendar- and daylight-saving-safe response deadlines for externally
  requested Guest Rights cases, resolved from the property time zone and exact
  schema-v2 country-policy binding without rejecting intake when policy data is
  temporarily unavailable;
- immutable deadline evidence freezes the controlling right, calendar period,
  policy digest and property topology/policy source revisions; routing retries
  missing evidence and discovery fails closed until it is assigned;
- PII-minimal case storage with authenticated staff actor attribution;
- bounded, exact-coordinate discovery delegated to authoritative owner modules;
- Guests-owned discovery with property-history visibility, masked contact hints,
  and owner revalidation before an opaque coordinate can be selected;
- no-store discovery responses and no lookup criteria persisted in the case;
- bounded subject selection that must be non-empty before review can begin;
- explicit review, decision-pending, approved or denied transitions with
  bounded reason codes and immutable decision revision/attribution;
- anonymisation approval is allowed only as a standalone operation and freezes
  server-resolved property, country-policy, retention-policy and digest
  evidence into the immutable decision revision;
- destructive approval requires an active, processing-enabled local Properties
  projection and the current country-policy pack to allow the erasure surface;
- the executor identity is rechecked against the approval revision and must be
  distinct from the deciding actor;
- starting anonymisation requires both the tenant-scoped erase permission and
  routing-property read permission, plus the configured destructive-operation
  authentication assurance;
- an idempotent execution batch freezes the exact approval, operation, bounded
  selected owner coordinates, record versions, policy digest and executor into
  ordered PII-minimal work items before any owner module can be invoked;
- durable, retry-safe worker dispatch invokes a versioned owner contributor,
  records the immutable owner proof and resumes safely after process failure;
- bounded owner task registrations cover each two-minute module deadline plus
  terminal persistence, while verification derives its outer worker boundary
  from the existing 64-owner contract; short tasks retain the host fallback;
- one terminal self-event per work item reconciles the case only after every
  selected owner has a durable result; all-success, all-unsuccessful and mixed
  batches become `Completed`, `Blocked` and `PartiallyCompleted` respectively;
- an append-only, HMAC-pseudonymised processing ledger plus an externally
  protected encrypted replay delta preserve deletion proof outside the
  application database;
- bounded restore reconciliation recreates missing ledger entries, invokes
  owner tombstone replay and advances a tenant checkpoint only after exact
  owner proof succeeds;
- API, Admin API, Admin CLI and Worker startup remain blocked until the
  protected recovery-scope snapshot is stable and fully reconciled;
- a PII-free, fail-closed owner-module approval gate that matches the exact
  tenant, property, operation, approved revision and selected record version;
- restriction approvals bind an explicit apply or release directive, so one
  approved intent cannot authorize the opposite transition;
- resumable selected-coordinate reads behind the sensitive discovery
  permission, plus a separate Review-guarded read of only the coordinates and
  pinned versions already attached to a case; ordinary case DTOs expose only a
  count;
- a PII-carrying owner-export envelope that is explicitly catalogued as a
  subject-scoped, cross-module, one-hour transient fragment;
- a neutral streaming contributor/sink contract; callers must discard partial
  fragments unless the owner returns success;
- a Data Rights-owned, short-lived protected-export aggregate that assembles
  bounded owner fragments, stores only encrypted artifacts in `IFileStorage`,
  and streams authenticated plaintext only through assurance-gated endpoints;
- exact terminal/transient owner failure semantics: automatic task retries keep
  the artifact active, while a failed artifact requires an assurance-gated,
  version-pinned and replay-safe operator retry;
- scoped permissions that are not granted to ordinary seeded roles;
- public controller API plus tenant-termination Admin API and Admin CLI
  operator controls;
- a DataRights-owned, rebuildable Properties topology and policy projection,
  including the property time zone, populated only through versioned
  Properties events or the bounded projection-rebuild contract;
- the projection is explicitly tenant-filtered through the GMA scoped-entity
  conventions; ordinary reads and writes require the active tenant, while two
  tenants may safely carry the same property id;
- topology and policy streams advance independently, ignore older facts,
  accept exact equal-version replays, and fail closed on conflicting replays;
  PostgreSQL binds tenant coordinates, partial-stream state, policy formats,
  known-state derivation, and acknowledgement shape with named constraints;
- PostgreSQL persistence, inbox/outbox infrastructure and focused architecture,
  privacy, domain, persistence, migration and authorization tests;
- an audited tenant-termination coordinator with one active process per tenant,
  exact owner-proof coordinates, bounded PII-free contributors, Admin API/CLI
  controls, protected start-intent recovery, and Worker-only destructive
  production admission.

The worker can rebuild the Properties policy projection for one tenant:

```powershell
tasks runs enqueue --tenant <tenant-id> --module data-rights --task rebuild-data-rights-properties --worker-group projection-workers --payload-json '{"projectionVersion":1,"batchSize":100,"dryRun":false}'
```

The case does not contain guest names, contacts, documents, search criteria,
provider payloads or free text. It stores only the selected owner's opaque
record coordinate and selection audit attribution. Guest data remains owned by
its source module. Data Rights never persists owner fragments or plaintext
exports: it persists bounded artifact lifecycle and integrity metadata while
encrypted artifact bytes remain in the configured file-storage adapter.
Protected downloads revalidate scope, permission, expiry, integrity, and fresh
MFA/two-step assurance. Guest anonymisation now reaches immutable owner and
processing-ledger proof, with protected pre-readiness replay after database
restore. Existing reservation facts remain owned by Reservations; only its
local Guest-link eligibility projection is updated from the PII-free Guests
event.

## Operational Surfaces

- The controller queue returns compact case summaries. Full selected-coordinate
  and approval/deadline evidence remains available only through scoped detail
  reads. The compact Guest Rights summary carries requester relationship and
  due time so the UI can distinguish policy-pending, scheduled, due-soon and
  overdue cases without an N+1 detail query.
- A tenant-scoped recurring task scans eligible external Guest Rights cases in
  bounded batches. It records one append-only receipt per case and alert kind
  in the same transaction as the PII-minimal outbox event: due-soon within 48
  hours, then overdue after the deadline. Delayed runs emit only the current
  state, and terminal or policy-pending cases are excluded.
- Data Rights publishes the deadline fact but does not choose notification
  recipients. The Operations Notifications extension applies current Staff
  membership and exact property-scoped `data-rights.read` authorization.
- Queue reads use a no-tracking scalar projection, stable ordering, and one-row
  lookahead to return truthful `HasMore` without an exact-count query.
- Property- and tenant-scoped public route groups apply `no-store` and related
  cache protections to every response, including case lifecycle responses.
- Exact subject discovery remains bounded to 20 candidates and returns
  `LimitReached` when that bound is filled, prompting the operator to refine a
  strong identifier rather than broadening the search.
- Review and Discover remain deliberately separate capabilities. Reviewers can
  read only the selected opaque coordinates and pinned versions through the
  exact-scope `review-evidence` route; lookup, candidate disclosure, selection,
  and unselection continue to require Discover. The client version-binds this
  evidence and withholds review or decision actions when it is missing, stale,
  or failed.
- The operator client snapshots normalized exact-match criteria with the case
  ID and version, aborts obsolete discovery requests, and renders only the
  current generation. Criteria remain in component memory, while selection is
  still revalidated against the authoritative owner projection.
- Confirmation panels snapshot the PII-free case identity, version, status,
  selected-record count, operation and action under review. Live case or
  permission drift hides stale confirmation immediately, and confirmed writes
  submit the reviewed version so the server rejects a later race instead of
  silently adopting newer state.
- Required-companion review failures distinguish an active business block
  (`409`), owner unavailability or an explicit transient retry (`503`), and an
  invalid internal contributor result (`500`). Only the explicit retry signal
  carries a short `Retry-After`; review is never retried automatically.
- Subject discovery and selection distinguish authoritative scope conflicts
  (`409`), missing compatible owners (`503` without `Retry-After`), explicit or
  exceptional owner retries (`503` with a bounded `Retry-After`), malformed
  operator coordinates (`400`), and invalid owner catalogs or results (`500`).
  Owner calls are never retried automatically and failure logs contain only the
  bounded owner key and exception type.
- Schema-v2 country packs define structural year/month/day response periods and
  allowlisted calculation time zones. Immutable v1 packs remain valid for
  ordinary processing but cannot authorize Guest Rights deadlines.
- Staff Rights deadlines remain intentionally unset until BunkFy has an
  explicit employment-jurisdiction source. Property policy is never borrowed
  for a tenant-scoped Staff case, and no BunkFy jurisdiction vocabulary belongs
  in GMA.

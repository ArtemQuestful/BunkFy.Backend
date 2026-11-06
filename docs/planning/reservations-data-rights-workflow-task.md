# Reservations Data Rights Workflow Task

Status: seventh implementation slice complete. Discovery, export, correction,
processing restriction, holds, destructive eligibility, irreversible
Reservations-owned redaction, DataRights dispatch, protected ledger, tombstone,
restore replay and the first authorized operator workflow are implemented.

## Outcome

Make Reservations a complete owner in the BunkFy DataRights workflow without
weakening booking, stay, provider-reconciliation or audit correctness.

An authorized controller operator must be able to discover a reservation,
select an exact version, export every Reservations-owned subject record,
correct or restrict permitted processing, and execute an approved irreversible
redaction when current policy and operational obligations allow it.

Archive, cancellation, unlinking a Guest Record and deleting a reminder are not
erasure. A reservation remains Reservations-owned even when it has a canonical
Guest link or originated in Ingestion.

## Boundary Decision

DataRights owns:

- case, verification, decision, approval and execution lifecycle;
- selected owner coordinates and operation revisions;
- PII-free work items, completion state and the authoritative ledger;
- protected artifact assembly and restore orchestration.

Reservations owns:

- booking identity/contact snapshots and notes;
- stay, source, lifecycle and allocation facts;
- active and historical Guest links;
- before/after details history;
- adapter operation receipts and reminder state;
- Reservations-specific correction, restriction, hold and redaction rules;
- immutable owner receipts, local tombstones and restore replay.

Guests owns durable Guest Records and their rights behavior. A Guest
anonymisation event prevents new links but does not silently rewrite a
historical reservation. Ingestion owns raw and normalized provider evidence.
Inventory owns physical allocation truth. Each owner receives an explicit
coordinate and produces its own receipt.

GMA remains unchanged. Contributor registration, Cqrs, tasks, messaging,
scoping, access control, recent-authentication policies, persistence retries,
health checks and runtime primitives already support this workflow. Reservation
rights vocabulary and disposition rules are BunkFy product concerns.

## Subject Coordinate And Discovery

The owner coordinate is:

- owner: `reservations`;
- record type: `reservation`;
- record id: the Reservations aggregate id;
- record version: the aggregate `Version`.

Discovery is property scoped and accepts exactly one strong coordinate:

- exact reservation id;
- normalized email; or
- normalized phone.

An optional normalized name may refine a strong coordinate. Name-only,
date-only, source-reference-only and fuzzy searches are rejected. Results are
bounded, deterministically ordered and expose only the reservation coordinate,
primary name and masked contact hints.

The current generic record-id lookup is owner-record specific. Bounded
cross-owner expansion from a selected Guest Record is a later DataRights
contract slice; it must be explicit rather than interpreting one id as both a
Guest and Reservation id.

Retired properties remain discoverable when their projection is known.
Unknown property projection, wrong tenant, disabled scope, malformed lookup or
unsupported coordinate fails closed.

Selection revalidates property ownership and exact aggregate version. It does
not use a candidate preview as authority.

## Catalogue-Driven Export

Reservations streams a versioned export fragment through
`IDataRightsSubjectExportContributor`. It never places export payloads in
tasks, messages, logs, metrics, traces, notifications or receipts.

One selected reservation can produce:

- one reservation record containing current booking, stay, source, allocation
  and lifecycle facts;
- one record per active or historical Guest link;
- one record per details-history revision;
- one record per data-rights correction receipt;
- one record per subject-linked adapter operation receipt;
- one record per arrival-reminder state.

Transport journals, projection checkpoints and rebuild copies are excluded.
Local property, Inventory and Guest projections are other owners' data or
rebuildable copies, not additional Reservations subject records.

Every exported member has a `data-rights-export` catalogue binding, approved
cross-module boundary, transient export-fragment retention and exact field id.
Composition fails when a DTO member lacks a binding, an unrelated field is
bound, an export policy differs, a record exceeds its bound, or the embedded
catalogue identity/version is wrong.

The contributor:

- validates tenant/property scope and known property projection;
- validates owner, record type, id and version;
- projects only the selected aggregate and independently linked records;
- orders child records by stable keys;
- streams records without loading all tenant data;
- returns `Stale`, `NotFound` or `ScopeUnavailable` without partial success.

The caller discards every partially written record unless the final result is
successful.

## Correction

Correction continues through Reservations aggregate commands; DataRights never
submits a generic patch or writes the Reservations schema.

An approved correction binds:

- tenant, property, case and approval revision;
- reservation id and selected aggregate version;
- a bounded semantic field set;
- expected details revision;
- actor, idempotency key and completion time.

The aggregate mutation, details-history entry, domain event, outbox event and
PII-free owner receipt commit atomically. Equivalent retries return the same
receipt; a changed key payload or stale revision fails closed.

Historical facts are superseded through normal history semantics. Correction
does not erase the previous snapshot.

## Processing Restriction

Reservations owns an explicit case-scoped restriction projection independent
from the existing Guest-link eligibility projection.

Restriction suppresses non-required:

- list, detail and history reads outside the rights workflow;
- guest-detail edits and canonical linking;
- adapter guest/detail amendments;
- reminders and operational notifications containing subject context;
- new processing derived from the reservation.

Policy-approved stay, safety, fraud, chargeback, statutory or controller
obligations may continue through narrowly named paths. Unknown restriction
state or a future contract version fails closed. The rights export and
controller review path remain available.

Restriction changes are idempotent, versioned and PII-free. DataRights records
the owner receipt; Reservations remains the authority that enforces it.

## Holds And Destructive Eligibility

Reservations owns independently releasable holds keyed by tenant, property and
reservation. A hold stores stable reason code, lifecycle actors/times and
optimistic version only; free-form legal advice is prohibited.

Irreversible redaction is blocked when:

- the reservation has an active or future operational stay;
- allocation or release work is pending or unresolved;
- a current policy or retention binding is unknown, stale or denies erasure;
- a Reservations hold is active;
- selected aggregate/details versions are stale;
- a provider reconciliation receipt still requires a direct reference;
- another destructive operation is unresolved.

Cancelled, no-show and checked-out records are not automatically eligible.
Current country and retention policy decides which minimum facts must remain.

## Irreversible Redaction

When approved, Reservations preserves only policy-approved non-identifying
booking facts needed for operational, financial and audit correctness.

The owner operation:

- replaces the primary guest name with a fixed non-identifying label;
- clears email, phone and free-text notes;
- removes or pseudonymizes the source reference according to current provider
  reconciliation policy;
- removes pending guest/contact/free-text amendment values;
- removes active and historical direct Guest links;
- redacts direct identity/contact/free text from every details-history
  snapshot while preserving revision and changed-field facts;
- reduces adapter receipts to approved reconciliation proof;
- suppresses pending reminders that no longer have an allowed purpose;
- writes an immutable owner receipt and local tombstone;
- emits one PII-free versioned event.

It does not fabricate dates, status, guest count, allocation facts or staff
audit attribution. Staff rights handle staff actor identity separately.

Ordinary reservation reads and writes exclude a terminally redacted record
unless a narrowly named policy-approved operational surface requires its
minimum facts.

## Ledger And Restore Safety

The generic DataRights anonymisation contributor is used with Reservations
coordinates only after eligibility and owner proof are implemented.

Reservations commits redaction, local tombstone, immutable receipt and outbox
event first. DataRights then durably flushes the protected external ledger delta
before appending its database ledger and completing the work item.

The Reservations restore contributor:

- accepts only verified external ledger proof;
- re-redacts a restored pre-operation aggregate and child records;
- verifies or recreates PII-free owner proof without fabricating the original
  receipt;
- writes an immutable restore receipt and advances the local tombstone;
- remains idempotent under crash/replay.

API, Admin API, Admin CLI and Worker readiness remain false until the existing
DataRights restore gate confirms the Reservations replay. Migrations remain
ungated.

## Efficiency

- Discovery uses tenant/property-first indexes and bounded paging.
- Export projects one selected reservation and streams child rows in stable
  order.
- Restriction, hold, receipt and tombstone indexes start with tenant/property
  and reservation coordinates.
- One work item mutates one reservation and one operation revision.
- Events, tasks, receipts and ledger records are constant-size and PII-free.
- No workflow scans a tenant, hashes every reservation, or copies another
  module's tables.

## Delivery Slices

1. [Complete] Add scoped discovery, exact selection revalidation and
   catalogue-driven export for current and historical Reservations-owned data.
2. [Complete] Add approved transactional correction and immutable owner
   receipts through existing aggregate/history semantics.
3. [Complete] Add Reservations-owned processing restriction, enforcement
   and rebuildable PII-free state; register the Reservations restriction owner
   with bounded exact release-target discovery, actor-bound proof replay, and
   exactly-one legacy unbound recovery.
4. [Complete] Add holds and fail-closed destructive eligibility.
5. [Complete] Add irreversible aggregate/history/link/receipt redaction, owner
   proof and terminal ordinary-surface enforcement.
6. [Complete] Add DataRights work-item dispatch, protected ledger completion,
   Reservations tombstone and pre-ready restore replay.
7. Add operator API/UI flows, migration/architecture/Docker/browser evidence
   and exact-commit publication gates.

Only one numbered slice is implemented at a time. Existing modules may be
touched through contracts, projections and tests, but Reservations remains the
only owner of its data.

## Seventh-Slice Design

- The existing DataRights case API remains the only product orchestration
  surface. The UI does not call a direct Reservations erasure endpoint or
  reconstruct approval, work-item or ledger state in the browser.
- Sensitive discovery accepts an optional exact owner key. When supplied,
  DataRights resolves exactly one registered contributor before executing the
  bounded lookup. Unknown, duplicate or unavailable owners fail closed. The
  Reservations operator flow always supplies `reservations`, preventing Guest
  candidates from consuming the bounded result set or exposing unrelated
  subject data.
- The property-scoped management page is named `Privacy requests`. Its first
  complete workflow supports one separately approved `Anonymisation`
  operation against one exact Reservations coordinate. Access export,
  correction, restriction, multi-owner cases and tenant termination remain
  outside this UI slice even though their backend foundations may already
  exist.
- Case lists contain only the existing PII-minimal case DTO and use status
  filtering plus bounded pagination. Discovery responses are transient
  component state: they are not written to URLs, browser storage, query cache,
  notifications, logs or analytics, and are cleared when the case, property or
  modal changes.
- The case detail renders server-owned lifecycle state and exposes only the
  next valid actions: requester verification and controller routing,
  discovery, exact selection, review, decision, and execution. Each action is
  independently permission gated at the existing property or tenant scope.
  Optimistic versions come only from the latest server response.
- Approval and irreversible execution remain separate duties. The UI explains
  that the approver cannot execute the same case, while the existing domain
  invariant remains authoritative. The destructive confirmation generates one
  in-memory idempotency key that is retained for exact retries and discarded
  after success, case close or coordinate change.
- Execution still requires tenant-scoped `data-rights.erase`,
  property-scoped `data-rights.read`, and the host-configured recent
  authentication assurance. Browser controls never weaken or emulate these
  server checks.
- Executing work is refreshed only while the case or work item is non-terminal,
  using the existing bounded foreground polling intervals. Completion
  invalidates case data plus the affected Reservations list, detail, history
  and dependent Guest-stay views; ordinary reservation surfaces must no longer
  reveal the redacted booking.
- DataRights and Reservations keep all current persistence ownership. No new
  database table is required for this slice. Existing additive migrations and
  restore replay are exercised from the operator path through real PostgreSQL,
  NATS and Worker composition.
- Admin API and Admin CLI do not gain personal-data discovery or destructive
  shortcuts in this slice. A later remote-operations surface must call the same
  application workflow and preserve the same assurance and separation rules.
  GMA remains unchanged.

## Seventh-Slice Acceptance Plan

- Application tests prove exact owner-filter normalization, unknown-owner
  denial, duplicate-owner denial, unfiltered compatibility, bounded ordering
  and no invocation of unrelated contributors.
- API metadata and integration tests prove sensitive no-store responses,
  property-scoped lifecycle permissions, tenant-scoped erase permission,
  recent-authentication assurance and stable error mapping.
- Web tests cover lifecycle action derivation, status and decision labels,
  terminal polling decisions, transient discovery handling, exact idempotency
  reuse and permission-aware navigation. Generated OpenAPI contracts remain
  drift free.
- A real PostgreSQL, NATS and Worker operator-path test creates, verifies,
  routes, discovers, selects, reviews, separately approves and executes one
  Reservation anonymisation, then proves ordinary surfaces exclude it while
  case, work item, ledger, owner receipt and tombstone evidence agree.
- Browser evidence uses separate authorized approver and executor accounts. It
  covers desktop and mobile layout, masked discovery, stale-version recovery,
  denied same-actor execution, recent-authentication denial, successful
  execution, live terminal refresh and disappearance from ordinary reservation
  views.
- Full architecture, migration-drift, non-Docker, Docker, generated-contract,
  dependency, security and exact-commit backend/web/root publication gates must
  pass before slice completion.

## Sixth-Slice Design

- DataRights keeps ownership of case execution, one-record work items,
  contributor dispatch, protected external ledger durability, bounded restore
  pages and the host readiness gate. Reservations registers one versioned
  anonymisation contributor and one versioned restore contributor through the
  existing contracts; it does not read DataRights persistence.
- The owner contributor resolves the current Reservations details revision for
  the exact approved aggregate version, maps generic routing-policy evidence to
  the existing owner command and accepts only a canonical Reservations receipt
  bound to the same case, operation, record and idempotency coordinates. A
  concurrent owner change advances the aggregate version and fails closed
  during the command's locked eligibility re-evaluation.
- The protected processing ledger carries the resulting owner record version
  already proven by the DataRights work item. New entries use ledger contract
  version 2 and bind that value into the canonical chain digest. Version 1
  entries and deltas remain readable with their original digest algorithm;
  their missing result-version evidence is never invented.
- Reservations commits its local tombstone with the original redaction receipt
  and aggregate mutation. The tombstone is keyed by tenant and reservation,
  stores property, original owner receipt digest, authoritative resulting
  versions and a monotonic replay revision, and deliberately has no foreign key
  to the reservation aggregate so a backup predating record creation can still
  retain deletion proof without fabricated booking facts.
- Restore replay accepts only the verified DataRights ledger envelope and runs
  behind the existing per-reservation operation lock. If a restored aggregate
  exists, Reservations re-scrubs current, pending, history, Guest-link,
  adapter-fingerprint and reminder state regardless of the older operational
  status while preserving factual stay, allocation and staff audit fields.
  If it is absent, replay writes only the tombstone and restore receipt.
- A fully matching terminal aggregate is not mutated again. An existing
  original receipt is verified when present but is never recreated from its
  digest. Every replay writes or reuses one immutable restore receipt binding
  the ledger entry, original owner receipt proof, resulting owner state and
  tombstone revision.
- Exact restore receipt replay verifies the complete current owner state. A
  changed ledger coordinate, receipt digest, tombstone, aggregate state or
  owned child store fails closed. The DataRights checkpoint advances only after
  this proof succeeds; API, Admin API, Admin CLI and Worker readiness therefore
  remain false after an unsafe restore.
- DataRights and Reservations executable personal-data catalogues include the
  new protected proof, internal commands, tombstone and restore receipt.
  Operator-facing erasure endpoints remain slice 7. GMA remains unchanged.

## Sixth-Slice Acceptance Plan

- DataRights domain and persistence tests prove contract-2 result-version
  binding, canonical chain integrity, version-1 read compatibility, exact
  restore-proof comparison and migration preservation of existing ledger rows.
- Contributor tests prove exact owner resolution, routing-policy mapping,
  blocked/failure classification, deadline handling and rejection of mismatched
  receipts.
- Reservations domain and handler tests cover original tombstone creation,
  active/terminal restored-row re-scrubbing, already-redacted replay, absent-row
  tombstone-only replay, immutable restore receipts and every mismatch path.
- Persistence tests cover append-only proof stores, tenant-first uniqueness,
  operation locking, full child-store re-scrubbing and exact idempotent replay.
- PostgreSQL migration and Docker restore drills execute anonymisation through
  the real DataRights worker, restore a pre-operation database, prove hosts stay
  unready until Reservations replay completes, then verify ledger, tombstone,
  owner state, restore receipt and checkpoint evidence.
- Full architecture, migration-drift, non-Docker, Docker, generated-contract,
  vulnerability, security and exact-commit publication gates must pass before
  slice completion.

## Fifth-Slice Design

- Reservations exposes one internal transactional owner command. It is not a
  public erasure endpoint and is not registered as a generic DataRights
  anonymisation contributor until slice 6.
- The command requires an exact approved anonymisation coordinate, selected
  reservation/details versions, current routing-policy evidence, one
  idempotency key and an executing actor. It acquires the existing
  per-reservation operation lock and re-runs destructive eligibility before
  loading or mutating owner data.
- One transaction replaces the current primary guest name with the fixed
  `Anonymised guest` label; clears contact, notes, search copies and direct
  source reference; clears pending identity/contact/free-text values; removes
  every current and historical Reservations-owned Guest link; redacts every
  details-history snapshot; reduces subject-linked adapter fingerprints to a
  fixed non-correlating marker; and supersedes pending arrival reminders.
- Dates, expected stay times, status, guest count, requested units, allocation
  and release facts, provider name, lifecycle times and staff audit attribution
  remain unchanged. The redaction state is orthogonal to operational
  reservation status.
- Details revision and aggregate version each advance once. The redaction
  history revision contains only the already-redacted snapshot plus changed
  field facts; no before-image containing subject values is written to domain
  events, outbox messages or receipts.
- The owner command appends one canonical, immutable receipt containing exact
  case, approval, operation, selected/resulting versions, policy digests,
  affected-row counts, actor, event and completion evidence. Subject export
  exposes only its minimum PII-free accountability projection.
- One versioned integration event contains tenant, property, reservation and
  resulting versions only. It never contains guest values, Guest ids, actor,
  idempotency material or policy payloads.
- Ordinary reservation list, detail, history, source lookup, write, reminder
  and adapter surfaces exclude terminally redacted reservations. Explicit
  DataRights and required-continuation paths remain separately named; a
  redacted terminal record has no admitted operational continuation.
- Exact idempotency replay verifies the canonical receipt, aggregate terminal
  state and affected owner stores. Changed coordinates or missing proof fail
  closed.
- The operation lock prevents concurrent correction, restriction, hold or
  redaction mutations. Because the owner mutation is one database transaction,
  an intermediate owner state is never committed. The reserved
  `DestructiveOperationInProgress` blocker becomes meaningful in slice 6 when
  a durable DataRights work item can be unresolved; slice 5 does not add a
  two-phase owner execution row that could strand PII after a crash.
- The local tombstone, protected ledger, generic contributor and restore replay
  remain slice 6. GMA remains unchanged.

## Fifth-Slice Acceptance Plan

- Domain tests cover exact-version redaction, terminal-only transitions,
  fixed-label/contact/source cleanup, pending-value cleanup, Guest-link
  removal, one-time version advancement, PII-free event payload and terminal
  replay proof.
- Handler tests cover tenant scope, approval and routing-policy binding,
  operation locking, eligibility re-evaluation, exact idempotency replay,
  changed-request conflict and atomic owner receipt creation.
- Persistence tests cover history snapshot rewriting and hashes, adapter
  fingerprint reduction, reminder suppression, Guest-link deletion, canonical
  receipt uniqueness and append-only enforcement.
- Ordinary-surface tests prove terminally redacted records cannot be listed,
  read, searched, changed, linked, reminded or amended through adapters while
  DataRights export and exact owner proof remain available.
- Export and catalogue tests prove the redacted aggregate, child records and
  minimum owner receipt are complete without actor, idempotency, Guest ids or
  removed subject values.
- PostgreSQL integration proves the real approval gate, shared operation lock,
  atomic aggregate/history/link/adapter/reminder/receipt/outbox persistence,
  replay, tenant/property isolation and upgrade from the preceding migration.
- Full architecture, migration-drift, non-Docker, Docker, generated-contract,
  vulnerability, security and exact-commit publication gates must pass before
  slice completion.

## Fourth-Slice Design

- Reservations owns independently releasable, tenant/property/reservation
  holds. Each hold stores one stable reason code, lifecycle actor/timestamps,
  state and optimistic version; free-form legal or investigation text is
  rejected by construction.
- Hold placement and release use exact reservation/details/hold versions,
  idempotency receipts and one reservation operation lock. Multiple active
  holds compose by existence; releasing one hold cannot release another.
- Hold receipts are append-only and PII-free. Actors remain on the
  Reservations-owned hold lifecycle for authorized operational audit, but are
  excluded from receipts, events and reservation-subject export records.
- Reservations exposes a versioned owner-local anonymisation eligibility
  contract. The evaluator reads only the Reservations aggregate, hold store,
  property/policy projection and processing-restriction projection.
- Eligibility requires exact selected aggregate and details revisions, a known
  current restriction projection contract, a terminal operational state, no
  pending allocation/amendment/release work and no active hold.
- `PendingAllocation`, `CancellationPending`, `NoShowPending`,
  `CheckoutPending` or a pending allocation amendment report a stable
  unresolved-work blocker. `Confirmed` and `CheckedIn` report an active-stay
  blocker. Only `AllocationRejected`, `Cancelled`, `NoShow` and `CheckedOut`
  can continue to policy evaluation.
- The current property policy must be known, processing-enabled, current and
  permit `data-rights-anonymisation` on the erasure surface. Retired known
  properties remain eligible for rights handling when that policy remains
  enabled; ordinary property management remains unchanged.
- The routing approval evidence must match the current property projection
  version, country policy, retention policy, content digest, purpose, surface,
  provenance and evaluation window. Missing, stale, future or changed evidence
  fails closed with a stable blocker code.
- External reservations with a direct source reference remain blocked in this
  slice. No provider-specific reconciliation-retention contract exists yet, so
  assuming that the reference may be removed would fail open. Direct bookings
  and external records without a direct reference can proceed when every other
  check passes.
- The future destructive command will acquire the same reservation operation
  lock before re-evaluating eligibility. The reserved
  `DestructiveOperationInProgress` blocker is not synthesized from an
  uncommitted database lock; it becomes observable only when durable
  DataRights work-item execution exists in slice 6.
- Rights export adds subject-linked hold lifecycle and minimum hold receipts.
  Staff actors, idempotency keys and legal advice are excluded. GMA remains
  unchanged.

## Fourth-Slice Acceptance Plan

- Domain tests cover coded hold placement/release, overlapping holds, actor and
  time validation, optimistic conflicts and terminal release behavior.
- Handler tests cover tenant scope, exact reservation/details/hold versions,
  exact idempotency replay, changed-request conflict and independent release.
- Eligibility tests cover every reservation state, pending amendment, missing
  and future restriction state, active holds, stale aggregate/details
  revisions, retired known property policy, stale/changed routing evidence and
  provider-reference denial.
- Persistence tests cover scoped indexes, hold concurrency, receipt uniqueness,
  append-only enforcement, operation-lock registration and bounded eligibility
  queries.
- Export and catalogue tests prove hold records are complete for the selected
  reservation while actors, idempotency material and unrelated holds remain
  absent.
- PostgreSQL integration proves real country-policy evaluation, transaction
  locking, hold placement/release, exact replay, eligibility transitions and
  tenant/property isolation.
- Full architecture, migration-drift, non-Docker, Docker, generated-contract,
  vulnerability, security and exact-commit publication gates must pass before
  slice completion.

## Third-Slice Design

- DataRights remains the case and approval authority. Reservations accepts only
  exact `Restriction` approvals bound to tenant, property, case revision,
  reservation id, selected record version and `Apply` or `Release`.
- Reservations owns case-scoped restriction aggregates, one effective
  per-reservation projection and immutable transition receipts. Multiple active
  cases compose through an active-count projection; releasing one case cannot
  release another.
- Missing state, unsupported contract versions, stale record/restriction/state
  revisions and reused approval coordinates fail closed.
- Ordinary list, detail and history reads exclude restricted reservations.
  Ordinary edits, Guest links, reassignments, stay commands and adapter
  guest/detail amendments use the same fail-closed admission state.
- Already-started allocation and cancellation outcomes use separately named
  required-continuation repository paths so restriction cannot strand
  distributed work. No new adapter amendment is admitted after restriction.
- Applying the first effective restriction supersedes pending arrival
  reminders in the same unit of work. Reminder creation, refresh and dispatch
  require current unrestricted state, preventing later regeneration or races.
- New reservations initialize unrestricted state atomically. A one-shot,
  scope-aware projection rebuild derives exact active count and transition
  revision from reservation and restriction source rows; the projection is not
  its own rebuild source.
- Apply/release mutation, effective-state transition, reminder suppression,
  immutable receipt and PII-free outbox event commit atomically. Exact
  idempotency retries return the existing receipt; changed coordinates fail
  closed.
- Actor identity remains only in the restriction aggregate's internal audit
  lifecycle. Receipts, integration events, tasks and rebuild snapshots contain
  stable coordinates and state only, never reservation guest values or actor
  identity.
- The existing rights export remains available and adds subject-linked
  restriction lifecycle, effective state and minimum transition-accountability
  receipts. GMA remains unchanged.

## Third-Slice Acceptance Plan

- Unit tests cover apply/release lifecycle, overlapping cases, stale revisions,
  approval reuse, exact replay, unsupported/missing state, append-only receipts
  and PII-free event contracts.
- Query and command tests prove ordinary surfaces deny while DataRights export
  and exact owner commands remain available.
- Reminder tests prove restriction supersedes pending reminders and prevents
  refresh/dispatch even when a stale reminder row exists.
- Rebuild tests prove zero, one, overlapping and released restrictions produce
  deterministic state without scanning another module.
- PostgreSQL integration proves the real approval gate, retired-property policy
  exception, atomic state/receipt/outbox/reminder persistence, ordinary-surface
  denial, required continuation, replay and release.
- Full architecture, migration-drift, non-Docker, Docker, generated-contract,
  security and publication gates must pass before slice completion.

## Completed First Slice

- Reservations registers `reservations/reservation` discovery and export
  contributors through the existing DataRights Contracts protocol.
- Exact reservation id, normalized email or normalized phone lookup is
  property scoped, bounded and indexed. Optional exact name refinement is
  supported; name-only, multiple strong coordinates and unknown scope deny.
- Current and pending adapter-amendment name/contact search copies are
  synchronized by the aggregate. Migration
  `20260725172015_AddReservationDataRightsDiscovery` backfills existing rows
  before enforcing the required current-name search value.
- Selection revalidates property ownership and exact aggregate `Version`.
- Export schema `reservations.subject-export` version 1 streams the current
  reservation, pending amendment, Guest-link rows, details-history revisions,
  subject-linked adapter receipts and reminders in stable order.
- Staff actor fields remain outside this subject fragment. Partial sink writes
  cannot return a successful result.
- Personal-data catalogue version 3 binds every exported member and derived
  search copy. Composition fails when code, policy, boundary, retention or
  catalogue identity drifts.
- Focused unit tests cover current/pending lookup, masking, bounds,
  tenant/property isolation, retired/unknown properties, stale selection,
  complete child export, unrelated-row exclusion and sink failure.
- A PostgreSQL container test proves migration, contributor registration,
  provider query translation and export streaming against the real provider.
- GMA is unchanged.

## First-Slice Acceptance Evidence

- Two-tenant/two-property tests prove discovery, selection and export isolation.
- Name-only and malformed multi-coordinate lookup fail closed.
- Exact email/phone lookup is normalized, bounded and deterministically
  ordered with masked hints.
- Retired known properties remain accessible; unknown properties deny.
- A stale aggregate coordinate cannot export.
- Export includes current aggregate, every Guest-link row, every details
  history revision, subject-linked adapter receipt and reminder exactly once.
- No unrelated reservation, projection copy, transport row or another
  property's record is exported.
- Every export DTO member is catalogue-bound and the generated inventory is
  deterministic.
- Contributor payloads remain absent from messaging, tasks, notifications,
  logs, metrics, traces and support bundles.
- Architecture tests preserve contracts-only module dependencies and GMA
  remains unchanged.

## Completed Second Slice

- Reservations exposes one transactional correction command for an exact
  approved DataRights case coordinate. DataRights remains the approval
  authority; no generic patch or premature public HTTP endpoint was added.
- The aggregate requires both the selected record `Version` and current
  `DetailsRevision`, rejects pending allocation amendments and records a full
  superseding details snapshot with `DataRightsCorrection` provenance.
- The reservation mutation, details-history row, PII-free immutable owner
  receipt and PII-free outbox event share the Reservations unit of work.
- Receipt uniqueness is tenant scoped across idempotency, details event and
  owner event coordinates. Database checks enforce contract, revision and
  changed-field-mask invariants; application code prevents receipt update or
  deletion.
- Exact retries return the committed receipt. Reusing the key with a different
  coordinate, revision or normalized correction payload fails closed. One
  bounded persistence retry resolves optimistic-concurrency and unique-key
  races through the generic GMA exception classifier.
- Known retired properties remain correctable only while their processing
  policy and governance binding remain current and enabled. Ordinary
  reservation management still requires an active property.
- Reservations export now includes minimum correction-accountability receipts
  without idempotency keys, correlation ids, actors or corrected values.
- Personal-data catalogue version 4 binds correction inputs, owner receipt,
  retention and rights behavior. Generated inventory and schema composition
  tests are current.
- Migration
  `20260725190654_AddReservationDataRightsCorrectionReceipts` creates the
  append-only receipt store and scoped indexes.
- Unit coverage proves aggregate revision semantics, approval binding,
  fail-closed replay, PII-free outbox projection, retry behavior, model
  constraints and append-only enforcement.
- A PostgreSQL container test proves the real DataRights approval gate,
  retired-property policy exception, GMA command pipeline, transactional
  persistence, exact replay, conflict rejection and PII-free owner event.
- GMA remains unchanged.

## Second-Slice Acceptance Evidence

- A correction cannot run without an exact approved case, selected reservation
  version, expected details revision and executing actor.
- No-change, stale, wrong-scope, changed-idempotency-payload and missing-policy
  requests fail without a committed receipt.
- One successful correction advances record and details revisions exactly once
  and creates exactly one details-history row, receipt and owner outbox event.
- Replay does not mutate the aggregate or duplicate history, receipt or outbox
  state.
- Receipt and owner event payloads contain coordinates and changed field ids,
  never corrected name, contact details, notes, actor or idempotency material.
- Ordinary management remains denied after property retirement while the
  narrowly named, policy-bound correction path remains available.
- GMA transaction, CQRS, scoping and EF classification primitives are reused
  unchanged; Reservations-specific policy and receipt semantics remain local.

## Completed Third Slice

- Reservations now accepts exact approved DataRights `Restriction` apply and
  release coordinates without owning case workflow or exposing a premature
  public operator endpoint.
- Case-scoped restriction aggregates retain internal actor evidence. A
  current-contract per-reservation projection composes overlapping cases by
  active count, and append-only transition receipts plus outbox events omit
  actors and reservation guest values.
- Ordinary list, detail, history, edit, Guest-link, reassignment, stay and new
  adapter-detail paths require a current unrestricted projection. Missing or
  future projection contracts fail closed.
- Explicitly named DataRights and required-continuation repository paths remain
  available while restricted so rights work and already-started allocation or
  cancellation outcomes are not stranded.
- The first effective restriction supersedes pending arrival reminders in the
  same unit of work. Reminder scheduling, refresh and claim paths independently
  require current unrestricted state.
- New reservations initialize unrestricted state through the repository.
  `RebuildReservationProcessingRestrictionsPayload` reconstructs projection
  revision, active count and last transition from Reservations-owned source
  rows in bounded ordinal batches.
- Rights export includes restriction lifecycle, effective state and minimum
  transition receipts. Personal-data catalogue version 5 and its generated
  inventory bind all new command, event, persistence and export members.
- Migration
  `20260725210703_AddReservationProcessingRestrictions` creates the aggregate,
  projection and append-only receipt stores and backfills every existing
  reservation with current-contract unrestricted state.
- GMA remains unchanged.

## Third-Slice Acceptance Evidence

- The complete Reservations suite passes 95 tests covering overlapping
  apply/release lifecycle, exact approval and replay, stale/missing/future
  state, approval reuse, append-only receipts, PII-free outbox/export payloads,
  reminder suppression, query gates and deterministic rebuilds.
- PostgreSQL migration coverage upgrades from the preceding Reservations
  migration and verifies one generated-ordinal unrestricted state row for an
  existing reservation.
- A PostgreSQL and NATS command-pipeline test proves retired-property apply and
  release, real DataRights approval, atomic projection/receipt/outbox/reminder
  persistence, ordinary denial, DataRights and required-continuation access,
  exact replay and post-release visibility.
- `eng/verify.ps1 -SkipRestore` passes solution synchronization,
  source-package ownership, a zero-warning full build, every GMA and BunkFy
  migration-drift check, architecture tests and all non-Docker suites.
- `eng/test-docker.ps1 -NoBuild` passes all 46 Docker integration tests. The
  strengthened required-continuation assertion also passes in a focused rerun.
- Recursive submodule status confirms every mounted GMA repository is clean;
  no GMA source or pointer changes are part of this slice.

## Fourth-Slice Acceptance Evidence

- Reservations owns coded, independently releasable holds, append-only
  PII-free lifecycle receipts and a shared per-reservation operation lock.
  Exact reservation, details and hold versions protect every transition;
  unchanged retries replay and changed payloads conflict.
- Destructive eligibility fails closed for active holds, non-terminal or
  pending reservation work, missing/current-policy mismatches and external
  provider records without a direct source reference. Terminal records at
  known retired properties can proceed when current policy evidence allows it.
- Rights export schema version 2 includes subject-linked hold lifecycle and
  minimum receipts without staff actors or idempotency keys. Personal-data
  catalogue version 6 and its generated inventory bind the new contracts,
  commands, persistence, policy evidence, eligibility result and export data.
- Migration `20260725224526_AddReservationDataHoldsAndEligibility` preserves
  existing reservations and creates empty hold, receipt and operation-lock
  stores with database lifecycle and append-only constraints. Migration drift
  is clean.
- The complete Reservations suite passes 111 tests. Six Reservations Docker
  integration tests pass, including upgrade from the preceding migration and
  a real PostgreSQL/NATS hold, replay, eligibility and release workflow.
- `eng/verify.ps1 -SkipRestore` passes solution synchronization,
  source-package ownership, a zero-warning full build, every GMA and BunkFy
  migration-drift check, 64 architecture tests and all non-Docker suites.
- `eng/test-docker.ps1 -NoBuild` passes all 48 Docker integration tests.
  Recursive submodule status confirms no GMA source or pointer change belongs
  to this slice.

## Fifth-Slice Acceptance Evidence

- Reservations owns one internal, transactionally atomic anonymisation command
  bound to the exact approval, operation coordinate, selected versions, current
  routing policy and shared per-reservation operation lock. Terminal state,
  immutable owner proof and exact replay are enforced independently of the
  DataRights orchestration that follows in slice 6.
- Aggregate, pending details, current and historical Guest links, history
  snapshots and hashes, source reference, adapter fingerprints and reminders
  are scrubbed or suppressed together. Ordinary reservation, history, source,
  reminder and adapter surfaces exclude redacted records; the explicitly named
  DataRights export and owner-proof paths remain available.
- Rights export schema version 3 includes the minimum PII-free anonymisation
  receipt without actor, idempotency, Guest ids or removed subject values.
  Personal-data catalogue version 7 and its generated inventory bind the new
  state, command, receipt, repository and event surfaces.
- Migration `20260726002653_AddReservationAnonymisationOwnerProof` preserves
  existing reservations as non-anonymised, creates an empty append-only receipt
  ledger and adds database state, uniqueness and lifecycle constraints.
  Historical upgrade tests seed their declared old schema directly, avoiding
  dependence on the current EF aggregate model.
- The complete Reservations suite passes 119 tests. Focused real-PostgreSQL
  migration and owner-workflow tests pass, including approval, locking, atomic
  scrub, receipt, PII-free outbox, ordinary-surface exclusion and exact replay.
- `eng/verify.ps1 -SkipRestore` passes solution synchronization,
  source-package ownership, a zero-warning full build, every GMA and BunkFy
  migration-drift check, 64 architecture tests and all non-Docker suites.
- `eng/test-docker.ps1 -NoBuild` passes all 50 Docker integration tests.
  GMA remains unchanged; recursive source and pointer cleanliness is checked
  before publication.

## Completed Sixth Slice

- Reservations registers exact `reservations/reservation` anonymisation and
  restore contributors through the existing DataRights contracts. Generic
  work items resolve the current details revision, re-run Reservations-owned
  policy and eligibility checks under the operation lock, and accept only the
  canonical owner receipt bound to the approved coordinate.
- DataRights processing-ledger contract version 2 binds the resulting owner
  record version into its canonical chain digest. Existing version-1 entries
  retain their original digest algorithm and remain restorable without
  inventing the missing result-version field.
- The original Reservations redaction transaction now writes an immutable
  local tombstone with the owner receipt. The tombstone deliberately has no
  operational Reservation foreign key and can therefore preserve proof when a
  restored backup predates creation of that booking.
- Restore replay accepts only the verified protected-ledger envelope. It
  re-scrubs a restored aggregate, pending amendment, history, Guest links,
  adapter operations and reminders, or writes proof only when the operational
  row is absent. It never recreates an original owner receipt.
- One append-only restore receipt binds the full generic ledger coordinate,
  original owner proof, resulting Reservations versions and tombstone
  revision. Exact replay is idempotent; changed ledger, owner, tombstone,
  aggregate or child-store state fails closed before the checkpoint advances.
- DataRights and Reservations personal-data catalogues and generated
  inventories classify the new internal commands, protected proof, tombstone
  and restore receipt. GMA remains unchanged.

## Sixth-Slice Acceptance Evidence

- DataRights, Guests and Reservations suites pass 116, 82 and 130 tests. They
  cover contract-2 canonical binding, captured contract-1 compatibility,
  exact contributor mapping, tombstone creation, restored-row and absent-row
  replay, immutable receipts, child-store re-scrubbing and mismatch paths.
- Migration
  `20260726022029_AddProcessingLedgerResultVersion` additively extends the
  generic ledger while preserving existing rows. Migration
  `20260726024630_AddReservationAnonymisationRestoreProof` creates the
  tombstone and restore-proof stores and backfills every existing owner
  receipt into an exact tombstone.
- A previous-schema PostgreSQL upgrade test seeds a real Reservations owner
  receipt, applies the current migration and verifies the exact tombstone
  digest without fabricating a restore receipt.
- A PostgreSQL and NATS worker drill executes the generic Reservations work
  item, records contract-2 ledger and owner proof, restores a pre-operation
  database, re-scrubs the restored booking and keeps readiness gated until the
  restore receipt, tombstone and checkpoint agree.
- `eng/verify.ps1 -SkipRestore` passes solution synchronization,
  source-package ownership, a zero-warning full build, every GMA and BunkFy
  migration-drift check, 64 architecture tests and all non-Docker suites.
- `eng/test-docker.ps1 -NoBuild` passes all 52 Docker integration tests.
  Recursive submodule status confirms no GMA source or pointer change belongs
  to this slice.

## Completed Seventh Slice

- DataRights discovery accepts an optional normalized exact owner key and
  resolves one contributor before running the bounded query. Unknown,
  duplicate, blank and oversized owner filters fail closed without invoking
  unrelated contributors; the existing unfiltered contract remains compatible.
- Every operator endpoint publishes its success response metadata, keeping the
  generated web OpenAPI contract complete for list, detail, discovery,
  selection and execution workflows.
- The property-scoped `Privacy requests` surface implements an
  Anonymisation-only queue and detail workflow with permission-aware
  navigation, bounded pagination, transient reservation matching, server-owned
  lifecycle actions, split approval and execution, exact retry idempotency and
  terminal query invalidation.
- Preview composition uses three distinct generated 32-byte keys and a
  protected local ledger volume shared by API, Worker and management hosts.
  This is explicitly a Preview-only topology; Production still rejects the
  local provider and requires a separately registered production-grade store.
- An unavailable external ledger provider now resolves to a fail-closed store
  with a stable readiness code instead of failing with an opaque dependency
  injection error. Runtime images create the protected ledger mount point with
  the correct application ownership.
- No database model changed and no migration was required. DataRights and
  Reservations retain their existing persistence and orchestration ownership;
  GMA remains unchanged.

## Seventh-Slice Acceptance Evidence

- The complete DataRights suite passes 122 tests, including owner-filter
  normalization, unknown and duplicate owner denial, unrelated-contributor
  isolation, OpenAPI success metadata and stable missing-provider readiness.
- Web contract drift checks pass. TypeScript, lint, all 114 web tests and the
  production bundle pass with the privacy workflow emitted as its own lazy
  route chunk.
- A live preview walkthrough with an authorized operator covers request
  creation, requester verification, controller routing, exact
  Reservations-only matching, a no-match result, guarded cancellation and
  clean desktop and mobile layouts. Browser errors and warnings remain empty;
  no reservation is destructively changed by this drill.
- `eng/verify.ps1 -SkipRestore` passes solution synchronization,
  source-package ownership, a zero-warning full build, every GMA and BunkFy
  migration-drift check, 64 architecture tests and all non-Docker suites.
- `eng/test-docker.ps1 -NoBuild` passes all 52 Docker integration tests. The
  existing real PostgreSQL, NATS and Worker anonymisation drill proves
  separately authorized execution, ordinary-surface exclusion, protected
  ledger and owner proof, restored-backup re-scrubbing and checkpoint
  readiness.
- Preview PowerShell scripts parse, Compose config validates, the protected
  ledger volume exists, and API, Worker, web, PostgreSQL, NATS, Redis and MinIO
  are healthy. Recursive status confirms no GMA source or pointer change
  belongs to this slice.

## Non-Goals

- Guest-profile, Ingestion-source, Inventory, Staff or Workspaces erasure.
- Automatic SP-003 retention scheduling.
- Payment, tax, chargeback or identity-document semantics not yet modeled.
- Public guest self-service or requester verification.
- Private counsel decisions, immutable object-store implementation or hosted
  deployment procedures.

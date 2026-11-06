# Guests

The module's versioned personal-data policy is defined in
[`personal-data-catalog.v1.json`](personal-data-catalog.v1.json). The generated
[`personal-data-inventory.v1.md`](personal-data-inventory.v1.md) is checked by
reflection-backed tests against persistence, commands, queries, responses,
projection exports, and integration events.

Guests owns BunkFy's tenant-wide canonical guest profiles and staff-facing stay history. Guest records never imply authentication, membership, or guest-facing access.

## Current Slice

- bounded profile/contact fields with actor provenance, archive lifecycle, optimistic concurrency, and duplicate-contact allowance;
- a bounded optional language collection with legacy scalar compatibility,
  identity-based replay, complete rights exports and anonymisation; see the
  [language contract and deployment gates](../../../../docs/planning/guest-multilanguage-contract-task.md).
- named canonical-profile database constraints for non-empty coordinates,
  positive projection ordinals, paired retained/search values, lifecycle
  versions and chronology, and complete anonymisation scrubbing. The additive
  `AddGuestProfileAuthoritativeStateIntegrity` migration rejects malformed
  legacy rows instead of rewriting Guest facts;
- property-scoped create, search, read, update, archive, and stay-history surfaces across public management API, Admin API, and Admin CLI;
- PII-minimal create, update, and archive receipts shared by every management
  front door; sensitive profile data remains behind the explicit detail read;
- retry-safe Guest creation across the public API, Admin API, Admin CLI, and
  web workflows, using the caller operation id as the Guest id and the existing
  tenant-scoped mutation coordinate without a retained request journal;
- retry-safe profile update and archive across every management front door,
  backed by a Guests-owned `(tenant, Guest, operation)` journal, canonical
  request fingerprints, original receipts, and one profile/outbox transaction;
- an optional owner-issued creation confirmation id with tenant-scoped
  uniqueness, exact replay matching, persistence, portability, and additive
  propagation on the PII-minimal Guest-created event;
- explicit success schemas and fail-safe `no-store` headers on every public and
  Admin Guest HTTP response;
- minimized directory rows projected directly from persistence with one-row
  look-ahead pagination, plus independently bounded and deterministically
  ordered stay history across every read front door;
- visibility through the profile's origin property or any current or historical
  stay association at the property;
- an in-process DataRights discovery contributor that accepts exactly one
  strong coordinate, returns bounded masked candidates, and revalidates opaque
  coordinates without exposing Guests persistence;
- a catalogue-versioned DataRights export contributor that revalidates the
  selected profile version and streams one profile plus only stay history for
  the authorized property;
- fail-closed embedded-catalogue validation at composition time, explicit
  one-hour transient export bindings, bounded field values and no staff audit
  attribution, normalized search copies or unrelated-property stays;
- executable catalogue coverage for both public and Admin API profile inputs,
  plus the bounded mutation receipt;
- normalization-aware profile update outcomes containing only changed field
  semantics, previous/current versions, event id and time, ready for later
  PII-free correction receipts without a second mutation path;
- approved DataRights correction execution with exact Guest-version binding,
  idempotent immutable receipts and no second profile mutation path;
- provider-enforced correction-receipt integrity: stable case and resulting
  Guest-version uniqueness, non-empty event coordinates, non-default completion
  time, and a named tenant-qualified foreign key to the authoritative profile;
- the processing-restriction owner foundation: reference-safe restriction
  records, immutable transition receipts, a versioned effective projection,
  initialization for every operationally visible Guest/property coordinate and
  a conservative PostgreSQL backfill;
- approved, idempotent restriction apply/release commands plus bounded active
  restriction listing, with exact approval intent and optimistic revisions;
- fail-closed ordinary detail, list, stay-history, update and archive
  enforcement while DataRights discovery, export, correction and restriction
  workflows retain explicit owner access;
- a versioned fail-closed restriction gate, constant-size transition event and
  bounded keyset rebuild export for dependent modules, with no direct identity,
  contact or free-text values crossing the boundary;
- provider-enforced processing-restriction integrity: aggregate, effective
  projection, and receipt coordinates, actors, timestamps, lifecycle versions,
  and reachable revision/count state fail closed at PostgreSQL. Named
  tenant-qualified foreign keys bind each restriction to its effective
  projection and every receipt to its authoritative restriction coordinates;
- independently releasable tenant/property/Guest data holds with stable reason
  codes, exact optimistic versions, immutable idempotent receipts, bounded
  listing and a dedicated permission absent from ordinary workspace roles;
- provider-enforced data-hold audit integrity: hold and receipt coordinates,
  normalized audit text, and timestamps fail closed at PostgreSQL, while a
  named composite tenant-qualified foreign key binds every receipt's property,
  Guest, and reason evidence to its authoritative hold. Model-wide guards also
  reject future unscoped Guests entities or cross-tenant relationships;
- host-configurable authentication assurance on public correction execution,
  processing-restriction transitions, and data-hold release, while hold
  placement and read-only investigation surfaces remain immediately available;
- a versioned internal anonymisation-eligibility contract that resolves the
  bounded Guest property set from Guests-owned projections, checks every
  active hold, operational stay, property state and current erasure policy,
  compares the exact frozen routing-policy evidence, and returns only stable
  blocker codes and canonical digests;
- terminal Guest anonymisation that clears every profile and normalized-search
  personal-data field in one owner transaction, persists an immutable
  canonical-digest receipt and monotonic tombstone, and emits one PII-free
  event;
- a versioned restore contributor that accepts only verified DataRights ledger
  proof, re-scrubs restored profiles or recreates a PII-free terminal profile,
  and persists an immutable restore receipt plus attached tombstone proof;
- provider-enforced anonymisation proof integrity: receipts and tombstones bind
  to their same-tenant Guest profile, restore receipts bind to exactly one
  tombstone, proof coordinates and lowercase SHA-256 values fail closed, and
  only the four reachable DataRights or Retention tombstone lifecycle shapes
  can be persisted;
- database-level exclusion of anonymised Guests from ordinary detail, list,
  discovery and export surfaces, plus operation locks that serialize new
  holds and owner projection changes against destructive eligibility;
- internal-only owner execution and restore commands exposed only through
  versioned DataRights contributor contracts; no public Guest surface can
  dispatch either command;
- explicit stay-projection contract versions so unknown future projection
  semantics block destructive eligibility instead of being interpreted
  optimistically;
- tenant-scoped automatic retention for the country-policy data class
  `guest-operational`, using a persisted fair-scan cursor, bounded set-based
  discovery, exact retry receipts, active-hold enforcement, and the latest
  applicable property-local stay deadline;
- deterministic retention time-zone evidence projected from Properties through
  generic v2 events, the dedicated time-zone event, and bounded rebuilds. Only
  an exact primary ID proved by the running embedded TZDB catalog is eligible
  for an active property. A retired alias may use its exact current-catalog
  Generic/Rebuild mapping because Properties correctly disables mutation after
  retirement; active aliases, Windows IDs, unknown IDs, stale evidence, catalog
  mismatch, and a whole skipped civil day fail closed without consulting host
  `TimeZoneInfo`;
- current-converged property time-zone semantics for unreceipted terminal
  `DateOnly` stays: a confirmed property correction recomputes future
  eligibility and changes its proof digest, while committed receipts and exact
  replays remain immutable;
- retention receipt contract version 2 with the exact PII-free TZDB catalog
  version, continued read/export support for version-1 receipts, and bounded
  refusal above 256 affected property associations;
- provider-enforced retention control and proof integrity: executions,
  fair-scan checkpoints, receipts, Guests, tombstones and PII-free events use
  stable named constraints and same-tenant relationships; only reachable
  control states, system attribution and lowercase SHA-256 proof can persist;
- attempt-fenced retention mutation and completion: an already stale worker is
  rejected before owner discovery or locking, a failed attempt leaves the
  fair-scan checkpoint at its proven start, and only a higher Retention attempt
  may reopen that execution while preserving its cumulative affected count;
- Guests tenant-termination owner catalog version 5, binding personal-data
  catalog 16 and tenant export schema 4 without reusing the prior version-4
  catalog coordinate. Frozen version-4 owner work fails the existing exact
  catalog check; current-coordinate closed-receipt replay remains stable;
- ordered adaptive scan pages: `ScanSize` is an upper bound, while a cumulative
  256-property-association materialization budget may return a shorter prefix
  with backlog for the next cursor. A single over-limit candidate fails closed
  without hydrating its related graph, so a short page is neither data loss nor
  a broken scan-size setting;
- authority-bound automatic anonymisation proof that cannot be replayed as a
  DataRights approval. The proof is currently local to Guests and is
  deliberately excluded from the DataRights subject export and protected
  replay ledger;
- a mandatory tenant-termination owner whose `Export` and `Destroy` phases
  depend on Reservations. Export streams 12 deterministic authoritative
  record types: profiles,
  management-operation replay proof, correction proof, processing restrictions
  and receipts, data holds and receipts, anonymisation receipts, tombstones and
  restore proof, plus retention execution and anonymisation proof;
- a tenant-local monotonic revision and shared tenant-mutation transaction key
  that serialize relational writes with repeatable-read export selection. The
  exporter validates the exact frozen Workspaces process, epoch and fence both
  before and after streaming, while ordinary writes fail closed when fence
  admission is unavailable or restricted;
- PostgreSQL append-only triggers for all six immutable receipt ledgers and a
  delete guard for anonymisation tombstones. Replicated property, stay and
  effective-restriction projections, inbox/outbox state, rebuild checkpoints,
  operation locks and retention sweep cursors remain outside the tenant
  portability artifact. Destroy removes that complete owner graph in
  foreign-key-safe batches of at most 500 rows, blocks active Guest legal
  holds before local progress, fences scoped messages and projections, and
  retains only a closed lifecycle row plus an immutable PII-free receipt;
- a transaction-local PostgreSQL destroy authorization bound to the live
  operation, scope, request digest, and `Closing` lifecycle. It permits only
  the owner deletion path through existing receipt/tombstone triggers; normal
  updates and deletes remain prohibited;
- PII-free profile and reservation/stay integration contracts;
- monotonic stay history that retains inactive replaced links for audit without granting visibility;
- local Properties projection plus task-driven rebuilds for Properties and Reservation stay history;
- PostgreSQL migrations, inbox/outbox, Worker composition, focused tests, and a real PostgreSQL/JetStream saga.

Reservations owns booking roles and current participant links. Guests owns profiles, visibility associations, and its history projection. Neither module writes the other's schema or uses cross-module foreign keys.

## Retention Time-Zone Recovery And Rollout

The stable owner outcome
`guests.guest-operational.time-zone-unavailable` is recovered through existing
surfaces: correct an active property through Properties time-zone compliance,
allow the dedicated event to converge or complete the existing Guests
Properties projection rebuild, re-check Properties compliance, then trigger or
await a new policy-version-2 retention execution and verify its PII-free
outcome clears. A failed owner result is replayed for the same attempt. A higher
Retention attempt deliberately reopens the same owner execution from its proven
failed cursor; the current execution row then supersedes that failed result,
while immutable Guest anonymisation receipts and tombstones are never rewritten.
A retired alias cannot be corrected; its current Properties snapshot plus a
verified Guests rebuild is the bounded recovery path. There is no raw Guests
projection editor or new Guest HTTP/Admin/CLI route.

The schema upgrade deliberately marks legacy projections fail closed. A hosted
rollout requires a bounded Guests maintenance window: quiesce all old-binary
Guests database traffic, including writers, long-running reads, consumers, and
retention workers; prove no retention execution is `Running` and no Guests
tenant destruction is `Closing`; then migrate and deploy the matching
binary/catalog. The migration takes `ACCESS EXCLUSIVE NOWAIT` over the complete
Guests owner graph, so lock failure stops the rollout until the blocker is
identified and drained; it is not retried against live API traffic. Keep other
Guests traffic quiesced while rebuilding all relevant tenant projections,
verify rebuild completion and Properties compliance, and only then resume
traffic and execution policy version 2. Downgrade is refused after version-2
execution, evidence, or receipts exist. Repository and Docker checks do not
substitute for that deployment proof.

## Deployment Assurance

The BunkFy composition repository owns an exact-release deployment verifier for
the existing durable Guest path. On 2026-08-13, its VPS Preview execution passed
19 checks covering minimal create and versioned update replay, conflict and stale
write rejection, the Reservations-owned primary link, monotonic stay-history
projection through checkout, Inventory release, archive replay, archived-history
visibility, nonmember denial, and release continuity. The proof changed no
Guests, Reservations, Inventory, or GMA module source; hosted-production
execution remains a separate admission requirement.

Identity documents, consent, orphan-profile retention triggers, duplicate
merge/split, entity resolution, guest flags, preferences, and guest accounts
remain deferred.
Immutable time-zone-at-stay provenance remains deferred; it would require a
separate Reservations event and rebuild-export contract evolution.
Protected export artifacts and download surfaces remain owned by DataRights.
The production tenant-termination task, download route and cleanup runner stay
disabled until every frozen mandatory owner, protected replay, operator
controls, and the final confirmation flow are complete.
Reservations owns its local restriction-eligibility projection and rechecks
this module's authoritative gate before every new canonical Guest link.

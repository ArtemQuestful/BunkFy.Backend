# Guests Retention Canonical Time-Zone Evidence Task

Status: implementation and local repository admission complete; deployment proof pending
Date: 2026-08-15

## Goal

Make automatic Guest retention deadlines deterministic across Linux, Windows,
containers, restore environments, and future worker nodes. Guests must use the
pinned embedded TZDB catalog and durable Properties evidence, never the host
operating system's time-zone database, before it performs destructive work.

This is a focused continuation of
[`guest-record-retention-task.md`](guest-record-retention-task.md). It does not
add a Guest HTTP or CLI surface and it does not change approved country-policy
periods.

## Ownership

- Properties remains the canonical property time-zone owner. Its generic
  topology events, dedicated time-zone transition event, compliance/correction
  workflow, and topology rebuild are the only repair authorities.
- Guests owns its tenant-scoped, rebuildable time-zone evidence projection,
  destructive-retention admission, bounded candidate discovery, receipt, and
  retry behavior.
- `BunkFy.TimeZones` owns embedded TZDB catalog resolution and civil-calendar
  math. The Guests retention path must not call the host `TimeZoneInfo`
  database; the separate Properties runtime-compatibility probe remains an
  operator compliance input.
- Retention continues to receive only stable owner outcome codes and bounded
  counts. It does not receive property, Guest, catalog, or deadline evidence.
- No GMA change belongs in this slice.

## Projected Evidence

For every property used by retention, Guests retains:

- the raw time-zone identifier projected by Properties;
- its canonical identifier when the embedded catalog can resolve it;
- `Canonical`, `Alias`, `Legacy`, or `Unrecognized` status;
- the exact embedded catalog version that classified a canonical identifier or
  alias;
- evidence source `Generic`, `Dedicated`, or `Rebuild`; and
- the positive source version that produced that evidence.

A PropertyCreated or PropertyUpdated v2 event is deterministically enriched in
the consumer. An exact primary embedded identifier records current-catalog
`Generic` evidence. An alias retains its canonical suggestion and
current-catalog `Generic` evidence, but active status makes that evidence
inadmissible. The mapping is retained so a retired property, whose time zone
can no longer be corrected, has a bounded rebuild recovery. A Windows
identifier is `Legacy`; an unknown identifier is `Unrecognized`. Those
classifications fail closed.

The dedicated time-zone event and projection rebuild can enrich an equal
topology version. Duplicate, stale, and out-of-order delivery cannot downgrade
stronger evidence or regress topology. A later topology version invalidates
older time-zone evidence only when that topology update carries raw time-zone
truth, until matching generic, dedicated, or rebuild evidence arrives. A
`PropertyRetired` transition carries no raw time-zone value and intentionally
preserves the last matching current-catalog evidence for retired-property
admission and rebuild recovery. Rebuild output uses the same embedded
classification rules rather than trusting exported strings as proof.

## Destructive Admission

Retention accepts an active property only when one fresh Guests transaction
proves all of the following:

- raw and canonical identifiers are exact ordinal matches;
- status is `Canonical`;
- the embedded catalog still resolves the identifier as an exact primary ID;
- the projected catalog version equals the running embedded catalog version;
- source is `Generic`, `Dedicated`, or `Rebuild`; and
- the evidence source version is positive and does not exceed the positive
  topology source version.

A retired property may instead use an exact current-catalog alias mapping only
from `Generic` or `Rebuild` evidence. Guests re-resolves the raw alias against
the embedded catalog and requires an ordinal match to the projected canonical
ID. Dedicated alias evidence is never accepted. This exception is necessary
because Properties truthfully disables correction after retirement; it does
not allow active properties to bypass canonicalization.

An active alias, Windows ID, unknown ID, missing, stale, future-version,
catalog-mismatched, or internally inconsistent evidence returns the stable
time-zone-unavailable owner outcome. It cannot anonymise a Guest.

Candidate discovery performs a bounded preflight before materializing stay,
hold, property, and governance-acknowledgement collections. More than 256
affected property associations, more than the Properties contract's bounded
acknowledgements for any projected policy, or any aggregate that cannot be
represented inside the frozen contract bounds fails closed without unbounded
allocation or one-query-per-Guest behavior.

`ScanSize` remains an upper bound, not a promise that every page contains that
many candidates. Candidate heads are considered in projection-ordinal order,
and one materialized page has a cumulative budget of 256 distinct property
associations. Reaching that budget returns the ordered prefix with backlog so
the next scan resumes after its last ordinal; it does not lose the remaining
candidates or indicate a broken `ScanSize` setting. A single candidate with
more than 256 associations is returned as fail-closed overflow metadata without
hydrating its stay, hold, property, or policy graph.

## Calendar Semantics

The `stay-ended` trigger is the first valid instant of the local calendar day
after the terminal stay business date, resolved by the pinned embedded TZDB:

- ordinary midnight maps to its exact UTC instant;
- non-hour offsets such as `Asia/Kathmandu` are retained exactly;
- a repeated midnight uses the later instant so destructive work does not run
  after only the first occurrence;
- a short midnight gap uses the first valid instant after the gap; and
- a whole skipped civil day or a gap longer than three hours fails closed.

The exact invariant country-policy period is then added to that trigger. The
canonical policy digest includes the raw/canonical identifier, catalog version,
evidence source/version, resolved trigger, and deadline so workers cannot
silently disagree about the proof they applied.

Terminal stay projections carry a `DateOnly`, not immutable time-zone-at-stay
provenance. Consistent with the original Guest retention contract, an
unreceipted candidate uses the property's current converged canonical time-zone
evidence. A confirmed real Properties time-zone correction therefore
recomputes future eligibility and changes its digest. An already committed
receipt and its exact replay never change. Retaining historical stay-zone proof
would require a separate Reservations event and rebuild-export contract
evolution and remains deferred unless retention policy changes.

## Receipts, Retry, And Compatibility

New retention anonymisation receipts use contract version 2 and persist the
exact time-zone catalog version used for the decision. The field is PII-free and
is included in canonical receipt proof and tenant export.

Existing version-1 receipts remain readable and exportable without fabricating
catalog evidence they never recorded. The migration must preserve their prior
canonical digest and append-only guarantees. Exact retries of a version-2
execution return the original result and keep one Guest mutation, one receipt,
one tombstone, and one outbox event.

Because the personal-data catalog advances to 16 and the tenant export schema
advances to 4, the Guests tenant-termination owner catalog advances from 4 to
5. Its manifest and SHA-256 therefore have a new coordinate; a centrally frozen
version-4 owner target is not rebound to the new manifest and must fail the
existing catalog-coordinate check. Closed destruction receipts remain
immutable, while a current-coordinate exact replay returns the same terminal
result and the version-5 descriptor.

## Rollout And Rollback

The schema upgrade intentionally leaves every pre-existing Guests property
projection without admissible evidence rather than inventing canonical proof.
Existing tenants therefore return
`guests.guest-operational.time-zone-unavailable` until their projection is
rebuilt. A production rollout must:

1. enter a bounded Guests maintenance window and quiesce all old-binary Guests
   database traffic, including writers, long-running reads, projection
   consumers, and retention workers;
2. prove no Guest retention execution remains `Running` and no Guests tenant
   destruction remains `Closing`;
3. apply the Guests migration, whose complete-owner-graph lock is
   `ACCESS EXCLUSIVE NOWAIT`; treat lock acquisition failure as a stopped
   rollout, identify and drain the blocker, and retry the migration only after
   the preflight is clean rather than looping against live traffic;
4. deploy the matching binary and embedded catalog;
5. while other Guests traffic remains quiesced, run the bounded Guests
   Properties projection rebuild for every relevant tenant;
6. verify the rebuild task receipts/completion and the Properties compliance
   view; and
7. only then leave the maintenance window and enable or resume Guest retention
   execution policy version 2 and the remaining Guests traffic.

Downgrade is refused after version-2 execution coordinates, current-catalog
projection evidence, or version-2 receipts exist. Operators must restore the
matching release or complete forward recovery; they must not discard proof to
force an older binary to start.

## Operator Recovery

`guests.guest-operational.time-zone-unavailable` is actionable without a new
Guest management surface:

1. inspect the existing Properties time-zone compliance view;
2. for an active property, correct it to an exact primary TZDB identifier
   through the Properties time-zone operation;
3. allow the dedicated event to converge, or run the existing Guests
   Properties projection rebuild when delivery evidence is missing; for a
   retired alias, where correction is intentionally disabled, the current
   Properties snapshot plus this rebuild is the bounded recovery path;
4. require completion evidence for that Guests rebuild and re-check the
   Properties compliance view for canonical, runtime-compatible active state,
   or exact current-catalog alias classification for a retired property;
5. trigger or await a new policy-version-2 retention execution and verify its
   stable PII-free owner outcome clears the time-zone-unavailable condition.

A failed owner result remains visible through Retention health with a stable,
PII-free outcome and is replayed for the same attempt. A higher Retention
attempt may deliberately reopen the same owner execution from the proven failed
cursor; that retry, not an exact replay, performs recovery. Immutable Guest
anonymisation receipts and tombstones are never rewritten. Operators must not
edit the Guests schema, rewrite immutable proof, or present a same-attempt
replay of the failed result as recovery. The later attempt-fencing correction is
specified in
[Guests retention attempt fencing and failed retry](guests-retention-attempt-fencing-and-failed-retry-task.md).

## Delivery

1. [x] Define the projected evidence and deterministic embedded-TZDB calendar
   contract.
2. [x] Add monotonic generic, dedicated, and rebuild projection convergence.
3. [x] Replace host time-zone deadline calculation and add bounded association
   preflight.
4. [x] Add receipt version 2, migration, tenant export compatibility, and
   append-only database constraints.
5. [x] Add focused calendar, projection, PostgreSQL runtime, retry, and
   two-tenant isolation proof.
6. [x] Update Guests documentation and run the complete backend admission gate.

## Acceptance

- `Etc/UTC`, `Asia/Kathmandu`, DST transition, repeated-midnight, and short-gap
  deadlines are identical on every supported host.
- A whole skipped civil date, active alias, Windows ID, unknown ID, or catalog
  mismatch fails closed with no Guest mutation.
- A retired alias becomes eligible only after exact current-catalog
  Generic/Rebuild evidence; no impossible post-retirement correction is
  required.
- New PropertyCreated v2 events are immediately usable when their identifier is
  already an exact current primary TZDB ID.
- Duplicate, stale, reversed generic/dedicated delivery, and rebuild recovery
  converge without evidence downgrade.
- More than 256 affected property associations or an over-bound policy
  acknowledgement set is refused through translated, bounded PostgreSQL
  candidate loading.
- Version-1 receipts still load and export; version-2 receipts retain the exact
  catalog evidence.
- An exact retry retains one receipt, one tombstone, and one anonymisation
  outbox event.
- Two tenants using identical local identifiers cannot read or mutate each
  other's projection, Guest, receipt, or outbox state.
- No new Guest API, Admin API, or Admin CLI route is introduced.

## Verification

Focused evidence on 2026-08-15 for migration
`20260815110326_AddGuestRetentionTimeZoneEvidence` (source SHA-256
`100345a7e58a22272938befe68d93d84220de5ebca28213e8231e8a9b976d940`):

- `Integration.Tests` built with zero warnings and zero errors;
- the complete Guests unit suite passed 203/203 and `BunkFy.TimeZones` passed
  34/34;
- the combined-worker owner-catalog coordinate check passed 1/1, including
  rejection of frozen Guests catalog version 4 against live version 5;
- documentation-link, documentation-index, and solution-file architecture
  guards passed 3/3;
- the PostgreSQL migration compatibility fact passed 1/1, covering fail-fast
  locking, all Up preflights and atomic revision invalidation, legacy backfill,
  version-1 receipt/export preservation, the post-Up policy-1 running guard,
  safe Down, and each independent Down refusal; and
- the embedded calendar plus three PostgreSQL runtime facts passed 4/4,
  covering convergence/rebuild, exact retry, two-tenant isolation, translated
  bounded loading, post-preflight growth, fail-closed outcomes, and recovery.

The complete post-rebase backend admission gate also passed: the strict full
solution build completed with zero warnings and zero errors; all 43 non-Docker
TRX suites reported 5,463/5,463 tests passed; all 21 configured migration
contexts were drift-free; solution sync, source packages, repository security,
repository release, and diff checks passed; all ten submodules matched their
recorded and live `origin/dev` heads; and the parsed 298-project direct and
transitive vulnerability audit reported no vulnerable packages.

These repository and local Docker checks do not prove the production rollout
protocol, hosted rebuild completion, or deployed retention execution;
deployment evidence remains a separate admission gate.

## Deferred

- Final production country packs, periods, enabled markets, and legal approval.
- Hosted production execution and restore-drill evidence.
- Automatic retention for other personal-data owners.
- Immutable time-zone-at-stay provenance across Reservations events and rebuild
  exports.
- A cross-module operator dashboard beyond the existing Properties compliance
  and Retention health surfaces.

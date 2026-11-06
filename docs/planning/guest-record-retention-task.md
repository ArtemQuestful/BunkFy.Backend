# Guest Record Automatic Retention Task

Status: complete
Date: 2026-07-28

## Goal

Automatically anonymise expired durable Guest Records through the existing
Retention control plane without turning Retention into a Guest-data owner,
copying personal data into task history, or inventing legal retention values
in application configuration.

The first slice covers the country-policy data class `guest-operational` with
trigger `stay-ended`. Profiles without a qualifying stay are not silently
assigned another trigger.

## Ownership

- Guests owns candidate discovery, stay and property projections, legal-hold
  checks, policy evaluation, exact mutation, receipts, tombstones, replay, and
  fair bounded scan progress.
- Retention owns the tenant schedule, PII-minimised execution receipt, health,
  retry visibility, and overdue status. It never reads Guests storage or
  receives Guest, stay, reservation, hold, or policy-binding identifiers.
- Properties owns property lifecycle, time zone, and activated governance
  bindings. Guests consumes those facts into a rebuildable local projection.
- Country-policy packs own approved retention policy id/version, data class,
  trigger, period, purpose, surface, provenance, and enabled-market evidence.
- GMA Tasks owns generic scheduling, leases, retries, cancellation, and worker
  execution. The existing `IRetentionExecutionContributor` contract is
  sufficient; no GMA change belongs in this slice.

`BunkFy.DataGovernance` may expose a generic exact retention-rule resolution
result so every BunkFy owner can consume the same country-pack fact. It must
not contain Guest-specific policy or lifecycle behavior.

## Eligibility

A profile is eligible only when one fresh Guests transaction proves all of
the following:

- the tenant and contributor contract coordinates match;
- the profile is `Active` or `Archived`, with the expected version;
- at least one supported terminal primary-stay projection exists;
- no current participant has an operational reservation state;
- every stay and affected-property projection uses a supported contract;
- no active Guest data hold applies;
- every affected property retains a valid governance binding whose exact
  country pack permits purpose `guest-profile-retention`, surface
  `retention`, and provenance `retention-worker`;
- the binding's exact retention rule is for data class
  `guest-operational` and trigger `stay-ended`;
- every applicable retention deadline has elapsed.

Affected properties are the origin property plus distinct stay properties.
Retired or processing-suspended properties do not make compliance work
disappear: their last valid projected binding may still authorize retention.
Missing, stale, expired, unsupported, or inconsistent policy evidence fails
closed.

An active legal hold returns a blocked owner result. Its earliest placement
time is the conservative review-due time; this slice does not invent a legal
hold review grace period.

## Deadline

For each affected property:

1. determine the latest applicable terminal stay business date;
2. use the property's projected time zone and the next local midnight as the
   conservative `stay-ended` instant;
3. add the exact invariant period resolved from the activated country-policy
   pack;
4. retain the canonical policy/rule evidence in a digest.

The profile is due only after the latest resulting property deadline.
An origin property with no stay uses the latest qualifying stay trigger from
the profile's other properties, which retains data longer rather than
shortening another property's rule. A cancellation without a more precise
terminal business date conservatively uses the booked departure date.

Profiles with no supported terminal stay are outside this data class. A later
country-policy rule may introduce an explicit orphan/profile-archive trigger;
this implementation must not infer one from creation, update, or archive
timestamps.

## Execution And Fairness

- Register one tenant-scoped schedule for owner `guests`, data class
  `guest-operational`, execution-policy version 1.
- Scan active/archived profiles by their monotonic projection ordinal using a
  tenant-local persisted cursor.
- Load each bounded page with set-based profile, stay, hold, and property
  queries; do not perform one discovery query per Guest.
- Re-evaluate each selected Guest under the existing Guest operation lock in
  its transactional command before mutation.
- Persist a Guests-owned execution record so a Retention retry returns exact
  accumulated affected counts after a mid-batch process failure.
- Fence every owner mutation and completion to the active Retention attempt.
  Failed evidence does not advance the fair-scan cursor; a later attempt
  reopens the execution and resumes from the proven failed cursor.
- Advance the sweep cursor only when the owner execution reaches a durable
  terminal result. A retry may rescan, but already anonymised Guests replay as
  exact no-ops.
- Wrap the cursor at the end of the candidate set. Blocked or malformed old
  records therefore cannot permanently starve later Guests.

All page, mutation, timeout, and attempt bounds are validated operational
settings. Retention periods never come from those settings.

## Mutation And Proof

The Guests aggregate reuses its existing irreversible personal-data scrub but
adds a retention-authorized transition that accepts `Active` or `Archived`.
The data-rights command remains approval-gated and retains its existing
eligibility semantics.

One transaction:

- revalidates policy, deadline, hold, stay, and profile version;
- clears the same Guest personal/search fields as data-rights anonymisation;
- writes a PII-free retention receipt with execution, policy-set digest,
  trigger/deadline, selected/resulting versions, event, and completion facts;
- writes the local Guest anonymisation tombstone with explicit retention
  authority;
- increments the owner execution's durable affected count;
- publishes the existing PII-free Guest anonymised event through the outbox.

The existing tombstone contract is evolved rather than creating two
conflicting one-to-one terminal markers. Existing data-rights tombstones are
backfilled as data-rights-authorized and remain compatible with protected
ledger restore. Retention tombstones do not pretend to have a data-rights case
or approval.

Backups and hosted restore drills remain deployment controls. Restoring a
pre-retention database makes the expired profile due again on the next bounded
sweep; this slice does not falsely claim protected data-rights-ledger coverage
for an automatic policy action.

## Security And Privacy

- Retention task payloads, receipts, logs, metrics, and status DTOs contain
  only tenant scope, owner/data-class keys, policy version, timestamps,
  bounded counts, and stable outcome codes.
- Guest, reservation, property, hold, receipt, and cursor identifiers remain
  inside Guests.
- No removed value, name, contact, date of birth, note, policy document, or
  property name is logged or returned to Retention.
- Unknown contract versions, policies, time zones, lifecycle states, and
  deadlines fail closed.
- Tenant scope is enforced in SQL and rechecked at the contributor boundary.
- The mutation is idempotent and optimistic-version controlled.

## Delivery

1. [x] Add generic country-pack retention-rule resolution and focused
   DataGovernance tests.
2. [x] Add Guests retention options, coordinates, candidate/evaluation
   contracts, and the tenant-scoped contributor.
3. [x] Add bounded set-based discovery, fair sweep cursor, exact owner
   execution, receipt/tombstone evolution, and PostgreSQL migration.
4. [x] Add transactional revalidation and aggregate mutation while preserving
   the data-rights approval path.
5. [x] Update property time-zone projection/rebuild handling and the
   development-only country policy.
6. [x] Update the Guests personal-data catalogue and concise development
   documentation.
7. [x] Add focused domain, policy, contributor, persistence, architecture,
   migration, and one PostgreSQL/Worker proof.
8. [x] Run one complete non-Docker backend gate, one relevant Docker gate,
   then publish and verify the exact candidate once.

## Verification

- The complete non-Docker backend verifier passed after its reported solution,
  digest-pin, request-file, and documentation-index corrections were applied;
  its build completed with zero warnings and every migration drift check
  passed.
- The final repaired architecture tail passed 3 of 3 checks, followed by 30
  ServiceDefaults tests and 39 non-Docker integration tests.
- The focused PostgreSQL/Worker Docker gate passed the two-tenant Retention
  control-plane scenario with all Ingestion and Guests schedules, one due
  Guest mutation, owner receipt, and retention-authority tombstone.
- The exact `dev` candidate is published and GitHub Actions is verified once
  before another production-readiness slice is selected.

## Acceptance

- A new active tenant receives the Guests schedule without Admin CLI work.
- A due active or archived Guest is anonymised exactly once and returns an
  exact result after process retry.
- Active/future stays, active holds, unknown policy, stale projection,
  unsupported lifecycle, and not-yet-due periods cannot mutate a profile.
- Policy migration changes future eligibility without replaying an old rule
  as current.
- A bounded scan eventually reaches later Guests even when earlier records
  remain blocked.
- Retention health can report complete, backlog, legal-hold, or stable failure
  outcomes without Guest identifiers.
- Two-tenant tests prove no cross-scope discovery or mutation.
- Existing data-rights anonymisation and protected restore behavior remain
  unchanged.
- Architecture guards prove Guests references only Retention Contracts and
  Retention does not reference Guests Application, Domain, or Persistence.

## Deferred

- A policy-approved trigger for durable profiles that have never had a stay.
- Final production country packs, periods, enabled markets, and legal approval.
- Hosted backup expiry and isolated restore-drill evidence.
- Automatic retention for Reservations, Staff, Notifications, Files, audit
  history, and other owner data classes.
- Tenant-termination deletion orchestration.

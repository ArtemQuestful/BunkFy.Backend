# Staff Record Automatic Retention Task

Status: implementation complete; retry and provider-integrity follow-up complete
Date: 2026-07-30

## Goal

Automatically anonymise expired departed Staff profiles through the existing
Retention control plane without turning Retention into a Staff-data owner,
copying Staff identifiers into task history, or bypassing the existing
employment-governance, hold, access-closure, and operation-lock safeguards.

This slice covers the existing country-policy data class `staff-employment`
with trigger `employment-ended`. It does not invent a fallback deadline for
active, suspended, ungoverned, or otherwise incomplete employment records.

## Ownership

- Staff owns candidate discovery, exact employment-governance and restriction
  snapshots, legal-hold checks, policy evaluation, mutation, receipts,
  tombstones, replay, and fair bounded scan progress.
- Retention owns the tenant schedule, PII-minimised execution result, health,
  retry visibility, and overdue status. It never reads Staff storage or
  receives Staff member, hold, policy-binding, or access-process identifiers.
- Workspaces owns workspace-access closure. It contributes one narrow,
  versioned Staff retention prerequisite that proves the completed departure
  access process and idempotently re-denies access before Staff is scrubbed.
- Country-policy packs own the approved policy id/version, retention period,
  data class, trigger, purpose, provenance, and enabled-market evidence.
- GMA Tasks owns generic scheduling, leases, retries, cancellation, and worker
  execution. Existing GMA and Retention contracts are sufficient.

The Workspaces prerequisite belongs in `Staff.Contracts`: Staff defines what
must be proven before its irreversible mutation, while Workspaces implements
the access fact it owns. Staff Application must not reference Workspaces
Application, Domain, or Persistence.

## Eligibility

A profile is eligible only when prerequisite preparation has committed and one
fresh Staff transaction proves all of the following:

- contributor and tenant coordinates match contract version 1;
- the Staff member is `Departed`, has `DepartedAtUtc` and
  `DepartureEffectiveOn`, and has no current property assignments;
- the expected Staff and operation-lock revisions still match;
- one current employment-governance record is bound to the exact selected
  Staff version;
- the processing-restriction projection uses the supported contract;
- no active Staff data hold applies;
- the exact country-policy binding permits purpose
  `staff-profile-retention`, provenance `retention-worker`, data class
  `staff-employment`, and trigger `employment-ended`;
- the retention deadline derived from `DepartedAtUtc` has elapsed; and
- every registered Staff retention prerequisite has prepared durable proof and
  verifies that proof without performing another mutation.

Restriction suppresses ordinary Staff processing but does not defeat
mandatory retention. Missing, stale, expired, unsupported, inconsistent, or
ambiguous governance and projection evidence fails closed.

An active hold returns a blocked owner result. Its earliest placement time is
the conservative review-due timestamp; this slice does not invent a legal
review interval.

## Access Closure

The Staff contracts assembly exposes a bounded, direct-identifier-free version
2 prerequisite request and result. Contributors have unique stable keys and
separate `PrepareAsync` and `VerifyAsync` phases. Both phases return
`Completed`, `Blocked`, or `RetryRequired` with a bounded stable code.

The Workspaces contributor:

- verifies the Staff record is still the selected departed version;
- for an Auth-linked profile, requires the matching completed departure
  access process; an unlinked profile has no access subject to close;
- verifies the process tenant, subject, Staff, version, and target state;
- idempotently re-applies access denial; and
- fails closed for protected workspace owners, missing mappings, state
  conflicts, or transient access-control failure.

Staff requires at least one prerequisite and rejects duplicate contributor
keys. A prerequisite failure is reported as a stable owner failure, never as
a legal hold, because Retention's blocked result requires a genuine
hold-review timestamp.

## Deadline And Policy Evidence

The immutable trigger is `DepartedAtUtc`, not a user-selected future
employment date. Staff adds the exact retention period resolved from the
current employment-governance binding and records a canonical digest over:

- Staff and governance source revisions;
- country-policy id, version, digest, and retention policy coordinates;
- data class, trigger, period, departure trigger, and resulting deadline;
- processing-restriction contract/revision; and
- operation-lock revision selected under the mutation transaction.

The persisted receipt stores only the digest and minimum coordinates needed
for proof. It does not copy profile fields, policy documents, hold reasons, or
workspace subject identifiers.

## Execution And Fairness

- Register one tenant-scoped schedule for owner `staff`, data class
  `staff-employment`, execution-policy version 1.
- Scan departed profiles by monotonic projection ordinal using a tenant-local
  persisted cursor.
- Load every bounded page with set-based Staff, governance, restriction, hold,
  and operation-lock queries; do not perform discovery queries per profile.
- Prepare every owner prerequisite before entering the Staff transaction, then
  re-evaluate each selected profile and verify durable prerequisite proof after
  acquiring the existing Staff operation lock.
- Persist a Staff-owned execution so a Retention retry returns exact
  accumulated affected counts after a mid-batch process failure.
- Fence every owner mutation and completion to the active Retention attempt.
  Failed evidence does not advance the fair-scan cursor; a later attempt
  reopens the execution and resumes from the proven failed cursor.
- Advance the cursor only with a durable terminal owner result, wrap at the
  end, and let already-anonymised records replay as exact no-ops.

Interval, scan, mutation-batch, timeout, and attempt bounds are validated
operational settings. Retention periods never come from application settings.

## Mutation And Proof

One Staff transaction:

- revalidates lifecycle, version, governance, policy, deadline, restriction,
  holds, operation lock, and the immutable Workspaces access-closure proof;
- reuses the existing irreversible Staff aggregate scrub;
- writes an immutable retention receipt containing execution id, random Staff
  id, selected/resulting Staff and lock revisions, trigger/deadline, policy
  digest, direct-identifier-free event coordinate, system actor, and completion time;
- writes the existing one-per-Staff tombstone with explicit `Retention`
  authority and the receipt digest;
- increments the owner execution's durable affected count; and
- publishes the existing direct-identifier-free Staff anonymised event through
  the outbox.

Data Rights receipts remain approval/case specific. Retention receipts never
pretend to have a Data Rights case, approval, executor, or protected ledger
entry. Data Rights restore accepts only Data Rights-authority tombstones.

Restoring a pre-retention application database makes the departed profile due
again on the next bounded sweep. This slice does not falsely claim protected
Data Rights ledger coverage for automatic policy execution.

## Persistence And Security

Staff adds tenant-scoped execution, checkpoint, and append-only retention
receipt tables. Existing tombstones evolve compatibly so current Data Rights
rows remain Data Rights-authorized. Indexes lead with tenant and operational
selection fields, and the candidate query remains bounded and indexable.

Retention payloads, logs, metrics, and status DTOs contain only tenant scope,
owner/data-class keys, policy version, timestamps, bounded counts, and stable
outcome codes. Staff identifiers never cross the Retention contributor
boundary. Unknown contracts and policy evidence fail closed.

Only the PostgreSQL migration project is provider specific. Staff Domain,
Application, Contracts, and Persistence remain provider agnostic.

## Delivery

1. [x] Add Staff retention coordinates, validated options, candidate and
   prerequisite contracts, and the tenant-scoped contributor.
2. [x] Add bounded set-based discovery, fair sweep checkpoint, exact owner
   execution, receipt, and retention-authority tombstone support.
3. [x] Add prerequisite preparation followed by transactional eligibility and
   proof revalidation, plus existing aggregate scrub reuse.
4. [x] Register the Workspaces prerequisite without introducing a direct
   Staff-to-Workspaces implementation dependency.
5. [x] Add the PostgreSQL migration, downgrade guard, module documentation,
   and executable personal-data catalogue entries.
6. [x] Add focused domain, policy, contributor, persistence, architecture,
   and one PostgreSQL/Worker proof.
7. [x] Run one complete non-Docker backend gate and one exact Docker
   scenario.

## Verification

- solution build: succeeded with zero warnings and zero errors;
- migration drift: clean for every mounted GMA and BunkFy migration project;
- non-Docker tests: 3,371 passed;
- Staff tests: 144 passed, including 31 focused retention tests; and
- exact PostgreSQL/Worker retention scenario: 1 passed.

## Acceptance

- A new active tenant receives the Staff schedule without Admin CLI work.
- A due departed Staff profile is anonymised exactly once and replays exactly
  after process retry.
- Active/suspended Staff, current assignments, active holds, future deadlines,
  stale governance, missing linked-profile access closure, and unknown policy
  cannot mutate.
- A bounded scan eventually reaches later Staff when earlier profiles remain
  held or invalid.
- Retention health reports complete, backlog, legal hold, or stable owner
  failure without Staff identifiers.
- Two-tenant proof demonstrates no cross-scope discovery or mutation.
- Existing Staff correction, restriction, Data Rights anonymisation,
  protected restore, departure access denial, and owner protection remain
  unchanged.
- Architecture guards prove Staff references only Retention Contracts and
  Staff Contracts own the Workspaces prerequisite boundary.

## Deferred

- Final production country packs, periods, enabled markets, and legal
  approval.
- Employment agreements or legal entities as a richer future governance
  owner.
- Hosted backup expiry and isolated restore-drill evidence.
- Automatic retention for Notifications, Files, audit history, and remaining
  owner data classes.
- Tenant-termination deletion orchestration.

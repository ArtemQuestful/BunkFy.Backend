# Reservation Record Retention Task

Status: complete; hosted execution proof remains a release gate

## Outcome

Add automatic, country-policy-driven minimisation for terminal reservation
records. Reservations remains the sole owner of candidate selection, legal-hold
checks, policy evaluation, mutation, idempotency, receipts, and tombstones.
Retention receives only bounded counts and stable outcome codes through the
existing `IRetentionExecutionContributor` contract.

The first slice covers one country-policy data class,
`reservation-operational`, with trigger `reservation-ended`. When the approved
period has elapsed, the owner removes guest identity, contact details, notes,
guest links, sensitive details history, direct provider references, pending
reminders, and reducible adapter evidence while retaining the non-personal
minimum reservation, stay, allocation, and accountability facts.

## Boundary

- Reservations owns all reservation records and every retention-side mutation.
- Retention owns generic schedule reconciliation, PII-minimized execution
  attempts, health, retry, and operator status.
- GMA owns leases, retries, cancellation, heartbeats, worker execution, and
  terminal task history. No BunkFy data class or policy belongs in GMA.
- Properties remains authoritative for topology and processing-policy facts.
  Reservations uses only its local projections and never reads another
  module's database.
- Country-policy packs authorize the purpose, surface, source provenance,
  retention data class, trigger, period, and policy evidence.
- A selected retention policy id/version is a versioned rule set; its rules are
  unique by policy id, version, data class, and trigger so one property binding
  can resolve multiple owner data classes.
- Existing data-rights anonymisation and automatic retention are separate
  authorities. They share the domain redaction primitive and owner-local
  persistence reduction, but not approval coordinates or receipts.

## Decisions

### Tenant-scoped scheduling

The contributor registers one tenant-scoped schedule. A property-scoped
schedule would stop when a property is suspended or retired and could strand
old reservation PII. The owner instead scans the tenant's terminal reservations
fairly, then resolves the exact current property projection and country policy
for each candidate. An inactive organization still receives no new ordinary
work from the Retention control plane.

### Immutable terminal trigger

`UpdatedAtUtc` is not a retention trigger because legitimate post-stay
correction, guest-link, restriction, hold, and anonymisation activity can
change it. Reservations therefore persists `TerminalAtUtc` exactly once when
the reservation first reaches `AllocationRejected`, `Cancelled`, `NoShow`, or
`CheckedOut`.

Existing terminal rows are backfilled conservatively from their terminal
lifecycle timestamps, with `UpdatedAtUtc` and then `CreatedAtUtc` as bounded
legacy fallbacks. The value never changes after the first terminal transition.
The retention deadline is `TerminalAtUtc + current approved policy period`.

### Eligibility

A reservation is eligible only when all of these remain true at mutation time:

- the tenant, reservation, property, record version, details revision, and
  terminal trigger are valid;
- the reservation is terminal, not already anonymised, and has no pending
  allocation amendment or release;
- the current processing-restriction projection contract is available;
- no active reservation data hold exists;
- the property projection and governance binding are current and usable,
  including for a retired or processing-suspended property;
- the country pack permits purpose `reservation-retention`, surface
  `retention`, source provenance `retention-worker`, data class
  `reservation-operational`, and trigger `reservation-ended`;
- the resolved period has elapsed.

Missing or stale owner facts fail closed. A due active hold returns a blocked
outcome and bounded review timestamp. A record changed between scan and mutation
is re-evaluated under its new version and skipped safely.

### Mutation and authority

The automatic path acquires the existing reservation operation lock, reloads
and re-evaluates the candidate, invokes the existing aggregate anonymisation
primitive with actor `system:retention`, and uses the existing owner-local
redaction repository for related history, adapter evidence, reminders, and
guest links.

It writes:

- a durable, versioned retention execution and fair-scan checkpoint;
- an append-only retention anonymisation receipt containing only owner-local
  coordinates, selected/resulting versions, terminal trigger, deadline,
  policy digest, reduction counts, event id, actor, and completion time;
- the existing reservation anonymisation tombstone with explicit
  `Retention` authority.

Existing data-rights tombstones are migrated to explicit `DataRights`
authority. Data-rights restore accepts only `DataRights` tombstones; an
automatic retention decision cannot be restored through a rights-case replay.
The retention receipt raises the existing reservation-anonymised event so
owner projections and downstream module contracts remain coherent.

### Attempt fencing and failed-attempt recovery

Task Runtime and Retention use two deliberately different counters. Task
Runtime `Attempt` remains the per-run retry-budget counter and restarts when an
operator invokes `RetryAsync`. The persisted, monotonic `LeaseGeneration`
survives that operation and is the value Retention stores and forwards through
its execution and owner-contract field named `Attempt`. Reservations therefore
fences owner work to a lease generation, not to the resettable Task Runtime
budget counter.

If lease reclaim reaches a central Retention execution that is already
`Completed` or `Blocked`, Retention replays that terminal aggregate without
redispatching Reservations. If Reservations committed terminal owner evidence
before the worker committed central completion, a newer lease generation opens
a forward-only central recovery window and redispatches only to obtain the
owner's exact replay. The Reservations execution and receipt remain unchanged;
when their completion predates the new central start, only the central result's
completion time is normalized to that new start before central persistence.
This preserves immutable owner proof while satisfying the new central window.

The Retention request attempt is carried through every owner mutation and the
owner completion command. A mutation is admitted only when the matching
Reservations execution is `Running` for the same tenant, data class,
execution-policy version, and exact attempt. Completion is likewise fenced to
the active attempt. A late worker from an earlier attempt can therefore neither
anonymise another record nor terminalize or advance the cursor of a newer
attempt.

`Failed` is terminal evidence for the current attempt. Replaying that same
attempt returns the persisted status, counts, outcome code, completion time,
and hold timestamp exactly; correcting the underlying condition does not
silently reopen it. Recovery requires a strictly higher lease generation,
exposed to the owner as a new attempt, and its start cannot precede the prior
failed completion. Starting that attempt retains the cumulative affected count,
clears the current-attempt terminal result, and starts from the failed
execution's original cursor.
Already-applied records are recognized through their retention receipt and
tombstone, so the required rescan does not duplicate mutation or proof.

A newly failed completion never advances the fair-scan checkpoint. The retry
therefore normally observes the unchanged starting ordinal and leaves the
checkpoint untouched until a non-failed completion succeeds. Compatibility
recovery for a legacy row that advanced on failure is deliberately narrow: the
execution must still be `Failed`; tenant, data class, and policy version must
match; retry time must be at or after the persisted completion; the checkpoint
must name that exact execution; and its update timestamp must equal that exact
completion timestamp. Only then is it rewound to the execution's starting
ordinal and disassociated from the failed execution. A checkpoint belonging to
another execution, carrying a different timestamp, or otherwise newer or
ambiguous fails closed.

Owner mutation and completion timestamps are converted to UTC and truncated to
PostgreSQL's microsecond precision before persistence and response creation.
This includes non-UTC clocks and sub-microsecond .NET ticks, so the first result,
the durable execution/receipt, and exact replay expose the same completion
instant.

### Fairness and efficiency

- Scan the indexed, non-anonymised terminal set by monotonic
  `ProjectionOrdinal`.
- Persist one cursor per tenant, data class, and execution-policy version.
- Validate `ScanSize` independently at configuration and repository boundaries
  as 1 through 1,000. Read at most `ScanSize + 1` candidate heads for end
  detection, but fully materialize at most `ScanSize` candidates and load their
  related facts with set-based queries.
- Materialize at most 64 governance acknowledgements per property from one
  coherent, bounded policy statement. A 65th row visible to that statement
  withholds the property's governance policy and produces a fail-closed
  policy-unavailable result. A later policy change is observed by the
  mutation-time reload or the next scan rather than mixed into the earlier
  snapshot.
- Mutate at most `MutationBatchSize` records per occurrence.
- Reset the cursor only after reaching the end, so a large tenant cannot starve
  later records and newly terminal earlier ordinals are picked up on the next
  cycle.
- Do not place reservation ids, property names, guest data, provider
  references, or free text in Retention payloads, logs, metrics, or receipts.

## Delivery

1. [x] Persist immutable terminal triggers and harden terminal transitions.
2. [x] Add Reservations-owned retention execution, checkpoint, receipt, and
   tombstone authority.
3. [x] Add bounded candidate queries, country-policy eligibility, and exact
   mutation-time re-evaluation.
4. [x] Register the tenant-scoped Retention contributor and validated runtime
   options.
5. [x] Add the development and integration country-policy purpose/rule and
   update the checked-in personal-data catalogue.
6. [x] Add focused domain, application, persistence, contributor,
   architecture, and catalogue coverage.
7. [x] Extend the existing PostgreSQL/Worker control-plane proof without
   creating another container.
8. [x] Run the complete non-Docker gate once and the relevant Docker gate once.
9. [x] Publish and verify exact candidates.

## Evidence

- The current local hardening pass is not hosted or deployment evidence.
  Focused retention tests pass 42/42, the complete Reservations and Retention
  unit suites pass 365/365 and 50/50, and the strict touched-project builds
  have zero warnings or errors. The exact PostgreSQL provider scenarios pass
  2/2; the consolidated Reservations Docker admission passes 25/25; and both
  saga facts pass twice consecutively. The post-rebase repository matrix,
  hosted checks, and deployed proof remain separate release gates for this
  amendment.
- Focused Reservations retention tests pass 24/24, including domain,
  eligibility, contributor, mutation, policy-version cursor, append-only
  receipt, tenant boundary, and model constraints.
- Reservations personal-data catalogue tests pass 8/8, the generated inventory
  is current, and the focused module-boundary architecture guard passes.
- The final Reservations migration builds without warnings and reports no
  pending model changes. Its upgrade path backfills terminal timestamps and
  migrates legacy tombstones to explicit DataRights authority before enforcing
  the new constraints.
- The existing two-tenant Retention worker scenario now includes one due
  Reservations candidate per tenant and verifies bounded central results,
  owner execution, redacted history, receipt, and Retention-authority
  tombstone.
- The complete non-Docker gate passed after correcting two stale rule-set and
  catalogue-version test expectations. Solution synchronization, source-package
  boundaries, the full build with zero warnings, migration drift, architecture,
  and all non-Docker test projects are green.
- The complete Docker gate passed 63/65 before exposing two isolated issues:
  a legacy migration assertion still expected tombstone contract v1, and the
  Reservations candidate query applied its cursor predicate after a correlated
  projection that PostgreSQL could not translate. Both were fixed as one batch;
  the exact migration scenario and the full two-tenant Retention scheduler
  scenario then passed 1/1 independently. The other 63 Docker scenarios were
  not replayed because neither correction touched their execution surfaces.
- Retention now logs owner exceptions with owner, data class, execution, and
  attempt coordinates before persisting the bounded generic failure outcome;
  payloads and durable outcomes remain free of reservation and guest data.

## Acceptance

- A terminal reservation becomes due only after its exact approved deadline.
- Active/future/pending, held, stale-policy, stale-projection, and changed
  reservations are never anonymised.
- Retired and processing-suspended property records continue to converge while
  their organization remains active and their last approved binding is usable.
- Exact retry returns the persisted result; partial progress resumes from the
  persisted cursor without duplicate mutation or proof.
- Data-rights and retention authority cannot impersonate or restore each
  other.
- The owner mutation removes every currently catalogued direct reservation
  identity surface and preserves minimum non-personal operational facts.
- Retention sees only counts, timestamps, stable codes, and schedule
  coordinates.
- Architecture tests prove Reservations references Retention only through
  Contracts and neither module reads the other's persistence.

## Deferred

- Final legal approval of periods, enabled country packs, and market-specific
  exceptions.
- Tenant-termination deletion and backup/object-store expiry.
- Retention of anonymisation receipts, holds, restriction receipts, message
  journals, and other accountability data classes.
- Provider-side deletion or remote OTA workflows.
- Financial, tax, payment, invoicing, and statutory accounting records that a
  future Accounting module must own.
- Generic retention policy abstractions in GMA.

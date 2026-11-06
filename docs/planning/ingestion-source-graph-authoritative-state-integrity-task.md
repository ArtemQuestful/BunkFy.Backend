# Ingestion Source Graph Authoritative State Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make Ingestion's canonical reservation source graph fail closed when direct
database access, a provider defect, an unsafe restore, or a future persistence
path attempts to store workflow state that the module cannot safely interpret.

This slice covers source links, reservation dispatches, change proposals, and
observation reprocessing attempts. It does not redesign adapter execution,
source ordering, Reservations authority, retention scheduling, Data Rights
coordination, or tenant termination.

## Audit Finding

Every online source-graph mutation is already tenant-admitted, serialized on a
deterministic source coordinate, reloaded after locking, and protected by
optimistic versions. Four stateful owner tables nevertheless declare no
model-owned lifecycle contract, allowing PostgreSQL to retain impossible:

- empty tenant, property, connection, receipt, operation, or task coordinates;
- unknown enum values, non-positive versions, invalid revisions, or negative
  source sequences and result counters;
- active records with terminal evidence, terminal records without completion
  or retention evidence, and contradictory decision state;
- partially retained or partially scrubbed sensitive history; and
- anonymised source records that still identify a reservation or provider
  revision.

Two sensitive-history constraints and the proposal reason-code constraint
already exist as hand-written SQL in
`AddSensitiveHistoryRetention`. They are absent from the EF model and snapshot.
That split ownership is migration-hostile and the sensitive-history checks also
require ordinary retention redaction to wait for its deadline, which
accidentally blocks an otherwise eligible Data Rights anonymisation performed
before that deadline.

## Ownership

- Ingestion owns source-link identity, external ordering evidence, dispatch
  state, staff proposal state, parser reprocessing state, and owner-local
  anonymisation.
- Reservations owns reservation truth and returns versioned operation outcomes
  through contracts; it does not write Ingestion tables.
- Data Rights owns case approval and orchestration; Ingestion owns the selected
  source graph's reduction and proof.
- Retention owns automatic scheduling; Ingestion owns record eligibility,
  deadlines, redaction, and legal-hold enforcement.
- The Ingestion EF model owns the durable relational contract and the BunkFy
  PostgreSQL migrations project owns its concrete migration.
- GMA remains unchanged. Transaction-key locking, scoped persistence, CQRS,
  TaskRuntime, and file abstractions already provide the generic primitives.

## Invariants

### Reservation Source Links

1. Tenant, link, property, connection, observation, operation, and deferred
   coordinates are non-empty whenever present; source identity is non-blank.
2. Observation evidence is either wholly absent for a new link or contains a
   receipt and a lowercase 64-character hexadecimal content hash. Optional
   revisions are non-blank and optional source sequences are non-negative.
3. Applied evidence is either wholly absent or contains a receipt, product
   operation, positive reservation-details revision, and valid optional source
   ordering. Linked records retain a non-blank operational baseline; cancelled
   records do not.
4. State, reservation identity, active cancellation operation, version, and
   timestamps form one valid lifecycle shape.
5. An anonymised link has its deterministic anonymised source reference, no
   reservation, revision, operational baseline, active/deferred operation, or
   identifying content hash, and matching anonymisation/change timestamps.

### Reservation Dispatches

1. Tenant and required graph coordinates are non-empty; trigger, dispatch kind,
   source ordering, and expected/result revisions are in their declared ranges.
2. Create dispatches have no expected reservation-details revision. Other
   dispatches have a positive expected revision and retain a reservation id
   until anonymised.
3. Pending, accepted-cancellation, and terminal records have distinct version,
   completion, retention, and sensitive-history shapes.
4. Automatic retention redaction occurs at or after the retained-until time.
   Data Rights anonymisation may reduce a terminal record earlier, but it must
   atomically clear the snapshot, source revision, and reservation identity and
   record one matching redaction/anonymisation timestamp.

### Change Proposals

1. Tenant and graph coordinates are non-empty until anonymisation deliberately
   replaces the reservation id with the empty sentinel; base revision and
   reason code are valid and the proposal state is known.
2. Pending, applying, directly decided, and apply-result states require their
   exact actor, reason, operation, decision, completion, version, and retention
   evidence.
3. Sensitive-history redaction follows the retention deadline, while approved
   anonymisation may happen earlier only for a terminal proposal and must clear
   the reservation id, diff, and free-form decision reason atomically.

### Reprocessing Attempts

1. Tenant, property, connection, source receipt, and task coordinates are
   non-empty, and attempt id equals TaskRuntime run id.
2. Parser identity, actor, parser version, reservation window, aggregate
   version, task attempt, and all counters are valid; outcome counters sum to
   the parsed count.
3. Queued retry, running, successful, no-match, failed, cancelled, and expired
   states carry only their valid start, completion, and bounded error evidence.

## Migration Compatibility

- The migration is additive for newly modelled constraints.
- It replaces the two existing SQL-only sensitive-history constraints with
  model-owned definitions and restores the exact prior definitions on down.
- It adopts the existing reason-code constraint into model metadata without
  attempting to add or remove the constraint that the older migration owns.
- It does not rewrite, discard, redact, or anonymise source data.
- A hosted rollout must preflight the exact release database. Any malformed
  legacy row stops migration at a named constraint and requires an
  operator-approved repair; disposable PostgreSQL proof cannot establish that
  hosted data is clean.

## Delivery

1. Declare named source-graph constraints in the four EF configurations.
2. Add focused model metadata and domain behavior proofs.
3. Generate and review the BunkFy PostgreSQL migration, then reconcile legacy
   SQL-only constraint ownership explicitly.
4. Add one PostgreSQL 16 upgrade scenario that preserves representative active,
   terminal, redacted, and anonymised records, proves early rights-driven
   anonymisation, and rejects representative malformed writes by exact
   constraint name.
5. Align the Ingestion development note and close with focused checks followed
   by one coherent end-of-slice gate.

## Deferred

- Observation receipt lifecycle expansion and infrastructure inbox/outbox
  constraints remain separate audits; neither currently lacks all database
  integrity controls.
- Multi-fragment tenant-termination export, raw-object orphan reconciliation,
  tenant enumeration, vendor adapters, and federated workload identity remain
  their documented production or product slices.
- Hosted data preflight, migration application, rollback rehearsal, and
  same-release deployment evidence remain deployment admission work.

## Completion Criteria

- existing valid source-graph rows upgrade unchanged;
- rights-driven anonymisation before an automatic retention deadline succeeds
  only with the complete terminal scrub shape;
- representative malformed coordinates, workflow states, sensitive history,
  anonymisation, and reprocessing counters fail at PostgreSQL with stable names;
- focused Ingestion, architecture, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the finished slice boundary; and
- no GMA repository change is required.

## Verification Evidence

- Official migration generation through `eng/add-migration.ps1` completed with
  zero warnings and zero errors.
- Focused source-graph behavior and metadata proofs passed: 31/31.
- The complete Ingestion unit suite passed: 343/343.
- `Integration.Tests` built with zero warnings and zero errors.
- The focused PostgreSQL 16 migration scenario passed: 1/1 in 11 seconds,
  including upgrade preservation, named-constraint rejection, early
  rights-driven anonymisation, and guarded rollback.
- `eng/verify.ps1 -SkipRestore` passed at the completed slice boundary: the
  solution graph was synchronized, the serial solution build completed with
  zero warnings and zero errors, every migration-drift check passed, and all
  5,662 eligible non-Docker tests passed.
- `git diff --check` passed. GMA remained pinned and unchanged.

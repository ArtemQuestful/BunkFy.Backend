# Guests Authoritative Profile State Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make the Guests-owned canonical profile row fail closed when a provider defect,
direct database action, unsafe restore, or future persistence path attempts to
store a shape the aggregate cannot produce.

This slice strengthens profile-domain and database integrity only. It does not
change property visibility, management permissions, stay-history ownership,
Data Rights orchestration, retention, or tenant termination.

## Audit Finding

Guests already protects ordinary mutations with tenant admission, operation
locks, optimistic versions, retry journals, and owner transactions. Its
privacy workflows also verify holds, processing restrictions, eligibility,
receipts, and terminal anonymisation. PostgreSQL can nevertheless persist an
impossible canonical profile beneath those controls:

- empty tenant, Guest, origin-property, or optional confirmation coordinates;
- missing or blank normalized search copies for retained profile values;
- blank optional country, language, or notes values;
- non-positive projection ordinals or terminal states at creation version;
- lifecycle timestamps outside the profile's durable change timeline; and
- an anonymised profile that still retains Guest personal data.

## Ownership

- Guests owns canonical profile identity, personal data, lifecycle, search
  copies, and anonymisation invariants.
- Reservations owns booking participants and booking lifecycle.
- Properties owns property identity and lifecycle; Guests retains only its
  local visibility and policy projections.
- Data Rights owns request approval and orchestration; Guests owns the actual
  profile mutation and proof.
- The Guests EF model declares the durable contract and the BunkFy PostgreSQL
  migrations project owns its concrete migration.
- GMA remains unchanged. These are product-domain facts, and the framework
  already supplies the required scoping, CQRS, and persistence primitives.

## Invariants

1. Guest, origin-property, and optional confirmation ids are non-empty and
   tenant scope is non-blank.
2. The generated projection ordinal is positive.
3. Display-name search text is non-blank; optional legal-name, email, and phone
   values and their search copies are either both absent or both present and
   non-blank.
4. Optional nationality, language, and notes values cannot be blank.
5. Active profiles have version one or later; archived and anonymised profiles
   require at least one durable transition after creation.
6. The last-change timestamp cannot precede creation. Archive evidence equals
   the terminal archive change, while anonymisation evidence falls between
   creation and the last durable change so verified restore replay remains
   valid.
7. An anonymised profile has the exact anonymised display identity and retains
   no legal name, contact data, date of birth, nationality, language, notes,
   creation confirmation, or archive timestamp.

## Delivery

1. Add named canonical-profile constraints to the Guests EF configuration.
2. Extend focused model metadata tests for the complete profile contract.
3. Generate and review the additive BunkFy PostgreSQL migration.
4. Add one PostgreSQL 16 migration scenario that upgrades valid active,
   archived, and anonymised legacy rows, then proves representative malformed
   writes fail with exact provider constraint names.
5. Align the Guests development note and close with focused checks followed by
   one coherent end-of-slice gate.

## Deployment Safety

The migration does not rewrite or discard Guest data. Existing malformed rows
cause deployment to stop at the named constraint that found them. A hosted
rollout therefore needs a preflight against the exact release database and an
operator-approved repair path; repository and disposable PostgreSQL evidence
do not prove that production data is clean.

## Deferred

- Identity documents, consent, preferences, merge/split, entity resolution,
  Guest flags, and Guest accounts remain future business features.
- Immutable time-zone-at-stay provenance remains a Reservations contract
  evolution, not a canonical-profile concern.
- Rebuildable stay and property projection contract evolution remains separate
  from authoritative profile integrity.
- Hosted release migration and production-data preflight evidence remain part
  of deployment admission.

## Completion Criteria

- valid active, archived, and anonymised profile rows upgrade unchanged;
- malformed coordinates, search pairs, optional values, versions, timestamps,
  and anonymised states fail at PostgreSQL;
- focused Guests, architecture, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the finished slice boundary; and
- no GMA repository change is required.

## Verification Evidence

- focused profile model metadata proof: passed;
- integration project build: zero warnings and zero errors; and
- PostgreSQL 16 upgrade from `AddGuestRetentionTimeZoneEvidence`: passed with
  active, archived, and restore-replayed anonymised profiles retained, followed
  by seven malformed writes rejected with exact constraint names;
- full Guests model and domain suite: 206/206 passed;
- Guests PostgreSQL migration drift: no pending model changes; and
- solution operational-file guard: passed.

The consolidated backend gate passed with solution synchronization,
source-package checks, a zero-warning serial build, every configured migration-
drift check, Guests 206/206, Architecture 112/112, and non-Docker Integration
65/65.

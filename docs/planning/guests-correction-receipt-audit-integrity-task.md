# Guests Correction-Receipt Audit Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make the immutable Guests-owned Data Rights correction receipt fail closed when
a provider defect, direct database action, unsafe restore, or future persistence
path attempts to store evidence the domain cannot produce.

This slice does not change correction values, approval policy, permissions,
public contracts, Data Rights orchestration, profile mutation, or tenant
termination.

## Audit Result

The correction workflow is architecturally sound. Data Rights owns the approved
case and actor-bound execution claim; Guests rechecks that claim, applies the
normal profile mutation under the Guest operation lock, appends one PII-free
receipt, and publishes its completion proof in one owner transaction. Receipts
are append-only and idempotency/event lookups are bounded by existing indexes.

PostgreSQL currently protects contract version, approval/version arithmetic,
and changed-field mask only. A malformed direct write can still persist empty
receipt, idempotency, property, case, Guest, or event coordinates, use the same
identifier for the profile event and correction-completion event, store the
domain's rejected default completion time, or reference no Guest profile.

## Ownership

- Guests owns profile correction, immutable owner receipt evidence, and the
  receipt database contract.
- Data Rights owns case approval, execution claims, completion orchestration,
  and its own proof projection. It does not write Guests persistence.
- GMA already owns tenant scoping, CQRS transactions, outbox delivery, and
  append-only infrastructure primitives. No correction semantics belong in the
  framework.
- The Guests PostgreSQL migrations project owns the concrete schema change and
  provider proof.

## Invariants

1. Receipt, idempotency, property, case, Guest, profile-event, and completion-
   event identifiers are non-empty; persisted tenant scope is non-blank.
2. Profile-event and completion-event identifiers are distinct, matching the
   two separate durable facts emitted by the owner transaction.
3. Completion time cannot use the domain's rejected default value.
4. Existing contract, approval, profile-version, and changed-field-mask rules
   remain unchanged.
5. One tenant/property/case/approval decision produces at most one receipt, and
   one Guest resulting profile version appears in at most one correction
   receipt.
6. Every receipt references one Guest profile in the same tenant.
7. The existing `(ScopeId, GuestId, CurrentRecordVersion)` index enforces the
   relationship without another index. Existing case/version indexes become
   unique, and the unused `(ScopeId, Id)` receipt alternate key is removed.

## Delivery

1. Add named coordinate and timestamp constraints to the Guests EF
   configuration.
2. Make the existing approved-case and resulting-profile-version indexes unique
   and add a stable tenant-qualified receipt-to-profile foreign key with
   restricted deletion.
3. Extend focused model metadata tests for the complete receipt contract.
4. Generate and review the additive BunkFy PostgreSQL migration.
5. Add one PostgreSQL 16 scenario that preserves valid correction evidence,
   rejects representative malformed rows with exact provider names, and proves
   downgrade plus re-upgrade.
6. Run focused checks while editing, followed by one provider scenario and one
   consolidated backend gate at the finished slice boundary.

## Deployment Safety

The migration does not rewrite or discard Guest data. New checks and the foreign
key validate existing rows, so malformed or orphaned legacy receipts stop
deployment rather than being silently repaired. A hosted rollout needs an
exact-release preflight and operator-approved repair for any finding;
disposable PostgreSQL evidence does not prove production data is clean.

## Deferred

- Anonymisation, restore, tombstone, and retention-receipt integrity are a
  separate slice because their external-ledger and dual-authority relationships
  differ from correction.
- A foreign key from receipt property to a Guest/property visibility fact is
  intentionally absent: stay visibility changes over time and is not the
  immutable owner of the correction receipt.
- Hosted migration, production preflight, repair execution, and deployed proof
  remain release-admission work.
- GMA and the web application remain unchanged.

## Completion Criteria

- valid correction receipts upgrade unchanged;
- malformed coordinates, event identity, timestamps, and orphaned Guest
  references fail at PostgreSQL with stable names;
- focused model, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the finished slice boundary; and
- no GMA or web change is required.

## Verification

- The focused correction-receipt model proof passed `1/1`.
- The Integration.Tests build completed with zero warnings and zero errors.
- The PostgreSQL 16 migration scenario passed `1/1` in 10 seconds, including
  valid-row preservation, exact constraint failures, downgrade, and re-upgrade.
- `eng/verify.ps1 -SkipRestore` passed with synchronized solution and source
  package checks, a zero-warning build, all 21 migration-drift checks, and
  `5,712` non-Docker tests. Guests passed `211/211`, Operations Notifications
  passed `104/104`, Architecture passed `112/112`, and Integration passed
  `65/65`.

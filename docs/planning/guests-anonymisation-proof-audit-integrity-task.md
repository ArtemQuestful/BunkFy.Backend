# Guests Anonymisation-Proof Audit Integrity Task

Status: complete
Date: 2026-08-21

## Goal

Make the Guests-owned anonymisation receipt, durable tombstone, and external-
ledger restore receipt fail closed when direct database writes, unsafe restores,
or future persistence paths attempt to create proof the domain cannot produce.

This slice does not change erasure eligibility, Data Rights approval, retention
policy, public contracts, profile mutation, or tenant termination.

## Audit Result

The owner flow is architecturally sound. Ordinary Data Rights anonymisation
rechecks the exact approval and eligibility under the tenant-local Guest and
property locks, then updates the terminal profile and appends its receipt and
tombstone in one Guests transaction. Restore uses the same Guest lock and may
reconstruct an anonymised profile, tombstone, and restore receipt from the
protected external Data Rights ledger.

That external-ledger recovery path means a tombstone cannot require a local
original anonymisation receipt: the owner receipt may legitimately exist only
in protected replay evidence. Retention tombstones likewise point to a distinct
Guests retention receipt type. The stable relational owners are therefore the
Guest profile for receipts and tombstones, and the tombstone for restore proof.

Provider enforcement is incomplete. Receipt and restore coordinates and
timestamps can be malformed; actor trimming is not enforced; tombstone restore
fields can be partially attached; authority, revision, and replay state can be
combined impossibly; and relationship names are provider-generated and
truncated. The original owner-proof migration used lowercase SHA-256 checks in
its executable migration while its generated model metadata recorded only
length checks, leaving the current snapshot weaker than the intended schema.

## Ownership

- Guests owns profile anonymisation, owner receipts, tombstones, restore
  receipts, and their PostgreSQL contract.
- Data Rights owns approved case orchestration and the protected external
  replay ledger. It does not write Guests persistence.
- Retention owns policy and scheduling while Guests owns its terminal profile
  mutation and owner proof. Retention receipt integrity remains a separate
  Guests slice.
- GMA already owns tenant scoping, CQRS transactions, locks, outbox delivery,
  and generic append-only infrastructure. No BunkFy anonymisation semantics
  belong in the framework.

## Valid Tombstone States

1. Ordinary Data Rights anonymisation: authority `DataRights`, revision `1`,
   and no ledger replay fields.
2. Reconstructed Data Rights anonymisation: authority `DataRights`, revision
   `1`, with a non-empty ledger entry and replay time at or after completion.
3. Restore proof attached to an existing Data Rights tombstone: authority
   `DataRights`, revision `2`, with the complete replay pair.
4. Retention anonymisation: authority `Retention`, revision `1`, and no Data
   Rights ledger replay fields.

No other authority/revision/replay combination is reachable in the current
contract.

## Invariants

1. Tenant scope and every identity coordinate are non-empty, and completion or
   replay timestamps cannot use the domain's rejected default value.
2. Receipt actors are nonblank and persisted exactly trimmed.
3. Every SHA-256 field is exactly 64 lowercase hexadecimal characters, matching
   the domain canonicalization contract and reconciling model metadata with the
   original executable migration intent.
4. One profile event appears in at most one anonymisation receipt per tenant.
5. Tombstone ledger id and replay time are both absent or both present; replay
   cannot precede anonymisation completion.
6. Tombstone authority, revision, and replay fields match one of the valid
   states above. A ledger id is unique within its tenant when present.
7. Restore receipt identity equals its ledger entry, references a real
   same-tenant tombstone, and records only a current tombstone revision.
8. Anonymisation receipt and restore receipt keep only their global primary
   keys; unused `(ScopeId, Id)` alternate keys are removed. The tombstone's
   tenant-qualified key remains because restore receipts depend on it.

## Delivery

1. Add named coordinate, timestamp, digest, actor, replay-pair, and lifecycle
   constraints to the three Guests EF configurations.
2. Add stable names to receipt-to-profile, tombstone-to-profile, and restore-
   receipt-to-tombstone foreign keys; add event and replay-ledger uniqueness.
3. Extend focused model metadata tests for the complete proof graph.
4. Generate and review one additive Guests PostgreSQL migration.
5. Add one PostgreSQL 16 scenario preserving ordinary, restored, and retention
   tombstone states; reject representative malformed rows by exact provider
   name; prove downgrade and re-upgrade.
6. Run focused checks while editing, then one provider scenario and one
   consolidated backend gate at the finished slice boundary.

## Deployment Safety

The migration does not rewrite or discard Guest data. New constraints and
foreign-key validation intentionally stop deployment if legacy proof is
malformed. A hosted release therefore needs an exact-release preflight and an
operator-approved repair for any finding; disposable PostgreSQL evidence does
not establish production data quality.

## Deferred

- Retention execution, sweep checkpoint, and retention anonymisation receipt
  integrity are the next Guests slice.
- A tombstone-to-original-receipt foreign key is intentionally absent because
  Data Rights restore may rely only on protected external ledger evidence and
  retention uses a different owner receipt type.
- Canonical hash recomputation remains a domain/application responsibility;
  PostgreSQL enforces its bounded lowercase representation and append-only
  storage, not BunkFy-specific hash serialization logic.
- Hosted migration, production repair, and deployed proof remain release-
  admission work. GMA and the web application remain unchanged.

## Completion Criteria

- all four valid tombstone shapes upgrade unchanged;
- malformed receipt, tombstone, and restore proof fail at PostgreSQL with
  stable names;
- focused model, migration-drift, and PostgreSQL proofs pass;
- one consolidated backend gate passes at the finished slice boundary; and
- no GMA or web change is required.

## Verification

- The focused domain and anonymisation-proof model checks passed `2/2`.
- The Integration.Tests build completed with zero warnings and zero errors.
- The PostgreSQL 16 migration scenario passed `1/1` in 9 seconds, including
  all four valid tombstone shapes, exact named failures, downgrade, and
  re-upgrade.
- `eng/verify.ps1 -SkipRestore` passed with synchronized solution and source
  package checks, a zero-warning build, all 21 migration-drift checks, and
  `5,714` non-Docker tests. Guests passed `213/213`, Operations Notifications
  passed `104/104`, Architecture passed `112/112`, and Integration passed
  `65/65`.

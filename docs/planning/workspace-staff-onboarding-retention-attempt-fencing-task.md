# Workspace Staff Onboarding Retention Attempt Fencing Task

Status: completed
Date: 2026-08-22

## Goal

Make Workspaces-owned automatic cleanup of abandoned Staff-onboarding staging
safe when a Retention task attempt fails, is retried, or continues after a
newer attempt has started. Preserve truthful affected counts and terminal
evidence without moving onboarding policy into Retention or GMA.

This slice does not change source-expiry authority, grace or authority windows,
candidate eligibility, access-plan convergence, onboarding provisioning,
public contracts, or the generic Retention schedule.

## Audit Result

Candidate reconciliation is already source-locked, transactionally reloaded,
and idempotent. The contributor itself is stateless, however: it lists owner
candidates directly and its reconciliation command carries neither the generic
execution id nor the active attempt. A worker whose generic lease was
superseded can therefore discover and mutate Workspaces records after a newer
attempt starts. Retention will reject the stale terminal result, but that is
too late to preserve owner-attempt authority, completion timing, or affected
counts.

The current failure result also reports the selected batch size rather than the
number of candidates actually reached before a command failure. Replay keeps
the product state safe, but the operational receipt can be misleading.

## Ownership

- Retention owns schedules, attempts, deadlines, leases, contributor dispatch,
  and generic execution health. It does not read or write Workspaces storage.
- Workspaces owns staging eligibility, exact-attempt admission, candidate
  discovery, source-graph ordering, reconciliation, affected counts, owner
  execution evidence, and tenant termination of that evidence.
- Organizations remains authoritative for enrollment-link and claim history;
  Staff and Access Control remain authoritative for their own provisioning and
  access state. Workspaces uses only their contracts.
- GMA owns transactions, tenant admission, task leases, and the provider-
  neutral transaction-key primitive. No Workspaces policy or persistence type
  belongs in GMA.

## Invariants

1. Begin, candidate discovery, candidate reconciliation, and owner completion
   serialize on one tenant-qualified Workspaces execution lock whose key is
   stable across attempts.
2. The execution lock is acquired before the existing onboarding source and
   application locks. A stale attempt cannot discover candidates, inspect
   Organizations authority, mutate staging, provision downstream state, or
   terminalize owner evidence.
3. One owner execution row uses the generic Retention execution id and stores
   its tenant, data class, policy version, current attempt, timing, cumulative
   scanned and affected counts, terminal result, and optimistic version.
4. Only the exact current `Running` attempt and its stored time window may list
   or reconcile candidates. Every successful reconciliation increments the
   scanned count, and an affected reconciliation also increments the affected
   count, in the same Workspaces transaction as the onboarding and access-plan
   mutation or no-op.
5. A higher attempt may supersede a `Running` or `Failed` attempt only when its
   start is not earlier than the current start or failed completion and its
   deadline is later. It preserves both cumulative counts and clears only
   superseded terminal fields.
6. A failed owner result records the number of candidates actually attempted,
   including at most the one failed candidate whose command rolled back, not
   every row selected into the batch. A completed result must exactly match
   recorded scan progress. Retry scans authoritative state again;
   already-converged candidates are bounded no-ops.
7. Same-attempt terminal replay is accepted only for an exact stored result.
   Completed owner evidence cannot be reopened.
8. Execution evidence is tenant-filtered, concurrency-protected, exported by
   tenant termination, deleted in the bounded destruction protocol, and fully
   classified in the Workspaces personal-data catalogue.

## Delivery

1. Add the Workspaces Staff-onboarding retention execution aggregate, state,
   repository, EF configuration, and stable execution-lock port.
2. Add transactional begin, candidate-list, and completion commands. Carry
   execution id and attempt through reconciliation.
3. Acquire and validate the owner attempt before discovery or source locking;
   atomically record affected reconciliation.
4. Route the contributor exclusively through those commands and persist a
   failed owner result for known reconciliation failures.
5. Add the execution table to tenant termination export and bounded deletion,
   then update the executable personal-data catalogue and deterministic
   inventory.
6. Generate and review one additive Workspaces PostgreSQL migration.
7. Add focused aggregate, handler, contributor, ordering, EF model, catalogue,
   tenant-termination, and PostgreSQL lock/provider proof.
8. Use focused non-Docker checks while editing, then run one relevant Docker
   provider scenario and one consolidated backend gate at the completed slice
   boundary.

## Deployment Safety

The migration adds one owner-local execution table; it does not rewrite
onboarding or access-plan rows. It advances previously completed tenant
destruction operations from stage 20 to stage 21 because stage 20 now owns
bounded deletion of the new execution evidence. Downgrade reverses that stage
mapping and refuses to discard nonempty execution evidence. The stable lock is
transaction-scoped and tenant-qualified, so unrelated tenants and executions
remain concurrent.
Hosted rollout still requires exact-release migration preflight and deployed
retry proof; a disposable PostgreSQL scenario is not production evidence.

## Deferred

- There is no sweep checkpoint because eligibility is an indexed bounded scan
  over naturally disappearing staging rows; authoritative rescanning is the
  intended retry strategy.
- The owner execution state machines in Workspaces, Guests, Staff,
  Reservations, and Ingestion retain different checkpoint, proof, failure, and
  external-side-effect semantics. A shared GMA aggregate would erase those
  distinctions, so only the transaction-key primitive is reused.
- Generic Retention-to-owner distributed execution leases remain out of scope.
  The modular-monolith transaction lock and owner row are the production
  boundary for this deployment model.
- Hosted migration, production-data preflight, backup expiry, and deployed
  stale-worker proof remain release-admission work.

## Completion Criteria

- a stale attempt fails before candidate discovery and source locking;
- affected reconciliation and its owner count commit or roll back together;
- failed and in-flight executions resume only under a valid higher attempt;
- exact terminal replay is idempotent and completed evidence cannot reopen;
- tenant export/destruction and catalogue coverage include the new evidence;
- focused proof, one PostgreSQL scenario, and one consolidated backend gate
  pass; and
- no Retention, GMA, Organizations, Staff, Access Control, or web change is
  required.

## Verification

- Focused Workspaces retention, execution, lock, EF model, personal-data
  catalogue, tenant-export, and destruction tests: 35/35 passed.
- Exact final Workspaces test assembly: 384/384 passed.
- PostgreSQL provider proof
  `WorkspaceStaffOnboardingRetentionExecutionSerializationIntegrationTests`:
  1/1 passed. It covers old completed-stage migration, same-coordinate waiting
  and reload, unrelated execution concurrency, durable rows, and guarded
  downgrade.
- `pwsh eng/verify.ps1 -SkipRestore`: passed before the final exact-window
  regression tightening. The solution and source-package checks passed; the
  full build completed with zero warnings and zero errors; every migration
  snapshot reported no drift; Workspaces passed 383/383; and the final
  non-Docker integration assembly passed 65/65. The exact final tree was then
  rebuilt by the focused test command and passed Workspaces 384/384.

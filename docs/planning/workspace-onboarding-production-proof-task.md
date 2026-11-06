# Workspace Onboarding Production Proof Task

Status: local, deployed API, and trusted-HTTPS Preview browser proof complete;
real-provider and hosted-orchestrator rehearsal remains
Date: 2026-07-22
Updated: 2026-08-12

## Goal

Prove that a workspace owner and separate applicant identities can complete BunkFy's recipient-bound invitation and reusable QR enrollment journeys through the real modular boundaries and durable infrastructure.

## Boundaries

- Auth owns registration, sessions, and verified account email.
- Organizations owns workspace membership, invitation/enrollment tokens, claims, approval, rejection, expiry, revocation, capacity, and replay decisions.
- Workspaces owns the applicant Staff proposal and constrained access plan.
- Staff owns the durable employment profile.
- AccessControl owns role/profile assignments and property scope.
- PostgreSQL, outbox/inbox, NATS JetStream, and the Worker carry every asynchronous transition.

The proof must not seed applicant memberships, grant test-only roles, inspect module internals to advance workflow state, bypass email verification in production code, or move BunkFy policy into GMA.

## Scenarios

1. A verified owner creates a workspace and properties through public APIs and waits for asynchronous owner access bootstrap.
2. A recipient-bound invitation is issued through the BunkFy facade for a low-privilege profile and one property.
3. A separately registered, verified applicant submits a Staff profile, accepts the invitation, and converges to one Staff record plus the intended property-scoped assignment.
4. The applicant can read the allowed property and cannot read another property or use management permissions.
5. Another identity cannot adopt the invitation or the prepared BunkFy application, and same-subject replay cannot duplicate Staff or access state.
6. A reusable QR enrollment with owner approval remains denied while pending, then converges through the same Staff/access path after approval.
7. Rejection, revocation/disablement, expiry, capacity, and process restart remain fail-closed and replay-safe.

## Verification

- Add a Docker-backed integration proof using distinct Auth accounts, PostgreSQL, NATS, API, and Worker hosts.
- Keep focused unit/contract coverage for edge-state permutations that would make the Docker proof slow or timing-sensitive.
- Run focused tests, the non-Docker suite, and the complete Docker suite.
- Publish backend and root submodule pointers only after local proof is green, then verify exact-commit CI.

## Implemented Findings

- Pre-membership submission now resolves the token's authoritative organization in a fresh dependency-injection scope before scoped repositories and units of work are created.
- Organization admission callbacks use the same authoritative-scope boundary and remain fail-closed for invalid tokens, identities, claims, plans, or verified-email changes.
- Workspaces registers the scope policies for every product permission it can delegate, independent of unrelated Worker module toggles.
- The Worker composes Properties application consumers, and Properties now owns a durable inbox plus PostgreSQL migration for those consumers.
- The Docker proof passes invitation, recipient mismatch, replay, QR approval/capacity, property isolation, management denial, and Worker restart recovery scenarios with separate verified accounts.
- The fast validation runner now executes each solution test project independently so a failed project cannot be hidden by a later successful project.

## Local Evidence

- `eng/verify.ps1 -SkipRestore`: synchronized solution, source-package checks, zero-warning build, migration drift checks, and every non-Docker test project passed.
- `eng/test-docker.ps1 -NoBuild`: 33 passed, 0 failed, 0 skipped in 4 minutes 35 seconds.

## Deployment Gate

The deterministic backend proof is now complemented by a trusted-HTTPS Preview
multi-account browser rehearsal. Before launch, repeat provider-owned delivery,
every enabled external identity flow, and process-control scenarios against the
hosted candidate and production-shaped services.

The root repository now provides mutation-bearing deployed invitation and QR
enrollment verifiers for the API-level, two-account, least-privilege, replay,
approval, and rejection paths. The remaining registration-adapter,
real-delivery, and hosted process-control evidence is defined by the root
`docs/operations/deployed-workspace-browser-rehearsal.md` runbook and must be
captured against the exact candidate deployment.

The automated Preview rehearsal has now registered and verified three clean
identities through private Mailpit capture, then passed invitation and QR
browser journeys with separate applicants, least-privilege property isolation,
approval, pending denial, guarded Worker restart, replay, and explicit cleanup.
The trusted-HTTPS evidence is bound to release
`preview-browser-https-1f3fd75` and passed all 18 checks.

That closes the repository-owned Preview browser and captured-delivery proof
only. Enabled external identity providers, real-provider delivery, and hosted
orchestrator control remain the private rehearsal described above.

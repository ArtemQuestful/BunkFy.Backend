# Ingestion Deployed Connection Lifecycle Proof Task

Status: implemented and exact-release deployment verified
Date: 2026-08-13

## Goal

Require release-bound evidence for the existing Ingestion control plane:
capability-driven connection management, one-time credential issuance,
independent remote-adapter authentication, terminal empty-run handling,
credential revocation, and safe connection disablement.

## Architecture

- Ingestion remains authoritative for connection configuration and state,
  optimistic versions, mutation journals, ingress credentials, remote leases,
  runs, health, and minimized operational projections.
- Adapter abstractions remain authoritative for execution-mode, descriptor,
  lease, and run contracts. Deployed orchestration discovers a registered
  `RemotePolling` descriptor instead of naming a project adapter.
- Data Governance remains authoritative for country-policy admission. The proof
  requires approved processing and does not activate or rebind policy.
- Workspaces, Access Control, and Auth continue to own tenant lifecycle,
  permission and scope decisions, subjects, sessions, and configured assurance.
- The BunkFy root operations layer owns deployed orchestration, synthetic
  terminal cleanup, minimized evidence, Preview composition, and
  production-admission policy.
- GMA already provides the generic framework contracts required by this flow;
  no GMA source or submodule pointer change is required.

## Delivery

The root verifier will create one synthetic `RemotePolling` connection, exercise
exact replay, changed operation reuse, optimistic stale-write rejection, opaque
secret-reference replace and clear semantics, disable and enable controls, and
one-time credential issuance. It will authenticate one zero-observation lease,
complete its run, revoke the credential, prove the old token is rejected, and
leave the connection disabled.

Passing evidence will exclude every tenant and domain identifier, adapter and
source names, credential and token material, opaque references, policy values,
checkpoints, response bodies, and raw headers. Existing module behavior remains
unchanged unless the deployment proof finds a concrete contract defect.

## Verification Cadence

Use focused root fixtures while changing the verifier. Run the complete root
operations gate once at the coherent slice boundary, then run one exact-release
Preview rehearsal across the edge, API, PostgreSQL, Ingestion, country-policy,
assurance, access, and independent adapter-auth boundaries. Do not repeat the
backend Docker suite for root-only operations script changes.

## Deferred

- provider records, observations, receipts, proposals, and checkpoints already
  covered by the separate deployed AdapterHost proof;
- polling schedule and checkpoint-reset controls;
- tenant/global ingress emergency controls;
- production secret-manager and orchestrator rotation evidence;
- approved production country-policy decisions;
- hosted-production execution and private approval; and
- framework extraction without a second generic consumer.

## Acceptance

- Focused verifier and production-admission fixtures pass.
- The complete root operations gate passes once.
- One exact-release Preview rehearsal leaves its synthetic credential revoked,
  run terminal, and connection disabled and produces minimized child evidence.
- Loopback Preview evidence remains explicitly distinct from hosted-production
  proof.

## Completion

The BunkFy root now owns a 32-check public-API verifier, deterministic valid and
token-redisclosure fixture, bounded missing-policy-projection convergence,
Preview composition, closed production-admission source, static guards, and
operator documentation. Exact release
`preview-workspace-access-estate-651107f` passed the deployed connection,
credential, independent-authentication, empty-run, revocation, and terminal
disable workflow, and the complete root operations gate passed once afterward.

No backend runtime or GMA source change was needed. Existing Ingestion contracts
already provide capability-driven connections, stable mutation receipts,
optimistic versions, one-time nonredisclosing credentials, independent adapter
authentication, terminal remote runs, and safe revocation and disablement. The
separate AdapterHost proof remains responsible for provider records, receipts,
provenance, and checkpoints; production policy approval and secret-manager or
orchestrator rotation remain outside this synthetic proof.

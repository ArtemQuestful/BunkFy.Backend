# Inventory Retirement Context Read — 2026-09-05

Status: implementation authorized; local, CI, preview and hosted acceptance pending.
Decision: BFX-UX-ADR-RETIREMENT-CONTEXT-0001 and mandatory A1; coordinator ledger0021.

## Bounded contract

Add GET to the existing exact room and bed retirement request paths under
tenant/property-scoped `inventory.retire`, without requiring Configure.
Verify the projected target, including the bed's parent room, before returning
context. Missing/wrong-property/wrong-parent is404; denied is403; unavailable
required target/process source is5xx, never an invented empty result.

Return target coordinates/state outside a nullable lifecycle-only process
summary. Select active states1/2/3/5 first, otherwise latest initiated retained
attempt by CreatedAtUtc descending then Id descending. Existing by-ID reads
retain access to older history. Map lifecycle directly from persistence, not
from an impact-dependent coordinator. Separately return nullable exact-target
impact and explicit available/unavailable status. Recognized operational
impact failure preserves the process and disables all advisory actions;
cancellation/programmer errors must not become healthy partial responses.
Claims remain bounded to25 distinct reservation IDs plus truncation. These
reads are observational, not atomic availability or command admission proof.

## Safety, privacy and compatibility

No POST rediscovery, command/journal/version/assurance change, GMA change,
audit deletion or generic history engine. Reads perform no transitions,
journal/outbox writes or availability publication. Enforce enabled tenant
scope and endpoint authority of inventory.retire only, no-store and exact
target binding. UI topology/inventory reads and reservation navigation remain
separate composition prerequisites, not additional endpoint permission checks.
Extend executable privacy classification
for bounded actor/reason/reservation references; never log their values.

Retired targets remain inspectable; canceled attempts permit a new confirmed
request only when the exact target is still active. Completed retirement has
no reopen action in the current Spaces workflow. Dated Blocks are the
temporary-closure alternative, not a retirement toggle.

## Verification and rollout gates

Test no attempt, all states, active-first, many retained terminal attempts,
equal-time UUID ties, tenant/property/parent isolation, retired targets,
retire-without-Configure, denied callers, partial impact, cancellation,
truncation/no-store/no-write invariants and generated OpenAPI/client equality.
Inspect existing PostgreSQL indexes and representative query plans before
accepting no new index; preserve the partial unique active-target indexes.

Run real dedicated-QA room/bed completion and blocked Draining-to-Stop,
reload/fresh-device discovery, exact lost-result replay, revision recovery,
origin/back navigation, current-role/target fencing and keyboard/reflow.
Both independent UX/product reviewers must gate the combined meaningful batch.

Deploy additive backend first, then web. New web with old backend fails closed
with useful retry. Web rollback retains additive reads and all audit history;
no schema/data rollback is expected unless measured index evidence requires
a separately reviewed migration. Support distinguishes unavailable, no attempt,
current attempt and older saved process. Release monitoring must retain
sanitized route/status/latency and resume/mismatch observations without reason,
actor or reservation identifiers. Hosted monitoring, support rehearsal,
backup/restore, exact-commit CI and company/security/privacy/legal admission
remain external evidence requirements, not satisfied by this planning note.

### 2026-09-05 measured query-plan limitation

Main receipt BFX-UX-RETIREMENT-CONTEXT-PLAN-RECEIPT-0001 (SHA
6b5510d7273e02ccfb24cfd793d385d28e050491e935bc245cdf95ef41f72e45)
verified all eight expected indexes on PostgreSQL17.5. Session-temporary tables
with100001 rows and20001 exact-target attempts measured custom target-index
plans at23–31ms and forced generic tenant-filter-OR full scans at45–63ms.
Equal-time UUID tie/update-skew and active-first outcomes were checked. This
supports no immediate index/migration for this slice, not hosted capacity:
deployment must verify preparation behavior, actual volume and endpoint latency.
No persistent process data, schema or index was changed by the benchmark.

# Ingestion Module

Status: reservation ingestion workflow, data-rights restore safety, and
tenant-termination export/destruction owner implemented; production termination
activation remains deferred

Ingestion is BunkFy's tenant- and property-scoped control plane for external source adapters, durable observations, normalization, and staff-reviewed change proposals.

The current foundation contains:

- the module project boundaries, PostgreSQL migration, inbox/outbox infrastructure, and optional API/admin front doors;
- scoped adapter connections with retry-safe caller-owned creation and settings
  update identities, conflict policy, write-only secret references,
  configuration references, and checkpoints;
- task-linked source-run state, durable receipt identity, source deduplication, and staff proposal state machines;
- repository-backed observation receipt handling with raw payload storage through the selected GMA file adapter;
- assignment-bound adapter acknowledgement and checkpoint coordination;
- an opt-in GMA TaskRuntime handler bridge and explicit local adapter runner registry;
- execution-time resolution of opaque configuration/secret references into disposable adapter material;
- a provider-neutral adapter capability registry with protocol/schema versions, supported execution modes, and optional polling guidance;
- a hardened `json.file-drop` polling runner with connection-isolated pending/processed/failed areas, strict envelopes, deterministic replay identity, bounded batches, permanent-input quarantine, and post-acknowledgement archival;
- versioned connection-owned polling schedules projected into tenant-scoped GMA TaskRuntime occurrences;
- bounded raw-payload reads and strict `reservation.v1` normalization, including optional minute-precision expected local arrival/departure times;
- durable reservation source links, source ordering, dispatch attempts, and versioned non-PII operational baselines;
- model-owned PostgreSQL invariants for source-link, dispatch, proposal, and
  reprocessing identity, state, version, chronology, result evidence,
  sensitive-history, and anonymisation shapes;
- contracts-only resolution of an exact dispatch correlation to its stable reservation source-link id for cross-module notification projection;
- asynchronous create/change/amend/cancel requests with correlated Reservations outcomes and deferred receipt recovery;
- strict operational-baseline classification of guest-only changes versus allocation-affecting amendments;
- atomic Inventory amendment confirmation/rejection through Reservations, without release-then-create gaps;
- automatic change proposals for suggestions-only policy, unverifiable ordering, and staff revision conflicts;
- proposal-triggered accept attempts, optimistic rejection, idempotent retries, and race-to-stale handling;
- property-scoped proposal lists, separately authorized sensitive proposal detail, and decisions in the authenticated API, Admin API, and confirmation-gated Admin CLI;
- property-scoped connection lifecycle management with immutable adapter identity, versioned future-run settings, and explicit keep/replace/clear secret-reference updates;
- connection create/update/start validation against composed adapter descriptors, plus worker-side descriptor/runner drift rejection;
- factual connection health derived from latest durable run outcome, last success/observation, receipt backlog, and retention backlog;
- paged connection, run, receipt, reprocessing, proposal, and credential
  directories that project minimized list items directly, use one-row
  look-ahead for `HasMore`, and reserve full records for explicit detail reads;
- minimal connection-management, credential-revocation, and proposal-decision
  receipts, with explicit response metadata and `no-store` caching policy on
  authenticated API and Admin API operational surfaces;
- property-scoped, hash-verified raw-payload downloads behind the separate sensitive-data permission;
- durable raw-payload retention deadlines and optimistic two-phase purge ownership across PostgreSQL and object storage;
- independent 90-day terminal normalized-history deadlines, reason-preserving proposal/dispatch redaction, and factual claimable/protected/due/redacted health counts;
- independently releasable property legal holds with audited actors/reasons, retention-query exclusion, and optimistic fencing against concurrent purge/redaction;
- TaskRuntime-backed run enqueue/retry/cancel orchestration in Admin API and Admin CLI;
- safe checkpoint reset for disabled connections and a rebuildable local active-property projection;
- separate scoped permissions for ordinary reads, connection management, run control, raw payload access, sensitive normalized history, and proposal decisions;
- versioned parser capability discovery plus retained-source reprocessing with immutable source receipts, derived lineage, per-output audit, bounded evidence reservations, and TaskRuntime execution;
- exact reservation-linked DataRights discovery with versioned Ingestion-owned source-link coordinates and no raw or fuzzy identity search;
- catalogue-driven DataRights export of the selected provider-evidence graph, including deterministic protected raw-payload and normalized-history chunking plus fail-closed retained-object reads;
- a mandatory tenant-termination owner that exports connection metadata,
  connection-management receipts, non-secret credential metadata, tenant ingress
  controls, run and receipt provenance, reprocessing history, proposals, source
  links, dispatches, holds, retention execution, and minimum anonymisation proof
  under the shared workspace fence;
- a destructive tenant-termination lifecycle that closes local admission,
  blocks on active legal holds and live outbox leases, proves raw-object absence
  before removing receipt rows, deletes the complete owner graph in bounded
  resumable batches, and retains only a closed lifecycle row plus immutable
  PII-free proof;
- an executable [personal-data catalogue](personal-data-catalog.v1.json) and deterministic [resolved inventory](personal-data-inventory.v1.md) covering persistence, application/API boundaries, adapter/parser ingress, raw evidence, credentials, audit data, and the minimal cross-module event.

GMA TaskRuntime owns enqueue state, worker leases, retries, cancellation, timeout, and daemon lifecycle. Ingestion records the linked task run/attempt and source-specific outcome rather than implementing a parallel scheduler. Admin orchestration resolves an Ingestion-owned connection or run first, then delegates execution control to TaskRuntime.

Persisted and remote run failures use bounded stable error codes only. Adapter-local diagnostics may retain a local message for process logs, but free-form provider errors do not cross the remote protocol or enter Ingestion run persistence and API health responses.

The deterministic `fake.http` adapter, hardened `json.file-drop` adapter, strict `imap.reservation-json` mailbox adapter, and local configuration-material provider exercise three polling mechanisms from `src/Adapters`. IMAP uses MailKit behind an adapter-local session boundary, password or OAuth 2 authentication, mandatory TLS outside explicit loopback development, HMAC-authenticated attachment bytes, read-only bounded MIME retrieval, and UIDVALIDITY-aware checkpoints. Signed malformed/future mail becomes replayable retained evidence, while unsigned or incorrectly signed mail is retained as non-reprocessable untrusted evidence; neither blocks later UIDs. `BunkFy.Parsers.ReservationMail` can replay only trusted-unparsed evidence through the same strict envelope reader; parser metadata is present in API/Admin hosts and executable code is Worker-only. The dependency-light `BunkFy.Adapters.Http` client exercises the authenticated external push boundary against the same shared observation contract without referencing Ingestion internals. `BunkFy.Adapter.Runtime` and the executable `BunkFy.AdapterHost` run polling contracts independently with pre-acknowledgement local checkpoint durability, reloadable token/material sources, bounded retry, and non-sensitive local status. Ingestion is composed into API, Admin API, Admin CLI, and Worker hosts. MinIO stores JSON and RFC822 raw payloads, and the worker opts into explicit adapter, parser, and projection groups. Vendor-specific OTA/mail/HTML parsers, remote fleet discovery, and federated workload identities remain deferred; bounded remote lease claim, renewal, observation submission, and completion are implemented.

Descriptor registration is separate from runner registration: control-plane hosts need capability metadata but must not load executable adapter runners. The current registry is host-composed and immutable for the process lifetime. A future remote adapter discovery implementation can replace `IAdapterDescriptorRegistry`; until then, deployments must keep descriptor registrations aligned across API, Admin, CLI, and Worker binaries. A worker rejects a runner whose descriptor differs from its registration.

Connection creation requires one caller-owned operation id, which becomes the
connection id. Exact retries are serialized by the connection transaction key
and return the current bounded connection receipt; reuse for changed normalized
configuration fails with a stable conflict. The connection and its append-only
digest-only operation receipt commit together, so a lost response cannot create
a second connection. Invalid or denied attempts do not reserve the operation id.
See [Ingestion Connection Creation Idempotency Task](../../../docs/planning/ingestion-connection-create-idempotency-task.md).

Connection settings updates likewise require one caller-owned operation id per
logical edit. Exact retries retain the original expected version and return the
current bounded receipt without another aggregate event; changed reuse fails
with a stable conflict. Keep, replace, and clear secret-reference intents are
fingerprinted without persisting raw references, and a valid no-change update
still records its successful operation receipt. See
[Ingestion Connection Update Idempotency Task](../../../docs/planning/ingestion-connection-update-idempotency-task.md).

Enable, disable, polling-schedule configuration and clearing, and checkpoint
reset also require one caller-owned operation id per logical action. The
existing connection lock serializes receipt lookup with aggregate mutation;
exact retries return before another transition, while changed reuse conflicts.
Lifecycle admission is rechecked on every attempt, enabling rechecks country
policy, schedule configuration rechecks adapter capability, and a disable
replay returns before it can inspect or cancel a later remote run. See
[Ingestion Connection Control Idempotency Task](../../../docs/planning/ingestion-connection-control-idempotency-task.md).

Checkpoint reset additionally requires explicit confirmation at the application
command boundary and configured recent-authentication assurance at the public
API. The confirmation check runs before admission or journal access and does not
become part of mutation identity, so an unconfirmed attempt reserves nothing and
a browser can retry the exact operation after step-up. Tenant ingress resume is
independently assurance-protected because it releases an emergency stop;
suspension remains immediately available for containment. Credential issuance
and revocation keep their existing assurance, while ordinary connection controls
remain unchanged. See
[Ingestion Sensitive-Control Assurance Task](../../../docs/planning/ingestion-sensitive-control-assurance-task.md).

Polling minimum/recommended intervals remain provider capability metadata, while each polling connection may separately own an explicit interval and retry limit. Ingestion persists that desired schedule and exposes it through a dynamic GMA `ITaskScheduleProvider`; TaskRuntime owns occurrence deduplication, leases, retries, and multi-worker execution. The trusted schedule reader crosses tenant query filters only to project enabled connection ids, tenant ids, cadence, and retry limits into tenant-scoped tasks. It does not expose adapter configuration or secret references.

Disabling a connection pauses schedule emission without deleting its configuration; re-enabling resumes it. Clearing is an explicit optimistic operation, and a schedule must be cleared before changing away from polling mode. Ingestion permits only one active source run per connection, guarded both before start and by a filtered PostgreSQL unique index so concurrent scheduler/manual starts fail closed.

Push is an ingress mode, not a task-run mode. Admin enqueue and task start reject push connections before creating Ingestion run state. The authenticated adapter-ingress endpoint feeds the same durable receipt path directly and accepts only `BunkFy-Adapter` credentials bound to the route tenant and push connection; staff JWTs are not adapter authority.

Ingress credentials are Ingestion-owned rotation records, not provider credentials and not general login accounts. A credential token contains 256 bits of random secret material, is returned once with `Cache-Control: no-store`, and is persisted only as a versioned SHA-256 digest. Credentials have bounded expiry, independent revocation, coarse last-authentication evidence, and five race-safe active slots per connection. PostgreSQL constrains digest shape, lifecycle completeness, expiry ordering, tenant-qualified connection ownership, and active-slot uniqueness. Issuance and revocation require caller-owned operation ids and commit immutable receipts with the credential mutation. An exact issuance retry returns `AlreadyIssued` and no token; after an uncertain first response, the operator must revoke the visible credential and issue a replacement. Management API, Admin API, and Admin CLI use the separate `ingestion.credentials.manage` permission; Admin revocation requires confirmation. See [Ingestion Credential Mutation Idempotency Task](../../../docs/planning/ingestion-credential-mutation-idempotency-task.md).

Direct submissions are bounded to 100 records, 16 MiB of decoded payload, and a 24 MiB HTTP body. Authentication happens before batch validation, then every record uses the existing operation/source deduplication and raw-payload path. A replay receives durable `Duplicate` results. Disabling the connection makes otherwise valid submissions receive per-record rejection. Direct ingress never accepts a run id, lease id, or checkpoint.

Source-specific adapters do not belong in this module. Local and future remote adapters use `BunkFy.Adapter.Abstractions`; Ingestion turns their observations into durable receipts and normalized product operations.

HTTP downloads are forced to opaque attachments with cache prevention and content sniffing disabled. The Admin CLI requires `--yes`, writes through a same-directory temporary file, and does not replace an existing file unless `--overwrite` is also supplied.

Raw payloads default to a 30-day retention period configured by `Ingestion:Retention:RawPayloadRetention` (valid range: one hour through ten years). Each receipt stores the deadline assigned when it is accepted, so later configuration changes do not silently rewrite existing retention obligations. Only processed or rejected receipts can be claimed; pending/applying proposals and non-expired reprocessing reservations keep source evidence out of the claim query. Reprocessing reservations expire automatically and are not substitutes for legal holds. The `purge-expired-raw-payloads` TaskRuntime job uses a durable claim before deleting MinIO content and finalizes the receipt afterward; the same task retry can resume immediately, another task can recover a stale claim, and an already-missing object is successful idempotent deletion. Admin API and CLI enqueue this tenant-scoped job behind `ingestion.retention.manage` and explicit confirmation.

Automatic Retention executions additionally fence every owner mutation by the
exact current attempt under one tenant-qualified transaction lock. Attempt
advance, raw claim, raw completion, sensitive-history redaction, and terminal
completion serialize on that stable execution coordinate before source-graph
locking or candidate discovery. Manual TaskRuntime maintenance jobs retain
their coordinate-free owner-idempotency path. See
[Ingestion Retention Attempt Fencing Task](../../../docs/planning/ingestion-retention-attempt-fencing-task.md).

DataRights uses exact product reservation or Ingestion-owned reservation
source-link coordinates only; no raw-provider or fuzzy identity search is
available. A selected coordinate streams its reachable source link, receipts,
proposals, dispatches, reprocessing lineage and retained raw evidence;
unrelated connections and property operations are excluded. A narrow
projection contract resolves scope, property, connection, operation, and
receipt through the exact persisted dispatch and returns only the stable
source-link id. It deliberately preserves identity resolution after source-link
anonymisation so late provider-attention events hit the closed notification
reference instead of recreating history.
Available raw objects are emitted as deterministic bounded chunks, a missing
available object makes the fragment unavailable, and a purged object is never
reconstructed. Proposal diffs, source baselines, and dispatch snapshots use
the same bounded reconstruction pattern rather than risking the shared export
field limit. Destructive DataRights execution remains intentionally
unregistered. The module now registers protected restore only: immutable
owner plans, keyed anti-resurrection fingerprints, local tombstones, ordinary
ingress/dispatch/reprocessing/raw/discovery/export barriers protect ordinary
surfaces. Host readiness remains closed while DataRights startup
reconciliation replays completed owner proofs after database restore.
PostgreSQL and object storage remain a two-phase state machine; reducing
tombstones are unhealthy until every planned object is absent and final
receipt state is committed. See
[Ingestion Data Rights Workflow Task](../../../docs/planning/ingestion-data-rights-workflow-task.md).

Tenant termination is deliberately a different export surface. It contains
portable adapter configuration metadata and normalized operational history,
but excludes secret references, connection request fingerprints, credential
hashes and hash algorithms, raw payload bytes, inbox/outbox state, projections,
checkpoints, source-operation locks, global ingress control, anonymisation plans,
and anonymisation fingerprints. Raw
payload bytes remain available only through the separately authorized
subject/evidence export while the retention policy still permits them.
Relational writes, direct credential expiry/telemetry updates, and export use
the shared BunkFy tenant-mutation transaction key; a frozen Workspaces fence
therefore serializes export against late Ingestion writes. Owner proof is
append-only in EF and independently protected by PostgreSQL triggers.
Tenant revision rows are GMA scope-classified entities, so another tenant can
never select or advance the revision used as export proof. The existing
termination owners also use distinct typed DI registrations; one composition
guard assembles all eight current phase and export contributors in the same
host.

Destruction reuses that tenant lifecycle and exclusive mutation lock. An
active Ingestion legal hold blocks before closing or external deletion starts;
released holds are ordinary tenant history and are removed. Raw payloads are
deleted in bounded groups outside database transactions and read back to prove
absence before their receipts can advance to `Purged`. A crash at either side
of that storage/row boundary safely repeats the same deterministic work. Row
removal then advances through foreign-key-safe stages, including derived
reprocessing lineage, with one non-empty batch of at most 500 records per
call. The deployment-global adapter ingress control is deliberately excluded.
Completion leaves only the closed tenant revision and an append-only receipt
with separate record and object proof chains. Personal-data catalogue version
11 classifies that minimum control-plane proof and the bounded operator
responses under their own access, retention, and rights policies. See
[Ingestion Tenant Termination Owner Task](../../../docs/planning/ingestion-tenant-termination-owner-task.md).

Tenant normalized-history bodies are deterministic 12,000-byte UTF-8 chunks.
This keeps individual fields bounded, but the current Data Rights contract
still stores one protected fragment per owner. A tenant with sufficiently
large retained Ingestion history can exceed that fragment ceiling. Production
termination task and route registration remain disabled until bounded
multi-fragment owner output or an equally explicit volume policy is designed
and proven; increasing the global limit is not an accepted substitute.

New evidence is written under a deterministic receipt key before the PostgreSQL transaction commits. This avoids acknowledging a receipt whose evidence was never stored and makes command retries idempotent, but a process crash can leave an unreferenced object. Production launch therefore still requires a bounded, grace-period orphan reconciliation job built on a provider-neutral GMA storage-inventory capability; Ingestion must not depend directly on MinIO listing APIs.

Normalized proposal diffs and reservation dispatch snapshots have an independent terminal retention policy configured by `Ingestion:Retention:SensitiveHistoryRetention`, defaulting to 90 days with the same bounded range. Pending/applying proposals, pending dispatches, and accepted cancellations have no deadline and keep their evidence. Every genuine terminal transition persists its deadline; `redact-expired-reservation-history` later nulls only the sensitive body and retains reason code, state, actor, decision reason, correlation, revisions, errors, and lifecycle timestamps. Proposal list responses never include diff bodies. Detail reads state whether sensitive history is available or redacted.

Data Rights may reduce a terminal source graph before that automatic deadline,
but only as one complete owner transition: reservation/provider identifiers and
sensitive bodies are cleared together with matching redaction/anonymisation
evidence. The migration adopts the older SQL-only reason and retention checks
into model metadata without stealing their historical ownership. It does not
repair malformed hosted rows; release rollout must preflight the target data,
and a named constraint failure requires an operator-approved repair. See
[Ingestion Source Graph Authoritative State Integrity Task](../../../docs/planning/ingestion-source-graph-authoritative-state-integrity-task.md).

Automatic schedule provisioning is intentionally not global: the current platform has no authoritative tenant-enumeration contract. Deployments must enqueue or schedule one scoped occurrence per tenant until that contract exists. Source links retain only arrival, departure, and sorted inventory-unit identity in strict versioned JSON.

Property legal holds are separate permanent audit records protected by `ingestion.legal-holds.manage`. Any active hold excludes the property's otherwise-eligible raw payloads and normalized history in the candidate SQL itself. Overlapping holds remain effective until the final release. Hold changes and retention batches share an optimistic property fence, while placement fails if an external raw-payload deletion is already in progress. Health reports active-hold and held-backlog counts but never reasons. The adapter-owned `json.file-drop` quarantine is outside these holds because malformed node-local input may not have a trustworthy property identity; it uses a separate bounded local retention policy, while export and audited quarantine holds remain deferred.

Connection health keeps its operational state factual: `NoActivity`, `RunActive`, each latest terminal run outcome, `ObservationsReceived`, and `Disabled` come from Ingestion-owned records. For enabled scheduled polling connections it also reports the configured cadence, an immediately due first run after configuration, later expected starts derived from the latest run start, and whether that time is due. `RunExpected` is timing evidence for operators, not an invented unhealthy verdict or proof that a scheduler instance is alive; quiet push connections remain cadence-free.

Health also reports whether the current host knows the connection's adapter descriptor and still supports its execution mode, together with protocol/configuration schema versions. This detects deployment composition drift without claiming that a registered adapter is operationally healthy.

Focused Docker coverage proves PostgreSQL, MinIO, JetStream, exact deduplication, worker-downtime recovery, automatic reservation creation, accepted and rejected allocation amendments, fresh worker restarts, the staff-conflict proposal path, real-token property-scoped connection management and health, separately authorized raw-payload retrieval, retention migration backfill, active-proposal evidence protection, legacy PII baseline reduction, normalized-history redaction and constraints, source-graph authoritative-state constraints and guarded rollback after early rights-driven anonymisation, overlapping legal holds and fence conflicts, protected-ledger restore replay, anti-resurrection barriers, physical object purge, concurrent one-time adapter credential issuance and idempotent revocation, tenant/connection denial, direct push acceptance/replay, standalone and remote-leased runner delivery plus checkpointing, queued JSON file-drop receipt/archive/quarantine behavior, real SMTP-to-IMAP reservation acquisition and poison-message progress through GreenMail, tenant-termination export across all 16 streams with cross-tenant revision isolation and frozen-write serialization, crash-safe tenant destruction across raw objects and derived/source receipt lineage, and Admin API/CLI confirmation. Vendor-specific connectors, federated workload identity, remote fleet discovery, orphan reconciliation, irreversible anonymisation execution, and broader operational workflows remain later slices.

Retained rejected evidence can be parsed again without reopening the source receipt; the durable attempt, lineage, and retention-fence contract is in [Ingestion Source Reprocessing Task](../../../docs/planning/ingestion-source-reprocessing-task.md).

See [Ingestion Module Task](../../../docs/planning/ingestion-module-task.md) and [Ingestion Adapter Boundary](../../../docs/architecture/ingestion-adapter-boundary.md).

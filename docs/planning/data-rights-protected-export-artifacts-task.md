# Data Rights Protected Export Artifacts Task

Status: complete; repository and exact-release Preview verification passed

## Goal

Turn approved `AccessExport` cases into bounded, encrypted, short-lived
artifacts that an authorized operator can generate and download without moving
owner data or hospitality semantics into Data Rights, GMA, or the object
storage adapter.

The first production path covers:

- property-scoped `GuestRights` cases using the existing Guests,
  Reservations, Ingestion, and Inventory export contributors; and
- tenant-scoped `StaffRights` cases using the Staff export contributor.

Tenant termination, correction, restriction, and destructive execution are not
part of this task.

## Ownership Boundary

Owner modules remain authoritative for:

- subject discovery and current-version revalidation;
- the records and fields included in their fragment;
- export schema and personal-data catalogue versions; and
- bounded owner-local reads.

Data Rights owns:

- approved-case and selected-coordinate validation;
- contributor resolution and deterministic artifact assembly;
- artifact lifecycle, expiry, storage references, and integrity proof;
- generation and download permissions;
- fresh-authentication requirements;
- generation and download audit facts; and
- cleanup coordination.

GMA supplies generic task execution, authorization scope, authentication
assurance, time, identity, and `IFileStorage` primitives. The GMA Files HTTP
module is intentionally not used because these artifacts require
product-owned cross-user authorization, retention, and delivery. No GMA source
change is required.

## Eligibility And Idempotency

Generation is admitted only when:

- the case belongs to the current tenant and route scope;
- the case is approved with `RequestValidated`;
- `AccessExport` is the only requested operation for the initial path;
- the decision revision and selected set are present and immutable;
- every selected owner has exactly one export contributor for the case type;
- every selected coordinate still has its approved record version; and
- the caller has the scoped `data-rights.export` permission plus configured
  recent authentication.

The request carries a bounded caller idempotency key and expected case version.
One artifact identity is allowed for one case decision revision and selected
set. Equivalent retries return the existing artifact. Reusing the key or case
revision for different input fails closed.
A failed, unexpired artifact may be retried with a fresh caller idempotency
key only after the handler revalidates the same immutable case snapshot. This
keeps retry recoverable after a browser reload without permitting a second
artifact or changed scope.

Task payloads contain only tenant-safe artifact and case coordinates. They do
not contain subject ids, lookup criteria, previews, exported fields, or contact
data.

## Artifact Aggregate

Persist a small `DataRightsExportArtifact` aggregate containing:

- artifact id, tenant, case id, case type, and nullable property scope;
- decision revision, selected-subject count, and idempotency identity;
- requested, generation, availability, expiry, and deletion timestamps;
- lifecycle state and bounded failure code;
- opaque storage key, encrypted byte length, plaintext SHA-256, and format
  version;
- active encryption-key version; and
- generation actor attribution and optimistic version.

The request timestamp and expiry are frozen when the artifact is requested.
The generation timestamp is frozen on the first durable generation claim and
is preserved across retries, so the same immutable input produces stable
document metadata.

The database never stores fragment values, subject previews, lookup criteria,
file bytes, or raw owner errors. The storage key uses a tenant digest and
opaque artifact id rather than a raw tenant, property, case, or subject id.

Generation and download audit entries are append-only, bounded, and contain
artifact/case coordinates, actor attribution, outcome code, and timestamp.
They do not contain exported data or free text.

The encrypted object is retained for 24 hours by default. Bounded lifecycle,
integrity, and access-audit proof follows the approved Data Rights case/proof
retention policy instead of pretending the append-only audit rows disappear
with the ciphertext.

## Assembly And Protection

Selected coordinates are ordered by owner, record type, and record id.
Contributors are resolved by case type and owner and are invoked sequentially
through the existing transient export sink contract.

The assembler writes one deterministic JSON document to a bounded temporary
file. It contains:

- a format/version header;
- case kind and scope type without exposing the internal tenant id;
- generation and expiry timestamps;
- one contributor descriptor per owner;
- ordered subject coordinates and owner records;
- field values emitted by the owner contributor; and
- final subject and record counts.

The plaintext SHA-256 is calculated after the complete JSON document is
assembled. It is persisted as protected artifact metadata and authenticated
during storage readback; it is not embedded in the document it hashes.

The whole artifact is rejected if any contributor is missing, duplicated,
stale, over limit, throws, or returns a non-success result. Partial output is
never stored.

Before object storage, a Data Rights-owned chunked authenticated-encryption
adapter protects the complete document with a versioned key. Artifact id,
format version, tenant digest, case id, decision revision, and expiry are
authenticated as associated data. Production startup rejects missing,
development, duplicate, or malformed key material.

The encrypted object has a deterministic key, a bounded maximum size, and
PII-free storage metadata. A retry after object storage succeeds but database
completion fails safely overwrites the same object and revalidates its
integrity before marking the aggregate available.
Failed or cancelled generation attempts delete any partially written
deterministic object before the durable task is retried.

## Delivery And Expiry

Artifact metadata is readable only through the matching property or tenant
case route. Generation requires `data-rights.export`. Download requires
`data-rights.export.download` and fresh MFA or two-step authentication.
Permission and assurance are re-evaluated for every request.

Downloads:

- are streamed only through the Data Rights API;
- decrypt and authenticate the complete artifact before returning plaintext;
- use `application/json` and attachment disposition;
- set `Cache-Control: no-store`, `Pragma: no-cache`, and
  `X-Content-Type-Options: nosniff`;
- never expose object-store or presigned URLs; and
- append a success or denied download audit fact.

Expired artifacts fail closed immediately even if physical cleanup is delayed.
A bounded scheduled cleanup marks expired artifacts, deletes their object, and
records deletion completion. A failed delete remains retryable and does not
make the artifact downloadable again.

An available artifact completes an approved access-export-only case. A failed
generation leaves the case approved so an authorized operator can retry after
the fault is corrected. A completed or expired artifact is not regenerated
from the same case; a new subject request uses a new case and fresh owner
versions.

## API And Operator Surface

Add matching property and tenant endpoints for:

- request-or-return export generation;
- read artifact status;
- download an available artifact; and
- retry a failed generation with the same immutable case input.

The operator page must make Guest and Staff scope explicit. It shows generation
progress and expiry without displaying object keys or internal error details.
Staff discovery accepts exact Staff id or exact Auth subject id only. The UI
does not offer erasure, correction, or restriction for `StaffRights`.

The UI starts after backend generation, download, expiry, and assurance tests
are green. It must not expose a workflow that stops at approval.

## Security And Efficiency

- Owner fragments are streamed; they are never assembled in an unbounded
  in-memory object graph.
- Plaintext exists only in bounded delete-on-close temporary files and buffers
  that are cleared when practical.
- Artifact, owner-record, field, and byte limits are enforced independently.
- Contributor calls are sequential because module DbContexts are scoped and
  not thread safe.
- Generation runs as one durable task with a deterministic deduplication key.
- Listing reads only artifact metadata and never opens object storage.
- Download performs one indexed metadata read and one object read.
- Logs, metrics, traces, tasks, events, notifications, URLs, and support output
  remain PII-free.
- Production object storage remains private and transport encrypted.
  Application-level authenticated encryption prevents storage operators or
  accidental bucket exposure from revealing plaintext.

## Implementation Slices

1. [x] Add the artifact aggregate, persistence migration, bounded assembler,
   authenticated encryption, generation task, and focused unit tests.
2. [x] Add property and tenant generation/status/download endpoints,
   assurance, append-only audit, expiry cleanup, and focused API/integration
   tests.
3. [x] Add the Guest/Staff operator workflow, live status refresh, download
   step-up handling, and focused frontend/browser tests.
4. [x] Run the complete candidate gates once, publish in dependency order,
   and verify GitHub Actions for the exact published commits once.

Only one numbered implementation slice is active at a time.

## Verification Cadence

During implementation run only focused Data Rights, affected architecture,
migration-drift, contract, and frontend tests.

At the completed-task gate run:

1. one complete non-Docker backend verifier;
2. one complete backend Docker gate, including PostgreSQL plus MinIO;
3. one complete web verifier and focused browser path;
4. one root preview/guard gate; and
5. exact-candidate GitHub Actions once after publication.

If a full gate fails, fix the collected batch with narrow checks and repeat the
full gate once. Documentation-only evidence updates do not repeat unchanged
expensive gates.

## Slice 1 Evidence

- Added the tenant-scoped artifact aggregate, idempotent request and generation
  commands, durable task event/payload, repository, and PostgreSQL migration
  `20260727062331_AddProtectedExportArtifacts`.
- Added deterministic, bounded owner-fragment assembly and chunked AES-256-GCM
  protection with versioned keys, context binding, opaque storage coordinates,
  post-write authenticated readback, and production option validation.
- Added database constraints for scope, lifecycle state, generation metadata,
  failure metadata, subject count, decision revision, and optimistic version.
- Updated the Data Rights personal-data catalogue to version 12 and regenerated
  its checked-in inventory for operator attribution and artifact digests.
- Focused export, catalogue, persistence-model, and protection checks passed:
  32 tests, including in-memory encrypt/store/readback and tamper rejection.
- The Data Rights PostgreSQL migration project builds with zero warnings and
  `has-pending-model-changes` reports no drift.
- GMA source was not changed. The implementation consumes the existing generic
  `IFileStorage`, task runtime, clock, and scoped module primitives.
- No operator UI is exposed yet; that remains slice 3.

## Slice 2 Evidence

- Added matching property and tenant generation, status, and protected-download
  routes with separate generation and download permissions.
- Production host policy requires privileged recent authentication for
  generation and fresh MFA or two-step authentication for download.
- Downloads verify the complete encrypted object, authenticated context,
  key/format versions, byte lengths, and plaintext SHA-256 before release.
  Expiry is checked both before and after verification.
- Added bounded append-only audit facts for generation, denied and released
  downloads, expiry, and deletion. PostgreSQL also rejects audit-row update or
  delete operations with a database trigger.
- Added durable frozen-expiry cleanup, idempotent lifecycle retries, deletion
  after partial/cancelled generation, and deterministic orphan cleanup when
  database completion loses a race.
- Plaintext and encrypted staging use bounded delete-on-close files in a
  process-private random directory, with owner-only Unix modes.
- Updated the Data Rights executable personal-data catalogue to version 13 and
  separated short-lived ciphertext retention from longer-lived bounded
  lifecycle and access proof.
- The final focused backend gate passed 60 tests. All 14 module-boundary tests
  passed during the slice review, the PostgreSQL migration project built
  without warnings, and the final model reported no pending migration changes.
- No complete backend verifier, Docker gate, web verifier, or GitHub Action was
  run during this implementation slice. Those remain the single final
  candidate gate after the operator workflow.
- GMA source and all nested GMA pointers remain unchanged.

## Slice 3 Evidence

- Added explicit Guest and Staff request scopes. Guest cases remain
  property-scoped; Staff access-export cases are tenant-scoped and do not
  depend on a selected property.
- Added operation-aware request labels, progress, creation, exact discovery,
  review, decision, and terminal states. Staff discovery accepts only an exact
  Staff id or exact account subject id and never exposes destructive actions.
- Added protected-artifact status, generation, retry, expiry, and download
  UI. Polling runs every two seconds only while either the case, execution
  work, or artifact is non-terminal and never runs in the background.
- The artifact panel continues reading the completed case after generation,
  while the generate command itself remains available only for an approved
  case. This preserves download access after the case becomes completed.
- Generation supports password confirmation through the existing GMA browser
  step-up endpoint. Download handles the configured fresh-MFA requirement
  without exposing object keys, storage details, or raw generation failures.
- Added BunkFy account administration for TOTP enrollment, activation,
  one-time recovery-code display, status, and authenticated disable. Password
  and external-provider login now share one browser MFA challenge form, and
  external-provider sign-in correctly handles GMA's accepted MFA challenge
  response.
- The current account page bounds long active-session history to a content
  scroll area so security controls are not pushed arbitrarily far down the
  page.
- Focused frontend verification passed: 9 workflow/authentication tests,
  affected-file ESLint, TypeScript type checking, diff validation, and a
  production Vite build.
- Live browser review passed for Guest and Staff queues, both creation forms,
  a case detail, account security, and 390 px responsive layouts. The checked
  pages had no horizontal overflow.
- The disposable preview first received only the current static web bundle for
  this review. The backend was rebuilt once at the final candidate preview gate
  for the end-to-end protected generation and download-assurance walkthrough.
- No complete backend verifier, backend Docker gate, complete web verifier, or
  GitHub Action was run during this slice.
- GMA source and all nested GMA pointers remain unchanged.

## Final Candidate Evidence

- The complete non-Docker backend verifier passed after one bounded correction:
  the export aggregate's retention and deletion transitions were moved to a
  dedicated partial file to satisfy the handwritten-domain-file size guard.
  The repeated final gate passed with a zero-warning build, synchronized
  solution, source-package checks, all PostgreSQL and SQL Server migration
  drift checks, all unit and architecture suites, and 30 non-Docker BunkFy
  integration tests.
- The complete web verifier passed TypeScript, full ESLint, all 118 Vitest
  tests across 18 files, and the production Vite build.
- Root security, latest-submodule, solution, operations-script, preview
  Compose, OpenAPI snapshot, and generated TypeScript contract guards passed.
- The one complete Docker gate passed all 63 container-backed integration
  tests in 8 minutes 58 seconds.
- Added a product-owned Docker integration scenario that generates a protected
  Staff export through the public Data Rights ports, persists and reloads its
  lifecycle metadata in PostgreSQL, stores encrypted bytes in MinIO, and
  decrypts and verifies the original content. This orchestration remains in
  BunkFy; GMA's generic MinIO contract continues to be tested independently.
- The final preview found two environment-realistic defects that the isolated
  tests did not expose: production file-storage policy did not admit the
  encrypted `application/octet-stream` object, and the case execution
  constraint did not admit an approved access-export case completing without
  anonymisation execution metadata. Host startup validation now checks the
  media type and bounded object size, and migration
  `20260727090449_AllowAccessExportCompletionWithoutExecution` admits only the
  exact approved `AccessExport` completion shape.
- Focused host, protection, persistence-model, and compile checks passed after
  those corrections. The PostgreSQL-plus-MinIO scenario now also completes and
  reloads its approved case, but the complete Docker gate was not repeated
  after the narrow correction in accordance with the documented verification
  cadence.
- The rebuilt preview migrated cleanly and recovered the original exhausted
  durable run through the audited Task Runtime retry command. The same artifact
  became available, the case became completed, and the operator UI displayed
  its fixed expiry without exposing its storage key.
- The live download attempt returned `401` and directed the operator to account
  security because the test account had no fresh MFA sign-in. This confirms the
  configured download assurance fails closed; the control was not weakened to
  manufacture a plaintext download.
- Staff request attribution now says `Requested by the staff member`, and
  selected discovery results no longer fall through to the no-match message.
- The first published Docker candidate built successfully and then exposed two
  test-fixture gaps: shared API test hosts replaced the production file-content
  type/capacity defaults, and the protected-export fixture skipped the required
  requester-verification and controller-routing transitions. The test hosts now
  preserve the opaque export type and production capacity, the fixture records
  both prerequisite transitions, and the focused integration-project build
  passes with zero warnings. Production fail-closed validation is unchanged.
- GMA source and all nested GMA pointers remain unchanged.
- Replacement backend commit `0165302f34258feb04a00fab36255211493276ca`
  passed exact-candidate validation in GitHub Actions run `30254840283` and
  the corrected Docker gate in run `30254840230`.
- Root exact-release verification later completed the missing deployed
  assurance path against Preview release
  `preview-workspace-access-estate-651107f`: one controller-initiated Guest
  Access Export passed all 18 checks through API, Worker, PostgreSQL, NATS, and
  MinIO. It proved stable request replay, rejection of a second live artifact,
  MFA-gated download, nonmember denial, strict no-store/attachment response,
  stable downloaded bytes, Guest archival, bounded 24-hour expiry, and release
  continuity. The minimized child SHA-256 is
  `5e03d355f26436960daa2fa4ab4f1a7bb4e3b815fd6f87f99cf3145c7e417f72`.
- The enclosing rehearsal disabled its temporary TOTP factor, revoked all
  sessions, removed joined members, retired properties, archived the workspace,
  and purged captured mail. This is loopback Preview evidence only; hosted
  trusted-HTTPS execution and independent object-store/key custody remain
  production-admission requirements.

## Deferred

- tenant-termination export;
- correction and restriction artifact workflows;
- guest or staff self-service download;
- configurable legal-hold retention beyond short-lived access artifacts;
- archival export formats such as ZIP, PDF, CSV, or machine-provider bundles;
- external delivery by email or third-party transfer service; and
- a reusable GMA cross-module export product abstraction.

# Local Development

Current backend validation:

```powershell
.\eng\verify.ps1
```

Product modules live under `src/Modules` and follow the GMA Skeleton layering conventions without carrying its example domains. Use the existing Properties, Inventory, Reservations, Guests, Staff, and Ingestion modules as BunkFy-specific references.

Regenerate the backend and root Visual Studio solution views after adding, removing, or renaming projects:

```powershell
.\eng\update-solutions.ps1 -IncludeRootWorkspace
```

The generated solution folders reflect actual ownership: BunkFy modules and product extensions stay under `src/Modules` and `src/Extensions`, while mounted framework, reusable extensions, and reusable-module projects stay under their `gma/framework`, `gma/extensions`, and `gma/modules` paths.

If Visual Studio opens only its shell and its activity log reports a missing `ComponentModelCache/Microsoft.VisualStudio.Default.assemblyMetadata`, close every Visual Studio process and run the installed `devenv.com /UpdateConfiguration`. That repairs local Visual Studio package metadata; changing the solution file does not fix that machine-level failure.

The current product-domain map lives in [Product Domain Map](../planning/product-domain-map.md), development flow notes live in [Development Guidelines](../planning/development-guidelines.md), and the current pre-module status lives in [Pre-Module Readiness](../planning/pre-module-readiness.md). Expected early modules include:

- Properties.
- Inventory.
- Reservations.
- Guest records, managed only by operators/staff.
- Billing.
- Housekeeping.

Keep reusable framework changes in the appropriate nested GMA repository and update the backend submodule pointer deliberately after validation.

For backend-only composition, run:

```powershell
.\eng\run-aspire.ps1
```

The original MinIO profile remains the default for existing Aspire data. Its
retired `quay.io/minio/minio:latest` image is unavailable for a fresh public pull;
keep the original environment and any retained image with existing data.
For a **fresh development environment**, explicitly select the reviewed Silo
provider before starting either the backend or root AppHost:

```powershell
$env:AppHost__Storage__Provider = 'Silo'
.\eng\run-aspire.ps1
```

This uses the immutable reviewed Linux amd64 image and the separate
`bunkfy-silo-data` volume. A read-only pre-start check rejects this selection if
`bunkfy-minio-data` exists, before starting infrastructure or migrations. It
never mounts or converts that legacy volume. Do not rename/relabel old volumes
to bypass the check; existing deployments need a separately qualified migration.
Returning to the original profile uses `AppHost__Storage__Provider=Minio`.

Silo receives a generated secret of at least 32 characters, persisted in the
AppHost's existing user-secrets store; API, Admin API and Worker share that same
parameter. Preserve that store with the development environment. An explicit
`Parameters:silo-password` configuration can supply an existing managed secret.
The legacy profile retains its previous default credentials unless its new
`Parameters:minio-password` parameter is configured. These are development
profiles, not production storage admission.

All host projects now receive the actual allocated storage host/port from
Aspire, and wait for its readiness endpoint. The previous literal `minio:9000`
address belongs to a container network and does not describe the endpoint of
projects launched on the developer host.

An independently deployed polling adapter normally uses a `RemotePolling` Ingestion connection and `AdapterHost__CoordinationMode=server-lease`. Give each process a stable non-empty `AdapterHost__WorkerId`, supply the remaining `AdapterHost__*` configuration, keep the one-time ingress token in the configured environment variable or secret file, and run:

```powershell
.\eng\run-adapter.ps1
```

In server-lease mode, Ingestion owns assignment and the durable checkpoint, so the daemon does not need a local checkpoint file and multiple replicas are fenced to one active worker. `local-file` mode remains available explicitly for a Push connection when node-local coordination is intended; it requires a persistent checkpoint directory and one host instance per connection. Local status binds to `http://127.0.0.1:8088` by default.

## Backend Stack Development Note

The baseline backend stack is intentionally already wired before product modules start:

- `BunkFy.Host.Api` composes Tenancy, global-account Auth with the optional TOTP authenticator, Files, Notifications, MinIO file storage, NATS/JetStream messaging, caching hooks, OpenAPI, and service defaults.
- `BunkFy.Host.AdminApi` and `BunkFy.Host.AdminCli` compose Administration, tenant-scoped Auth admin surfaces, TaskRuntime controls, and BunkFy product administration surfaces.
- Administration audit reads use `Administration:Audit:DefaultPageSize` and `Administration:Audit:MaxPageSize`; purge batches use `Administration:Audit:DefaultPurgeBatchSize` and `Administration:Audit:MaxPurgeBatchSize`.
- `BunkFy.Host.Worker` is present from the start. Enabling `AppHost:Worker:Enabled=true` runs the worker with NATS publishing, Auth support, TaskRuntime persistence, and the task worker loop.
- PostgreSQL is the default persistence provider and normal Aspire database. SQL Server remains available only where a composed reusable GMA module retains that provider.
- MinIO is the default storage path for local and development environments. Local disk storage remains only as an adapter/test reference.
- BunkFy is management-only: operators and staff authenticate; guests are PMS records and do not access the system directly.

Development persists ASP.NET Core Data Protection keys under `src/BunkFy.Host.Api/.data`, which is ignored by Git. Production must configure `DataProtection__KeyRingPath` to shared durable storage mounted by every API replica and keep `DataProtection__ApplicationName` stable. The API fails at startup when that production key-ring path is absent because OIDC state and protected Auth authenticator secrets must survive restarts and replica changes.

Ingestion anonymisation fingerprints also require an explicit production
keyring. Configure the same
`Ingestion__AnonymisationFingerprints__ActiveKeyVersion` and versioned
`Ingestion__AnonymisationFingerprints__Keys__<version>` values in API, Admin
API, Admin CLI and Worker deployments. Every value is a secret,
base64-encoded 32-byte key. Production rejects missing keys and the development
key at startup. Add a new version before making it active, and retain every
old key while any fingerprint created with that version remains; removing it
would reopen that provider identity to ingestion. Keep these values in the
deployment secret store, never appsettings or source control. A live reduction,
a protected replay reduction, or an attached ledger proof that has not finished
local verification reports
`ingestion.anonymisation-restore.incomplete` through host readiness until
the persisted owner state is complete.

Keep first product work focused on real BunkFy modules, starting with Properties/Inventory-style setup and only adding task handlers when a concrete module needs background work.


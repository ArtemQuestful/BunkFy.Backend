# retention Personal-Data Inventory v4

Generated from `retention.personal-data` schema v1.
Catalogue approval: `engineering-default`.

Engineering metadata is not legal or country-launch approval.

## Access Policies

| Id | Scope | Readers | Writers |
|---|---|---|---|
| retention-control-plane | tenant-retention-schedule-and-execution-coordinates | permission:retention.read<br>permission:retention.retry<br>system:retention-scheduler<br>system:retention.tenant-termination-export | system:retention-scheduler<br>system:retention-scope-projections |
| retention-tenant-destruction-proof | tenant-termination-control-and-accountability-proof | permission:data-rights.tenant-termination<br>system:authorized-audit-consumer<br>system:tenant-termination-coordinator | system:authorized-tenant-termination-owner |

## Retention Policies

| Id | Approval | Starts | Ends or duration | Legal hold |
|---|---|---|---|---|
| retention-control-plane-evidence | engineering-default | retention-scope-or-execution-recorded | tenant-termination-and-approved-pseudonymization | retain-minimum-required-schedule-and-execution-evidence |
| retention-tenant-destruction-proof | engineering-default | approved-retention-tenant-destruction-started | approved-minimum-termination-proof-retention-completed | retain-minimum-required-termination-proof |
| retention-tenant-termination-export-fragment | engineering-default | authorized-tenant-export-started | authorized-tenant-export-completed | not-applicable |
| transient-owner-execution-request | engineering-default | owner-retention-contribution-requested | owner-retention-contribution-completed | not-applicable |

## Rights Policies

| Id | Export | Correction | Restriction | Erasure |
|---|---|---|---|---|
| retention-scope-coordinate | include-in-authorized-operational-retention-export | correct-through-authoritative-organization-or-property-workflow | stop-scheduling-when-authoritative-scope-is-inactive | remove-or-pseudonymize-after-approved-tenant-termination-policy |
| retention-tenant-destruction-proof-control | include-in-authorized-tenant-termination-audit | append-a-corrective-control-plane-record | retain-only-the-minimum-owner-proof | remove-after-approved-proof-retention |

## Fields

| Id | Subject | Class | Sensitivity | Purposes | Sources | Owner | Context | Access | Country | Retention | Rights | Surfaces | Boundaries | Approval |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| retention.property-reference | account-holder | linked-operational | elevated | owner-module-execution<br>property-retention-scheduling<br>schedule-health | properties-projection | properties | customer-controller-bunk-fy-processor | retention-control-plane | retention.property-reference | retention-control-plane-evidence | retention-scope-coordinate | api-input<br>api-response<br>application-command<br>application-query<br>data-rights-export<br>integration-command<br>persistence | cross-module<br>customer-api<br>intra-module<br>processor | engineering-default |
| retention.tenant-destruction.owner-proof | subject-scoped | linked-operational | elevated | destruction-accountability<br>owner-proof-correlation<br>tenant-termination-safety | approved-tenant-termination-case<br>retention-owner-destruction | retention | customer-controller-bunk-fy-processor | retention-tenant-destruction-proof | retention.tenant-destruction.owner-proof | retention-tenant-destruction-proof | retention-tenant-destruction-proof-control | persistence | intra-module<br>processor | engineering-default |
| retention.tenant-scope-reference | account-holder | pseudonymous-identifier | elevated | owner-module-execution<br>retention-scope-correlation<br>schedule-health | organizations-projection<br>tenant-execution-context | organizations | customer-controller-bunk-fy-processor | retention-control-plane | retention.tenant-scope-reference | retention-control-plane-evidence | retention-scope-coordinate | application-command<br>application-query<br>data-rights-export<br>integration-command<br>integration-event<br>persistence | cross-module<br>intra-module<br>processor | engineering-default |

## Code Bindings

| Field | Assembly | Type | Member | Surface | Effective retention |
|---|---|---|---|---|---|
| retention.property-reference | BunkFy.Modules.Retention.Application | BunkFy.Modules.Retention.Application.Commands.RetentionRunRetryExpectation | PropertyId | application-command | transient-owner-execution-request |
| retention.property-reference | BunkFy.Modules.Retention.Application | BunkFy.Modules.Retention.Application.Ports.RetentionScheduleTarget | PropertyId | application-query | transient-owner-execution-request |
| retention.property-reference | BunkFy.Modules.Retention.Contracts | BunkFy.Modules.Retention.Contracts.RetentionContributionRequest | PropertyId | integration-command | transient-owner-execution-request |
| retention.property-reference | BunkFy.Modules.Retention.Contracts | BunkFy.Modules.Retention.Contracts.RetentionRunRetryWorkItem | PropertyId | integration-command | transient-owner-execution-request |
| retention.property-reference | BunkFy.Modules.Retention.Contracts | BunkFy.Modules.Retention.Contracts.RetentionScheduleHealthDto | PropertyId | api-response | transient-owner-execution-request |
| retention.property-reference | BunkFy.Modules.Retention.Contracts | BunkFy.Modules.Retention.Contracts.RetryRetentionScheduleRequest | PropertyId | api-input | transient-owner-execution-request |
| retention.property-reference | BunkFy.Modules.Retention.Domain | BunkFy.Modules.Retention.Domain.Aggregates.RetentionExecution | PropertyId | persistence | retention-control-plane-evidence |
| retention.property-reference | BunkFy.Modules.Retention.Domain | BunkFy.Modules.Retention.Domain.Aggregates.RetentionRunRetryRequest | PropertyId | persistence | retention-control-plane-evidence |
| retention.property-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.Repositories.RetentionExecutionTenantExport | PropertyId | data-rights-export | retention-tenant-termination-export-fragment |
| retention.property-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.Repositories.RetentionRunRetryRequestTenantExport | PropertyId | data-rights-export | retention-tenant-termination-export-fragment |
| retention.property-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.Repositories.RetentionScheduleStateTenantExport | PropertyId | data-rights-export | retention-tenant-termination-export-fragment |
| retention.property-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.RetentionPropertyProjection | Id | persistence | retention-control-plane-evidence |
| retention.property-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.RetentionScheduleState | PropertyId | persistence | retention-control-plane-evidence |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | BatchSize | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | CompletedBatchCount | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | ConcurrencyVersion | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | OperationId | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | ProofVersion | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | RemovalProofSha256 | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | RemovedRecordCount | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | RequestSha256 | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | ResultingRevision | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | ScopeId | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | SelectedRevision | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | Stage | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | StartedAtUtc | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyOperation | UpdatedAtUtc | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | BatchSize | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | CompletedAtUtc | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | CompletedBatchCount | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | OperationId | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | RemovalProofSha256 | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | RemovalProofVersion | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | RemovedRecordCount | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | RequestSha256 | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | ResultingRevision | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | ScopeId | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | SelectedRevision | persistence | retention-tenant-destruction-proof |
| retention.tenant-destruction.owner-proof | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.TenantTermination.RetentionTenantDestroyReceipt | StartedAtUtc | persistence | retention-tenant-destruction-proof |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Application | BunkFy.Modules.Retention.Application.Commands.RequestRetentionRunRetryCommand | TenantId | application-command | transient-owner-execution-request |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Application | BunkFy.Modules.Retention.Application.Ports.RetentionScheduleTarget | ScopeId | application-query | transient-owner-execution-request |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Contracts | BunkFy.Modules.Retention.Contracts.RetentionContributionRequest | TenantId | integration-command | transient-owner-execution-request |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Contracts | BunkFy.Modules.Retention.Contracts.RetentionRunRetryRequestedIntegrationEvent | TenantId | integration-event | transient-owner-execution-request |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Contracts | BunkFy.Modules.Retention.Contracts.RetentionRunRetryWorkItem | TenantId | integration-command | transient-owner-execution-request |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Domain | BunkFy.Modules.Retention.Domain.Aggregates.RetentionExecution | ScopeId | persistence | retention-control-plane-evidence |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Domain | BunkFy.Modules.Retention.Domain.Aggregates.RetentionRunRetryRequest | ScopeId | persistence | retention-control-plane-evidence |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.Repositories.RetentionExecutionTenantExport | ScopeId | data-rights-export | retention-tenant-termination-export-fragment |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.Repositories.RetentionRunRetryRequestTenantExport | ScopeId | data-rights-export | retention-tenant-termination-export-fragment |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.Repositories.RetentionScheduleStateTenantExport | ScopeId | data-rights-export | retention-tenant-termination-export-fragment |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.RetentionScheduleState | ScopeId | persistence | retention-control-plane-evidence |
| retention.tenant-scope-reference | BunkFy.Modules.Retention.Persistence | BunkFy.Modules.Retention.Persistence.RetentionTenantProjection | ScopeId | persistence | retention-control-plane-evidence |

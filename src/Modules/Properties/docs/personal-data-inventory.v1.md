# properties Personal-Data Inventory v5

Generated from `properties.personal-data` schema v1.
Catalogue approval: `engineering-default`.

Engineering metadata is not legal or country-launch approval.

## Access Policies

| Id | Scope | Readers | Writers |
|---|---|---|---|
| properties-authorization | tenant-property-access-evaluation | system:properties-access-resolution | system:authenticated-access-context |
| properties-lifecycle-audit | tenant-property-lifecycle-audit | permission:properties.properties.manage<br>system:authorized-audit-consumer<br>system:operations-notifications | permission:properties.properties.manage |
| properties-tenant-destruction-proof | tenant-termination-control-and-accountability-proof | permission:data-rights.tenant-termination<br>system:authorized-audit-consumer<br>system:tenant-termination-coordinator | system:authorized-tenant-termination-owner |
| properties-time-zone-management | tenant-property-time-zone-compliance-and-correction | permission:properties.time-zones.manage<br>system:authorized-audit-consumer | permission:properties.time-zones.manage |

## Retention Policies

| Id | Approval | Starts | Ends or duration | Legal hold |
|---|---|---|---|---|
| integration-message-journal | engineering-default | message-created | message-journal-retention-completed | no-payload-hold |
| properties-tenant-destruction-proof | engineering-default | approved-properties-tenant-destruction-started | approved-minimum-termination-proof-retention-completed | retain-minimum-required-termination-proof |
| properties-tenant-termination-export-fragment | engineering-default | tenant-export-fragment-generation | tenant-export-artifact-expiry-or-deletion | retain-only-with-authorized-tenant-export-artifact |
| property-governance-revision-lifecycle | engineering-default | property-governance-decision-recorded | tenant-termination-or-approved-pseudonymization | retain-minimum-required-audit-evidence |
| property-time-zone-operation-lifecycle | engineering-default | property-time-zone-operation-recorded | tenant-termination-or-approved-pseudonymization | retain-minimum-required-audit-evidence |
| transient-domain-event | engineering-default | domain-event-raised | domain-event-dispatched | not-applicable |
| transient-request | engineering-default | request-accepted | request-completed | not-applicable |
| transient-response | engineering-default | response-created | response-delivered | not-applicable |

## Rights Policies

| Id | Export | Correction | Restriction | Erasure |
|---|---|---|---|---|
| authorization-subject | not-retained-by-properties | correct-through-authoritative-auth-workflow | deny-property-access-when-subject-access-is-restricted | not-retained-by-properties |
| properties-tenant-destruction-proof-control | include-in-authorized-tenant-termination-audit | append-a-corrective-control-plane-record | retain-only-the-minimum-owner-proof | remove-after-approved-proof-retention |
| property-time-zone-operations | include-in-authorized-property-or-tenant-export-when-retained | append-an-authorized-property-time-zone-operation | enforce-property-scope-and-sensitive-management-permission | remove-with-approved-tenant-destruction-after-required-audit-retention |
| staff-audit-attribution | include-in-authorized-staff-audit-or-tenant-export | append-corrective-property-lifecycle-action | retain-minimum-required-audit-and-notification-processing | pseudonymize-subject-when-approved-retention-permits |

## Fields

| Id | Subject | Class | Sensitivity | Purposes | Sources | Owner | Context | Access | Country | Retention | Rights | Surfaces | Boundaries | Approval |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| properties.authorization-subject-reference | account-holder | pseudonymous-identifier | elevated | authorization-scope-resolution | authenticated-access-subject | auth | customer-controller-bunk-fy-processor | properties-authorization | properties.authorization-subject-reference | transient-request | authorization-subject | application-query | intra-module | engineering-default |
| properties.staff-actor-reference | staff | audit-attribution | standard | property-lifecycle-audit-correlation<br>property-time-zone-correction-audit<br>self-notification-suppression | authenticated-access-subject<br>authenticated-admin-actor-context | auth | customer-controller-bunk-fy-processor | properties-lifecycle-audit | properties.staff-actor-reference | integration-message-journal | staff-audit-attribution | admin-input<br>admin-output<br>api-response<br>application-command<br>application-query<br>data-rights-export<br>domain-event<br>integration-event<br>persistence | cross-module<br>customer-api<br>intra-module<br>support | engineering-default |
| properties.tenant-destruction.owner-proof | subject-scoped | linked-operational | elevated | destruction-accountability<br>owner-proof-correlation<br>tenant-termination-safety | approved-tenant-termination-case<br>properties-owner-destruction | properties | customer-controller-bunk-fy-processor | properties-tenant-destruction-proof | properties.tenant-destruction.owner-proof | properties-tenant-destruction-proof | properties-tenant-destruction-proof-control | persistence | intra-module<br>processor | engineering-default |
| properties.time-zone.management-coordinate | subject-scoped | linked-operational | standard | property-time-zone-compliance<br>property-time-zone-correction<br>property-time-zone-operation-recovery | staff-action<br>system-generated | properties | customer-controller-bunk-fy-processor | properties-time-zone-management | properties.time-zone.operations | transient-request | property-time-zone-operations | admin-input<br>admin-output<br>api-input<br>api-response<br>application-command<br>application-query | customer-api<br>intra-module<br>support | engineering-default |

## Code Bindings

| Field | Assembly | Type | Member | Surface | Effective retention |
|---|---|---|---|---|---|
| properties.authorization-subject-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListVisiblePropertiesQuery | Subject | application-query | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.ActivatePropertyProcessingCommand | ActorId | application-command | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.CreatePropertyCommand | ActorId | application-command | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.CreatePropertyCommand | ActorId | admin-input | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.RetirePropertyCommand | ActorId | application-command | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | ActorId | application-command | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | ActorId | admin-input | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SuspendPropertyProcessingCommand | ActorId | application-command | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Ports.PropertyGovernanceRevisionWriteModel | ActorId | application-command | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Ports.PropertyTimeZoneRevisionReadModel | ActorId | application-query | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Ports.PropertyTimeZoneRevisionWriteModel | ActorId | application-command | transient-request |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyRetiredIntegrationEvent | ActorId | integration-event | integration-message-journal |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | Receipt | api-response | transient-response |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | Receipt | admin-output | transient-response |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | ActorId | api-response | transient-response |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | ActorId | admin-output | transient-response |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Domain | BunkFy.Modules.Properties.Domain.Events.PropertyProcessingPolicyActivatedDomainEvent | ActorId | domain-event | transient-domain-event |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Domain | BunkFy.Modules.Properties.Domain.Events.PropertyProcessingSuspendedDomainEvent | ActorId | domain-event | transient-domain-event |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Domain | BunkFy.Modules.Properties.Domain.Events.PropertyRetiredDomainEvent | ActorId | domain-event | transient-domain-event |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.PropertyGovernanceRevision | ActorId | persistence | property-governance-revision-lifecycle |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.PropertyTimeZoneOperation | ActorId | persistence | property-time-zone-operation-lifecycle |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.Repositories.PropertiesGovernanceRevisionTenantExport | ActorId | data-rights-export | properties-tenant-termination-export-fragment |
| properties.staff-actor-reference | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.Repositories.PropertiesPropertyTimeZoneOperationTenantExport | ActorId | data-rights-export | properties-tenant-termination-export-fragment |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | BatchSize | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | CompletedBatchCount | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | ConcurrencyVersion | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | OperationId | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | ProofVersion | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | RemovalProofSha256 | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | RemovedRecordCount | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | RequestSha256 | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | ResultingRevision | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | ScopeId | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | SelectedRevision | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | Stage | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | StartedAtUtc | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyOperation | UpdatedAtUtc | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | BatchSize | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | CompletedAtUtc | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | CompletedBatchCount | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | OperationId | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | RemovalProofSha256 | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | RemovalProofVersion | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | RemovedRecordCount | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | RequestSha256 | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | ResultingRevision | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | ScopeId | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | SelectedRevision | persistence | properties-tenant-destruction-proof |
| properties.tenant-destruction.owner-proof | BunkFy.Modules.Properties.Persistence | BunkFy.Modules.Properties.Persistence.TenantTermination.PropertiesTenantDestroyReceipt | StartedAtUtc | persistence | properties-tenant-destruction-proof |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.AdminApi | BunkFy.Modules.Properties.AdminApi.PropertiesAdminApiModule+PropertyCreateRequest | TimeZoneId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.AdminApi | BunkFy.Modules.Properties.AdminApi.PropertiesAdminApiModule+PropertyUpdateRequest | TimeZoneId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.AdminApi | BunkFy.Modules.Properties.AdminApi.PropertiesAdminApiModule+SetPropertyTimeZoneRequest | Confirmed | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.AdminApi | BunkFy.Modules.Properties.AdminApi.PropertiesAdminApiModule+SetPropertyTimeZoneRequest | ExpectedVersion | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.AdminApi | BunkFy.Modules.Properties.AdminApi.PropertiesAdminApiModule+SetPropertyTimeZoneRequest | OperationId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.AdminApi | BunkFy.Modules.Properties.AdminApi.PropertiesAdminApiModule+SetPropertyTimeZoneRequest | TimeZoneId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Api | BunkFy.Modules.Properties.Api.PropertiesModule+PropertyCreateRequest | TimeZoneId | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Api | BunkFy.Modules.Properties.Api.PropertiesModule+PropertyUpdateRequest | TimeZoneId | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Api | BunkFy.Modules.Properties.Api.PropertiesModule+SetPropertyTimeZoneRequest | Confirmed | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Api | BunkFy.Modules.Properties.Api.PropertiesModule+SetPropertyTimeZoneRequest | ExpectedVersion | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Api | BunkFy.Modules.Properties.Api.PropertiesModule+SetPropertyTimeZoneRequest | OperationId | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Api | BunkFy.Modules.Properties.Api.PropertiesModule+SetPropertyTimeZoneRequest | TimeZoneId | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.CreatePropertyCommand | TimeZoneId | application-command | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.CreatePropertyCommand | TimeZoneId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | Confirmed | application-command | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | Confirmed | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | ExpectedVersion | application-command | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | ExpectedVersion | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | OperationId | application-command | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | OperationId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | PropertyId | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | PropertyId | application-command | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | PropertyId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | TimeZoneId | application-command | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.SetPropertyTimeZoneCommand | TimeZoneId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.UpdatePropertyCommand | TimeZoneId | application-command | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Commands.UpdatePropertyCommand | TimeZoneId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.GetPropertyTimeZoneRecoveryQuery | OperationId | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.GetPropertyTimeZoneRecoveryQuery | OperationId | application-query | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.GetPropertyTimeZoneRecoveryQuery | OperationId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.GetPropertyTimeZoneRecoveryQuery | PropertyId | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.GetPropertyTimeZoneRecoveryQuery | PropertyId | application-query | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.GetPropertyTimeZoneRecoveryQuery | PropertyId | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | CountryCode | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | CountryCode | application-query | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | CountryCode | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | Cursor | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | Cursor | application-query | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | Cursor | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | PageSize | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | PageSize | application-query | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | PageSize | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | Search | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | Search | application-query | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneCatalogQuery | Search | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneComplianceQuery | Cursor | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneComplianceQuery | Cursor | application-query | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneComplianceQuery | Cursor | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneComplianceQuery | PageSize | api-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneComplianceQuery | PageSize | application-query | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Application | BunkFy.Modules.Properties.Application.Queries.ListPropertyTimeZoneComplianceQuery | PageSize | admin-input | transient-request |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | CanonicalTimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | CanonicalTimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneCatalogVersion | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneCatalogVersion | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneCorrectionAllowed | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneCorrectionAllowed | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneObservedAtUtc | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneObservedAtUtc | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneStatus | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyDto | TimeZoneStatus | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | CanonicalTimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | CanonicalTimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneCatalogVersion | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneCatalogVersion | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneCorrectionAllowed | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneCorrectionAllowed | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneObservedAtUtc | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneObservedAtUtc | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneStatus | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyListItemDto | TimeZoneStatus | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | Comment | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | Comment | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | Countries | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | Countries | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | RuntimeAvailable | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | RuntimeAvailable | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | TimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | TimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | UtcOffsetMinutes | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogItemDto | UtcOffsetMinutes | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | CatalogVersion | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | CatalogVersion | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | HasMore | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | HasMore | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | NextCursor | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | NextCursor | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | ObservedAtUtc | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | ObservedAtUtc | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | TimeZones | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCatalogPageDto | TimeZones | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | CanonicalTimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | CanonicalTimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | Code | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | Code | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | CorrectionAllowed | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | CorrectionAllowed | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | Name | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | Name | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | OperatingCountryCode | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | OperatingCountryCode | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | ProcessingStatus | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | ProcessingStatus | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | PropertyId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | PropertyId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | Status | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | Status | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | TimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | TimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | TimeZoneStatus | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | TimeZoneStatus | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | Version | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneComplianceItemDto | Version | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | CatalogVersion | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | CatalogVersion | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | HasMore | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | HasMore | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | NextCursor | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | NextCursor | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | ObservedAtUtc | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | ObservedAtUtc | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | Properties | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCompliancePageDto | Properties | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCountryDto | Code | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCountryDto | Code | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCountryDto | Name | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneCountryDto | Name | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentCanonicalTimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentCanonicalTimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentTimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentTimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentTimeZoneObservedAtUtc | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentTimeZoneObservedAtUtc | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentTimeZoneStatus | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentTimeZoneStatus | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentVersion | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.PropertyTimeZoneRecoveryDto | CurrentVersion | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | CatalogVersion | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | CatalogVersion | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | ChangeKind | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | ChangeKind | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | CompletedAtUtc | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | CompletedAtUtc | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | ExpectedVersion | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | ExpectedVersion | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | OperationId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | OperationId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | PreviousTimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | PreviousTimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | PropertyId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | PropertyId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | RequestedTimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | RequestedTimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | TimeZoneId | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | TimeZoneId | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | TimeZoneStatus | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | TimeZoneStatus | admin-output | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | Version | api-response | transient-response |
| properties.time-zone.management-coordinate | BunkFy.Modules.Properties.Contracts | BunkFy.Modules.Properties.Contracts.SetPropertyTimeZoneReceiptDto | Version | admin-output | transient-response |

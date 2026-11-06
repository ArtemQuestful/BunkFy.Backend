namespace BunkFy.Modules.DataRights.Tests.Persistence;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.DataRights.Persistence.Repositories;
using Gma.Framework.Pagination;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;
using DataRightsCaseListResponse =
    DataRights.Contracts.DataRightsCaseListResponse;
using DataRightsRequesterRelationship =
    DataRights.Contracts.DataRightsRequesterRelationship;

[Trait("Category", "Unit")]
public sealed class DataRightsModelTests
{
    [Fact]
    public void Model_enforces_concurrency_scope_and_lifecycle_constraints()
    {
        using DataRightsDbContext dbContext = CreateDbContext(
            $"data-rights-model-{Guid.NewGuid():N}",
            new InMemoryDatabaseRoot(),
            "tenant-a");
        IEntityType entity = dbContext.Model.FindEntityType(typeof(DataRightsCase))!;
        IEntityType designEntity = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(DataRightsCase))!;

        Assert.True(entity.FindProperty(nameof(DataRightsCase.Version))!.IsConcurrencyToken);
        Assert.Equal(
            DataRightsCase.ActorIdMaxLength,
            entity.FindProperty(nameof(DataRightsCase.CreatedBy))!.GetMaxLength());
        Assert.Contains(entity.GetIndexes(), index => index.Properties.Select(item => item.Name)
            .SequenceEqual([
                nameof(DataRightsCase.ScopeId),
                nameof(DataRightsCase.PropertyId),
                nameof(DataRightsCase.Status),
                nameof(DataRightsCase.CreatedAtUtc),
                nameof(DataRightsCase.Id)
            ]));
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_cases_property_scope");
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_cases_operations");
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_cases_restriction_directive");
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_cases_requester_scope");
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_cases_decision_details");
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_cases_decision_state");
        Assert.Equal(
            "(\"ExecutionRevision\" IS NULL AND \"ExecutionStartedBy\" IS NULL AND " +
            "\"ExecutionStartedAtUtc\" IS NULL AND " +
            "(\"Status\" IN (1, 2, 3, 4, 5, 6, 11) OR " +
            "(\"Status\" = 9 AND \"Decision\" = 1 AND \"RequestedOperations\" = 1))) OR " +
            "(\"ExecutionRevision\" IS NOT NULL AND \"ExecutionRevision\" > \"DecisionRevision\" AND " +
            "\"ExecutionRevision\" <= \"Version\" AND \"ExecutionStartedBy\" IS NOT NULL AND " +
            "\"ExecutionStartedAtUtc\" IS NOT NULL AND \"Decision\" = 1 AND " +
            "\"Status\" IN (7, 8, 9, 10, 11))",
            designEntity.GetCheckConstraints().Single(
                constraint => constraint.Name == "CK_data_rights_cases_execution").Sql);
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_cases_approval_policy_evidence");
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                    "CK_data_rights_cases_response_deadline_evidence");
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_cases_tenant_termination");
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint =>
                constraint.Name == "CK_data_rights_cases_restriction_execution_proof");
        Assert.Contains(
            designEntity.GetCheckConstraints(),
            constraint =>
                constraint.Name == "CK_data_rights_cases_restriction_target");
        Assert.Equal(
            "\"Kind\" IN (1, 2, 3)",
            designEntity.GetCheckConstraints().Single(
                constraint => constraint.Name == "CK_data_rights_cases_kind").Sql);
        Assert.Equal(
            "(\"Kind\" = 1 AND \"RequestedOperations\" BETWEEN 1 AND 31) OR " +
            "(\"Kind\" = 2 AND \"RequestedOperations\" = 16) OR " +
            "(\"Kind\" = 3 AND \"RequestedOperations\" IN (1, 2, 4, 16))",
            designEntity.GetCheckConstraints().Single(
                constraint => constraint.Name == "CK_data_rights_cases_operations").Sql);
        Assert.Equal(
            "(\"Kind\" = 1 AND \"PropertyId\" IS NOT NULL) OR " +
            "(\"Kind\" IN (2, 3) AND \"PropertyId\" IS NULL)",
            designEntity.GetCheckConstraints().Single(
                constraint => constraint.Name == "CK_data_rights_cases_property_scope").Sql);
        Assert.Equal(
            "(\"Kind\" IN (1, 3) AND \"RequesterRelationship\" IN (1, 2, 3)) OR " +
            "(\"Kind\" = 2 AND \"RequesterRelationship\" IN (3, 4))",
            designEntity.GetCheckConstraints().Single(
                constraint => constraint.Name == "CK_data_rights_cases_requester_scope").Sql);
        Assert.Equal(
            TenantTerminationProcess.Sha256Length,
            entity.FindProperty(
                nameof(DataRightsCase.TenantTerminationPolicyEvidenceSha256))!
                .GetMaxLength());
        IIndex activeTenantTermination = Assert.Single(
            entity.GetIndexes(),
            index =>
                index.GetDatabaseName() ==
                    "UX_data_rights_cases_active_tenant_termination");
        Assert.True(activeTenantTermination.IsUnique);
        Assert.Equal(
            [nameof(DataRightsCase.ScopeId)],
            activeTenantTermination.Properties.Select(property => property.Name));
        Assert.Equal(
            "\"Kind\" = 2 AND \"Status\" NOT IN (6, 9, 11)",
            activeTenantTermination.GetFilter());
        Assert.Equal(
            DataRightsCase.ActorIdMaxLength,
            entity.FindProperty(nameof(DataRightsCase.DecidedBy))!.GetMaxLength());
        IEntityType responseDeadlineEvidence = entity.FindNavigation(
            nameof(DataRightsCase.ResponseDeadlinePolicyEvidence))!
            .TargetEntityType;
        Assert.Equal(
            DataRightsResponseDeadlinePolicyEvidence.ContentSha256Length,
            responseDeadlineEvidence.FindProperty(
                nameof(DataRightsResponseDeadlinePolicyEvidence.ContentSha256))!
                .GetMaxLength());
        Assert.Equal(
            DataRightsResponseDeadlinePolicyEvidence.TimeZoneIdMaxLength,
            responseDeadlineEvidence.FindProperty(
                nameof(DataRightsResponseDeadlinePolicyEvidence.TimeZoneId))!
                .GetMaxLength());
        IEntityType deadlineAlertDispatch = dbContext.Model.FindEntityType(
            typeof(DataRightsResponseDeadlineAlertDispatchReceipt))!;
        IEntityType designDeadlineAlertDispatch = dbContext
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(DataRightsResponseDeadlineAlertDispatchReceipt))!;
        Assert.Contains(deadlineAlertDispatch.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsResponseDeadlineAlertDispatchReceipt.ScopeId),
                nameof(DataRightsResponseDeadlineAlertDispatchReceipt.CaseId),
                nameof(DataRightsResponseDeadlineAlertDispatchReceipt.AlertKind)
            ]));
        Assert.Contains(
            designDeadlineAlertDispatch.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_response_deadline_alert_dispatches_timing");
        Assert.Contains(
            designDeadlineAlertDispatch.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType ==
                typeof(DataRightsCase) &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        IEntityType propertyProjection = dbContext.Model.FindEntityType(
            typeof(DataRightsPropertyProjection))!;
        IEntityType designPropertyProjection = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(DataRightsPropertyProjection))!;
        Assert.NotNull(propertyProjection.FindDeclaredQueryFilter(
            ScopeFilterNames.ScopeFilter));
        Assert.Equal(
            Properties.Contracts.PropertiesContractLimits
                .TimeZoneIdMaxLength,
            propertyProjection.FindProperty(
                nameof(DataRightsPropertyProjection.TimeZoneId))!
                .GetMaxLength());
        string[] propertyProjectionConstraints =
        [
            "CK_data_rights_property_projection_coordinates",
            "CK_data_rights_property_projection_versions",
            "CK_data_rights_property_projection_topology",
            "CK_data_rights_property_projection_topology_text",
            "CK_data_rights_property_projection_known",
            "CK_data_rights_property_projection_processing_status",
            "CK_data_rights_property_projection_policy_source",
            "CK_data_rights_property_projection_governance_policy"
        ];
        Assert.All(
            propertyProjectionConstraints,
            constraintName => Assert.Contains(
                designPropertyProjection.GetCheckConstraints(),
                constraint => constraint.Name == constraintName));
        IEntityType designPropertyPolicyAcknowledgement = dbContext
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(DataRightsPropertyPolicyAcknowledgement))!;
        Assert.Contains(
            designPropertyPolicyAcknowledgement.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_data_rights_property_policy_acknowledgements_contract");
        IEntityType selectedSubject =
            dbContext.Model.FindEntityType(typeof(DataRightsSubjectCoordinate))!;
        IEntityType designSelectedSubject = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(DataRightsSubjectCoordinate))!;
        Assert.Equal(
            DataRightsSubjectCoordinate.OwnerKeyMaxLength,
            selectedSubject.FindProperty(nameof(DataRightsSubjectCoordinate.OwnerKey))!.GetMaxLength());
        Assert.Equal(
            DataRightsCase.ActorIdMaxLength,
            selectedSubject.FindProperty(nameof(DataRightsSubjectCoordinate.SelectedBy))!.GetMaxLength());
        Assert.Equal(
            ["CaseId", "OwnerKey", "RecordType", "RecordId"],
            selectedSubject.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(
            designSelectedSubject.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_selected_subjects_record_version");
        Assert.Contains(
            designSelectedSubject.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_selected_subjects_selected_by");
        IEntityType correctionExecution =
            dbContext.Model.FindEntityType(typeof(DataRightsCorrectionExecution))!;
        IEntityType designCorrectionExecution = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(DataRightsCorrectionExecution))!;
        Assert.True(
            correctionExecution.FindProperty(nameof(DataRightsCorrectionExecution.Version))!
                .IsConcurrencyToken);
        Assert.Equal(
            DataRightsCorrectionExecution.FieldPolicyKeyMaxLength,
            correctionExecution
                .FindProperty(nameof(DataRightsCorrectionExecution.FieldPolicyKey))!
                .GetMaxLength());
        Assert.Contains(correctionExecution.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsCorrectionExecution.ScopeId),
                nameof(DataRightsCorrectionExecution.CaseId)
            ]));
        Assert.Contains(correctionExecution.GetIndexes(), index =>
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsCorrectionExecution.ScopeId),
                nameof(DataRightsCorrectionExecution.CaseKind),
                nameof(DataRightsCorrectionExecution.PropertyId),
                nameof(DataRightsCorrectionExecution.State),
                nameof(DataRightsCorrectionExecution.ExpiresAtUtc)
            ]));
        Assert.True(
            correctionExecution
                .FindProperty(nameof(DataRightsCorrectionExecution.PropertyId))!
                .IsNullable);
        Assert.Contains(
            designCorrectionExecution.GetCheckConstraints(),
            constraint =>
                constraint.Name == "CK_data_rights_correction_executions_contract");
        Assert.Contains(
            designCorrectionExecution.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                    "CK_data_rights_correction_executions_coordinates");
        Assert.Contains(
            designCorrectionExecution.GetCheckConstraints(),
            constraint =>
                constraint.Name == "CK_data_rights_correction_executions_state");
        Assert.Contains(
            designCorrectionExecution.GetForeignKeys(),
            foreignKey =>
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict &&
                foreignKey.Properties.Select(property => property.Name).SequenceEqual([
                    nameof(DataRightsCorrectionExecution.ScopeId),
                    nameof(DataRightsCorrectionExecution.CaseId)
                ]));
        IEntityType batch =
            dbContext.Model.FindEntityType(typeof(DataRightsExecutionBatch))!;
        IEntityType designBatch = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(DataRightsExecutionBatch))!;
        Assert.True(
            batch.FindProperty(nameof(DataRightsExecutionBatch.Version))!
                .IsConcurrencyToken);
        Assert.Contains(batch.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsExecutionBatch.ScopeId),
                nameof(DataRightsExecutionBatch.IdempotencyKey)
            ]));
        Assert.Contains(batch.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsExecutionBatch.ScopeId),
                nameof(DataRightsExecutionBatch.CaseId)
            ]));
        Assert.Contains(
            designBatch.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_data_rights_execution_batches_subject_count");
        Assert.Contains(
            designBatch.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_data_rights_execution_batches_scope");
        Assert.True(
            batch.FindProperty(nameof(DataRightsExecutionBatch.PropertyId))!
                .IsNullable);
        IEntityType workItem =
            dbContext.Model.FindEntityType(typeof(DataRightsExecutionWorkItem))!;
        IEntityType designWorkItem = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(DataRightsExecutionWorkItem))!;
        Assert.True(
            workItem.FindProperty(nameof(DataRightsExecutionWorkItem.Version))!
                .IsConcurrencyToken);
        Assert.Equal(
            DataRightsCase.ActorIdMaxLength,
            workItem.FindProperty(nameof(DataRightsExecutionWorkItem.CreatedBy))!.GetMaxLength());
        Assert.Equal(
            DataRightsExecutionWorkItem.OwnerCodeMaxLength,
            workItem.FindProperty(nameof(DataRightsExecutionWorkItem.OwnerDispositionCode))!
                .GetMaxLength());
        Assert.Equal(
            DataRightsExecutionWorkItem.OwnerCodeMaxLength,
            workItem.FindProperty(nameof(DataRightsExecutionWorkItem.OwnerReasonCode))!
                .GetMaxLength());
        Assert.Equal(
            DataRightsExecutionWorkItem.Sha256Length,
            workItem.FindProperty(nameof(DataRightsExecutionWorkItem.OwnerReceiptSha256))!
                .GetMaxLength());
        Assert.Equal(
            DataRightsExecutionWorkItem.OutcomeCodeMaxLength,
            workItem.FindProperty(nameof(DataRightsExecutionWorkItem.OutcomeCode))!
                .GetMaxLength());
        Assert.Equal(
            DataRightsApprovalPolicyEvidence.CountryCodeLength,
            workItem.FindProperty(
                nameof(DataRightsExecutionWorkItem.PolicyOperatingCountryCode))!
                .GetMaxLength());
        Assert.Equal(
            DataRightsApprovalPolicyEvidence.StateBindingsJsonMaxLength,
            workItem.FindProperty(
                nameof(DataRightsExecutionWorkItem.PolicyStateBindingsJson))!
                .GetMaxLength());
        Assert.Equal(
            DataRightsApprovalPolicyEvidence.ContentSha256Length,
            workItem.FindProperty(
                nameof(DataRightsExecutionWorkItem.PolicyStateBindingsSha256))!
                .GetMaxLength());
        Assert.True(
            workItem.FindProperty(
                nameof(DataRightsExecutionWorkItem.PropertyId))!.IsNullable);
        Assert.False(
            workItem.FindProperty(
                nameof(DataRightsExecutionWorkItem.PolicyStateBindingsJson))!
                .IsNullable);
        Assert.Contains(workItem.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsExecutionWorkItem.ScopeId),
                nameof(DataRightsExecutionWorkItem.IdempotencyKey)
            ]));
        Assert.Contains(workItem.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsExecutionWorkItem.ScopeId),
                nameof(DataRightsExecutionWorkItem.CaseId),
                nameof(DataRightsExecutionWorkItem.ApprovalRevision),
                nameof(DataRightsExecutionWorkItem.Operation),
                nameof(DataRightsExecutionWorkItem.OwnerKey),
                nameof(DataRightsExecutionWorkItem.RecordType),
                nameof(DataRightsExecutionWorkItem.RecordId)
            ]));
        Assert.Contains(workItem.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsExecutionWorkItem.ScopeId),
                nameof(DataRightsExecutionWorkItem.TaskRunId)
            ]));
        Assert.Contains(
            designWorkItem.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_execution_work_items_revisions");
        Assert.Contains(
            designWorkItem.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_execution_work_items_policy");
        Assert.Contains(
            designWorkItem.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_execution_work_items_scope");
        Assert.Contains(
            designWorkItem.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_execution_work_items_state");
        Assert.Contains(
            designWorkItem.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_execution_work_items_attempts");
        Assert.Contains(
            designWorkItem.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_execution_work_items_owner_contract");
        Assert.Contains(
            designWorkItem.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_execution_work_items_task");
        Assert.Contains(
            designWorkItem.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_execution_work_items_owner_outcome");
        Assert.Contains(
            designWorkItem.GetCheckConstraints(),
            constraint => constraint.Name == "CK_data_rights_execution_work_items_timestamps");
        Assert.Contains(
            designWorkItem.GetForeignKeys(),
            foreignKey =>
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict &&
                foreignKey.Properties.Select(property => property.Name).SequenceEqual([
                    nameof(DataRightsExecutionWorkItem.ScopeId),
                    nameof(DataRightsExecutionWorkItem.CaseId)
                ]));
    }

    [Fact]
    public void Export_artifact_model_enforces_scope_identity_and_state_shape()
    {
        using DataRightsDbContext dbContext = CreateDbContext(
            $"data-rights-export-model-{Guid.NewGuid():N}",
            new InMemoryDatabaseRoot(),
            "tenant-a");
        IEntityType artifact =
            dbContext.Model.FindEntityType(typeof(DataRightsExportArtifact))!;
        IEntityType designArtifact = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(DataRightsExportArtifact))!;

        Assert.True(
            artifact.FindProperty(nameof(DataRightsExportArtifact.Version))!
                .IsConcurrencyToken);
        Assert.False(
            artifact.FindProperty(nameof(DataRightsExportArtifact.ExpiresAtUtc))!
                .IsNullable);
        Assert.True(
            artifact.FindProperty(
                nameof(DataRightsExportArtifact.LastRetryBaseVersion))!
                .IsNullable);
        Assert.Equal(
            DataRightsExportArtifact.Sha256Length,
            artifact.FindProperty(nameof(DataRightsExportArtifact.SelectionSha256))!
                .GetMaxLength());
        Assert.Equal(
            DataRightsExportArtifact.ActorIdMaxLength,
            artifact.FindProperty(nameof(DataRightsExportArtifact.RequestedBy))!
                .GetMaxLength());
        Assert.Contains(artifact.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsExportArtifact.ScopeId),
                nameof(DataRightsExportArtifact.IdempotencyKey)
            ]));
        Assert.Contains(artifact.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(DataRightsExportArtifact.ScopeId),
                nameof(DataRightsExportArtifact.CaseId)
            ]));
        Assert.Contains(
            designArtifact.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_data_rights_export_artifacts_generation_shape");
        Assert.Contains(
            designArtifact.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_data_rights_export_artifacts_storage_shape");
        Assert.Contains(
            designArtifact.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_data_rights_export_artifacts_lifecycle_shape");
        Assert.Contains(
            designArtifact.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_data_rights_export_artifacts_timestamps");
        Assert.Contains(
            designArtifact.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_data_rights_export_artifacts_failure_shape");
        Assert.Contains(
            designArtifact.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_data_rights_export_artifacts_retry_version");
        Assert.Contains(
            designArtifact.GetForeignKeys(),
            foreignKey =>
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict &&
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(DataRightsExportArtifact.ScopeId),
                        nameof(DataRightsExportArtifact.CaseId)
                    ]));
    }

    [Fact]
    public async Task Export_audit_model_is_scoped_bounded_and_append_only()
    {
        string databaseName = $"data-rights-export-audit-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        DataRightsExportAuditEntry entry =
            DataRightsExportAuditEntry.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                propertyId: null,
                DataRightsCaseKind.TenantTermination,
                DataRightsExportAuditAction.Download,
                "user:privacy",
                "succeeded",
                new DateTimeOffset(
                    2026,
                    7,
                    27,
                    12,
                    0,
                    0,
                    TimeSpan.Zero)).Value;

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            IEntityType entity = writer.Model.FindEntityType(
                typeof(DataRightsExportAuditEntry))!;
            IEntityType designEntity = writer.GetService<IDesignTimeModel>()
                .Model
                .FindEntityType(typeof(DataRightsExportAuditEntry))!;
            Assert.Equal(
                DataRightsExportArtifact.ActorIdMaxLength,
                entity.FindProperty(nameof(DataRightsExportAuditEntry.ActorId))!
                    .GetMaxLength());
            Assert.Equal(
                DataRightsExportAuditEntry.OutcomeCodeMaxLength,
                entity.FindProperty(nameof(DataRightsExportAuditEntry.OutcomeCode))!
                    .GetMaxLength());
            string scopeConstraint = Assert.Single(
                designEntity.GetCheckConstraints(),
                constraint =>
                    constraint.Name == "CK_data_rights_export_audit_scope")
                .Sql;
            Assert.Contains(
                $"\"CaseKind\" IN ({(int)DataRightsCaseKind.TenantTermination}, {(int)DataRightsCaseKind.StaffRights})",
                scopeConstraint,
                StringComparison.Ordinal);
            Assert.Contains(
                designEntity.GetCheckConstraints(),
                constraint =>
                    constraint.Name == "CK_data_rights_export_audit_action");

            writer.ExportAuditEntries.Add(entry);
            await writer.SaveChangesAsync();
            writer.Entry(entry).State = EntityState.Modified;
            InvalidOperationException failure =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => writer.SaveChangesAsync());
            Assert.Contains("append-only", failure.Message);
        }

        await using DataRightsDbContext tenantB = CreateDbContext(
            databaseName,
            root,
            "tenant-b");
        Assert.Empty(await tenantB.ExportAuditEntries.ToArrayAsync());
    }

    [Fact]
    public async Task Scope_filter_hides_cases_from_another_tenant()
    {
        string databaseName = $"data-rights-scope-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateCase("tenant-a", propertyId);

        await using (DataRightsDbContext tenantA = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            tenantA.Cases.Add(dataRightsCase);
            await tenantA.SaveChangesAsync();
        }

        await using DataRightsDbContext tenantB = CreateDbContext(
            databaseName,
            root,
            "tenant-b");
        Assert.Empty(await tenantB.Cases.ToArrayAsync());
        Assert.Null(await new DataRightsCaseRepository(tenantB).GetByIdAsync(
            dataRightsCase.Id,
            CancellationToken.None));
    }

    [Fact]
    public async Task Case_repository_keeps_property_and_tenant_case_kinds_isolated()
    {
        string databaseName = $"data-rights-case-scope-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        Guid propertyA = Guid.NewGuid();
        Guid propertyB = Guid.NewGuid();
        DataRightsCase guestA = CreateCase("tenant-a", propertyA);
        DataRightsCase guestB = CreateCase("tenant-a", propertyB);
        DataRightsCase staff = CreateTenantCase(
            "tenant-a",
            DataRightsCaseKind.StaffRights,
            DataRightsRequesterRelation.DataSubject);
        DataRightsCase termination = CreateTenantCase(
            "tenant-a",
            DataRightsCaseKind.TenantTermination,
            DataRightsRequesterRelation.TenantOwner);

        await using DataRightsDbContext dbContext = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        dbContext.Cases.AddRange(guestA, guestB, staff, termination);
        await dbContext.SaveChangesAsync();
        DataRightsCaseRepository repository = new(dbContext);

        Assert.Same(
            guestA,
            await repository.GetAsync(
                DataRightsCaseScope.ForProperty(propertyA),
                guestA.Id,
                CancellationToken.None));
        Assert.Null(await repository.GetAsync(
            DataRightsCaseScope.ForProperty(propertyB),
            guestA.Id,
            CancellationToken.None));
        Assert.Same(
            staff,
            await repository.GetAsync(
                DataRightsCaseScope.Staff,
                staff.Id,
                CancellationToken.None));
        Assert.Null(await repository.GetAsync(
            DataRightsCaseScope.Staff,
            termination.Id,
            CancellationToken.None));
        Assert.Same(
            guestA,
            await repository.GetByIdAsync(
                guestA.Id,
                CancellationToken.None));
        Assert.Same(
            termination,
            await repository.GetByIdAsync(
                termination.Id,
                CancellationToken.None));
        Assert.Same(
            termination,
            await repository.GetAsync(
                DataRightsCaseScope.TenantTermination,
                termination.Id,
                CancellationToken.None));

        DataRightsCaseListResponse staffCases = await repository.ListAsync(
            DataRightsCaseScope.Staff,
            status: null,
            new PageRequest(1, 20),
            CancellationToken.None);
        Assert.Equal(staff.Id, Assert.Single(staffCases.Items).Id);
        Assert.False(staffCases.HasMore);
    }

    [Fact]
    public async Task Case_list_returns_stable_summary_pages_with_truthful_continuation()
    {
        string databaseName = $"data-rights-case-pages-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        DataRightsCase[] cases =
        [
            CreateTenantCase(
                "tenant-a",
                DataRightsCaseKind.StaffRights,
                DataRightsRequesterRelation.ControllerInitiated),
            CreateTenantCase(
                "tenant-a",
                DataRightsCaseKind.StaffRights,
                DataRightsRequesterRelation.ControllerInitiated),
            CreateTenantCase(
                "tenant-a",
                DataRightsCaseKind.StaffRights,
                DataRightsRequesterRelation.ControllerInitiated)
        ];
        Assert.True(cases[0].BeginDiscovery(
            cases[0].Version,
            "user:operator",
            cases[0].LastChangedAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(cases[0].SelectSubject(
            "staff",
            "staff-profile",
            Guid.NewGuid(),
            4,
            cases[0].Version,
            "user:operator",
            cases[0].LastChangedAtUtc.AddMinutes(1)).IsSuccess);

        await using DataRightsDbContext dbContext = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        dbContext.Cases.AddRange(cases);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        DataRightsCaseRepository repository = new(dbContext);
        DataRightsCase[] expected = cases
            .OrderByDescending(dataRightsCase => dataRightsCase.CreatedAtUtc)
            .ThenBy(dataRightsCase => dataRightsCase.Id)
            .ToArray();

        DataRightsCaseListResponse firstPage = await repository.ListAsync(
            DataRightsCaseScope.Staff,
            status: null,
            new PageRequest(1, 2),
            CancellationToken.None);
        DataRightsCaseListResponse secondPage = await repository.ListAsync(
            DataRightsCaseScope.Staff,
            status: null,
            new PageRequest(2, 2),
            CancellationToken.None);

        Assert.Equal(expected.Take(2).Select(item => item.Id), firstPage.Items.Select(item => item.Id));
        Assert.Equal([expected[2].Id], secondPage.Items.Select(item => item.Id));
        Assert.True(firstPage.HasMore);
        Assert.False(secondPage.HasMore);
        Assert.Equal(1, firstPage.Items.Concat(secondPage.Items)
            .Single(item => item.Id == cases[0].Id)
            .SelectedSubjectCount);
        Assert.All(
            firstPage.Items.Concat(secondPage.Items),
            item => Assert.Equal(
                DataRightsRequesterRelationship.ControllerInitiated,
                item.RequesterRelationship));
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Selected_subject_coordinates_round_trip_with_the_case()
    {
        string databaseName = $"data-rights-subjects-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        DataRightsCase dataRightsCase = CreateCase("tenant-a", Guid.NewGuid());
        DateTimeOffset discoveryAt = dataRightsCase.CreatedAtUtc.AddMinutes(1);
        Assert.True(dataRightsCase.BeginDiscovery(1, "user:operator", discoveryAt).IsSuccess);
        Guid guestId = Guid.NewGuid();
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            guestId,
            7,
            2,
            "user:operator",
            discoveryAt.AddMinutes(1)).IsSuccess);

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            writer.Cases.Add(dataRightsCase);
            await writer.SaveChangesAsync();
        }

        await using DataRightsDbContext reader = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        DataRightsCase restored = await reader.Cases.SingleAsync();
        DataRightsSubjectCoordinate coordinate = Assert.Single(restored.SelectedSubjects);
        Assert.Equal("guests", coordinate.OwnerKey);
        Assert.Equal("guest-profile", coordinate.RecordType);
        Assert.Equal(guestId, coordinate.RecordId);
        Assert.Equal(7, coordinate.RecordVersion);
        Assert.Equal("user:operator", coordinate.SelectedBy);
    }

    [Fact]
    public async Task Approved_decision_revision_and_attribution_round_trip_with_the_case()
    {
        string databaseName = $"data-rights-decision-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        DataRightsCase dataRightsCase = CreateCase("tenant-a", Guid.NewGuid());
        DateTimeOffset discoveryAt = dataRightsCase.CreatedAtUtc.AddMinutes(1);
        Assert.True(dataRightsCase.BeginDiscovery(1, "user:operator", discoveryAt).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            1,
            2,
            "user:operator",
            discoveryAt.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            3,
            "user:operator",
            discoveryAt.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            4,
            "user:decision-maker",
            discoveryAt.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            5,
            "user:decision-maker",
            discoveryAt.AddMinutes(4)).IsSuccess);

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            writer.Cases.Add(dataRightsCase);
            await writer.SaveChangesAsync();
        }

        await using DataRightsDbContext reader = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        DataRightsCase restored = await reader.Cases.SingleAsync();
        Assert.Equal(DataRightsCaseDecision.Approved, restored.Decision);
        Assert.Equal(DataRightsCaseDecisionReason.RequestValidated, restored.DecisionReason);
        Assert.Equal(6, restored.DecisionRevision);
        Assert.Equal("user:decision-maker", restored.DecidedBy);
        Assert.Equal(discoveryAt.AddMinutes(4), restored.DecidedAtUtc);
    }

    [Fact]
    public async Task Restriction_directive_round_trips_with_the_case()
    {
        string databaseName = $"data-rights-restriction-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            Guid.NewGuid(),
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Restriction,
            DataRightsRequesterRelation.ControllerInitiated,
            DataRightsRestrictionAction.Release).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator",
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero)).Value;

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            writer.Cases.Add(dataRightsCase);
            await writer.SaveChangesAsync();
        }

        await using DataRightsDbContext reader = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        DataRightsCase restored = await reader.Cases.SingleAsync();
        Assert.Equal(DataRightsRestrictionAction.Release, restored.RestrictionAction);
    }

    [Fact]
    public async Task Restriction_release_target_round_trips_with_the_case()
    {
        string databaseName =
            $"data-rights-restriction-target-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        Guid propertyId = Guid.NewGuid();
        Guid ownerOperationId = Guid.NewGuid();
        DateTimeOffset now =
            new(2026, 8, 15, 12, 30, 0, TimeSpan.Zero);
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Restriction,
            DataRightsRequesterRelation.ControllerInitiated,
            DataRightsRestrictionAction.Release).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:privacy",
            now.AddMinutes(-3)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy",
            now.AddMinutes(-2)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            recordVersion: 5,
            dataRightsCase.Version,
            "user:privacy",
            now.AddMinutes(-1)).IsSuccess);
        Assert.True(dataRightsCase.SelectRestrictionReleaseTarget(
            "guests",
            ownerOperationId,
            ownerOperationVersion: 3,
            dataRightsCase.Version,
            "user:reviewer",
            now).IsSuccess);

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            writer.Cases.Add(dataRightsCase);
            await writer.SaveChangesAsync();
        }

        await using DataRightsDbContext reader = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        DataRightsCase restored = await reader.Cases.SingleAsync();
        Assert.Equal(
            DataRightsRestrictionReleaseTarget.CurrentBindingVersion,
            restored.RestrictionTargetingContractVersion);
        Assert.NotNull(restored.RestrictionReleaseTarget);
        Assert.Equal(
            ownerOperationId,
            restored.RestrictionReleaseTarget.OwnerOperationId);
        Assert.Equal(
            3,
            restored.RestrictionReleaseTarget.OwnerOperationVersion);
        Assert.Equal("guests", restored.RestrictionReleaseTarget.OwnerKey);
        Assert.Equal("user:reviewer", restored.RestrictionReleaseTarget.SelectedBy);
        Assert.Equal(now, restored.RestrictionReleaseTarget.SelectedAtUtc);
    }

    [Fact]
    public async Task Restriction_execution_proof_round_trips_with_the_case()
    {
        string databaseName = $"data-rights-restriction-proof-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        Guid propertyId = Guid.NewGuid();
        DateTimeOffset now =
            new(2026, 7, 27, 16, 30, 0, TimeSpan.Zero);
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Restriction,
            DataRightsRequesterRelation.ControllerInitiated,
            DataRightsRestrictionAction.Apply).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:privacy",
            now.AddMinutes(-6)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy",
            now.AddMinutes(-5)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            recordVersion: 3,
            dataRightsCase.Version,
            "user:privacy",
            now.AddMinutes(-4)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy",
            now.AddMinutes(-3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            now.AddMinutes(-2)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            now.AddMinutes(-1)).IsSuccess);
        DataRightsRestrictionExecutionProof proof =
            DataRightsRestrictionExecutionProof.Create(
                Guid.NewGuid(),
                dataRightsCase.DecisionRevision!.Value,
                DataRightsRestrictionAction.Apply,
                dataRightsCase.SelectedSubjects.Single(),
                receiptContractVersion: 1,
                Guid.NewGuid(),
                Guid.NewGuid(),
                resultingOwnerRevision: 2,
                resultingProjectionRevision: 4,
                effectiveRestricted: true,
                new string('a', 64),
                "user:executor",
                now.AddSeconds(-5)).Value;
        Assert.True(dataRightsCase.CompleteRestrictionExecution(
            dataRightsCase.Version,
            proof,
            now).IsSuccess);

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            writer.Cases.Add(dataRightsCase);
            await writer.SaveChangesAsync();
        }

        await using DataRightsDbContext reader = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        DataRightsCase restored = await reader.Cases.SingleAsync();
        Assert.Equal(DataRightsCaseState.Completed, restored.Status);
        Assert.NotNull(restored.RestrictionExecutionProof);
        Assert.Equal(proof.IdempotencyKey, restored.RestrictionExecutionProof.IdempotencyKey);
        Assert.Equal(proof.ReceiptSha256, restored.RestrictionExecutionProof.ReceiptSha256);
        Assert.True(restored.RestrictionExecutionProof.EffectiveRestricted);
    }

    [Fact]
    public async Task Guest_response_deadline_evidence_round_trips_with_the_case()
    {
        string databaseName =
            $"data-rights-response-deadline-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        Guid propertyId = Guid.NewGuid();
        DateTimeOffset receivedAt =
            new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.DataSubject).Value;
        DataRightsResponseDeadlinePolicyEvidence evidence =
            DataRightsResponseDeadlinePolicyEvidence.Create(
                propertyId,
                17,
                19,
                "GB",
                "development-hostel-example",
                2,
                new string('d', 64),
                DataRightsResponseRight.Export,
                "development-example",
                0,
                1,
                0,
                "Europe/London",
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
                receivedAt,
                receivedAt.AddMinutes(2),
                receivedAt.AddMonths(1)).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator",
            receivedAt,
            evidence).Value;

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            writer.Cases.Add(dataRightsCase);
            await writer.SaveChangesAsync();
        }

        await using DataRightsDbContext reader = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        DataRightsCase restored = await reader.Cases.SingleAsync();
        DataRightsResponseDeadlinePolicyEvidence restoredEvidence =
            Assert.IsType<DataRightsResponseDeadlinePolicyEvidence>(
                restored.ResponseDeadlinePolicyEvidence);
        Assert.Equal(evidence.DueAtUtc, restored.DueAtUtc);
        Assert.Equal(17, restoredEvidence.PropertyTopologySourceVersion);
        Assert.Equal(19, restoredEvidence.PropertyPolicySourceVersion);
        Assert.Equal("Europe/London", restoredEvidence.TimeZoneId);
        Assert.True(restoredEvidence.HasValidShape());
    }

    [Fact]
    public async Task Anonymisation_approval_evidence_round_trips_with_the_case()
    {
        string databaseName = $"data-rights-evidence-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        Guid propertyId = Guid.NewGuid();
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DateTimeOffset now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator",
            now).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            1,
            "user:operator",
            now.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            4,
            2,
            "user:operator",
            now.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            3,
            "user:operator",
            now.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            4,
            "user:decision-maker",
            now.AddMinutes(4)).IsSuccess);
        DataRightsApprovalPolicyEvidence evidence =
            DataRightsApprovalPolicyEvidence.Create(
                propertyId,
                12,
                "GB",
                "approved-policy",
                3,
                "guest-retention",
                2,
                new string('c', 64),
                "data-rights-anonymisation",
                "erasure",
                "authorized-workspace-operator",
                now.AddMinutes(5)).Value;
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            5,
            "user:decision-maker",
            now.AddMinutes(5),
            evidence).IsSuccess);

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            writer.Cases.Add(dataRightsCase);
            await writer.SaveChangesAsync();
        }

        await using DataRightsDbContext reader = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        DataRightsCase restored = await reader.Cases.SingleAsync();
        DataRightsApprovalPolicyEvidence restoredEvidence =
            Assert.IsType<DataRightsApprovalPolicyEvidence>(
                restored.ApprovalPolicyEvidence);
        Assert.Equal("approved-policy", restoredEvidence.PolicyId);
        Assert.Equal(12, restoredEvidence.PropertyVersion);
        Assert.Equal(new string('c', 64), restoredEvidence.ContentSha256);
        Assert.True(restoredEvidence.RequiresDistinctExecutor);
    }

    [Fact]
    public async Task Staff_anonymisation_evidence_round_trips_in_tenant_scope()
    {
        string databaseName =
            $"data-rights-staff-evidence-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DateTimeOffset now =
            new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator",
            now).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            1,
            "user:operator",
            now.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "staff",
            "staff-member",
            Guid.NewGuid(),
            8,
            2,
            "user:operator",
            now.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            3,
            "user:operator",
            now.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            4,
            "user:decision-maker",
            now.AddMinutes(4)).IsSuccess);
        DataRightsApprovalPolicyEvidence evidence =
            DataRightsApprovalPolicyEvidence.CreateScoped(
                DataRightsCaseKind.StaffRights,
                DataRightsCaseScopeKind.Tenant,
                propertyId: null,
                propertyVersion: 0,
                "GB",
                "approved-staff-policy",
                3,
                "staff-retention",
                2,
                new string('c', 64),
                "staff-data-rights-anonymisation",
                "erasure",
                "authorized-workspace-operator",
                "staff-employment",
                "employment-ended",
                now.AddDays(-8),
                now.AddDays(-1),
                now.AddMinutes(5),
                [
                    DataRightsApprovalEvidenceBinding.Create(
                        "staff.record",
                        8,
                        new string('d', 64)).Value,
                    DataRightsApprovalEvidenceBinding.Create(
                        "staff.governance",
                        2,
                        new string('e', 64)).Value
                ]).Value;
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            5,
            "user:decision-maker",
            now.AddMinutes(5),
            evidence).IsSuccess);
        Assert.True(dataRightsCase.BeginAnonymisationExecution(
            6,
            "user:executor",
            now.AddMinutes(6)).IsSuccess);
        DataRightsExecutionWorkItem workItem =
            DataRightsExecutionWorkItem.Prepare(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                dataRightsCase.Id,
                DataRightsExecutionScope.Staff,
                approvalRevision: 6,
                executionRevision: 7,
                DataRightsCaseOperation.Anonymisation,
                Assert.Single(dataRightsCase.SelectedSubjects),
                evidence,
                "user:executor",
                now.AddMinutes(6)).Value;

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            writer.Cases.Add(dataRightsCase);
            writer.ExecutionWorkItems.Add(workItem);
            await writer.SaveChangesAsync();
        }

        await using DataRightsDbContext reader = CreateDbContext(
            databaseName,
            root,
            "tenant-a");
        DataRightsCase restored = await reader.Cases.SingleAsync();
        DataRightsApprovalPolicyEvidence restoredEvidence =
            Assert.IsType<DataRightsApprovalPolicyEvidence>(
                restored.ApprovalPolicyEvidence);
        Assert.True(restoredEvidence.HasValidShape());
        Assert.Equal(
            DataRightsCaseKind.StaffRights,
            restoredEvidence.CaseKind);
        Assert.Equal(
            DataRightsCaseScopeKind.Tenant,
            restoredEvidence.ScopeKind);
        Assert.Null(restoredEvidence.PropertyId);
        Assert.Equal(
            ["staff.governance", "staff.record"],
            restoredEvidence.StateBindings.Select(binding => binding.Key));
        Assert.Equal(
            evidence.StateBindingsSha256,
            restoredEvidence.StateBindingsSha256);
        DataRightsExecutionWorkItem restoredWorkItem =
            await reader.ExecutionWorkItems.SingleAsync();
        Assert.Equal(
            DataRightsCaseKind.StaffRights,
            restoredWorkItem.CaseKind);
        Assert.Equal(
            DataRightsCaseScopeKind.Tenant,
            restoredWorkItem.ScopeKind);
        Assert.Null(restoredWorkItem.PropertyId);
        Assert.Equal(
            evidence.StateBindingsJson,
            restoredWorkItem.PolicyStateBindingsJson);
        Assert.True(restoredWorkItem.MatchesPolicyEvidence(
            restoredEvidence));
    }

    [Fact]
    public async Task Prepared_anonymisation_execution_round_trips_and_is_tenant_isolated()
    {
        string databaseName = $"data-rights-execution-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        (DataRightsCase dataRightsCase, DataRightsExecutionWorkItem workItem) =
            CreatePreparedExecution();

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            writer.Cases.Add(dataRightsCase);
            writer.ExecutionWorkItems.Add(workItem);
            await writer.SaveChangesAsync();
        }

        await using (DataRightsDbContext reader = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            DataRightsCase restoredCase = await reader.Cases.SingleAsync();
            DataRightsExecutionWorkItem restoredWorkItem =
                await reader.ExecutionWorkItems.SingleAsync();
            Assert.Equal(DataRightsCaseState.Executing, restoredCase.Status);
            Assert.Equal(7, restoredCase.ExecutionRevision);
            Assert.Equal("user:executor", restoredCase.ExecutionStartedBy);
            Assert.Equal(DataRightsExecutionWorkItemState.Prepared, restoredWorkItem.State);
            Assert.Equal(restoredCase.Id, restoredWorkItem.CaseId);
            Assert.Equal(restoredCase.ExecutionRevision, restoredWorkItem.ExecutionRevision);
            Assert.Equal("approved-policy", restoredWorkItem.PolicyId);
            Assert.Equal(12, restoredWorkItem.PolicyPropertyVersion);
            Assert.Equal("GB", restoredWorkItem.PolicyOperatingCountryCode);
            Assert.True(restoredWorkItem.PolicyRequiresDistinctExecutor);
            Assert.True(restoredWorkItem.MatchesPolicyEvidence(
                Assert.IsType<DataRightsApprovalPolicyEvidence>(
                    restoredCase.ApprovalPolicyEvidence)));
        }

        await using DataRightsDbContext tenantB = CreateDbContext(
            databaseName,
            root,
            "tenant-b");
        Assert.Empty(await tenantB.ExecutionWorkItems.ToArrayAsync());
    }

    [Fact]
    public async Task Processing_ledger_model_is_scoped_unique_and_append_only()
    {
        string databaseName = $"data-rights-ledger-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        (DataRightsCase dataRightsCase, DataRightsExecutionWorkItem workItem) =
            CreatePreparedExecution();
        Guid taskRunId = Guid.NewGuid();
        DateTimeOffset proofAt = workItem.CreatedAtUtc.AddMinutes(2);
        Assert.True(workItem.BeginProcessing(
            taskRunId,
            taskAttempt: 1,
            proofAt.AddMinutes(-1)).IsSuccess);
        Assert.True(workItem.RecordOwnerProof(
            workItem.Version,
            taskRunId,
            taskAttempt: 1,
            receiptContractVersion: 1,
            Guid.NewGuid(),
            resultingRecordVersion: workItem.SelectedRecordVersion + 1,
            "guests.completed",
            "guests.profile-anonymised",
            new string('d', 64),
            proofAt,
            proofAt.AddMinutes(1)).IsSuccess);
        DataRightsProcessingLedgerEntry ledgerEntry =
            DataRightsProcessingLedgerEntry.Create(
                Guid.NewGuid(),
                tenantSequence: 1,
                workItem,
                DataRightsRecordPseudonym.Create(
                    1,
                    new string('e', 64)).Value,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256).Value;

        await using (DataRightsDbContext writer = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            IEntityType entity = writer.Model.FindEntityType(
                typeof(DataRightsProcessingLedgerEntry))!;
            IEntityType designEntity = writer.GetService<IDesignTimeModel>()
                .Model
                .FindEntityType(typeof(DataRightsProcessingLedgerEntry))!;
            Assert.Contains(entity.GetIndexes(), index =>
                index.IsUnique &&
                index.Properties.Select(item => item.Name).SequenceEqual([
                    nameof(DataRightsProcessingLedgerEntry.ScopeId),
                    nameof(DataRightsProcessingLedgerEntry.TenantSequence)
                ]));
            Assert.Contains(entity.GetIndexes(), index =>
                index.IsUnique &&
                index.Properties.Select(item => item.Name).SequenceEqual([
                    nameof(DataRightsProcessingLedgerEntry.ScopeId),
                    nameof(DataRightsProcessingLedgerEntry.WorkItemId)
                ]));
            Assert.Contains(entity.GetIndexes(), index =>
                index.IsUnique &&
                index.Properties.Select(item => item.Name).SequenceEqual([
                    nameof(DataRightsProcessingLedgerEntry.ScopeId),
                    nameof(DataRightsProcessingLedgerEntry.OwnerReceiptId)
                ]));
            Assert.Contains(
                designEntity.GetCheckConstraints(),
                constraint =>
                    constraint.Name == "CK_data_rights_processing_ledger_chain");
            Assert.Contains(
                designEntity.GetCheckConstraints(),
                constraint =>
                    constraint.Name ==
                    "CK_data_rights_processing_ledger_scope");
            Assert.Contains(
                designEntity.GetCheckConstraints(),
                constraint =>
                    constraint.Name ==
                    "CK_data_rights_processing_ledger_policy");
            Assert.True(
                entity.FindProperty(
                    nameof(DataRightsProcessingLedgerEntry.RoutingPropertyId))!
                    .IsNullable);
            Assert.Equal(
                DataRightsApprovalPolicyEvidence.StateBindingsJsonMaxLength,
                entity.FindProperty(
                    nameof(DataRightsProcessingLedgerEntry.PolicyStateBindingsJson))!
                    .GetMaxLength());
            Assert.Empty(entity.GetForeignKeys());

            writer.Cases.Add(dataRightsCase);
            writer.ExecutionWorkItems.Add(workItem);
            writer.ProcessingLedgerEntries.Add(ledgerEntry);
            await writer.SaveChangesAsync();

            writer.Entry(ledgerEntry).State = EntityState.Modified;
            InvalidOperationException updateFailure =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => writer.SaveChangesAsync());
            Assert.Contains("append-only", updateFailure.Message);
        }

        await using (DataRightsDbContext cleanup = CreateDbContext(
            databaseName,
            root,
            "tenant-a"))
        {
            cleanup.ExecutionWorkItems.Remove(
                await cleanup.ExecutionWorkItems.SingleAsync());
            await cleanup.SaveChangesAsync();
            Assert.Empty(await cleanup.ExecutionWorkItems.ToArrayAsync());
            Assert.Single(
                await cleanup.ProcessingLedgerEntries.ToArrayAsync());
        }

        await using DataRightsDbContext tenantB = CreateDbContext(
            databaseName,
            root,
            "tenant-b");
        Assert.Empty(await tenantB.ProcessingLedgerEntries.ToArrayAsync());
    }

    private static DataRightsCase CreateCase(string tenantId, Guid propertyId)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        return DataRightsCase.Create(
            Guid.NewGuid(),
            tenantId,
            request,
            "user:operator",
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)).Value;
    }

    private static DataRightsCase CreateTenantCase(
        string tenantId,
        DataRightsCaseKind kind,
        DataRightsRequesterRelation requesterRelationship)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            kind,
            kind == DataRightsCaseKind.TenantTermination
                ? DataRightsCaseOperation.Anonymisation
                : DataRightsCaseOperation.AccessExport,
            requesterRelationship).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            tenantId,
            request,
            "user:operator",
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)).Value;
        if (kind == DataRightsCaseKind.TenantTermination)
        {
            Assert.True(dataRightsCase.PrepareTenantTerminationReview(
                exportRequested: false,
                1,
                "user:operator",
                dataRightsCase.CreatedAtUtc.AddMinutes(1)).IsSuccess);
        }

        return dataRightsCase;
    }

    private static (DataRightsCase Case, DataRightsExecutionWorkItem WorkItem)
        CreatePreparedExecution()
    {
        Guid propertyId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator",
            now).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            1,
            "user:operator",
            now.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            4,
            2,
            "user:operator",
            now.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            3,
            "user:operator",
            now.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            4,
            "user:decision-maker",
            now.AddMinutes(4)).IsSuccess);
        DataRightsApprovalPolicyEvidence evidence =
            DataRightsApprovalPolicyEvidence.Create(
                propertyId,
                12,
                "GB",
                "approved-policy",
                3,
                "guest-retention",
                2,
                new string('c', 64),
                "data-rights-anonymisation",
                "erasure",
                "authorized-workspace-operator",
                now.AddMinutes(5)).Value;
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            5,
            "user:decision-maker",
            now.AddMinutes(5),
            evidence).IsSuccess);
        Assert.True(dataRightsCase.BeginAnonymisationExecution(
            6,
            "user:executor",
            now.AddMinutes(6)).IsSuccess);
        DataRightsExecutionWorkItem workItem = DataRightsExecutionWorkItem.Prepare(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            dataRightsCase.Id,
            DataRightsExecutionScope.ForProperty(propertyId),
            6,
            7,
            DataRightsCaseOperation.Anonymisation,
            Assert.Single(dataRightsCase.SelectedSubjects),
            evidence,
            "user:executor",
            now.AddMinutes(6)).Value;
        return (dataRightsCase, workItem);
    }

    private static DataRightsDbContext CreateDbContext(
        string databaseName,
        InMemoryDatabaseRoot root,
        string tenantId)
    {
        DbContextOptions<DataRightsDbContext> options =
            new DbContextOptionsBuilder<DataRightsDbContext>()
                .UseInMemoryDatabase(databaseName, root)
                .Options;
        return new(options, new TestScopeContext(tenantId));
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}

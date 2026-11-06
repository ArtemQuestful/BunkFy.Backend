namespace BunkFy.Modules.DataRights.Persistence.Configurations;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class DataRightsCaseConfiguration : IEntityTypeConfiguration<DataRightsCase>
{
    public void Configure(EntityTypeBuilder<DataRightsCase> builder)
    {
        builder.ToTable("cases", table =>
        {
            table.HasCheckConstraint("CK_data_rights_cases_version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_data_rights_cases_kind", "\"Kind\" IN (1, 2, 3)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_operations",
                "(\"Kind\" = 1 AND \"RequestedOperations\" BETWEEN 1 AND 31) OR " +
                "(\"Kind\" = 2 AND \"RequestedOperations\" = 16) OR " +
                "(\"Kind\" = 3 AND \"RequestedOperations\" IN (1, 2, 4, 16))");
            table.HasCheckConstraint(
                "CK_data_rights_cases_restriction_directive",
                "((\"RequestedOperations\" & 4) = 0 AND \"RestrictionDirective\" = 0) OR " +
                "((\"RequestedOperations\" & 4) = 4 AND \"RestrictionDirective\" BETWEEN 0 AND 2)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_restriction_target",
                "(\"RestrictionTargetingContractVersion\" IS NULL AND " +
                "\"RestrictionTargetOwnerKey\" IS NULL AND " +
                "\"RestrictionTargetOwnerOperationId\" IS NULL AND " +
                "\"RestrictionTargetOwnerOperationVersion\" IS NULL AND " +
                "\"RestrictionTargetSelectedBy\" IS NULL AND " +
                "\"RestrictionTargetSelectedAtUtc\" IS NULL) OR " +
                "(\"RequestedOperations\" = 4 AND " +
                "\"RestrictionDirective\" = 2 AND " +
                "\"RestrictionTargetingContractVersion\" = 1 AND " +
                "((\"Status\" IN (1, 2, 11) AND " +
                "\"RestrictionTargetOwnerKey\" IS NULL AND " +
                "\"RestrictionTargetOwnerOperationId\" IS NULL AND " +
                "\"RestrictionTargetOwnerOperationVersion\" IS NULL AND " +
                "\"RestrictionTargetSelectedBy\" IS NULL AND " +
                "\"RestrictionTargetSelectedAtUtc\" IS NULL) OR " +
                "(length(trim(\"RestrictionTargetOwnerKey\")) > 0 AND " +
                "\"RestrictionTargetOwnerOperationId\" IS NOT NULL AND " +
                "\"RestrictionTargetOwnerOperationVersion\" BETWEEN 1 AND " +
                    "9223372036854775806 AND " +
                "length(trim(\"RestrictionTargetSelectedBy\")) > 0 AND " +
                "\"RestrictionTargetSelectedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "\"RestrictionTargetSelectedAtUtc\" <= \"LastChangedAtUtc\")))");
            table.HasCheckConstraint(
                "CK_data_rights_cases_requester",
                "\"RequesterRelationship\" IN (1, 2, 3, 4)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_requester_scope",
                "(\"Kind\" IN (1, 3) AND \"RequesterRelationship\" IN (1, 2, 3)) OR " +
                "(\"Kind\" = 2 AND \"RequesterRelationship\" IN (3, 4))");
            table.HasCheckConstraint(
                "CK_data_rights_cases_verification",
                "\"VerificationStatus\" IN (1, 2, 3, 4)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_routing",
                "\"RoutingStatus\" IN (1, 2, 3)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_status",
                "\"Status\" BETWEEN 1 AND 11");
            table.HasCheckConstraint(
                "CK_data_rights_cases_decision",
                "\"Decision\" BETWEEN 0 AND 2");
            table.HasCheckConstraint(
                "CK_data_rights_cases_decision_reason",
                "\"DecisionReason\" BETWEEN 0 AND 6");
            table.HasCheckConstraint(
                "CK_data_rights_cases_decision_reason_match",
                "(\"Decision\" = 0 AND \"DecisionReason\" = 0) OR " +
                "(\"Decision\" = 1 AND \"DecisionReason\" = 1) OR " +
                "(\"Decision\" = 2 AND \"DecisionReason\" BETWEEN 2 AND 6)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_decision_details",
                "(\"Decision\" = 0 AND \"DecisionRevision\" IS NULL AND " +
                "\"DecidedBy\" IS NULL AND \"DecidedAtUtc\" IS NULL) OR " +
                "(\"Decision\" IN (1, 2) AND \"DecisionRevision\" IS NOT NULL AND " +
                "\"DecisionRevision\" >= 1 AND \"DecisionRevision\" <= \"Version\" AND " +
                "\"DecidedBy\" IS NOT NULL AND \"DecidedAtUtc\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_decision_attribution",
                "\"DecidedBy\" IS NULL OR (length(trim(\"DecidedBy\")) > 0 AND " +
                "\"DecidedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "\"DecidedAtUtc\" <= \"LastChangedAtUtc\")");
            table.HasCheckConstraint(
                "CK_data_rights_cases_decision_state",
                "(\"Decision\" = 0 AND \"Status\" IN (1, 2, 3, 4, 11)) OR " +
                "(\"Decision\" = 1 AND \"Status\" IN (5, 7, 8, 9, 10, 11)) OR " +
                "(\"Decision\" = 2 AND \"Status\" = 6)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_execution",
                "(\"ExecutionRevision\" IS NULL AND \"ExecutionStartedBy\" IS NULL AND " +
                "\"ExecutionStartedAtUtc\" IS NULL AND " +
                "(\"Status\" IN (1, 2, 3, 4, 5, 6, 11) OR " +
                "(\"Status\" = 9 AND \"Decision\" = 1 AND \"RequestedOperations\" = 1))) OR " +
                "(\"ExecutionRevision\" IS NOT NULL AND \"ExecutionRevision\" > \"DecisionRevision\" AND " +
                "\"ExecutionRevision\" <= \"Version\" AND \"ExecutionStartedBy\" IS NOT NULL AND " +
                "\"ExecutionStartedAtUtc\" IS NOT NULL AND \"Decision\" = 1 AND " +
                "\"Status\" IN (7, 8, 9, 10, 11))");
            table.HasCheckConstraint(
                "CK_data_rights_cases_execution_attribution",
                "\"ExecutionStartedBy\" IS NULL OR " +
                "(length(trim(\"ExecutionStartedBy\")) > 0 AND " +
                "\"ExecutionStartedAtUtc\" >= \"DecidedAtUtc\" AND " +
                "\"ExecutionStartedAtUtc\" <= \"LastChangedAtUtc\")");
            table.HasCheckConstraint(
                "CK_data_rights_cases_approval_policy_evidence",
                "(\"Kind\" <> 2 AND \"Decision\" = 1 AND " +
                "\"RequestedOperations\" = 16 AND " +
                "\"ApprovalEvidenceSchemaVersion\" IN (1, 2) AND " +
                "\"ApprovalEvidenceCaseKind\" = \"Kind\" AND " +
                "\"ApprovalEvidenceOperatingCountryCode\" IS NOT NULL AND " +
                "char_length(\"ApprovalEvidenceOperatingCountryCode\") = 2 AND " +
                "\"ApprovalEvidencePolicyId\" IS NOT NULL AND " +
                "length(trim(\"ApprovalEvidencePolicyId\")) > 0 AND " +
                "\"ApprovalEvidencePolicyVersion\" > 0 AND " +
                "\"ApprovalEvidenceRetentionPolicyId\" IS NOT NULL AND " +
                "length(trim(\"ApprovalEvidenceRetentionPolicyId\")) > 0 AND " +
                "\"ApprovalEvidenceRetentionPolicyVersion\" > 0 AND " +
                "\"ApprovalEvidenceContentSha256\" IS NOT NULL AND " +
                "char_length(\"ApprovalEvidenceContentSha256\") = 64 AND " +
                "\"ApprovalEvidencePurposeCode\" IS NOT NULL AND " +
                "length(trim(\"ApprovalEvidencePurposeCode\")) > 0 AND " +
                "\"ApprovalEvidenceSurface\" = 'erasure' AND " +
                "\"ApprovalEvidenceSourceProvenance\" IS NOT NULL AND " +
                "length(trim(\"ApprovalEvidenceSourceProvenance\")) > 0 AND " +
                "\"ApprovalEvidenceEvaluatedAtUtc\" IS NOT NULL AND " +
                "\"ApprovalEvidenceRequiresDistinctExecutor\" = TRUE AND " +
                "((\"ApprovalEvidenceSchemaVersion\" = 1 AND " +
                "\"Kind\" = 1 AND \"ApprovalEvidenceScopeKind\" = 1 AND " +
                "\"ApprovalEvidencePropertyId\" = \"PropertyId\" AND " +
                "\"ApprovalEvidencePropertyVersion\" > 0 AND " +
                "\"ApprovalEvidencePurposeCode\" = 'data-rights-anonymisation' AND " +
                "\"ApprovalEvidenceSourceProvenance\" = " +
                    "'authorized-workspace-operator' AND " +
                "\"ApprovalEvidenceRetentionDataClass\" = '' AND " +
                "\"ApprovalEvidenceRetentionTrigger\" = '' AND " +
                "\"ApprovalEvidenceRetentionTriggeredAtUtc\" IS NULL AND " +
                "\"ApprovalEvidenceRetentionDeadlineUtc\" IS NULL AND " +
                "\"ApprovalEvidenceStateBindingsJson\" = '[]' AND " +
                "\"ApprovalEvidenceStateBindingsSha256\" IS NOT NULL AND " +
                "char_length(\"ApprovalEvidenceStateBindingsSha256\") = 64) OR " +
                "(\"ApprovalEvidenceSchemaVersion\" = 2 AND " +
                "((\"Kind\" = 1 AND \"ApprovalEvidenceScopeKind\" = 1 AND " +
                "\"ApprovalEvidencePropertyId\" = \"PropertyId\" AND " +
                "\"ApprovalEvidencePropertyVersion\" > 0) OR " +
                "(\"Kind\" IN (2, 3) AND " +
                "\"ApprovalEvidenceScopeKind\" = 2 AND " +
                "\"PropertyId\" IS NULL AND " +
                "\"ApprovalEvidencePropertyId\" IS NULL AND " +
                "\"ApprovalEvidencePropertyVersion\" = 0)) AND " +
                "\"ApprovalEvidenceRetentionDataClass\" IS NOT NULL AND " +
                "length(trim(\"ApprovalEvidenceRetentionDataClass\")) > 0 AND " +
                "\"ApprovalEvidenceRetentionTrigger\" IS NOT NULL AND " +
                "length(trim(\"ApprovalEvidenceRetentionTrigger\")) > 0 AND " +
                "\"ApprovalEvidenceRetentionTriggeredAtUtc\" IS NOT NULL AND " +
                "\"ApprovalEvidenceRetentionDeadlineUtc\" > " +
                    "\"ApprovalEvidenceRetentionTriggeredAtUtc\" AND " +
                "\"ApprovalEvidenceRetentionDeadlineUtc\" <= " +
                    "\"ApprovalEvidenceEvaluatedAtUtc\" AND " +
                "\"ApprovalEvidenceStateBindingsJson\" IS NOT NULL AND " +
                "char_length(\"ApprovalEvidenceStateBindingsJson\") > 2 AND " +
                "\"ApprovalEvidenceStateBindingsSha256\" IS NOT NULL AND " +
                "char_length(\"ApprovalEvidenceStateBindingsSha256\") = 64))) OR " +
                "((\"Kind\" = 2 OR \"Decision\" <> 1 OR " +
                "\"RequestedOperations\" <> 16) AND " +
                "\"ApprovalEvidenceSchemaVersion\" IS NULL AND " +
                "\"ApprovalEvidenceCaseKind\" IS NULL AND " +
                "\"ApprovalEvidenceScopeKind\" IS NULL AND " +
                "\"ApprovalEvidencePropertyId\" IS NULL AND " +
                "\"ApprovalEvidencePropertyVersion\" IS NULL AND " +
                "\"ApprovalEvidenceOperatingCountryCode\" IS NULL AND " +
                "\"ApprovalEvidencePolicyId\" IS NULL AND " +
                "\"ApprovalEvidencePolicyVersion\" IS NULL AND " +
                "\"ApprovalEvidenceRetentionPolicyId\" IS NULL AND " +
                "\"ApprovalEvidenceRetentionPolicyVersion\" IS NULL AND " +
                "\"ApprovalEvidenceContentSha256\" IS NULL AND " +
                "\"ApprovalEvidencePurposeCode\" IS NULL AND " +
                "\"ApprovalEvidenceSurface\" IS NULL AND " +
                "\"ApprovalEvidenceSourceProvenance\" IS NULL AND " +
                "\"ApprovalEvidenceRetentionDataClass\" IS NULL AND " +
                "\"ApprovalEvidenceRetentionTrigger\" IS NULL AND " +
                "\"ApprovalEvidenceRetentionTriggeredAtUtc\" IS NULL AND " +
                "\"ApprovalEvidenceRetentionDeadlineUtc\" IS NULL AND " +
                "\"ApprovalEvidenceEvaluatedAtUtc\" IS NULL AND " +
                "\"ApprovalEvidenceStateBindingsJson\" IS NULL AND " +
                "\"ApprovalEvidenceStateBindingsSha256\" IS NULL AND " +
                "\"ApprovalEvidenceRequiresDistinctExecutor\" IS NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_tenant_termination",
                "(\"Kind\" = 2 AND " +
                "\"TenantTerminationExportRequested\" IS NOT NULL AND " +
                "((\"Decision\" = 1 AND " +
                "\"TenantTerminationPolicyEvidenceSha256\" IS NOT NULL AND " +
                "char_length(\"TenantTerminationPolicyEvidenceSha256\") = 64 AND " +
                "\"TenantTerminationPolicyEvidenceSha256\" ~ '^[0-9a-f]{64}$') OR " +
                "(\"Decision\" <> 1 AND " +
                "\"TenantTerminationPolicyEvidenceSha256\" IS NULL))) OR " +
                "(\"Kind\" <> 2 AND " +
                "\"TenantTerminationExportRequested\" IS NULL AND " +
                "\"TenantTerminationPolicyEvidenceSha256\" IS NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_restriction_execution_proof",
                "((\"RequestedOperations\" = 4 AND \"Status\" = 9) AND " +
                "\"RestrictionExecutionIdempotencyKey\" IS NOT NULL AND " +
                "\"RestrictionExecutionApprovalRevision\" = \"DecisionRevision\" AND " +
                "\"RestrictionExecutionDirective\" = \"RestrictionDirective\" AND " +
                "length(trim(\"RestrictionExecutionOwnerKey\")) > 0 AND " +
                "length(trim(\"RestrictionExecutionRecordType\")) > 0 AND " +
                "\"RestrictionExecutionRecordId\" IS NOT NULL AND " +
                "\"RestrictionExecutionSelectedRecordVersion\" >= 1 AND " +
                "\"RestrictionExecutionReceiptContractVersion\" >= 1 AND " +
                "\"RestrictionExecutionReceiptId\" IS NOT NULL AND " +
                "\"RestrictionExecutionOwnerOperationId\" IS NOT NULL AND " +
                "\"RestrictionExecutionResultingOwnerRevision\" >= 1 AND " +
                "\"RestrictionExecutionResultingProjectionRevision\" >= 1 AND " +
                "((\"RestrictionExecutionDirective\" = 1 AND " +
                "\"RestrictionTargetingContractVersion\" IS NULL AND " +
                "\"RestrictionExecutionEffectiveRestricted\" = TRUE) OR " +
                "(\"RestrictionExecutionDirective\" = 2 AND " +
                "((\"RestrictionTargetingContractVersion\" IS NULL AND " +
                "\"RestrictionExecutionEffectiveRestricted\" = FALSE) OR " +
                "(\"RestrictionTargetingContractVersion\" = 1 AND " +
                "\"RestrictionExecutionOwnerOperationId\" = " +
                    "\"RestrictionTargetOwnerOperationId\" AND " +
                "\"RestrictionExecutionResultingOwnerRevision\" = " +
                    "\"RestrictionTargetOwnerOperationVersion\" + 1)))) AND " +
                "\"RestrictionExecutionReceiptSha256\" IS NOT NULL AND " +
                "char_length(\"RestrictionExecutionReceiptSha256\") = 64 AND " +
                "length(trim(\"RestrictionExecutionExecutedBy\")) > 0 AND " +
                "\"RestrictionExecutionCompletedAtUtc\" >= \"DecidedAtUtc\" AND " +
                "\"RestrictionExecutionCompletedAtUtc\" <= \"LastChangedAtUtc\") OR " +
                "((\"RequestedOperations\" <> 4 OR \"Status\" <> 9) AND " +
                "\"RestrictionExecutionIdempotencyKey\" IS NULL AND " +
                "\"RestrictionExecutionApprovalRevision\" IS NULL AND " +
                "\"RestrictionExecutionDirective\" IS NULL AND " +
                "\"RestrictionExecutionOwnerKey\" IS NULL AND " +
                "\"RestrictionExecutionRecordType\" IS NULL AND " +
                "\"RestrictionExecutionRecordId\" IS NULL AND " +
                "\"RestrictionExecutionSelectedRecordVersion\" IS NULL AND " +
                "\"RestrictionExecutionReceiptContractVersion\" IS NULL AND " +
                "\"RestrictionExecutionReceiptId\" IS NULL AND " +
                "\"RestrictionExecutionOwnerOperationId\" IS NULL AND " +
                "\"RestrictionExecutionResultingOwnerRevision\" IS NULL AND " +
                "\"RestrictionExecutionResultingProjectionRevision\" IS NULL AND " +
                "\"RestrictionExecutionEffectiveRestricted\" IS NULL AND " +
                "\"RestrictionExecutionReceiptSha256\" IS NULL AND " +
                "\"RestrictionExecutionExecutedBy\" IS NULL AND " +
                "\"RestrictionExecutionCompletedAtUtc\" IS NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_property_scope",
                "(\"Kind\" = 1 AND \"PropertyId\" IS NOT NULL) OR " +
                "(\"Kind\" IN (2, 3) AND \"PropertyId\" IS NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_cases_timestamps",
                "\"LastChangedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "(\"DueAtUtc\" IS NULL OR \"DueAtUtc\" >= \"CreatedAtUtc\")");
            table.HasCheckConstraint(
                "CK_data_rights_cases_response_deadline_evidence",
                "(\"ResponseDeadlineSchemaVersion\" IS NULL AND " +
                "\"ResponseDeadlinePropertyId\" IS NULL AND " +
                "\"ResponseDeadlineTopologySourceVersion\" IS NULL AND " +
                "\"ResponseDeadlinePolicySourceVersion\" IS NULL AND " +
                "\"ResponseDeadlineOperatingCountryCode\" IS NULL AND " +
                "\"ResponseDeadlinePolicyId\" IS NULL AND " +
                "\"ResponseDeadlinePolicyVersion\" IS NULL AND " +
                "\"ResponseDeadlineContentSha256\" IS NULL AND " +
                "\"ResponseDeadlineControllingRight\" IS NULL AND " +
                "\"ResponseDeadlineRuleReference\" IS NULL AND " +
                "\"ResponseDeadlinePeriodYears\" IS NULL AND " +
                "\"ResponseDeadlinePeriodMonths\" IS NULL AND " +
                "\"ResponseDeadlinePeriodDays\" IS NULL AND " +
                "\"ResponseDeadlineTimeZoneId\" IS NULL AND " +
                "\"ResponseDeadlinePolicyEffectiveAtUtc\" IS NULL AND " +
                "\"ResponseDeadlinePolicyExpiresAtUtc\" IS NULL AND " +
                "\"ResponseDeadlineReceivedAtUtc\" IS NULL AND " +
                "\"ResponseDeadlineEvaluatedAtUtc\" IS NULL AND " +
                "\"ResponseDeadlineDueAtUtc\" IS NULL AND " +
                "\"DueAtUtc\" IS NULL) OR " +
                "(\"Kind\" = 1 AND \"RequesterRelationship\" IN (1, 2) AND " +
                "\"ResponseDeadlineSchemaVersion\" = 1 AND " +
                "\"ResponseDeadlinePropertyId\" = \"PropertyId\" AND " +
                "\"ResponseDeadlineTopologySourceVersion\" > 0 AND " +
                "\"ResponseDeadlinePolicySourceVersion\" > 0 AND " +
                "char_length(\"ResponseDeadlineOperatingCountryCode\") = 2 AND " +
                "length(trim(\"ResponseDeadlinePolicyId\")) > 0 AND " +
                "\"ResponseDeadlinePolicyVersion\" > 0 AND " +
                "char_length(\"ResponseDeadlineContentSha256\") = 64 AND " +
                "\"ResponseDeadlineControllingRight\" BETWEEN 1 AND 4 AND " +
                "length(trim(\"ResponseDeadlineRuleReference\")) > 0 AND " +
                "\"ResponseDeadlinePeriodYears\" BETWEEN 0 AND 10 AND " +
                "\"ResponseDeadlinePeriodMonths\" BETWEEN 0 AND 11 AND " +
                "\"ResponseDeadlinePeriodDays\" BETWEEN 0 AND 366 AND " +
                "(\"ResponseDeadlinePeriodYears\" + " +
                    "\"ResponseDeadlinePeriodMonths\" + " +
                    "\"ResponseDeadlinePeriodDays\") > 0 AND " +
                "length(trim(\"ResponseDeadlineTimeZoneId\")) > 0 AND " +
                "\"ResponseDeadlinePolicyEffectiveAtUtc\" < " +
                    "\"ResponseDeadlinePolicyExpiresAtUtc\" AND " +
                "\"ResponseDeadlineReceivedAtUtc\" = \"CreatedAtUtc\" AND " +
                "\"ResponseDeadlineReceivedAtUtc\" >= " +
                    "\"ResponseDeadlinePolicyEffectiveAtUtc\" AND " +
                "\"ResponseDeadlineReceivedAtUtc\" < " +
                    "\"ResponseDeadlinePolicyExpiresAtUtc\" AND " +
                "\"ResponseDeadlineEvaluatedAtUtc\" >= " +
                    "\"ResponseDeadlineReceivedAtUtc\" AND " +
                "\"ResponseDeadlineDueAtUtc\" = \"DueAtUtc\" AND " +
                "\"ResponseDeadlineDueAtUtc\" > \"CreatedAtUtc\")");
            table.HasCheckConstraint(
                "CK_data_rights_cases_created_by",
                "length(trim(\"CreatedBy\")) > 0");
            table.HasCheckConstraint(
                "CK_data_rights_cases_last_changed_by",
                "length(trim(\"LastChangedBy\")) > 0");
        });
        builder.HasKey(dataRightsCase => dataRightsCase.Id);
        builder.HasAlternateKey(dataRightsCase => new
        {
            dataRightsCase.ScopeId,
            dataRightsCase.Id
        });
        builder.Property(dataRightsCase => dataRightsCase.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.Kind)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.RequestedOperations)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.RestrictionAction)
            .HasColumnName("RestrictionDirective")
            .HasConversion<int>()
            .IsRequired();
        builder.Property(dataRightsCase =>
                dataRightsCase.RestrictionTargetingContractVersion)
            .HasColumnName("RestrictionTargetingContractVersion");
        builder.Property(dataRightsCase => dataRightsCase.RequesterRelationship)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.VerificationStatus)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.RoutingStatus)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.Status)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.Decision)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.DecisionReason)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.DecidedBy)
            .HasMaxLength(DataRightsCase.ActorIdMaxLength);
        builder.Property(dataRightsCase => dataRightsCase.ExecutionStartedBy)
            .HasMaxLength(DataRightsCase.ActorIdMaxLength);
        builder.Property(dataRightsCase =>
                dataRightsCase.TenantTerminationPolicyEvidenceSha256)
            .HasMaxLength(TenantTerminationProcess.Sha256Length)
            .IsFixedLength();
        builder.Property(dataRightsCase => dataRightsCase.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.CreatedBy)
            .HasMaxLength(DataRightsCase.ActorIdMaxLength)
            .IsRequired();
        builder.Property(dataRightsCase => dataRightsCase.LastChangedBy)
            .HasMaxLength(DataRightsCase.ActorIdMaxLength)
            .IsRequired();
        builder.HasIndex(dataRightsCase => new
        {
            dataRightsCase.ScopeId,
            dataRightsCase.PropertyId,
            dataRightsCase.Status,
            dataRightsCase.CreatedAtUtc,
            dataRightsCase.Id
        });
        builder.HasIndex(dataRightsCase => new
        {
            dataRightsCase.ScopeId,
            dataRightsCase.Kind,
            dataRightsCase.RequesterRelationship,
            dataRightsCase.DueAtUtc,
            dataRightsCase.Id
        });
        builder.HasIndex(dataRightsCase => dataRightsCase.ScopeId)
            .HasDatabaseName(
                "UX_data_rights_cases_active_tenant_termination")
            .HasFilter("\"Kind\" = 2 AND \"Status\" NOT IN (6, 9, 11)")
            .IsUnique();
        builder.OwnsOne(dataRightsCase => dataRightsCase.ApprovalPolicyEvidence, evidence =>
        {
            evidence.Property(value => value.SchemaVersion)
                .HasColumnName("ApprovalEvidenceSchemaVersion");
            evidence.Property(value => value.CaseKind)
                .HasColumnName("ApprovalEvidenceCaseKind")
                .HasConversion<int>();
            evidence.Property(value => value.ScopeKind)
                .HasColumnName("ApprovalEvidenceScopeKind")
                .HasConversion<int>();
            evidence.Property(value => value.PropertyId)
                .HasColumnName("ApprovalEvidencePropertyId");
            evidence.Property(value => value.PropertyVersion)
                .HasColumnName("ApprovalEvidencePropertyVersion");
            evidence.Property(value => value.OperatingCountryCode)
                .HasColumnName("ApprovalEvidenceOperatingCountryCode")
                .HasMaxLength(DataRightsApprovalPolicyEvidence.CountryCodeLength);
            evidence.Property(value => value.PolicyId)
                .HasColumnName("ApprovalEvidencePolicyId")
                .HasMaxLength(DataRightsApprovalPolicyEvidence.KeyMaxLength);
            evidence.Property(value => value.PolicyVersion)
                .HasColumnName("ApprovalEvidencePolicyVersion");
            evidence.Property(value => value.RetentionPolicyId)
                .HasColumnName("ApprovalEvidenceRetentionPolicyId")
                .HasMaxLength(DataRightsApprovalPolicyEvidence.KeyMaxLength);
            evidence.Property(value => value.RetentionPolicyVersion)
                .HasColumnName("ApprovalEvidenceRetentionPolicyVersion");
            evidence.Property(value => value.ContentSha256)
                .HasColumnName("ApprovalEvidenceContentSha256")
                .HasMaxLength(DataRightsApprovalPolicyEvidence.ContentSha256Length);
            evidence.Property(value => value.PurposeCode)
                .HasColumnName("ApprovalEvidencePurposeCode")
                .HasMaxLength(DataRightsApprovalPolicyEvidence.KeyMaxLength);
            evidence.Property(value => value.Surface)
                .HasColumnName("ApprovalEvidenceSurface")
                .HasMaxLength(DataRightsApprovalPolicyEvidence.KeyMaxLength);
            evidence.Property(value => value.SourceProvenance)
                .HasColumnName("ApprovalEvidenceSourceProvenance")
                .HasMaxLength(DataRightsApprovalPolicyEvidence.KeyMaxLength);
            evidence.Property(value => value.RetentionDataClass)
                .HasColumnName("ApprovalEvidenceRetentionDataClass")
                .HasMaxLength(DataRightsApprovalPolicyEvidence.KeyMaxLength);
            evidence.Property(value => value.RetentionTrigger)
                .HasColumnName("ApprovalEvidenceRetentionTrigger")
                .HasMaxLength(DataRightsApprovalPolicyEvidence.KeyMaxLength);
            evidence.Property(value => value.RetentionTriggeredAtUtc)
                .HasColumnName("ApprovalEvidenceRetentionTriggeredAtUtc");
            evidence.Property(value => value.RetentionDeadlineUtc)
                .HasColumnName("ApprovalEvidenceRetentionDeadlineUtc");
            evidence.Property(value => value.EvaluatedAtUtc)
                .HasColumnName("ApprovalEvidenceEvaluatedAtUtc");
            evidence.Property(value => value.StateBindingsJson)
                .HasColumnName("ApprovalEvidenceStateBindingsJson")
                .HasMaxLength(
                    DataRightsApprovalPolicyEvidence
                        .StateBindingsJsonMaxLength);
            evidence.Property(value => value.StateBindingsSha256)
                .HasColumnName("ApprovalEvidenceStateBindingsSha256")
                .HasMaxLength(
                    DataRightsApprovalPolicyEvidence.ContentSha256Length)
                .IsFixedLength();
            evidence.Property(value => value.RequiresDistinctExecutor)
                .HasColumnName("ApprovalEvidenceRequiresDistinctExecutor");
            evidence.Ignore(value => value.StateBindings);
        });
        builder.OwnsOne(
            dataRightsCase => dataRightsCase.ResponseDeadlinePolicyEvidence,
            evidence =>
            {
                evidence.Property(value => value.SchemaVersion)
                    .HasColumnName("ResponseDeadlineSchemaVersion");
                evidence.Property(value => value.PropertyId)
                    .HasColumnName("ResponseDeadlinePropertyId");
                evidence.Property(value => value.PropertyTopologySourceVersion)
                    .HasColumnName("ResponseDeadlineTopologySourceVersion");
                evidence.Property(value => value.PropertyPolicySourceVersion)
                    .HasColumnName("ResponseDeadlinePolicySourceVersion");
                evidence.Property(value => value.OperatingCountryCode)
                    .HasColumnName("ResponseDeadlineOperatingCountryCode")
                    .HasMaxLength(
                        DataRightsResponseDeadlinePolicyEvidence.CountryCodeLength);
                evidence.Property(value => value.PolicyId)
                    .HasColumnName("ResponseDeadlinePolicyId")
                    .HasMaxLength(
                        DataRightsResponseDeadlinePolicyEvidence.KeyMaxLength);
                evidence.Property(value => value.PolicyVersion)
                    .HasColumnName("ResponseDeadlinePolicyVersion");
                evidence.Property(value => value.ContentSha256)
                    .HasColumnName("ResponseDeadlineContentSha256")
                    .HasMaxLength(
                        DataRightsResponseDeadlinePolicyEvidence.ContentSha256Length)
                    .IsFixedLength();
                evidence.Property(value => value.ControllingRight)
                    .HasColumnName("ResponseDeadlineControllingRight")
                    .HasConversion<int>();
                evidence.Property(value => value.RuleReference)
                    .HasColumnName("ResponseDeadlineRuleReference")
                    .HasMaxLength(
                        DataRightsResponseDeadlinePolicyEvidence.KeyMaxLength);
                evidence.Property(value => value.PeriodYears)
                    .HasColumnName("ResponseDeadlinePeriodYears");
                evidence.Property(value => value.PeriodMonths)
                    .HasColumnName("ResponseDeadlinePeriodMonths");
                evidence.Property(value => value.PeriodDays)
                    .HasColumnName("ResponseDeadlinePeriodDays");
                evidence.Property(value => value.TimeZoneId)
                    .HasColumnName("ResponseDeadlineTimeZoneId")
                    .HasMaxLength(
                        DataRightsResponseDeadlinePolicyEvidence.TimeZoneIdMaxLength);
                evidence.Property(value => value.PolicyEffectiveAtUtc)
                    .HasColumnName("ResponseDeadlinePolicyEffectiveAtUtc");
                evidence.Property(value => value.PolicyExpiresAtUtc)
                    .HasColumnName("ResponseDeadlinePolicyExpiresAtUtc");
                evidence.Property(value => value.ReceivedAtUtc)
                    .HasColumnName("ResponseDeadlineReceivedAtUtc");
                evidence.Property(value => value.EvaluatedAtUtc)
                    .HasColumnName("ResponseDeadlineEvaluatedAtUtc");
                evidence.Property(value => value.DueAtUtc)
                    .HasColumnName("ResponseDeadlineDueAtUtc");
            });
        builder.OwnsOne(dataRightsCase => dataRightsCase.RestrictionExecutionProof, proof =>
        {
            proof.Property(value => value.IdempotencyKey)
                .HasColumnName("RestrictionExecutionIdempotencyKey");
            proof.Property(value => value.ApprovalRevision)
                .HasColumnName("RestrictionExecutionApprovalRevision");
            proof.Property(value => value.Directive)
                .HasColumnName("RestrictionExecutionDirective")
                .HasConversion<int>();
            proof.Property(value => value.OwnerKey)
                .HasColumnName("RestrictionExecutionOwnerKey")
                .HasMaxLength(DataRightsSubjectCoordinate.OwnerKeyMaxLength);
            proof.Property(value => value.RecordType)
                .HasColumnName("RestrictionExecutionRecordType")
                .HasMaxLength(DataRightsSubjectCoordinate.RecordTypeMaxLength);
            proof.Property(value => value.RecordId)
                .HasColumnName("RestrictionExecutionRecordId");
            proof.Property(value => value.SelectedRecordVersion)
                .HasColumnName("RestrictionExecutionSelectedRecordVersion");
            proof.Property(value => value.ReceiptContractVersion)
                .HasColumnName("RestrictionExecutionReceiptContractVersion");
            proof.Property(value => value.ReceiptId)
                .HasColumnName("RestrictionExecutionReceiptId");
            proof.Property(value => value.OwnerOperationId)
                .HasColumnName("RestrictionExecutionOwnerOperationId");
            proof.Property(value => value.ResultingOwnerRevision)
                .HasColumnName("RestrictionExecutionResultingOwnerRevision");
            proof.Property(value => value.ResultingProjectionRevision)
                .HasColumnName("RestrictionExecutionResultingProjectionRevision");
            proof.Property(value => value.EffectiveRestricted)
                .HasColumnName("RestrictionExecutionEffectiveRestricted");
            proof.Property(value => value.ReceiptSha256)
                .HasColumnName("RestrictionExecutionReceiptSha256")
                .HasMaxLength(DataRightsRestrictionExecutionProof.Sha256Length)
                .IsFixedLength();
            proof.Property(value => value.ExecutedBy)
                .HasColumnName("RestrictionExecutionExecutedBy")
                .HasMaxLength(DataRightsCase.ActorIdMaxLength);
            proof.Property(value => value.CompletedAtUtc)
                .HasColumnName("RestrictionExecutionCompletedAtUtc");
        });
        builder.OwnsOne(
            dataRightsCase => dataRightsCase.RestrictionReleaseTarget,
            target =>
            {
                target.Property(value => value.OwnerKey)
                    .HasColumnName("RestrictionTargetOwnerKey")
                    .HasMaxLength(DataRightsSubjectCoordinate.OwnerKeyMaxLength);
                target.Property(value => value.OwnerOperationId)
                    .HasColumnName("RestrictionTargetOwnerOperationId");
                target.Property(value => value.OwnerOperationVersion)
                    .HasColumnName("RestrictionTargetOwnerOperationVersion");
                target.Property(value => value.SelectedBy)
                    .HasColumnName("RestrictionTargetSelectedBy")
                    .HasMaxLength(DataRightsCase.ActorIdMaxLength);
                target.Property(value => value.SelectedAtUtc)
                    .HasColumnName("RestrictionTargetSelectedAtUtc");
            });
        builder.OwnsMany(dataRightsCase => dataRightsCase.SelectedSubjects, subjects =>
        {
            subjects.ToTable("selected_subjects", table =>
            {
                table.HasCheckConstraint(
                    "CK_data_rights_selected_subjects_owner",
                    "length(trim(\"OwnerKey\")) > 0");
                table.HasCheckConstraint(
                    "CK_data_rights_selected_subjects_record_type",
                    "length(trim(\"RecordType\")) > 0");
                table.HasCheckConstraint(
                    "CK_data_rights_selected_subjects_record_version",
                    "\"RecordVersion\" >= 1");
                table.HasCheckConstraint(
                    "CK_data_rights_selected_subjects_selected_by",
                    "length(trim(\"SelectedBy\")) > 0");
            });
            subjects.WithOwner().HasForeignKey("CaseId");
            subjects.Property<Guid>("CaseId");
            subjects.HasKey(
                "CaseId",
                nameof(DataRightsSubjectCoordinate.OwnerKey),
                nameof(DataRightsSubjectCoordinate.RecordType),
                nameof(DataRightsSubjectCoordinate.RecordId));
            subjects.Property(subject => subject.OwnerKey)
                .HasMaxLength(DataRightsSubjectCoordinate.OwnerKeyMaxLength)
                .IsRequired();
            subjects.Property(subject => subject.RecordType)
                .HasMaxLength(DataRightsSubjectCoordinate.RecordTypeMaxLength)
                .IsRequired();
            subjects.Property(subject => subject.RecordId).ValueGeneratedNever();
            subjects.Property(subject => subject.RecordVersion).IsRequired();
            subjects.Property(subject => subject.SelectedBy)
                .HasMaxLength(DataRightsCase.ActorIdMaxLength)
                .IsRequired();
            subjects.Property(subject => subject.SelectedAtUtc).IsRequired();
        });
        builder.Navigation(dataRightsCase => dataRightsCase.SelectedSubjects)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(dataRightsCase => dataRightsCase.DomainEvents);
    }
}

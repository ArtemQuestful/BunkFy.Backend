namespace BunkFy.Modules.DataRights.Persistence.Configurations;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class DataRightsExportArtifactConfiguration
    : IEntityTypeConfiguration<DataRightsExportArtifact>
{
    public void Configure(EntityTypeBuilder<DataRightsExportArtifact> builder)
    {
        builder.ToTable("export_artifacts", table =>
        {
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_scope",
                $"(\"CaseKind\" = {(int)DataRightsCaseKind.GuestRights} AND " +
                "\"PropertyId\" IS NOT NULL) OR " +
                $"(\"CaseKind\" = {(int)DataRightsCaseKind.StaffRights} AND " +
                "\"PropertyId\" IS NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_subject_count",
                $"\"SelectedSubjectCount\" BETWEEN 1 AND " +
                DataRightsCase.MaxSelectedSubjects);
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_state",
                $"\"State\" BETWEEN {(int)DataRightsExportArtifactState.Requested} AND " +
                (int)DataRightsExportArtifactState.Deleted);
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_revision",
                "\"DecisionRevision\" > 0");
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_version",
                "\"Version\" >= 1");
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_retry_version",
                "\"LastRetryBaseVersion\" IS NULL OR " +
                "(\"LastRetryBaseVersion\" >= 1 AND " +
                "\"LastRetryBaseVersion\" < \"Version\")");
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_timestamps",
                "\"ExpiresAtUtc\" > \"RequestedAtUtc\" AND " +
                "(\"GenerationStartedAtUtc\" IS NULL OR " +
                "(\"GenerationStartedAtUtc\" >= \"RequestedAtUtc\" AND " +
                "\"GenerationStartedAtUtc\" < \"ExpiresAtUtc\")) AND " +
                "(\"AvailableAtUtc\" IS NULL OR " +
                "(\"GenerationStartedAtUtc\" IS NOT NULL AND " +
                "\"AvailableAtUtc\" >= \"GenerationStartedAtUtc\" AND " +
                "\"AvailableAtUtc\" < \"ExpiresAtUtc\")) AND " +
                "(\"DeletionStartedAtUtc\" IS NULL OR " +
                "\"DeletionStartedAtUtc\" >= \"ExpiresAtUtc\") AND " +
                "(\"DeletedAtUtc\" IS NULL OR " +
                "(\"DeletionStartedAtUtc\" IS NOT NULL AND " +
                "\"DeletedAtUtc\" >= \"DeletionStartedAtUtc\"))");
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_generation_shape",
                "(\"GenerationActor\" IS NULL AND \"GenerationRunId\" IS NULL AND " +
                "\"GenerationAttempt\" IS NULL AND \"GenerationStartedAtUtc\" IS NULL) OR " +
                "(\"GenerationActor\" IS NOT NULL AND \"GenerationRunId\" IS NOT NULL AND " +
                "\"GenerationAttempt\" > 0 AND \"GenerationStartedAtUtc\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_storage_shape",
                "(\"StorageKey\" IS NULL AND \"EncryptedByteLength\" IS NULL AND " +
                "\"PlaintextSha256\" IS NULL AND \"EncryptionKeyVersion\" IS NULL AND " +
                "\"FormatVersion\" IS NULL AND \"AvailableAtUtc\" IS NULL) OR " +
                "(\"GenerationActor\" IS NOT NULL AND \"StorageKey\" IS NOT NULL AND " +
                "\"EncryptedByteLength\" > 0 AND \"PlaintextSha256\" IS NOT NULL AND " +
                "\"EncryptionKeyVersion\" > 0 AND \"FormatVersion\" > 0 AND " +
                "\"AvailableAtUtc\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_lifecycle_shape",
                $"(\"State\" = {(int)DataRightsExportArtifactState.Requested} AND " +
                "\"GenerationActor\" IS NULL AND \"StorageKey\" IS NULL AND " +
                "\"FailureCode\" IS NULL AND \"DeletionRunId\" IS NULL AND " +
                "\"DeletionStartedAtUtc\" IS NULL AND \"DeletedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)DataRightsExportArtifactState.Generating} AND " +
                "\"GenerationActor\" IS NOT NULL AND \"GenerationRunId\" IS NOT NULL AND " +
                "\"GenerationAttempt\" > 0 AND \"GenerationStartedAtUtc\" IS NOT NULL AND " +
                "\"StorageKey\" IS NULL AND \"EncryptedByteLength\" IS NULL AND " +
                "\"PlaintextSha256\" IS NULL AND \"EncryptionKeyVersion\" IS NULL AND " +
                "\"FormatVersion\" IS NULL AND \"AvailableAtUtc\" IS NULL AND " +
                "\"FailureCode\" IS NULL AND \"DeletionRunId\" IS NULL AND " +
                "\"DeletionStartedAtUtc\" IS NULL AND \"DeletedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)DataRightsExportArtifactState.Failed} AND " +
                "\"GenerationActor\" IS NOT NULL AND \"GenerationRunId\" IS NOT NULL AND " +
                "\"GenerationAttempt\" > 0 AND \"GenerationStartedAtUtc\" IS NOT NULL AND " +
                "\"StorageKey\" IS NULL AND \"FailureCode\" IS NOT NULL AND " +
                "\"DeletionRunId\" IS NULL AND \"DeletionStartedAtUtc\" IS NULL AND " +
                "\"DeletedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)DataRightsExportArtifactState.Available} AND " +
                "\"GenerationActor\" IS NOT NULL AND \"StorageKey\" IS NOT NULL AND " +
                "\"EncryptedByteLength\" > 0 AND \"PlaintextSha256\" IS NOT NULL AND " +
                "\"EncryptionKeyVersion\" > 0 AND \"FormatVersion\" > 0 AND " +
                "\"AvailableAtUtc\" IS NOT NULL AND \"FailureCode\" IS NULL AND " +
                "\"DeletionRunId\" IS NULL AND \"DeletionStartedAtUtc\" IS NULL AND " +
                "\"DeletedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)DataRightsExportArtifactState.Expired} AND " +
                "\"FailureCode\" IS NULL AND \"DeletionRunId\" IS NULL AND " +
                "\"DeletionStartedAtUtc\" IS NULL AND \"DeletedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)DataRightsExportArtifactState.Deleting} AND " +
                "\"FailureCode\" IS NULL AND \"DeletionRunId\" IS NOT NULL AND " +
                "\"DeletionStartedAtUtc\" IS NOT NULL AND \"DeletedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)DataRightsExportArtifactState.Deleted} AND " +
                "\"FailureCode\" IS NULL AND \"DeletionRunId\" IS NOT NULL AND " +
                "\"DeletionStartedAtUtc\" IS NOT NULL AND \"DeletedAtUtc\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_export_artifacts_failure_shape",
                $"(\"State\" = {(int)DataRightsExportArtifactState.Failed} AND " +
                "\"FailureCode\" IS NOT NULL) OR " +
                $"(\"State\" <> {(int)DataRightsExportArtifactState.Failed} AND " +
                "\"FailureCode\" IS NULL)");
        });

        builder.HasKey(artifact => artifact.Id);
        builder.HasAlternateKey(artifact => new { artifact.ScopeId, artifact.Id });
        builder.Property(artifact => artifact.Id).ValueGeneratedNever();
        builder.Property(artifact => artifact.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(artifact => artifact.SelectionSha256)
            .HasMaxLength(DataRightsExportArtifact.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(artifact => artifact.RequestedBy)
            .HasMaxLength(DataRightsExportArtifact.ActorIdMaxLength)
            .IsRequired();
        builder.Property(artifact => artifact.GenerationActor)
            .HasMaxLength(DataRightsExportArtifact.ActorIdMaxLength);
        builder.Property(artifact => artifact.FailureCode)
            .HasMaxLength(DataRightsExportArtifact.FailureCodeMaxLength);
        builder.Property(artifact => artifact.StorageKey)
            .HasMaxLength(DataRightsExportArtifact.StorageKeyMaxLength);
        builder.Property(artifact => artifact.PlaintextSha256)
            .HasMaxLength(DataRightsExportArtifact.Sha256Length)
            .IsFixedLength();
        builder.Property(artifact => artifact.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(artifact => new
        {
            artifact.ScopeId,
            artifact.IdempotencyKey
        }).IsUnique();
        builder.HasIndex(artifact => new
        {
            artifact.ScopeId,
            artifact.CaseId
        }).IsUnique();
        builder.HasIndex(artifact => new
        {
            artifact.ScopeId,
            artifact.State,
            artifact.ExpiresAtUtc
        });
        builder.HasOne<DataRightsCase>()
            .WithMany()
            .HasForeignKey(artifact => new { artifact.ScopeId, artifact.CaseId })
            .HasPrincipalKey(dataRightsCase => new
            {
                dataRightsCase.ScopeId,
                dataRightsCase.Id
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(artifact => artifact.DomainEvents);
    }
}

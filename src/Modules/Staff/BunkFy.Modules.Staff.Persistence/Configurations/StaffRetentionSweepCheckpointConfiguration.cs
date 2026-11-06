namespace BunkFy.Modules.Staff.Persistence.Configurations;

using BunkFy.Modules.Staff.Domain.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class StaffRetentionSweepCheckpointConfiguration
    : IEntityTypeConfiguration<StaffRetentionSweepCheckpoint>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(
        EntityTypeBuilder<StaffRetentionSweepCheckpoint> builder)
    {
        builder.ToTable(
            "staff_retention_sweep_checkpoints",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_staff_retention_checkpoints_coordinates",
                    $"\"Id\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> ''");
                table.HasCheckConstraint(
                    "CK_staff_retention_checkpoints_cursor",
                    "\"AfterProjectionOrdinal\" >= 0");
                table.HasCheckConstraint(
                    "CK_staff_retention_checkpoints_key",
                    "\"DataClassKey\" ~ '^[a-z0-9.-]+$'");
                table.HasCheckConstraint(
                    "CK_staff_retention_checkpoints_policy",
                    "\"ExecutionPolicyVersion\" >= 1");
                table.HasCheckConstraint(
                    "CK_staff_retention_checkpoints_version",
                    "\"Version\" >= 1");
                table.HasCheckConstraint(
                    "CK_staff_retention_checkpoints_lifecycle",
                    "(\"LastExecutionId\" IS NULL AND " +
                    "((\"Version\" = 1 AND \"AfterProjectionOrdinal\" = 0) OR " +
                    "\"Version\" >= 3)) OR " +
                    "(\"LastExecutionId\" IS NOT NULL AND " +
                    $"\"LastExecutionId\" <> '{EmptyGuid}' AND \"Version\" >= 2)");
                table.HasCheckConstraint(
                    "CK_staff_retention_checkpoints_timestamp",
                    "\"UpdatedAtUtc\" > " +
                    "TIMESTAMPTZ '0001-01-01 00:00:00+00'");
            });
        builder.HasKey(checkpoint => checkpoint.Id);
        builder.Property(checkpoint => checkpoint.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(checkpoint => checkpoint.DataClassKey)
            .HasMaxLength(
                StaffRetentionExecution.DataClassKeyMaxLength)
            .IsRequired();
        builder.Property(checkpoint => checkpoint.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(checkpoint => new
        {
            checkpoint.ScopeId,
            checkpoint.DataClassKey,
            checkpoint.ExecutionPolicyVersion
        })
            .HasDatabaseName(
                "UX_staff_retention_checkpoints_data_class_policy")
            .IsUnique();
        builder.HasIndex(checkpoint => new
        {
            checkpoint.ScopeId,
            checkpoint.LastExecutionId
        })
            .HasDatabaseName(
                "UX_staff_retention_checkpoints_last_execution")
            .HasFilter("\"LastExecutionId\" IS NOT NULL")
            .IsUnique();
        builder.Ignore(checkpoint => checkpoint.DomainEvents);
    }
}

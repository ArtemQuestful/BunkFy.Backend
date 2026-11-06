namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestRetentionSweepCheckpointConfiguration
    : IEntityTypeConfiguration<GuestRetentionSweepCheckpoint>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(
        EntityTypeBuilder<GuestRetentionSweepCheckpoint> builder)
    {
        builder.ToTable("guest_retention_sweep_checkpoints", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_retention_sweep_checkpoints_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_guest_retention_sweep_checkpoints_cursor",
                "\"AfterProjectionOrdinal\" >= 0");
            table.HasCheckConstraint(
                "CK_guest_retention_sweep_checkpoints_key",
                "\"DataClassKey\" ~ '^[a-z0-9.-]+$'");
            table.HasCheckConstraint(
                "CK_guest_retention_sweep_checkpoints_version",
                "\"Version\" >= 1");
            table.HasCheckConstraint(
                "CK_guest_retention_sweep_checkpoints_lifecycle",
                "(\"LastExecutionId\" IS NULL AND " +
                "((\"Version\" = 1 AND \"AfterProjectionOrdinal\" = 0) OR " +
                "\"Version\" >= 3)) OR " +
                "(\"LastExecutionId\" IS NOT NULL AND " +
                $"\"LastExecutionId\" <> '{EmptyGuid}' AND \"Version\" >= 2)");
            table.HasCheckConstraint(
                "CK_guest_retention_sweep_checkpoints_timestamp",
                "\"UpdatedAtUtc\" > " +
                "TIMESTAMPTZ '0001-01-01 00:00:00+00'");
        });
        builder.HasKey(checkpoint => checkpoint.Id);
        builder.Property(checkpoint => checkpoint.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(checkpoint => checkpoint.DataClassKey)
            .HasMaxLength(GuestRetentionExecution.DataClassKeyMaxLength)
            .IsRequired();
        builder.Property(checkpoint => checkpoint.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(checkpoint => new
        {
            checkpoint.ScopeId,
            checkpoint.DataClassKey
        })
            .HasDatabaseName(
                "UX_guest_retention_checkpoints_data_class")
            .IsUnique();
        builder.HasIndex(checkpoint => new
        {
            checkpoint.ScopeId,
            checkpoint.LastExecutionId
        })
            .HasDatabaseName(
                "UX_guest_retention_checkpoints_last_execution")
            .HasFilter("\"LastExecutionId\" IS NOT NULL")
            .IsUnique();
        builder.Ignore(checkpoint => checkpoint.DomainEvents);
    }
}

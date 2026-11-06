namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class
    WorkspaceStaffIdentityAnchorSweepCheckpointConfiguration
    : IEntityTypeConfiguration<
        WorkspaceStaffIdentityAnchorSweepCheckpoint>
{
    public void Configure(
        EntityTypeBuilder<WorkspaceStaffIdentityAnchorSweepCheckpoint>
            builder)
    {
        builder.ToTable(
            "workspace_staff_identity_anchor_sweep_checkpoints",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_protocol",
                    $"\"ProtocolVersion\" = " +
                    $"{WorkspaceStaffIdentityAnchorSweepCheckpoint.CurrentProtocolVersion}");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_identifiers",
                    "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND " +
                    "(\"CycleId\" IS NULL OR \"CycleId\" <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid) AND " +
                    "(\"LastCompletedCycleId\" IS NULL OR " +
                    "\"LastCompletedCycleId\" <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid) AND " +
                    "(\"LastAdvanceId\" IS NULL OR \"LastAdvanceId\" <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid) AND " +
                    "(\"LastRunId\" IS NULL OR \"LastRunId\" <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid)");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_version",
                    "\"Version\" >= 1");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_cycle",
                    "(\"CycleId\" IS NULL AND " +
                    "\"CycleUpperOrdinal\" IS NULL AND " +
                    "\"AfterOrdinal\" IS NULL AND " +
                    "\"CycleStartedAtUtc\" IS NULL AND " +
                    "\"CycleScannedCount\" = 0) OR " +
                    "(\"CycleId\" IS NOT NULL AND " +
                    "\"CycleUpperOrdinal\" > 0 AND " +
                    "(\"AfterOrdinal\" IS NULL OR " +
                    "(\"AfterOrdinal\" > 0 AND " +
                    "\"AfterOrdinal\" <= \"CycleUpperOrdinal\")) AND " +
                    "\"CycleStartedAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_cycle_counts_nonnegative",
                    "\"CycleScannedCount\" >= 0 AND " +
                    "\"CycleNoAnchorCount\" >= 0 AND " +
                    "\"CycleRemovedCount\" >= 0 AND " +
                    "\"CycleObservedCount\" >= 0 AND " +
                    "\"CycleAlreadyObservedCount\" >= 0 AND " +
                    "\"CycleDeferredCount\" >= 0 AND " +
                    "\"CycleConflictCount\" >= 0 AND " +
                    "\"CyclePassOneCommittedCount\" >= 0 AND " +
                    "\"CycleResolutionRecordConfirmedCount\" >= 0");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_cycle_counts_partition",
                    "\"CycleScannedCount\" = " +
                    "\"CycleNoAnchorCount\" + " +
                    "\"CycleRemovedCount\" + " +
                    "\"CycleObservedCount\" + " +
                    "\"CycleAlreadyObservedCount\" + " +
                    "\"CycleDeferredCount\" + " +
                    "\"CycleConflictCount\" AND " +
                    "\"CyclePassOneCommittedCount\" <= " +
                    "\"CycleScannedCount\" AND " +
                    "\"CycleResolutionRecordConfirmedCount\" <= " +
                    "\"CycleScannedCount\"");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_last_counts_nonnegative",
                    "\"LastCompletedScannedCount\" >= 0 AND " +
                    "\"LastCompletedNoAnchorCount\" >= 0 AND " +
                    "\"LastCompletedRemovedCount\" >= 0 AND " +
                    "\"LastCompletedObservedCount\" >= 0 AND " +
                    "\"LastCompletedAlreadyObservedCount\" >= 0 AND " +
                    "\"LastCompletedDeferredCount\" >= 0 AND " +
                    "\"LastCompletedConflictCount\" >= 0 AND " +
                    "\"LastCompletedPassOneCommittedCount\" >= 0 AND " +
                    "\"LastCompletedResolutionRecordConfirmedCount\" >= 0");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_last_counts_partition",
                    "\"LastCompletedScannedCount\" = " +
                    "\"LastCompletedNoAnchorCount\" + " +
                    "\"LastCompletedRemovedCount\" + " +
                    "\"LastCompletedObservedCount\" + " +
                    "\"LastCompletedAlreadyObservedCount\" + " +
                    "\"LastCompletedDeferredCount\" + " +
                    "\"LastCompletedConflictCount\" AND " +
                    "\"LastCompletedPassOneCommittedCount\" <= " +
                    "\"LastCompletedScannedCount\" AND " +
                    "\"LastCompletedResolutionRecordConfirmedCount\" <= " +
                    "\"LastCompletedScannedCount\"");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_last_cycle",
                    "(\"LastCompletedCycleId\" IS NULL AND " +
                    "\"LastCompletedUpperOrdinal\" IS NULL AND " +
                    "\"LastCompletedAtUtc\" IS NULL AND " +
                    "\"LastCompletedScannedCount\" = 0) OR " +
                    "(\"LastCompletedCycleId\" IS NOT NULL AND " +
                    "(\"LastCompletedUpperOrdinal\" > 0 OR " +
                    "(\"LastCompletedUpperOrdinal\" IS NULL AND " +
                    "\"LastCompletedScannedCount\" = 0)) AND " +
                    "\"LastCompletedAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_last_advance",
                    "(\"LastAdvanceId\" IS NULL AND " +
                    "\"LastAdvanceSha256\" IS NULL) OR " +
                    "(\"LastAdvanceId\" IS NOT NULL AND " +
                    "\"LastAdvanceSha256\" ~ '^[0-9a-f]{64}$')");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_run",
                    "(\"CycleId\" IS NULL AND " +
                    "\"LastCompletedCycleId\" IS NULL AND " +
                    "\"LastAdvanceId\" IS NULL AND \"LastRunId\" IS NULL) OR " +
                    "(\"LastRunId\" IS NOT NULL AND (" +
                    "\"CycleId\" IS NOT NULL OR " +
                    "\"LastCompletedCycleId\" IS NOT NULL))");
                table.HasCheckConstraint(
                    "CK_ws_anchor_sweep_times",
                    "(\"CycleStartedAtUtc\" IS NULL OR " +
                    "\"CycleStartedAtUtc\" <= \"UpdatedAtUtc\") AND " +
                    "(\"LastCompletedAtUtc\" IS NULL OR " +
                    "\"LastCompletedAtUtc\" <= \"UpdatedAtUtc\")");
            });
        builder.HasKey(checkpoint => checkpoint.Id);
        builder.HasAlternateKey(checkpoint => new
        {
            checkpoint.ScopeId,
            checkpoint.Id
        });
        builder.Property(checkpoint => checkpoint.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(checkpoint => checkpoint.ProtocolVersion)
            .IsRequired();
        builder.Property(checkpoint => checkpoint.LastAdvanceSha256)
            .HasMaxLength(
                WorkspaceStaffIdentityAnchorSweepCheckpoint
                    .AdvanceSha256Length);
        builder.Property(checkpoint => checkpoint.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(checkpoint => new
        {
            checkpoint.ScopeId,
            checkpoint.ProtocolVersion
        }).IsUnique();
        builder.HasIndex(checkpoint => new
        {
            checkpoint.ScopeId,
            checkpoint.LastCompletedAtUtc
        });
        builder.Ignore(checkpoint => checkpoint.DomainEvents);
        builder.Ignore(checkpoint => checkpoint.HasActiveCycle);
    }
}

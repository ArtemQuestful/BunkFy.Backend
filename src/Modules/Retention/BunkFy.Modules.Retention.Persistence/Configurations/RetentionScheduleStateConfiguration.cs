namespace BunkFy.Modules.Retention.Persistence.Configurations;

using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class RetentionScheduleStateConfiguration
    : IEntityTypeConfiguration<RetentionScheduleState>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<RetentionScheduleState> builder)
    {
        builder.ToTable("schedule_state", table =>
        {
            table.HasCheckConstraint(
                "CK_retention_schedule_state_coordinates",
                $"trim(\"ScopeId\") <> '' AND \"LastExecutionId\" <> '{EmptyGuid}' AND " +
                "\"OwnerKey\" ~ '^[a-z0-9.-]+$' AND " +
                "\"DataClassKey\" ~ '^[a-z0-9.-]+$' AND " +
                "(\"OutcomeCode\" IS NULL OR " +
                "\"OutcomeCode\" ~ '^[A-Za-z0-9.-]+$')");
            table.HasCheckConstraint(
                "CK_retention_schedule_state_versions",
                "\"ExecutionPolicyVersion\" >= 1 AND \"Version\" >= 1 AND " +
                "\"ConsecutiveFailures\" >= 0 AND " +
                $"(\"State\" = {(int)RetentionExecutionState.Running} OR " +
                "\"Version\" >= 2)");
            table.HasCheckConstraint(
                "CK_retention_schedule_state_time",
                "\"NextDueAtUtc\" > \"LastStartedAtUtc\" AND " +
                "(\"LastCompletedAtUtc\" IS NULL OR " +
                "\"LastCompletedAtUtc\" >= \"LastStartedAtUtc\")");
            table.HasCheckConstraint(
                "CK_retention_schedule_state_target",
                "(\"TargetKey\" = 'tenant' AND \"PropertyId\" IS NULL) OR " +
                $"(\"PropertyId\" IS NOT NULL AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                "\"TargetKey\" = replace(\"PropertyId\"::text, '-', ''))");
            table.HasCheckConstraint(
                "CK_retention_schedule_state_result",
                $"(\"State\" = {(int)RetentionExecutionState.Running} AND " +
                "\"LastCompletedAtUtc\" IS NULL AND \"LastScannedCount\" IS NULL AND " +
                "\"LastAffectedCount\" IS NULL AND \"LastRemainingCount\" IS NULL AND " +
                "\"OutcomeCode\" IS NULL AND \"HoldReviewDueAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)RetentionExecutionState.Completed} AND " +
                "\"LastCompletedAtUtc\" IS NOT NULL AND " +
                "\"LastScannedCount\" >= 0 AND \"LastAffectedCount\" BETWEEN 0 AND " +
                "\"LastScannedCount\" AND \"LastRemainingCount\" >= 0 AND " +
                "\"OutcomeCode\" IS NOT NULL AND \"HoldReviewDueAtUtc\" IS NULL AND " +
                "\"ConsecutiveFailures\" = 0) OR " +
                $"(\"State\" = {(int)RetentionExecutionState.Blocked} AND " +
                "\"LastCompletedAtUtc\" IS NOT NULL AND " +
                "\"LastScannedCount\" >= 0 AND \"LastAffectedCount\" BETWEEN 0 AND " +
                "\"LastScannedCount\" AND \"LastRemainingCount\" >= 0 AND " +
                "\"OutcomeCode\" IS NOT NULL AND \"HoldReviewDueAtUtc\" IS NOT NULL AND " +
                "\"ConsecutiveFailures\" = 0) OR " +
                $"(\"State\" = {(int)RetentionExecutionState.Failed} AND " +
                "\"LastCompletedAtUtc\" IS NOT NULL AND " +
                "\"LastScannedCount\" >= 0 AND \"LastAffectedCount\" BETWEEN 0 AND " +
                "\"LastScannedCount\" AND \"LastRemainingCount\" >= 0 AND " +
                "\"OutcomeCode\" IS NOT NULL AND \"HoldReviewDueAtUtc\" IS NULL AND " +
                "\"ConsecutiveFailures\" >= 1)");
        });
        builder.HasKey(state => new
        {
            state.ScopeId,
            state.OwnerKey,
            state.DataClassKey,
            state.TargetKey,
            state.ExecutionPolicyVersion
        });
        builder.Property(state => state.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(state => state.OwnerKey)
            .HasMaxLength(RetentionExecution.KeyMaxLength)
            .IsRequired();
        builder.Property(state => state.DataClassKey)
            .HasMaxLength(RetentionExecution.KeyMaxLength)
            .IsRequired();
        builder.Property(state => state.TargetKey)
            .HasMaxLength(RetentionScheduleState.TargetKeyMaxLength)
            .IsRequired();
        builder.Property(state => state.State).HasConversion<int>().IsRequired();
        builder.Property(state => state.OutcomeCode)
            .HasMaxLength(RetentionExecution.OutcomeCodeMaxLength);
        builder.Property(state => state.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(state => new
        {
            state.ScopeId,
            state.State,
            state.NextDueAtUtc
        });
        builder.HasIndex(state => new
        {
            state.ScopeId,
            state.HoldReviewDueAtUtc
        });
        builder.HasIndex(state => new
        {
            state.ScopeId,
            state.LastExecutionId
        }).IsUnique();
    }
}

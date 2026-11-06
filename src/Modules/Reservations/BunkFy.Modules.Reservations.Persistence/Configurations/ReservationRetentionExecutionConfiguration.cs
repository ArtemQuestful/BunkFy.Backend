namespace BunkFy.Modules.Reservations.Persistence.Configurations;

using BunkFy.Modules.Reservations.Domain.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class ReservationRetentionExecutionConfiguration
    : IEntityTypeConfiguration<ReservationRetentionExecution>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(
        EntityTypeBuilder<ReservationRetentionExecution> builder)
    {
        builder.ToTable("reservation_retention_executions", table =>
        {
            table.HasCheckConstraint(
                "CK_reservation_retention_executions_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_reservation_retention_executions_policy",
                "\"ExecutionPolicyVersion\" >= 1 AND \"Attempt\" >= 1 AND " +
                "\"DeadlineUtc\" > \"StartedAtUtc\"");
            table.HasCheckConstraint(
                "CK_reservation_retention_executions_cursor",
                "\"StartingProjectionOrdinal\" >= 0");
            table.HasCheckConstraint(
                "CK_reservation_retention_executions_key",
                "\"DataClassKey\" ~ '^[a-z0-9.-]+$'");
            table.HasCheckConstraint(
                "CK_reservation_retention_executions_version",
                $"\"Version\" >= 1 AND " +
                $"(\"State\" = {(int)ReservationRetentionExecutionState.Running} OR " +
                "\"Version\" >= 2)");
            table.HasCheckConstraint(
                "CK_reservation_retention_executions_timestamp",
                "\"StartedAtUtc\" > " +
                "TIMESTAMPTZ '0001-01-01 00:00:00+00'");
            table.HasCheckConstraint(
                "CK_reservation_retention_executions_counts",
                "\"AffectedCount\" >= 0 AND " +
                "(\"ScannedCount\" IS NULL OR \"ScannedCount\" >= 0) AND " +
                "(\"RemainingCount\" IS NULL OR \"RemainingCount\" >= 0) AND " +
                "(\"ScannedCount\" IS NULL OR " +
                "\"AffectedCount\" <= \"ScannedCount\")");
            table.HasCheckConstraint(
                "CK_reservation_retention_executions_state",
                $"(\"State\" = {(int)ReservationRetentionExecutionState.Running} AND " +
                "\"CompletedAtUtc\" IS NULL AND \"ScannedCount\" IS NULL AND " +
                "\"RemainingCount\" IS NULL AND \"OutcomeCode\" IS NULL AND " +
                "\"HoldReviewDueAtUtc\" IS NULL) OR " +
                $"(\"State\" IN ({(int)ReservationRetentionExecutionState.Completed}, " +
                $"{(int)ReservationRetentionExecutionState.Blocked}, " +
                $"{(int)ReservationRetentionExecutionState.Failed}) AND " +
                "\"CompletedAtUtc\" BETWEEN \"StartedAtUtc\" AND \"DeadlineUtc\" AND " +
                "\"ScannedCount\" IS NOT NULL AND \"RemainingCount\" IS NOT NULL AND " +
                "\"OutcomeCode\" ~ '^[A-Za-z0-9.-]+$' AND " +
                $"((\"State\" = {(int)ReservationRetentionExecutionState.Blocked} AND " +
                "\"HoldReviewDueAtUtc\" IS NOT NULL) OR " +
                $"(\"State\" <> {(int)ReservationRetentionExecutionState.Blocked} AND " +
                "\"HoldReviewDueAtUtc\" IS NULL)))");
        });
        builder.HasKey(execution => execution.Id);
        builder.HasAlternateKey(execution => new
        {
            execution.ScopeId,
            execution.Id
        });
        builder.Property(execution => execution.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(execution => execution.DataClassKey)
            .HasMaxLength(
                ReservationRetentionExecution.DataClassKeyMaxLength)
            .IsRequired();
        builder.Property(execution => execution.State)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(execution => execution.OutcomeCode)
            .HasMaxLength(
                ReservationRetentionExecution.OutcomeCodeMaxLength);
        builder.Property(execution => execution.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(execution => new
        {
            execution.ScopeId,
            execution.DataClassKey,
            execution.CompletedAtUtc,
            execution.Id
        }).HasDatabaseName(
            "IX_reservation_retention_executions_history");
        builder.Ignore(execution => execution.DomainEvents);
    }
}

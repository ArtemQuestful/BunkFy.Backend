namespace BunkFy.Modules.Retention.Persistence.Configurations;

using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class RetentionExecutionConfiguration
    : IEntityTypeConfiguration<RetentionExecution>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<RetentionExecution> builder)
    {
        builder.ToTable("executions", table =>
        {
            table.HasCheckConstraint(
                "CK_retention_executions_coordinate",
                $"\"Id\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> '' AND " +
                "((\"TargetKind\" = 1 AND \"PropertyId\" IS NULL) OR " +
                $"(\"TargetKind\" = 2 AND \"PropertyId\" IS NOT NULL AND " +
                $"\"PropertyId\" <> '{EmptyGuid}'))");
            table.HasCheckConstraint(
                "CK_retention_executions_keys",
                "\"OwnerKey\" ~ '^[a-z0-9.-]+$' AND " +
                "\"DataClassKey\" ~ '^[a-z0-9.-]+$' AND " +
                "(\"OutcomeCode\" IS NULL OR " +
                "\"OutcomeCode\" ~ '^[A-Za-z0-9.-]+$')");
            table.HasCheckConstraint(
                "CK_retention_executions_versions",
                "\"ExecutionPolicyVersion\" >= 1 AND \"Attempt\" >= 1 AND " +
                "\"Version\" >= 1 AND " +
                $"(\"State\" = {(int)RetentionExecutionState.Running} OR " +
                "\"Version\" >= 2)");
            table.HasCheckConstraint(
                "CK_retention_executions_time",
                "\"DeadlineUtc\" > \"StartedAtUtc\" AND " +
                "(\"CompletedAtUtc\" IS NULL OR " +
                "\"CompletedAtUtc\" >= \"StartedAtUtc\") AND " +
                $"(\"State\" = {(int)RetentionExecutionState.Failed} OR " +
                "\"CompletedAtUtc\" IS NULL OR " +
                "\"CompletedAtUtc\" <= \"DeadlineUtc\")");
            table.HasCheckConstraint(
                "CK_retention_executions_state",
                $"(\"State\" = {(int)RetentionExecutionState.Running} AND " +
                "\"CompletedAtUtc\" IS NULL AND \"ScannedCount\" IS NULL AND " +
                "\"AffectedCount\" IS NULL AND \"RemainingCount\" IS NULL AND " +
                "\"OutcomeCode\" IS NULL AND \"HoldReviewDueAtUtc\" IS NULL) OR " +
                $"(\"State\" IN ({(int)RetentionExecutionState.Completed}, " +
                $"{(int)RetentionExecutionState.Blocked}, " +
                $"{(int)RetentionExecutionState.Failed}) AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"ScannedCount\" >= 0 AND " +
                "\"AffectedCount\" BETWEEN 0 AND \"ScannedCount\" AND " +
                "\"RemainingCount\" >= 0 AND length(trim(\"OutcomeCode\")) > 0 AND " +
                $"((\"State\" = {(int)RetentionExecutionState.Blocked} AND " +
                "\"HoldReviewDueAtUtc\" IS NOT NULL) OR " +
                $"(\"State\" <> {(int)RetentionExecutionState.Blocked} AND " +
                "\"HoldReviewDueAtUtc\" IS NULL)))");
        });
        builder.HasKey(execution => execution.Id);
        builder.HasAlternateKey(execution => new
        {
            execution.ScopeId,
            execution.Id
        });
        builder.Property(execution => execution.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(execution => execution.OwnerKey)
            .HasMaxLength(RetentionExecution.KeyMaxLength)
            .IsRequired();
        builder.Property(execution => execution.DataClassKey)
            .HasMaxLength(RetentionExecution.KeyMaxLength)
            .IsRequired();
        builder.Property(execution => execution.TargetKind)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(execution => execution.State)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(execution => execution.OutcomeCode)
            .HasMaxLength(RetentionExecution.OutcomeCodeMaxLength);
        builder.Property(execution => execution.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(execution => new
        {
            execution.ScopeId,
            execution.OwnerKey,
            execution.DataClassKey,
            execution.PropertyId,
            execution.ExecutionPolicyVersion,
            execution.StartedAtUtc
        });
        builder.HasIndex(execution => new
        {
            execution.ScopeId,
            execution.State,
            execution.CompletedAtUtc
        });
        builder.Ignore(execution => execution.DomainEvents);
    }
}

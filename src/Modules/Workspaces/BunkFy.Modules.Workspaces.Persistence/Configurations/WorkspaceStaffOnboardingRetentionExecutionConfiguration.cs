namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class WorkspaceStaffOnboardingRetentionExecutionConfiguration
    : IEntityTypeConfiguration<WorkspaceStaffOnboardingRetentionExecution>
{
    public void Configure(
        EntityTypeBuilder<WorkspaceStaffOnboardingRetentionExecution> builder)
    {
        builder.ToTable(
            "staff_onboarding_retention_executions",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_staff_onboarding_retention_execution_coordinate",
                    "char_length(trim(\"ScopeId\")) > 0 AND " +
                    "char_length(\"DataClassKey\") BETWEEN 1 AND 64 AND " +
                    "lower(\"DataClassKey\") = \"DataClassKey\" AND " +
                    "\"ExecutionPolicyVersion\" > 0 AND \"Attempt\" > 0");
                table.HasCheckConstraint(
                    "CK_staff_onboarding_retention_execution_state",
                    "\"State\" BETWEEN 1 AND 3");
                table.HasCheckConstraint(
                    "CK_staff_onboarding_retention_execution_timing",
                    "\"StartedAtUtc\" <> '-infinity' AND " +
                    "\"DeadlineUtc\" > \"StartedAtUtc\" AND " +
                    "(\"CompletedAtUtc\" IS NULL OR " +
                    "(\"CompletedAtUtc\" >= \"StartedAtUtc\" AND " +
                    "\"CompletedAtUtc\" <= \"DeadlineUtc\"))");
                table.HasCheckConstraint(
                    "CK_staff_onboarding_retention_execution_counts",
                    "\"AffectedCount\" >= 0 AND " +
                    "\"ScannedCount\" >= \"AffectedCount\" AND " +
                    "(\"RemainingCount\" IS NULL OR \"RemainingCount\" >= 0)");
                table.HasCheckConstraint(
                    "CK_staff_onboarding_retention_execution_lifecycle",
                    $"((\"State\" = " +
                    $"{(int)WorkspaceStaffOnboardingRetentionExecutionState.Running} " +
                    "AND \"CompletedAtUtc\" IS NULL AND " +
                    "\"RemainingCount\" IS NULL AND \"OutcomeCode\" IS NULL) OR " +
                    $"(\"State\" IN ({(int)WorkspaceStaffOnboardingRetentionExecutionState.Completed}, " +
                    $"{(int)WorkspaceStaffOnboardingRetentionExecutionState.Failed}) " +
                    "AND \"CompletedAtUtc\" IS NOT NULL AND " +
                    "\"RemainingCount\" IS NOT NULL AND \"OutcomeCode\" IS NOT NULL))");
                table.HasCheckConstraint(
                    "CK_staff_onboarding_retention_execution_failed_remaining",
                    $"\"State\" <> " +
                    $"{(int)WorkspaceStaffOnboardingRetentionExecutionState.Failed} " +
                    "OR \"RemainingCount\" > 0");
                table.HasCheckConstraint(
                    "CK_staff_onboarding_retention_execution_version",
                    "\"Version\" >= 1 AND " +
                    "(\"State\" = 1 OR \"Version\" >= 2)");
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
                WorkspaceStaffOnboardingRetentionExecution.DataClassKeyMaxLength)
            .IsRequired();
        builder.Property(execution => execution.State)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(execution => execution.OutcomeCode)
            .HasMaxLength(
                WorkspaceStaffOnboardingRetentionExecution.OutcomeCodeMaxLength);
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
            "IX_staff_onboarding_retention_executions_history");
        builder.Ignore(execution => execution.DomainEvents);
    }
}

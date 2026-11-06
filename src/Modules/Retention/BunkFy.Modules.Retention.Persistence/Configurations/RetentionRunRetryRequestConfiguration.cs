namespace BunkFy.Modules.Retention.Persistence.Configurations;

using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class RetentionRunRetryRequestConfiguration
    : IEntityTypeConfiguration<RetentionRunRetryRequest>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(
        EntityTypeBuilder<RetentionRunRetryRequest> builder)
    {
        builder.ToTable("run_retry_requests", table =>
        {
            table.HasCheckConstraint(
                "CK_retention_run_retry_request_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"RunId\" <> '{EmptyGuid}' AND " +
                "trim(\"ScopeId\") <> '' AND " +
                "\"OwnerKey\" ~ '^[a-z0-9.-]+$' AND " +
                "\"DataClassKey\" ~ '^[a-z0-9.-]+$' AND " +
                "(\"FailureCode\" IS NULL OR " +
                "\"FailureCode\" ~ '^[a-z0-9.-]+$')");
            table.HasCheckConstraint(
                "CK_retention_run_retry_request_versions",
                "\"ExecutionPolicyVersion\" >= 1 AND " +
                "\"EvidenceVersion\" >= 1 AND \"Attempt\" >= 1 AND " +
                "\"Version\" >= 1 AND " +
                $"(\"State\" = {(int)RetentionRunRetryRequestState.Pending} OR " +
                "\"Version\" >= 2)");
            table.HasCheckConstraint(
                "CK_retention_run_retry_request_target",
                $"(\"TargetKind\" = {(int)RetentionExecutionTargetKind.Tenant} " +
                "AND \"PropertyId\" IS NULL) OR " +
                $"(\"TargetKind\" = {(int)RetentionExecutionTargetKind.Property} " +
                $"AND \"PropertyId\" IS NOT NULL AND " +
                $"\"PropertyId\" <> '{EmptyGuid}')");
            table.HasCheckConstraint(
                "CK_retention_run_retry_request_state",
                $"\"State\" IN ({(int)RetentionRunRetryRequestState.Pending}, " +
                $"{(int)RetentionRunRetryRequestState.Applied}, " +
                $"{(int)RetentionRunRetryRequestState.Failed}) AND " +
                $"((\"State\" = {(int)RetentionRunRetryRequestState.Pending} " +
                "AND \"CompletedAtUtc\" IS NULL AND \"FailureCode\" IS NULL) OR " +
                $"(\"State\" = {(int)RetentionRunRetryRequestState.Applied} " +
                "AND \"CompletedAtUtc\" IS NOT NULL AND \"FailureCode\" IS NULL) OR " +
                $"(\"State\" = {(int)RetentionRunRetryRequestState.Failed} " +
                "AND \"CompletedAtUtc\" IS NOT NULL AND \"FailureCode\" IS NOT NULL))");
            table.HasCheckConstraint(
                "CK_retention_run_retry_request_timestamps",
                "(\"ScheduledAtUtc\" IS NULL OR " +
                "\"ScheduledAtUtc\" >= \"RequestedAtUtc\") AND " +
                "(\"CompletedAtUtc\" IS NULL OR " +
                "\"CompletedAtUtc\" >= \"RequestedAtUtc\")");
        });
        builder.HasKey(request => request.Id);
        builder.Property(request => request.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(request => request.OwnerKey)
            .HasMaxLength(RetentionExecution.KeyMaxLength)
            .IsRequired();
        builder.Property(request => request.DataClassKey)
            .HasMaxLength(RetentionExecution.KeyMaxLength)
            .IsRequired();
        builder.Property(request => request.TargetKind)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(request => request.State)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(request => request.FailureCode)
            .HasMaxLength(RetentionRunRetryRequest.FailureCodeMaxLength);
        builder.Property(request => request.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(request => new
        {
            request.ScopeId,
            request.RunId,
            request.EvidenceVersion
        }).IsUnique();
        builder.HasIndex(request => new
        {
            request.ScopeId,
            request.State,
            request.RequestedAtUtc
        });
    }
}

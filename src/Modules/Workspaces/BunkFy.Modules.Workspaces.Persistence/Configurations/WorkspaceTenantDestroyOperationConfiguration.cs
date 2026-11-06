namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence.TenantTermination;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class WorkspaceTenantDestroyOperationConfiguration
    : IEntityTypeConfiguration<WorkspaceTenantDestroyOperation>
{
    public void Configure(
        EntityTypeBuilder<WorkspaceTenantDestroyOperation> builder)
    {
        builder.ToTable("tenant_destroy_operations", table =>
        {
            table.HasCheckConstraint(
                "CK_workspaces_tenant_destroy_operation_revisions",
                "\"SelectedFenceVersion\" >= 1 AND " +
                "\"ResultingFenceVersion\" = " +
                "\"SelectedFenceVersion\" + 2");
            table.HasCheckConstraint(
                "CK_workspaces_tenant_destroy_operation_batch",
                $"\"BatchSize\" BETWEEN 1 AND " +
                $"{WorkspaceTenantDestroyOperation.MaximumBatchSize}");
            table.HasCheckConstraint(
                "CK_workspaces_tenant_destroy_operation_progress",
                $"\"Stage\" BETWEEN 1 AND " +
                $"{(int)WorkspaceTenantDestroyStage.HistoricalNoProvisionReceipts} AND " +
                "\"RemovedRecordCount\" >= 0 AND " +
                "\"CompletedBatchCount\" >= 0 AND " +
                "\"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");
            table.HasCheckConstraint(
                "CK_workspaces_tenant_destroy_operation_times",
                "\"UpdatedAtUtc\" >= \"StartedAtUtc\"");
        });
        builder.HasKey(operation => operation.OperationId);
        builder.Property(operation => operation.OperationId)
            .ValueGeneratedNever();
        builder.Property(operation => operation.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(operation => operation.RequestSha256)
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();
        builder.Property(operation => operation.Stage)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(operation => operation.RemovalProofSha256)
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();
        builder.Property(operation => operation.ConcurrencyVersion)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(operation => operation.ScopeId).IsUnique();
        builder.HasOne<WorkspaceTerminationFence>()
            .WithMany()
            .HasForeignKey(operation => new
            {
                operation.ScopeId,
                operation.FenceId
            })
            .HasPrincipalKey(fence => new { fence.ScopeId, fence.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

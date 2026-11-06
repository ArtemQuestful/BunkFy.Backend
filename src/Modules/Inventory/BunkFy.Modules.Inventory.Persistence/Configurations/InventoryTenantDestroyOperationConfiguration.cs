namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using BunkFy.Modules.Inventory.Persistence.TenantTermination;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class InventoryTenantDestroyOperationConfiguration
    : IEntityTypeConfiguration<InventoryTenantDestroyOperation>
{
    public void Configure(
        EntityTypeBuilder<InventoryTenantDestroyOperation> builder)
    {
        builder.ToTable("tenant_destroy_operations", table =>
        {
            table.HasCheckConstraint(
                "CK_inventory_tenant_destroy_operation_revisions",
                "\"SelectedRevision\" >= 0 AND " +
                "\"ResultingRevision\" = \"SelectedRevision\" + 1");
            table.HasCheckConstraint(
                "CK_inventory_tenant_destroy_operation_batch",
                $"\"BatchSize\" BETWEEN 1 AND " +
                $"{InventoryTenantDestroyOperation.MaximumBatchSize}");
            table.HasCheckConstraint(
                "CK_inventory_tenant_destroy_operation_progress",
                $"\"Stage\" BETWEEN 1 AND " +
                $"{(int)InventoryTenantDestroyStage.ManualBlockGroups} AND " +
                "\"RemovedRecordCount\" >= 0 AND " +
                "\"CompletedBatchCount\" >= 0 AND " +
                "\"ProofVersion\" = 1 AND \"ConcurrencyVersion\" >= 1");
            table.HasCheckConstraint(
                "CK_inventory_tenant_destroy_operation_times",
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
    }
}

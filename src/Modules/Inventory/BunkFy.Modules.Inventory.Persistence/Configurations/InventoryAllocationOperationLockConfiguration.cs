namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class
    InventoryAllocationOperationLockConfiguration
    : IEntityTypeConfiguration<
        InventoryAllocationOperationLock>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(
        EntityTypeBuilder<
            InventoryAllocationOperationLock> builder)
    {
        builder.ToTable(
            "allocation_operation_locks",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_allocation_operation_locks_revision",
                    "\"Revision\" >= 1");
                table.HasCheckConstraint(
                    "CK_allocation_operation_locks_coordinates",
                    $"\"Id\" <> '{EmptyGuid}' AND \"Id\" = \"AllocationId\" AND " +
                    "length(trim(\"ScopeId\")) > 0");
            });
        builder.HasKey(resourceLock => resourceLock.Id);
        builder.HasAlternateKey(resourceLock => new
        {
            resourceLock.ScopeId,
            resourceLock.Id
        });
        builder.Property(resourceLock => resourceLock.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(resourceLock => resourceLock.Revision)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(resourceLock => new
        {
            resourceLock.ScopeId,
            resourceLock.AllocationId
        }).IsUnique();
    }
}

namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using BunkFy.Modules.Inventory.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class ManualInventoryBlockConfiguration : IEntityTypeConfiguration<ManualInventoryBlock>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<ManualInventoryBlock> builder)
    {
        builder.ToTable("manual_blocks", table =>
        {
            table.HasCheckConstraint(
                "CK_manual_blocks_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND " +
                $"\"BlockGroupId\" <> '{EmptyGuid}' AND " +
                $"\"PropertyId\" <> '{EmptyGuid}' AND " +
                $"\"InventoryUnitId\" <> '{EmptyGuid}' AND " +
                "length(trim(\"ScopeId\")) > 0");
            table.HasCheckConstraint(
                "CK_manual_blocks_content",
                "\"Arrival\" < \"Departure\" AND length(trim(\"Reason\")) > 0");
            table.HasCheckConstraint(
                "CK_manual_blocks_lifecycle",
                "(\"Status\" = 1 AND \"Version\" = 1 AND \"ReleasedAtUtc\" IS NULL) OR " +
                "(\"Status\" = 2 AND \"Version\" = 2 AND \"ReleasedAtUtc\" IS NOT NULL AND " +
                "\"ReleasedAtUtc\" >= \"CreatedAtUtc\")");
        });
        builder.HasKey(block => block.Id);
        builder.Property(block => block.BlockGroupId).IsRequired();
        builder.Property(block => block.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(block => block.Reason).HasMaxLength(ManualInventoryBlock.ReasonMaxLength).IsRequired();
        builder.Property(block => block.Status).HasConversion<int>().IsRequired();
        builder.Property(block => block.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(block => new
        {
            block.ScopeId,
            block.PropertyId,
            block.InventoryUnitId,
            block.Status,
            block.Arrival,
            block.Departure
        });
        builder.HasIndex(block => new { block.ScopeId, block.PropertyId, block.BlockGroupId, block.Status });
        builder.HasIndex(block => new
        {
            block.ScopeId,
            block.PropertyId,
            block.BlockGroupId,
            block.InventoryUnitId
        })
            .IsUnique()
            .HasDatabaseName("UX_inventory_manual_blocks_group_unit");
        builder.HasOne<ManualInventoryBlockGroup>()
            .WithMany()
            .HasForeignKey(block => new
            {
                block.ScopeId,
                block.PropertyId,
                block.BlockGroupId
            })
            .HasPrincipalKey(group => new
            {
                group.ScopeId,
                group.PropertyId,
                group.Id
            })
            .HasConstraintName("FK_inventory_manual_blocks_group")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InventoryUnit>()
            .WithMany()
            .HasForeignKey(block => new
            {
                block.ScopeId,
                block.PropertyId,
                block.InventoryUnitId
            })
            .HasPrincipalKey(unit => new
            {
                unit.ScopeId,
                unit.PropertyId,
                unit.Id
            })
            .HasConstraintName("FK_inventory_manual_blocks_inventory_unit")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(block => block.DomainEvents);
    }
}

namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using BunkFy.Modules.Properties.Contracts;

internal sealed class InventoryUnitConfiguration : IEntityTypeConfiguration<InventoryUnit>
{
    public void Configure(EntityTypeBuilder<InventoryUnit> builder)
    {
        builder.ToTable("inventory_units");
        builder.HasKey(unit => unit.Id);
        builder.HasAlternateKey(unit => new
        {
            unit.ScopeId,
            unit.PropertyId,
            unit.Id
        });
        builder.Property(unit => unit.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(unit => unit.Kind).HasConversion<int>().IsRequired();
        builder.Property(unit => unit.Label).HasMaxLength(PropertiesContractLimits.RoomNameMaxLength).IsRequired();
        builder.Property(unit => unit.AvailabilityMutationVersion).IsConcurrencyToken().IsRequired();
        builder.HasIndex(unit => new { unit.ScopeId, unit.PropertyId, unit.RoomId, unit.Kind });
    }
}

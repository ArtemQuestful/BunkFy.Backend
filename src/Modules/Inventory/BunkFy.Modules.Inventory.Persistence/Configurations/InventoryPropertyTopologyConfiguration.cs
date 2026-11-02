namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using BunkFy.Modules.Properties.Contracts;

internal sealed class InventoryPropertyTopologyConfiguration : IEntityTypeConfiguration<InventoryPropertyTopology>
{
    public void Configure(EntityTypeBuilder<InventoryPropertyTopology> builder)
    {
        builder.ToTable("property_topology");
        builder.HasKey(property => property.Id);
        builder.Property(property => property.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(property => property.Name).HasMaxLength(PropertiesContractLimits.PropertyNameMaxLength).IsRequired();
        builder.Property(property => property.Code).HasMaxLength(PropertiesContractLimits.PropertyCodeMaxLength).IsRequired();
        builder.Property(property => property.TimeZoneId).HasMaxLength(PropertiesContractLimits.TimeZoneIdMaxLength).IsRequired();
        builder.Property(property => property.Status).HasConversion<int>();
        builder.Property(property => property.AvailabilitySelectionVersion)
            .HasDefaultValue(1L)
            .IsConcurrencyToken();
        builder.Property(property => property.ProjectionOrdinal).ValueGeneratedOnAdd();
        builder.HasIndex(property => new { property.ScopeId, property.Code });
        builder.HasIndex(property => property.ProjectionOrdinal).IsUnique();
    }
}

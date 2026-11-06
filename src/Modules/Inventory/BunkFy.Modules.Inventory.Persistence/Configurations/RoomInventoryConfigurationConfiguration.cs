namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using BunkFy.Modules.Inventory.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class RoomInventoryConfigurationConfiguration : IEntityTypeConfiguration<RoomInventoryConfiguration>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<RoomInventoryConfiguration> builder)
    {
        builder.ToTable("room_configurations", table =>
        {
            table.HasCheckConstraint(
                "CK_room_configurations_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                "length(trim(\"ScopeId\")) > 0");
            table.HasCheckConstraint(
                "CK_room_configurations_state",
                "\"AvailabilityMutationVersion\" >= \"Version\" AND " +
                "((\"Version\" = 1 AND \"SalesMode\" = 1 AND \"UpdatedAtUtc\" IS NULL) OR " +
                "(\"Version\" >= 2 AND \"SalesMode\" IN (2, 3) AND " +
                "\"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\"))");
        });
        builder.HasKey(configuration => configuration.Id);
        builder.Property(configuration => configuration.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(configuration => configuration.SalesMode).HasConversion<int>();
        builder.Property(configuration => configuration.Version).IsConcurrencyToken();
        builder.Property(configuration => configuration.AvailabilityMutationVersion).IsConcurrencyToken().IsRequired();
        builder.HasIndex(configuration => new { configuration.ScopeId, configuration.PropertyId });
        builder.Ignore(configuration => configuration.DomainEvents);
    }
}

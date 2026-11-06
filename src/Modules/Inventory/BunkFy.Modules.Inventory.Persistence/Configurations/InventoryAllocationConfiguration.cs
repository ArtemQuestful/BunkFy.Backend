namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class InventoryAllocationConfiguration : IEntityTypeConfiguration<InventoryAllocation>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<InventoryAllocation> builder)
    {
        builder.ToTable("allocations", table =>
        {
            table.HasCheckConstraint(
                "CK_allocations_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND " +
                $"\"ReservationId\" <> '{EmptyGuid}' AND " +
                $"\"AllocationRequestId\" <> '{EmptyGuid}' AND " +
                $"\"PropertyId\" <> '{EmptyGuid}' AND " +
                "length(trim(\"ScopeId\")) > 0");
            table.HasCheckConstraint(
                "CK_allocations_stay_and_version",
                "\"Arrival\" < \"Departure\" AND \"Version\" >= 1");
            table.HasCheckConstraint(
                "CK_allocations_lifecycle",
                "(\"Status\" = 1 AND \"Rejection\" = 0 AND " +
                "\"ReleaseRequestId\" IS NULL AND \"ReleasedAtUtc\" IS NULL) OR " +
                "(\"Status\" = 2 AND \"Rejection\" BETWEEN 1 AND 6 AND " +
                "\"ReleaseRequestId\" IS NULL AND \"ReleasedAtUtc\" IS NULL) OR " +
                "(\"Status\" = 3 AND \"Rejection\" = 0 AND " +
                $"\"ReleaseRequestId\" IS NOT NULL AND \"ReleaseRequestId\" <> '{EmptyGuid}' AND " +
                "\"ReleasedAtUtc\" IS NOT NULL AND \"ReleasedAtUtc\" >= \"CreatedAtUtc\")");
            table.HasCheckConstraint(
                "CK_allocations_anonymisation_state",
                "(\"IsAnonymised\" = TRUE AND \"AnonymisedAtUtc\" IS NOT NULL) OR " +
                "(\"IsAnonymised\" = FALSE AND \"AnonymisedAtUtc\" IS NULL)");
        });
        builder.HasKey(allocation => allocation.Id);
        builder.HasAlternateKey(allocation => new { allocation.ScopeId, allocation.Id });
        builder.Property(allocation => allocation.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(allocation => allocation.Status).HasConversion<int>().IsRequired();
        builder.Property(allocation => allocation.Rejection).HasConversion<int>().IsRequired();
        builder.Property(allocation => allocation.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(allocation => new { allocation.ScopeId, allocation.AllocationRequestId }).IsUnique();
        builder.HasIndex(allocation => new { allocation.ScopeId, allocation.ReservationId }).IsUnique();
        builder.HasIndex(allocation => new
        {
            allocation.ScopeId,
            allocation.PropertyId,
            allocation.Status,
            allocation.Arrival,
            allocation.Departure
        });
        builder.HasMany(allocation => allocation.Units)
            .WithOne()
            .HasForeignKey(unit => new { unit.ScopeId, unit.AllocationId })
            .HasPrincipalKey(allocation => new { allocation.ScopeId, allocation.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(allocation => allocation.Units).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(allocation => allocation.DomainEvents);
    }
}

internal sealed class InventoryAllocationUnitConfiguration : IEntityTypeConfiguration<InventoryAllocationUnit>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<InventoryAllocationUnit> builder)
    {
        builder.ToTable("allocation_units", table => table.HasCheckConstraint(
            "CK_allocation_units_coordinates",
            $"\"Id\" <> '{EmptyGuid}' AND \"AllocationId\" <> '{EmptyGuid}' AND " +
            "length(trim(\"ScopeId\")) > 0"));
        builder.HasKey(unit => new { unit.ScopeId, unit.AllocationId, unit.Id });
        builder.Property(unit => unit.ScopeId).HasMaxLength(128).IsRequired();
        builder.Ignore(unit => unit.InventoryUnitId);
        builder.HasIndex(unit => new { unit.ScopeId, unit.Id, unit.AllocationId });
    }
}

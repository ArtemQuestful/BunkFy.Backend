namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Properties.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class ManualInventoryBlockGroupConfiguration
    : IEntityTypeConfiguration<ManualInventoryBlockGroup>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<ManualInventoryBlockGroup> builder)
    {
        builder.ToTable("manual_block_groups", table =>
        {
            table.HasCheckConstraint(
                "CK_inventory_manual_block_groups_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND " +
                $"\"PropertyId\" <> '{EmptyGuid}' AND " +
                "char_length(btrim(\"ScopeId\")) > 0 AND " +
                "(\"ReplacesGroupId\" IS NULL OR " +
                $"(\"ReplacesGroupId\" <> '{EmptyGuid}' AND " +
                "\"ReplacesGroupId\" <> \"Id\"))");
            table.HasCheckConstraint(
                "CK_inventory_manual_block_groups_target",
                "(\"TargetKind\" = 0 AND \"SelectionDigest\" IS NULL AND " +
                "\"BuildingLabel\" IS NULL AND " +
                "\"FloorLabel\" IS NULL AND \"RoomId\" IS NULL AND " +
                "\"InventoryUnitId\" IS NULL) OR " +
                "(\"TargetKind\" = 1 AND \"SelectionDigest\" IS NOT NULL AND " +
                "\"BuildingLabel\" IS NULL AND " +
                "\"FloorLabel\" IS NULL AND \"RoomId\" IS NULL AND " +
                "\"InventoryUnitId\" IS NULL) OR " +
                "(\"TargetKind\" = 2 AND \"SelectionDigest\" IS NOT NULL AND " +
                "\"BuildingLabel\" IS NOT NULL AND " +
                "char_length(btrim(\"BuildingLabel\")) > 0 AND " +
                "\"BuildingLabel\" = btrim(\"BuildingLabel\") AND " +
                "\"BuildingLabel\" !~ '[[:cntrl:]]' AND " +
                "\"FloorLabel\" IS NULL AND \"RoomId\" IS NULL AND " +
                "\"InventoryUnitId\" IS NULL) OR " +
                "(\"TargetKind\" = 3 AND \"SelectionDigest\" IS NOT NULL AND " +
                "\"FloorLabel\" IS NOT NULL AND " +
                "char_length(btrim(\"FloorLabel\")) > 0 AND " +
                "\"FloorLabel\" = btrim(\"FloorLabel\") AND " +
                "\"FloorLabel\" !~ '[[:cntrl:]]' AND " +
                "(\"BuildingLabel\" IS NULL OR " +
                "(char_length(btrim(\"BuildingLabel\")) > 0 AND " +
                "\"BuildingLabel\" = btrim(\"BuildingLabel\") AND " +
                "\"BuildingLabel\" !~ '[[:cntrl:]]')) AND " +
                "\"RoomId\" IS NULL AND \"InventoryUnitId\" IS NULL) OR " +
                "(\"TargetKind\" = 4 AND \"SelectionDigest\" IS NOT NULL AND " +
                "\"BuildingLabel\" IS NULL AND " +
                "\"FloorLabel\" IS NULL AND \"RoomId\" IS NOT NULL AND " +
                $"\"RoomId\" <> '{EmptyGuid}' AND " +
                "\"InventoryUnitId\" IS NULL) OR " +
                "(\"TargetKind\" = 5 AND \"SelectionDigest\" IS NOT NULL AND " +
                "\"BuildingLabel\" IS NULL AND " +
                "\"FloorLabel\" IS NULL AND \"RoomId\" IS NULL AND " +
                "\"InventoryUnitId\" IS NOT NULL AND " +
                $"\"InventoryUnitId\" <> '{EmptyGuid}')");
            table.HasCheckConstraint(
                "CK_inventory_manual_block_groups_range",
                "\"Arrival\" < \"Departure\"");
            table.HasCheckConstraint(
                "CK_inventory_manual_block_groups_text",
                "char_length(\"Reason\") BETWEEN 1 AND " +
                $"{ManualInventoryBlock.ReasonMaxLength} AND " +
                "\"Reason\" = btrim(\"Reason\") AND " +
                "\"Reason\" !~ '[[:cntrl:]]'");
            table.HasCheckConstraint(
                "CK_inventory_manual_block_groups_actors",
                "((\"TargetKind\" = 0 AND " +
                "\"CreatedByActorId\" IS NULL AND " +
                "(\"LastModifiedByActorId\" IS NULL OR " +
                "(char_length(btrim(\"LastModifiedByActorId\")) > 0 AND " +
                "\"LastModifiedByActorId\" = btrim(\"LastModifiedByActorId\") AND " +
                "\"LastModifiedByActorId\" !~ '[[:cntrl:]]'))) OR " +
                "(\"TargetKind\" BETWEEN 1 AND 5 AND " +
                "\"CreatedByActorId\" IS NOT NULL AND " +
                "char_length(btrim(\"CreatedByActorId\")) > 0 AND " +
                "\"CreatedByActorId\" = btrim(\"CreatedByActorId\") AND " +
                "\"CreatedByActorId\" !~ '[[:cntrl:]]' AND " +
                "\"LastModifiedByActorId\" IS NOT NULL AND " +
                "char_length(btrim(\"LastModifiedByActorId\")) > 0 AND " +
                "\"LastModifiedByActorId\" = btrim(\"LastModifiedByActorId\") AND " +
                "\"LastModifiedByActorId\" !~ '[[:cntrl:]]'))");
            table.HasCheckConstraint(
                "CK_inventory_manual_block_groups_digests",
                "(\"SelectionDigest\" IS NULL OR " +
                "(char_length(\"SelectionDigest\") = 64 AND " +
                "\"SelectionDigest\" ~ '^[0-9a-f]{64}$')) AND " +
                "char_length(\"MembershipDigest\") = 64 AND " +
                "\"MembershipDigest\" ~ '^[0-9a-f]{64}$' AND " +
                $"\"MembershipDigestVersion\" = {ManualInventoryBlockGroup.CurrentMembershipDigestVersion}");
            table.HasCheckConstraint(
                "CK_inventory_manual_block_groups_counts",
                "\"InitialBlockCount\" > 0 AND " +
                "\"ActiveBlockCount\" BETWEEN 0 AND \"InitialBlockCount\" AND " +
                $"(\"InitialBlockCount\" <= {ManualInventoryBlockGroup.MaximumMemberCount} OR " +
                "(\"TargetKind\" = 0 AND \"State\" = 3 AND " +
                "\"ActiveBlockCount\" = 0))");
            table.HasCheckConstraint(
                "CK_inventory_manual_block_groups_lifecycle",
                "\"Version\" > 0 AND " +
                "(\"UpdatedAtUtc\" IS NULL OR \"UpdatedAtUtc\" >= \"CreatedAtUtc\") AND " +
                "(\"ReleasedAtUtc\" IS NULL OR " +
                "\"ReleasedAtUtc\" >= \"CreatedAtUtc\") AND " +
                "((\"State\" = 1 AND " +
                "\"ActiveBlockCount\" = \"InitialBlockCount\" AND " +
                "\"UpdatedAtUtc\" IS NULL AND \"ReleasedAtUtc\" IS NULL) OR " +
                "(\"State\" = 2 AND \"ActiveBlockCount\" > 0 AND " +
                "\"ActiveBlockCount\" < \"InitialBlockCount\" AND " +
                "\"UpdatedAtUtc\" IS NOT NULL AND \"ReleasedAtUtc\" IS NULL) OR " +
                "(\"State\" IN (3, 4) AND \"ActiveBlockCount\" = 0 AND " +
                "\"UpdatedAtUtc\" IS NOT NULL AND " +
                "\"ReleasedAtUtc\" = \"UpdatedAtUtc\"))");
        });

        builder.HasKey(group => new
        {
            group.ScopeId,
            group.PropertyId,
            group.Id
        }).HasName("PK_inventory_manual_block_groups");
        builder.Property(group => group.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(group => group.TargetKind)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(group => group.BuildingLabel)
            .HasMaxLength(PropertiesContractLimits.PhysicalLabelMaxLength);
        builder.Property(group => group.FloorLabel)
            .HasMaxLength(PropertiesContractLimits.PhysicalLabelMaxLength);
        builder.Property(group => group.Reason)
            .HasMaxLength(ManualInventoryBlock.ReasonMaxLength)
            .IsRequired();
        builder.Property(group => group.SelectionDigest)
            .HasMaxLength(ManualInventoryBlockGroup.SelectionDigestLength)
            .IsFixedLength();
        builder.Property(group => group.MembershipDigest)
            .HasMaxLength(ManualInventoryBlockGroup.SelectionDigestLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(group => group.State)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(group => group.Version)
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(group => group.CreatedByActorId)
            .HasMaxLength(ManualInventoryBlockGroup.ActorIdMaxLength);
        builder.Property(group => group.LastModifiedByActorId)
            .HasMaxLength(ManualInventoryBlockGroup.ActorIdMaxLength);

        builder.HasIndex(group => new
        {
            group.ScopeId,
            group.PropertyId,
            group.CreatedAtUtc,
            group.Id
        })
            .IsDescending(false, false, true, false)
            .HasDatabaseName(
                "IX_inventory_manual_block_groups_property_created");
        builder.HasIndex(group => new
        {
            group.ScopeId,
            group.PropertyId,
            group.State,
            group.CreatedAtUtc,
            group.Id
        })
            .IsDescending(false, false, false, true, false)
            .HasDatabaseName(
                "IX_inventory_manual_block_groups_property_state_created");
        builder.HasIndex(group => new
        {
            group.ScopeId,
            group.PropertyId,
            group.ReplacesGroupId
        })
            .IsUnique()
            .HasFilter("\"ReplacesGroupId\" IS NOT NULL")
            .HasDatabaseName("UX_inventory_manual_block_groups_replaces");
        builder.HasOne<ManualInventoryBlockGroup>()
            .WithMany()
            .HasForeignKey(group => new
            {
                group.ScopeId,
                group.PropertyId,
                group.ReplacesGroupId
            })
            .HasPrincipalKey(group => new
            {
                group.ScopeId,
                group.PropertyId,
                group.Id
            })
            .HasConstraintName("FK_inventory_manual_block_groups_replaces")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(group => group.DomainEvents);
    }
}

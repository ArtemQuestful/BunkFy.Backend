namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using BunkFy.Modules.Inventory.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class InventoryManagementOperationConfiguration
    : IEntityTypeConfiguration<InventoryManagementOperation>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(
        EntityTypeBuilder<InventoryManagementOperation> builder)
    {
        builder.ToTable("management_operations", table =>
        {
            table.HasCheckConstraint(
                "CK_inventory_management_operations_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND " +
                $"\"PropertyId\" <> '{EmptyGuid}' AND " +
                $"\"ResourceId\" <> '{EmptyGuid}' AND " +
                "char_length(btrim(\"ScopeId\")) > 0");
            table.HasCheckConstraint(
                "CK_inventory_management_operations_fingerprint",
                "char_length(\"RequestFingerprint\") = 64 AND " +
                "\"RequestFingerprint\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "CK_inventory_management_operations_kind",
                "\"Kind\" BETWEEN 1 AND 14 AND " +
                "((\"Kind\" = 1 AND \"ResourceKind\" = 1) OR " +
                "(\"Kind\" IN (2, 3) AND \"ResourceKind\" = 2 AND " +
                "\"ResourceId\" = \"PropertyId\") OR " +
                "(\"Kind\" = 4 AND \"ResourceKind\" = 3 AND " +
                "\"ResultBlockId\" IS NOT NULL AND " +
                "\"ResourceId\" = \"ResultBlockId\") OR " +
                "(\"Kind\" = 5 AND \"ResourceKind\" = 4 AND " +
                "\"ResultBlockGroupId\" IS NOT NULL AND " +
                "\"ResourceId\" = \"ResultBlockGroupId\") OR " +
                "(\"Kind\" = 6 AND \"ResourceKind\" = 5) OR " +
                "(\"Kind\" = 7 AND \"ResourceKind\" = 6 AND " +
                "\"ResourceId\" = \"ResultTopologyChangeId\") OR " +
                "(\"Kind\" = 8 AND \"ResourceKind\" = 1) OR " +
                "(\"Kind\" = 9 AND \"ResourceKind\" = 7 AND " +
                "\"ResourceId\" = \"ResultTopologyChangeId\") OR " +
                "(\"Kind\" = 10 AND \"ResourceKind\" = 6 AND " +
                "\"ResourceId\" = \"ResultTopologyChangeId\") OR " +
                "(\"Kind\" = 11 AND \"ResourceKind\" = 7 AND " +
                "\"ResourceId\" = \"ResultTopologyChangeId\") OR " +
                "(\"Kind\" = 12 AND \"ResourceKind\" = 2 AND " +
                "\"ResourceId\" = \"PropertyId\") OR " +
                "(\"Kind\" = 13 AND \"ResourceKind\" = 4 AND " +
                "\"ResourceId\" = \"ResultPreviousBlockGroupId\") OR " +
                "(\"Kind\" = 14 AND \"ResourceKind\" = 4 AND " +
                "\"ResourceId\" = \"ResultBlockGroupId\"))");
            table.HasCheckConstraint(
                "CK_inventory_management_operations_group_v2_shape",
                "((\"Kind\" BETWEEN 1 AND 11 AND " +
                "\"ResultBlockGroupStatus\" IS NULL AND " +
                "\"ResultPreviousBlockGroupId\" IS NULL AND " +
                "\"ResultTotalBlockCount\" IS NULL AND " +
                "\"ResultActiveBlockCount\" IS NULL AND " +
                "\"ResultReleasedBlockCount\" IS NULL AND " +
                "\"ResultAlreadyReleasedBlockCount\" IS NULL AND " +
                "\"ResultCreatedBlockCount\" IS NULL AND " +
                "\"ResultMembershipDigest\" IS NULL) OR " +
                "(\"Kind\" = 12 AND " +
                "\"ResultBlockGroupStatus\" = 1 AND " +
                "\"ResultPreviousBlockGroupId\" IS NULL AND " +
                $"\"ResultTotalBlockCount\" BETWEEN 1 AND {ManualInventoryBlockGroup.MaximumMemberCount} AND " +
                "\"ResultActiveBlockCount\" = \"ResultTotalBlockCount\" AND " +
                "\"ResultReleasedBlockCount\" = 0 AND " +
                "\"ResultAlreadyReleasedBlockCount\" = 0 AND " +
                "\"ResultCreatedBlockCount\" = \"ResultTotalBlockCount\" AND " +
                "\"ResultAffectedBlockCount\" = \"ResultTotalBlockCount\" AND " +
                "\"ResultVersion\" = 1 AND " +
                "\"ResultMembershipDigest\" IS NOT NULL) OR " +
                "(\"Kind\" = 13 AND " +
                "\"ResultPreviousBlockGroupId\" = \"ResourceId\" AND " +
                $"\"ResultTotalBlockCount\" BETWEEN 1 AND {ManualInventoryBlockGroup.MaximumMemberCount} AND " +
                "\"ResultActiveBlockCount\" > 0 AND " +
                "\"ResultReleasedBlockCount\" >= 0 AND " +
                "\"ResultAlreadyReleasedBlockCount\" >= 0 AND " +
                "\"ResultCreatedBlockCount\" >= 0 AND " +
                "\"ResultMembershipDigest\" IS NOT NULL AND " +
                "((\"ResultBlockGroupId\" = \"ResourceId\" AND " +
                "\"ResultVersion\" = \"ExpectedVersion\" AND " +
                "\"ResultAffectedBlockCount\" = 0 AND " +
                "\"ResultReleasedBlockCount\" = 0 AND " +
                "\"ResultCreatedBlockCount\" = 0 AND " +
                "\"ResultTotalBlockCount\" = \"ResultActiveBlockCount\" + " +
                "\"ResultAlreadyReleasedBlockCount\" AND " +
                "((\"ResultBlockGroupStatus\" = 1 AND " +
                "\"ResultActiveBlockCount\" = \"ResultTotalBlockCount\") OR " +
                "(\"ResultBlockGroupStatus\" = 2 AND " +
                "\"ResultActiveBlockCount\" < \"ResultTotalBlockCount\"))) OR " +
                "(\"ResultBlockGroupId\" <> \"ResourceId\" AND " +
                "\"ResultVersion\" = 1 AND " +
                "\"ResultBlockGroupStatus\" = 1 AND " +
                "\"ResultActiveBlockCount\" = \"ResultTotalBlockCount\" AND " +
                "\"ResultCreatedBlockCount\" = \"ResultTotalBlockCount\" AND " +
                "\"ResultReleasedBlockCount\" > 0 AND " +
                "\"ResultAffectedBlockCount\" = \"ResultReleasedBlockCount\" + " +
                "\"ResultCreatedBlockCount\"))) OR " +
                "(\"Kind\" = 14 AND " +
                "\"ResultPreviousBlockGroupId\" IS NULL AND " +
                "\"ResultBlockGroupId\" = \"ResourceId\" AND " +
                "\"ResultTotalBlockCount\" > 0 AND " +
                "\"ResultActiveBlockCount\" = 0 AND " +
                "\"ResultCreatedBlockCount\" = 0 AND " +
                "\"ResultMembershipDigest\" IS NOT NULL AND " +
                "((\"ResultVersion\" = \"ExpectedVersion\" AND " +
                "\"ResultBlockGroupStatus\" IN (3, 4) AND " +
                "\"ResultAffectedBlockCount\" = 0 AND " +
                "\"ResultReleasedBlockCount\" = 0 AND " +
                "\"ResultAlreadyReleasedBlockCount\" = \"ResultTotalBlockCount\") OR " +
                "(\"ResultVersion\" = \"ExpectedVersion\" + 1 AND " +
                "\"ResultBlockGroupStatus\" = 3 AND " +
                "\"ResultReleasedBlockCount\" > 0 AND " +
                "\"ResultAffectedBlockCount\" = \"ResultReleasedBlockCount\" AND " +
                "\"ResultTotalBlockCount\" = \"ResultReleasedBlockCount\" + " +
                "\"ResultAlreadyReleasedBlockCount\" AND " +
                $"\"ResultTotalBlockCount\" <= {ManualInventoryBlockGroup.MaximumMemberCount}))))");
            table.HasCheckConstraint(
                "CK_inventory_management_operations_result",
                "(\"Kind\" = 1 AND \"ExpectedVersion\" > 0 AND " +
                "\"ResultVersion\" BETWEEN \"ExpectedVersion\" AND " +
                "\"ExpectedVersion\" + 1 AND " +
                "\"ResultSalesMode\" IS NOT NULL AND " +
                "\"ResultSalesMode\" IN (2, 3) AND " +
                "\"ResultBlockId\" IS NULL AND " +
                "\"ResultBlockGroupId\" IS NULL AND " +
                "\"ResultBlockStatus\" IS NULL AND " +
                "\"ResultAffectedBlockCount\" IS NULL AND " +
                "\"ResultTopologyChangeId\" IS NULL) OR " +
                "(\"Kind\" = 2 AND \"ExpectedVersion\" = 0 AND " +
                "\"ResultVersion\" = 1 AND " +
                "\"ResultSalesMode\" IS NULL AND " +
                "\"ResultBlockId\" IS NOT NULL AND " +
                $"\"ResultBlockId\" <> '{EmptyGuid}' AND " +
                "\"ResultBlockGroupId\" IS NOT NULL AND " +
                $"\"ResultBlockGroupId\" <> '{EmptyGuid}' AND " +
                "\"ResultBlockStatus\" IS NOT NULL AND " +
                "\"ResultBlockStatus\" = 1 AND " +
                "\"ResultAffectedBlockCount\" IS NOT NULL AND " +
                "\"ResultAffectedBlockCount\" = 1 AND " +
                "\"ResultTopologyChangeId\" IS NULL) OR " +
                "(\"Kind\" = 3 AND \"ExpectedVersion\" = 0 AND " +
                "\"ResultVersion\" = 0 AND " +
                "\"ResultSalesMode\" IS NULL AND " +
                "\"ResultBlockId\" IS NULL AND " +
                "\"ResultBlockGroupId\" IS NOT NULL AND " +
                $"\"ResultBlockGroupId\" <> '{EmptyGuid}' AND " +
                "\"ResultBlockStatus\" IS NULL AND " +
                "\"ResultAffectedBlockCount\" IS NOT NULL AND " +
                "\"ResultAffectedBlockCount\" > 0 AND " +
                "\"ResultTopologyChangeId\" IS NULL) OR " +
                "(\"Kind\" = 4 AND \"ExpectedVersion\" > 0 AND " +
                "\"ResultVersion\" = \"ExpectedVersion\" + 1 AND " +
                "\"ResultSalesMode\" IS NULL AND " +
                "\"ResultBlockId\" IS NOT NULL AND " +
                $"\"ResultBlockId\" <> '{EmptyGuid}' AND " +
                "\"ResultBlockGroupId\" IS NOT NULL AND " +
                $"\"ResultBlockGroupId\" <> '{EmptyGuid}' AND " +
                "\"ResultBlockStatus\" IS NOT NULL AND " +
                "\"ResultBlockStatus\" = 2 AND " +
                "\"ResultAffectedBlockCount\" IS NOT NULL AND " +
                "\"ResultAffectedBlockCount\" = 1 AND " +
                "\"ResultTopologyChangeId\" IS NULL) OR " +
                "(\"Kind\" = 5 AND \"ExpectedVersion\" = 0 AND " +
                "\"ResultVersion\" = 0 AND " +
                "\"ResultSalesMode\" IS NULL AND " +
                "\"ResultBlockId\" IS NULL AND " +
                "\"ResultBlockGroupId\" IS NOT NULL AND " +
                $"\"ResultBlockGroupId\" <> '{EmptyGuid}' AND " +
                "\"ResultBlockStatus\" IS NULL AND " +
                "\"ResultAffectedBlockCount\" IS NOT NULL AND " +
                "\"ResultAffectedBlockCount\" > 0 AND " +
                "\"ResultTopologyChangeId\" IS NULL) OR " +
                "(\"Kind\" IN (6, 8) AND \"ExpectedVersion\" = 0 AND " +
                "\"ResultVersion\" > 0 AND " +
                "\"ResultSalesMode\" IS NULL AND " +
                "\"ResultBlockId\" IS NULL AND " +
                "\"ResultBlockGroupId\" IS NULL AND " +
                "\"ResultBlockStatus\" IS NULL AND " +
                "\"ResultAffectedBlockCount\" IS NULL AND " +
                "\"ResultTopologyChangeId\" IS NOT NULL AND " +
                $"\"ResultTopologyChangeId\" <> '{EmptyGuid}') OR " +
                "(\"Kind\" IN (7, 9, 10, 11) AND \"ExpectedVersion\" > 0 AND " +
                "\"ResultVersion\" = \"ExpectedVersion\" + 1 AND " +
                "\"ResultSalesMode\" IS NULL AND " +
                "\"ResultBlockId\" IS NULL AND " +
                "\"ResultBlockGroupId\" IS NULL AND " +
                "\"ResultBlockStatus\" IS NULL AND " +
                "\"ResultAffectedBlockCount\" IS NULL AND " +
                "\"ResultTopologyChangeId\" IS NOT NULL AND " +
                $"\"ResultTopologyChangeId\" <> '{EmptyGuid}') OR " +
                "(\"Kind\" IN (12, 13, 14) AND " +
                "\"ResultSalesMode\" IS NULL AND " +
                "\"ResultBlockId\" IS NULL AND " +
                "\"ResultBlockGroupId\" IS NOT NULL AND " +
                $"\"ResultBlockGroupId\" <> '{EmptyGuid}' AND " +
                "\"ResultBlockStatus\" IS NULL AND " +
                "\"ResultAffectedBlockCount\" IS NOT NULL AND " +
                "\"ResultAffectedBlockCount\" >= 0 AND " +
                "\"ResultTopologyChangeId\" IS NULL AND " +
                "\"ResultBlockGroupStatus\" IS NOT NULL AND " +
                "\"ResultBlockGroupStatus\" BETWEEN 1 AND 4 AND " +
                "\"ResultTotalBlockCount\" IS NOT NULL AND " +
                "\"ResultTotalBlockCount\" > 0 AND " +
                "\"ResultActiveBlockCount\" IS NOT NULL AND " +
                "\"ResultActiveBlockCount\" BETWEEN 0 AND " +
                "\"ResultTotalBlockCount\" AND " +
                "\"ResultReleasedBlockCount\" IS NOT NULL AND " +
                "\"ResultReleasedBlockCount\" >= 0 AND " +
                "\"ResultAlreadyReleasedBlockCount\" IS NOT NULL AND " +
                "\"ResultAlreadyReleasedBlockCount\" >= 0 AND " +
                "\"ResultCreatedBlockCount\" IS NOT NULL AND " +
                "\"ResultCreatedBlockCount\" >= 0 AND " +
                "char_length(\"ResultMembershipDigest\") = 64 AND " +
                "\"ResultMembershipDigest\" ~ '^[0-9a-f]{64}$' AND " +
                "((\"Kind\" = 12 AND \"ExpectedVersion\" = 0 AND " +
                "\"ResultVersion\" = 1 AND " +
                "\"ResultPreviousBlockGroupId\" IS NULL) OR " +
                "(\"Kind\" = 13 AND \"ExpectedVersion\" > 0 AND " +
                "\"ResultVersion\" > 0 AND " +
                "\"ResultPreviousBlockGroupId\" IS NOT NULL AND " +
                $"\"ResultPreviousBlockGroupId\" <> '{EmptyGuid}') OR " +
                "(\"Kind\" = 14 AND \"ExpectedVersion\" > 0 AND " +
                "\"ResultVersion\" > 0 AND " +
                "\"ResultPreviousBlockGroupId\" IS NULL)))");
        });
        builder.HasKey(operation => new
        {
            operation.ScopeId,
            operation.ResourceKind,
            operation.ResourceId,
            operation.Id
        });
        builder.Property(operation => operation.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(operation => operation.ResourceKind)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(operation => operation.Kind)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(operation => operation.RequestFingerprint)
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();
        builder.Property(operation => operation.ResultSalesMode)
            .HasConversion<int?>();
        builder.Property(operation => operation.ResultBlockStatus)
            .HasConversion<int?>();
        builder.Property(operation => operation.ResultBlockGroupStatus)
            .HasConversion<int?>();
        builder.Property(operation => operation.ResultMembershipDigest)
            .HasMaxLength(64)
            .IsFixedLength();
        builder.HasIndex(operation => new
        {
            operation.ScopeId,
            operation.PropertyId,
            operation.CompletedAtUtc,
            operation.ResourceKind,
            operation.ResourceId,
            operation.Id
        });
    }
}

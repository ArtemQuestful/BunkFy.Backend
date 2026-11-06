namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class InventoryAllocationAmendmentDecisionConfiguration
    : IEntityTypeConfiguration<InventoryAllocationAmendmentDecision>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<InventoryAllocationAmendmentDecision> builder)
    {
        builder.ToTable("allocation_amendment_decisions", table =>
        {
            table.HasCheckConstraint(
                "CK_allocation_amendment_decisions_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND " +
                $"\"AllocationId\" <> '{EmptyGuid}' AND " +
                $"\"ReservationId\" <> '{EmptyGuid}' AND " +
                $"\"PropertyId\" <> '{EmptyGuid}' AND " +
                "char_length(\"ScopeId\") > 0 AND " +
                "\"ScopeId\" = btrim(\"ScopeId\") AND " +
                "\"ScopeId\" !~ '[[:space:][:cntrl:]]'");
            table.HasCheckConstraint(
                "CK_allocation_amendment_decisions_fingerprint",
                "length(\"RequestFingerprint\") = 64 AND " +
                "\"RequestFingerprint\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "CK_allocation_amendment_decisions_outcome",
                "(\"Confirmed\" = TRUE AND \"RejectionReason\" IS NULL AND " +
                "\"AllocationVersion\" IS NOT NULL AND \"AllocationVersion\" >= 1) OR " +
                "(\"Confirmed\" = FALSE AND \"RejectionReason\" IS NOT NULL AND " +
                "\"RejectionReason\" BETWEEN 1 AND 11 AND " +
                "\"AllocationVersion\" IS NULL)");
            table.HasCheckConstraint(
                "CK_allocation_amendment_decisions_decided_at",
                "\"DecidedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");
        });
        builder.HasKey(decision => new { decision.ScopeId, decision.Id });
        builder.Property(decision => decision.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(decision => decision.RequestFingerprint).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(decision => decision.RejectionReason).HasConversion<int?>();
        builder.HasIndex(decision => new { decision.ScopeId, decision.AllocationId, decision.DecidedAtUtc });
    }
}

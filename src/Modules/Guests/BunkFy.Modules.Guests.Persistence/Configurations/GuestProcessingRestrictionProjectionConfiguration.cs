namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.DataRights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestProcessingRestrictionProjectionConfiguration
    : IEntityTypeConfiguration<GuestProcessingRestrictionProjection>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<GuestProcessingRestrictionProjection> builder)
    {
        builder.ToTable("guest_processing_restriction_state", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_projection_coordinates",
                $"\"PropertyId\" <> '{EmptyGuid}' AND \"GuestId\" <> '{EmptyGuid}' AND " +
                "trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_contract_version",
                "\"ContractVersion\" >= 1");
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_revision",
                "\"Revision\" >= 0");
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_projection_ordinal",
                "\"ProjectionOrdinal\" >= 1");
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_transition_timestamp",
                "\"LastTransitionAtUtc\" > " +
                "TIMESTAMPTZ '0001-01-01 00:00:00+00'");
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_effective_state",
                "\"ActiveRestrictionCount\" >= 0 AND " +
                "\"Revision\" >= \"ActiveRestrictionCount\" AND " +
                "MOD(\"Revision\" - \"ActiveRestrictionCount\", 2) = 0 AND " +
                "((\"ActiveRestrictionCount\" = 0 AND NOT \"IsRestricted\") OR " +
                "(\"ActiveRestrictionCount\" > 0 AND \"IsRestricted\"))");
        });
        builder.HasKey(projection => new
        {
            projection.ScopeId,
            projection.PropertyId,
            projection.GuestId
        });
        builder.Property(projection => projection.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(projection => projection.ProjectionOrdinal)
            .ValueGeneratedOnAdd()
            .IsRequired();
        builder.Property(projection => projection.Revision).IsConcurrencyToken();
        builder.HasIndex(projection => projection.ProjectionOrdinal).IsUnique();
        builder.HasIndex(projection => new
        {
            projection.ScopeId,
            projection.PropertyId,
            projection.IsRestricted,
            projection.GuestId
        });
    }
}

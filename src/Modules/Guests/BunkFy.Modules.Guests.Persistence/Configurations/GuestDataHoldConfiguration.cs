namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestDataHoldConfiguration : IEntityTypeConfiguration<GuestDataHold>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<GuestDataHold> builder)
    {
        builder.ToTable("data_holds", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_data_holds_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                $"\"GuestId\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_guest_data_holds_audit_text",
                "length(trim(\"ReasonCode\")) > 0 AND " +
                "\"ReasonCode\" = lower(trim(\"ReasonCode\")) AND " +
                "length(trim(\"PlacedBy\")) > 0 AND \"PlacedBy\" = trim(\"PlacedBy\") AND " +
                "(\"ReleasedBy\" IS NULL OR (length(trim(\"ReleasedBy\")) > 0 AND " +
                "\"ReleasedBy\" = trim(\"ReleasedBy\")))");
            table.HasCheckConstraint(
                "CK_guest_data_holds_timestamps",
                "\"PlacedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND " +
                "(\"ReleasedAtUtc\" IS NULL OR " +
                "\"ReleasedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00')");
            table.HasCheckConstraint("CK_guest_data_holds_version", "\"Version\" >= 1");
            table.HasCheckConstraint(
                "CK_guest_data_holds_lifecycle",
                "(\"State\" = 1 AND \"ReleasedBy\" IS NULL AND \"ReleasedAtUtc\" IS NULL AND \"Version\" = 1) OR " +
                "(\"State\" = 2 AND \"ReleasedBy\" IS NOT NULL AND \"ReleasedAtUtc\" IS NOT NULL AND " +
                "\"ReleasedAtUtc\" >= \"PlacedAtUtc\" AND \"Version\" >= 2)");
        });
        builder.HasKey(hold => hold.Id);
        builder.HasAlternateKey(hold => new
        {
            hold.ScopeId,
            hold.Id,
            hold.PropertyId,
            hold.GuestId,
            hold.ReasonCode
        })
            .HasName("AK_guest_data_holds_receipt_evidence");
        builder.Property(hold => hold.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(hold => hold.ReasonCode)
            .HasMaxLength(GuestDataHold.ReasonCodeMaxLength)
            .IsRequired();
        builder.Property(hold => hold.State).HasConversion<int>().IsRequired();
        builder.Property(hold => hold.PlacedBy)
            .HasMaxLength(GuestProfile.ActorIdMaxLength)
            .IsRequired();
        builder.Property(hold => hold.ReleasedBy)
            .HasMaxLength(GuestProfile.ActorIdMaxLength);
        builder.Property(hold => hold.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(hold => new
        {
            hold.ScopeId,
            hold.GuestId,
            hold.State,
            hold.PropertyId,
            hold.Id
        });
        builder.HasIndex(hold => new
        {
            hold.ScopeId,
            hold.PropertyId,
            hold.GuestId,
            hold.PlacedAtUtc,
            hold.Id
        });
        builder.HasIndex(hold => new
        {
            hold.ScopeId,
            hold.PropertyId,
            hold.GuestId,
            hold.State,
            hold.PlacedAtUtc,
            hold.Id
        });
    }
}

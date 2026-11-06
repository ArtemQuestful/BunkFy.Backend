namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestDataHoldReceiptConfiguration
    : IEntityTypeConfiguration<GuestDataHoldReceipt>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<GuestDataHoldReceipt> builder)
    {
        builder.ToTable("data_hold_receipts", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_data_hold_receipts_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"IdempotencyKey\" <> '{EmptyGuid}' AND " +
                $"\"HoldId\" <> '{EmptyGuid}' AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                $"\"GuestId\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_guest_data_hold_receipts_audit_text",
                "length(trim(\"ReasonCode\")) > 0 AND " +
                "\"ReasonCode\" = lower(trim(\"ReasonCode\")) AND " +
                "length(trim(\"ActorId\")) > 0 AND \"ActorId\" = trim(\"ActorId\")");
            table.HasCheckConstraint(
                "CK_guest_data_hold_receipts_timestamp",
                "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");
            table.HasCheckConstraint(
                "CK_guest_data_hold_receipts_versions",
                "\"SelectedGuestVersion\" >= 1 AND " +
                "((\"Action\" = 1 AND \"ResultingHoldVersion\" = 1) OR " +
                "(\"Action\" = 2 AND \"ResultingHoldVersion\" >= 2))");
        });
        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(receipt => receipt.Action).HasConversion<int>().IsRequired();
        builder.Property(receipt => receipt.ReasonCode)
            .HasMaxLength(GuestDataHold.ReasonCodeMaxLength)
            .IsRequired();
        builder.Property(receipt => receipt.ActorId)
            .HasMaxLength(GuestProfile.ActorIdMaxLength)
            .IsRequired();
        builder.HasIndex(receipt => new { receipt.ScopeId, receipt.IdempotencyKey })
            .IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.HoldId,
            receipt.Action
        }).IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.PropertyId,
            receipt.GuestId,
            receipt.HoldId,
            receipt.CompletedAtUtc
        });
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.HoldId,
            receipt.PropertyId,
            receipt.GuestId,
            receipt.ReasonCode
        })
            .HasDatabaseName("IX_guest_data_hold_receipts_hold_evidence");
        builder.HasOne<GuestDataHold>()
            .WithMany()
            .HasPrincipalKey(hold => new
            {
                hold.ScopeId,
                hold.Id,
                hold.PropertyId,
                hold.GuestId,
                hold.ReasonCode
            })
            .HasForeignKey(receipt => new
            {
                receipt.ScopeId,
                receipt.HoldId,
                receipt.PropertyId,
                receipt.GuestId,
                receipt.ReasonCode
            })
            .HasConstraintName("FK_guest_data_hold_receipts_hold_evidence")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

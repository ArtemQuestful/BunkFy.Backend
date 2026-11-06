namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.DataRights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestAnonymisationRestoreReceiptConfiguration
    : IEntityTypeConfiguration<GuestAnonymisationRestoreReceipt>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(
        EntityTypeBuilder<GuestAnonymisationRestoreReceipt> builder)
    {
        builder.ToTable(
            "guest_anonymisation_restore_receipts",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_guest_anonymisation_restore_receipts_coordinates",
                    $"\"Id\" <> '{EmptyGuid}' AND \"LedgerEntryId\" <> '{EmptyGuid}' AND " +
                    $"\"GuestId\" <> '{EmptyGuid}' AND \"OwnerReceiptId\" <> '{EmptyGuid}' AND " +
                    "trim(\"ScopeId\") <> ''");
                table.HasCheckConstraint(
                    "CK_guest_anonymisation_restore_receipts_contract",
                    $"\"ContractVersion\" = {GuestAnonymisationRestoreReceipt.CurrentContractVersion}");
                table.HasCheckConstraint(
                    "CK_guest_anonymisation_restore_receipts_identity",
                    "\"LedgerEntryId\" = \"Id\"");
                table.HasCheckConstraint(
                    "CK_guest_anonymisation_restore_receipts_versions",
                    "\"OwnerReceiptContractVersion\" >= 1 AND " +
                    "\"ResultingGuestVersion\" >= 1 AND " +
                    $"\"TombstoneRevision\" BETWEEN 1 AND " +
                    $"{GuestAnonymisationTombstone.MaximumRevision}");
                table.HasCheckConstraint(
                    "CK_guest_anonymisation_restore_receipts_digests",
                    $"char_length(\"OwnerReceiptSha256\") = {GuestAnonymisationReceipt.Sha256Length} AND " +
                    "\"OwnerReceiptSha256\" ~ '^[0-9a-f]+$' AND " +
                    $"char_length(\"CanonicalSha256\") = {GuestAnonymisationReceipt.Sha256Length} AND " +
                    "\"CanonicalSha256\" ~ '^[0-9a-f]+$'");
                table.HasCheckConstraint(
                    "CK_guest_anonymisation_restore_receipts_timestamp",
                    "\"ReplayedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");
            });

        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.Id).ValueGeneratedNever();
        builder.Property(receipt => receipt.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(receipt => receipt.OwnerReceiptSha256)
            .HasMaxLength(GuestAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.CanonicalSha256)
            .HasMaxLength(GuestAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.GuestId
        })
            .HasDatabaseName(
                "UX_guest_anonymisation_restore_receipts_tombstone")
            .IsUnique();
        builder.HasOne<GuestAnonymisationTombstone>()
            .WithMany()
            .HasForeignKey(receipt => new
            {
                receipt.ScopeId,
                receipt.GuestId
            })
            .HasPrincipalKey(tombstone => new
            {
                tombstone.ScopeId,
                tombstone.Id
            })
            .HasConstraintName(
                "FK_guest_anonymisation_restore_receipts_tombstone")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

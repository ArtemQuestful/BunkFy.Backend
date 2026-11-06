namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestAnonymisationTombstoneConfiguration
    : IEntityTypeConfiguration<GuestAnonymisationTombstone>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<GuestAnonymisationTombstone> builder)
    {
        builder.ToTable("guest_anonymisation_tombstones", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_anonymisation_tombstones_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_tombstones_contract",
                $"\"ContractVersion\" = {GuestAnonymisationTombstone.CurrentContractVersion}");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_tombstones_revision",
                $"\"Revision\" BETWEEN 1 AND " +
                $"{GuestAnonymisationTombstone.MaximumRevision}");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_tombstones_state",
                $"\"State\" = {(int)GuestAnonymisationTombstoneState.Anonymised}");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_tombstones_authority",
                $"\"Authority\" IN ({(int)GuestAnonymisationAuthority.DataRights}, {(int)GuestAnonymisationAuthority.Retention})");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_tombstones_receipt_digest",
                $"char_length(\"OwnerReceiptSha256\") = {GuestAnonymisationReceipt.Sha256Length} AND " +
                "\"OwnerReceiptSha256\" ~ '^[0-9a-f]+$'");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_tombstones_restore_pair",
                "(\"LedgerEntryId\" IS NULL AND \"LastReplayedAtUtc\" IS NULL) OR " +
                $"(\"LedgerEntryId\" IS NOT NULL AND \"LedgerEntryId\" <> '{EmptyGuid}' AND " +
                "\"LastReplayedAtUtc\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_tombstones_timestamps",
                "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND " +
                "(\"LastReplayedAtUtc\" IS NULL OR " +
                "\"LastReplayedAtUtc\" >= \"CompletedAtUtc\")");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_tombstones_lifecycle",
                $"(\"Authority\" = {(int)GuestAnonymisationAuthority.DataRights} AND " +
                "(\"Revision\" = 1 OR " +
                "(\"Revision\" = 2 AND \"LedgerEntryId\" IS NOT NULL))) OR " +
                $"(\"Authority\" = {(int)GuestAnonymisationAuthority.Retention} AND " +
                "\"Revision\" = 1 AND \"LedgerEntryId\" IS NULL)");
        });
        builder.HasKey(tombstone => tombstone.Id);
        builder.HasAlternateKey(tombstone => new
        {
            tombstone.ScopeId,
            tombstone.Id
        });
        builder.Property(tombstone => tombstone.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(tombstone => tombstone.Revision)
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(tombstone => tombstone.State)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(tombstone => tombstone.Authority)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(tombstone => tombstone.OwnerReceiptSha256)
            .HasMaxLength(GuestAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.HasIndex(tombstone => new
        {
            tombstone.ScopeId,
            tombstone.CompletedAtUtc,
            tombstone.Id
        });
        builder.HasIndex(tombstone => new
        {
            tombstone.ScopeId,
            tombstone.LedgerEntryId
        })
            .HasDatabaseName(
                "UX_guest_anonymisation_tombstones_ledger_entry")
            .HasFilter("\"LedgerEntryId\" IS NOT NULL")
            .IsUnique();
        builder.HasOne<GuestProfile>()
            .WithOne()
            .HasForeignKey<GuestAnonymisationTombstone>(
                tombstone => new { tombstone.ScopeId, tombstone.Id })
            .HasPrincipalKey<GuestProfile>(
                profile => new { profile.ScopeId, profile.Id })
            .HasConstraintName("FK_guest_anonymisation_tombstones_guest_profile")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(tombstone => tombstone.DomainEvents);
    }
}

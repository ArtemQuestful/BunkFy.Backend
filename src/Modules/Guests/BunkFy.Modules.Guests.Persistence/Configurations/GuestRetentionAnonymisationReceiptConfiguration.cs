namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestRetentionAnonymisationReceiptConfiguration
    : IEntityTypeConfiguration<GuestRetentionAnonymisationReceipt>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(
        EntityTypeBuilder<GuestRetentionAnonymisationReceipt> builder)
    {
        builder.ToTable(
            "guest_retention_anonymisation_receipts",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_guest_retention_receipts_coordinates",
                    $"\"Id\" <> '{EmptyGuid}' AND " +
                    $"\"ExecutionId\" <> '{EmptyGuid}' AND " +
                    $"\"GuestId\" <> '{EmptyGuid}' AND " +
                    $"\"EventId\" <> '{EmptyGuid}' AND " +
                    "trim(\"ScopeId\") <> ''");
                table.HasCheckConstraint(
                    "CK_guest_retention_receipts_contract",
                    $"\"ContractVersion\" BETWEEN {GuestRetentionAnonymisationReceipt.MinimumSupportedContractVersion} AND {GuestRetentionAnonymisationReceipt.CurrentContractVersion} AND " +
                    "((\"ContractVersion\" = 1 AND \"TimeZoneCatalogVersion\" IS NULL) OR " +
                    "(\"ContractVersion\" = 2 AND char_length(\"TimeZoneCatalogVersion\") > 0 AND " +
                    "\"TimeZoneCatalogVersion\" = btrim(\"TimeZoneCatalogVersion\") AND " +
                    "\"TimeZoneCatalogVersion\" !~ '[[:cntrl:]]'))");
                table.HasCheckConstraint(
                    "CK_guest_retention_receipts_actor",
                    $"\"ActorId\" = " +
                    $"'{GuestRetentionAnonymisationReceipt.SystemActorId}'");
                table.HasCheckConstraint(
                    "CK_guest_retention_receipts_versions",
                    "\"SelectedGuestVersion\" >= 1 AND " +
                    "\"ResultingGuestVersion\" = \"SelectedGuestVersion\" + 1");
                table.HasCheckConstraint(
                    "CK_guest_retention_receipts_properties",
                    $"\"AffectedPropertyCount\" BETWEEN 1 AND {GuestRetentionAnonymisationReceipt.MaximumAffectedProperties}");
                table.HasCheckConstraint(
                    "CK_guest_retention_receipts_digests",
                    $"char_length(\"PolicySetSha256\") = {GuestRetentionAnonymisationReceipt.Sha256Length} AND " +
                    "\"PolicySetSha256\" ~ '^[0-9a-f]+$' AND " +
                    $"char_length(\"CanonicalSha256\") = {GuestRetentionAnonymisationReceipt.Sha256Length} AND " +
                    "\"CanonicalSha256\" ~ '^[0-9a-f]+$'");
                table.HasCheckConstraint(
                    "CK_guest_retention_receipts_timestamps",
                    "\"RetentionDeadlineUtc\" > " +
                    "TIMESTAMPTZ '0001-01-01 00:00:00+00' AND " +
                    "\"CompletedAtUtc\" >= \"RetentionDeadlineUtc\"");
            });
        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(receipt => receipt.PolicySetSha256)
            .HasMaxLength(GuestRetentionAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.TimeZoneCatalogVersion)
            .HasMaxLength(
                GuestRetentionAnonymisationReceipt
                    .TimeZoneCatalogVersionMaxLength);
        builder.Property(receipt => receipt.CanonicalSha256)
            .HasMaxLength(GuestRetentionAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.ActorId)
            .HasMaxLength(GuestProfile.ActorIdMaxLength)
            .IsRequired();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.GuestId
        })
            .HasDatabaseName("UX_guest_retention_receipts_guest")
            .IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.ExecutionId
        }).HasDatabaseName("IX_guest_retention_receipts_execution");
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.EventId
        })
            .HasDatabaseName("UX_guest_retention_receipts_event")
            .IsUnique();
        builder.HasOne<GuestRetentionExecution>()
            .WithMany()
            .HasForeignKey(receipt => new
            {
                receipt.ScopeId,
                receipt.ExecutionId
            })
            .HasPrincipalKey(execution => new
            {
                execution.ScopeId,
                execution.Id
            })
            .HasConstraintName("FK_guest_retention_receipts_execution")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GuestProfile>()
            .WithOne()
            .HasForeignKey<GuestRetentionAnonymisationReceipt>(
                receipt => new
                {
                    receipt.ScopeId,
                    receipt.GuestId
                })
            .HasPrincipalKey<GuestProfile>(
                profile => new
                {
                    profile.ScopeId,
                    profile.Id
                })
            .HasConstraintName("FK_guest_retention_receipts_guest_profile")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GuestAnonymisationTombstone>()
            .WithOne()
            .HasForeignKey<GuestRetentionAnonymisationReceipt>(
                receipt => new
                {
                    receipt.ScopeId,
                    receipt.GuestId
                })
            .HasPrincipalKey<GuestAnonymisationTombstone>(
                tombstone => new
                {
                    tombstone.ScopeId,
                    tombstone.Id
                })
            .HasConstraintName("FK_guest_retention_receipts_tombstone")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(receipt => receipt.DomainEvents);
    }
}

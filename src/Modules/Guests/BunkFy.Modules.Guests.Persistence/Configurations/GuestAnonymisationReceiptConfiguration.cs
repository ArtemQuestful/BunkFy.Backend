namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainDisposition = BunkFy.Modules.Guests.Domain.Models.GuestAnonymisationDisposition;
using DomainReason = BunkFy.Modules.Guests.Domain.Models.GuestAnonymisationReason;

internal sealed class GuestAnonymisationReceiptConfiguration
    : IEntityTypeConfiguration<GuestAnonymisationReceipt>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<GuestAnonymisationReceipt> builder)
    {
        builder.ToTable("guest_anonymisation_receipts", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_anonymisation_receipts_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"IdempotencyKey\" <> '{EmptyGuid}' AND " +
                $"\"RoutingPropertyId\" <> '{EmptyGuid}' AND \"CaseId\" <> '{EmptyGuid}' AND " +
                $"\"GuestId\" <> '{EmptyGuid}' AND \"EventId\" <> '{EmptyGuid}' AND " +
                "trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_receipts_contract",
                $"\"ContractVersion\" = {GuestAnonymisationReceipt.CurrentContractVersion}");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_receipts_revisions",
                "\"ApprovalRevision\" >= 1 AND \"OperationRevision\" > \"ApprovalRevision\"");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_receipts_versions",
                "\"SelectedGuestVersion\" >= 1 AND " +
                "\"ResultingGuestVersion\" = \"SelectedGuestVersion\" + 1");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_receipts_outcome",
                $"\"Disposition\" = {(int)DomainDisposition.Completed} AND " +
                $"\"Reason\" = {(int)DomainReason.ProfileAnonymised}");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_receipts_property_count",
                $"\"AffectedPropertyCount\" BETWEEN 1 AND " +
                $"{GuestAnonymisationEligibilityContract.MaximumAffectedProperties}");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_receipts_digests",
                $"char_length(\"ApprovalEvidenceSha256\") = {GuestAnonymisationReceipt.Sha256Length} AND " +
                "\"ApprovalEvidenceSha256\" ~ '^[0-9a-f]+$' AND " +
                $"char_length(\"PolicySetSha256\") = {GuestAnonymisationReceipt.Sha256Length} AND " +
                "\"PolicySetSha256\" ~ '^[0-9a-f]+$' AND " +
                $"char_length(\"CanonicalSha256\") = {GuestAnonymisationReceipt.Sha256Length} AND " +
                "\"CanonicalSha256\" ~ '^[0-9a-f]+$'");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_receipts_actor",
                "length(\"ActorId\") > 0 AND \"ActorId\" = trim(\"ActorId\")");
            table.HasCheckConstraint(
                "CK_guest_anonymisation_receipts_timestamp",
                "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");
        });
        builder.HasKey(receipt => receipt.Id);
        builder.HasAlternateKey(receipt => new
        {
            receipt.ScopeId,
            receipt.CanonicalSha256
        });
        builder.Property(receipt => receipt.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(receipt => receipt.Disposition).HasConversion<int>().IsRequired();
        builder.Property(receipt => receipt.Reason).HasConversion<int>().IsRequired();
        builder.Property(receipt => receipt.ApprovalEvidenceSha256)
            .HasMaxLength(GuestAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.PolicySetSha256)
            .HasMaxLength(GuestAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.CanonicalSha256)
            .HasMaxLength(GuestAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.ActorId)
            .HasMaxLength(GuestProfile.ActorIdMaxLength)
            .IsRequired();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.IdempotencyKey
        })
            .HasDatabaseName("UX_guest_anonymisation_receipts_idempotency")
            .IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.CaseId,
            receipt.ApprovalRevision,
            receipt.OperationRevision,
            receipt.GuestId
        })
            .HasDatabaseName("UX_guest_anonymisation_receipts_case_operation")
            .IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.GuestId,
            receipt.ResultingGuestVersion
        })
            .HasDatabaseName("UX_guest_anonymisation_receipts_guest_version")
            .IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.EventId
        })
            .HasDatabaseName("UX_guest_anonymisation_receipts_event")
            .IsUnique();
        builder.HasOne<GuestProfile>()
            .WithMany()
            .HasForeignKey(receipt => new { receipt.ScopeId, receipt.GuestId })
            .HasPrincipalKey(profile => new { profile.ScopeId, profile.Id })
            .HasConstraintName("FK_guest_anonymisation_receipts_guest_profile")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(receipt => receipt.DomainEvents);
    }
}

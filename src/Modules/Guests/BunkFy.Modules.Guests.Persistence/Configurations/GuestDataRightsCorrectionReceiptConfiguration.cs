namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestDataRightsCorrectionReceiptConfiguration
    : IEntityTypeConfiguration<GuestDataRightsCorrectionReceipt>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<GuestDataRightsCorrectionReceipt> builder)
    {
        builder.ToTable("guest_data_rights_correction_receipts", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_data_rights_correction_receipts_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"IdempotencyKey\" <> '{EmptyGuid}' AND " +
                $"\"PropertyId\" <> '{EmptyGuid}' AND \"CaseId\" <> '{EmptyGuid}' AND " +
                $"\"GuestId\" <> '{EmptyGuid}' AND \"EventId\" <> '{EmptyGuid}' AND " +
                $"\"CompletionEventId\" <> '{EmptyGuid}' AND " +
                "\"EventId\" <> \"CompletionEventId\" AND trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_guest_data_rights_correction_receipts_timestamp",
                "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");
            table.HasCheckConstraint(
                "CK_guest_data_rights_correction_receipts_contract",
                "\"ContractVersion\" IN (1, 2)");
            table.HasCheckConstraint(
                "CK_guest_data_rights_correction_receipts_approval_revision",
                "\"ApprovalRevision\" >= 1");
            table.HasCheckConstraint(
                "CK_guest_data_rights_correction_receipts_versions",
                "\"SelectedRecordVersion\" >= 1 AND " +
                "\"CurrentRecordVersion\" = \"SelectedRecordVersion\" + 1");
            table.HasCheckConstraint(
                "CK_guest_data_rights_correction_receipts_changed_fields",
                $"(\"ContractVersion\" = 1 AND \"ChangedFieldsMask\" BETWEEN 1 AND {GuestDataRightsCorrectionReceipt.LegacyChangedFieldsMask}) OR " +
                $"(\"ContractVersion\" = 2 AND \"ChangedFieldsMask\" BETWEEN 1 AND {GuestDataRightsCorrectionReceipt.AllChangedFieldsMask})");
        });
        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(receipt => receipt.ContractVersion).IsRequired();
        builder.HasIndex(receipt => new { receipt.ScopeId, receipt.IdempotencyKey }).IsUnique();
        builder.HasIndex(receipt => new { receipt.ScopeId, receipt.EventId }).IsUnique();
        builder.HasIndex(receipt => new { receipt.ScopeId, receipt.CompletionEventId }).IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.PropertyId,
            receipt.CaseId,
            receipt.ApprovalRevision
        })
            .HasDatabaseName("UX_guest_correction_receipts_case_approval")
            .IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.GuestId,
            receipt.CurrentRecordVersion
        })
            .HasDatabaseName("UX_guest_correction_receipts_guest_version")
            .IsUnique();
        builder.HasOne<GuestProfile>()
            .WithMany()
            .HasForeignKey(receipt => new
            {
                receipt.ScopeId,
                receipt.GuestId
            })
            .HasPrincipalKey(profile => new
            {
                profile.ScopeId,
                profile.Id
            })
            .HasConstraintName(
                "FK_guest_data_rights_correction_receipts_guest_profile")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(receipt => receipt.ChangedFields);
        builder.Ignore(receipt => receipt.DomainEvents);
    }
}

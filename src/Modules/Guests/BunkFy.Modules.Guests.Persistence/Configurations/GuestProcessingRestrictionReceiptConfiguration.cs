namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestProcessingRestrictionReceiptConfiguration
    : IEntityTypeConfiguration<GuestProcessingRestrictionReceipt>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<GuestProcessingRestrictionReceipt> builder)
    {
        builder.ToTable("guest_processing_restriction_receipts", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_processing_restriction_receipts_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"IdempotencyKey\" <> '{EmptyGuid}' AND " +
                $"\"RestrictionId\" <> '{EmptyGuid}' AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                $"\"GuestId\" <> '{EmptyGuid}' AND \"CaseId\" <> '{EmptyGuid}' AND " +
                $"\"EventId\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_guest_processing_restriction_receipts_audit_text",
                "length(trim(\"ActorId\")) > 0 AND \"ActorId\" = trim(\"ActorId\")");
            table.HasCheckConstraint(
                "CK_guest_processing_restriction_receipts_timestamp",
                "\"CompletedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00'");
            table.HasCheckConstraint(
                "CK_guest_processing_restriction_receipts_versions",
                "\"ApprovalRevision\" >= 1 AND \"SelectedGuestVersion\" >= 1 AND " +
                "\"ResultingProjectionRevision\" >= 1 AND " +
                "((\"Action\" = 1 AND \"ResultingRestrictionVersion\" = 1 AND " +
                "\"EffectiveRestricted\") OR " +
                "(\"Action\" = 2 AND \"ResultingRestrictionVersion\" = 2))");
        });
        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(receipt => receipt.Action).HasConversion<int>().IsRequired();
        builder.Property(receipt => receipt.ActorId)
            .HasMaxLength(GuestProfile.ActorIdMaxLength)
            .IsRequired();
        builder.HasIndex(receipt => new { receipt.ScopeId, receipt.IdempotencyKey }).IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.PropertyId,
            receipt.GuestId,
            receipt.CompletedAtUtc
        });
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.PropertyId,
            receipt.CaseId,
            receipt.ApprovalRevision
        });
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.RestrictionId,
            receipt.PropertyId,
            receipt.GuestId
        })
            .HasDatabaseName(
                "IX_guest_processing_restriction_receipts_restriction_evidence");
        builder.HasOne<GuestProcessingRestriction>()
            .WithMany()
            .HasPrincipalKey(restriction => new
            {
                restriction.ScopeId,
                restriction.Id,
                restriction.PropertyId,
                restriction.GuestId
            })
            .HasForeignKey(receipt => new
            {
                receipt.ScopeId,
                receipt.RestrictionId,
                receipt.PropertyId,
                receipt.GuestId
            })
            .HasConstraintName(
                "FK_guest_processing_restriction_receipts_restriction_evidence")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(receipt => receipt.DomainEvents);
    }
}

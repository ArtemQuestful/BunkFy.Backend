namespace BunkFy.Modules.Guests.Persistence.Configurations;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class GuestProcessingRestrictionConfiguration
    : IEntityTypeConfiguration<GuestProcessingRestriction>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<GuestProcessingRestriction> builder)
    {
        builder.ToTable("guest_processing_restrictions", table =>
        {
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                $"\"GuestId\" <> '{EmptyGuid}' AND \"ApplyCaseId\" <> '{EmptyGuid}' AND " +
                $"(\"ReleaseCaseId\" IS NULL OR \"ReleaseCaseId\" <> '{EmptyGuid}') AND " +
                "trim(\"ScopeId\") <> ''");
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_audit_text",
                "length(trim(\"AppliedBy\")) > 0 AND \"AppliedBy\" = trim(\"AppliedBy\") AND " +
                "(\"ReleasedBy\" IS NULL OR (length(trim(\"ReleasedBy\")) > 0 AND " +
                "\"ReleasedBy\" = trim(\"ReleasedBy\")))");
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_timestamps",
                "\"AppliedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND " +
                "(\"ReleasedAtUtc\" IS NULL OR " +
                "\"ReleasedAtUtc\" > TIMESTAMPTZ '0001-01-01 00:00:00+00')");
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_apply_approval",
                "\"ApplyApprovalRevision\" >= 1 AND \"ApplySelectedGuestVersion\" >= 1");
            table.HasCheckConstraint(
                "CK_guest_processing_restrictions_lifecycle",
                "(\"Status\" = 1 AND \"ReleaseCaseId\" IS NULL AND " +
                "\"ReleaseApprovalRevision\" IS NULL AND " +
                "\"ReleaseSelectedGuestVersion\" IS NULL AND " +
                "\"ReleasedBy\" IS NULL AND \"ReleasedAtUtc\" IS NULL AND \"Version\" = 1) OR " +
                "(\"Status\" = 2 AND \"ReleaseCaseId\" IS NOT NULL AND " +
                "\"ReleaseCaseId\" <> \"ApplyCaseId\" AND " +
                "\"ReleaseApprovalRevision\" >= 1 AND " +
                "\"ReleaseSelectedGuestVersion\" >= 1 AND " +
                "\"ReleasedBy\" IS NOT NULL AND \"ReleasedAtUtc\" IS NOT NULL AND " +
                "\"ReleasedAtUtc\" >= \"AppliedAtUtc\" AND \"Version\" = 2)");
        });
        builder.HasKey(restriction => restriction.Id);
        builder.HasAlternateKey(restriction => new
        {
            restriction.ScopeId,
            restriction.Id,
            restriction.PropertyId,
            restriction.GuestId
        })
            .HasName("AK_guest_processing_restrictions_receipt_evidence");
        builder.Property(restriction => restriction.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(restriction => restriction.Status).HasConversion<int>().IsRequired();
        builder.Property(restriction => restriction.Version).IsConcurrencyToken();
        builder.Property(restriction => restriction.AppliedBy)
            .HasMaxLength(GuestProfile.ActorIdMaxLength)
            .IsRequired();
        builder.Property(restriction => restriction.ReleasedBy)
            .HasMaxLength(GuestProfile.ActorIdMaxLength);
        builder.HasIndex(restriction => new
        {
            restriction.ScopeId,
            restriction.PropertyId,
            restriction.GuestId,
            restriction.ApplyCaseId,
            restriction.ApplyApprovalRevision
        }).IsUnique();
        builder.HasIndex(restriction => new
        {
            restriction.ScopeId,
            restriction.PropertyId,
            restriction.GuestId,
            restriction.ReleaseCaseId,
            restriction.ReleaseApprovalRevision
        }).IsUnique();
        builder.HasIndex(restriction => new
        {
            restriction.ScopeId,
            restriction.PropertyId,
            restriction.GuestId,
            restriction.Status,
            restriction.AppliedAtUtc
        });
        builder.HasOne<GuestProcessingRestrictionProjection>()
            .WithMany()
            .HasPrincipalKey(projection => new
            {
                projection.ScopeId,
                projection.PropertyId,
                projection.GuestId
            })
            .HasForeignKey(restriction => new
            {
                restriction.ScopeId,
                restriction.PropertyId,
                restriction.GuestId
            })
            .HasConstraintName(
                "FK_guest_processing_restrictions_effective_projection")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(restriction => restriction.DomainEvents);
    }
}

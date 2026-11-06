namespace BunkFy.Modules.Staff.Persistence.Configurations;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class StaffIdentityProvisioningAnchorConfiguration
    : IEntityTypeConfiguration<StaffIdentityProvisioningAnchor>
{
    public void Configure(
        EntityTypeBuilder<StaffIdentityProvisioningAnchor> builder)
    {
        builder.ToTable("identity_provisioning_anchors", table =>
        {
            table.HasCheckConstraint(
                "CK_staff_identity_provisioning_anchors_source_kind",
                $"\"SourceKind\" IN (" +
                $"{(int)StaffIdentityProvisioningSourceKind.WorkspaceOnboarding}, " +
                $"{(int)StaffIdentityProvisioningSourceKind.OrganizationMembership})");
            table.HasCheckConstraint(
                "CK_staff_identity_provisioning_anchors_ids",
                "\"SourceId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND " +
                "\"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint(
                "CK_staff_identity_provisioning_anchors_resolution_event",
                $"(\"SourceKind\" = " +
                $"{(int)StaffIdentityProvisioningSourceKind.WorkspaceOnboarding} AND " +
                "\"ResolutionEventId\" IS NOT NULL AND " +
                "\"ResolutionEventId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND " +
                "\"ResolutionEventId\" <> \"SourceId\") OR " +
                $"(\"SourceKind\" = " +
                $"{(int)StaffIdentityProvisioningSourceKind.OrganizationMembership} AND " +
                "\"ResolutionEventId\" IS NULL)");
        });
        builder.HasKey(anchor => new
        {
            anchor.ScopeId,
            anchor.SourceKind,
            anchor.SourceId
        });
        builder.HasAlternateKey(anchor => new
        {
            anchor.ScopeId,
            anchor.SourceKind,
            anchor.SourceId,
            anchor.StaffMemberId
        });
        builder.Property(anchor => anchor.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(anchor => anchor.SourceKind)
            .HasConversion<int>()
            .IsRequired();
        builder.HasIndex(anchor => anchor.ResolutionEventId)
            .IsUnique()
            .HasFilter("\"ResolutionEventId\" IS NOT NULL");
        builder.HasIndex(anchor => new
        {
            anchor.ScopeId,
            anchor.SourceKind,
            anchor.SourceId,
            anchor.StaffMemberId,
            anchor.ResolutionEventId
        }).IsUnique()
            .HasFilter("\"ResolutionEventId\" IS NOT NULL");
        builder.HasOne<StaffMember>()
            .WithMany()
            .HasForeignKey(anchor => new
            {
                anchor.ScopeId,
                anchor.StaffMemberId
            })
            .HasPrincipalKey(member => new
            {
                member.ScopeId,
                member.Id
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(anchor => new
        {
            anchor.ScopeId,
            anchor.StaffMemberId,
            anchor.SourceKind,
            anchor.SourceId
        });
    }
}

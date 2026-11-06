namespace BunkFy.Modules.Staff.Persistence.Configurations;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class StaffIdentityProvisioningAnchorResolutionConfiguration
    : IEntityTypeConfiguration<StaffIdentityProvisioningAnchorResolution>
{
    public void Configure(
        EntityTypeBuilder<StaffIdentityProvisioningAnchorResolution> builder)
    {
        builder.ToTable("identity_provisioning_anchor_resolutions", table =>
        {
            table.HasTrigger(
                "TR_staff_identity_provisioning_anchor_resolution_integrity");
            table.HasCheckConstraint(
                "CK_staff_identity_provisioning_anchor_resolutions_source",
                $"\"SourceKind\" = " +
                $"{(int)StaffIdentityProvisioningSourceKind.WorkspaceOnboarding}");
            table.HasCheckConstraint(
                "CK_staff_identity_provisioning_anchor_resolutions_ids",
                "\"SourceId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND " +
                "\"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND " +
                "\"ResolutionEventId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND " +
                "\"ResolutionEventId\" <> \"SourceId\"");
            table.HasCheckConstraint(
                "CK_staff_identity_provisioning_anchor_resolutions_evidence",
                $"\"WorkspaceApplicationVersion\" >= 1 AND " +
                $"\"Disposition\" BETWEEN " +
                $"{(int)StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition.CompletedRedacted} AND " +
                $"{(int)StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition.WithdrawnRedacted}");
        });
        builder.HasKey(resolution => new
        {
            resolution.ScopeId,
            resolution.SourceKind,
            resolution.SourceId
        });
        builder.Property(resolution => resolution.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(resolution => resolution.SourceKind)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(resolution => resolution.Disposition)
            .HasConversion<int>()
            .IsRequired();
        builder.HasOne<StaffIdentityProvisioningAnchor>()
            .WithMany()
            .HasForeignKey(resolution => new
            {
                resolution.ScopeId,
                resolution.SourceKind,
                resolution.SourceId,
                resolution.StaffMemberId
            })
            .HasPrincipalKey(anchor => new
            {
                anchor.ScopeId,
                anchor.SourceKind,
                anchor.SourceId,
                anchor.StaffMemberId
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(resolution => new
        {
            resolution.ScopeId,
            resolution.StaffMemberId,
            resolution.SourceId
        });
        builder.HasIndex(resolution => resolution.ResolutionEventId)
            .IsUnique();
    }
}

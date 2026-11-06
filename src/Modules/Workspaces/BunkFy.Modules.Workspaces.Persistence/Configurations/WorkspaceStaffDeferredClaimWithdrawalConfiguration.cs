namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class WorkspaceStaffDeferredClaimWithdrawalConfiguration
    : IEntityTypeConfiguration<WorkspaceStaffDeferredClaimWithdrawal>
{
    public void Configure(
        EntityTypeBuilder<WorkspaceStaffDeferredClaimWithdrawal> builder)
    {
        builder.ToTable(
            "staff_deferred_claim_withdrawals",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_staff_deferred_claim_withdrawal_version",
                    "\"ClaimVersion\" > 0");
                table.HasCheckConstraint(
                    "CK_staff_deferred_claim_withdrawal_coordinates",
                    "\"ClaimId\" <> '00000000-0000-0000-0000-000000000000'::uuid " +
                    "AND \"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid " +
                    "AND \"EnrollmentLinkId\" <> '00000000-0000-0000-0000-000000000000'::uuid " +
                    "AND \"EventId\" <> '00000000-0000-0000-0000-000000000000'::uuid " +
                    "AND \"ScopeId\" = \"OrganizationId\"::text");
            });
        builder.HasKey(withdrawal => withdrawal.Id);
        builder.Property(withdrawal => withdrawal.Id)
            .HasColumnName("ClaimId");
        builder.Property(withdrawal => withdrawal.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.HasIndex(withdrawal => new
        {
            withdrawal.ScopeId,
            withdrawal.EnrollmentLinkId,
            withdrawal.ClaimVersion
        });
        builder.HasIndex(withdrawal => new
        {
            withdrawal.ScopeId,
            withdrawal.Id
        });
    }
}

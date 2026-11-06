namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class WorkspaceStaffOnboardingRecoveryReceiptConfiguration
    : IEntityTypeConfiguration<WorkspaceStaffOnboardingRecoveryReceipt>
{
    public void Configure(
        EntityTypeBuilder<WorkspaceStaffOnboardingRecoveryReceipt> builder)
    {
        builder.ToTable("staff_onboarding_recovery_receipts", table =>
        {
            table.HasCheckConstraint(
                "CK_workspaces_staff_onboarding_recovery_receipts_contract",
                $"\"ContractVersion\" = " +
                $"{WorkspaceStaffOnboardingRecoveryReceipt.CurrentContractVersion}");
            table.HasCheckConstraint(
                "CK_workspaces_staff_onboarding_recovery_receipts_versions",
                "\"ExpectedApplicationVersion\" >= 1 AND " +
                "\"SelectedApplicationVersion\" >= 1 AND " +
                "\"ExpectedApplicationVersion\" = " +
                "\"SelectedApplicationVersion\" AND " +
                "\"SelectedApplicationStatus\" = 6 AND " +
                "((\"Outcome\" = 1 AND \"ResultingApplicationVersion\" = " +
                "\"SelectedApplicationVersion\" + 1 AND " +
                "\"ResultingApplicationStatus\" = 5) OR " +
                "(\"Outcome\" IN (2, 3) AND \"ResultingApplicationVersion\" = " +
                "\"SelectedApplicationVersion\" AND " +
                "\"ResultingApplicationStatus\" = 6))");
            table.HasCheckConstraint(
                "CK_workspaces_staff_onboarding_recovery_receipts_outcome",
                "(\"Outcome\" = 1 AND \"Reason\" = 1) OR " +
                "(\"Outcome\" = 2 AND \"Reason\" IN (2, 3, 5, 7, 9)) OR " +
                "(\"Outcome\" = 3 AND \"Reason\" IN (4, 6, 8))");
            table.HasCheckConstraint(
                "CK_workspaces_staff_onboarding_recovery_receipts_verification",
                "\"OrganizationVerification\" BETWEEN 0 AND 3 AND " +
                "\"StaffVerification\" BETWEEN 0 AND 3 AND " +
                "\"AccessVerification\" BETWEEN 0 AND 3 AND " +
                "((\"Reason\" = 1 AND \"OrganizationVerification\" = 1 " +
                "AND \"StaffVerification\" = 1 AND " +
                "\"AccessVerification\" = 1) OR " +
                "(\"Reason\" IN (2, 9) AND " +
                "\"OrganizationVerification\" = 0 AND " +
                "\"StaffVerification\" = 0 AND " +
                "\"AccessVerification\" = 0) OR " +
                "(\"Reason\" = 3 AND \"OrganizationVerification\" = 2 " +
                "AND \"StaffVerification\" IN (0, 1) AND " +
                "\"AccessVerification\" IN (0, 1)) OR " +
                "(\"Reason\" = 4 AND \"OrganizationVerification\" = 3 " +
                "AND \"StaffVerification\" IN (0, 1) AND " +
                "\"AccessVerification\" IN (0, 1)) OR " +
                "(\"Reason\" = 5 AND \"OrganizationVerification\" = 1 " +
                "AND \"StaffVerification\" = 2 AND " +
                "\"AccessVerification\" IN (0, 1)) OR " +
                "(\"Reason\" = 6 AND \"OrganizationVerification\" = 1 " +
                "AND \"StaffVerification\" = 3 AND " +
                "\"AccessVerification\" IN (0, 1)) OR " +
                "(\"Reason\" = 7 AND \"OrganizationVerification\" = 1 " +
                "AND \"StaffVerification\" = 1 AND " +
                "\"AccessVerification\" = 2) OR " +
                "(\"Reason\" = 8 AND \"OrganizationVerification\" = 1 " +
                "AND \"StaffVerification\" = 1 AND " +
                "\"AccessVerification\" = 3))");
            table.HasCheckConstraint(
                "CK_workspaces_staff_onboarding_recovery_receipts_presence",
                "((\"OrganizationVerification\" = 0 AND " +
                "\"OrganizationObservedAtUtc\" IS NULL AND " +
                "\"OrganizationRevision\" IS NULL AND " +
                "\"MembershipId\" IS NULL AND " +
                "\"MembershipVersion\" IS NULL AND " +
                "\"MembershipStatus\" IS NULL) OR " +
                "(\"OrganizationVerification\" <> 0 AND " +
                "\"OrganizationObservedAtUtc\" IS NOT NULL AND " +
                "(\"OrganizationRevision\" IS NULL OR " +
                "\"OrganizationRevision\" >= 0) AND " +
                "((\"MembershipId\" IS NULL AND " +
                "\"MembershipVersion\" IS NULL AND " +
                "\"MembershipStatus\" IS NULL) OR " +
                "(\"MembershipId\" IS NOT NULL AND " +
                "\"MembershipVersion\" > 0 AND " +
                "\"MembershipStatus\" IS NOT NULL)) AND " +
                "(\"OrganizationVerification\" <> 1 OR " +
                "(\"OrganizationRevision\" IS NOT NULL AND " +
                "\"MembershipId\" IS NOT NULL AND " +
                "\"MembershipVersion\" > 0 AND " +
                "\"MembershipStatus\" = 1)))) AND " +
                "((\"StaffVerification\" = 0 AND " +
                "\"StaffObservedAtUtc\" IS NULL AND " +
                "\"StaffTenantRevision\" IS NULL AND " +
                "\"StaffMemberVersion\" IS NULL AND " +
                "\"StaffStateProofSha256\" IS NULL AND " +
                "\"StaffPropertySetSha256\" IS NULL) OR " +
                "(\"StaffVerification\" <> 0 AND " +
                "\"StaffObservedAtUtc\" IS NOT NULL AND " +
                "(\"StaffTenantRevision\" IS NULL OR " +
                "\"StaffTenantRevision\" >= 0) AND " +
                "(\"StaffMemberVersion\" IS NULL OR " +
                "\"StaffMemberVersion\" >= 1) AND " +
                "(\"StaffVerification\" <> 1 OR " +
                "(\"StaffTenantRevision\" IS NOT NULL AND " +
                "\"StaffMemberVersion\" IS NOT NULL AND " +
                "\"StaffStateProofSha256\" IS NOT NULL AND " +
                "\"StaffPropertySetSha256\" IS NOT NULL)))) AND " +
                "((\"AccessVerification\" = 0 AND " +
                "\"AccessObservedAtUtc\" IS NULL AND " +
                "\"AccessManagementRevision\" IS NULL AND " +
                "\"AccessFactsSha256\" IS NULL) OR " +
                "(\"AccessVerification\" <> 0 AND " +
                "\"AccessObservedAtUtc\" IS NOT NULL AND " +
                "(\"AccessManagementRevision\" IS NULL OR " +
                "\"AccessManagementRevision\" >= 0) AND " +
                "(\"AccessVerification\" <> 1 OR " +
                "(\"AccessManagementRevision\" IS NOT NULL AND " +
                "\"AccessFactsSha256\" IS NOT NULL)))) AND " +
                "(\"StaffVerification\" = 0 OR " +
                "\"OrganizationVerification\" <> 0) AND " +
                "(\"AccessVerification\" = 0 OR " +
                "\"StaffVerification\" <> 0)");
            table.HasCheckConstraint(
                "CK_workspaces_staff_onboarding_recovery_receipts_chronology",
                "\"RecordedAtUtc\" > " +
                "TIMESTAMPTZ '0001-01-01 00:00:00+00' AND " +
                "(\"OrganizationObservedAtUtc\" IS NULL OR " +
                "\"OrganizationObservedAtUtc\" <= \"RecordedAtUtc\") AND " +
                "(\"StaffObservedAtUtc\" IS NULL OR " +
                "\"StaffObservedAtUtc\" <= \"RecordedAtUtc\") AND " +
                "(\"AccessObservedAtUtc\" IS NULL OR " +
                "\"AccessObservedAtUtc\" <= \"RecordedAtUtc\")");
            table.HasCheckConstraint(
                "CK_workspaces_staff_onboarding_recovery_receipts_no_mutation",
                "\"AccessMutationPerformed\" = FALSE");
            table.HasCheckConstraint(
                "CK_workspaces_staff_onboarding_recovery_receipts_digests",
                "char_length(\"RequestSha256\") = 64 AND " +
                "char_length(\"TargetSetSha256\") = 64 AND " +
                "(\"StaffStateProofSha256\" IS NULL OR " +
                "char_length(\"StaffStateProofSha256\") = 64) AND " +
                "(\"StaffPropertySetSha256\" IS NULL OR " +
                "char_length(\"StaffPropertySetSha256\") = 64) AND " +
                "(\"AccessFactsSha256\" IS NULL OR " +
                "char_length(\"AccessFactsSha256\") = 64)");
        });
        builder.HasKey(receipt => receipt.Id);
        builder.HasAlternateKey(receipt => new { receipt.ScopeId, receipt.Id });
        builder.Property(receipt => receipt.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(receipt => receipt.ApplicantSubjectId)
            .HasMaxLength(WorkspaceStaffOnboardingRecoveryReceipt.ActorIdMaxLength)
            .IsRequired();
        builder.Property(receipt => receipt.PlanCreatorActorId)
            .HasMaxLength(WorkspaceStaffOnboardingRecoveryReceipt.ActorIdMaxLength)
            .IsRequired();
        builder.Property(receipt => receipt.RecoveryActorId)
            .HasMaxLength(WorkspaceStaffOnboardingRecoveryReceipt.ActorIdMaxLength)
            .IsRequired();
        builder.Property(receipt => receipt.EffectiveSystemActorId)
            .HasMaxLength(WorkspaceStaffOnboardingRecoveryReceipt.ActorIdMaxLength)
            .IsRequired();
        ConfigureDigest(builder, receipt => receipt.RequestSha256, required: true);
        ConfigureDigest(builder, receipt => receipt.TargetSetSha256, required: true);
        ConfigureDigest(builder, receipt => receipt.StaffStateProofSha256, required: false);
        ConfigureDigest(builder, receipt => receipt.StaffPropertySetSha256, required: false);
        ConfigureDigest(builder, receipt => receipt.AccessFactsSha256, required: false);
        builder.Property(receipt => receipt.TriggerKind).HasConversion<int>();
        builder.Property(receipt => receipt.SelectedApplicationStatus).HasConversion<int>();
        builder.Property(receipt => receipt.ResultingApplicationStatus).HasConversion<int>();
        builder.Property(receipt => receipt.OrganizationVerification).HasConversion<int>();
        builder.Property(receipt => receipt.StaffVerification).HasConversion<int>();
        builder.Property(receipt => receipt.AccessVerification).HasConversion<int>();
        builder.Property(receipt => receipt.Outcome).HasConversion<int>();
        builder.Property(receipt => receipt.Reason).HasConversion<int>();
        builder.HasIndex(receipt => new { receipt.ScopeId, receipt.OperationId })
            .IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.ApplicationId,
            receipt.RecordedAtUtc,
            receipt.Id
        });
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.RecoveryActorId,
            receipt.RecordedAtUtc,
            receipt.Id
        });
        builder.HasOne<WorkspaceStaffOnboarding>()
            .WithMany()
            .HasForeignKey(receipt => new
            {
                receipt.ScopeId,
                receipt.ApplicationId
            })
            .HasPrincipalKey(application => new
            {
                application.ScopeId,
                application.Id
            })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Ignore(receipt => receipt.DomainEvents);
    }

    private static void ConfigureDigest(
        EntityTypeBuilder<WorkspaceStaffOnboardingRecoveryReceipt> builder,
        System.Linq.Expressions.Expression<Func<
            WorkspaceStaffOnboardingRecoveryReceipt,
            string?>> property,
        bool required)
    {
        PropertyBuilder<string?> configured = builder.Property(property)
            .HasMaxLength(WorkspaceStaffOnboardingRecoveryReceipt.DigestLength)
            .IsFixedLength();
        if (required)
        {
            configured.IsRequired();
        }
    }
}

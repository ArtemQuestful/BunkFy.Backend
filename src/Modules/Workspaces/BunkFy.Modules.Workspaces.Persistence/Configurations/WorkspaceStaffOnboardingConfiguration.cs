namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class WorkspaceStaffOnboardingConfiguration
    : IEntityTypeConfiguration<WorkspaceStaffOnboarding>
{
    public void Configure(EntityTypeBuilder<WorkspaceStaffOnboarding> builder)
    {
        builder.ToTable("staff_onboarding_applications", table =>
        {
            table.HasCheckConstraint("CK_staff_onboarding_version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_staff_onboarding_source", "\"SourceKind\" IN (1, 2)");
            table.HasCheckConstraint("CK_staff_onboarding_status", "\"Status\" BETWEEN 1 AND 10");
            table.HasCheckConstraint("CK_staff_onboarding_claim",
                "(\"ClaimId\" IS NULL AND \"ClaimVersion\" IS NULL) OR " +
                "(\"ClaimId\" IS NOT NULL AND \"ClaimVersion\" > 0)");
            table.HasCheckConstraint("CK_staff_onboarding_membership_binding",
                "(\"AcceptedMembershipId\" IS NULL AND " +
                "\"AcceptedMembershipVersion\" IS NULL) OR " +
                "(\"AcceptedMembershipId\" IS NOT NULL AND " +
                "\"AcceptedMembershipVersion\" > 0)");
            table.HasCheckConstraint("CK_staff_onboarding_staff",
                "(\"StaffMemberId\" IS NULL OR \"StaffMemberId\" <> " +
                "'00000000-0000-0000-0000-000000000000'::uuid) AND " +
                "(\"Status\" NOT IN (4, 5) OR \"StaffMemberId\" IS NOT NULL)");
            table.HasCheckConstraint("CK_staff_onboarding_pending_profile",
                "\"StaffMemberId\" IS NOT NULL OR " +
                "\"Status\" IN (5, 7, 8, 9, 10) OR " +
                "(\"VerifiedAccountEmail\" IS NOT NULL AND \"DisplayName\" IS NOT NULL)");
            table.HasCheckConstraint("CK_staff_onboarding_terminal_redaction",
                "\"Status\" NOT IN (4, 5, 7, 8, 9, 10) OR " +
                "(\"VerifiedAccountEmail\" IS NULL AND \"DisplayName\" IS NULL AND " +
                "\"LegalName\" IS NULL AND \"WorkEmail\" IS NULL AND \"WorkPhone\" IS NULL AND " +
                "\"EmployeeNumber\" IS NULL AND \"JobTitle\" IS NULL AND \"Department\" IS NULL)");
            table.HasCheckConstraint("CK_staff_onboarding_anchor_bound_redaction",
                "\"StaffMemberId\" IS NULL OR " +
                "(\"VerifiedAccountEmail\" IS NULL AND \"DisplayName\" IS NULL AND " +
                "\"LegalName\" IS NULL AND \"WorkEmail\" IS NULL AND \"WorkPhone\" IS NULL AND " +
                "\"EmployeeNumber\" IS NULL AND \"JobTitle\" IS NULL AND \"Department\" IS NULL)");
            table.HasCheckConstraint(
                "CK_staff_onboarding_anchor_expected_resolution",
                "(\"IdentityAnchorExpectedResolutionEventId\" IS NULL OR " +
                "(\"StaffMemberId\" IS NOT NULL AND " +
                "\"IdentityAnchorExpectedResolutionEventId\" <> " +
                "'00000000-0000-0000-0000-000000000000'::uuid AND " +
                "\"IdentityAnchorExpectedResolutionEventId\" <> \"Id\")) AND " +
                "(\"IdentityAnchorContinuationEventId\" IS NULL OR " +
                "(\"IdentityAnchorExpectedResolutionEventId\" IS NOT NULL AND " +
                "\"IdentityAnchorContinuationEventId\" <> " +
                "'00000000-0000-0000-0000-000000000000'::uuid AND " +
                "\"IdentityAnchorContinuationEventId\" <> \"Id\" AND " +
                "\"IdentityAnchorContinuationEventId\" <> " +
                "\"IdentityAnchorExpectedResolutionEventId\"))");
            table.HasCheckConstraint(
                "CK_staff_onboarding_anchor_resolution_intent",
                "(\"IdentityAnchorResolutionEventId\" IS NULL AND " +
                "\"IdentityAnchorResolutionStaffMemberId\" IS NULL AND " +
                "\"IdentityAnchorResolutionApplicationVersion\" IS NULL AND " +
                "\"IdentityAnchorResolutionDisposition\" IS NULL AND " +
                "\"IdentityAnchorResolutionIntentAtUtc\" IS NULL) OR " +
                "(\"IdentityAnchorResolutionEventId\" IS NOT NULL AND " +
                "\"StaffMemberId\" IS NOT NULL AND " +
                "\"IdentityAnchorResolutionStaffMemberId\" IS NOT NULL AND " +
                "\"IdentityAnchorResolutionApplicationVersion\" > 0 AND " +
                "\"IdentityAnchorResolutionApplicationVersion\" <= \"Version\" AND " +
                "\"IdentityAnchorResolutionDisposition\" BETWEEN 1 AND 5 AND " +
                "\"IdentityAnchorResolutionIntentAtUtc\" IS NOT NULL AND " +
                "\"IdentityAnchorResolutionIntentAtUtc\" <= " +
                "\"LastChangedAtUtc\")");
            table.HasCheckConstraint(
                "CK_staff_onboarding_anchor_resolution_coordinates",
                "\"IdentityAnchorResolutionEventId\" IS NULL OR " +
                "(\"IdentityAnchorExpectedResolutionEventId\" IS NOT NULL AND " +
                "\"IdentityAnchorResolutionEventId\" = " +
                "\"IdentityAnchorExpectedResolutionEventId\" AND " +
                "\"StaffMemberId\" IS NOT NULL AND " +
                "\"IdentityAnchorResolutionStaffMemberId\" = \"StaffMemberId\")");
            table.HasCheckConstraint(
                "CK_staff_onboarding_anchor_resolution_terminal",
                "\"IdentityAnchorResolutionEventId\" IS NULL OR " +
                "((\"Status\" = 5 AND \"IdentityAnchorResolutionDisposition\" = 1) OR " +
                "(\"Status\" = 7 AND \"IdentityAnchorResolutionDisposition\" = 2) OR " +
                "(\"Status\" = 8 AND \"IdentityAnchorResolutionDisposition\" = 3) OR " +
                "(\"Status\" = 9 AND \"IdentityAnchorResolutionDisposition\" = 4) OR " +
                "(\"Status\" = 10 AND \"IdentityAnchorResolutionDisposition\" = 5))");
            table.HasCheckConstraint(
                "CK_staff_onboarding_anchor_resolution_observation",
                "\"IdentityAnchorResolutionObservedAtUtc\" IS NULL OR " +
                "(\"IdentityAnchorResolutionEventId\" IS NOT NULL AND " +
                "\"IdentityAnchorResolutionObservedAtUtc\" >= " +
                "\"IdentityAnchorResolutionIntentAtUtc\" AND " +
                "\"IdentityAnchorResolutionObservedAtUtc\" <= " +
                "\"LastChangedAtUtc\")");
            table.HasCheckConstraint(
                "CK_staff_onboarding_identity_anchor_sweep_ordinal",
                "\"IdentityAnchorSweepOrdinal\" > 0");
        });
        builder.HasKey(application => application.Id);
        builder.HasAlternateKey(application => new { application.ScopeId, application.Id });
        builder.Property(application => application.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(application => application.SourceKind).HasConversion<int>().IsRequired();
        builder.Property(application => application.SubjectId)
            .HasMaxLength(WorkspaceStaffOnboardingRules.SubjectIdMaxLength).IsRequired();
        builder.Property(application => application.VerifiedAccountEmail)
            .HasMaxLength(WorkspaceStaffOnboardingRules.EmailMaxLength);
        builder.Property(application => application.DisplayName)
            .HasMaxLength(WorkspaceStaffOnboardingRules.DisplayNameMaxLength);
        builder.Property(application => application.LegalName)
            .HasMaxLength(WorkspaceStaffOnboardingRules.LegalNameMaxLength);
        builder.Property(application => application.WorkEmail)
            .HasMaxLength(WorkspaceStaffOnboardingRules.EmailMaxLength);
        builder.Property(application => application.WorkPhone)
            .HasMaxLength(WorkspaceStaffOnboardingRules.PhoneMaxLength);
        builder.Property(application => application.EmployeeNumber)
            .HasMaxLength(WorkspaceStaffOnboardingRules.EmployeeNumberMaxLength);
        builder.Property(application => application.JobTitle)
            .HasMaxLength(WorkspaceStaffOnboardingRules.JobTitleMaxLength);
        builder.Property(application => application.Department)
            .HasMaxLength(WorkspaceStaffOnboardingRules.DepartmentMaxLength);
        builder.Property(application => application.Status).HasConversion<int>().IsRequired();
        builder.Property(application => application.AcceptedMembershipVersion);
        builder.Property(application => application.IdentityAnchorSweepOrdinal)
            .UseIdentityAlwaysColumn()
            .ValueGeneratedOnAdd()
            .IsRequired();
        builder.Property(application => application.FailureCode)
            .HasMaxLength(WorkspaceStaffOnboardingRules.FailureCodeMaxLength);
        builder.Property(application =>
                application.IdentityAnchorResolutionDisposition)
            .HasConversion<int?>();
        builder.Property(application => application.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(application => new
        {
            application.ScopeId,
            application.SourceKind,
            application.SourceId,
            application.SubjectId
        }).IsUnique();
        builder.HasIndex(application => new
        {
            application.ScopeId,
            application.ClaimId
        }).IsUnique();
        builder.HasIndex(application => new
        {
            application.ScopeId,
            application.Status,
            application.CreatedAtUtc,
            application.Id
        });
        builder.HasIndex(application => new
        {
            application.ScopeId,
            application.SubjectId,
            application.Status,
            application.Id
        });
        builder.HasIndex(application => new
        {
            application.ScopeId,
            application.StaffMemberId,
            application.Id
        }).HasFilter("\"StaffMemberId\" IS NOT NULL");
        builder.HasIndex(application => new
        {
            application.ScopeId,
            application.IdentityAnchorSweepOrdinal
        }).IsUnique();
        builder.HasIndex(application =>
                application.IdentityAnchorExpectedResolutionEventId)
            .IsUnique()
            .HasFilter(
                "\"IdentityAnchorExpectedResolutionEventId\" IS NOT NULL");
        builder.HasIndex(application =>
                application.IdentityAnchorContinuationEventId)
            .IsUnique()
            .HasFilter(
                "\"IdentityAnchorContinuationEventId\" IS NOT NULL");
        builder.Ignore(application => application.IsAdmissible);
        builder.Ignore(application => application.HasApplicantAuthority);
        builder.Ignore(application => application.DomainEvents);
    }
}

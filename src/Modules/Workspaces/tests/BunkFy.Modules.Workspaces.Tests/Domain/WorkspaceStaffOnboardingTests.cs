namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingTests
{
    [Fact]
    public void Deferred_withdrawal_canonicalizes_equivalent_organization_scope_text()
    {
        Guid claimId = Guid.NewGuid();
        Guid enrollmentLinkId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        string equivalentScope = OrganizationId.ToString("N").ToUpperInvariant();

        WorkspaceStaffDeferredClaimWithdrawal withdrawal =
            WorkspaceStaffDeferredClaimWithdrawal.Create(
                equivalentScope,
                OrganizationId,
                enrollmentLinkId,
                claimId,
                2,
                eventId,
                Now).Value;

        Assert.Equal(OrganizationId.ToString("D"), withdrawal.ScopeId);
        Assert.True(withdrawal.Matches(
            equivalentScope,
            OrganizationId,
            enrollmentLinkId,
            claimId,
            2,
            eventId,
            Now));
    }

    [Fact]
    public void Completion_requires_staff_and_redacts_applicant_data()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();

        Assert.True(application.ObserveClaimAccepted(claimId, 1, Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.MarkStaffReady(staffMemberId, Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);

        Assert.Equal(WorkspaceStaffOnboardingState.Completed, application.Status);
        Assert.Equal(staffMemberId, application.StaffMemberId);
        AssertApplicantDataRedacted(application);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Terminal_denial_states_redact_applicant_data(bool reject)
    {
        WorkspaceStaffOnboarding application = CreateApplication();

        if (reject)
        {
            Guid claimId = Guid.NewGuid();
            Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(1)).IsSuccess);
            Assert.True(application.ObserveClaimRejected(claimId, 2, Now.AddMinutes(2)).IsSuccess);
            Assert.Equal(WorkspaceStaffOnboardingState.Rejected, application.Status);
        }
        else
        {
            Assert.True(application.Supersede(Now.AddMinutes(1)).IsSuccess);
            Assert.Equal(WorkspaceStaffOnboardingState.Superseded, application.Status);
        }

        AssertApplicantDataRedacted(application);
    }

    [Fact]
    public void Invitation_expiry_is_terminal_idempotent_and_redacts_applicant_data()
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            WorkspaceStaffOnboardingSource.Invitation);

        Assert.True(application.Expire(Now.AddMinutes(1)).IsSuccess);
        long expiredVersion = application.Version;
        Assert.True(application.Expire(Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.Supersede(Now.AddMinutes(3)).IsSuccess);

        Assert.Equal(WorkspaceStaffOnboardingState.Expired, application.Status);
        Assert.Equal(expiredVersion, application.Version);
        Assert.False(application.IsActive);
        AssertApplicantDataRedacted(application);
    }

    [Fact]
    public void Failed_staff_ready_work_can_reenter_provisioning_without_losing_staff_identity()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Assert.True(application.ObserveClaimAccepted(claimId, 1, Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.MarkStaffReady(staffMemberId, Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.Fail("Workspaces.AccessProvisioningFailed", Now.AddMinutes(3)).IsSuccess);

        Assert.True(application.BeginProvisioning(Now.AddMinutes(4)).IsSuccess);

        Assert.Equal(WorkspaceStaffOnboardingState.Provisioning, application.Status);
        Assert.Equal(staffMemberId, application.StaffMemberId);
    }

    [Fact]
    public void Accepted_claim_records_the_authoritative_version()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();

        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.ObserveClaimAccepted(claimId, 2, Now.AddMinutes(2)).IsSuccess);

        Assert.Equal(claimId, application.ClaimId);
        Assert.Equal(2, application.ClaimVersion);
        Assert.Equal(WorkspaceStaffOnboardingState.Provisioning, application.Status);
    }

    [Fact]
    public void Stale_requested_event_cannot_regress_an_accepted_claim()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.ObserveClaimAccepted(claimId, 2, Now.AddMinutes(2)).IsSuccess);
        long aggregateVersion = application.Version;

        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(3)).IsSuccess);

        Assert.Equal(2, application.ClaimVersion);
        Assert.Equal(WorkspaceStaffOnboardingState.Provisioning, application.Status);
        Assert.Equal(aggregateVersion, application.Version);
    }

    [Fact]
    public void Stale_rejection_cannot_terminalize_failed_accepted_provisioning()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.ObserveClaimAccepted(claimId, 2, Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.Fail("Workspaces.AccessProvisioningFailed", Now.AddMinutes(3)).IsSuccess);
        long aggregateVersion = application.Version;

        Assert.True(application.ObserveClaimRejected(claimId, 1, Now.AddMinutes(4)).IsSuccess);

        Assert.Equal(2, application.ClaimVersion);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);
        Assert.Equal(aggregateVersion, application.Version);
        Assert.NotNull(application.DisplayName);
    }

    [Fact]
    public void Duplicate_rejection_is_idempotent_and_keeps_applicant_data_redacted()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.ObserveClaimRejected(claimId, 2, Now.AddMinutes(2)).IsSuccess);
        long aggregateVersion = application.Version;

        Assert.True(application.ObserveClaimRejected(claimId, 2, Now.AddMinutes(3)).IsSuccess);

        Assert.Equal(WorkspaceStaffOnboardingState.Rejected, application.Status);
        Assert.Equal(aggregateVersion, application.Version);
        AssertApplicantDataRedacted(application);
    }

    [Fact]
    public void Claim_expiry_is_monotonic_terminal_and_redacts_applicant_data()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(1)).IsSuccess);

        Assert.True(application.ObserveClaimExpired(claimId, 2, Now.AddMinutes(2)).IsSuccess);
        long expiredVersion = application.Version;
        Assert.True(application.ObserveClaimExpired(claimId, 2, Now.AddMinutes(3)).IsSuccess);
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(4)).IsSuccess);
        Assert.True(application.ObserveClaimAccepted(claimId, 1, Now.AddMinutes(5)).IsSuccess);
        Assert.True(application.ObserveClaimRejected(claimId, 1, Now.AddMinutes(6)).IsSuccess);

        Assert.Equal(WorkspaceStaffOnboardingState.Expired, application.Status);
        Assert.Equal(2, application.ClaimVersion);
        Assert.Equal(expiredVersion, application.Version);
        Assert.False(application.IsActive);
        AssertApplicantDataRedacted(application);
    }

    [Fact]
    public void Claim_withdrawal_is_monotonic_terminal_and_redacts_applicant_data()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(1)).IsSuccess);

        Assert.True(application.ObserveClaimWithdrawn(claimId, 2, Now.AddMinutes(2)).IsSuccess);
        long withdrawnVersion = application.Version;
        Assert.True(application.ObserveClaimWithdrawn(claimId, 2, Now.AddMinutes(3)).IsSuccess);
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(4)).IsSuccess);
        Assert.True(application.ObserveClaimAccepted(claimId, 1, Now.AddMinutes(5)).IsSuccess);
        Assert.True(application.ObserveClaimRejected(claimId, 1, Now.AddMinutes(6)).IsSuccess);
        Assert.True(application.ObserveClaimExpired(claimId, 1, Now.AddMinutes(7)).IsSuccess);

        Assert.Equal(WorkspaceStaffOnboardingState.Withdrawn, application.Status);
        Assert.Equal(2, application.ClaimVersion);
        Assert.Equal(withdrawnVersion, application.Version);
        Assert.False(application.IsActive);
        AssertApplicantDataRedacted(application);
    }

    [Fact]
    public void Accepted_event_can_arrive_before_the_requested_event()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();

        Assert.True(application.ObserveClaimAccepted(claimId, 1, Now.AddMinutes(1)).IsSuccess);
        long aggregateVersion = application.Version;
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now.AddMinutes(2)).IsSuccess);

        Assert.Equal(1, application.ClaimVersion);
        Assert.Equal(WorkspaceStaffOnboardingState.Provisioning, application.Status);
        Assert.Equal(aggregateVersion, application.Version);
    }

    [Fact]
    public void Provisioning_requires_authoritative_source_acceptance()
    {
        WorkspaceStaffOnboarding application = CreateApplication();

        Result submitted = application.BeginProvisioning(Now.AddMinutes(1));
        Assert.True(submitted.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingState.Submitted, application.Status);

        Assert.True(application.ObserveClaimRequested(
            Guid.NewGuid(),
            1,
            Now.AddMinutes(2)).IsSuccess);
        Result pendingApproval = application.BeginProvisioning(Now.AddMinutes(3));
        Assert.True(pendingApproval.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingState.PendingApproval, application.Status);
    }

    [Fact]
    public void Pending_approval_freezes_the_profile_reviewed_by_the_owner()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.ObserveClaimRequested(
            Guid.NewGuid(),
            1,
            Now.AddMinutes(1)).IsSuccess);

        Result updated = application.UpdateSubmission(
            "verified@example.test",
            "Changed after review",
            null,
            null,
            null,
            null,
            null,
            null,
            Now.AddMinutes(2));

        Assert.True(updated.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingErrors.Unavailable, updated.Error);
        Assert.Equal("Ada Operator", application.DisplayName);
        Assert.Equal(WorkspaceStaffOnboardingState.PendingApproval, application.Status);
    }

    [Fact]
    public void Exact_submission_replay_does_not_advance_the_aggregate()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        long version = application.Version;
        DateTimeOffset lastChangedAtUtc = application.LastChangedAtUtc;

        Result replayed = application.UpdateSubmission(
            " verified@example.test ",
            " Ada Operator ",
            " Ada Lovelace ",
            " ada@workspace.test ",
            " +1 555 0100 ",
            " EMP-100 ",
            " Manager ",
            " Operations ",
            Now.AddMinutes(1));

        Assert.True(replayed.IsSuccess, replayed.Error.Code);
        Assert.Equal(version, application.Version);
        Assert.Equal(lastChangedAtUtc, application.LastChangedAtUtc);
    }

    [Fact]
    public void Data_rights_correction_normalizes_profile_and_preserves_verified_identity()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        string? verifiedAccountEmail = application.VerifiedAccountEmail;
        Guid applicantEventId = Guid.NewGuid();
        Result<WorkspaceStaffApplicantProfile> requested =
            WorkspaceStaffApplicantProfile.Create(
                " Ada Corrected ",
                " ",
                " ADA.NEW@WORKSPACE.TEST ",
                " +1 555 0199 ",
                " EMP-101 ",
                " Director ",
                " ");
        Assert.True(requested.IsSuccess, requested.Error.Code);

        Result<WorkspaceStaffOnboardingCorrectionOutcome> result =
            application.ApplyDataRightsCorrection(
                requested.Value,
                expectedVersion: 1,
                applicantEventId,
                Now.AddMinutes(1));

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(1, result.Value.PreviousVersion);
        Assert.Equal(2, result.Value.CurrentVersion);
        Assert.Equal(
            [
                WorkspaceStaffOnboardingApplicantField.DisplayName,
                WorkspaceStaffOnboardingApplicantField.LegalName,
                WorkspaceStaffOnboardingApplicantField.WorkEmail,
                WorkspaceStaffOnboardingApplicantField.WorkPhone,
                WorkspaceStaffOnboardingApplicantField.EmployeeNumber,
                WorkspaceStaffOnboardingApplicantField.JobTitle,
                WorkspaceStaffOnboardingApplicantField.Department
            ],
            result.Value.ChangedFields);
        Assert.Equal(applicantEventId, result.Value.ApplicantEventId);
        Assert.Equal("Ada Corrected", application.DisplayName);
        Assert.Null(application.LegalName);
        Assert.Equal("ADA.NEW@WORKSPACE.TEST", application.WorkEmail);
        Assert.Equal("+1 555 0199", application.WorkPhone);
        Assert.Equal("EMP-101", application.EmployeeNumber);
        Assert.Equal("Director", application.JobTitle);
        Assert.Null(application.Department);
        Assert.Equal(verifiedAccountEmail, application.VerifiedAccountEmail);
        Assert.Equal(2, application.Version);
    }

    [Fact]
    public void Data_rights_correction_rejects_stale_noop_and_reviewed_targets()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        WorkspaceStaffApplicantProfile unchanged =
            WorkspaceStaffApplicantProfile.Create(
                application.DisplayName,
                application.LegalName,
                application.WorkEmail,
                application.WorkPhone,
                application.EmployeeNumber,
                application.JobTitle,
                application.Department).Value;

        Result<WorkspaceStaffOnboardingCorrectionOutcome> stale =
            application.ApplyDataRightsCorrection(
                unchanged,
                expectedVersion: 2,
                Guid.NewGuid(),
                Now.AddMinutes(1));
        Result<WorkspaceStaffOnboardingCorrectionOutcome> noOp =
            application.ApplyDataRightsCorrection(
                unchanged,
                expectedVersion: 1,
                Guid.NewGuid(),
                Now.AddMinutes(2));
        Assert.True(application.ObserveClaimRequested(
            Guid.NewGuid(),
            1,
            Now.AddMinutes(3)).IsSuccess);
        Result<WorkspaceStaffOnboardingCorrectionOutcome> reviewed =
            application.ApplyDataRightsCorrection(
                WorkspaceStaffApplicantProfile.Create(
                    "Changed",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null).Value,
                application.Version,
                Guid.NewGuid(),
                Now.AddMinutes(4));

        Assert.Equal(
            WorkspaceStaffOnboardingErrors.CorrectionVersionConflict,
            stale.Error);
        Assert.Equal(
            WorkspaceStaffOnboardingErrors.CorrectionNoChanges,
            noOp.Error);
        Assert.Equal(
            WorkspaceStaffOnboardingErrors.CorrectionUnavailable,
            reviewed.Error);
        Assert.Equal("Ada Operator", application.DisplayName);
    }

    [Fact]
    public void Invitation_acceptance_opens_the_provisioning_path()
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            WorkspaceStaffOnboardingSource.Invitation);

        Assert.True(application.ObserveInvitationAccepted(Now.AddMinutes(1)).IsSuccess);

        Assert.Equal(WorkspaceStaffOnboardingState.Provisioning, application.Status);
        Assert.True(application.BeginProvisioning(Now.AddMinutes(2)).IsSuccess);
    }

    internal static WorkspaceStaffOnboarding CreateApplication(
        WorkspaceStaffOnboardingSource sourceKind = WorkspaceStaffOnboardingSource.EnrollmentLink) =>
        WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            OrganizationId.ToString("D"),
            sourceKind,
            Guid.NewGuid(),
            SubjectId,
            "verified@example.test",
            "Ada Operator",
            "Ada Lovelace",
            "ada@workspace.test",
            "+1 555 0100",
            "EMP-100",
            "Manager",
            "Operations",
            Now).Value;

    internal static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal static readonly string SubjectId = Guid.Parse("20000000-0000-0000-0000-000000000002").ToString("D");
    internal static readonly DateTimeOffset Now = new(2026, 7, 21, 8, 0, 0, TimeSpan.Zero);

    private static void AssertApplicantDataRedacted(WorkspaceStaffOnboarding application)
    {
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
        Assert.Null(application.LegalName);
        Assert.Null(application.WorkEmail);
        Assert.Null(application.WorkPhone);
        Assert.Null(application.EmployeeNumber);
        Assert.Null(application.JobTitle);
        Assert.Null(application.Department);
    }
}

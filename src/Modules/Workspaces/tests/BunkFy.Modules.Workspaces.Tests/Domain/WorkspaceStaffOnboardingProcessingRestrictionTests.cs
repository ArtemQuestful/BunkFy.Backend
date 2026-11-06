namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Domain.Events;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingProcessingRestrictionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Applicant_authority_ends_when_staff_is_created()
    {
        WorkspaceStaffOnboarding onboarding = CreateOnboarding();
        Assert.True(onboarding.HasApplicantAuthority);

        Assert.True(onboarding.ObserveInvitationAccepted(Now.AddMinutes(1))
            .IsSuccess);
        Assert.True(onboarding.HasApplicantAuthority);

        Assert.True(onboarding.MarkStaffReady(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        Assert.False(onboarding.HasApplicantAuthority);
    }

    [Fact]
    public void Failed_pre_staff_provisioning_retains_applicant_authority()
    {
        WorkspaceStaffOnboarding onboarding = CreateOnboarding();
        Assert.True(onboarding.ObserveInvitationAccepted(Now.AddMinutes(1))
            .IsSuccess);
        Assert.True(onboarding.Fail(
            "Workspaces.TestFailure",
            Now.AddMinutes(2)).IsSuccess);

        Assert.True(onboarding.HasApplicantAuthority);
    }

    [Fact]
    public void Release_records_distinct_approval_and_advances_version()
    {
        WorkspaceStaffOnboardingProcessingRestriction restriction =
            WorkspaceStaffOnboardingProcessingRestriction.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                applyApprovalRevision: 4,
                applySelectedOnboardingVersion: 7,
                "user:privacy",
                Now).Value;
        Guid releaseCaseId = Guid.NewGuid();

        Result released = restriction.Release(
            releaseCaseId,
            releaseApprovalRevision: 9,
            releaseSelectedOnboardingVersion: 8,
            expectedVersion: 1,
            "user:decision-maker",
            Now.AddMinutes(5));

        Assert.True(released.IsSuccess);
        Assert.Equal(
            WorkspaceStaffOnboardingProcessingRestrictionState.Released,
            restriction.Status);
        Assert.Equal(2, restriction.Version);
        Assert.Equal(releaseCaseId, restriction.ReleaseCaseId);
        Assert.Equal(9, restriction.ReleaseApprovalRevision);
        Assert.Equal(8, restriction.ReleaseSelectedOnboardingVersion);
        Assert.Equal("user:decision-maker", restriction.ReleasedBy);
    }

    [Fact]
    public void Stale_or_repeated_release_does_not_change_state()
    {
        WorkspaceStaffOnboardingProcessingRestriction restriction =
            WorkspaceStaffOnboardingProcessingRestriction.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                4,
                7,
                "user:privacy",
                Now).Value;

        Result stale = restriction.Release(
            Guid.NewGuid(),
            5,
            7,
            expectedVersion: 2,
            "user:privacy",
            Now.AddMinutes(1));
        Assert.Equal(
            "Workspaces.StaffOnboardingRestrictionVersionConflict",
            stale.Error.Code);

        Assert.True(restriction.Release(
            Guid.NewGuid(),
            5,
            7,
            expectedVersion: 1,
            "user:privacy",
            Now.AddMinutes(1)).IsSuccess);
        Result repeated = restriction.Release(
            Guid.NewGuid(),
            6,
            7,
            expectedVersion: 2,
            "user:privacy",
            Now.AddMinutes(2));
        Assert.Equal(
            "Workspaces.StaffOnboardingRestrictionAlreadyReleased",
            repeated.Error.Code);
        Assert.Equal(2, restriction.Version);
    }

    [Fact]
    public void Projection_reference_counts_independent_restrictions()
    {
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
                "tenant-a",
                Guid.NewGuid(),
                contractVersion: 1,
                Now).Value;

        Assert.True(projection.Apply(0, 1, Now.AddMinutes(1)).IsSuccess);
        Assert.True(projection.Apply(1, 1, Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(2, projection.ActiveRestrictionCount);
        Assert.True(projection.IsRestricted);

        Assert.True(projection.Release(2, 1, Now.AddMinutes(3)).IsSuccess);
        Assert.Equal(1, projection.ActiveRestrictionCount);
        Assert.True(projection.IsRestricted);

        Assert.True(projection.Release(3, 1, Now.AddMinutes(4)).IsSuccess);
        Assert.Equal(0, projection.ActiveRestrictionCount);
        Assert.False(projection.IsRestricted);
        Assert.Equal(4, projection.Revision);
    }

    [Fact]
    public void Receipt_binds_versions_and_raises_pii_free_transition()
    {
        Guid applicationId = Guid.NewGuid();
        Result<WorkspaceStaffOnboardingProcessingRestrictionReceipt>
            invalidApply =
                WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                    Guid.NewGuid(),
                    "tenant-a",
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    WorkspaceStaffOnboardingProcessingRestrictionAction.Apply,
                    applicationId,
                    Guid.NewGuid(),
                    3,
                    5,
                    1,
                    1,
                    8,
                    effectiveRestricted: false,
                    "user:privacy",
                    Guid.NewGuid(),
                    Now);
        Assert.Equal(
            "Workspaces.StaffOnboardingRestrictionReceiptTransitionInvalid",
            invalidApply.Error.Code);

        WorkspaceStaffOnboardingProcessingRestrictionReceipt release =
            WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                WorkspaceStaffOnboardingProcessingRestrictionAction.Release,
                applicationId,
                Guid.NewGuid(),
                3,
                5,
                1,
                2,
                8,
                effectiveRestricted: false,
                "user:privacy",
                Guid.NewGuid(),
                Now).Value;

        WorkspaceStaffOnboardingProcessingRestrictionChangedDomainEvent
            domainEvent = Assert.IsType<
                WorkspaceStaffOnboardingProcessingRestrictionChangedDomainEvent>(
                Assert.Single(release.DomainEvents));
        Assert.Equal(applicationId, domainEvent.ApplicationId);
        Assert.Equal(1, domainEvent.ContractVersion);
        Assert.Equal(8, domainEvent.ProjectionRevision);
        Assert.False(domainEvent.IsRestricted);
    }

    private static WorkspaceStaffOnboarding CreateOnboarding() =>
        WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            "tenant-a",
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            Guid.NewGuid().ToString("D"),
            "applicant@example.test",
            "Applicant",
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            Now).Value;
}

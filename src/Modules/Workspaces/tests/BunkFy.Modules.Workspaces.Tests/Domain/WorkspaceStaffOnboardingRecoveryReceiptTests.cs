namespace BunkFy.Modules.Workspaces.Tests.Domain;

using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingRecoveryReceiptTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 15, 0, 0, TimeSpan.Zero);

    public static TheoryData<
        WorkspaceStaffOnboardingRecoveryOutcome,
        WorkspaceStaffOnboardingRecoveryReason,
        WorkspaceRecoveryVerification,
        WorkspaceRecoveryVerification,
        WorkspaceRecoveryVerification> TruthfulCases => new()
    {
        {
            WorkspaceStaffOnboardingRecoveryOutcome.Recovered,
            WorkspaceStaffOnboardingRecoveryReason.ExactStateRecovered,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent
        },
        {
            WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
            WorkspaceStaffOnboardingRecoveryReason
                .LegacyMembershipBindingMissing,
            WorkspaceRecoveryVerification.Unknown,
            WorkspaceRecoveryVerification.Unknown,
            WorkspaceRecoveryVerification.Unknown
        },
        {
            WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
            WorkspaceStaffOnboardingRecoveryReason.ProcessingRestricted,
            WorkspaceRecoveryVerification.Unknown,
            WorkspaceRecoveryVerification.Unknown,
            WorkspaceRecoveryVerification.Unknown
        },
        {
            WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
            WorkspaceStaffOnboardingRecoveryReason.MembershipConflict,
            WorkspaceRecoveryVerification.ReviewRequired,
            WorkspaceRecoveryVerification.Unknown,
            WorkspaceRecoveryVerification.Unknown
        },
        {
            WorkspaceStaffOnboardingRecoveryOutcome.RetryLater,
            WorkspaceStaffOnboardingRecoveryReason.MembershipUnavailable,
            WorkspaceRecoveryVerification.RetryLater,
            WorkspaceRecoveryVerification.Unknown,
            WorkspaceRecoveryVerification.Unknown
        },
        {
            WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
            WorkspaceStaffOnboardingRecoveryReason.StaffConflict,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.ReviewRequired,
            WorkspaceRecoveryVerification.Unknown
        },
        {
            WorkspaceStaffOnboardingRecoveryOutcome.RetryLater,
            WorkspaceStaffOnboardingRecoveryReason.StaffUnavailable,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.RetryLater,
            WorkspaceRecoveryVerification.Unknown
        },
        {
            WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
            WorkspaceStaffOnboardingRecoveryReason.AccessConflict,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.ReviewRequired
        },
        {
            WorkspaceStaffOnboardingRecoveryOutcome.RetryLater,
            WorkspaceStaffOnboardingRecoveryReason.AccessUnavailable,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.RetryLater
        }
    };

    [Fact]
    public void Exact_recovery_requires_complete_read_only_proof()
    {
        WorkspaceStaffOnboardingRecoveryReceiptValues values = Values(
            WorkspaceStaffOnboardingRecoveryOutcome.Recovered,
            WorkspaceStaffOnboardingRecoveryReason.ExactStateRecovered,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent,
            selectedVersion: 6,
            resultingVersion: 7,
            resultingStatus: WorkspaceStaffOnboardingState.Completed,
            organizationObservedAtUtc: Now,
            staffObservedAtUtc: Now,
            accessObservedAtUtc: Now);

        Result<WorkspaceStaffOnboardingRecoveryReceipt> result =
            WorkspaceStaffOnboardingRecoveryReceipt.Create(values);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.False(result.Value.AccessMutationPerformed);
        Assert.Equal(6, result.Value.SelectedApplicationVersion);
        Assert.Equal(7, result.Value.ResultingApplicationVersion);
        Assert.Equal(Now, result.Value.OrganizationObservedAtUtc);
    }

    [Fact]
    public void Review_receipt_keeps_application_unchanged_and_allows_no_read_timestamp()
    {
        WorkspaceStaffOnboardingRecoveryReceiptValues values = TruthfulValues(
            WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
            WorkspaceStaffOnboardingRecoveryReason
                .LegacyMembershipBindingMissing,
            WorkspaceRecoveryVerification.Unknown,
            WorkspaceRecoveryVerification.Unknown,
            WorkspaceRecoveryVerification.Unknown);

        Result<WorkspaceStaffOnboardingRecoveryReceipt> result =
            WorkspaceStaffOnboardingRecoveryReceipt.Create(values);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Null(result.Value.OrganizationObservedAtUtc);
        Assert.Equal(
            result.Value.SelectedApplicationVersion,
            result.Value.ResultingApplicationVersion);
        Assert.Equal(
            result.Value.SelectedApplicationStatus,
            result.Value.ResultingApplicationStatus);
    }

    [Fact]
    public void Changed_review_or_incomplete_recovery_proof_is_rejected()
    {
        WorkspaceStaffOnboardingRecoveryReceiptValues changedReview = Values(
            WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
            WorkspaceStaffOnboardingRecoveryReason.MembershipConflict,
            WorkspaceRecoveryVerification.ReviewRequired,
            WorkspaceRecoveryVerification.Unknown,
            WorkspaceRecoveryVerification.Unknown,
            selectedVersion: 6,
            resultingVersion: 7,
            resultingStatus: WorkspaceStaffOnboardingState.Failed,
            organizationObservedAtUtc: Now,
            staffObservedAtUtc: null,
            accessObservedAtUtc: null);
        WorkspaceStaffOnboardingRecoveryReceiptValues incompleteRecovery =
            Values(
                WorkspaceStaffOnboardingRecoveryOutcome.Recovered,
                WorkspaceStaffOnboardingRecoveryReason.ExactStateRecovered,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.Equivalent,
                selectedVersion: 6,
                resultingVersion: 7,
                resultingStatus: WorkspaceStaffOnboardingState.Completed,
                organizationObservedAtUtc: Now,
                staffObservedAtUtc: null,
                accessObservedAtUtc: Now);

        Assert.True(WorkspaceStaffOnboardingRecoveryReceipt
            .Create(changedReview).IsFailure);
        Assert.True(WorkspaceStaffOnboardingRecoveryReceipt
            .Create(incompleteRecovery).IsFailure);
        Assert.True(WorkspaceStaffOnboardingRecoveryReceipt
            .Create(incompleteRecovery with
            {
                StaffObservedAtUtc = Now,
                AccessMutationPerformed = true
            }).IsFailure);
        Assert.True(WorkspaceStaffOnboardingRecoveryReceipt
            .Create(incompleteRecovery with
            {
                StaffObservedAtUtc = Now,
                MembershipStatus = 2
            }).IsFailure);
    }

    [Theory]
    [MemberData(nameof(TruthfulCases))]
    public void Domain_truth_table_accepts_only_compatible_outcome_reason_and_stage(
        WorkspaceStaffOnboardingRecoveryOutcome outcome,
        WorkspaceStaffOnboardingRecoveryReason reason,
        WorkspaceRecoveryVerification organization,
        WorkspaceRecoveryVerification staff,
        WorkspaceRecoveryVerification access)
    {
        WorkspaceStaffOnboardingRecoveryReceiptValues values = TruthfulValues(
            outcome,
            reason,
            organization,
            staff,
            access);

        Result<WorkspaceStaffOnboardingRecoveryReceipt> result =
            WorkspaceStaffOnboardingRecoveryReceipt.Create(values);

        Assert.True(result.IsSuccess, result.Error.Code);
    }

    [Fact]
    public void Domain_truth_table_rejects_version_status_stage_and_chronology_lies()
    {
        WorkspaceStaffOnboardingRecoveryReceiptValues exact = TruthfulValues(
            WorkspaceStaffOnboardingRecoveryOutcome.Recovered,
            WorkspaceStaffOnboardingRecoveryReason.ExactStateRecovered,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent);

        AssertFailure(exact with { ExpectedApplicationVersion = 5 });
        AssertFailure(exact with
        {
            SelectedApplicationStatus = WorkspaceStaffOnboardingState.Completed
        });
        AssertFailure(exact with
        {
            Outcome = WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired
        });
        AssertFailure(exact with
        {
            Reason = WorkspaceStaffOnboardingRecoveryReason.AccessConflict
        });
        AssertFailure(exact with
        {
            ResultingApplicationStatus = WorkspaceStaffOnboardingState.Failed
        });
        AssertFailure(exact with { ResultingApplicationVersion = 6 });
        AssertFailure(exact with
        {
            AccessVerification = WorkspaceRecoveryVerification.RetryLater
        });
        AssertFailure(exact with { AccessObservedAtUtc = null });
        AssertFailure(exact with
        {
            AccessObservedAtUtc = exact.RecordedAtUtc.AddTicks(1)
        });
    }

    private static void AssertFailure(
        WorkspaceStaffOnboardingRecoveryReceiptValues values) =>
        Assert.True(WorkspaceStaffOnboardingRecoveryReceipt
            .Create(values).IsFailure);

    private static WorkspaceStaffOnboardingRecoveryReceiptValues TruthfulValues(
        WorkspaceStaffOnboardingRecoveryOutcome outcome,
        WorkspaceStaffOnboardingRecoveryReason reason,
        WorkspaceRecoveryVerification organization,
        WorkspaceRecoveryVerification staff,
        WorkspaceRecoveryVerification access)
    {
        bool recovered = outcome ==
            WorkspaceStaffOnboardingRecoveryOutcome.Recovered;
        WorkspaceStaffOnboardingRecoveryReceiptValues values = Values(
            outcome,
            reason,
            organization,
            staff,
            access,
            selectedVersion: 6,
            resultingVersion: recovered ? 7 : 6,
            resultingStatus: recovered
                ? WorkspaceStaffOnboardingState.Completed
                : WorkspaceStaffOnboardingState.Failed,
            organizationObservedAtUtc: organization ==
                WorkspaceRecoveryVerification.Unknown ? null : Now,
            staffObservedAtUtc: staff ==
                WorkspaceRecoveryVerification.Unknown ? null : Now,
            accessObservedAtUtc: access ==
                WorkspaceRecoveryVerification.Unknown ? null : Now);
        if (organization == WorkspaceRecoveryVerification.Unknown)
        {
            values = values with
            {
                OrganizationRevision = null,
                MembershipId = null,
                MembershipVersion = null,
                MembershipStatus = null
            };
        }

        if (staff == WorkspaceRecoveryVerification.Unknown)
        {
            values = values with
            {
                StaffTenantRevision = null,
                StaffMemberVersion = null,
                StaffStateProofSha256 = null,
                StaffPropertySetSha256 = null
            };
        }

        if (access == WorkspaceRecoveryVerification.Unknown)
        {
            values = values with
            {
                AccessManagementRevision = null,
                AccessFactsSha256 = null
            };
        }

        return values;
    }

    private static WorkspaceStaffOnboardingRecoveryReceiptValues Values(
        WorkspaceStaffOnboardingRecoveryOutcome outcome,
        WorkspaceStaffOnboardingRecoveryReason reason,
        WorkspaceRecoveryVerification organizationVerification,
        WorkspaceRecoveryVerification staffVerification,
        WorkspaceRecoveryVerification accessVerification,
        long selectedVersion,
        long resultingVersion,
        WorkspaceStaffOnboardingState resultingStatus,
        DateTimeOffset? organizationObservedAtUtc,
        DateTimeOffset? staffObservedAtUtc,
        DateTimeOffset? accessObservedAtUtc) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "tenant-a",
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        WorkspaceStaffOnboardingRecoveryTriggerKind.ManualOperator,
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "auth:applicant",
        "auth:plan-creator",
        "auth:recovery-operator",
        "system:workspaces",
        new string('a', 64),
        ExpectedApplicationVersion: 6,
        selectedVersion,
        WorkspaceStaffOnboardingState.Failed,
        resultingVersion,
        resultingStatus,
        Guid.Parse("66666666-6666-6666-6666-666666666666"),
        CurrentProfileVersion: 3,
        PropertyCount: 2,
        new string('b', 64),
        OrganizationRevision: 4,
        Guid.Parse("77777777-7777-7777-7777-777777777777"),
        MembershipVersion: 2,
        MembershipStatus: 1,
        StaffTenantRevision: 5,
        StaffMemberVersion: 4,
        new string('c', 64),
        new string('d', 64),
        AccessManagementRevision: 8,
        new string('e', 64),
        organizationVerification,
        staffVerification,
        accessVerification,
        outcome,
        reason,
        AccessMutationPerformed: false,
        organizationObservedAtUtc,
        staffObservedAtUtc,
        accessObservedAtUtc,
        Now);
}

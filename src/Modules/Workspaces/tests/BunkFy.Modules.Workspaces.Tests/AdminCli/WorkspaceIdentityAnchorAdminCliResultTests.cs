namespace BunkFy.Modules.Workspaces.Tests.AdminCli;

using BunkFy.Modules.Workspaces.AdminCli;
using BunkFy.Modules.Workspaces.Admin.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Administration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceIdentityAnchorAdminCliResultTests
{
    private static readonly string SourceSha256 = new('a', 64);
    private static readonly string StateSha256 = new('b', 64);
    private static readonly string OwnerSha256 = new('c', 64);
    private static readonly DateTimeOffset ReviewedAtUtc = new(
        2026,
        8,
        11,
        16,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Verified_result_requires_a_complete_consistent_truth_shape()
    {
        WorkspaceStaffIdentityAnchorReconcileResult result = ValidResult();

        Assert.True(
            WorkspacesAdminCliModule
                .IsVerifiedIdentityAnchorReconcileResult(result));
    }

    [Fact]
    public void Unknown_or_inconsistent_success_shapes_are_not_cli_success()
    {
        WorkspaceStaffIdentityAnchorReconcileResult valid = ValidResult();
        WorkspaceStaffIdentityAnchorReconcileResult[] invalid =
        [
            valid with
            {
                Outcome = WorkspaceStaffIdentityAnchorReconcileOutcome.Unknown
            },
            valid with
            {
                Outcome = WorkspaceStaffIdentityAnchorReconcileOutcome
                    .ApplyOutcomeUnknown,
                MustRerunStatus = true
            },
            valid with { AppliedCount = null },
            valid with { AppliedCount = -1 },
            valid with { Status = null },
            valid with { MustRerunStatus = true },
            valid with { AcceptedAnchorStateSha256 = "not-a-digest" },
            valid with
            {
                AcceptedSourceEvidenceSha256 = new string('d', 64)
            },
            valid with
            {
                AcceptedOwnerManifestSha256 = new string('e', 64)
            }
        ];

        Assert.All(invalid, result => Assert.False(
            WorkspacesAdminCliModule
                .IsVerifiedIdentityAnchorReconcileResult(result)));
    }

    [Fact]
    public void Historical_review_result_requires_complete_terminal_truth()
    {
        WorkspaceStaffHistoricalNoProvisionDispositionResult result =
            ValidHistoricalResult();

        Assert.True(
            WorkspacesAdminCliModule
                .IsWellFormedHistoricalNoProvisionResult(
                    result,
                    expectedApplicationVersion: 3,
                    WorkspaceStaffOnboardingState.Submitted));
    }

    [Fact]
    public void Historical_review_unknown_success_shapes_are_not_cli_success()
    {
        WorkspaceStaffHistoricalNoProvisionDispositionResult valid =
            ValidHistoricalResult();
        WorkspaceStaffHistoricalNoProvisionDispositionResult[] invalid =
        [
            valid with { ReceiptId = Guid.Empty },
            valid with { OperationId = Guid.Empty },
            valid with { ApplicationId = Guid.Empty },
            valid with { ResultApplicationVersion = 0 },
            valid with { ResultApplicationVersion = 5 },
            valid with
            {
                ResultApplicationStatus = WorkspaceStaffOnboardingState.Unknown
            },
            valid with
            {
                ResultApplicationStatus =
                    WorkspaceStaffOnboardingState.Submitted
            },
            valid with { StaffEvidenceSha256 = "not-a-digest" },
            valid with { CanonicalSha256 = "not-a-digest" },
            valid with { ReviewedAtUtc = default }
        ];

        Assert.All(invalid, result => Assert.False(
            WorkspacesAdminCliModule
                .IsWellFormedHistoricalNoProvisionResult(
                    result,
                    expectedApplicationVersion: 3,
                    WorkspaceStaffOnboardingState.Submitted)));
    }

    [Fact]
    public void Historical_review_has_a_stable_dedicated_operation_permission()
    {
        const string expected =
            "workspaces.identity-anchors.historical-no-provision.review";

        Assert.Equal(
            expected,
            WorkspacesAdminOperationNames
                .IdentityAnchorsHistoricalNoProvisionReview);
        Assert.Equal(
            expected,
            WorkspacesAdminPermissions
                .IdentityAnchorsHistoricalNoProvisionReview.Code);
    }

    [Fact]
    public void Historical_review_receipt_uses_the_authenticated_admin_actor()
    {
        ScopedAdminActorContext actorContext = new();
        actorContext.SetActor(AdminActor.System("actual-reviewer"));
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IAdminActorContext>(actorContext)
            .BuildServiceProvider();

        Assert.Equal(
            "actual-reviewer",
            WorkspacesAdminCliModule.ResolveHistoricalReviewActor(services));
    }

    private static WorkspaceStaffIdentityAnchorReconcileResult ValidResult() =>
        new(
            AppliedCount: 1,
            Status: new WorkspaceStaffIdentityAnchorCutoverStatus(
                WorkspaceSourceCount: 1,
                OwnerBindingCount: 0,
                AlreadyAnchoredCount: 1,
                SeedableWorkspaceCount: 0,
                SeedableOwnerCount: 0,
                AmbiguousCount: 0,
                ConflictCount: 0,
                SourceSha256,
                StateSha256,
                OwnerSha256,
                OwnerManifestProvided: true,
                CanReconcile: true,
                IsReady: true,
                AuthoritativeOwnerCount: 0,
                OrganizationsScopeRevision: 1,
                HistoricalBindingCount: 0,
                WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                    .ReviewedNoHistoricalOwnerSources,
                HistoricalEvidenceSha256: new string('f', 64),
                TotalIssueCount: 0,
                HasMoreIssues: false,
                Issues: []),
            WorkspaceStaffIdentityAnchorReconcileOutcome.AppliedAndVerified,
            MustRerunStatus: false,
            SourceSha256,
            StateSha256,
            OwnerSha256);

    private static WorkspaceStaffHistoricalNoProvisionDispositionResult
        ValidHistoricalResult() => new(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("22222222-2222-3333-4444-555555555555"),
            Guid.Parse("33333333-2222-3333-4444-555555555555"),
            ResultApplicationVersion: 4,
            WorkspaceStaffOnboardingState.Superseded,
            new string('d', 64),
            new string('e', 64),
            ReviewedAtUtc,
            AlreadyReviewed: false);
}

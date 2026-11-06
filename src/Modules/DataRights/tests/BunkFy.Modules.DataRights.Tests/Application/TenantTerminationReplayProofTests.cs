namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationReplayProofTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 16, 0, 0, TimeSpan.Zero);
    private static readonly string PolicyDigest = new('a', 64);
    private static readonly string CatalogDigest = new('b', 64);

    [Fact]
    public void Intent_binds_exact_request_approval_execution_and_catalog_coordinates()
    {
        TenantTerminationReplayIntent intent = CreateIntent();
        TenantTerminationReplayJournalEntry entry =
            TenantTerminationReplayJournalEntry.ForIntent(intent);

        Assert.True(intent.HasValidProof());
        Assert.True(entry.HasValidProof());
        Assert.Equal(TenantTerminationReplayEntryKind.Intent, entry.Kind);
        Assert.Equal(intent.TenantId, entry.TenantId);
        Assert.Equal(intent.ProcessId, entry.ProcessId);
        Assert.Equal(64, intent.IntentSha256.Length);
        Assert.False((intent with { ExportRequested = false }).HasValidProof());
        Assert.False((intent with
        {
            ApprovedOwnerCatalogSha256 = new string('c', 64)
        }).HasValidProof());
    }

    [Fact]
    public void Intent_requires_distinct_approver_and_executor()
    {
        Assert.Throws<ArgumentException>(() => CreateIntent(
            executingActorId: "operator:approver"));
    }

    [Fact]
    public void Dispatch_binds_exact_process_owner_task_and_catalog_coordinates()
    {
        TenantTerminationReplayDispatch dispatch = CreateDispatch();
        TenantTerminationReplayJournalEntry entry =
            TenantTerminationReplayJournalEntry.ForDispatch(dispatch);

        Assert.True(dispatch.HasValidProof());
        Assert.True(entry.HasValidProof());
        Assert.Equal(
            TenantTerminationReplayEntryKind.Dispatch,
            entry.Kind);
        Assert.Equal(64, dispatch.DispatchSha256.Length);
        Assert.Equal(64, entry.LogicalEntryId.Length);
        Assert.False((dispatch with { OwnerKey = "guests" }).HasValidProof());
        Assert.False((dispatch with
        {
            ExecutionBoundary =
                TenantTerminationExecutionBoundary.TenantScopedTask
        }).HasValidProof());
    }

    [Fact]
    public void Result_is_authenticated_against_the_exact_dispatch()
    {
        TenantTerminationReplayDispatch dispatch = CreateDispatch();
        TenantTerminationContributionResult contribution = CompletedResult();
        TenantTerminationReplayResult result =
            TenantTerminationReplayResult.Create(
                dispatch,
                contribution,
                Now.AddMinutes(3));
        TenantTerminationReplayJournalEntry entry =
            TenantTerminationReplayJournalEntry.ForResult(dispatch, result);

        Assert.True(result.HasValidProof(dispatch));
        Assert.True(entry.HasValidProof());
        Assert.NotEqual(
            TenantTerminationReplayJournalEntry.ForDispatch(dispatch)
                .LogicalEntryId,
            entry.LogicalEntryId);

        TenantTerminationReplayDispatch anotherAttempt = CreateDispatch(
            taskAttempt: 2);
        Assert.False(result.HasValidProof(anotherAttempt));
        Assert.False((result with
        {
            Contribution = contribution with
            {
                ResultCode = "reservations.termination.changed"
            }
        }).HasValidProof(dispatch));
    }

    [Fact]
    public void Result_requires_contribution_and_protection_before_deadline()
    {
        TenantTerminationReplayDispatch dispatch = CreateDispatch();

        Assert.Throws<ArgumentException>(() =>
            TenantTerminationReplayResult.Create(
                dispatch,
                CompletedResult() with
                {
                    RecordedAtUtc = dispatch.DeadlineUtc
                },
                dispatch.DeadlineUtc));
        Assert.Throws<ArgumentException>(() =>
            TenantTerminationReplayResult.Create(
                dispatch,
                CompletedResult(),
                dispatch.DeadlineUtc));
    }

    [Fact]
    public void Invalid_owner_outcome_shapes_are_rejected_before_journaling()
    {
        TenantTerminationReplayDispatch dispatch = CreateDispatch();
        TenantTerminationContributionResult invalidCompleted =
            CompletedResult() with { RemainingActiveCount = 1 };
        TenantTerminationContributionResult staleBlocked = new(
            TenantTerminationContributionStatus.Blocked,
            "reservations.termination.legal-hold",
            AffectedCount: 0,
            RetainedMinimumCount: 1,
            RemainingActiveCount: 1,
            HoldReviewAtUtc: Now,
            SelectedProofRevision: null,
            ResultingProofRevision: null,
            CatalogVersion: 3,
            CatalogDigest,
            Now.AddMinutes(2));

        Assert.Throws<ArgumentException>(() =>
            TenantTerminationReplayResult.Create(
                dispatch,
                invalidCompleted,
                Now.AddMinutes(3)));
        Assert.Throws<ArgumentException>(() =>
            TenantTerminationReplayResult.Create(
                dispatch,
                staleBlocked,
                Now.AddMinutes(3)));
    }

    [Fact]
    public void Global_control_boundary_is_restricted_to_destroy_dispatches()
    {
        TenantTerminationContributionRequest freeze = Request() with
        {
            Phase = TenantTerminationContributionPhase.Freeze
        };

        Assert.Throws<ArgumentException>(() =>
            TenantTerminationReplayDispatch.Create(
                freeze,
                "workspaces",
                catalogVersion: 3,
                CatalogDigest,
                TenantTerminationExecutionBoundary.GlobalControlTask,
                Guid.NewGuid(),
                taskAttempt: 1,
                Now.AddMinutes(1)));
    }

    private static TenantTerminationReplayDispatch CreateDispatch(
        int taskAttempt = 1) =>
        TenantTerminationReplayDispatch.Create(
            Request(),
            "reservations",
            catalogVersion: 3,
            CatalogDigest,
            TenantTerminationExecutionBoundary.GlobalControlTask,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            taskAttempt,
            Now.AddMinutes(1));

    private static TenantTerminationReplayIntent CreateIntent(
        string executingActorId = "operator:executor") =>
        TenantTerminationReplayIntent.Create(
            "tenant-a",
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("22222222-3333-4444-5555-666666666666"),
            DataRightsRequesterRelationship.TenantOwner,
            "operator:requester",
            Now.AddMinutes(-3),
            exportRequested: true,
            approvalRevision: 3,
            "operator:approver",
            Now.AddMinutes(-2),
            PolicyDigest,
            CatalogDigest,
            Guid.Parse("33333333-4444-5555-6666-777777777777"),
            Guid.Parse("44444444-5555-6666-7777-888888888888"),
            executingActorId,
            Now.AddMinutes(-1),
            Now);

    private static TenantTerminationContributionRequest Request() =>
        new(
            TenantTerminationContract.CurrentVersion,
            "tenant-a",
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("22222222-3333-4444-5555-666666666666"),
            ApprovalRevision: 7,
            OperationRevision: 8,
            Guid.Parse("33333333-4444-5555-6666-777777777777"),
            TenantTerminationContributionPhase.Destroy,
            Guid.Parse("44444444-5555-6666-7777-888888888888"),
            Guid.Parse("55555555-6666-7777-8888-999999999999"),
            PolicyDigest,
            "operator:executor",
            Now.AddMinutes(5));

    private static TenantTerminationContributionResult CompletedResult() =>
        new(
            TenantTerminationContributionStatus.Completed,
            "reservations.termination.destroyed",
            AffectedCount: 12,
            RetainedMinimumCount: 2,
            RemainingActiveCount: 0,
            HoldReviewAtUtc: null,
            SelectedProofRevision: 4,
            ResultingProofRevision: 5,
            CatalogVersion: 3,
            CatalogDigest,
            Now.AddMinutes(2));
}

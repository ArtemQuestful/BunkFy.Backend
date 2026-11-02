namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain.Termination;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceTenantTerminationContributorTests
{
    private const string TenantId =
        "9a8f9c94-2dd7-42b4-9910-b00cb92f2a98";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Catalog_v12_has_one_immutable_manifest_identity()
    {
        Assert.Equal(12, WorkspacesTenantTerminationMetadata.CatalogVersion);
        Assert.Equal(
            12,
            WorkspacesTenantTerminationMetadata.PersonalDataCatalogVersion);
        Assert.Equal(
            "fc26fc9027bff3e5c859a261ac589ff7a576da29a9c3ea10ad30886789a74022",
            WorkspacesTenantTerminationMetadata.CatalogSha256);
        Assert.NotEqual(
            "29475cb08300f9bbee923231b2059dc0de55b64875bfb21d32be64c18728b301",
            WorkspacesTenantTerminationMetadata.CatalogSha256);
    }

    [Fact]
    public async Task Freeze_returns_exact_pii_free_owner_proof()
    {
        StubDispatcher dispatcher = new();
        WorkspaceTenantTerminationContributor contributor = new(
            dispatcher,
            new StubRepository(),
            new TestScopeContext(),
            new TestClock());
        TenantTerminationContributionRequest request = NewRequest();

        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(request, default);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("workspace.termination.frozen", result.ResultCode);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(0, result.RemainingActiveCount);
        Assert.Equal(0, result.SelectedProofRevision);
        Assert.Equal(1, result.ResultingProofRevision);
        Assert.Equal(64, result.CatalogSha256.Length);
        Assert.IsType<ApplyWorkspaceTerminationFenceCommand>(
            dispatcher.Command);
        Assert.Equal(
            [
                TenantTerminationContributionPhase.Freeze,
                TenantTerminationContributionPhase.Export,
                TenantTerminationContributionPhase.Destroy,
                TenantTerminationContributionPhase.Restore
            ],
            contributor.Descriptor.PhasePlans.Select(plan => plan.Phase));
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.DestroyDependencyOwnerKeys,
            contributor.Descriptor.PhasePlans.Single(plan =>
                plan.Phase == TenantTerminationContributionPhase.Destroy)
                .DependsOnOwnerKeys);
        Assert.All(
            contributor.Descriptor.PhasePlans.Where(plan =>
                plan.Phase != TenantTerminationContributionPhase.Destroy),
            plan => Assert.Empty(plan.DependsOnOwnerKeys));
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.CatalogVersion,
            contributor.Descriptor.CatalogVersion);
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.CatalogSha256,
            contributor.Descriptor.CatalogSha256);
        Assert.True(contributor.Descriptor.MandatoryForProduction);
    }

    [Fact]
    public async Task Destroy_routes_to_the_persistence_owner()
    {
        StubDispatcher dispatcher = new();
        StubDestructionOwner destruction = new();
        WorkspaceTenantTerminationContributor contributor = new(
            dispatcher,
            new StubRepository(),
            new TestScopeContext(),
            new TestClock(),
            destruction);
        TenantTerminationContributionRequest request = NewRequest() with
        {
            OperationRevision = 2,
            Phase = TenantTerminationContributionPhase.Destroy
        };

        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(request, default);

        Assert.Same(request, destruction.Request);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal("workspace.termination.destroy-test", result.ResultCode);
        Assert.Null(dispatcher.Command);
    }

    [Fact]
    public async Task Scope_mismatch_fails_without_dispatch()
    {
        StubDispatcher dispatcher = new();
        WorkspaceTenantTerminationContributor contributor = new(
            dispatcher,
            new StubRepository(),
            new TestScopeContext(),
            new TestClock());
        TenantTerminationContributionRequest request = NewRequest() with
        {
            TenantId = Guid.NewGuid().ToString("D")
        };

        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(request, default);

        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            result.Status);
        Assert.Null(dispatcher.Command);
    }

    private static TenantTerminationContributionRequest NewRequest() =>
        new(
            TenantTerminationContract.CurrentVersion,
            TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            1,
            Guid.NewGuid(),
            TenantTerminationContributionPhase.Freeze,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Digest,
            "system-operator",
            Now.AddMinutes(5));

    private sealed class StubDispatcher : IRequestDispatcher
    {
        public object? Command { get; private set; }

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            this.Command = command;
            ApplyWorkspaceTerminationFenceCommand apply =
                Assert.IsType<ApplyWorkspaceTerminationFenceCommand>(command);
            WorkspaceTerminationFenceReceiptDto receipt = new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                apply.ProcessId,
                apply.CaseId,
                apply.ApprovalRevision,
                apply.OperationRevision,
                apply.WorkItemId,
                apply.IdempotencyKey,
                apply.TerminationEpoch,
                WorkspaceTerminationFenceActionDto.Freeze,
                0,
                1,
                WorkspaceTerminationFenceReceiptStateDto.Frozen,
                apply.PolicyEvidenceSha256,
                apply.ActorId,
                Now);
            return Task.FromResult(
                (Result<TResponse>)(object)Result.Success(receipt));
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubRepository
        : IWorkspaceTerminationFenceRepository
    {
        public Task<WorkspaceTerminationFence?> GetActiveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceTerminationFence?>(null);

        public Task<WorkspaceTerminationFence?> GetByProcessAsync(
            Guid processId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceTerminationFence?>(null);

        public Task<bool> HasCoordinatesAsync(
                Guid processId,
                Guid terminationEpoch,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<WorkspaceTerminationFenceReceipt?> FindReceiptAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceTerminationFenceReceipt?>(null);

        public Task<bool> TryLockAsync(
            Guid fenceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AddAsync(
            WorkspaceTerminationFence fence,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AddReceiptAsync(
            WorkspaceTerminationFenceReceipt receipt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubDestructionOwner
        : IWorkspaceTenantDestructionOwner
    {
        public TenantTerminationContributionRequest? Request { get; private set; }

        public Task<TenantTerminationContributionResult> ExecuteAsync(
            TenantTerminationContributionRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            return Task.FromResult(new TenantTerminationContributionResult(
                TenantTerminationContributionStatus.RetryRequired,
                "workspace.termination.destroy-test",
                AffectedCount: 0,
                RetainedMinimumCount: 0,
                RemainingActiveCount: 1,
                HoldReviewAtUtc: null,
                SelectedProofRevision: null,
                ResultingProofRevision: null,
                WorkspacesTenantTerminationMetadata.CatalogVersion,
                WorkspacesTenantTerminationMetadata.CatalogSha256,
                Now));
        }
    }
}

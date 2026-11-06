namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ExecuteTenantTerminationExportOwnerWorkTaskHandlerTests
{
    private const string TenantId = "tenant-a";
    private const string OwnerKey = "reservations";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProcessId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid WorkItemId =
        Guid.Parse("22222222-3333-4444-5555-666666666666");
    private static readonly Guid RunId =
        Guid.Parse("33333333-4444-5555-6666-777777777777");
    private static readonly string PolicyDigest = new('a', 64);
    private static readonly string CatalogDigest = new('b', 64);
    private static readonly string FrozenDigest = new('c', 64);

    [Fact]
    public async Task Normal_generator_return_at_deadline_is_failed_not_journaled()
    {
        List<string> order = [];
        MutableClock clock = new(Now);
        TenantTerminationContributionRequest ownerRequest = OwnerRequest();
        TenantTerminationExportFragmentGenerationRequest generationRequest =
            GenerationRequest(ownerRequest);
        RecordingDispatcher dispatcher = new(
            Start(ownerRequest),
            new(FragmentVersion: 4, generationRequest),
            order);
        RecordingReplayStore replayStore = new(order);
        LateGenerator generator = new(clock, order);
        ExecuteTenantTerminationExportOwnerWorkTaskHandler handler = new(
            dispatcher,
            replayStore,
            generator,
            new RecordingScheduler(),
            clock,
            new FixedScopeContext(TenantId));

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(
            () => handler.HandleAsync(
                new(ProcessId, WorkItemId, OperationRevision: 8, OwnerKey),
                Context(),
                CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationOwnerDeadlineExceeded",
            failure.Message);
        Assert.Equal(
            [
                "begin-owner",
                "append-dispatch",
                "begin-fragment",
                "generate",
                "fail-fragment"
            ],
            order);
        Assert.Null(replayStore.Attempt?.Result);
        Assert.Equal(
            "owner-export-deadline-exceeded",
            dispatcher.Failed?.FailureCode);
        Assert.False(dispatcher.Completed);
    }

    private static TenantTerminationOwnerWorkStart Start(
        TenantTerminationContributionRequest request) => new(
            DispatchRequired: true,
            TenantTerminationOwnerWorkState.Processing,
            WorkItemVersion: 2,
            OwnerKey,
            CatalogVersion: 3,
            CatalogDigest,
            TenantTerminationExecutionBoundary.TenantScopedTask,
            request,
            ReadyDispatches: []);

    private static TenantTerminationContributionRequest OwnerRequest() => new(
        TenantTerminationContract.CurrentVersion,
        TenantId,
        ProcessId,
        Guid.Parse("44444444-5555-6666-7777-888888888888"),
        ApprovalRevision: 7,
        OperationRevision: 8,
        Guid.Parse("55555555-6666-7777-8888-999999999999"),
        TenantTerminationContributionPhase.Export,
        WorkItemId,
        Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"),
        PolicyDigest,
        TenantTerminationCoordination.ExecutorActorId,
        Now.AddMinutes(2));

    private static TenantTerminationExportFragmentGenerationRequest
        GenerationRequest(TenantTerminationContributionRequest request) => new(
            new(
                TenantId,
                ProcessId,
                request.CaseId,
                request.ApprovalRevision,
                FreezeOperationRevision: 7,
                ExportOperationRevision: request.OperationRevision,
                request.TerminationEpoch,
                WorkspaceFenceRevision: 4,
                FrozenDigest,
                PolicyDigest,
                request.ExecutingActorId,
                FrozenAtUtc: Now.AddMinutes(-1),
                GeneratedAtUtc: Now,
                request.DeadlineUtc,
                new(
                    OwnerKey,
                    WorkItemId,
                    request.IdempotencyKey,
                    request.ContractVersion,
                    CatalogVersion: 3,
                    CatalogDigest),
                [
                    new(
                        OwnerKey,
                        request.ContractVersion,
                        CatalogVersion: 3,
                        CatalogDigest)
                ]),
            RunId,
            GenerationAttempt: 1,
            ExpiresAtUtc: Now.AddDays(1));

    private static TaskExecutionContext Context() => new(
        RunId,
        DataRightsModuleMetadata.Name,
        ExecuteTenantTerminationExportOwnerWorkPayload.TaskName,
        DataRightsModuleMetadata.TenantTerminationWorkerGroup,
        "worker-1",
        "node-1",
        attempt: 1,
        scopeId: TenantId,
        correlationId: ProcessId,
        payloadVersion:
            ExecuteTenantTerminationExportOwnerWorkPayload.PayloadVersion);

    private sealed class RecordingDispatcher(
        TenantTerminationOwnerWorkStart start,
        TenantTerminationExportFragmentGenerationStart fragmentStart,
        List<string> order) : ITaskCommandDispatcher
    {
        public FailTenantTerminationExportFragmentGenerationCommand? Failed
        {
            get;
            private set;
        }

        public bool Completed { get; private set; }

        public Task<Result<TResponse>> DispatchAsync<TCommand, TResponse>(
            TaskExecutionContext context,
            TCommand command,
            CancellationToken cancellationToken)
            where TCommand : ICommand<TResponse>
        {
            object result = command switch
            {
                BeginTenantTerminationOwnerWorkCommand => this.BeginOwner(),
                BeginTenantTerminationExportFragmentGenerationCommand =>
                    this.BeginFragment(),
                CompleteTenantTerminationExportFragmentGenerationCommand =>
                    this.Complete(),
                FailTenantTerminationExportFragmentGenerationCommand failure =>
                    this.Fail(failure),
                _ => throw new InvalidOperationException(
                    $"Unexpected command {command.GetType().Name}.")
            };
            return Task.FromResult((Result<TResponse>)result);
        }

        private Result<TenantTerminationOwnerWorkStart> BeginOwner()
        {
            order.Add("begin-owner");
            return Result.Success(start);
        }

        private Result<TenantTerminationExportFragmentGenerationStart>
            BeginFragment()
        {
            order.Add("begin-fragment");
            return Result.Success(fragmentStart);
        }

        private Result<TenantTerminationOwnerResultRecorded> Complete()
        {
            this.Completed = true;
            throw new InvalidOperationException(
                "A late export result must not complete the fragment.");
        }

        private Result<Unit> Fail(
            FailTenantTerminationExportFragmentGenerationCommand failure)
        {
            order.Add("fail-fragment");
            this.Failed = failure;
            return Result.Success(Unit.Value);
        }
    }

    private sealed class RecordingReplayStore(List<string> order)
        : ITenantTerminationReplayStore
    {
        public TenantTerminationReplayAttempt? Attempt { get; private set; }

        public Task<TenantTerminationReplayAppendReceipt> AppendAsync(
            TenantTerminationReplayJournalEntry entry,
            CancellationToken cancellationToken)
        {
            order.Add(entry.Kind == TenantTerminationReplayEntryKind.Dispatch
                ? "append-dispatch"
                : "append-result");
            this.Attempt = entry.Kind switch
            {
                TenantTerminationReplayEntryKind.Dispatch =>
                    new(entry.Dispatch!, Result: null),
                TenantTerminationReplayEntryKind.Result =>
                    new(entry.Dispatch!, entry.Result),
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new TenantTerminationReplayAppendReceipt(
                TenantTerminationReplayAppendReceipt.CurrentContractVersion,
                entry.LogicalEntryId,
                entry.Kind,
                new(Sequence: 1, new string('d', 64)),
                Now,
                new string('e', 64)));
        }

        public Task<TenantTerminationReplayAttempt?> ReadAttemptAsync(
            TenantTerminationReplayAttemptCoordinate coordinate,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Attempt?.Dispatch.Coordinate == coordinate
                    ? this.Attempt
                    : null);

        public Task<TenantTerminationReplayStoreReadiness> CheckReadinessAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayIntent?> ReadIntentAsync(
            string tenantId,
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayCheckpoint>
            ReadTrustedCheckpointAsync(
                string tenantId,
                Guid processId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayPage> ReadAfterAsync(
            string tenantId,
            Guid processId,
            TenantTerminationReplayCursor cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class LateGenerator(
        MutableClock clock,
        List<string> order) : ITenantTerminationExportFragmentGenerator
    {
        public Task<TenantTerminationProtectedExportFragment> GenerateAsync(
            TenantTerminationExportFragmentGenerationRequest request,
            CancellationToken cancellationToken)
        {
            order.Add("generate");
            clock.UtcNow = request.AssemblyRequest.DeadlineUtc;
            return Task.FromResult(new TenantTerminationProtectedExportFragment(
                new(
                    request.AssemblyRequest.FrozenRevisionSha256,
                    new(
                        OwnerKey,
                        RecordCount: 0,
                        SelectedProofRevision: 0,
                        ResultingProofRevision: 0,
                        "reservations.tenant-export.completed",
                        Now)),
                "tenant-exports/fragment",
                EncryptedByteLength: 1,
                new string('f', 64),
                EncryptionKeyVersion: 1,
                FormatVersion: 1,
                AvailableAtUtc: Now,
                request.ExpiresAtUtc));
        }
    }

    private sealed class RecordingScheduler : ITenantTerminationTaskScheduler
    {
        public Task EnqueueAsync(
            string tenantId,
            IReadOnlyCollection<TenantTerminationPlannedDispatch> dispatches,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EnqueueExportArtifactAsync(
            string tenantId,
            Guid processId,
            long operationRevision,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EnqueueVerificationAsync(
            string tenantId,
            Guid processId,
            long operationRevision,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class FixedScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}

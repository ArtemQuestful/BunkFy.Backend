namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationVerificationExecutionTests
{
    private const string TenantId = "tenant-a";
    private const string Executor = "system:tenant-termination";
    private static readonly string Digest = new('a', 64);
    private static readonly string CheckpointDigest = new('b', 64);
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_owner_replays_seal_receipt_and_complete_process_once()
    {
        VerificationFixture fixture = await ArrangeAsync();

        await fixture.Task.HandleAsync(
            fixture.Payload,
            fixture.Context,
            CancellationToken.None);

        Assert.Equal(
            TenantTerminationProcessPhase.Completed,
            fixture.Process.Phase);
        Assert.Equal(
            TenantTerminationProcessStatus.Completed,
            fixture.Process.Status);
        Assert.Equal(DataRightsCaseState.Completed, fixture.Case.Status);
        TenantTerminationTerminalReceipt receipt = Assert.Single(
            fixture.Receipts.Items);
        Assert.Equal(2, receipt.OwnerCount);
        Assert.Equal("workspaces", receipt.TerminalOwnerKey);
        Assert.Equal(20, receipt.TerminalOwnerSelectedProofRevision);
        Assert.Equal(21, receipt.TerminalOwnerResultingProofRevision);
        Assert.Equal(4, receipt.ReplayCheckpointSequence);
        Assert.Equal(receipt.Id, fixture.Process.TerminalReceiptId);
        Assert.Equal(1, fixture.Reservations.ExecutionCount);
        Assert.Equal(1, fixture.Workspaces.ExecutionCount);

        await fixture.Task.HandleAsync(
            fixture.Payload,
            fixture.Context,
            CancellationToken.None);

        Assert.Single(fixture.Receipts.Items);
        Assert.Equal(1, fixture.Reservations.ExecutionCount);
        Assert.Equal(1, fixture.Workspaces.ExecutionCount);
    }

    [Fact]
    public async Task Tampered_protected_result_fails_before_owner_replay()
    {
        VerificationFixture fixture = await ArrangeAsync();
        TenantTerminationOwnerWorkItem reservations = fixture.WorkItems.Single(
            item => item.OwnerKey == "reservations");
        fixture.ReplayStore.TamperResult(reservations);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => fixture.Task.HandleAsync(
                fixture.Payload,
                fixture.Context,
                CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationVerificationProofInvalid",
            exception.Message);
        Assert.Empty(fixture.Receipts.Items);
        Assert.Equal(0, fixture.Reservations.ExecutionCount);
        Assert.Equal(0, fixture.Workspaces.ExecutionCount);
    }

    [Fact]
    public async Task Live_owner_drift_and_stale_checkpoint_fail_closed()
    {
        VerificationFixture drifted = await ArrangeAsync();
        drifted.Reservations.LiveResult = drifted.Reservations.Result with
        {
            AffectedCount = drifted.Reservations.Result.AffectedCount + 1
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            drifted.Task.HandleAsync(
                drifted.Payload,
                drifted.Context,
                CancellationToken.None));
        Assert.Empty(drifted.Receipts.Items);

        VerificationFixture stale = await ArrangeAsync();
        stale.ReplayStore.Checkpoint = stale.ReplayStore.Checkpoint with
        {
            FlushedAtUtc = Now.AddMinutes(6)
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stale.Task.HandleAsync(
                stale.Payload,
                stale.Context,
                CancellationToken.None));
        Assert.Empty(stale.Receipts.Items);
        Assert.Equal(TenantTerminationProcessPhase.Verify, stale.Process.Phase);
        Assert.Equal(
            TenantTerminationProcessStatus.Running,
            stale.Process.Status);
    }

    [Fact]
    public async Task Normal_verification_return_at_deadline_cannot_seal_receipt()
    {
        MutableClock clock = new(Now.AddMinutes(8));
        VerificationFixture fixture = await ArrangeAsync(clock);
        fixture.Reservations.OnExecute = request =>
            clock.UtcNow = request.DeadlineUtc;

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(
            () => fixture.Task.HandleAsync(
                fixture.Payload,
                fixture.Context,
                CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationVerificationDeadlineExceeded",
            failure.Message);
        Assert.Empty(fixture.Receipts.Items);
        Assert.Equal(1, fixture.Reservations.ExecutionCount);
        Assert.Equal(0, fixture.Workspaces.ExecutionCount);
        Assert.Equal(
            TenantTerminationProcessPhase.Verify,
            fixture.Process.Phase);
    }

    private static async Task<VerificationFixture> ArrangeAsync(
        ISystemClock? executionClock = null)
    {
        StubContributor reservations = new(
            "reservations",
            destroyDependencies: [],
            selectedProofRevision: 10,
            resultingProofRevision: 11);
        StubContributor workspaces = new(
            "workspaces",
            destroyDependencies: ["reservations"],
            selectedProofRevision: 20,
            resultingProofRevision: 21);
        ITenantTerminationContributor[] contributors =
            [reservations, workspaces];
        Guid caseId = Guid.NewGuid();
        DataRightsCase dataRightsCase = PrepareExecutingCase(caseId);
        TenantTerminationProcess process = PrepareDestroyingProcess(
            contributors,
            caseId);
        TenantTerminationPhasePlanner phasePlanner = new(contributors);
        TenantTerminationOwnerWorkItem[] workItems = phasePlanner
            .PrepareWorkItems(process, Now.AddMinutes(4)).Value.ToArray();
        StubReplayStore replayStore = new();
        foreach (TenantTerminationOwnerWorkItem workItem in workItems)
        {
            Guid ownerRunId = TenantTerminationExecutionIdentity.CreateTaskRunId(
                workItem.Id,
                dispatchSequence: 1);
            Assert.True(workItem.BeginProcessing(
                ownerRunId,
                taskAttempt: 1,
                workItem.Version,
                Now.AddMinutes(5)).IsSuccess);
            StubContributor contributor = Assert.IsType<StubContributor>(
                contributors.Single(item => item.Descriptor.OwnerKey ==
                    workItem.OwnerKey));
            TenantTerminationContributionResult result = contributor.Result;
            Assert.True(workItem.RecordResult(
                TenantTerminationOwnerWorkState.Completed,
                result.ResultCode,
                result.AffectedCount,
                result.RetainedMinimumCount,
                result.RemainingActiveCount,
                result.HoldReviewAtUtc,
                result.SelectedProofRevision,
                result.ResultingProofRevision,
                result.CatalogVersion,
                result.CatalogSha256,
                ownerRunId,
                taskAttempt: 1,
                workItem.Version,
                result.RecordedAtUtc).IsSuccess);
            TenantTerminationContributionRequest request = new(
                workItem.OwnerContractVersion,
                process.ScopeId,
                process.Id,
                process.CaseId,
                process.ApprovalRevision,
                process.OperationRevision,
                process.TerminationEpoch,
                TenantTerminationContributionPhase.Destroy,
                workItem.Id,
                workItem.IdempotencyKey,
                process.PolicyEvidenceSha256,
                Executor,
                Now.AddMinutes(8));
            TenantTerminationExecutionBoundary boundary = contributor
                .Descriptor.PhasePlans.Single(plan =>
                    plan.Phase ==
                        TenantTerminationContributionPhase.Destroy)
                .ExecutionBoundary;
            TenantTerminationReplayDispatch dispatch =
                TenantTerminationReplayDispatch.Create(
                    request,
                    workItem.OwnerKey,
                    workItem.CatalogVersion,
                    workItem.CatalogSha256,
                    boundary,
                    ownerRunId,
                    taskAttempt: 1,
                    Now.AddMinutes(5));
            TenantTerminationReplayResult replayResult =
                TenantTerminationReplayResult.Create(
                    dispatch,
                    result,
                    Now.AddMinutes(7));
            replayStore.Add(new(dispatch, replayResult));
        }

        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Destroy,
            process.OperationRevision,
            process.Version,
            Executor,
            Now.AddMinutes(7)).IsSuccess);
        StubTerminationRepository repository = new(process, workItems);
        TenantTerminationVerificationPlanner verificationPlanner = new(
            contributors);
        BeginTenantTerminationVerificationCommandHandler begin = new(
            DataRightsMutationTestSupport.TenantTermination(repository),
            new FixedClock(Now.AddMinutes(8)));
        Result<TenantTerminationVerificationPhaseStart> phaseStarted =
            await begin.HandleAsync(
                new(process.Id, process.Version, Executor),
                CancellationToken.None);
        Assert.True(phaseStarted.IsSuccess);

        replayStore.Checkpoint = new(
            TenantTerminationReplayCheckpoint.CurrentContractVersion,
            process.Id,
            new(Sequence: 4, CheckpointDigest),
            IntegrityKeyVersion: 2,
            CheckpointDigest,
            Now.AddMinutes(7));
        StubReceiptRepository receipts = new();
        PrepareTenantTerminationVerificationCommandHandler prepare = new(
            repository,
            DataRightsMutationTestSupport.TenantTermination(repository),
            receipts,
            verificationPlanner);
        CompleteTenantTerminationVerificationCommandHandler complete = new(
            repository,
            DataRightsMutationTestSupport.TenantTermination(
                repository,
                new StubCaseRepository(dataRightsCase)),
            receipts,
            verificationPlanner,
            new FixedClock(Now.AddMinutes(9)));
        VerifyTenantTerminationTaskHandler task = new(
            new VerificationCommandDispatcher(prepare, complete),
            replayStore,
            contributors,
            executionClock ?? new FixedClock(Now.AddMinutes(8)),
            new FixedScopeContext(TenantId));
        VerifyTenantTerminationPayload payload = new(
            process.Id,
            phaseStarted.Value.VerificationOperationRevision);
        return new(
            process,
            dataRightsCase,
            workItems,
            reservations,
            workspaces,
            replayStore,
            receipts,
            task,
            payload,
            Context(process.Id, phaseStarted.Value.TaskRunId));
    }

    private static TenantTerminationProcess PrepareDestroyingProcess(
        IReadOnlyCollection<ITenantTerminationContributor> contributors,
        Guid caseId)
    {
        TenantTerminationProcess process = TenantTerminationProcess.Prepare(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            caseId,
            approvalRevision: 3,
            Guid.NewGuid(),
            exportRequested: false,
            Digest,
            "owner:approver",
            Now,
            "creator",
            Now).Value;
        _ = process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Executor,
            Now.AddMinutes(1));
        _ = process.CompleteFreeze(
            process.OperationRevision,
            workspaceFenceRevision: 9,
            Digest,
            contributors.Select(contributor =>
                new TenantTerminationFrozenOwnerDescriptor(
                    contributor.Descriptor.OwnerKey,
                    contributor.Descriptor.ContractVersion,
                    contributor.Descriptor.CatalogVersion,
                    contributor.Descriptor.CatalogSha256)).ToArray(),
            process.Version,
            Executor,
            Now.AddMinutes(2));
        _ = process.BeginPhase(
            TenantTerminationProcessPhase.Destroy,
            process.Version,
            Executor,
            Now.AddMinutes(3));
        return process;
    }

    private static DataRightsCase PrepareExecutingCase(Guid caseId)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.TenantTermination,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.TenantOwner,
            DataRightsRestrictionAction.None).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            caseId,
            TenantId,
            request,
            "owner:requester",
            Now).Value;
        Assert.True(dataRightsCase.PrepareTenantTerminationReview(
            exportRequested: false,
            dataRightsCase.Version,
            "owner:requester",
            Now).IsSuccess);
        Assert.True(dataRightsCase.RecordTenantTerminationDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            Digest,
            dataRightsCase.Version,
            "owner:approver",
            Now).IsSuccess);
        Assert.Equal(3, dataRightsCase.DecisionRevision);
        Assert.True(dataRightsCase.BeginTenantTerminationExecution(
            dataRightsCase.Version,
            Executor,
            Now).IsSuccess);
        return dataRightsCase;
    }

    private static TaskExecutionContext Context(Guid processId, Guid runId) =>
        new(
            runId,
            DataRightsModuleMetadata.Name,
            VerifyTenantTerminationPayload.TaskName,
            DataRightsModuleMetadata.TenantTerminationWorkerGroup,
            "worker-1",
            "node-1",
            attempt: 1,
            scopeId: TenantId,
            correlationId: processId,
            payloadVersion: VerifyTenantTerminationPayload.PayloadVersion);

    private sealed class StubContributor(
        string ownerKey,
        IReadOnlyCollection<string> destroyDependencies,
        long selectedProofRevision,
        long resultingProofRevision) : ITenantTerminationContributor
    {
        public TenantTerminationContributorDescriptor Descriptor { get; } =
            new(
                ownerKey,
                TenantTerminationContract.CurrentVersion,
                [
                    new(TenantTerminationContributionPhase.Export, []),
                    new(
                        TenantTerminationContributionPhase.Destroy,
                        destroyDependencies)
                ],
                MandatoryForProduction: true,
                CatalogVersion: 1,
                CatalogSha256: Digest);
        public TenantTerminationContributionResult Result { get; } =
            new(
                TenantTerminationContributionStatus.Completed,
                $"{ownerKey}.tenant-destroy.completed",
                AffectedCount: 3,
                RetainedMinimumCount: 1,
                RemainingActiveCount: 0,
                HoldReviewAtUtc: null,
                selectedProofRevision,
                resultingProofRevision,
                CatalogVersion: 1,
                CatalogSha256: Digest,
                RecordedAtUtc: Now.AddMinutes(6));
        public TenantTerminationContributionResult? LiveResult { get; set; }
        public Action<TenantTerminationContributionRequest>? OnExecute
        {
            get;
            set;
        }
        public int ExecutionCount { get; private set; }

        public Task<TenantTerminationContributionResult> ExecuteAsync(
            TenantTerminationContributionRequest request,
            CancellationToken cancellationToken)
        {
            this.ExecutionCount++;
            this.OnExecute?.Invoke(request);
            Assert.Equal(
                TenantTerminationContributionPhase.Destroy,
                request.Phase);
            Assert.True(request.DeadlineUtc > Now.AddMinutes(8));
            return Task.FromResult(this.LiveResult ?? this.Result);
        }
    }

    private sealed record VerificationFixture(
        TenantTerminationProcess Process,
        DataRightsCase Case,
        TenantTerminationOwnerWorkItem[] WorkItems,
        StubContributor Reservations,
        StubContributor Workspaces,
        StubReplayStore ReplayStore,
        StubReceiptRepository Receipts,
        VerifyTenantTerminationTaskHandler Task,
        VerifyTenantTerminationPayload Payload,
        TaskExecutionContext Context);

    private sealed class StubCaseRepository(DataRightsCase dataRightsCase)
        : ITenantTerminationCaseRepository
    {
        public Task AddAsync(
            DataRightsCase candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                dataRightsCase.Id == caseId ? dataRightsCase : null);

        public Task<DataRightsCase?> GetActiveAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class VerificationCommandDispatcher(
        PrepareTenantTerminationVerificationCommandHandler prepare,
        CompleteTenantTerminationVerificationCommandHandler complete)
        : ITaskCommandDispatcher
    {
        public async Task<Result<TResponse>> DispatchAsync<
            TCommand,
            TResponse>(
                TaskExecutionContext context,
                TCommand command,
                CancellationToken cancellationToken)
            where TCommand : ICommand<TResponse>
        {
            object result = command switch
            {
                PrepareTenantTerminationVerificationCommand candidate =>
                    await prepare.HandleAsync(
                        candidate,
                        cancellationToken),
                CompleteTenantTerminationVerificationCommand candidate =>
                    await complete.HandleAsync(
                        candidate,
                        cancellationToken),
                _ => throw new NotSupportedException()
            };
            return (Result<TResponse>)result;
        }
    }

    private sealed class StubTerminationRepository(
        TenantTerminationProcess process,
        IReadOnlyList<TenantTerminationOwnerWorkItem> workItems)
        : ITenantTerminationRepository
    {
        public Task AddProcessAsync(
            TenantTerminationProcess candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddOwnerWorkItemAsync(
            TenantTerminationOwnerWorkItem workItem,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationProcess?> GetProcessAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult<TenantTerminationProcess?>(
                process.Id == processId ? process : null);

        public Task<TenantTerminationProcess?> GetActiveProcessAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<TenantTerminationProcess?>(process);

        public Task<TenantTerminationProcess?> GetProcessByIdempotencyKeyAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<TenantTerminationProcess?>(
                process.IdempotencyKey == idempotencyKey ? process : null);

        public Task<TenantTerminationOwnerWorkItem?> GetOwnerWorkItemAsync(
            Guid processId,
            TenantTerminationOwnerPhase phase,
            string ownerKey,
            long operationRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(workItems.SingleOrDefault(item =>
                item.ProcessId == processId &&
                item.Phase == phase &&
                item.OwnerKey == ownerKey &&
                item.OperationRevision == operationRevision));

        public Task<IReadOnlyList<TenantTerminationOwnerWorkItem>>
            ListOwnerWorkItemsAsync(
                Guid processId,
                TenantTerminationOwnerPhase phase,
                long operationRevision,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TenantTerminationOwnerWorkItem>>(
                workItems.Where(item =>
                    item.ProcessId == processId &&
                    item.Phase == phase &&
                    item.OperationRevision == operationRevision).ToArray());
    }

    private sealed class StubReceiptRepository
        : ITenantTerminationTerminalReceiptRepository
    {
        public List<TenantTerminationTerminalReceipt> Items { get; } = [];

        public Task AddAsync(
            TenantTerminationTerminalReceipt receipt,
            CancellationToken cancellationToken)
        {
            this.Items.Add(receipt);
            return Task.CompletedTask;
        }

        public Task<TenantTerminationTerminalReceipt?> GetAsync(
            Guid receiptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(item =>
                item.Id == receiptId));

        public Task<TenantTerminationTerminalReceipt?>
            GetByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(item =>
                item.IdempotencyKey == idempotencyKey));

        public Task<TenantTerminationTerminalReceipt?> GetByProcessAsync(
            Guid processId,
            long verificationOperationRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(item =>
                item.ProcessId == processId &&
                item.VerificationOperationRevision ==
                    verificationOperationRevision));
    }

    private sealed class StubReplayStore : ITenantTerminationReplayStore
    {
        private readonly Dictionary<
            TenantTerminationReplayAttemptCoordinate,
            TenantTerminationReplayAttempt> attempts = [];

        public TenantTerminationReplayCheckpoint Checkpoint { get; set; } =
            null!;

        public void Add(TenantTerminationReplayAttempt attempt) =>
            this.attempts[attempt.Dispatch.Coordinate] = attempt;

        public void TamperResult(TenantTerminationOwnerWorkItem workItem)
        {
            TenantTerminationReplayAttemptCoordinate coordinate =
                this.attempts.Keys.Single(candidate =>
                    candidate.WorkItemId == workItem.Id);
            TenantTerminationReplayAttempt attempt =
                this.attempts[coordinate];
            this.attempts[coordinate] = attempt with
            {
                Result = attempt.Result! with
                {
                    ResultSha256 = new string('c', 64)
                }
            };
        }

        public Task<TenantTerminationReplayAttempt?> ReadAttemptAsync(
            TenantTerminationReplayAttemptCoordinate coordinate,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.attempts.GetValueOrDefault(coordinate));

        public Task<TenantTerminationReplayIntent?> ReadIntentAsync(
            string tenantId,
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult<TenantTerminationReplayIntent?>(null);

        public Task<TenantTerminationReplayCheckpoint>
            ReadTrustedCheckpointAsync(
                string tenantId,
                Guid processId,
                CancellationToken cancellationToken) =>
            Task.FromResult(this.Checkpoint);

        public Task<TenantTerminationReplayStoreReadiness> CheckReadinessAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayAppendReceipt> AppendAsync(
            TenantTerminationReplayJournalEntry entry,
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

    private sealed class FixedClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
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

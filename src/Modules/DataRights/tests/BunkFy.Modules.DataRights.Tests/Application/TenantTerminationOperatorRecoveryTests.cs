namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Production;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationOperatorRecoveryTests
{
    private const string TenantId = "tenant-a";
    private const string Approver = "operator:approver";
    private const string Executor = "system:tenant-termination";
    private static readonly string Digest = new('a', 64);
    private static readonly string CatalogDigest = new('b', 64);
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Retry_resumes_only_failed_work_in_the_same_operation()
    {
        StubContributor contributor = new(
            TenantTerminationContributionPhase.Freeze);
        TenantTerminationProcess process = PrepareProcess(
            Guid.NewGuid(),
            exportRequested: false);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Executor,
            Now.AddMinutes(1)).IsSuccess);
        TenantTerminationPhasePlanner planner = new([contributor]);
        TenantTerminationOwnerWorkItem workItem = Assert.Single(
            planner.PrepareWorkItems(process, Now.AddMinutes(1)).Value);
        Assert.True(workItem.BeginProcessing(
            Guid.NewGuid(),
            taskAttempt: 1,
            workItem.Version,
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(workItem.RecordResult(
            TenantTerminationOwnerWorkState.Failed,
            "workspaces.retry-exhausted",
            affectedCount: 0,
            retainedMinimumCount: 0,
            remainingActiveCount: 1,
            holdReviewAtUtc: null,
            selectedProofRevision: null,
            resultingProofRevision: null,
            workItem.CatalogVersion,
            workItem.CatalogSha256,
            workItem.TaskRunId!.Value,
            workItem.LastTaskAttempt,
            workItem.Version,
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(process.RecordFailed(
            process.Phase,
            process.OperationRevision,
            TenantTerminationPhaseEvaluator.FailedOutcomeCode,
            process.Version,
            Executor,
            Now.AddMinutes(3)).IsSuccess);
        long operationRevision = process.OperationRevision;
        StubRepository repository = new(process, [workItem]);
        RecordingSignal signal = new();
        RetryTenantTerminationCommandHandler handler = new(
            repository,
            DataRightsMutationTestSupport.TenantTermination(repository),
            new InMemoryTenantTerminationExportArtifactRepository(),
            new InMemoryTenantTerminationExportFragmentRepository(),
            planner,
            signal,
            new FixedClock(Now.AddMinutes(4)));

        Result<TenantTerminationProcessDto> result = await handler.HandleAsync(
            new(
                process.Id,
                process.Version,
                "operator:recovery"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TenantTerminationStatus.Running, result.Value.Status);
        Assert.Equal(operationRevision, result.Value.OperationRevision);
        Assert.Equal(TenantTerminationOwnerWorkState.Prepared, workItem.State);
        Assert.Null(workItem.TaskRunId);
        Assert.Equal(1, signal.Count);
    }

    [Fact]
    public async Task Cancellation_request_is_idempotent_while_restore_is_pending()
    {
        TenantTerminationProcess process = PrepareExportPendingProcess(
            Guid.NewGuid());
        StubRepository repository = new(process, []);
        RecordingSignal signal = new();
        RequestTenantTerminationCancellationCommandHandler handler = new(
            DataRightsMutationTestSupport.TenantTermination(repository),
            signal,
            new FixedClock(Now.AddMinutes(4)));
        RequestTenantTerminationCancellationCommand command = new(
            process.Id,
            process.Version,
            "operator:canceller");

        Result<TenantTerminationProcessDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<TenantTerminationProcessDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(TenantTerminationPhase.Restore, first.Value.Phase);
        Assert.Equal(TenantTerminationStatus.Pending, first.Value.Status);
        Assert.Equal(1, signal.Count);
        Assert.Equal(first.Value.Version, replay.Value.Version);
    }

    [Fact]
    public async Task Restore_completion_atomically_cancels_the_operator_case()
    {
        DataRightsCase dataRightsCase = PrepareExecutingCase();
        TenantTerminationProcess process = PrepareExportPendingProcess(
            dataRightsCase.Id);
        Assert.True(process.RequestCancellation(
            process.Version,
            "operator:canceller",
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Restore,
            process.Version,
            Executor,
            Now.AddMinutes(4)).IsSuccess);
        StubContributor contributor = new(
            TenantTerminationContributionPhase.Restore);
        TenantTerminationPhasePlanner planner = new([contributor]);
        TenantTerminationOwnerWorkItem workItem = Assert.Single(
            planner.PrepareWorkItems(process, Now.AddMinutes(4)).Value);
        Complete(workItem, Now.AddMinutes(5));
        StubRepository repository = new(process, [workItem]);
        StubCaseRepository cases = new(dataRightsCase);
        ReconcileTenantTerminationPhaseCommandHandler handler = new(
            repository,
            DataRightsMutationTestSupport.TenantTermination(
                repository,
                cases),
            new TenantTerminationPhaseEvaluator(planner),
            [contributor],
            new RecordingSignal(),
            new FixedClock(Now.AddMinutes(6)));

        Result<TenantTerminationPhaseReconciliation> result =
            await handler.HandleAsync(
                new(
                    process.Id,
                    process.Phase,
                    process.OperationRevision,
                    process.Version,
                    Executor),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            TenantTerminationProcessStatus.Cancelled,
            process.Status);
        Assert.Equal(DataRightsCaseState.Canceled, dataRightsCase.Status);
    }

    [Fact]
    public async Task Status_returns_bounded_case_process_and_owner_progress()
    {
        DataRightsCase dataRightsCase = PrepareExecutingCase();
        TenantTerminationProcess process = PrepareProcess(
            dataRightsCase.Id,
            exportRequested: true);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Executor,
            Now.AddMinutes(1)).IsSuccess);
        StubContributor contributor = new(
            TenantTerminationContributionPhase.Freeze);
        TenantTerminationOwnerWorkItem workItem = Assert.Single(
            new TenantTerminationPhasePlanner([contributor])
                .PrepareWorkItems(process, Now.AddMinutes(1)).Value);
        Assert.True(process.CompleteFreeze(
            process.OperationRevision,
            workspaceFenceRevision: 1,
            Digest,
            [new("workspaces", 1, 1, Digest)],
            process.Version,
            Executor,
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Export,
            process.Version,
            Executor,
            Now.AddMinutes(3)).IsSuccess);
        TenantTerminationExportArtifact artifact =
            TenantTerminationExportArtifact.Request(
                TenantTerminationExecutionIdentity.CreateExportArtifactId(
                    process.Id,
                    process.OperationRevision),
                process.ScopeId,
                process.Id,
                process.CaseId,
                process.ApprovalRevision,
                process.FreezeOperationRevision!.Value,
                process.OperationRevision,
                process.TerminationEpoch,
                TenantTerminationExecutionIdentity
                    .CreateExportArtifactIdempotencyKey(
                        process.Id,
                        process.OperationRevision),
                process.FrozenRevisionSha256!,
                process.PolicyEvidenceSha256,
                expectedFragmentCount: 1,
                Digest,
                Now.AddMinutes(4),
                Now.AddHours(24)).Value;
        InMemoryTenantTerminationExportArtifactRepository artifacts = new();
        await artifacts.AddAsync(artifact, CancellationToken.None);
        StubCaseRepository cases = new(dataRightsCase);
        StubStatusRepository status = new(process, [workItem]);
        GetTenantTerminationOperatorStatusQueryHandler handler = new(
            cases,
            status,
            artifacts);

        Result<TenantTerminationOperatorStatusDto> result =
            await handler.HandleAsync(
                new GetTenantTerminationOperatorStatusQuery(
                    dataRightsCase.Id),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(dataRightsCase.Id, result.Value.Case.Id);
        Assert.Equal(process.Id, result.Value.Process!.Id);
        TenantTerminationOwnerWorkItemDto owner = Assert.Single(
            result.Value.OwnerWorkItems);
        Assert.Equal("workspaces", owner.OwnerKey);
        Assert.Equal(
            TenantTerminationOwnerWorkStatus.Prepared,
            owner.Status);
        Assert.Equal(
            artifact.Id,
            result.Value.ExportHandoff!.ArtifactId);
        Assert.False(result.Value.ExportHandoff.Confirmed);
    }

    [Fact]
    public async Task Status_fails_closed_when_case_and_process_disagree()
    {
        DataRightsCase dataRightsCase = PrepareExecutingCase();
        TenantTerminationProcess process = PrepareProcess(
            dataRightsCase.Id,
            exportRequested: false);
        GetTenantTerminationOperatorStatusQueryHandler handler = new(
            new StubCaseRepository(dataRightsCase),
            new StubStatusRepository(process, []),
            new InMemoryTenantTerminationExportArtifactRepository());

        Result<TenantTerminationOperatorStatusDto> result =
            await handler.HandleAsync(
                new GetTenantTerminationOperatorStatusQuery(
                    dataRightsCase.Id),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors
                .TenantTerminationExecutionStateInvalid,
            result.Error);
    }

    [Fact]
    public async Task Recovery_reconstructs_an_orphaned_protected_start()
    {
        DataRightsCase dataRightsCase = PrepareRecoveryCase(
            executing: false);
        Guid processId = Guid.NewGuid();
        TenantTerminationReplayIntent intent = RecoveryIntent(
            dataRightsCase,
            processId,
            Now.AddMinutes(2));
        StubRepository processes = new(process: null, []);
        RecordingSignal signal = new();
        RecordingScheduler scheduler = new();
        RecoverTenantTerminationCommandHandler handler = RecoveryHandler(
            dataRightsCase,
            processes,
            new StubReplayStore(intent),
            signal,
            scheduler);

        Result<TenantTerminationStartDto> result = await handler.HandleAsync(
            RecoveryCommand(
                dataRightsCase,
                processId,
                expectedProcessVersion: null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TenantTerminationStatus.Pending, result.Value.Process.Status);
        Assert.Equal(intent.ExecutionStartedAtUtc,
            result.Value.Case.ExecutionStartedAtUtc);
        Assert.Equal(Executor, processes.Process!.CreatedBy);
        Assert.Equal(1, processes.AddCount);
        Assert.Equal(1, signal.Count);
        Assert.Equal(0, scheduler.VerificationCount);
    }

    [Fact]
    public async Task Recovery_never_reconstructs_without_a_protected_intent()
    {
        DataRightsCase dataRightsCase = PrepareRecoveryCase(
            executing: false);
        StubRepository processes = new(process: null, []);
        RecoverTenantTerminationCommandHandler handler = RecoveryHandler(
            dataRightsCase,
            processes,
            new StubReplayStore(intent: null),
            new RecordingSignal(),
            new RecordingScheduler());

        Result<TenantTerminationStartDto> result = await handler.HandleAsync(
            RecoveryCommand(
                dataRightsCase,
                Guid.NewGuid(),
                expectedProcessVersion: null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors
                .TenantTerminationReplayIntentInvalid,
            result.Error);
        Assert.Equal(0, processes.AddCount);
    }

    [Fact]
    public async Task Recovery_resignals_an_active_non_terminal_process()
    {
        RecoveryState state = PrepareRecoveryState();
        RecordingSignal signal = new();
        RecordingScheduler scheduler = new();
        RecoverTenantTerminationCommandHandler handler = RecoveryHandler(
            state.Case,
            new StubRepository(state.Process, []),
            new StubReplayStore(state.Intent),
            signal,
            scheduler);

        Result<TenantTerminationStartDto> result = await handler.HandleAsync(
            RecoveryCommand(
                state.Case,
                state.Process.Id,
                state.Process.Version),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, signal.Count);
        Assert.Equal(0, scheduler.VerificationCount);
    }

    [Fact]
    public async Task Recovery_requires_explicit_retry_for_a_failed_process()
    {
        RecoveryState state = PrepareRecoveryState();
        Assert.True(state.Process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            state.Process.Version,
            Executor,
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(state.Process.RecordFailed(
            state.Process.Phase,
            state.Process.OperationRevision,
            "workspaces.failed",
            state.Process.Version,
            Executor,
            Now.AddMinutes(4)).IsSuccess);
        RecordingSignal signal = new();
        RecordingScheduler scheduler = new();
        RecoverTenantTerminationCommandHandler handler = RecoveryHandler(
            state.Case,
            new StubRepository(state.Process, []),
            new StubReplayStore(state.Intent),
            signal,
            scheduler);

        Result<TenantTerminationStartDto> result = await handler.HandleAsync(
            RecoveryCommand(
                state.Case,
                state.Process.Id,
                state.Process.Version),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors
                .TenantTerminationRecoveryRequiresRetry,
            result.Error);
        Assert.Equal(0, signal.Count);
        Assert.Equal(0, scheduler.VerificationCount);
    }

    [Fact]
    public async Task Recovery_reenqueues_central_verification_directly()
    {
        RecoveryState state = PrepareRecoveryState();
        Assert.True(state.Process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            state.Process.Version,
            Executor,
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(state.Process.CompleteFreeze(
            state.Process.OperationRevision,
            workspaceFenceRevision: 1,
            Digest,
            [new("workspaces", 1, 1, Digest)],
            state.Process.Version,
            Executor,
            Now.AddMinutes(4)).IsSuccess);
        Assert.True(state.Process.BeginPhase(
            TenantTerminationProcessPhase.Destroy,
            state.Process.Version,
            Executor,
            Now.AddMinutes(5)).IsSuccess);
        Assert.True(state.Process.CompletePhase(
            TenantTerminationProcessPhase.Destroy,
            state.Process.OperationRevision,
            state.Process.Version,
            Executor,
            Now.AddMinutes(6)).IsSuccess);
        Assert.True(state.Process.BeginPhase(
            TenantTerminationProcessPhase.Verify,
            state.Process.Version,
            Executor,
            Now.AddMinutes(7)).IsSuccess);
        RecordingSignal signal = new();
        RecordingScheduler scheduler = new();
        RecoverTenantTerminationCommandHandler handler = RecoveryHandler(
            state.Case,
            new StubRepository(state.Process, []),
            new StubReplayStore(state.Intent),
            signal,
            scheduler);

        Result<TenantTerminationStartDto> result = await handler.HandleAsync(
            RecoveryCommand(
                state.Case,
                state.Process.Id,
                state.Process.Version),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, signal.Count);
        Assert.Equal(1, scheduler.VerificationCount);
        Assert.Equal(state.Process.Id, scheduler.ProcessId);
        Assert.Equal(
            state.Process.OperationRevision,
            scheduler.OperationRevision);
    }

    private static TenantTerminationProcess PrepareExportPendingProcess(
        Guid caseId)
    {
        TenantTerminationProcess process = PrepareProcess(
            caseId,
            exportRequested: true);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Executor,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(process.CompleteFreeze(
            process.OperationRevision,
            workspaceFenceRevision: 1,
            Digest,
            [new("workspaces", 1, 1, Digest)],
            process.Version,
            Executor,
            Now.AddMinutes(2)).IsSuccess);
        return process;
    }

    private static RecoverTenantTerminationCommandHandler RecoveryHandler(
        DataRightsCase dataRightsCase,
        StubRepository processes,
        StubReplayStore replayStore,
        RecordingSignal signal,
        RecordingScheduler scheduler)
    {
        StubCaseRepository cases = new(dataRightsCase);
        FixedScopeContext scope = new();
        FixedClock clock = new(Now.AddMinutes(10));
        TenantTerminationStartCoordinator coordinator = new(
            DataRightsMutationTestSupport.TenantTermination(
                processes,
                cases),
            processes,
            replayStore,
            new StubProductionCatalog(),
            new StubRequiredOwners(),
            signal,
            scope,
            clock);
        return new(
            coordinator,
            processes,
            signal,
            scheduler,
            scope,
            clock);
    }

    private static RecoverTenantTerminationCommand RecoveryCommand(
        DataRightsCase dataRightsCase,
        Guid processId,
        long? expectedProcessVersion) =>
        new(
            dataRightsCase.Id,
            processId,
            RecoveryEvidence(),
            dataRightsCase.Version,
            expectedProcessVersion,
            "operator:successor");

    private static RecoveryState PrepareRecoveryState()
    {
        DataRightsCase dataRightsCase = PrepareRecoveryCase(executing: true);
        Guid processId = Guid.NewGuid();
        TenantTerminationProcess process = TenantTerminationProcess.Prepare(
            processId,
            TenantId,
            TenantTerminationExecutionIdentity
                .CreateProcessIdempotencyKey(processId),
            dataRightsCase.Id,
            dataRightsCase.DecisionRevision!.Value,
            TenantTerminationExecutionIdentity
                .CreateTerminationEpoch(processId),
            exportRequested: false,
            dataRightsCase.TenantTerminationPolicyEvidenceSha256!,
            dataRightsCase.DecidedBy!,
            dataRightsCase.DecidedAtUtc!.Value,
            Executor,
            dataRightsCase.ExecutionStartedAtUtc!.Value).Value;
        return new(
            dataRightsCase,
            process,
            RecoveryIntent(
                dataRightsCase,
                processId,
                dataRightsCase.ExecutionStartedAtUtc.Value));
    }

    private static DataRightsCase PrepareRecoveryCase(bool executing)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.TenantTermination,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.TenantOwner,
            DataRightsRestrictionAction.None).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "operator:requester",
            Now).Value;
        Assert.True(dataRightsCase.PrepareTenantTerminationReview(
            exportRequested: false,
            dataRightsCase.Version,
            "operator:requester",
            Now).IsSuccess);
        Assert.True(dataRightsCase.RecordTenantTerminationDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            RecoveryEvidence().ComputeSha256(),
            dataRightsCase.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);
        if (executing)
        {
            Assert.True(dataRightsCase.BeginTenantTerminationExecution(
                dataRightsCase.Version,
                Executor,
                Now.AddMinutes(2)).IsSuccess);
        }

        return dataRightsCase;
    }

    private static TenantTerminationReplayIntent RecoveryIntent(
        DataRightsCase dataRightsCase,
        Guid processId,
        DateTimeOffset executionStartedAtUtc) =>
        TenantTerminationReplayIntent.Create(
            TenantId,
            processId,
            dataRightsCase.Id,
            DataRightsRequesterRelationship.TenantOwner,
            dataRightsCase.CreatedBy,
            dataRightsCase.CreatedAtUtc,
            exportRequested: false,
            dataRightsCase.DecisionRevision!.Value,
            dataRightsCase.DecidedBy!,
            dataRightsCase.DecidedAtUtc!.Value,
            dataRightsCase.TenantTerminationPolicyEvidenceSha256!,
            CatalogDigest,
            TenantTerminationExecutionIdentity
                .CreateProcessIdempotencyKey(processId),
            TenantTerminationExecutionIdentity
                .CreateTerminationEpoch(processId),
            Executor,
            executionStartedAtUtc,
            executionStartedAtUtc);

    private static TenantTerminationApprovalEvidence RecoveryEvidence() =>
        new(
            "approval:recovery-1",
            CatalogDigest,
            "backup:recovery-1",
            "restore:recovery-1",
            "assurance:recovery-1");

    private static TenantTerminationProcess PrepareProcess(
        Guid caseId,
        bool exportRequested) =>
        TenantTerminationProcess.Prepare(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            caseId,
            approvalRevision: 3,
            Guid.NewGuid(),
            exportRequested,
            Digest,
            Approver,
            Now,
            Executor,
            Now).Value;

    private static DataRightsCase PrepareExecutingCase()
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.TenantTermination,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.TenantOwner,
            DataRightsRestrictionAction.None).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "operator:requester",
            Now).Value;
        Assert.True(dataRightsCase.PrepareTenantTerminationReview(
            exportRequested: true,
            dataRightsCase.Version,
            "operator:requester",
            Now).IsSuccess);
        Assert.True(dataRightsCase.RecordTenantTerminationDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            Digest,
            dataRightsCase.Version,
            Approver,
            Now).IsSuccess);
        Assert.True(dataRightsCase.BeginTenantTerminationExecution(
            dataRightsCase.Version,
            Executor,
            Now).IsSuccess);
        return dataRightsCase;
    }

    private static void Complete(
        TenantTerminationOwnerWorkItem workItem,
        DateTimeOffset completedAtUtc)
    {
        Assert.True(workItem.BeginProcessing(
            Guid.NewGuid(),
            taskAttempt: 1,
            workItem.Version,
            completedAtUtc).IsSuccess);
        Assert.True(workItem.RecordResult(
            TenantTerminationOwnerWorkState.Completed,
            "workspaces.restore.completed",
            affectedCount: 1,
            retainedMinimumCount: 0,
            remainingActiveCount: 0,
            holdReviewAtUtc: null,
            selectedProofRevision: 1,
            resultingProofRevision: 2,
            workItem.CatalogVersion,
            workItem.CatalogSha256,
            workItem.TaskRunId!.Value,
            workItem.LastTaskAttempt,
            workItem.Version,
            completedAtUtc).IsSuccess);
    }

    private sealed class StubContributor(
        TenantTerminationContributionPhase phase)
        : ITenantTerminationContributor
    {
        public TenantTerminationContributorDescriptor Descriptor { get; } =
            new(
                "workspaces",
                TenantTerminationContract.CurrentVersion,
                [new(phase, [])],
                MandatoryForProduction: true,
                CatalogVersion: 1,
                CatalogSha256: Digest);

        public Task<TenantTerminationContributionResult> ExecuteAsync(
            TenantTerminationContributionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubRepository(
        TenantTerminationProcess? process,
        IReadOnlyList<TenantTerminationOwnerWorkItem> workItems)
        : ITenantTerminationRepository
    {
        private TenantTerminationProcess? process = process;

        public int AddCount { get; private set; }
        public TenantTerminationProcess? Process => this.process;

        public Task AddProcessAsync(
            TenantTerminationProcess candidate,
            CancellationToken cancellationToken)
        {
            this.process = candidate;
            this.AddCount++;
            return Task.CompletedTask;
        }

        public Task AddOwnerWorkItemAsync(
            TenantTerminationOwnerWorkItem workItem,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationProcess?> GetProcessAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.process?.Id == processId ? this.process : null);

        public Task<TenantTerminationProcess?> GetActiveProcessAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(this.process);

        public Task<TenantTerminationProcess?>
            GetProcessByIdempotencyKeyAsync(
                Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.process?.IdempotencyKey == idempotencyKey
                    ? this.process
                    : null);

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

    private sealed class StubReplayStore(TenantTerminationReplayIntent? intent)
        : ITenantTerminationReplayStore
    {
        public Task<TenantTerminationReplayIntent?> ReadIntentAsync(
            string tenantId,
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                intent?.TenantId == tenantId && intent.ProcessId == processId
                    ? intent
                    : null);

        public Task<TenantTerminationReplayStoreReadiness>
            CheckReadinessAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayAppendReceipt> AppendAsync(
            TenantTerminationReplayJournalEntry entry,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayAttempt?> ReadAttemptAsync(
            TenantTerminationReplayAttemptCoordinate coordinate,
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

    private sealed class StubProductionCatalog
        : ITenantTerminationProductionCatalog
    {
        public Result<TenantTerminationProductionCatalogEvidence> Validate(
            IReadOnlyCollection<string> requiredOwnerKeys) =>
            Result.Success(new TenantTerminationProductionCatalogEvidence(
                OwnerCount: 1,
                ExportOwnerCount: 1,
                TerminalOwnerKey: "workspaces",
                CatalogDigest));
    }

    private sealed class StubRequiredOwners
        : ITenantTerminationRequiredOwnerCatalog
    {
        public IReadOnlyCollection<string> RequiredOwnerKeys { get; } =
            ["workspaces"];
    }

    private sealed class RecordingScheduler : ITenantTerminationTaskScheduler
    {
        public int VerificationCount { get; private set; }
        public Guid? ProcessId { get; private set; }
        public long? OperationRevision { get; private set; }

        public Task EnqueueAsync(
            string tenantId,
            IReadOnlyCollection<TenantTerminationPlannedDispatch> dispatches,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EnqueueExportArtifactAsync(
            string tenantId,
            Guid processId,
            long operationRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EnqueueVerificationAsync(
            string tenantId,
            Guid processId,
            long operationRevision,
            CancellationToken cancellationToken)
        {
            this.VerificationCount++;
            this.ProcessId = processId;
            this.OperationRevision = operationRevision;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedScopeContext : IScopeContext
    {
        public string? ScopeId => TenantId;
        public bool IsEnabled => true;
    }

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
            Task.FromResult<DataRightsCase?>(dataRightsCase);
    }

    private sealed class StubStatusRepository(
        TenantTerminationProcess process,
        IReadOnlyList<TenantTerminationOwnerWorkItem> workItems)
        : ITenantTerminationOperatorStatusRepository
    {
        public Task<TenantTerminationProcess?> GetProcessByCaseIdAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(process.CaseId == caseId ? process : null);

        public Task<IReadOnlyList<TenantTerminationOwnerWorkItem>>
            ListOwnerWorkItemsAsync(
                Guid processId,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                process.Id == processId ? workItems : []);
    }

    private sealed class RecordingSignal
        : ITenantTerminationCoordinationSignal
    {
        public int Count { get; private set; }

        public Task<bool> EnqueueAsync(
            TenantTerminationProcess process,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            this.Count++;
            return Task.FromResult(true);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed record RecoveryState(
        DataRightsCase Case,
        TenantTerminationProcess Process,
        TenantTerminationReplayIntent Intent);
}

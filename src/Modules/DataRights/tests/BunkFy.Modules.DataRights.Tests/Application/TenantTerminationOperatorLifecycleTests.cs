namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Production;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationOperatorLifecycleTests
{
    private static readonly Guid CaseId =
        Guid.Parse("72000000-0000-0000-0000-000000000001");
    private static readonly Guid ProcessId =
        Guid.Parse("72000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset RequestedAt =
        new(2026, 8, 4, 19, 0, 0, TimeSpan.Zero);
    private static readonly string CatalogSha256 = new('a', 64);

    [Fact]
    public async Task Request_enters_review_and_exact_replay_is_idempotent()
    {
        StubCaseRepository repository = new();
        RequestTenantTerminationCommandHandler handler = new(
            repository,
            DataRightsMutationTestSupport.OperationLock,
            new FixedScopeContext(),
            new FixedClock(RequestedAt));
        RequestTenantTerminationCommand command = new(
            CaseId,
            ExportRequested: true,
            DataRightsRequesterRelationship.TenantOwner,
            "operator:requester");

        Result<TenantTerminationCaseDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<TenantTerminationCaseDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(DataRightsCaseStatus.ReviewRequired, first.Value.Status);
        Assert.True(first.Value.ExportRequested);
        Assert.Equal(2, first.Value.Version);
        Assert.Equal(CaseId, replay.Value.Id);
        Assert.Equal(1, repository.AddCount);
    }

    [Fact]
    public async Task A_second_active_request_is_rejected()
    {
        StubCaseRepository repository = new(PrepareReviewCase());
        RequestTenantTerminationCommandHandler handler = new(
            repository,
            DataRightsMutationTestSupport.OperationLock,
            new FixedScopeContext(),
            new FixedClock(RequestedAt.AddMinutes(1)));

        Result<TenantTerminationCaseDto> result = await handler.HandleAsync(
            new(
                Guid.NewGuid(),
                ExportRequested: false,
                DataRightsRequesterRelationship.ControllerInitiated,
                "operator:requester"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors.TenantTerminationActiveCaseExists,
            result.Error);
        Assert.Equal(0, repository.AddCount);
    }

    [Fact]
    public async Task Approval_binds_the_live_catalog_and_persists_only_its_digest()
    {
        DataRightsCase dataRightsCase = PrepareReviewCase();
        StubCaseRepository repository = new(dataRightsCase);
        TenantTerminationApprovalEvidence evidence = ApprovalEvidence();
        DecideTenantTerminationCommandHandler handler = DecisionHandler(
            repository,
            CatalogSha256);
        DecideTenantTerminationCommand command = new(
            dataRightsCase.Id,
            DataRightsDecisionOutcome.Approved,
            DataRightsDecisionReason.RequestValidated,
            evidence,
            dataRightsCase.Version,
            "operator:approver");

        Result<TenantTerminationCaseDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<TenantTerminationCaseDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(DataRightsCaseStatus.Approved, first.Value.Status);
        Assert.Equal(
            evidence.ComputeSha256(),
            dataRightsCase.TenantTerminationPolicyEvidenceSha256);
        Assert.DoesNotContain(
            evidence.ApprovalReference,
            dataRightsCase.TenantTerminationPolicyEvidenceSha256,
            StringComparison.Ordinal);
        Assert.Equal(first.Value.Version, replay.Value.Version);
    }

    [Fact]
    public async Task Approval_with_a_stale_owner_catalog_fails_closed()
    {
        DataRightsCase dataRightsCase = PrepareReviewCase();
        StubCaseRepository repository = new(dataRightsCase);

        Result<TenantTerminationCaseDto> result = await DecisionHandler(
                repository,
                new string('b', 64))
            .HandleAsync(
                new(
                    dataRightsCase.Id,
                    DataRightsDecisionOutcome.Approved,
                    DataRightsDecisionReason.RequestValidated,
                    ApprovalEvidence(),
                    dataRightsCase.Version,
                    "operator:approver"),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors
                .TenantTerminationApprovalEvidenceInvalid,
            result.Error);
        Assert.Equal(DataRightsCaseState.ReviewRequired, dataRightsCase.Status);
    }

    [Fact]
    public async Task Start_protects_intent_before_database_and_outbox_changes()
    {
        DataRightsCase dataRightsCase = PrepareApprovedCase();
        List<string> order = [];
        StubProcessRepository processRepository = new(order);
        StubReplayStore replayStore = new(order);
        RecordingSignal signal = new(order);
        StartTenantTerminationCommandHandler handler = StartHandler(
            new StubCaseRepository(dataRightsCase),
            processRepository,
            replayStore,
            signal,
            RequestedAt.AddMinutes(2));

        Result<TenantTerminationStartDto> result = await handler.HandleAsync(
            StartCommand(dataRightsCase),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["intent", "process", "signal"], order);
        Assert.Equal(DataRightsCaseStatus.Executing, result.Value.Case.Status);
        Assert.Equal(TenantTerminationStatus.Pending, result.Value.Process.Status);
        Assert.Equal(TenantTerminationPhase.Freeze, result.Value.Process.Phase);
        Assert.NotNull(replayStore.Intent);
        Assert.Equal(1, replayStore.AppendCount);
    }

    [Fact]
    public async Task Late_cancellation_after_intent_append_stops_and_exact_retry_recovers()
    {
        DataRightsCase dataRightsCase = PrepareApprovedCase();
        List<string> order = [];
        using CancellationTokenSource source = new();
        StubProcessRepository processRepository = new(order);
        StubReplayStore replayStore = new(order, source.Cancel);
        RecordingSignal signal = new(order);
        StartTenantTerminationCommandHandler handler = StartHandler(
            new StubCaseRepository(dataRightsCase),
            processRepository,
            replayStore,
            signal,
            RequestedAt.AddMinutes(2));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(StartCommand(dataRightsCase), source.Token));

        Assert.Equal(["intent"], order);
        Assert.Equal(0, processRepository.AddCount);
        Assert.Equal(0, signal.Count);

        Result<TenantTerminationStartDto> recovered =
            await handler.HandleAsync(
                StartCommand(dataRightsCase),
                CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(["intent", "process", "signal"], order);
        Assert.Equal(1, replayStore.AppendCount);
        Assert.Equal(1, processRepository.AddCount);
        Assert.Equal(1, signal.Count);
    }

    [Fact]
    public async Task Protected_intent_recovers_a_failed_database_start_exactly()
    {
        List<string> firstOrder = [];
        StubReplayStore replayStore = new(firstOrder);
        DataRightsCase firstCase = PrepareApprovedCase();
        StubProcessRepository failingProcesses = new(firstOrder)
        {
            ThrowOnAdd = true
        };
        StartTenantTerminationCommandHandler firstHandler = StartHandler(
            new StubCaseRepository(firstCase),
            failingProcesses,
            replayStore,
            new RecordingSignal(firstOrder),
            RequestedAt.AddMinutes(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstHandler.HandleAsync(
                StartCommand(firstCase),
                CancellationToken.None));

        TenantTerminationReplayIntent protectedIntent =
            Assert.IsType<TenantTerminationReplayIntent>(replayStore.Intent);
        DataRightsCase recoveredCase = PrepareApprovedCase();
        List<string> recoveryOrder = [];
        StubProcessRepository recoveredProcesses = new(recoveryOrder);
        StartTenantTerminationCommandHandler recoveryHandler = StartHandler(
            new StubCaseRepository(recoveredCase),
            recoveredProcesses,
            replayStore,
            new RecordingSignal(recoveryOrder),
            RequestedAt.AddHours(1));

        Result<TenantTerminationStartDto> recovered =
            await recoveryHandler.HandleAsync(
                StartCommand(recoveredCase),
                CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(1, replayStore.AppendCount);
        Assert.Equal(
            protectedIntent.ExecutionStartedAtUtc,
            recovered.Value.Case.ExecutionStartedAtUtc);
        Assert.Equal(
            protectedIntent.ExecutionStartedAtUtc,
            recovered.Value.Process.CreatedAtUtc);
        Assert.Equal(["process", "signal"], recoveryOrder);
    }

    [Fact]
    public async Task Exact_start_replay_does_not_duplicate_intent_or_signal()
    {
        DataRightsCase dataRightsCase = PrepareApprovedCase();
        List<string> order = [];
        StubProcessRepository processes = new(order);
        StubReplayStore replay = new(order);
        RecordingSignal signal = new(order);
        StartTenantTerminationCommandHandler handler = StartHandler(
            new StubCaseRepository(dataRightsCase),
            processes,
            replay,
            signal,
            RequestedAt.AddMinutes(2));
        StartTenantTerminationCommand command = StartCommand(dataRightsCase);

        Result<TenantTerminationStartDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<TenantTerminationStartDto> second = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, replay.AppendCount);
        Assert.Equal(1, processes.AddCount);
        Assert.Equal(1, signal.Count);
        Assert.Equal(first.Value.Process, second.Value.Process);
    }

    private static DecideTenantTerminationCommandHandler DecisionHandler(
        StubCaseRepository repository,
        string catalogSha256) =>
        new(
            DataRightsMutationTestSupport.TenantCase(repository),
            new StubProductionCatalog(catalogSha256),
            new StubRequiredOwners(),
            new FixedScopeContext(),
            new FixedClock(RequestedAt.AddMinutes(1)));

    private static StartTenantTerminationCommandHandler StartHandler(
        StubCaseRepository cases,
        StubProcessRepository processes,
        StubReplayStore replayStore,
        RecordingSignal signal,
        DateTimeOffset nowUtc) =>
        new(
            new TenantTerminationStartCoordinator(
                DataRightsMutationTestSupport.TenantTermination(
                    processes,
                    cases),
                processes,
                replayStore,
                new StubProductionCatalog(CatalogSha256),
                new StubRequiredOwners(),
                signal,
                new FixedScopeContext(),
                new FixedClock(nowUtc)));

    private static StartTenantTerminationCommand StartCommand(
        DataRightsCase dataRightsCase) =>
        new(
            dataRightsCase.Id,
            ProcessId,
            ApprovalEvidence(),
            dataRightsCase.Version,
            "operator:executor");

    private static DataRightsCase PrepareReviewCase()
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.TenantTermination,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.TenantOwner,
            DataRightsRestrictionAction.None).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            CaseId,
            "tenant-a",
            request,
            "operator:requester",
            RequestedAt).Value;
        Assert.True(dataRightsCase.PrepareTenantTerminationReview(
            exportRequested: true,
            dataRightsCase.Version,
            "operator:requester",
            RequestedAt).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsCase PrepareApprovedCase()
    {
        DataRightsCase dataRightsCase = PrepareReviewCase();
        Assert.True(dataRightsCase.RecordTenantTerminationDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            ApprovalEvidence().ComputeSha256(),
            dataRightsCase.Version,
            "operator:approver",
            RequestedAt.AddMinutes(1)).IsSuccess);
        return dataRightsCase;
    }

    private static TenantTerminationApprovalEvidence ApprovalEvidence() =>
        new(
            "approval:change-7201",
            CatalogSha256,
            "backup:evidence-7201",
            "restore:drill-7201",
            "assurance:operator-7201");

    private sealed class StubCaseRepository(DataRightsCase? dataRightsCase = null)
        : ITenantTerminationCaseRepository
    {
        private DataRightsCase? value = dataRightsCase;

        public int AddCount { get; private set; }

        public Task AddAsync(
            DataRightsCase dataRightsCase,
            CancellationToken cancellationToken)
        {
            this.value = dataRightsCase;
            this.AddCount++;
            return Task.CompletedTask;
        }

        public Task<DataRightsCase?> GetAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.value?.Id == caseId ? this.value : null);

        public Task<DataRightsCase?> GetActiveAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(this.value is not null &&
                this.value.Status is not DataRightsCaseState.Denied and not
                    DataRightsCaseState.Completed and not
                    DataRightsCaseState.Canceled
                    ? this.value
                    : null);
    }

    private sealed class StubProcessRepository(List<string> order)
        : ITenantTerminationRepository
    {
        private TenantTerminationProcess? process;

        public bool ThrowOnAdd { get; init; }

        public int AddCount { get; private set; }

        public Task AddProcessAsync(
            TenantTerminationProcess process,
            CancellationToken cancellationToken)
        {
            order.Add("process");
            if (this.ThrowOnAdd)
            {
                throw new InvalidOperationException("simulated database failure");
            }

            this.process = process;
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
            throw new NotSupportedException();

        public Task<IReadOnlyList<TenantTerminationOwnerWorkItem>>
            ListOwnerWorkItemsAsync(
                Guid processId,
                TenantTerminationOwnerPhase phase,
                long operationRevision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubReplayStore(
        List<string> order,
        Action? afterAppend = null)
        : ITenantTerminationReplayStore
    {
        public TenantTerminationReplayIntent? Intent { get; private set; }

        public int AppendCount { get; private set; }

        public Task<TenantTerminationReplayStoreReadiness>
            CheckReadinessAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayAppendReceipt> AppendAsync(
            TenantTerminationReplayJournalEntry entry,
            CancellationToken cancellationToken)
        {
            order.Add("intent");
            Assert.Equal(TenantTerminationReplayEntryKind.Intent, entry.Kind);
            this.Intent = Assert.IsType<TenantTerminationReplayIntent>(
                entry.Intent);
            this.AppendCount++;
            afterAppend?.Invoke();
            return Task.FromResult(new TenantTerminationReplayAppendReceipt(
                TenantTerminationReplayAppendReceipt.CurrentContractVersion,
                entry.LogicalEntryId,
                entry.Kind,
                new(1, new string('b', 64)),
                this.Intent.RecordedAtUtc,
                new string('c', 64)));
        }

        public Task<TenantTerminationReplayAttempt?> ReadAttemptAsync(
            TenantTerminationReplayAttemptCoordinate coordinate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayIntent?> ReadIntentAsync(
            string tenantId,
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Intent?.ProcessId == processId &&
                string.Equals(
                    this.Intent.TenantId,
                    tenantId,
                    StringComparison.Ordinal)
                    ? this.Intent
                    : null);

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

    private sealed class StubProductionCatalog(string catalogSha256)
        : ITenantTerminationProductionCatalog
    {
        public Result<TenantTerminationProductionCatalogEvidence> Validate(
            IReadOnlyCollection<string> requiredOwnerKeys) =>
            Result.Success(new TenantTerminationProductionCatalogEvidence(
                OwnerCount: 1,
                ExportOwnerCount: 1,
                TerminalOwnerKey: "workspaces",
                catalogSha256));
    }

    private sealed class StubRequiredOwners
        : ITenantTerminationRequiredOwnerCatalog
    {
        public IReadOnlyCollection<string> RequiredOwnerKeys { get; } =
            ["workspaces"];
    }

    private sealed class RecordingSignal(List<string> order)
        : ITenantTerminationCoordinationSignal
    {
        public int Count { get; private set; }

        public Task<bool> EnqueueAsync(
            TenantTerminationProcess process,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            order.Add("signal");
            this.Count++;
            return Task.FromResult(true);
        }
    }

    private sealed class FixedScopeContext : IScopeContext
    {
        public bool IsEnabled => true;

        public string ScopeId => "tenant-a";
    }

    private sealed class FixedClock(DateTimeOffset nowUtc) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = nowUtc;
    }
}

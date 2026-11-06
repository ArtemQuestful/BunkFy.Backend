namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Xunit;
using SelectedSubject =
    BunkFy.Modules.DataRights.Domain.Entities.DataRightsSubjectCoordinate;

[Trait("Category", "Unit")]
public sealed class FinalizeDataRightsAnonymisationLedgerCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task External_ack_precedes_database_append_and_retry_is_idempotent()
    {
        ExecutionFixture fixture = CreateFixture();
        RecordingLedgerRepository ledgers = new();
        FinalizeDataRightsAnonymisationLedgerCommand command =
            Command(fixture);
        FinalizeDataRightsAnonymisationLedgerCommandHandler unavailable =
            CreateHandler(
                fixture,
                ledgers,
                new RecordingDeltaStore(throwBeforeAck: true));

        DataRightsLedgerDeltaStoreException failure =
            await Assert.ThrowsAsync<DataRightsLedgerDeltaStoreException>(
                () => unavailable.HandleAsync(
                    command,
                    CancellationToken.None));
        Assert.Equal("test.unavailable", failure.Code);
        Assert.Null(ledgers.Entry);

        RecordingDeltaStore durable = new(throwBeforeAck: false);
        RecordingOutbox outbox = new();
        FinalizeDataRightsAnonymisationLedgerCommandHandler handler =
            CreateHandler(fixture, ledgers, durable, outbox);
        Result<Unit> completed = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Guid ledgerEntryId = ledgers.Entry!.Id;
        Result<Unit> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(completed.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(1, ledgers.AddCount);
        Assert.Equal(2, durable.AppendCount);
        Assert.Equal(
            DataRightsExecutionWorkItemState.Completed,
            fixture.WorkItem.State);
        Assert.IsType<DataRightsAnonymisationWorkItemTerminalIntegrationEvent>(
            Assert.Single(outbox.Events));
        Assert.All(
            durable.Deltas,
            delta => Assert.Equal(ledgerEntryId, delta.Ledger.EntryId));
        Assert.Equal(
            DataRightsProcessingLedgerIdentity.Create(
                fixture.WorkItem.ScopeId,
                fixture.WorkItem.Id,
                fixture.WorkItem.OwnerReceiptId!.Value,
                fixture.WorkItem.OwnerReceiptSha256!),
            ledgerEntryId);
    }

    [Fact]
    public async Task Late_cancellation_after_external_ack_retries_the_exact_delta()
    {
        ExecutionFixture fixture = CreateFixture();
        RecordingLedgerRepository ledgers = new();
        RecordingOutbox outbox = new();
        using CancellationTokenSource source = new();
        RecordingDeltaStore durable = new(
            throwBeforeAck: false,
            afterAck: source.Cancel);
        FinalizeDataRightsAnonymisationLedgerCommandHandler handler =
            CreateHandler(fixture, ledgers, durable, outbox);
        FinalizeDataRightsAnonymisationLedgerCommand command =
            Command(fixture);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(command, source.Token));

        Assert.Null(ledgers.Entry);
        Assert.Equal(
            DataRightsExecutionWorkItemState.OwnerProofRecorded,
            fixture.WorkItem.State);
        Assert.Empty(outbox.Events);

        Result<Unit> retried = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(retried.IsSuccess);
        Assert.Equal(1, ledgers.AddCount);
        Assert.Equal(2, durable.AppendCount);
        Assert.Equal(durable.Deltas[0], durable.Deltas[1]);
        Assert.Equal(
            DataRightsExecutionWorkItemState.Completed,
            fixture.WorkItem.State);
        Assert.Single(outbox.Events);
    }

    [Fact]
    public async Task Invalid_durability_receipt_does_not_append_database_ledger()
    {
        ExecutionFixture fixture = CreateFixture();
        RecordingLedgerRepository ledgers = new();
        FinalizeDataRightsAnonymisationLedgerCommandHandler handler =
            CreateHandler(
                fixture,
                ledgers,
                new RecordingDeltaStore(
                    throwBeforeAck: false,
                    returnInvalidReceipt: true));

        Result<Unit> result = await handler.HandleAsync(
            Command(fixture),
            CancellationToken.None);

        Assert.Equal(
            "DataRights.ProcessingLedgerDurabilityInvalid",
            result.Error.Code);
        Assert.Null(ledgers.Entry);
    }

    private static FinalizeDataRightsAnonymisationLedgerCommandHandler
        CreateHandler(
            ExecutionFixture fixture,
            RecordingLedgerRepository ledgers,
        RecordingDeltaStore deltaStore,
        RecordingOutbox? outbox = null) =>
        new(
            DataRightsMutationTestSupport.Execution(fixture.Cases),
            fixture.WorkItems,
            ledgers,
            new StubPseudonymizer(),
            new StubReplayProtector(),
            deltaStore,
            new RecordingOutboxRegistry(outbox ?? new RecordingOutbox()),
            new TestClock(),
            new TestIdGenerator());

    private static FinalizeDataRightsAnonymisationLedgerCommand Command(
        ExecutionFixture fixture) =>
        new(
            fixture.WorkItem.Id,
            fixture.Case.Id,
            DataRightsCaseScope.ForProperty(
                fixture.WorkItem.PropertyId!.Value),
            fixture.WorkItem.ApprovalRevision,
            fixture.WorkItem.ExecutionRevision,
            fixture.TaskRunId);

    private static ExecutionFixture CreateFixture()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator",
            Now.AddMinutes(-8)).Value;
        _ = dataRightsCase.BeginDiscovery(
            1,
            "user:operator",
            Now.AddMinutes(-7));
        _ = dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            3,
            2,
            "user:operator",
            Now.AddMinutes(-6));
        _ = dataRightsCase.RequireReview(
            3,
            "user:operator",
            Now.AddMinutes(-5));
        _ = dataRightsCase.BeginDecision(
            4,
            "user:approver",
            Now.AddMinutes(-4));
        DataRightsApprovalPolicyEvidence evidence =
            DataRightsApprovalPolicyEvidence.Create(
                propertyId,
                11,
                "GB",
                "approved-policy",
                3,
                "guest-retention",
                2,
                new string('a', 64),
                "data-rights-anonymisation",
                "erasure",
                "authorized-workspace-operator",
                Now.AddMinutes(-3)).Value;
        _ = dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            5,
            "user:approver",
            Now.AddMinutes(-3),
            evidence);
        _ = dataRightsCase.BeginAnonymisationExecution(
            6,
            "user:executor",
            Now.AddMinutes(-2));
        SelectedSubject subject = Assert.Single(
            dataRightsCase.SelectedSubjects);
        DataRightsExecutionWorkItem workItem =
            DataRightsExecutionWorkItem.Prepare(
                Guid.NewGuid(),
                dataRightsCase.ScopeId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                dataRightsCase.Id,
                DataRightsExecutionScope.ForProperty(propertyId),
                approvalRevision: 6,
                dataRightsCase.ExecutionRevision!.Value,
                DataRightsCaseOperation.Anonymisation,
                subject,
                evidence,
                "user:executor",
                Now.AddMinutes(-2)).Value;
        Guid taskRunId = Guid.NewGuid();
        _ = workItem.BeginProcessing(
            taskRunId,
            taskAttempt: 1,
            Now.AddMinutes(-1));
        _ = workItem.RecordOwnerProof(
            workItem.Version,
            taskRunId,
            taskAttempt: 1,
            receiptContractVersion: 1,
            Guid.NewGuid(),
            resultingRecordVersion: 4,
            "guests.completed",
            "guests.profile-anonymised",
            new string('b', 64),
            Now,
            Now.AddSeconds(1));
        return new(
            dataRightsCase,
            workItem,
            taskRunId,
            new StubCaseRepository(dataRightsCase),
            new StubWorkItemRepository(workItem));
    }

    private sealed record ExecutionFixture(
        DataRightsCase Case,
        DataRightsExecutionWorkItem WorkItem,
        Guid TaskRunId,
        StubCaseRepository Cases,
        StubWorkItemRepository WorkItems);

    private sealed class StubCaseRepository(DataRightsCase dataRightsCase)
        : IDataRightsCaseRepository
    {
        public Task AddAsync(
            DataRightsCase ignored,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                dataRightsCase.PropertyId == scope.PropertyId &&
                dataRightsCase.Kind == (DataRightsCaseKind)scope.CaseType &&
                dataRightsCase.Id == caseId
                    ? dataRightsCase
                    : null);

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubWorkItemRepository(
        DataRightsExecutionWorkItem workItem)
        : IDataRightsExecutionWorkItemRepository
    {
        public Task AddAsync(
            DataRightsExecutionWorkItem ignored,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<DataRightsExecutionWorkItem>> ListByBatchAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            Guid batchId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                (IReadOnlyCollection<DataRightsExecutionWorkItem>)(
                    workItem.CaseKind ==
                        (DataRightsCaseKind)scope.CaseType &&
                    workItem.PropertyId == scope.PropertyId &&
                    workItem.CaseId == caseId &&
                    workItem.BatchId == batchId
                        ? [workItem]
                        : []));

        public Task<DataRightsExecutionWorkItem?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            Guid workItemId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                workItem.CaseKind ==
                    (DataRightsCaseKind)scope.CaseType &&
                workItem.PropertyId == scope.PropertyId &&
                workItem.CaseId == caseId &&
                workItem.Id == workItemId
                    ? workItem
                    : null);
    }

    private sealed class RecordingLedgerRepository
        : IDataRightsProcessingLedgerRepository
    {
        public DataRightsProcessingLedgerEntry? Entry { get; private set; }
        public int AddCount { get; private set; }

        public Task AddAsync(
            DataRightsProcessingLedgerEntry entry,
            CancellationToken cancellationToken)
        {
            this.Entry = entry;
            this.AddCount++;
            return Task.CompletedTask;
        }

        public Task<DataRightsProcessingLedgerEntry?> GetByWorkItemAsync(
            Guid workItemId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Entry?.WorkItemId == workItemId
                    ? this.Entry
                    : null);

        public Task<DataRightsProcessingLedgerEntry?> GetByOwnerReceiptAsync(
            string ownerKey,
            Guid ownerReceiptId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsProcessingLedgerEntry?> GetLatestAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Entry);

        public Task<DataRightsProcessingLedgerEntry?> GetBySequenceAsync(
            long tenantSequence,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Entry?.TenantSequence == tenantSequence
                    ? this.Entry
                    : null);
    }

    private sealed class StubPseudonymizer
        : IDataRightsRecordPseudonymizer
    {
        public Result<DataRightsRecordPseudonym> CreateActive(
            string tenantId,
            string ownerKey,
            string recordType,
            Guid recordId) =>
            DataRightsRecordPseudonym.Create(1, new string('c', 64));

        public Result<DataRightsRecordPseudonym> Create(
            int keyVersion,
            string tenantId,
            string ownerKey,
            string recordType,
            Guid recordId) =>
            this.CreateActive(tenantId, ownerKey, recordType, recordId);
    }

    private sealed class StubReplayProtector
        : IDataRightsReplayEnvelopeProtector
    {
        public Result<DataRightsProtectedReplayEnvelope> Protect(
            DataRightsProcessingLedgerSnapshot ledger,
            Guid recordId) =>
            Result.Success(new DataRightsProtectedReplayEnvelope(
                DataRightsProtectedReplayEnvelope.CurrentContractVersion,
                KeyVersion: 1,
                DataRightsProtectedReplayEnvelope.Aes256GcmAlgorithm,
                Convert.ToBase64String(new byte[12]),
                Convert.ToBase64String(new byte[32]),
                Convert.ToBase64String(new byte[16])));

        public Result<Guid> Unprotect(
            DataRightsProcessingLedgerSnapshot ledger,
            DataRightsProtectedReplayEnvelope envelope) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingDeltaStore(
        bool throwBeforeAck,
        bool returnInvalidReceipt = false,
        Action? afterAck = null)
        : IDataRightsLedgerDeltaStore
    {
        public List<DataRightsLedgerDelta> Deltas { get; } = [];
        public int AppendCount => this.Deltas.Count;

        public Task<DataRightsLedgerDeltaStoreReadiness> CheckReadinessAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsLedgerDeltaAppendReceipt> AppendAsync(
            DataRightsLedgerDelta delta,
            CancellationToken cancellationToken)
        {
            if (throwBeforeAck)
            {
                throw new DataRightsLedgerDeltaStoreException(
                    "test.unavailable",
                    "Unavailable.");
            }

            this.Deltas.Add(delta);
            afterAck?.Invoke();
            return Task.FromResult(new DataRightsLedgerDeltaAppendReceipt(
                DataRightsLedgerDeltaAppendReceipt.CurrentContractVersion,
                delta.Ledger.EntryId,
                new DataRightsLedgerDeltaCursor(
                    delta.Ledger.TenantSequence,
                    delta.Ledger.EntrySha256,
                    new string('d', 64)),
                Now.AddSeconds(2),
                returnInvalidReceipt
                    ? "invalid"
                    : new string('e', 64)));
        }

        public Task<DataRightsLedgerDeltaCheckpoint>
            ReadTrustedCheckpointAsync(
                string tenantId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsLedgerDeltaPage> ReadAfterAsync(
            string tenantId,
            DataRightsLedgerDeltaCursor cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now.AddSeconds(2);
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public List<object> Events { get; } = [];
        public string ModuleName => DataRightsModuleMetadata.Name;

        public Task EnqueueAsync<TEvent>(
            TEvent integrationEvent,
            CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutboxRegistry(RecordingOutbox outbox)
        : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName)
        {
            Assert.Equal(DataRightsModuleMetadata.Name, moduleName);
            return outbox;
        }
    }
}

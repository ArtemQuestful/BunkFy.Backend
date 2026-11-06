namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.DataRights.Tests.Persistence;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsRestoreCoordinatorTests
{
    [Fact]
    public async Task Coordinator_advances_only_after_owner_proof_then_confirms()
    {
        HmacDataRightsRecordPseudonymizer pseudonymizer =
            ProtectedLedgerTestData.CreatePseudonymizer((1, 'a'));
        AesGcmDataRightsReplayEnvelopeProtector protector =
            ProtectedLedgerTestData.CreateProtector(
                pseudonymizer,
                activeKeyVersion: 1,
                (1, 'r'));
        Guid recordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry ledger =
            ProtectedLedgerTestData.CreateLedger(
                pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                recordId);
        DataRightsLedgerDelta delta =
            ProtectedLedgerTestData.CreateDelta(
                protector,
                ledger,
                recordId);
        DataRightsLedgerDeltaCursor targetCursor = new(
            TenantSequence: 1,
            ledger.EntrySha256,
            StorageMacSha256: new string('c', 64));
        List<string> calls = [];
        RecordingDispatcher dispatcher =
            new(targetCursor, calls);
        StubDeltaStore deltaStore =
            new(delta, targetCursor, calls);
        StubContributor contributor =
            new(ledger, calls);
        DataRightsRestoreCoordinator coordinator = new(
            dispatcher,
            deltaStore,
            new StubReplayProtector(recordId),
            [contributor],
            new TestScopeContext(),
            new FixedTimeProvider(
                ProtectedLedgerTestData.Now.AddHours(2)));
        DataRightsRestoreScope scope = new(
            DataRightsRestoreScope.CurrentContractVersion,
            "tenant-a",
            new DataRightsLedgerDeltaCheckpoint(
                DataRightsLedgerDeltaCheckpoint.CurrentContractVersion,
                targetCursor,
                IntegrityKeyVersion: 1,
                CheckpointMacSha256: new string('d', 64)));

        Result<Unit> result = await coordinator.ReconcileAsync(
            scope,
            scopeSnapshotSha256: new string('e', 64),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["query", "read", "prepare", "owner", "advance", "confirm"],
            calls);
        Assert.Equal(recordId, contributor.RecordId);
        Assert.Equal(ledger.ResultingRecordVersion, contributor.ResultingRecordVersion);
    }

    [Fact]
    public async Task Version_three_fails_closed_before_replay_envelope_decryption()
    {
        HmacDataRightsRecordPseudonymizer pseudonymizer =
            ProtectedLedgerTestData.CreatePseudonymizer((1, 'a'));
        AesGcmDataRightsReplayEnvelopeProtector envelopeProtector =
            ProtectedLedgerTestData.CreateProtector(
                pseudonymizer,
                activeKeyVersion: 1,
                (1, 'r'));
        Guid recordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry ledger =
            ProtectedLedgerTestData.CreateStaffLedger(
                pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                recordId);
        DataRightsLedgerDeltaCursor targetCursor = new(
            TenantSequence: 1,
            ledger.EntrySha256,
            StorageMacSha256: new string('c', 64));
        List<string> calls = [];
        StubReplayProtector replayProtector = new(recordId);
        DataRightsRestoreCoordinator coordinator = new(
            new RecordingDispatcher(targetCursor, calls),
            new StubDeltaStore(
                ProtectedLedgerTestData.CreateDelta(
                    envelopeProtector,
                    ledger,
                    recordId),
                targetCursor,
                calls),
            replayProtector,
            [],
            new TestScopeContext(),
            new FixedTimeProvider(
                ProtectedLedgerTestData.Now.AddHours(2)));

        Result<Unit> result = await coordinator.ReconcileAsync(
            new DataRightsRestoreScope(
                DataRightsRestoreScope.CurrentContractVersion,
                "tenant-a",
                new DataRightsLedgerDeltaCheckpoint(
                    DataRightsLedgerDeltaCheckpoint.CurrentContractVersion,
                    targetCursor,
                    IntegrityKeyVersion: 1,
                    CheckpointMacSha256: new string('d', 64))),
            scopeSnapshotSha256: new string('e', 64),
            CancellationToken.None);

        Assert.Equal(
            "DataRights.RestorePrerequisiteUnavailable",
            result.Error.Code);
        Assert.Equal(["query", "read", "prepare"], calls);
        Assert.Equal(0, replayProtector.UnprotectCount);
    }

    [Fact]
    public async Task Version_three_denies_access_before_staff_owner_and_checkpoint()
    {
        HmacDataRightsRecordPseudonymizer pseudonymizer =
            ProtectedLedgerTestData.CreatePseudonymizer((1, 'a'));
        AesGcmDataRightsReplayEnvelopeProtector envelopeProtector =
            ProtectedLedgerTestData.CreateProtector(
                pseudonymizer,
                activeKeyVersion: 1,
                (1, 'r'));
        Guid recordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry ledger =
            ProtectedLedgerTestData.CreateStaffLedger(
                pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                recordId);
        DataRightsLedgerDeltaCursor targetCursor = new(
            TenantSequence: 1,
            ledger.EntrySha256,
            StorageMacSha256: new string('c', 64));
        List<string> calls = [];
        StubReplayProtector replayProtector = new(recordId, calls);
        StubRestorePrerequisite prerequisite = new(ledger, calls);
        StubScopedContributor contributor = new(ledger, calls);
        DataRightsRestoreCoordinator coordinator = new(
            new RecordingDispatcher(targetCursor, calls),
            new StubDeltaStore(
                ProtectedLedgerTestData.CreateDelta(
                    envelopeProtector,
                    ledger,
                    recordId),
                targetCursor,
                calls),
            replayProtector,
            [],
            new TestScopeContext(),
            new FixedTimeProvider(
                ProtectedLedgerTestData.Now.AddHours(2)),
            [prerequisite],
            [contributor]);

        Result<Unit> result = await coordinator.ReconcileAsync(
            new DataRightsRestoreScope(
                DataRightsRestoreScope.CurrentContractVersion,
                "tenant-a",
                new DataRightsLedgerDeltaCheckpoint(
                    DataRightsLedgerDeltaCheckpoint.CurrentContractVersion,
                    targetCursor,
                    IntegrityKeyVersion: 1,
                    CheckpointMacSha256: new string('d', 64))),
            scopeSnapshotSha256: new string('e', 64),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                "query",
                "read",
                "prepare",
                "decrypt",
                "prerequisite",
                "owner",
                "advance",
                "confirm"
            ],
            calls);
        Assert.Equal(recordId, prerequisite.RecordId);
        Assert.Same(prerequisite.Request, contributor.Request);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Normal_restore_boundary_return_after_cancellation_stops_checkpoint_progression(
        bool cancelAfterRead)
    {
        using CancellationTokenSource source = new();
        (DataRightsRestoreCoordinator coordinator, DataRightsRestoreScope scope, List<string> calls) =
            CreateLegacyCoordinator(
                cancelAfterRead
                    ? _ => source.Cancel()
                    : null,
                cancelAfterRead
                    ? null
                    : _ => source.Cancel());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.ReconcileAsync(
                scope,
                scopeSnapshotSha256: new string('e', 64),
                source.Token));

        Assert.Equal(
            cancelAfterRead
                ? ["query", "read"]
                : ["query", "read", "prepare", "owner"],
            calls);
    }

    [Fact]
    public async Task Normal_restore_prerequisite_return_after_cancellation_does_not_invoke_owner()
    {
        using CancellationTokenSource source = new();
        (DataRightsRestoreCoordinator coordinator, DataRightsRestoreScope scope, List<string> calls) =
            CreateScopedCoordinator(_ => source.Cancel());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.ReconcileAsync(
                scope,
                scopeSnapshotSha256: new string('e', 64),
                source.Token));

        Assert.Equal(
            ["query", "read", "prepare", "decrypt", "prerequisite"],
            calls);
    }

    private static (
        DataRightsRestoreCoordinator Coordinator,
        DataRightsRestoreScope Scope,
        List<string> Calls) CreateLegacyCoordinator(
            Action<CancellationToken>? afterRead,
            Action<CancellationToken>? afterRestore)
    {
        HmacDataRightsRecordPseudonymizer pseudonymizer =
            ProtectedLedgerTestData.CreatePseudonymizer((1, 'a'));
        AesGcmDataRightsReplayEnvelopeProtector protector =
            ProtectedLedgerTestData.CreateProtector(
                pseudonymizer,
                activeKeyVersion: 1,
                (1, 'r'));
        Guid recordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry ledger =
            ProtectedLedgerTestData.CreateLedger(
                pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                recordId);
        DataRightsLedgerDelta delta =
            ProtectedLedgerTestData.CreateDelta(
                protector,
                ledger,
                recordId);
        DataRightsLedgerDeltaCursor targetCursor = new(
            TenantSequence: 1,
            ledger.EntrySha256,
            StorageMacSha256: new string('c', 64));
        List<string> calls = [];
        DataRightsRestoreCoordinator coordinator = new(
            new RecordingDispatcher(targetCursor, calls),
            new StubDeltaStore(
                delta,
                targetCursor,
                calls,
                afterRead),
            new StubReplayProtector(recordId),
            [new StubContributor(ledger, calls, afterRestore)],
            new TestScopeContext(),
            new FixedTimeProvider(
                ProtectedLedgerTestData.Now.AddHours(2)));
        DataRightsRestoreScope scope = new(
            DataRightsRestoreScope.CurrentContractVersion,
            "tenant-a",
            new DataRightsLedgerDeltaCheckpoint(
                DataRightsLedgerDeltaCheckpoint.CurrentContractVersion,
                targetCursor,
                IntegrityKeyVersion: 1,
                CheckpointMacSha256: new string('d', 64)));
        return (coordinator, scope, calls);
    }

    private static (
        DataRightsRestoreCoordinator Coordinator,
        DataRightsRestoreScope Scope,
        List<string> Calls) CreateScopedCoordinator(
            Action<CancellationToken> afterPrerequisite)
    {
        HmacDataRightsRecordPseudonymizer pseudonymizer =
            ProtectedLedgerTestData.CreatePseudonymizer((1, 'a'));
        AesGcmDataRightsReplayEnvelopeProtector envelopeProtector =
            ProtectedLedgerTestData.CreateProtector(
                pseudonymizer,
                activeKeyVersion: 1,
                (1, 'r'));
        Guid recordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry ledger =
            ProtectedLedgerTestData.CreateStaffLedger(
                pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                recordId);
        DataRightsLedgerDeltaCursor targetCursor = new(
            TenantSequence: 1,
            ledger.EntrySha256,
            StorageMacSha256: new string('c', 64));
        List<string> calls = [];
        DataRightsRestoreCoordinator coordinator = new(
            new RecordingDispatcher(targetCursor, calls),
            new StubDeltaStore(
                ProtectedLedgerTestData.CreateDelta(
                    envelopeProtector,
                    ledger,
                    recordId),
                targetCursor,
                calls),
            new StubReplayProtector(recordId, calls),
            [],
            new TestScopeContext(),
            new FixedTimeProvider(
                ProtectedLedgerTestData.Now.AddHours(2)),
            [new StubRestorePrerequisite(ledger, calls, afterPrerequisite)],
            [new StubScopedContributor(ledger, calls)]);
        DataRightsRestoreScope scope = new(
            DataRightsRestoreScope.CurrentContractVersion,
            "tenant-a",
            new DataRightsLedgerDeltaCheckpoint(
                DataRightsLedgerDeltaCheckpoint.CurrentContractVersion,
                targetCursor,
                IntegrityKeyVersion: 1,
                CheckpointMacSha256: new string('d', 64)));
        return (coordinator, scope, calls);
    }

    private sealed class RecordingDispatcher(
        DataRightsLedgerDeltaCursor target,
        List<string> calls)
        : IRequestDispatcher
    {
        private DataRightsRestoreCheckpointState state = new(
            Version: 0,
            DataRightsRestoreCursor.Genesis,
            DataRightsProcessingLedgerEntry.GenesisEntrySha256);

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            switch (command)
            {
                case PrepareDataRightsRestoreBatchCommand:
                    calls.Add("prepare");
                    return Reply<TResponse, Unit>(
                        Result.Success(Unit.Value));
                case AdvanceDataRightsRestoreCheckpointCommand:
                    calls.Add("advance");
                    this.state = new(
                        Version: 1,
                        new DataRightsRestoreCursor(
                            target.TenantSequence,
                            target.EntrySha256),
                        target.StorageMacSha256);
                    return Reply<TResponse, DataRightsRestoreCheckpointState>(
                        Result.Success(this.state));
                case ConfirmDataRightsRestoreCheckpointCommand:
                    calls.Add("confirm");
                    this.state = this.state with
                    {
                        Version = this.state.Version + 1
                    };
                    return Reply<TResponse, DataRightsRestoreCheckpointState>(
                        Result.Success(this.state));
                default:
                    throw new NotSupportedException(
                        command.GetType().FullName);
            }
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default)
        {
            Assert.IsType<GetDataRightsRestoreCheckpointQuery>(query);
            calls.Add("query");
            return Reply<TResponse, DataRightsRestoreCheckpointState>(
                Result.Success(this.state));
        }

        private static Task<Result<TResponse>> Reply<TResponse, TValue>(
            Result<TValue> result) =>
            Task.FromResult((Result<TResponse>)(object)result);
    }

    private sealed class StubDeltaStore(
        DataRightsLedgerDelta delta,
        DataRightsLedgerDeltaCursor target,
        List<string> calls,
        Action<CancellationToken>? afterRead = null)
        : IDataRightsLedgerDeltaStore
    {
        public Task<DataRightsLedgerDeltaPage> ReadAfterAsync(
            string tenantId,
            DataRightsLedgerDeltaCursor cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            calls.Add("read");
            afterRead?.Invoke(cancellationToken);
            return Task.FromResult(new DataRightsLedgerDeltaPage(
                DataRightsLedgerDeltaPage.CurrentContractVersion,
                [delta],
                target,
                HasMore: false));
        }

        public Task<DataRightsLedgerDeltaStoreReadiness> CheckReadinessAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsLedgerDeltaAppendReceipt> AppendAsync(
            DataRightsLedgerDelta appended,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsLedgerDeltaCheckpoint>
            ReadTrustedCheckpointAsync(
                string tenantId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubReplayProtector(
        Guid recordId,
        List<string>? calls = null)
        : IDataRightsReplayEnvelopeProtector
    {
        public int UnprotectCount { get; private set; }

        public Result<Guid> Unprotect(
            DataRightsProcessingLedgerSnapshot ledger,
            DataRightsProtectedReplayEnvelope envelope)
        {
            this.UnprotectCount++;
            calls?.Add("decrypt");
            return Result.Success(recordId);
        }

        public Result<DataRightsProtectedReplayEnvelope> Protect(
            DataRightsProcessingLedgerSnapshot ledger,
            Guid protectedRecordId) =>
            throw new NotSupportedException();
    }

    private sealed class StubContributor(
        DataRightsProcessingLedgerEntry ledger,
        List<string> calls,
        Action<CancellationToken>? afterRestore = null)
        : IDataRightsAnonymisationRestoreContributor
    {
        public string OwnerKey => ledger.OwnerKey;
        public string RecordType => ledger.RecordType;
        public int ContractVersion =>
            DataRightsAnonymisationRestoreContract.CurrentVersion;
        public Guid RecordId { get; private set; }
        public long? ResultingRecordVersion { get; private set; }

        public Task<DataRightsAnonymisationRestoreResult> RestoreAsync(
            DataRightsAnonymisationRestoreRequest request,
            CancellationToken cancellationToken)
        {
            calls.Add("owner");
            this.RecordId = request.RecordId;
            this.ResultingRecordVersion = request.ResultingRecordVersion;
            afterRestore?.Invoke(cancellationToken);
            return Task.FromResult(
                DataRightsAnonymisationRestoreResult.Completed(
                    new(
                        ledger.Id,
                        ledger.OwnerReceiptId,
                        ledger.OwnerReceiptSha256,
                        ResultingRecordVersion: 5,
                        TombstoneRevision: 1,
                        ProtectedLedgerTestData.Now.AddHours(1))));
        }
    }

    private sealed class StubRestorePrerequisite(
        DataRightsProcessingLedgerEntry ledger,
        List<string> calls,
        Action<CancellationToken>? afterExecute = null)
        : IDataRightsAnonymisationRestorePrerequisiteV3
    {
        public string OwnerKey => ledger.OwnerKey;
        public string RecordType => ledger.RecordType;
        public DataRightsCaseType CaseType => DataRightsCaseType.StaffRights;
        public int ContractVersion =>
            DataRightsAnonymisationRestoreContractV3.CurrentVersion;
        public Guid RecordId { get; private set; }
        public DataRightsAnonymisationRestoreRequestV3? Request
        {
            get;
            private set;
        }

        public Task<DataRightsAnonymisationRestorePrerequisiteResult>
            ExecuteAsync(
                DataRightsAnonymisationRestoreRequestV3 request,
                CancellationToken cancellationToken)
        {
            calls.Add("prerequisite");
            this.Request = request;
            this.RecordId = request.RecordId;
            afterExecute?.Invoke(cancellationToken);
            return Task.FromResult(
                DataRightsAnonymisationRestorePrerequisiteResult.Completed(
                    DataRightsAnonymisationRestoreContractV3.CurrentVersion));
        }
    }

    private sealed class StubScopedContributor(
        DataRightsProcessingLedgerEntry ledger,
        List<string> calls)
        : IDataRightsAnonymisationRestoreContributorV3
    {
        public string OwnerKey => ledger.OwnerKey;
        public string RecordType => ledger.RecordType;
        public DataRightsCaseType CaseType => DataRightsCaseType.StaffRights;
        public int ContractVersion =>
            DataRightsAnonymisationRestoreContractV3.CurrentVersion;
        public DataRightsAnonymisationRestoreRequestV3? Request
        {
            get;
            private set;
        }

        public Task<DataRightsAnonymisationRestoreResult> RestoreAsync(
            DataRightsAnonymisationRestoreRequestV3 request,
            CancellationToken cancellationToken)
        {
            calls.Add("owner");
            this.Request = request;
            return Task.FromResult(
                DataRightsAnonymisationRestoreResult.Completed(
                    DataRightsAnonymisationRestoreContractV3.CurrentVersion,
                    new(
                        ledger.Id,
                        ledger.OwnerReceiptId,
                        ledger.OwnerReceiptSha256,
                        request.ResultingRecordVersion,
                        TombstoneRevision: 1,
                        ProtectedLedgerTestData.Now.AddHours(1))));
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

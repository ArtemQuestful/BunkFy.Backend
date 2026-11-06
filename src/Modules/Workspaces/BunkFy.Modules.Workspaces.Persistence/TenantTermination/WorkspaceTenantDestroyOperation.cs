namespace BunkFy.Modules.Workspaces.Persistence.TenantTermination;

using Gma.Framework.Domain;

internal sealed class WorkspaceTenantDestroyOperation : IScopedEntity
{
    public const int MaximumBatchSize = 500;
    public const int RemovalProofVersion = 1;
    public static readonly string InitialRemovalProofSha256 =
        WorkspaceTenantLifecycleHashes.Sha256(
            "bunkfy-workspaces-tenant-destroy-proof/v1|empty");

    private WorkspaceTenantDestroyOperation() { }

    private WorkspaceTenantDestroyOperation(
        Guid operationId,
        string scopeId,
        string requestSha256,
        Guid fenceId,
        long selectedFenceVersion,
        int batchSize,
        DateTimeOffset startedAtUtc)
    {
        this.OperationId = operationId;
        this.ScopeId = scopeId;
        this.RequestSha256 = requestSha256;
        this.FenceId = fenceId;
        this.SelectedFenceVersion = selectedFenceVersion;
        this.ResultingFenceVersion = selectedFenceVersion + 2;
        this.BatchSize = batchSize;
        this.Stage = WorkspaceTenantDestroyStage.OutboxMessages;
        this.RemovalProofSha256 = InitialRemovalProofSha256;
        this.StartedAtUtc = startedAtUtc;
        this.UpdatedAtUtc = startedAtUtc;
        this.ConcurrencyVersion = 1;
    }

    public Guid OperationId { get; private set; }
    public string ScopeId { get; private set; } = string.Empty;
    public string RequestSha256 { get; private set; } = string.Empty;
    public Guid FenceId { get; private set; }
    public long SelectedFenceVersion { get; private set; }
    public long ResultingFenceVersion { get; private set; }
    public int BatchSize { get; private set; }
    public WorkspaceTenantDestroyStage Stage { get; private set; }
    public long RemovedRecordCount { get; private set; }
    public int CompletedBatchCount { get; private set; }
    public int ProofVersion { get; private set; } = RemovalProofVersion;
    public string RemovalProofSha256 { get; private set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public int ConcurrencyVersion { get; private set; }
    public bool IsComplete =>
        this.Stage == WorkspaceTenantDestroyStage.Completed;

    public static WorkspaceTenantDestroyOperation? TryCreate(
        Guid operationId,
        string scopeId,
        string requestSha256,
        Guid fenceId,
        long selectedFenceVersion,
        int batchSize,
        DateTimeOffset startedAtUtc)
    {
        if (operationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(scopeId) ||
            !WorkspaceTenantLifecycleHashes.IsSha256(requestSha256) ||
            fenceId == Guid.Empty ||
            selectedFenceVersion is < 1 or > long.MaxValue - 2 ||
            batchSize is < 1 or > MaximumBatchSize ||
            startedAtUtc == default)
        {
            return null;
        }

        return new(
            operationId,
            scopeId,
            requestSha256,
            fenceId,
            selectedFenceVersion,
            batchSize,
            startedAtUtc);
    }

    public bool Matches(Guid operationId, string requestSha256) =>
        this.OperationId == operationId &&
        string.Equals(
            this.RequestSha256,
            requestSha256,
            StringComparison.Ordinal);

    public bool RecordBatch(
        WorkspaceTenantDestroyStage stage,
        int removedRecordCount,
        string removedRecordKeysSha256,
        bool stageCompleted,
        DateTimeOffset recordedAtUtc)
    {
        if (this.IsComplete ||
            stage != this.Stage ||
            removedRecordCount is < 1 or > MaximumBatchSize ||
            removedRecordCount > this.BatchSize ||
            !WorkspaceTenantLifecycleHashes.IsSha256(
                removedRecordKeysSha256) ||
            recordedAtUtc < this.UpdatedAtUtc ||
            this.RemovedRecordCount > long.MaxValue - removedRecordCount ||
            this.CompletedBatchCount == int.MaxValue ||
            this.ConcurrencyVersion == int.MaxValue)
        {
            return false;
        }

        int nextBatch = this.CompletedBatchCount + 1;
        this.RemovalProofSha256 = WorkspaceTenantLifecycleHashes.Sha256(
            "bunkfy-workspaces-tenant-destroy-proof/v1|" +
            $"{this.RemovalProofSha256}|{nextBatch}|{(int)stage}|" +
            $"{removedRecordCount}|{removedRecordKeysSha256}");
        this.RemovedRecordCount += removedRecordCount;
        this.CompletedBatchCount = nextBatch;
        this.UpdatedAtUtc = recordedAtUtc;
        this.ConcurrencyVersion++;
        if (stageCompleted)
        {
            this.Stage = Next(stage);
        }

        return true;
    }

    public bool AdvanceEmptyStage(DateTimeOffset recordedAtUtc)
    {
        if (this.IsComplete ||
            recordedAtUtc < this.UpdatedAtUtc ||
            this.ConcurrencyVersion == int.MaxValue)
        {
            return false;
        }

        this.Stage = Next(this.Stage);
        this.UpdatedAtUtc = recordedAtUtc;
        this.ConcurrencyVersion++;
        return true;
    }

    private static WorkspaceTenantDestroyStage Next(
        WorkspaceTenantDestroyStage stage)
    {
        if (stage is < WorkspaceTenantDestroyStage.OutboxMessages or
            > WorkspaceTenantDestroyStage.HistoricalNoProvisionReceipts or
            WorkspaceTenantDestroyStage.Completed)
        {
            throw new InvalidOperationException(
                "The Workspaces tenant destruction stage is invalid.");
        }

        return stage switch
        {
            WorkspaceTenantDestroyStage.StaffOnboardingRetentionExecutions =>
                WorkspaceTenantDestroyStage.SweepCheckpoints,
            WorkspaceTenantDestroyStage.SweepCheckpoints =>
                WorkspaceTenantDestroyStage.HistoricalNoProvisionReceipts,
            WorkspaceTenantDestroyStage.HistoricalNoProvisionReceipts =>
                WorkspaceTenantDestroyStage.Completed,
            _ => (WorkspaceTenantDestroyStage)((int)stage + 1)
        };
    }
}

internal enum WorkspaceTenantDestroyStage
{
    Unknown = 0,
    OutboxMessages = 1,
    InboxMessages = 2,
    ProjectionRebuildCheckpoints = 3,
    PropertyProjections = 4,
    OnboardingCorrectionReceipts = 5,
    OnboardingRestrictionReceipts = 6,
    OnboardingRestrictionProjections = 7,
    OnboardingRestrictions = 8,
    AccessPlanProperties = 9,
    AccessPlans = 10,
    AccessProfileSnapshots = 11,
    AccessProcesses = 12,
    OnboardingApplications = 13,
    RetentionCorrelationReceipts = 14,
    AnonymisationRestoreReceipts = 15,
    AnonymisationTombstones = 16,
    AnonymisationReceipts = 17,
    TerminationFenceReceipts = 18,
    HistoricalTerminationFences = 19,
    StaffOnboardingRetentionExecutions = 20,
    Completed = 21,
    SweepCheckpoints = 22,
    HistoricalNoProvisionReceipts = 23
}

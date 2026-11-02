namespace BunkFy.Modules.Inventory.Persistence.TenantTermination;

using Gma.Framework.Domain;

internal sealed class InventoryTenantDestroyOperation : IScopedEntity
{
    public const int MaximumBatchSize = 500;
    public const int RemovalProofVersion = 1;
    public static readonly string InitialRemovalProofSha256 =
        InventoryTenantLifecycleHashes.Sha256(
            "bunkfy-inventory-tenant-destroy-proof/v1|empty");

    private InventoryTenantDestroyOperation() { }

    private InventoryTenantDestroyOperation(
        Guid operationId,
        string scopeId,
        string requestSha256,
        long selectedRevision,
        int batchSize,
        DateTimeOffset startedAtUtc)
    {
        this.OperationId = operationId;
        this.ScopeId = scopeId;
        this.RequestSha256 = requestSha256;
        this.SelectedRevision = selectedRevision;
        this.ResultingRevision = selectedRevision + 1;
        this.BatchSize = batchSize;
        this.Stage = InventoryTenantDestroyStage.OutboxMessages;
        this.RemovalProofSha256 = InitialRemovalProofSha256;
        this.StartedAtUtc = startedAtUtc;
        this.UpdatedAtUtc = startedAtUtc;
        this.ConcurrencyVersion = 1;
    }

    public Guid OperationId { get; private set; }
    public string ScopeId { get; private set; } = string.Empty;
    public string RequestSha256 { get; private set; } = string.Empty;
    public long SelectedRevision { get; private set; }
    public long ResultingRevision { get; private set; }
    public int BatchSize { get; private set; }
    public InventoryTenantDestroyStage Stage { get; private set; }
    public long RemovedRecordCount { get; private set; }
    public int CompletedBatchCount { get; private set; }
    public int ProofVersion { get; private set; } = RemovalProofVersion;
    public string RemovalProofSha256 { get; private set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public int ConcurrencyVersion { get; private set; }
    public bool IsComplete =>
        this.Stage == InventoryTenantDestroyStage.Completed;

    public static InventoryTenantDestroyOperation? TryCreate(
        Guid operationId,
        string scopeId,
        string requestSha256,
        long selectedRevision,
        int batchSize,
        DateTimeOffset startedAtUtc)
    {
        if (operationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(scopeId) ||
            !InventoryTenantLifecycleHashes.IsSha256(requestSha256) ||
            selectedRevision is < 0 or long.MaxValue ||
            batchSize is < 1 or > MaximumBatchSize ||
            startedAtUtc == default)
        {
            return null;
        }

        return new(
            operationId,
            scopeId,
            requestSha256,
            selectedRevision,
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
        InventoryTenantDestroyStage stage,
        int removedRecordCount,
        string removedRecordKeysSha256,
        bool stageCompleted,
        DateTimeOffset recordedAtUtc)
    {
        if (this.IsComplete ||
            stage != this.Stage ||
            removedRecordCount is < 1 ||
            removedRecordCount > this.BatchSize ||
            !InventoryTenantLifecycleHashes.IsSha256(
                removedRecordKeysSha256) ||
            recordedAtUtc < this.UpdatedAtUtc ||
            this.RemovedRecordCount > long.MaxValue - removedRecordCount ||
            this.CompletedBatchCount == int.MaxValue ||
            this.ConcurrencyVersion == int.MaxValue)
        {
            return false;
        }

        int nextBatch = this.CompletedBatchCount + 1;
        this.RemovalProofSha256 = InventoryTenantLifecycleHashes.Sha256(
            "bunkfy-inventory-tenant-destroy-proof/v1|" +
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

    private static InventoryTenantDestroyStage Next(
        InventoryTenantDestroyStage stage)
    {
        if (!Enum.IsDefined(stage) ||
            stage is InventoryTenantDestroyStage.Unknown or
                InventoryTenantDestroyStage.Completed)
        {
            throw new InvalidOperationException(
                "The Inventory tenant destruction stage is invalid.");
        }

        return stage switch
        {
            InventoryTenantDestroyStage.RoomRetirements =>
                InventoryTenantDestroyStage.ManagementOperations,
            InventoryTenantDestroyStage.ManagementOperations =>
                InventoryTenantDestroyStage.RoomConfigurations,
            InventoryTenantDestroyStage.ManualBlocks =>
                InventoryTenantDestroyStage.ManualBlockGroups,
            InventoryTenantDestroyStage.ManualBlockGroups =>
                InventoryTenantDestroyStage.AllocationOperationLocks,
            InventoryTenantDestroyStage.ProjectionRebuildCheckpoints =>
                InventoryTenantDestroyStage.Completed,
            _ => (InventoryTenantDestroyStage)((int)stage + 1)
        };
    }
}

internal enum InventoryTenantDestroyStage
{
    Unknown = 0,
    OutboxMessages = 1,
    InboxMessages = 2,
    AllocationAnonymisationRestoreReceipts = 3,
    AllocationAnonymisationReceipts = 4,
    AllocationAnonymisationTombstones = 5,
    AllocationAmendmentDecisions = 6,
    AllocationUnits = 7,
    Allocations = 8,
    ManualBlocks = 9,
    AllocationOperationLocks = 10,
    BedRetirements = 11,
    RoomRetirements = 12,
    RoomConfigurations = 13,
    InventoryUnits = 14,
    BedTopology = 15,
    RoomTopology = 16,
    PropertyTopology = 17,
    ProjectionRebuildCheckpoints = 18,
    Completed = 19,
    ManagementOperations = 20,
    ManualBlockGroups = 21
}

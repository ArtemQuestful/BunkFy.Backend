namespace BunkFy.Modules.Properties.Persistence.TenantTermination;

using Gma.Framework.Domain;

internal sealed class PropertiesTenantDestroyOperation : IScopedEntity
{
    public const int MaximumBatchSize = 500;
    public const int RemovalProofVersion = 1;
    public static readonly string InitialRemovalProofSha256 =
        PropertiesTenantLifecycleHashes.Sha256(
            "bunkfy-properties-tenant-destroy-proof/v1|empty");

    private PropertiesTenantDestroyOperation() { }

    private PropertiesTenantDestroyOperation(
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
        this.Stage = PropertiesTenantDestroyStage.OutboxMessages;
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
    public PropertiesTenantDestroyStage Stage { get; private set; }
    public long RemovedRecordCount { get; private set; }
    public int CompletedBatchCount { get; private set; }
    public int ProofVersion { get; private set; } = RemovalProofVersion;
    public string RemovalProofSha256 { get; private set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public int ConcurrencyVersion { get; private set; }
    public bool IsComplete =>
        this.Stage == PropertiesTenantDestroyStage.Completed;

    public static PropertiesTenantDestroyOperation? TryCreate(
        Guid operationId,
        string scopeId,
        string requestSha256,
        long selectedRevision,
        int batchSize,
        DateTimeOffset startedAtUtc)
    {
        if (operationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(scopeId) ||
            !PropertiesTenantLifecycleHashes.IsSha256(requestSha256) ||
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
        PropertiesTenantDestroyStage stage,
        int removedRecordCount,
        string removedRecordKeysSha256,
        bool stageCompleted,
        DateTimeOffset recordedAtUtc)
    {
        if (this.IsComplete ||
            stage != this.Stage ||
            removedRecordCount is < 1 ||
            removedRecordCount > this.BatchSize ||
            !PropertiesTenantLifecycleHashes.IsSha256(
                removedRecordKeysSha256) ||
            recordedAtUtc < this.UpdatedAtUtc ||
            this.RemovedRecordCount > long.MaxValue - removedRecordCount ||
            this.CompletedBatchCount == int.MaxValue ||
            this.ConcurrencyVersion == int.MaxValue)
        {
            return false;
        }

        int nextBatch = this.CompletedBatchCount + 1;
        this.RemovalProofSha256 = PropertiesTenantLifecycleHashes.Sha256(
            "bunkfy-properties-tenant-destroy-proof/v1|" +
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

    private static PropertiesTenantDestroyStage Next(
        PropertiesTenantDestroyStage stage)
    {
        if (stage is < PropertiesTenantDestroyStage.OutboxMessages or
            > PropertiesTenantDestroyStage.PropertyTimeZoneOperations or
            PropertiesTenantDestroyStage.Completed)
        {
            throw new InvalidOperationException(
                "The Properties tenant destruction stage is invalid.");
        }

        return stage switch
        {
            PropertiesTenantDestroyStage.Rooms =>
                PropertiesTenantDestroyStage.PropertyMutationOperations,
            PropertiesTenantDestroyStage.PropertyMutationOperations =>
                PropertiesTenantDestroyStage.PropertyTimeZoneOperations,
            PropertiesTenantDestroyStage.PropertyTimeZoneOperations =>
                PropertiesTenantDestroyStage.Properties,
            _ => (PropertiesTenantDestroyStage)((int)stage + 1)
        };
    }
}

internal enum PropertiesTenantDestroyStage
{
    Unknown = 0,
    OutboxMessages = 1,
    InboxMessages = 2,
    GovernanceRevisions = 3,
    GovernanceAcknowledgements = 4,
    Beds = 5,
    Rooms = 6,
    Properties = 7,
    PropertyOperationLocks = 8,
    RoomOperationLocks = 9,
    Completed = 10,
    PropertyMutationOperations = 11,
    PropertyTimeZoneOperations = 12
}

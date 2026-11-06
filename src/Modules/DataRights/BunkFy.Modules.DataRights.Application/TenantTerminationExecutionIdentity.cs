namespace BunkFy.Modules.DataRights.Application;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;

internal static class TenantTerminationExecutionIdentity
{
    private const string WorkItemDomain =
        "bunkfy.data-rights.tenant-termination.work-item.v1";
    private const string WorkIdempotencyDomain =
        "bunkfy.data-rights.tenant-termination.idempotency.v1";
    private const string TaskRunDomain =
        "bunkfy.data-rights.tenant-termination.task-run.v1";
    private const string ExportArtifactDomain =
        "bunkfy.data-rights.tenant-termination.export-artifact.v1";
    private const string ExportArtifactIdempotencyDomain =
        "bunkfy.data-rights.tenant-termination.export-artifact-idempotency.v1";
    private const string ExportArtifactTaskRunDomain =
        "bunkfy.data-rights.tenant-termination.export-artifact-task-run.v1";
    private const string ExportArtifactCleanupTaskRunDomain =
        "bunkfy.data-rights.tenant-termination.export-artifact-cleanup-task-run.v1";
    private const string ExportFragmentCleanupTaskRunDomain =
        "bunkfy.data-rights.tenant-termination.export-fragment-cleanup-task-run.v1";
    private const string TerminalReceiptDomain =
        "bunkfy.data-rights.tenant-termination.terminal-receipt.v1";
    private const string TerminalReceiptIdempotencyDomain =
        "bunkfy.data-rights.tenant-termination.terminal-receipt-idempotency.v1";
    private const string VerificationTaskRunDomain =
        "bunkfy.data-rights.tenant-termination.verification-task-run.v1";
    private const string ProcessIdempotencyDomain =
        "bunkfy.data-rights.tenant-termination.process-idempotency.v1";
    private const string TerminationEpochDomain =
        "bunkfy.data-rights.tenant-termination.epoch.v1";
    private const string TaskDeduplicationPrefix = "tenant-termination:";

    public static Guid CreateWorkItemId(
        Guid processId,
        long operationRevision,
        TenantTerminationOwnerPhase phase,
        string ownerKey) =>
        CreateCoordinateGuid(
            WorkItemDomain,
            processId,
            operationRevision,
            phase,
            ownerKey);

    public static Guid CreateWorkItemIdempotencyKey(
        Guid processId,
        long operationRevision,
        TenantTerminationOwnerPhase phase,
        string ownerKey) =>
        CreateCoordinateGuid(
            WorkIdempotencyDomain,
            processId,
            operationRevision,
            phase,
            ownerKey);

    public static Guid CreateTaskRunId(
        Guid workItemId,
        int dispatchSequence)
    {
        if (workItemId == Guid.Empty || dispatchSequence <= 0)
        {
            throw new ArgumentException(
                "A work item and positive dispatch sequence are required.");
        }

        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, TaskRunDomain);
        Append(hash, workItemId.ToString("N"));
        Append(
            hash,
            dispatchSequence.ToString(CultureInfo.InvariantCulture));
        return ReadGuid(hash);
    }

    public static string CreateTaskDeduplicationKey(Guid taskRunId)
    {
        if (taskRunId == Guid.Empty)
        {
            throw new ArgumentException(
                "A task run id is required.",
                nameof(taskRunId));
        }

        return TaskDeduplicationPrefix + taskRunId.ToString("N");
    }

    public static Guid CreateExportArtifactId(
        Guid processId,
        long operationRevision) =>
        CreateProcessOperationGuid(
            ExportArtifactDomain,
            processId,
            operationRevision);

    public static Guid CreateExportArtifactIdempotencyKey(
        Guid processId,
        long operationRevision) =>
        CreateProcessOperationGuid(
            ExportArtifactIdempotencyDomain,
            processId,
            operationRevision);

    public static Guid CreateExportArtifactTaskRunId(
        Guid processId,
        long operationRevision) =>
        CreateProcessOperationGuid(
            ExportArtifactTaskRunDomain,
            processId,
            operationRevision);

    public static Guid CreateExportArtifactCleanupTaskRunId(Guid artifactId) =>
        CreateProcessGuid(ExportArtifactCleanupTaskRunDomain, artifactId);

    public static Guid CreateExportFragmentCleanupTaskRunId(Guid fragmentId) =>
        CreateProcessGuid(ExportFragmentCleanupTaskRunDomain, fragmentId);

    public static Guid CreateTerminalReceiptId(
        Guid processId,
        long verificationOperationRevision) =>
        CreateProcessOperationGuid(
            TerminalReceiptDomain,
            processId,
            verificationOperationRevision);

    public static Guid CreateTerminalReceiptIdempotencyKey(
        Guid processId,
        long verificationOperationRevision) =>
        CreateProcessOperationGuid(
            TerminalReceiptIdempotencyDomain,
            processId,
            verificationOperationRevision);

    public static Guid CreateVerificationTaskRunId(
        Guid processId,
        long verificationOperationRevision) =>
        CreateProcessOperationGuid(
            VerificationTaskRunDomain,
            processId,
            verificationOperationRevision);

    public static Guid CreateProcessIdempotencyKey(Guid processId) =>
        CreateProcessGuid(ProcessIdempotencyDomain, processId);

    public static Guid CreateTerminationEpoch(Guid processId) =>
        CreateProcessGuid(TerminationEpochDomain, processId);

    private static Guid CreateCoordinateGuid(
        string domain,
        Guid processId,
        long operationRevision,
        TenantTerminationOwnerPhase phase,
        string ownerKey)
    {
        string normalizedOwnerKey = ownerKey?.Trim() ?? string.Empty;
        if (processId == Guid.Empty ||
            operationRevision <= 0 ||
            !Enum.IsDefined(phase) ||
            phase == TenantTerminationOwnerPhase.Unknown ||
            !IsStableOwnerKey(normalizedOwnerKey))
        {
            throw new ArgumentException(
                "Tenant-termination execution coordinates are invalid.");
        }

        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        Append(hash, processId.ToString("N"));
        Append(
            hash,
            operationRevision.ToString(CultureInfo.InvariantCulture));
        Append(hash, ((int)phase).ToString(CultureInfo.InvariantCulture));
        Append(hash, normalizedOwnerKey);
        return ReadGuid(hash);
    }

    private static Guid CreateProcessOperationGuid(
        string domain,
        Guid processId,
        long operationRevision)
    {
        if (processId == Guid.Empty || operationRevision <= 0)
        {
            throw new ArgumentException(
                "Tenant-termination process coordinates are invalid.");
        }

        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        Append(hash, processId.ToString("N"));
        Append(
            hash,
            operationRevision.ToString(CultureInfo.InvariantCulture));
        return ReadGuid(hash);
    }

    private static Guid CreateProcessGuid(string domain, Guid processId)
    {
        if (processId == Guid.Empty)
        {
            throw new ArgumentException(
                "A tenant-termination process id is required.",
                nameof(processId));
        }

        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        Append(hash, processId.ToString("N"));
        return ReadGuid(hash);
    }

    private static Guid ReadGuid(IncrementalHash hash)
    {
        byte[] digest = hash.GetHashAndReset();
        try
        {
            return new Guid(digest.AsSpan(0, 16));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsStableOwnerKey(string value) =>
        value.Length is > 0 and <=
            TenantTerminationOwnerWorkItem.OwnerKeyMaxLength &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character =>
            character is (>= 'a' and <= 'z') or
                (>= '0' and <= '9') or '.' or '-' or '_');
}

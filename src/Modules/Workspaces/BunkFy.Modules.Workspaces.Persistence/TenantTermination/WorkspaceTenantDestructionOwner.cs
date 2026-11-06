namespace BunkFy.Modules.Workspaces.Persistence.TenantTermination;

using System.Data;
using System.Globalization;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain.Termination;
using Gma.Framework.Naming;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using DomainFenceState =
    BunkFy.Modules.Workspaces.Domain.Termination.WorkspaceTerminationFenceState;

internal sealed partial class WorkspaceTenantDestructionOwner(
    WorkspacesDbContext dbContext,
    IScopeContext scopeContext,
    ISystemClock clock)
    : IWorkspaceTenantDestructionOwner
{
    private const int DestroyBatchSize =
        WorkspaceTenantDestroyOperation.MaximumBatchSize;

    public async Task<TenantTerminationContributionResult> ExecuteAsync(
        TenantTerminationContributionRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = clock.UtcNow;
        if (!IsValid(request, scopeContext, startedAtUtc) ||
            !TenantIds.TryNormalize(request.TenantId, out string? tenantId))
        {
            return Failed(
                "workspace.termination.destroy-request-invalid",
                startedAtUtc);
        }

        if (dbContext.Database.IsRelational() &&
            dbContext.Database.CurrentTransaction is not null)
        {
            return Failed(
                "workspace.termination.destroy-transaction-conflict",
                startedAtUtc);
        }

        string requestSha256 = DestroyRequestSha256(request, tenantId);
        IDbContextTransaction? transaction = null;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                transaction = await dbContext.Database.BeginTransactionAsync(
                        IsolationLevel.ReadCommitted,
                        cancellationToken)
                    .ConfigureAwait(false);
                await WorkspaceTenantMutationLock.AcquireLifecycleAsync(
                        dbContext,
                        tenantId,
                        request.IdempotencyKey,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            WorkspaceTenantDestroyReceipt[] receiptMatches =
                await dbContext.TenantDestroyReceipts
                    .IgnoreQueryFilters()
                    .Where(receipt =>
                        receipt.OperationId == request.IdempotencyKey ||
                        receipt.ScopeId == tenantId)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (receiptMatches.Length > 0)
            {
                WorkspaceTenantDestroyReceipt? exact = receiptMatches
                    .SingleOrDefault(receipt => receipt.ScopeId == tenantId);
                TenantTerminationContributionResult replay =
                    exact is not null &&
                    receiptMatches.Length == 1 &&
                    exact.Matches(
                        request.IdempotencyKey,
                        requestSha256) &&
                    await this.HasValidCompletedProofAsync(
                            exact,
                            request,
                            cancellationToken)
                        .ConfigureAwait(false)
                        ? Completed(exact)
                        : Failed(
                            "workspace.termination.destroy-conflict",
                            clock.UtcNow);
                return await FinishAsync(
                    transaction,
                    replay,
                    cancellationToken).ConfigureAwait(false);
            }

            WorkspaceTenantDestroyOperation[] operationMatches =
                await dbContext.TenantDestroyOperations
                    .IgnoreQueryFilters()
                    .Where(operation =>
                        operation.OperationId == request.IdempotencyKey ||
                        operation.ScopeId == tenantId)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            WorkspaceTenantDestroyOperation? operation = operationMatches
                .SingleOrDefault(candidate => candidate.ScopeId == tenantId);
            if (operationMatches.Length > 0 &&
                (operation is null ||
                 operationMatches.Length != 1 ||
                 !operation.Matches(
                     request.IdempotencyKey,
                     requestSha256)))
            {
                return await FinishAsync(
                    transaction,
                    Failed(
                        "workspace.termination.destroy-conflict",
                        clock.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }

            WorkspaceTerminationFence? fence = await dbContext
                .WorkspaceTerminationFences
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.ScopeId == tenantId &&
                        candidate.ProcessId == request.ProcessId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fence is null || !MatchesFence(request, tenantId, fence))
            {
                return await FinishAsync(
                    transaction,
                    RetryRequired(
                        "workspace.termination.destroy-fence-unavailable",
                        operation?.RemovedRecordCount ?? 0,
                        clock.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }

            if (operation is null)
            {
                if (fence.State != DomainFenceState.Frozen)
                {
                    return await FinishAsync(
                        transaction,
                        Failed(
                            "workspace.termination.destroy-conflict",
                            clock.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                }

                long selectedFenceVersion = fence.Version;
                DateTimeOffset operationStartedAtUtc = clock.UtcNow;
                operation = WorkspaceTenantDestroyOperation.TryCreate(
                    request.IdempotencyKey,
                    tenantId,
                    requestSha256,
                    fence.Id,
                    selectedFenceVersion,
                    DestroyBatchSize,
                    operationStartedAtUtc);
                if (operation is null ||
                    fence.BeginDestruction(
                        selectedFenceVersion,
                        request.ExecutingActorId,
                        operationStartedAtUtc).IsFailure)
                {
                    return await FinishAsync(
                        transaction,
                        Failed(
                            "workspace.termination.destroy-state-invalid",
                            clock.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                }

                WorkspaceTerminationFenceReceipt? beginReceipt =
                    CreateFenceReceipt(
                        request,
                        fence,
                        WorkspaceTerminationFenceAction.BeginDestruction,
                        selectedFenceVersion,
                        operationStartedAtUtc);
                if (beginReceipt is null)
                {
                    return await FinishAsync(
                        transaction,
                        Failed(
                            "workspace.termination.destroy-state-invalid",
                            clock.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                }

                dbContext.TenantDestroyOperations.Add(operation);
                dbContext.WorkspaceTerminationFenceReceipts.Add(beginReceipt);
                await dbContext.SaveTenantDestructionChangesAsync(
                        tenantId,
                        request.IdempotencyKey,
                        attemptedStage: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (fence.Id != operation.FenceId ||
                fence.State !=
                    DomainFenceState.DestructionStarted ||
                fence.Version != operation.SelectedFenceVersion + 1)
            {
                return await FinishAsync(
                    transaction,
                    Failed(
                        "workspace.termination.destroy-conflict",
                        clock.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }

            DateTimeOffset observedAtUtc = clock.UtcNow;
            bool hasActiveOutboxLease = await dbContext.OutboxMessages
                .IgnoreQueryFilters()
                .AnyAsync(
                    message =>
                        message.ScopeId == tenantId &&
                        message.ProcessedAtUtc == null &&
                        message.LockedBy != null &&
                        message.LockedUntilUtc > observedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (hasActiveOutboxLease)
            {
                return await FinishAsync(
                    transaction,
                    RetryRequired(
                        "workspace.termination.destroy-outbox-busy",
                        operation.RemovedRecordCount,
                        observedAtUtc),
                    cancellationToken).ConfigureAwait(false);
            }

            while (!operation.IsComplete)
            {
                WorkspaceTenantDestroyStage attemptedStage = operation.Stage;
                bool removed = await this.RemoveCurrentStageAsync(
                        operation,
                        tenantId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!removed)
                {
                    continue;
                }

                await dbContext.SaveTenantDestructionChangesAsync(
                        tenantId,
                        request.IdempotencyKey,
                        attemptedStage,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await FinishAsync(
                    transaction,
                    RetryRequired(
                        "workspace.termination.destroy-in-progress",
                        operation.RemovedRecordCount,
                        clock.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }

            if (await this.HasRemainingOwnerRecordsAsync(
                    operation,
                    tenantId,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "Workspaces tenant destruction completed with remaining owner records.");
            }

            DateTimeOffset completedAtUtc = clock.UtcNow;
            if (completedAtUtc > request.DeadlineUtc)
            {
                return await FinishAsync(
                    transaction,
                    RetryRequired(
                        "workspace.termination.destroy-deadline-expired",
                        operation.RemovedRecordCount,
                        completedAtUtc),
                    cancellationToken).ConfigureAwait(false);
            }

            long selectedCloseVersion = fence.Version;
            if (fence.Close(
                    selectedCloseVersion,
                    request.ExecutingActorId,
                    completedAtUtc).IsFailure ||
                fence.Version != operation.ResultingFenceVersion)
            {
                throw new InvalidDataException(
                    "Workspaces tenant destruction fence completion is invalid.");
            }

            WorkspaceTerminationFenceReceipt? closeReceipt =
                CreateFenceReceipt(
                    request,
                    fence,
                    WorkspaceTerminationFenceAction.Close,
                    selectedCloseVersion,
                    completedAtUtc);
            WorkspaceTenantDestroyReceipt? receipt = closeReceipt is null
                ? null
                : WorkspaceTenantDestroyReceipt.TryCreate(
                    operation,
                    closeReceipt.Id,
                    completedAtUtc);
            if (closeReceipt is null || receipt is null)
            {
                throw new InvalidDataException(
                    "Workspaces tenant destruction completion proof is invalid.");
            }

            dbContext.WorkspaceTerminationFenceReceipts.Add(closeReceipt);
            dbContext.TenantDestroyReceipts.Add(receipt);
            dbContext.TenantDestroyOperations.Remove(operation);
            await dbContext.SaveTenantDestructionChangesAsync(
                    tenantId,
                    request.IdempotencyKey,
                    attemptedStage: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return await FinishAsync(
                transaction,
                Completed(receipt),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> HasValidCompletedProofAsync(
        WorkspaceTenantDestroyReceipt receipt,
        TenantTerminationContributionRequest request,
        CancellationToken cancellationToken)
    {
        WorkspaceTerminationFence? fence = await dbContext
            .WorkspaceTerminationFences
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.ScopeId == receipt.ScopeId &&
                    candidate.Id == receipt.FenceId,
                cancellationToken)
            .ConfigureAwait(false);
        WorkspaceTerminationFenceReceipt? closeReceipt = await dbContext
            .WorkspaceTerminationFenceReceipts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.ScopeId == receipt.ScopeId &&
                    candidate.Id == receipt.CloseFenceReceiptId,
                cancellationToken)
            .ConfigureAwait(false);
        return fence is not null &&
            closeReceipt is not null &&
            MatchesFence(request, receipt.ScopeId, fence) &&
            fence.State == DomainFenceState.Closed &&
            fence.Version == receipt.ResultingFenceVersion &&
            closeReceipt.FenceId == fence.Id &&
            closeReceipt.Action == WorkspaceTerminationFenceAction.Close &&
            closeReceipt.ProcessId == request.ProcessId &&
            closeReceipt.CaseId == request.CaseId &&
            closeReceipt.ApprovalRevision == request.ApprovalRevision &&
            closeReceipt.OperationRevision == request.OperationRevision &&
            closeReceipt.WorkItemId == request.WorkItemId &&
            closeReceipt.TerminationEpoch == request.TerminationEpoch &&
            closeReceipt.SelectedFenceVersion ==
                receipt.SelectedFenceVersion + 1 &&
            closeReceipt.ResultingFenceVersion ==
                receipt.ResultingFenceVersion &&
            string.Equals(
                closeReceipt.PolicyEvidenceSha256,
                request.PolicyEvidenceSha256,
                StringComparison.Ordinal) &&
            string.Equals(
                closeReceipt.ActorId,
                request.ExecutingActorId.Trim(),
                StringComparison.Ordinal) &&
            closeReceipt.CompletedAtUtc == receipt.CompletedAtUtc;
    }

    private static WorkspaceTerminationFenceReceipt? CreateFenceReceipt(
        TenantTerminationContributionRequest request,
        WorkspaceTerminationFence fence,
        WorkspaceTerminationFenceAction action,
        long selectedFenceVersion,
        DateTimeOffset completedAtUtc)
    {
        string discriminator = action switch
        {
            WorkspaceTerminationFenceAction.BeginDestruction => "begin",
            WorkspaceTerminationFenceAction.Close => "close",
            _ => string.Empty
        };
        if (discriminator.Length == 0)
        {
            return null;
        }

        Guid receiptId = DataRightsExportRecordIds.CreateDeterministicChild(
            request.IdempotencyKey,
            $"workspaces-tenant-destroy-{discriminator}-receipt");
        Guid receiptIdempotencyKey =
            DataRightsExportRecordIds.CreateDeterministicChild(
                request.IdempotencyKey,
                $"workspaces-tenant-destroy-{discriminator}-idempotency");
        Gma.Framework.Results.Result<WorkspaceTerminationFenceReceipt> result =
            WorkspaceTerminationFenceReceipt.Create(
                receiptId,
                request.TenantId,
                fence.Id,
                request.ProcessId,
                request.CaseId,
                request.ApprovalRevision,
                request.OperationRevision,
                request.WorkItemId,
                receiptIdempotencyKey,
                request.TerminationEpoch,
                action,
                selectedFenceVersion,
                fence.Version,
                fence.State,
                request.PolicyEvidenceSha256,
                request.ExecutingActorId,
                completedAtUtc);
        return result.IsSuccess ? result.Value : null;
    }

    private static bool MatchesFence(
        TenantTerminationContributionRequest request,
        string tenantId,
        WorkspaceTerminationFence fence) =>
        fence.ScopeId == tenantId &&
        fence.ProcessId == request.ProcessId &&
        fence.CaseId == request.CaseId &&
        fence.ApprovalRevision == request.ApprovalRevision &&
        fence.TerminationEpoch == request.TerminationEpoch &&
        string.Equals(
            fence.PolicyEvidenceSha256,
            request.PolicyEvidenceSha256,
            StringComparison.Ordinal);

    private static bool IsValid(
        TenantTerminationContributionRequest request,
        IScopeContext scope,
        DateTimeOffset nowUtc) =>
        request is not null &&
        request.ContractVersion == TenantTerminationContract.CurrentVersion &&
        TenantIds.TryNormalize(request.TenantId, out string? tenantId) &&
        scope.IsEnabled &&
        string.Equals(scope.ScopeId, tenantId, StringComparison.Ordinal) &&
        request.ProcessId != Guid.Empty &&
        request.CaseId != Guid.Empty &&
        request.ApprovalRevision > 0 &&
        request.OperationRevision > request.ApprovalRevision &&
        request.TerminationEpoch != Guid.Empty &&
        request.Phase == TenantTerminationContributionPhase.Destroy &&
        request.WorkItemId != Guid.Empty &&
        request.IdempotencyKey != Guid.Empty &&
        WorkspaceTenantLifecycleHashes.IsSha256(
            request.PolicyEvidenceSha256) &&
        request.DeadlineUtc > nowUtc &&
        IsActor(request.ExecutingActorId);

    private static string DestroyRequestSha256(
        TenantTerminationContributionRequest request,
        string tenantId)
    {
        static string Field(string value) =>
            $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

        return WorkspaceTenantLifecycleHashes.Sha256(
            "bunkfy-workspaces-tenant-destroy-request/v1|" +
            $"{request.IdempotencyKey:N}|{Field(tenantId)}|" +
            $"{request.ProcessId:N}|{request.CaseId:N}|" +
            $"{request.ApprovalRevision.ToString(CultureInfo.InvariantCulture)}|" +
            $"{request.OperationRevision.ToString(CultureInfo.InvariantCulture)}|" +
            $"{request.TerminationEpoch:N}|{request.WorkItemId:N}|" +
            $"{request.PolicyEvidenceSha256}|" +
            $"{Field(request.ExecutingActorId.Trim())}|{DestroyBatchSize}");
    }

    private static bool IsActor(string? actorId)
    {
        string normalized = actorId?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
            TenantTerminationContract.ActorIdMaxLength;
    }

    private static TenantTerminationContributionResult Completed(
        WorkspaceTenantDestroyReceipt receipt) =>
        new(
            TenantTerminationContributionStatus.Completed,
            "workspace.termination.destroyed",
            receipt.RemovedRecordCount,
            RetainedMinimumCount: 0,
            RemainingActiveCount: 0,
            HoldReviewAtUtc: null,
            receipt.SelectedFenceVersion,
            receipt.ResultingFenceVersion,
            WorkspacesTenantTerminationMetadata.CatalogVersion,
            WorkspacesTenantTerminationMetadata.CatalogSha256,
            receipt.CompletedAtUtc);

    private static TenantTerminationContributionResult RetryRequired(
        string code,
        long affectedCount,
        DateTimeOffset recordedAtUtc) =>
        new(
            TenantTerminationContributionStatus.RetryRequired,
            code,
            affectedCount,
            RetainedMinimumCount: 0,
            RemainingActiveCount: 1,
            HoldReviewAtUtc: null,
            SelectedProofRevision: null,
            ResultingProofRevision: null,
            WorkspacesTenantTerminationMetadata.CatalogVersion,
            WorkspacesTenantTerminationMetadata.CatalogSha256,
            recordedAtUtc);

    private static TenantTerminationContributionResult Failed(
        string code,
        DateTimeOffset recordedAtUtc) =>
        new(
            TenantTerminationContributionStatus.Failed,
            code,
            AffectedCount: 0,
            RetainedMinimumCount: 0,
            RemainingActiveCount: 1,
            HoldReviewAtUtc: null,
            SelectedProofRevision: null,
            ResultingProofRevision: null,
            WorkspacesTenantTerminationMetadata.CatalogVersion,
            WorkspacesTenantTerminationMetadata.CatalogSha256,
            recordedAtUtc);

    private static async Task<TenantTerminationContributionResult> FinishAsync(
        IDbContextTransaction? transaction,
        TenantTerminationContributionResult result,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }
}

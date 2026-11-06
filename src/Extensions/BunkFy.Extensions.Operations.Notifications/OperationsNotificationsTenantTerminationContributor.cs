namespace BunkFy.Extensions.Operations.Notifications;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Naming;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.Notifications.Contracts;

internal sealed partial class OperationsNotificationsTenantTerminationContributor(
    INotificationScopeLifecycle lifecycle,
    IScopeContext scopeContext,
    ISystemClock clock,
    IWorkspaceTerminationFenceReader? terminationFences = null)
    : ITenantTerminationContributor,
      ITenantTerminationExportContributor
{
    private const string DeadlineExceeded =
        "OperationsNotifications.TenantTerminationDeadlineExceeded";

    public TenantTerminationContributorDescriptor Descriptor { get; } = new(
        OperationsNotificationsTenantTerminationMetadata.OwnerKey,
        TenantTerminationContract.CurrentVersion,
        [
            new(
                TenantTerminationContributionPhase.Export,
                [OperationsNotificationsTenantTerminationMetadata.DependencyOwnerKey]),
            new(
                TenantTerminationContributionPhase.Destroy,
                [OperationsNotificationsTenantTerminationMetadata.DependencyOwnerKey])
        ],
        MandatoryForProduction: true,
        OperationsNotificationsTenantTerminationMetadata.CatalogVersion,
        OperationsNotificationsTenantTerminationMetadata.CatalogSha256);

    public DataRightsExportDescriptor ExportDescriptor =>
        OperationsNotificationsTenantTerminationExportSchema.Descriptor;

    private async Task<WorkspaceTerminationFenceSnapshot?> ReadFenceAsync(
        CancellationToken cancellationToken)
    {
        if (terminationFences is null)
        {
            return null;
        }

        try
        {
            return await terminationFences.GetCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private DateTimeOffset GetUtcNowBeforeDeadline(
        DateTimeOffset deadlineUtc)
    {
        DateTimeOffset observedAtUtc = clock.UtcNow;
        if (observedAtUtc >= deadlineUtc)
        {
            throw new TimeoutException(DeadlineExceeded);
        }

        return observedAtUtc;
    }

    private static bool Matches(
        TenantTerminationExportRequest request,
        WorkspaceTerminationFenceSnapshot? fence) =>
        fence is not null &&
        fence.ProcessId == request.Contribution.ProcessId &&
        fence.TerminationEpoch == request.Contribution.TerminationEpoch &&
        fence.State == WorkspaceTerminationFenceState.Frozen &&
        fence.Version == request.WorkspaceFenceRevision;

    private static bool Matches(
        TenantTerminationContributionRequest request,
        WorkspaceTerminationFenceSnapshot? fence) =>
        fence is not null &&
        fence.ProcessId == request.ProcessId &&
        fence.TerminationEpoch == request.TerminationEpoch &&
        fence.State == WorkspaceTerminationFenceState.Frozen;

    private static bool IsValid(
        TenantTerminationExportRequest request,
        IScopeContext scope,
        DateTimeOffset nowUtc)
    {
        TenantTerminationContributionRequest contribution =
            request.Contribution;
        return contribution.ContractVersion ==
                TenantTerminationContract.CurrentVersion &&
            TenantIds.TryNormalize(
                contribution.TenantId,
                out string? tenantId) &&
            scope.IsEnabled &&
            string.Equals(scope.ScopeId, tenantId, StringComparison.Ordinal) &&
            contribution.ProcessId != Guid.Empty &&
            contribution.CaseId != Guid.Empty &&
            contribution.ApprovalRevision > 0 &&
            request.FreezeOperationRevision > 0 &&
            contribution.OperationRevision >
                request.FreezeOperationRevision &&
            contribution.TerminationEpoch != Guid.Empty &&
            contribution.Phase ==
                TenantTerminationContributionPhase.Export &&
            contribution.WorkItemId != Guid.Empty &&
            contribution.IdempotencyKey != Guid.Empty &&
            OperationsNotificationsDataRightsValidation.IsSha256(
                contribution.PolicyEvidenceSha256) &&
            OperationsNotificationsDataRightsValidation.IsSha256(
                request.FrozenRevisionSha256) &&
            request.WorkspaceFenceRevision > 0 &&
            request.FrozenAtUtc != default &&
            request.FrozenAtUtc <= nowUtc &&
            contribution.DeadlineUtc > nowUtc &&
            IsActor(contribution.ExecutingActorId);
    }

    private static bool IsValidDestroy(
        TenantTerminationContributionRequest? request,
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
        request.OperationRevision > 0 &&
        request.TerminationEpoch != Guid.Empty &&
        request.Phase == TenantTerminationContributionPhase.Destroy &&
        request.WorkItemId != Guid.Empty &&
        request.IdempotencyKey != Guid.Empty &&
        OperationsNotificationsDataRightsValidation.IsSha256(
            request.PolicyEvidenceSha256) &&
        request.DeadlineUtc > nowUtc &&
        IsActor(request.ExecutingActorId);

    private static bool IsActor(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
            TenantTerminationContract.ActorIdMaxLength;
    }

    private static TenantTerminationContributionResult Completed(
        string code,
        long affectedCount,
        long selectedRevision,
        long resultingRevision,
        DateTimeOffset recordedAtUtc) =>
        new(
            TenantTerminationContributionStatus.Completed,
            code,
            affectedCount,
            RetainedMinimumCount: 0,
            RemainingActiveCount: 0,
            HoldReviewAtUtc: null,
            selectedRevision,
            resultingRevision,
            OperationsNotificationsTenantTerminationMetadata.CatalogVersion,
            OperationsNotificationsTenantTerminationMetadata.CatalogSha256,
            recordedAtUtc);

    private static TenantTerminationContributionResult Retry(
        string code,
        DateTimeOffset recordedAtUtc,
        long affectedCount = 0) =>
        new(
            TenantTerminationContributionStatus.RetryRequired,
            code,
            affectedCount,
            RetainedMinimumCount: 0,
            RemainingActiveCount: 1,
            HoldReviewAtUtc: null,
            SelectedProofRevision: null,
            ResultingProofRevision: null,
            OperationsNotificationsTenantTerminationMetadata.CatalogVersion,
            OperationsNotificationsTenantTerminationMetadata.CatalogSha256,
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
            OperationsNotificationsTenantTerminationMetadata.CatalogVersion,
            OperationsNotificationsTenantTerminationMetadata.CatalogSha256,
            recordedAtUtc);
}

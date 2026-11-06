namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Microsoft.Extensions.DependencyInjection;

internal sealed class GetWorkspaceStaffIdentityAnchorCutoverStatusQueryHandler(
    WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator,
    IScopeContext scopeContext)
    : IQueryHandler<GetWorkspaceStaffIdentityAnchorCutoverStatusQuery,
        WorkspaceStaffIdentityAnchorCutoverStatus>
{
    public Task<Result<WorkspaceStaffIdentityAnchorCutoverStatus>> HandleAsync(
        GetWorkspaceStaffIdentityAnchorCutoverStatusQuery query,
        CancellationToken cancellationToken)
    {
        if (!WorkspaceStaffIdentityAnchorTenantScope.TryGetCanonicalTenantId(
                scopeContext,
                out string tenantId))
        {
            return Task.FromResult(Result.Failure<
                WorkspaceStaffIdentityAnchorCutoverStatus>(
                    WorkspaceStaffIdentityAnchorCutoverErrors.TenantRequired));
        }

        return coordinator.GetStatusAsync(
            tenantId,
            query.OwnerManifest,
            cancellationToken);
    }
}

internal sealed class ReconcileWorkspaceStaffIdentityAnchorsCommandHandler(
    WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator,
    IWorkspaceIdentityAnchorCutoverExecutionBoundary executionBoundary,
    IWorkspaceCrossGraphMutationLock crossGraphLock,
    IStaffIdentityProvisioningAnchorCutover staff,
    IWorkspaceStaffIdentityAnchorFreshStatusReader freshStatusReader,
    IScopeContext scopeContext)
    : ICommandHandler<ReconcileWorkspaceStaffIdentityAnchorsCommand,
        WorkspaceStaffIdentityAnchorReconcileResult>
{
    public async Task<Result<WorkspaceStaffIdentityAnchorReconcileResult>>
        HandleAsync(
            ReconcileWorkspaceStaffIdentityAnchorsCommand command,
            CancellationToken cancellationToken)
    {
        if (!WorkspaceStaffIdentityAnchorTenantScope.TryGetCanonicalTenantId(
                scopeContext,
                out string tenantId))
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorReconcileResult>(
                    WorkspaceStaffIdentityAnchorCutoverErrors.TenantRequired);
        }

        Result<WorkspaceStaffIdentityAnchorPreparedReconcile> prepared =
            await executionBoundary.ExecuteAsync(
            async operationCancellationToken =>
            {
                await crossGraphLock.AcquireAsync(
                        operationCancellationToken)
                    .ConfigureAwait(false);
                return await coordinator.PrepareReconcileAsync(
                    tenantId,
                    command.ExpectedSourceEvidenceSha256,
                    command.ExpectedAnchorStateSha256,
                    command.OwnerManifest,
                    command.ExpectedOwnerManifestSha256,
                    command.BatchSize,
                    operationCancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        if (prepared.IsFailure)
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorReconcileResult>(
                prepared.Error);
        }

        return await this.ApplyAndVerifyAsync(
                prepared.Value,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Result<WorkspaceStaffIdentityAnchorReconcileResult>>
        ApplyAndVerifyAsync(
            WorkspaceStaffIdentityAnchorPreparedReconcile prepared,
            CancellationToken cancellationToken)
    {
        if (prepared.Batch.Count == 0)
        {
            return Result.Success(
                VerifiedResult(prepared, appliedCount: 0,
                    prepared.AcceptedStatus));
        }

        bool applyAttempted = false;
        try
        {
            applyAttempted = true;
            StaffIdentityProvisioningAnchorApplyResult applied =
                await staff.ApplyAsync(prepared.Batch, cancellationToken)
                    .ConfigureAwait(false);
            if (!IsWellFormed(applied, prepared.Batch.Count))
            {
                return await this.ApplyOutcomeUnknownAsync(
                        prepared,
                        appliedCount: null)
                    .ConfigureAwait(false);
            }

            if (!applied.IsSuccess)
            {
                return Result.Failure<
                    WorkspaceStaffIdentityAnchorReconcileResult>(
                        WorkspaceStaffIdentityAnchorCutoverErrors
                            .StaffUnavailable);
            }

            WorkspaceStaffIdentityAnchorFreshVerification? fresh =
                await this.TryReadFreshVerificationAsync(prepared)
                    .ConfigureAwait(false);
            return fresh is null || !IsExactVerifiedFreshState(prepared, fresh)
                ? ApplyOutcomeUnknownResult(
                    prepared,
                    applied.AppliedCount,
                    fresh?.Status)
                : Result.Success(VerifiedResult(
                    prepared,
                    applied.AppliedCount,
                    fresh.Status));
        }
        catch (Exception) when (applyAttempted)
        {
            return await this.ApplyOutcomeUnknownAsync(
                    prepared,
                    appliedCount: null)
                .ConfigureAwait(false);
        }
    }

    private async Task<Result<WorkspaceStaffIdentityAnchorReconcileResult>>
        ApplyOutcomeUnknownAsync(
            WorkspaceStaffIdentityAnchorPreparedReconcile prepared,
            int? appliedCount)
    {
        WorkspaceStaffIdentityAnchorFreshVerification? fresh =
            await this.TryReadFreshVerificationAsync(prepared)
                .ConfigureAwait(false);
        return ApplyOutcomeUnknownResult(
            prepared,
            appliedCount,
            fresh?.Status);
    }

    private async Task<WorkspaceStaffIdentityAnchorFreshVerification?>
        TryReadFreshVerificationAsync(
            WorkspaceStaffIdentityAnchorPreparedReconcile prepared)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            Result<WorkspaceStaffIdentityAnchorFreshVerification> fresh =
                await freshStatusReader.ReadAsync(
                        prepared,
                        timeout.Token)
                    .ConfigureAwait(false);
            return fresh.IsSuccess ? fresh.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsWellFormed(
        StaffIdentityProvisioningAnchorApplyResult applied,
        int attemptedCount) =>
        applied is not null &&
        applied.AppliedCount >= 0 &&
        applied.AlreadyAnchoredCount >= 0 &&
        (applied.IsSuccess
            ? (long)applied.AppliedCount + applied.AlreadyAnchoredCount ==
                attemptedCount &&
              string.IsNullOrWhiteSpace(applied.ErrorCode)
            : applied.AppliedCount == 0 &&
              applied.AlreadyAnchoredCount == 0 &&
              !string.IsNullOrWhiteSpace(applied.ErrorCode));

    private static bool IsExactVerifiedFreshState(
        WorkspaceStaffIdentityAnchorPreparedReconcile prepared,
        WorkspaceStaffIdentityAnchorFreshVerification fresh) =>
        string.Equals(
            prepared.AcceptedSourceEvidenceSha256,
            fresh.Status.SourceEvidenceSha256,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            prepared.AcceptedOwnerManifestSha256,
            fresh.Status.OwnerManifestSha256,
            StringComparison.OrdinalIgnoreCase) &&
        WorkspaceStaffIdentityAnchorCutoverCoordinator.TryValidateInspection(
            prepared.Batch,
            fresh.BatchInspection,
            out Dictionary<(StaffIdentityProvisioningAnchorSourceKind, Guid),
                StaffIdentityProvisioningAnchorCutoverDisposition> bySource) &&
        bySource.Values.All(disposition => disposition ==
            StaffIdentityProvisioningAnchorCutoverDisposition.AlreadyAnchored);

    private static WorkspaceStaffIdentityAnchorReconcileResult VerifiedResult(
        WorkspaceStaffIdentityAnchorPreparedReconcile prepared,
        int appliedCount,
        WorkspaceStaffIdentityAnchorCutoverStatus status) =>
        new(
            appliedCount,
            status,
            WorkspaceStaffIdentityAnchorReconcileOutcome.AppliedAndVerified,
            MustRerunStatus: false,
            prepared.AcceptedSourceEvidenceSha256,
            prepared.AcceptedAnchorStateSha256,
            prepared.AcceptedOwnerManifestSha256);

    private static Result<WorkspaceStaffIdentityAnchorReconcileResult>
        ApplyOutcomeUnknownResult(
            WorkspaceStaffIdentityAnchorPreparedReconcile prepared,
            int? appliedCount,
            WorkspaceStaffIdentityAnchorCutoverStatus? status) =>
        Result.Success(new WorkspaceStaffIdentityAnchorReconcileResult(
            appliedCount,
            status,
            WorkspaceStaffIdentityAnchorReconcileOutcome.ApplyOutcomeUnknown,
            MustRerunStatus: true,
            prepared.AcceptedSourceEvidenceSha256,
            prepared.AcceptedAnchorStateSha256,
            prepared.AcceptedOwnerManifestSha256));
}

internal interface IWorkspaceStaffIdentityAnchorFreshStatusReader
{
    Task<Result<WorkspaceStaffIdentityAnchorFreshVerification>> ReadAsync(
        WorkspaceStaffIdentityAnchorPreparedReconcile prepared,
        CancellationToken cancellationToken);
}

internal sealed class WorkspaceStaffIdentityAnchorFreshStatusReader(
    IWorkspaceAuthoritativeScope authoritativeScope)
    : IWorkspaceStaffIdentityAnchorFreshStatusReader
{
    public Task<Result<WorkspaceStaffIdentityAnchorFreshVerification>> ReadAsync(
        WorkspaceStaffIdentityAnchorPreparedReconcile prepared,
        CancellationToken cancellationToken) =>
        authoritativeScope.RunAsync(
            prepared.OrganizationId,
            async provider =>
            {
                IWorkspaceIdentityAnchorCutoverExecutionBoundary boundary =
                    provider.GetRequiredService<
                        IWorkspaceIdentityAnchorCutoverExecutionBoundary>();
                IWorkspaceCrossGraphMutationLock crossGraphLock = provider
                    .GetRequiredService<IWorkspaceCrossGraphMutationLock>();
                return await boundary.ExecuteAsync(
                    async operationCancellationToken =>
                    {
                        await crossGraphLock.AcquireAsync(
                                operationCancellationToken)
                            .ConfigureAwait(false);
                        Result<WorkspaceStaffIdentityAnchorCutoverStatus>
                            status = await provider.GetRequiredService<
                                    IRequestDispatcher>()
                                .QueryAsync(
                                    new
                                        GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
                                            prepared.OwnerManifest),
                                    operationCancellationToken)
                                .ConfigureAwait(false);
                        if (status.IsFailure)
                        {
                            return Result.Failure<
                                WorkspaceStaffIdentityAnchorFreshVerification>(
                                    status.Error);
                        }

                        StaffIdentityProvisioningAnchorInspection inspection =
                            await provider.GetRequiredService<
                                    IStaffIdentityProvisioningAnchorCutover>()
                                .InspectAsync(
                                    prepared.Batch,
                                    operationCancellationToken)
                                .ConfigureAwait(false);
                        return Result.Success(
                            new WorkspaceStaffIdentityAnchorFreshVerification(
                                status.Value,
                                inspection));
                    },
                    cancellationToken).ConfigureAwait(false);
            });
}

internal sealed record WorkspaceStaffIdentityAnchorFreshVerification(
    WorkspaceStaffIdentityAnchorCutoverStatus Status,
    StaffIdentityProvisioningAnchorInspection BatchInspection);

internal static class WorkspaceStaffIdentityAnchorTenantScope
{
    public static bool TryGetCanonicalTenantId(
        IScopeContext scopeContext,
        out string tenantId)
    {
        tenantId = string.Empty;
        if (!scopeContext.IsEnabled ||
            !Guid.TryParseExact(scopeContext.ScopeId, "D", out Guid parsed))
        {
            return false;
        }

        string canonical = parsed.ToString("D");
        if (!string.Equals(scopeContext.ScopeId, canonical,
                StringComparison.Ordinal))
        {
            return false;
        }

        tenantId = canonical;
        return true;
    }
}

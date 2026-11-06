namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class GetLatestWorkspaceStaffOnboardingRecoveryAttemptQueryHandler(
    IWorkspaceStaffOnboardingRecoveryReceiptRepository receipts)
    : IQueryHandler<
        GetLatestWorkspaceStaffOnboardingRecoveryAttemptQuery,
        WorkspaceStaffOnboardingRecoveryAttemptDto>
{
    public async Task<Result<WorkspaceStaffOnboardingRecoveryAttemptDto>>
        HandleAsync(
            GetLatestWorkspaceStaffOnboardingRecoveryAttemptQuery query,
            CancellationToken cancellationToken)
    {
        BunkFy.Modules.Workspaces.Domain
            .WorkspaceStaffOnboardingRecoveryReceipt? receipt =
            await receipts.FindLatestAsync(
                query.ApplicationId,
                cancellationToken).ConfigureAwait(false);
        return receipt is null
            ? Result.Failure<WorkspaceStaffOnboardingRecoveryAttemptDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .RecoveryAttemptNotFound)
            : Result.Success(
                AttemptWorkspaceStaffOnboardingRecoveryCommandHandler
                    .ToDtoForQuery(receipt));
    }
}

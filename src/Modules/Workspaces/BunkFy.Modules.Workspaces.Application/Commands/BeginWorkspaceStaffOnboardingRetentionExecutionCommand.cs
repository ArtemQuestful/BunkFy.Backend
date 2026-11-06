namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;

internal sealed record BeginWorkspaceStaffOnboardingRetentionExecutionCommand(
    RetentionContributionRequest Request)
    : ITransactionalCommand<WorkspaceStaffOnboardingRetentionExecutionStart>;

internal sealed record WorkspaceStaffOnboardingRetentionExecutionStart(
    bool DispatchRequired,
    int ScannedCount,
    int AffectedCount,
    RetentionContributionResult? CompletedResult);

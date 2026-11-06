namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;

internal sealed record CompleteWorkspaceStaffOnboardingRetentionExecutionCommand(
    Guid ExecutionId,
    int Attempt,
    WorkspaceStaffOnboardingRetentionExecutionState State,
    int ScannedCount,
    int RemainingCount,
    string OutcomeCode,
    DateTimeOffset CompletedAtUtc)
    : ITransactionalCommand<RetentionContributionResult>;

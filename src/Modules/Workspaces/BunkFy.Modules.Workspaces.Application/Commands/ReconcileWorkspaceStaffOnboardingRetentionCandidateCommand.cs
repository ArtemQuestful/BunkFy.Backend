namespace BunkFy.Modules.Workspaces.Application.Commands;

using Gma.Framework.Cqrs;

internal sealed record ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand(
    Guid ExecutionId,
    int Attempt,
    Guid ApplicationId,
    long ExpectedVersion)
    : ITransactionalCommand<WorkspaceStaffOnboardingRetentionReconciliation>;

internal sealed record WorkspaceStaffOnboardingRetentionReconciliation(
    WorkspaceStaffOnboardingRetentionOutcome Outcome,
    bool Affected);

internal enum WorkspaceStaffOnboardingRetentionOutcome
{
    Unchanged = 0,
    Expired = 1,
    ClaimPending = 2,
    ClaimRejected = 3,
    ClaimExpired = 4,
    ClaimAccepted = 5,
    ClaimAcceptedRecoveryRequired = 6,
    AuthorityLapsed = 7,
    ClaimWithdrawn = 8
}

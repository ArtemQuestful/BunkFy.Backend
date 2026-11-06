namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Workspaces.Application.Ports;
using Gma.Framework.Cqrs;

internal sealed record ListWorkspaceStaffOnboardingRetentionCandidatesCommand(
    Guid ExecutionId,
    int Attempt,
    DateTimeOffset SourceExpiredBeforeUtc,
    int MaximumCount)
    : ITransactionalCommand<
        IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate>>;

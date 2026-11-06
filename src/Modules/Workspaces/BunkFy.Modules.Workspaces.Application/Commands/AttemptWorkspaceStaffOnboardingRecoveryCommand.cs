namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Cqrs;

public sealed record AttemptWorkspaceStaffOnboardingRecoveryCommand(
    Guid ApplicationId,
    Guid OperationId,
    long ExpectedApplicationVersion,
    string RecoveryActorId)
    : ITransactionalCommand<WorkspaceStaffOnboardingRecoveryAttemptDto>,
      IWorkspacePersistenceRetryableCommand;

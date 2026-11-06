namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Cqrs;

public sealed record
    ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommand(
        Guid IdempotencyKey,
        Guid RestrictionId,
        Guid CaseId,
        long ApprovalRevision,
        Guid ApplicationId,
        long ExpectedOnboardingVersion,
        long ExpectedRestrictionVersion,
        long ExpectedProjectionRevision,
        string ActorId,
        bool LegacyUnboundTarget = false)
    : ITransactionalCommand<
        WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>;

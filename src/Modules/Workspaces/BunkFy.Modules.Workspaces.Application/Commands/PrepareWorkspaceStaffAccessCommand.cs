namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Cqrs;

public sealed record PrepareWorkspaceStaffAccessCommand(
    StaffLifecyclePolicyContext Context)
    : ITransactionalCommand<WorkspaceStaffAccessPreparation>;

public sealed record WorkspaceStaffAccessPreparation(
    Guid ProcessId,
    bool RequiresAccessDenial,
    WorkspaceStaffAccessRestorationDisposition RestorationDisposition);

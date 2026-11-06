namespace BunkFy.Modules.Workspaces.Application.Mapping;

using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using ContractRestorationDisposition =
    BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffAccessRestorationDisposition;
using DomainRestorationDisposition =
    BunkFy.Modules.Workspaces.Domain.WorkspaceStaffAccessRestorationDisposition;

public static class WorkspaceStaffAccessMappings
{
    public static WorkspaceStaffAccessProcessDto ToDto(this WorkspaceStaffAccessProcess process) => new(
        process.Id,
        Guid.Parse(process.ScopeId),
        process.StaffMemberId,
        MapTargetStatus(process.TargetState),
        MapRestorationDisposition(process.RestorationDisposition),
        process.TargetStaffVersion,
        process.EffectiveOn,
        MapStatus(process.State),
        process.ProfileSnapshots.Count,
        process.FailureCode,
        process.Version,
        process.CreatedAtUtc,
        process.LastChangedAtUtc,
        process.CompletedAtUtc);

    public static WorkspaceStaffAccessTargetStatus MapTargetStatus(
        WorkspaceStaffAccessTargetState status) => status switch
        {
            WorkspaceStaffAccessTargetState.Active => WorkspaceStaffAccessTargetStatus.Active,
            WorkspaceStaffAccessTargetState.Suspended => WorkspaceStaffAccessTargetStatus.Suspended,
            WorkspaceStaffAccessTargetState.Departed => WorkspaceStaffAccessTargetStatus.Departed,
            _ => WorkspaceStaffAccessTargetStatus.Unknown
        };

    public static ContractRestorationDisposition
        MapRestorationDisposition(
            DomainRestorationDisposition disposition) =>
        disposition switch
        {
            DomainRestorationDisposition.NotApplicable =>
                ContractRestorationDisposition.NotApplicable,
            DomainRestorationDisposition.RestoreSnapshot =>
                ContractRestorationDisposition.RestoreSnapshot,
            DomainRestorationDisposition.Suppressed =>
                ContractRestorationDisposition.Suppressed,
            _ => ContractRestorationDisposition.Unknown
        };

    public static WorkspaceStaffAccessProcessStatus MapStatus(
        WorkspaceStaffAccessProcessState status) => status switch
        {
            WorkspaceStaffAccessProcessState.Prepared => WorkspaceStaffAccessProcessStatus.Prepared,
            WorkspaceStaffAccessProcessState.AwaitingStaffCommit =>
                WorkspaceStaffAccessProcessStatus.AwaitingStaffCommit,
            WorkspaceStaffAccessProcessState.RestorationPending =>
                WorkspaceStaffAccessProcessStatus.RestorationPending,
            WorkspaceStaffAccessProcessState.Completed => WorkspaceStaffAccessProcessStatus.Completed,
            _ => WorkspaceStaffAccessProcessStatus.Unknown
        };
}

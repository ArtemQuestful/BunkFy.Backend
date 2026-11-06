namespace BunkFy.Modules.DataRights.Contracts;

using Gma.Framework.Tasks;

[TaskName(TaskName)]
[TaskPayloadVersion(PayloadVersion)]
[TaskDescription("Delete one expired protected tenant-termination export fragment.")]
[TaskKind(ModuleTaskKind.OneShot)]
[TaskWorkerGroup(DataRightsModuleMetadata.TenantTerminationWorkerGroup)]
[SupportsTaskControl]
public sealed record DeleteExpiredTenantTerminationExportFragmentPayload(
    string TenantId,
    Guid ProcessId,
    Guid FragmentId,
    long ExportOperationRevision,
    DateTimeOffset ExpiresAtUtc) : ITaskPayload
{
    public const string TaskName =
        "delete-expired-tenant-termination-export-fragment";
    public const int PayloadVersion = 1;
}

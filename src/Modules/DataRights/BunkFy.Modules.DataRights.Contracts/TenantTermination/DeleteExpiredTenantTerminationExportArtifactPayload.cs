namespace BunkFy.Modules.DataRights.Contracts;

using Gma.Framework.Tasks;

[TaskName(TaskName)]
[TaskPayloadVersion(PayloadVersion)]
[TaskDescription("Delete one expired protected tenant-termination export artifact.")]
[TaskKind(ModuleTaskKind.OneShot)]
[TaskWorkerGroup(DataRightsModuleMetadata.TenantTerminationWorkerGroup)]
[SupportsTaskControl]
public sealed record DeleteExpiredTenantTerminationExportArtifactPayload(
    string TenantId,
    Guid ProcessId,
    Guid ArtifactId,
    long ExportOperationRevision,
    DateTimeOffset ExpiresAtUtc) : ITaskPayload
{
    public const string TaskName =
        "delete-expired-tenant-termination-export-artifact";
    public const int PayloadVersion = 1;
}

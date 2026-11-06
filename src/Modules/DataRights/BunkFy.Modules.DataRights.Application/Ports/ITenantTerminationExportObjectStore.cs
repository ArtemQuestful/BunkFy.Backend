namespace BunkFy.Modules.DataRights.Application.Ports;

public interface ITenantTerminationExportObjectStore
{
    Task<bool> DeleteArtifactAsync(
        Guid processId,
        Guid artifactId,
        CancellationToken cancellationToken);

    Task<bool> DeleteFragmentAsync(
        Guid processId,
        Guid fragmentId,
        CancellationToken cancellationToken);
}

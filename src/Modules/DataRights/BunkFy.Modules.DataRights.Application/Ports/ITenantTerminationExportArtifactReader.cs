namespace BunkFy.Modules.DataRights.Application.Ports;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Domain.Aggregates;

public interface ITenantTerminationExportArtifactReader
{
    Task<DataRightsExportDownload> OpenVerifiedAsync(
        TenantTerminationExportArtifact artifact,
        CancellationToken cancellationToken);
}

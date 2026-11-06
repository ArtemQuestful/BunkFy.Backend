namespace BunkFy.Modules.DataRights.Application.Commands;

using BunkFy.Modules.DataRights.Application.Models;
using Gma.Framework.Cqrs;

public sealed record PrepareTenantTerminationExportDownloadCommand(
    Guid CaseId,
    Guid ProcessId,
    Guid ArtifactId,
    string ActorId) : ICommand<DataRightsExportDownload>;

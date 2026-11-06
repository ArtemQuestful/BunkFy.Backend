namespace BunkFy.Modules.DataRights.Application.Commands;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using Gma.Framework.Cqrs;

public sealed record BeginTenantTerminationExportArtifactDeletionCommand(
    Guid ProcessId,
    Guid ArtifactId,
    long ExportOperationRevision,
    DateTimeOffset ExpiresAtUtc,
    Guid RunId)
    : ITransactionalCommand<TenantTerminationExportObjectDeletionStart>,
        IDataRightsPersistenceRetryableCommand;

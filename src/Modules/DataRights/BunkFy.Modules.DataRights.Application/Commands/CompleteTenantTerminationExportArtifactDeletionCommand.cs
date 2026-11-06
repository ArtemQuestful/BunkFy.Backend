namespace BunkFy.Modules.DataRights.Application.Commands;

using BunkFy.Modules.DataRights.Application.Ports;
using Gma.Framework.Cqrs;

public sealed record CompleteTenantTerminationExportArtifactDeletionCommand(
    Guid ProcessId,
    Guid ArtifactId,
    long ExportOperationRevision,
    Guid RunId)
    : ITransactionalCommand<Unit>, IDataRightsPersistenceRetryableCommand;

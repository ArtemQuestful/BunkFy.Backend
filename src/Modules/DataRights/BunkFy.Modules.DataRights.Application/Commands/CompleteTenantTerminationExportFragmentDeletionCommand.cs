namespace BunkFy.Modules.DataRights.Application.Commands;

using BunkFy.Modules.DataRights.Application.Ports;
using Gma.Framework.Cqrs;

public sealed record CompleteTenantTerminationExportFragmentDeletionCommand(
    Guid ProcessId,
    Guid FragmentId,
    long ExportOperationRevision,
    Guid RunId)
    : ITransactionalCommand<Unit>, IDataRightsPersistenceRetryableCommand;

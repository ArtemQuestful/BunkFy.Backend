namespace BunkFy.Modules.DataRights.Application.Commands;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using Gma.Framework.Cqrs;

public sealed record BeginTenantTerminationExportFragmentDeletionCommand(
    Guid ProcessId,
    Guid FragmentId,
    long ExportOperationRevision,
    DateTimeOffset ExpiresAtUtc,
    Guid RunId)
    : ITransactionalCommand<TenantTerminationExportObjectDeletionStart>,
        IDataRightsPersistenceRetryableCommand;

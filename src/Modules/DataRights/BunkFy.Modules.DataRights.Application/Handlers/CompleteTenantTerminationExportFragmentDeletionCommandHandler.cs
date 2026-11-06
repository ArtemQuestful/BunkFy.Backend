namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class CompleteTenantTerminationExportFragmentDeletionCommandHandler(
    ITenantTerminationExportFragmentRepository fragments,
    IDataRightsExportAuditSink audit,
    ISystemClock clock)
    : ICommandHandler<CompleteTenantTerminationExportFragmentDeletionCommand,
        Unit>
{
    private const string Actor =
        "system:tenant-termination-export-retention";

    public async Task<Result<Unit>> HandleAsync(
        CompleteTenantTerminationExportFragmentDeletionCommand command,
        CancellationToken cancellationToken)
    {
        TenantTerminationExportFragment? fragment = await fragments.GetAsync(
            command.FragmentId,
            cancellationToken).ConfigureAwait(false);
        if (fragment is null ||
            fragment.ProcessId != command.ProcessId ||
            fragment.ExportOperationRevision !=
                command.ExportOperationRevision)
        {
            return Invalid();
        }

        bool alreadyDeleted =
            fragment.State == TenantTerminationExportFragmentState.Deleted;
        DateTimeOffset nowUtc = clock.UtcNow;
        Result deleted = fragment.MarkDeleted(command.RunId, nowUtc);
        if (deleted.IsFailure)
        {
            return Result.Failure<Unit>(deleted.Error);
        }

        if (!alreadyDeleted)
        {
            await audit.RecordAsync(
                DataRightsExportAuditFacts.Create(
                    fragment,
                    DataRightsExportAuditAction.Deleted,
                    Actor,
                    "deleted",
                    nowUtc),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(Unit.Value);
    }

    private static Result<Unit> Invalid() => Result.Failure<Unit>(
        DataRightsApplicationErrors.TenantTerminationExecutionStateInvalid);
}

namespace BunkFy.Modules.DataRights.Application.Validation;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using Gma.Framework.Cqrs;

internal sealed class PrepareTenantTerminationExportDownloadCommandValidator
    : ICommandValidator<PrepareTenantTerminationExportDownloadCommand>
{
    public IEnumerable<string> Validate(
        PrepareTenantTerminationExportDownloadCommand command)
    {
        if (command.CaseId == Guid.Empty ||
            command.ProcessId == Guid.Empty ||
            command.ArtifactId == Guid.Empty)
        {
            yield return "Case, process, and artifact are required.";
        }

        string actor = command.ActorId?.Trim() ?? string.Empty;
        if (actor.Length is 0 or > TenantTerminationProcess.ActorIdMaxLength)
        {
            yield return
                "ActorId is required and must be within the supported limit.";
        }
    }
}

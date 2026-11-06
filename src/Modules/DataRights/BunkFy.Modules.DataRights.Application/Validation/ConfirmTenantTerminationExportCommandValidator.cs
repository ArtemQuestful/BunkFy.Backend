namespace BunkFy.Modules.DataRights.Application.Validation;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using Gma.Framework.Cqrs;

internal sealed class ConfirmTenantTerminationExportCommandValidator
    : ICommandValidator<ConfirmTenantTerminationExportCommand>
{
    public IEnumerable<string> Validate(
        ConfirmTenantTerminationExportCommand command)
    {
        if (command.CaseId == Guid.Empty ||
            command.ProcessId == Guid.Empty ||
            command.ArtifactId == Guid.Empty ||
            command.ExportOperationRevision <= 0 ||
            command.ExpectedProcessVersion <= 0 ||
            command.ExpectedArtifactVersion <= 0)
        {
            yield return
                "Case, process, artifact, operation, and versions are required.";
        }

        if (!IsSha256(command.FrozenRevisionSha256) ||
            !IsSha256(command.FragmentSetSha256))
        {
            yield return "Exact lowercase SHA-256 export proofs are required.";
        }

        string actor = command.ActorId?.Trim() ?? string.Empty;
        if (actor.Length is 0 or > TenantTerminationProcess.ActorIdMaxLength ||
            string.Equals(
                actor,
                TenantTerminationCoordination.ExecutorActorId,
                StringComparison.Ordinal))
        {
            yield return "A supported non-system operator actor is required.";
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: TenantTerminationProcess.Sha256Length } &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

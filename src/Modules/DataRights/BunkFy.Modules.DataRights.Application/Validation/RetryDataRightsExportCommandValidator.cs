namespace BunkFy.Modules.DataRights.Application.Validation;

using BunkFy.Modules.DataRights.Application.Commands;
using Gma.Framework.Cqrs;

internal sealed class RetryDataRightsExportCommandValidator
    : ICommandValidator<RetryDataRightsExportCommand>
{
    public IEnumerable<string> Validate(RetryDataRightsExportCommand command)
    {
        foreach (string error in DataRightsCaseValidation.Mutation(
            command.Scope,
            command.CaseId,
            command.ExpectedCaseVersion,
            command.ActorId))
        {
            yield return error;
        }

        if (command.ArtifactId == Guid.Empty)
        {
            yield return "ArtifactId is required.";
        }

        if (command.ExpectedArtifactVersion <= 0)
        {
            yield return "ExpectedArtifactVersion must be positive.";
        }
    }
}

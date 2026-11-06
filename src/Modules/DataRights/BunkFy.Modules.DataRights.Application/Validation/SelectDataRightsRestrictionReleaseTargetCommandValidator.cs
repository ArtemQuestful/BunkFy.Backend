namespace BunkFy.Modules.DataRights.Application.Validation;

using BunkFy.Modules.DataRights.Application.Commands;
using Gma.Framework.Cqrs;

internal sealed class SelectDataRightsRestrictionReleaseTargetCommandValidator
    : ICommandValidator<SelectDataRightsRestrictionReleaseTargetCommand>
{
    public IEnumerable<string> Validate(
        SelectDataRightsRestrictionReleaseTargetCommand command)
    {
        foreach (string error in DataRightsCaseValidation.Mutation(
            command.Scope,
            command.CaseId,
            command.ExpectedVersion,
            command.ActorId))
        {
            yield return error;
        }

        if (command.OwnerOperationId == Guid.Empty ||
            command.OwnerOperationVersion is < 1 or long.MaxValue)
        {
            yield return "A valid restriction owner operation and version are required.";
        }
    }
}

namespace BunkFy.Modules.Properties.Application.Validation;

using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Gma.Framework.Cqrs;

internal sealed class SetPropertyTimeZoneCommandValidator
    : ICommandValidator<SetPropertyTimeZoneCommand>
{
    public IEnumerable<string> Validate(SetPropertyTimeZoneCommand command)
    {
        if (command.PropertyId == Guid.Empty)
        {
            yield return "Property id is required.";
        }

        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        foreach (string error in PropertiesValidation.ValidateExpectedVersion(
                     command.ExpectedVersion,
                     "property"))
        {
            yield return error;
        }

        if (string.IsNullOrWhiteSpace(command.TimeZoneId))
        {
            yield return "Time zone id is required.";
        }
        else if (command.TimeZoneId.Trim().Length >
                 Property.TimeZoneIdMaxLength ||
                 command.TimeZoneId.Any(char.IsControl))
        {
            yield return
                $"Time zone id must be {Property.TimeZoneIdMaxLength} characters or fewer and cannot contain control characters.";
        }

        foreach (string error in PropertiesValidation.ValidateActor(
                     command.ActorId,
                     required: true))
        {
            yield return error;
        }
    }
}

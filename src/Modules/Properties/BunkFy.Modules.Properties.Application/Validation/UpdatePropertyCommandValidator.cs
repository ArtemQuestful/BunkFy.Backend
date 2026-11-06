namespace BunkFy.Modules.Properties.Application.Validation;

using BunkFy.Modules.Properties.Application.Commands;
using Gma.Framework.Cqrs;

internal sealed class UpdatePropertyCommandValidator : ICommandValidator<UpdatePropertyCommand>
{
    public IEnumerable<string> Validate(UpdatePropertyCommand command)
    {
        if (command.PropertyId == Guid.Empty)
        {
            yield return "Property id is required.";
        }

        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        foreach (string error in PropertiesValidation.ValidateExpectedVersion(command.ExpectedVersion, "property"))
        {
            yield return error;
        }

        foreach (string error in PropertiesValidation.ValidatePropertyWrite(
                     command.Name,
                     command.Code,
                     "Etc/UTC"))
        {
            yield return error;
        }

        foreach (string error in
                 PropertiesValidation.ValidateOptionalPropertyTimeZone(
                     command.TimeZoneId))
        {
            yield return error;
        }
    }
}

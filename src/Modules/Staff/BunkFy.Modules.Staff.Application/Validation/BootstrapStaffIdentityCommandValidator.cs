namespace BunkFy.Modules.Staff.Application.Validation;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;

internal sealed class BootstrapStaffIdentityCommandValidator
    : ICommandValidator<BootstrapStaffIdentityCommand>
{
    public IEnumerable<string> Validate(BootstrapStaffIdentityCommand command)
    {
        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        if (command.SourceId == Guid.Empty)
        {
            yield return "SourceId is required.";
        }

        string authSubjectId = command.AuthSubjectId?.Trim() ?? string.Empty;
        if (authSubjectId.Length is 0 or > StaffContractLimits.AuthSubjectIdMaxLength)
        {
            yield return "AuthSubjectId is required and must be within the supported limit.";
        }
    }
}

namespace BunkFy.Modules.DataRights.Application.Validation;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using Gma.Framework.Cqrs;

internal sealed class CreateDataRightsCaseCommandValidator
    : ICommandValidator<CreateDataRightsCaseCommand>
{
    private const DataRightsOperation KnownOperations =
        DataRightsOperation.AccessExport |
        DataRightsOperation.Correction |
        DataRightsOperation.Restriction |
        DataRightsOperation.Erasure |
        DataRightsOperation.Anonymisation;

    public IEnumerable<string> Validate(CreateDataRightsCaseCommand command)
    {
        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        if (command.Scope is null)
        {
            yield return "Scope is required.";
        }
        else if (command.Scope.CaseType is not DataRightsCaseType.GuestRights
            and not DataRightsCaseType.StaffRights)
        {
            yield return "CaseType is invalid.";
        }

        if (command.RequestedOperations == DataRightsOperation.None ||
            (command.RequestedOperations & ~KnownOperations) != DataRightsOperation.None)
        {
            yield return "RequestedOperations is invalid.";
        }

        if (command.Scope?.CaseType == DataRightsCaseType.StaffRights &&
            command.RequestedOperations is not DataRightsOperation.AccessExport
                and not DataRightsOperation.Correction
                and not DataRightsOperation.Restriction
                and not DataRightsOperation.Anonymisation)
        {
            yield return "StaffRights supports one AccessExport, Correction, Restriction, or Anonymisation operation.";
        }

        bool restrictionRequested =
            (command.RequestedOperations & DataRightsOperation.Restriction) != DataRightsOperation.None;
        if ((restrictionRequested &&
                command.RestrictionDirective is not DataRightsRestrictionDirective.Apply
                    and not DataRightsRestrictionDirective.Release) ||
            (!restrictionRequested &&
                command.RestrictionDirective != DataRightsRestrictionDirective.Unknown))
        {
            yield return "RestrictionDirective must match the requested operations.";
        }

        if (command.RequesterRelationship == DataRightsRequesterRelationship.Unknown ||
            !Enum.IsDefined(command.RequesterRelationship) ||
            command.RequesterRelationship == DataRightsRequesterRelationship.TenantOwner)
        {
            yield return "RequesterRelationship is invalid.";
        }

        string actor = command.ActorId?.Trim() ?? string.Empty;
        if (actor.Length is 0 or > DataRightsCase.ActorIdMaxLength)
        {
            yield return "ActorId is required and must be within the supported limit.";
        }
    }
}

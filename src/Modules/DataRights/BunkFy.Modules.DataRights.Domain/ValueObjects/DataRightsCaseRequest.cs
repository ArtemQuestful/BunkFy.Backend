namespace BunkFy.Modules.DataRights.Domain.ValueObjects;

using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Results;

public sealed class DataRightsCaseRequest
{
    private const DataRightsCaseOperation KnownOperations =
        DataRightsCaseOperation.AccessExport |
        DataRightsCaseOperation.Correction |
        DataRightsCaseOperation.Restriction |
        DataRightsCaseOperation.Erasure |
        DataRightsCaseOperation.Anonymisation;

    private DataRightsCaseRequest(
        Guid? propertyId,
        DataRightsCaseKind kind,
        DataRightsCaseOperation requestedOperations,
        DataRightsRestrictionAction restrictionAction,
        DataRightsRequesterRelation requesterRelationship)
    {
        this.PropertyId = propertyId;
        this.Kind = kind;
        this.RequestedOperations = requestedOperations;
        this.RestrictionAction = restrictionAction;
        this.RequesterRelationship = requesterRelationship;
    }

    public Guid? PropertyId { get; }
    public DataRightsCaseKind Kind { get; }
    public DataRightsCaseOperation RequestedOperations { get; }
    public DataRightsRestrictionAction RestrictionAction { get; }
    public DataRightsRequesterRelation RequesterRelationship { get; }

    public static Result<DataRightsCaseRequest> Create(
        Guid? propertyId,
        DataRightsCaseKind kind,
        DataRightsCaseOperation requestedOperations,
        DataRightsRequesterRelation requesterRelationship,
        DataRightsRestrictionAction restrictionAction = DataRightsRestrictionAction.None)
    {
        if (kind is not DataRightsCaseKind.GuestRights
            and not DataRightsCaseKind.TenantTermination
            and not DataRightsCaseKind.StaffRights)
        {
            return Result.Failure<DataRightsCaseRequest>(DataRightsDomainErrors.CaseTypeInvalid);
        }

        if (requestedOperations == DataRightsCaseOperation.None ||
            (requestedOperations & ~KnownOperations) != DataRightsCaseOperation.None)
        {
            return Result.Failure<DataRightsCaseRequest>(DataRightsDomainErrors.OperationsInvalid);
        }

        bool restrictionRequested =
            (requestedOperations & DataRightsCaseOperation.Restriction) != DataRightsCaseOperation.None;
        if (restrictionRequested && requestedOperations != DataRightsCaseOperation.Restriction)
        {
            return Result.Failure<DataRightsCaseRequest>(DataRightsDomainErrors.OperationsInvalid);
        }

        if ((restrictionRequested &&
                restrictionAction is not DataRightsRestrictionAction.Apply
                    and not DataRightsRestrictionAction.Release) ||
            (!restrictionRequested && restrictionAction != DataRightsRestrictionAction.None))
        {
            return Result.Failure<DataRightsCaseRequest>(
                DataRightsDomainErrors.RestrictionDirectiveInvalid);
        }

        if (requesterRelationship is not DataRightsRequesterRelation.DataSubject
            and not DataRightsRequesterRelation.AuthorizedRepresentative
            and not DataRightsRequesterRelation.ControllerInitiated
            and not DataRightsRequesterRelation.TenantOwner)
        {
            return Result.Failure<DataRightsCaseRequest>(
                DataRightsDomainErrors.RequesterRelationshipInvalid);
        }

        if (kind == DataRightsCaseKind.GuestRights &&
            (!propertyId.HasValue || propertyId.Value == Guid.Empty))
        {
            return Result.Failure<DataRightsCaseRequest>(DataRightsDomainErrors.PropertyRequired);
        }

        if (kind == DataRightsCaseKind.GuestRights &&
            requesterRelationship == DataRightsRequesterRelation.TenantOwner)
        {
            return Result.Failure<DataRightsCaseRequest>(
                DataRightsDomainErrors.GuestRightsRequesterInvalid);
        }

        if ((kind is DataRightsCaseKind.StaffRights or DataRightsCaseKind.TenantTermination) &&
            propertyId.HasValue)
        {
            return Result.Failure<DataRightsCaseRequest>(DataRightsDomainErrors.PropertyNotAllowed);
        }

        if (kind == DataRightsCaseKind.StaffRights &&
            requestedOperations is not DataRightsCaseOperation.AccessExport
                and not DataRightsCaseOperation.Correction
                and not DataRightsCaseOperation.Restriction
                and not DataRightsCaseOperation.Anonymisation)
        {
            return Result.Failure<DataRightsCaseRequest>(
                DataRightsDomainErrors.StaffRightsOperationsInvalid);
        }

        if (kind == DataRightsCaseKind.StaffRights &&
            requesterRelationship == DataRightsRequesterRelation.TenantOwner)
        {
            return Result.Failure<DataRightsCaseRequest>(
                DataRightsDomainErrors.StaffRightsRequesterInvalid);
        }

        if (kind == DataRightsCaseKind.TenantTermination &&
            requesterRelationship is not DataRightsRequesterRelation.ControllerInitiated
                and not DataRightsRequesterRelation.TenantOwner)
        {
            return Result.Failure<DataRightsCaseRequest>(
                DataRightsDomainErrors.TenantTerminationRequesterInvalid);
        }

        if (kind == DataRightsCaseKind.TenantTermination &&
            requestedOperations != DataRightsCaseOperation.Anonymisation)
        {
            return Result.Failure<DataRightsCaseRequest>(
                DataRightsDomainErrors.OperationsInvalid);
        }

        return Result.Success(new DataRightsCaseRequest(
            propertyId,
            kind,
            requestedOperations,
            restrictionAction,
            requesterRelationship));
    }
}

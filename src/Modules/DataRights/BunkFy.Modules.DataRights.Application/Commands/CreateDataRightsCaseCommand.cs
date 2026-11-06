namespace BunkFy.Modules.DataRights.Application.Commands;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;

public sealed record CreateDataRightsCaseCommand(
    Guid OperationId,
    DataRightsCaseScope Scope,
    DataRightsOperation RequestedOperations,
    DataRightsRestrictionDirective RestrictionDirective,
    DataRightsRequesterRelationship RequesterRelationship,
    string ActorId) : ITransactionalCommand<DataRightsCaseDto>;

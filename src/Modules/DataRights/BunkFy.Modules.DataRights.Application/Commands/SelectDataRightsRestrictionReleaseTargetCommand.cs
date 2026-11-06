namespace BunkFy.Modules.DataRights.Application.Commands;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;

public sealed record SelectDataRightsRestrictionReleaseTargetCommand(
    DataRightsCaseScope Scope,
    Guid CaseId,
    Guid OwnerOperationId,
    long OwnerOperationVersion,
    long ExpectedVersion,
    string ActorId) : ITransactionalCommand<DataRightsCaseDto>;

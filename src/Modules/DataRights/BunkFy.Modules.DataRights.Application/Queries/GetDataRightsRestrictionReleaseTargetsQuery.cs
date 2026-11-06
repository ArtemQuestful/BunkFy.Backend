namespace BunkFy.Modules.DataRights.Application.Queries;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;

public sealed record GetDataRightsRestrictionReleaseTargetsQuery(
    DataRightsCaseScope Scope,
    Guid CaseId) : IQuery<DataRightsRestrictionReleaseTargetListResponse>;

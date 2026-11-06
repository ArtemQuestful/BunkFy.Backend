namespace BunkFy.Modules.DataRights.Application.Queries;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Application.Models;
using Gma.Framework.Cqrs;

public sealed record GetDataRightsCorrectionExecutionQuery(
    DataRightsCaseScope Scope,
    Guid CaseId,
    string ActorId) : IQuery<DataRightsCorrectionExecutionDetailsDto>;

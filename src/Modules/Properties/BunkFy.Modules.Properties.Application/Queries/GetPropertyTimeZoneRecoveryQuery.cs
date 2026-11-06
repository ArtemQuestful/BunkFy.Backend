namespace BunkFy.Modules.Properties.Application.Queries;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;

public sealed record GetPropertyTimeZoneRecoveryQuery(
    Guid PropertyId,
    Guid OperationId)
    : IQuery<PropertyTimeZoneRecoveryDto>;

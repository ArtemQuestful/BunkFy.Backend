namespace BunkFy.Modules.Properties.Application.Commands;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;

public sealed record CreatePropertyCommand(
    Guid OperationId,
    string Name,
    string Code,
    string TimeZoneId,
    string ActorId)
    : ITransactionalCommand<PropertyMutationReceiptDto>;

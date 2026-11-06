namespace BunkFy.Modules.Properties.Application.Commands;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;

public sealed record UpdatePropertyCommand(
    Guid PropertyId,
    Guid OperationId,
    string Name,
    string Code,
    string? TimeZoneId,
    long ExpectedVersion)
    : ITransactionalCommand<PropertyMutationReceiptDto>;

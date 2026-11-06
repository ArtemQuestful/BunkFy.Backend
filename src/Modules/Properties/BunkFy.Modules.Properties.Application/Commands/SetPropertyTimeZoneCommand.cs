namespace BunkFy.Modules.Properties.Application.Commands;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;

public sealed record SetPropertyTimeZoneCommand(
    Guid PropertyId,
    Guid OperationId,
    string TimeZoneId,
    bool Confirmed,
    long ExpectedVersion,
    string ActorId)
    : ITransactionalCommand<SetPropertyTimeZoneReceiptDto>;

namespace BunkFy.Modules.Staff.Application.Commands;

using BunkFy.Modules.Staff.Application.Ports;
using Gma.Framework.Cqrs;

public sealed record BootstrapStaffIdentityCommand(
    Guid OperationId,
    Guid SourceId,
    string AuthSubjectId,
    string DisplayName,
    string? WorkEmail,
    string ActorId) : ITransactionalCommand<Unit>,
    IStaffPersistenceRetryableCommand;

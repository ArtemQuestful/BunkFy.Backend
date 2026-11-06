namespace BunkFy.Modules.Guests.Application.Commands;

using BunkFy.Modules.Guests.Contracts;
using Gma.Framework.Cqrs;

public sealed record ReleaseGuestProcessingRestrictionCommand(
    Guid IdempotencyKey,
    Guid PropertyId,
    Guid RestrictionId,
    Guid CaseId,
    long ApprovalRevision,
    Guid GuestId,
    long ExpectedGuestVersion,
    long ExpectedRestrictionVersion,
    long ExpectedProjectionRevision,
    string ActorId,
    bool LegacyUnboundTarget = false)
    : ITransactionalCommand<GuestProcessingRestrictionReceiptDto>;

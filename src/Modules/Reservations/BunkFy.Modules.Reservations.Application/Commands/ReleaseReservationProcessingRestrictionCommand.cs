namespace BunkFy.Modules.Reservations.Application.Commands;

using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;

public sealed record ReleaseReservationProcessingRestrictionCommand(
    Guid IdempotencyKey,
    Guid PropertyId,
    Guid RestrictionId,
    Guid CaseId,
    long ApprovalRevision,
    Guid ReservationId,
    long ExpectedReservationVersion,
    long ExpectedRestrictionVersion,
    long ExpectedProjectionRevision,
    string ActorId,
    bool LegacyUnboundTarget = false)
    : ITransactionalCommand<ReservationProcessingRestrictionReceiptDto>;

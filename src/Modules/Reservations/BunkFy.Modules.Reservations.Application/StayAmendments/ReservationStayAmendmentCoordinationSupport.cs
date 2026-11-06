namespace BunkFy.Modules.Reservations.Application.StayAmendments;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using Gma.Framework.Results;

internal static class ReservationStayAmendmentCoordinationSupport
{
    public static async Task<Result> ValidateSelectionAsync(
        IInventoryProjectionRepository inventoryProjection,
        Guid propertyId,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        CancellationToken cancellationToken)
    {
        InventoryUnitSelectionValidation selection = await inventoryProjection.ValidateSelectionAsync(
            propertyId,
            inventoryUnitIds,
            cancellationToken).ConfigureAwait(false);
        return selection switch
        {
            InventoryUnitSelectionValidation.Valid => Result.Success(),
            InventoryUnitSelectionValidation.UnitNotFound => Result.Failure(
                ReservationsApplicationErrors.InventoryUnitNotFound),
            _ => Result.Failure(ReservationsApplicationErrors.InventoryUnitPropertyMismatch)
        };
    }

    public static ReservationManagementOperationRecord CreateManagementOperation(
        Reservation reservation,
        Guid operationId,
        ReservationManagementOperationKind kind,
        long expectedDetailsRevision,
        string fingerprint,
        DateTimeOffset createdAtUtc) => new(
            operationId,
            reservation.ScopeId,
            reservation.PropertyId,
            reservation.Id,
            kind,
            ExpectedVersion: null,
            expectedDetailsRevision,
            BusinessDate: null,
            createdAtUtc,
            fingerprint);

    public static bool IsValidActor(string actorId) =>
        actorId.Length is > 0 and <= Reservation.ActorIdMaxLength &&
        !actorId.Any(char.IsControl);
}

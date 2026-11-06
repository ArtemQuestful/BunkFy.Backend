namespace BunkFy.Modules.Reservations.Application.Stations;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts.Stations;
using Gma.Framework.Scoping;

/// <summary>Guest-free receipt/provenance and lifecycle projection reads. No mutation, guest reader or dispatcher.</summary>
internal sealed class StationCheckOutOutcomeReader(IReservationManagementOperationRepository operations, IScopeContext scope)
    : IStationCheckOutOutcomeReader
{
    public async Task<StationCheckOutOutcome> ResolveAsync(Guid propertyId, Guid stationId, Guid browserSessionId,
        Guid actorSessionId, long generation, Guid reservationId, Guid operationId, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!scope.IsEnabled || !Guid.TryParseExact(scope.ScopeId, "D", out Guid tenant) || tenant == Guid.Empty ||
            scope.ScopeId != tenant.ToString("D") || propertyId == Guid.Empty || stationId == Guid.Empty ||
            browserSessionId == Guid.Empty || actorSessionId == Guid.Empty || generation < 1 ||
            reservationId == Guid.Empty || operationId == Guid.Empty || expectedVersion < 1)
        { return new(StationCheckOutOutcomeState.Conflict); }
        string tenantId = scope.ScopeId;
        try
        {
            var receipt = await operations.GetAsync(reservationId, operationId, cancellationToken).ConfigureAwait(false);
            var attribution = await operations.GetStationAttributionAsync(reservationId, operationId, cancellationToken).ConfigureAwait(false);
            if (!attribution.Supported)
            { return new(StationCheckOutOutcomeState.Unavailable); }
            if (!scope.IsEnabled || scope.ScopeId != tenantId)
            { return new(StationCheckOutOutcomeState.Conflict); }
            if (receipt is null)
            {
                // A receipt may commit between these reads. Absence at the first read is never terminal proof.
                return new(StationCheckOutOutcomeState.Pending);
            }
            var original = attribution.Provenance;
            bool matches = receipt.ScopeId == tenantId && receipt.PropertyId == propertyId &&
                receipt.ReservationId == reservationId && receipt.OperationId == operationId &&
                receipt.BusinessDate is { } date && date != default &&
                receipt.MatchesLifecycle(ReservationManagementOperationKind.CheckOut, expectedVersion, date) &&
                original is { IsValid: true } && original.StationId == stationId && original.BrowserSessionId == browserSessionId &&
                original.ActorSessionId == actorSessionId && original.Generation == generation;
            // Staff ID and authority come from the persisted attribution, never caller storage or a new actor.
            if (!matches || expectedVersion == long.MaxValue || attribution.ResultingVersion != expectedVersion + 1 ||
                (original!.Authority == StationReservationAuthority.StationOnly && original.JobGrantRevision is null))
            { return new(StationCheckOutOutcomeState.Conflict); }
            var state = await operations.ReadCheckOutStateAsync(propertyId, reservationId, attribution.ResultingVersion, cancellationToken).ConfigureAwait(false);
            return new(scope.IsEnabled && scope.ScopeId == tenantId && Enum.IsDefined(state)
                ? state : StationCheckOutOutcomeState.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationCheckOutOutcomeState.Unavailable); }
    }
}

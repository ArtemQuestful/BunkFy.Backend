namespace BunkFy.Modules.Reservations.Application.Stations;

using BunkFy.DataGovernance;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts.Stations;
using Gma.Framework.Cqrs;

internal sealed class StationReservationOperations(IStationDueArrivalRepository arrivals,
    IReservationManagementOperationRepository operations, IReservationCountryPolicyAdmission country,
    IRequestDispatcher dispatcher) : IStationReservationOperations
{
    public async Task<StationDueArrivalPage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
        StationArrivalCursor? after, CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await this.AdmitAsync(propertyId, cancellationToken).ConfigureAwait(false);
            return state == StationReservationState.Ready
                ? await arrivals.ListAsync(propertyId, localDate, pageSize, after, cancellationToken).ConfigureAwait(false)
                : new(state, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationReservationState.Unavailable, []); }
    }

    public async Task<StationCheckInPreparation> PrepareAsync(Guid propertyId, Guid reservationId, Guid operationId,
        long expectedVersion, DateOnly localDate, StationCheckInProvenance provenance, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || reservationId == Guid.Empty || expectedVersion < 1 || localDate == default || !StationOperationProvenance.ValidNew(provenance))
        { return new(StationReservationState.Conflict, localDate); }
        try
        {
            var state = await this.AdmitAsync(propertyId, cancellationToken).ConfigureAwait(false);
            if (state != StationReservationState.Ready)
            { return new(state, localDate); }
            if (!await arrivals.IsVisibleAsync(propertyId, reservationId, cancellationToken).ConfigureAwait(false))
            { return new(StationReservationState.Denied, localDate); }
            var prior = await operations.GetAsync(reservationId, operationId, cancellationToken).ConfigureAwait(false);
            if (prior is not null)
            {
                var attribution = await operations.GetStationAttributionAsync(reservationId, operationId, cancellationToken).ConfigureAwait(false);
                if (prior.PropertyId != propertyId || prior.BusinessDate is not { } originalDate ||
                    !prior.MatchesLifecycle(ReservationManagementOperationKind.CheckIn, expectedVersion, originalDate) ||
                    !attribution.Supported || !StationOperationProvenance.Matches(ReservationManagementOperationKind.CheckIn, attribution.Provenance, provenance))
                { return new(StationReservationState.Conflict, localDate); }
                // Outcome recovery uses the original server-owned operation date, never a caller's historical date.
                // The dispatcher still locks/reloads and checks the parent/provenance again.
                return new(StationReservationState.Ready, originalDate, Replay: true);
            }
            var target = await arrivals.FindAsync(propertyId, reservationId, localDate, cancellationToken).ConfigureAwait(false);
            return target is not null && target.ExpectedVersion == expectedVersion
                ? new(StationReservationState.Ready, localDate, target)
                : new(StationReservationState.Incomplete, localDate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationReservationState.Unavailable, localDate); }
    }

    public async Task<StationCheckInResult> CheckInAsync(Guid propertyId, Guid reservationId, Guid operationId, long expectedVersion,
        StationCheckInPreparation preparation, StationCheckInProvenance provenance, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || reservationId == Guid.Empty || expectedVersion < 1 || !StationOperationProvenance.ValidNew(provenance) ||
            preparation.State != StationReservationState.Ready || preparation.BusinessDate == default ||
            (!preparation.Replay && (preparation.Arrival?.ReservationId != reservationId || preparation.Arrival.ExpectedVersion != expectedVersion)))
        { return new(StationReservationState.Conflict); }
        try
        {
            var state = await this.AdmitAsync(propertyId, cancellationToken).ConfigureAwait(false);
            if (state != StationReservationState.Ready)
            { return new(state); }
            var result = await dispatcher.SendAsync(new StationCheckInReservationCommand(operationId, propertyId, reservationId,
                preparation.BusinessDate, expectedVersion, preparation.Arrival?.AllocationId, preparation.Arrival?.AllocationVersion,
                provenance), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? new(StationReservationState.Applied, result.Value) : new(StationReservationState.Conflict);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationReservationState.Unavailable); }
    }

    private async Task<StationReservationState> AdmitAsync(Guid propertyId, CancellationToken ct)
    {
        if (!arrivals.SupportsStationOperations)
        { return StationReservationState.Unsupported; }
        if (propertyId == Guid.Empty)
        { return StationReservationState.Denied; }
        // This narrow queue exists only for the admitted check-in job, so its operational write policy is a prerequisite too.
        var policy = await country.EvaluateAsync(propertyId, ReservationCountryPolicyAdmission.ReservationManagementPurpose,
            CountryPolicySurface.ApiWrite, ReservationCountryPolicyAdmission.AuthorizedOperatorProvenance, ct).ConfigureAwait(false);
        return policy.IsAllowed ? StationReservationState.Ready : StationReservationState.Denied;
    }
}

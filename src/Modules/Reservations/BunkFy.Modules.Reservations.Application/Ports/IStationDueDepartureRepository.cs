namespace BunkFy.Modules.Reservations.Application.Ports;

using BunkFy.Modules.Reservations.Contracts.Stations;

public interface IStationDueDepartureRepository
{
    bool SupportsStationOperations { get; }
    Task<StationDueDeparturePage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
        StationDepartureCursor? after, CancellationToken cancellationToken);
    Task<StationDueDeparture?> FindAsync(Guid propertyId, Guid reservationId, DateOnly localDate, CancellationToken cancellationToken);
    Task<bool> IsVisibleAsync(Guid propertyId, Guid reservationId, CancellationToken cancellationToken);
}

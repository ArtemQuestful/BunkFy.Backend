namespace BunkFy.Modules.Reservations.Application.Ports;

using BunkFy.Modules.Reservations.Contracts.Stations;

public interface IStationDueArrivalRepository
{
    bool SupportsStationOperations { get; }
    Task<StationDueArrivalPage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
        StationArrivalCursor? after, CancellationToken cancellationToken);
    Task<StationDueArrival?> FindAsync(Guid propertyId, Guid reservationId, DateOnly localDate, CancellationToken cancellationToken);
    Task<bool> IsVisibleAsync(Guid propertyId, Guid reservationId, CancellationToken cancellationToken);
}

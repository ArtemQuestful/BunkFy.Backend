namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts.Stations;
using Microsoft.EntityFrameworkCore;

internal sealed class ReservationManagementOperationRepository(
    ReservationsDbContext dbContext)
    : IReservationManagementOperationRepository
{
    public async Task<ReservationManagementOperationRecord?> GetAsync(
        Guid reservationId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ReservationManagementOperation? operation = await this.ParentQuery
            .SingleOrDefaultAsync(
                item => item.ReservationId == reservationId &&
                    item.Id == operationId,
                cancellationToken)
            .ConfigureAwait(false);
        return operation?.ToRecord();
    }

    public Task AddAsync(
        ReservationManagementOperationRecord operation,
        CancellationToken cancellationToken)
    {
        dbContext.Set<ReservationManagementOperation>()
            .Add(new ReservationManagementOperation(operation));
        return Task.CompletedTask;
    }

    internal IQueryable<ReservationManagementOperation> ParentQuery => dbContext.Set<ReservationManagementOperation>().AsNoTracking();
    internal IQueryable<ReservationStationAttribution>? AttributionQuery => dbContext.Database.IsNpgsql()
        ? dbContext.Set<ReservationStationAttribution>().AsNoTracking() : null;

    public async Task<ReservationStationAttributionRead> GetStationAttributionAsync(Guid reservationId, Guid operationId,
        CancellationToken cancellationToken)
    {
        if (this.AttributionQuery is not { } query)
        { return new(false); }
        var row = await query.SingleOrDefaultAsync(x => x.ReservationId == reservationId && x.OperationId == operationId,
            cancellationToken).ConfigureAwait(false);
        return new(true, row?.ToProvenance(), row?.ResultingVersion ?? 0);
    }

    public async Task<StationCheckOutOutcomeState> ReadCheckOutStateAsync(Guid propertyId, Guid reservationId,
        long resultingVersion, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsNpgsql() || resultingVersion < 1)
        { return StationCheckOutOutcomeState.Unavailable; }
        var current = await ReservationVisibilityQueries.Ordinary(dbContext).AsNoTracking()
            .Where(x => x.PropertyId == propertyId && x.Id == reservationId)
            .Select(x => new { x.Status, x.Version, Rejected = x.LastReleaseRejectionCode != null })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
        { return StationCheckOutOutcomeState.Unavailable; }
        if (current.Status == BunkFy.Modules.Reservations.Domain.Aggregates.ReservationState.CheckoutPending &&
            current.Version == resultingVersion)
        { return StationCheckOutOutcomeState.ReleasePending; }
        if (resultingVersion < long.MaxValue && current.Version == resultingVersion + 1)
        {
            if (current.Status == BunkFy.Modules.Reservations.Domain.Aggregates.ReservationState.CheckedOut)
            { return StationCheckOutOutcomeState.Completed; }
            if (current.Status == BunkFy.Modules.Reservations.Domain.Aggregates.ReservationState.CheckedIn && current.Rejected)
            { return StationCheckOutOutcomeState.ReleaseRejected; }
        }
        // A later lifecycle cannot be attributed to this request merely from its current status.
        return StationCheckOutOutcomeState.Recorded;
    }

    public Task AddStationAsync(ReservationManagementOperationRecord operation, StationCheckInProvenance provenance,
        long resultingVersion, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsNpgsql())
        { throw new NotSupportedException("Station attribution requires PostgreSQL."); }
        if (operation.Kind is not (ReservationManagementOperationKind.CheckIn or ReservationManagementOperationKind.CheckOut) || operation.RequestFingerprint is not null ||
            !provenance.IsValid || (provenance.Authority == StationReservationAuthority.StationOnly && provenance.JobGrantRevision is not > 0))
        { throw new ArgumentException("Invalid attributed operation.", nameof(operation)); }
        dbContext.Set<ReservationManagementOperation>().Add(new ReservationManagementOperation(operation));
        dbContext.Set<ReservationStationAttribution>().Add(new(operation.ScopeId, operation.ReservationId,
            operation.OperationId, provenance, resultingVersion));
        return Task.CompletedTask;
    }
}

namespace BunkFy.Modules.Reservations.Domain.Aggregates;

using BunkFy.Modules.Reservations.Domain.Errors;
using BunkFy.Modules.Reservations.Domain.Events;
using Gma.Framework.Results;

public sealed partial class Reservation
{
    public Result AdvanceStayAmendmentEvidenceCoordinate(DateTimeOffset nowUtc)
    {
        if (nowUtc == default || nowUtc < this.UpdatedAtUtc)
        {
            return Result.Failure(ReservationsDomainErrors.AllocationAmendmentInvalid);
        }

        this.Version++;
        this.UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    public Result RepublishPendingAllocationAmendment(
        Guid amendmentRequestId,
        string requestFingerprint,
        DateOnly arrival,
        DateOnly departure,
        TimeOnly? expectedArrivalTime,
        TimeOnly? expectedDepartureTime,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(inventoryUnitIds);
        Guid[] units = inventoryUnitIds.ToArray();
        if (amendmentRequestId == Guid.Empty || eventId == Guid.Empty || nowUtc == default ||
            nowUtc < this.UpdatedAtUtc ||
            this.Status != ReservationState.Confirmed || this.AllocationId is null || this.AllocationVersion is null ||
            this.PendingAllocationAmendmentId != amendmentRequestId ||
            !this.PendingInventoryAmendmentRequestId.HasValue ||
            this.PendingDetailsChangeOrigin != ReservationDetailsChangeOrigin.Staff ||
            !string.Equals(
                this.PendingAllocationAmendmentRequestFingerprint,
                requestFingerprint,
                StringComparison.Ordinal) ||
            this.PendingArrival != arrival || this.PendingDeparture != departure ||
            this.PendingExpectedArrivalTime != expectedArrivalTime ||
            this.PendingExpectedDepartureTime != expectedDepartureTime ||
            string.IsNullOrWhiteSpace(this.PendingInventoryUnitIds) ||
            units.Length is 0 or > MaximumRequestedUnits || units.Any(id => id == Guid.Empty) ||
            units.Distinct().Count() != units.Length)
        {
            return Result.Failure(ReservationsDomainErrors.AllocationAmendmentInvalid);
        }

        Guid[] pendingUnits = this.PendingInventoryUnitIds.Split(',')
            .Select(value => Guid.ParseExact(value, "N"))
            .ToArray();
        if (!pendingUnits.Order().SequenceEqual(units.Order()))
        {
            return Result.Failure(ReservationsDomainErrors.AllocationAmendmentInvalid);
        }

        this.Version++;
        this.UpdatedAtUtc = nowUtc;
        this.RaiseDomainEvent(new ReservationAllocationAmendmentRequestedDomainEvent(
            eventId,
            nowUtc,
            this.ScopeId,
            this.Id,
            this.PropertyId,
            this.PendingInventoryAmendmentRequestId.Value,
            this.AllocationId.Value,
            this.AllocationVersion.Value,
            arrival,
            departure,
            units));
        return Result.Success();
    }
}

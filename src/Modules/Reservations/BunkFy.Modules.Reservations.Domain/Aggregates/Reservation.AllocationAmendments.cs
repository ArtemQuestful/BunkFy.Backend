namespace BunkFy.Modules.Reservations.Domain.Aggregates;

using Gma.Framework.Results;
using BunkFy.Modules.Reservations.Domain.Entities;
using BunkFy.Modules.Reservations.Domain.Errors;
using BunkFy.Modules.Reservations.Domain.Events;

public sealed partial class Reservation
{
    public Result<ReservationDetailsChangeOutcome> BeginAllocationAmendment(
        Guid amendmentRequestId,
        Guid inventoryRequestId,
        string requestFingerprint,
        DateOnly arrival,
        DateOnly departure,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        string primaryGuestName,
        string? email,
        string? phone,
        int guestCount,
        string? notes,
        long expectedDetailsRevision,
        ReservationDetailsChangeOrigin origin,
        string actorId,
        Guid? adapterConnectionId,
        Guid? externalOperationId,
        Guid correlationId,
        Guid eventId,
        DateTimeOffset nowUtc,
        TimeOnly? expectedArrivalTime = null,
        TimeOnly? expectedDepartureTime = null)
    {
        string normalizedFingerprint = requestFingerprint?.Trim().ToLowerInvariant() ?? string.Empty;
        if (this.PendingAllocationAmendmentId == amendmentRequestId &&
            string.Equals(this.PendingAllocationAmendmentRequestFingerprint, normalizedFingerprint, StringComparison.Ordinal))
        {
            return Result.Success(ReservationDetailsChangeOutcome.Changed);
        }

        if (this.PendingAllocationAmendmentId.HasValue)
        {
            return Result.Failure<ReservationDetailsChangeOutcome>(ReservationsDomainErrors.AllocationAmendmentInProgress);
        }

        if (this.Status != ReservationState.Confirmed || this.AllocationId is null || this.AllocationVersion is null ||
            expectedDetailsRevision != this.DetailsRevision || amendmentRequestId == Guid.Empty ||
            inventoryRequestId == Guid.Empty || eventId == Guid.Empty ||
            nowUtc == default || nowUtc < this.UpdatedAtUtc ||
            normalizedFingerprint.Length != RequestFingerprintLength || !normalizedFingerprint.All(Uri.IsHexDigit))
        {
            return Result.Failure<ReservationDetailsChangeOutcome>(
                expectedDetailsRevision != this.DetailsRevision
                    ? ReservationsDomainErrors.DetailsRevisionConflict
                    : ReservationsDomainErrors.AllocationAmendmentInvalid);
        }

        string normalizedActorId = actorId?.Trim() ?? string.Empty;
        bool adapterOrigin = origin == ReservationDetailsChangeOrigin.Adapter;
        if (origin == ReservationDetailsChangeOrigin.Unknown || !Enum.IsDefined(origin) ||
            normalizedActorId.Length is 0 or > ActorIdMaxLength || correlationId == Guid.Empty ||
            adapterOrigin != adapterConnectionId.HasValue || adapterOrigin != externalOperationId.HasValue ||
            adapterConnectionId == Guid.Empty || externalOperationId == Guid.Empty)
        {
            return Result.Failure<ReservationDetailsChangeOutcome>(ReservationsDomainErrors.DetailsChangeProvenanceInvalid);
        }

        if (arrival >= departure)
        {
            return Result.Failure<ReservationDetailsChangeOutcome>(ReservationsDomainErrors.StayRangeInvalid);
        }

        if (!HasMinutePrecision(expectedArrivalTime) || !HasMinutePrecision(expectedDepartureTime))
        {
            return Result.Failure<ReservationDetailsChangeOutcome>(ReservationsDomainErrors.ExpectedStayTimeInvalid);
        }

        ArgumentNullException.ThrowIfNull(inventoryUnitIds);
        Guid[] units = inventoryUnitIds.ToArray();
        if (units.Length is 0 or > MaximumRequestedUnits || units.Any(id => id == Guid.Empty) ||
            units.Distinct().Count() != units.Length)
        {
            return Result.Failure<ReservationDetailsChangeOutcome>(ReservationsDomainErrors.RequestedUnitsInvalid);
        }

        string normalizedGuestName = NormalizeRequired(primaryGuestName);
        string? normalizedEmail = NormalizeOptional(email);
        string? normalizedPhone = NormalizeOptional(phone);
        string? normalizedNotes = NormalizeOptional(notes);
        if (normalizedGuestName.Length is 0 or > PrimaryGuestNameMaxLength || normalizedEmail?.Length > EmailMaxLength ||
            normalizedPhone?.Length > PhoneMaxLength || normalizedNotes?.Length > NotesMaxLength || guestCount <= 0)
        {
            return Result.Failure<ReservationDetailsChangeOutcome>(ReservationsDomainErrors.AllocationAmendmentInvalid);
        }

        bool unchanged = this.Arrival == arrival && this.Departure == departure &&
            this.ExpectedArrivalTime == expectedArrivalTime && this.ExpectedDepartureTime == expectedDepartureTime &&
            this.requestedUnits.Select(unit => unit.InventoryUnitId).Order().SequenceEqual(units.Order()) &&
            this.PrimaryGuestName == normalizedGuestName && this.Email == normalizedEmail && this.Phone == normalizedPhone &&
            this.GuestCount == guestCount && this.Notes == normalizedNotes;
        if (unchanged)
        {
            return Result.Success(ReservationDetailsChangeOutcome.Unchanged);
        }

        this.PendingAllocationAmendmentId = amendmentRequestId;
        this.PendingInventoryAmendmentRequestId = inventoryRequestId;
        this.PendingAllocationAmendmentRequestFingerprint = normalizedFingerprint;
        this.PendingArrival = arrival;
        this.PendingDeparture = departure;
        this.PendingExpectedArrivalTime = expectedArrivalTime;
        this.PendingExpectedDepartureTime = expectedDepartureTime;
        this.PendingInventoryUnitIds = string.Join(',', units.Order().Select(id => id.ToString("N")));
        this.PendingPrimaryGuestName = normalizedGuestName;
        this.PendingPrimaryGuestNameSearch = NormalizeSearch(normalizedGuestName);
        this.PendingEmail = normalizedEmail;
        this.PendingEmailSearch = NormalizeSearch(normalizedEmail);
        this.PendingPhone = normalizedPhone;
        this.PendingPhoneSearch = NormalizeSearch(normalizedPhone);
        this.PendingGuestCount = guestCount;
        this.PendingNotes = normalizedNotes;
        this.PendingDetailsChangeOrigin = origin;
        this.PendingDetailsActorId = normalizedActorId;
        this.PendingDetailsAdapterConnectionId = adapterConnectionId;
        this.PendingDetailsExternalOperationId = externalOperationId;
        this.PendingDetailsCorrelationId = correlationId;
        this.LastAllocationAmendmentRejectionCode = null;
        this.Version++;
        this.UpdatedAtUtc = nowUtc;
        this.RaiseDomainEvent(new ReservationAllocationAmendmentRequestedDomainEvent(
            eventId,
            nowUtc,
            this.ScopeId,
            this.Id,
            this.PropertyId,
            inventoryRequestId,
            this.AllocationId.Value,
            this.AllocationVersion.Value,
            arrival,
            departure,
            units));
        return Result.Success(ReservationDetailsChangeOutcome.Changed);
    }

    public Result CompleteAllocationAmendment(
        Guid inventoryRequestId,
        Guid allocationId,
        DateOnly arrival,
        DateOnly departure,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        long allocationVersion,
        Guid detailsEventId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(inventoryUnitIds);
        Guid[] confirmedUnits = inventoryUnitIds.ToArray();
        if (this.PendingInventoryAmendmentRequestId != inventoryRequestId || this.AllocationId != allocationId ||
            allocationVersion <= 0 || detailsEventId == Guid.Empty ||
            !this.PendingArrival.HasValue || !this.PendingDeparture.HasValue ||
            string.IsNullOrWhiteSpace(this.PendingInventoryUnitIds) || !this.PendingGuestCount.HasValue ||
            !this.PendingDetailsCorrelationId.HasValue || this.PendingArrival != arrival || this.PendingDeparture != departure)
        {
            return Result.Failure(ReservationsDomainErrors.AllocationCorrelationMismatch);
        }

        Guid[] units = this.PendingInventoryUnitIds.Split(',')
            .Select(value => Guid.ParseExact(value, "N"))
            .ToArray();
        if (!units.Order().SequenceEqual(confirmedUnits.Order()))
        {
            return Result.Failure(ReservationsDomainErrors.AllocationCorrelationMismatch);
        }

        bool allocationTargetUnchanged = this.Arrival == arrival &&
            this.Departure == departure &&
            this.requestedUnits.Select(unit => unit.InventoryUnitId).Order()
                .SequenceEqual(confirmedUnits.Order());
        if (!this.AllocationVersion.HasValue ||
            (!allocationTargetUnchanged && this.AllocationVersion == long.MaxValue) ||
            allocationVersion != (allocationTargetUnchanged
                ? this.AllocationVersion.Value
                : this.AllocationVersion.Value + 1))
        {
            return Result.Failure(ReservationsDomainErrors.AllocationCorrelationMismatch);
        }

        ReservationDetailsSnapshot before = this.CaptureDetails();
        List<string> changedFields = [];
        AddChanged(changedFields, nameof(this.Arrival), this.Arrival, this.PendingArrival.Value);
        AddChanged(changedFields, nameof(this.Departure), this.Departure, this.PendingDeparture.Value);
        AddChanged(changedFields, nameof(this.ExpectedArrivalTime), this.ExpectedArrivalTime, this.PendingExpectedArrivalTime);
        AddChanged(changedFields, nameof(this.ExpectedDepartureTime), this.ExpectedDepartureTime, this.PendingExpectedDepartureTime);
        if (!this.requestedUnits.Select(unit => unit.InventoryUnitId).Order().SequenceEqual(units.Order()))
        {
            changedFields.Add(nameof(this.RequestedUnits));
        }
        AddChanged(changedFields, nameof(this.PrimaryGuestName), this.PrimaryGuestName, this.PendingPrimaryGuestName!);
        AddChanged(changedFields, nameof(this.Email), this.Email, this.PendingEmail);
        AddChanged(changedFields, nameof(this.Phone), this.Phone, this.PendingPhone);
        AddChanged(changedFields, nameof(this.GuestCount), this.GuestCount, this.PendingGuestCount.Value);
        AddChanged(changedFields, nameof(this.Notes), this.Notes, this.PendingNotes);

        this.Arrival = this.PendingArrival.Value;
        this.Departure = this.PendingDeparture.Value;
        this.ExpectedArrivalTime = this.PendingExpectedArrivalTime;
        this.ExpectedDepartureTime = this.PendingExpectedDepartureTime;
        this.requestedUnits.Clear();
        this.requestedUnits.AddRange(units.Select(unitId => new RequestedInventoryUnit(unitId, this.ScopeId, this.Id)));
        this.PrimaryGuestName = this.PendingPrimaryGuestName!;
        this.PrimaryGuestNameSearch = this.PendingPrimaryGuestNameSearch!;
        this.Email = this.PendingEmail;
        this.EmailSearch = this.PendingEmailSearch;
        this.Phone = this.PendingPhone;
        this.PhoneSearch = this.PendingPhoneSearch;
        this.GuestCount = this.PendingGuestCount.Value;
        this.Notes = this.PendingNotes;
        this.AllocationVersion = allocationVersion;
        long fromRevision = this.DetailsRevision;
        this.DetailsRevision++;
        this.LastDetailsChangeOrigin = this.PendingDetailsChangeOrigin;
        this.LastDetailsActorId = this.PendingDetailsActorId;
        this.LastDetailsAdapterConnectionId = this.PendingDetailsAdapterConnectionId;
        this.LastDetailsExternalOperationId = this.PendingDetailsExternalOperationId;
        this.LastDetailsChangedAtUtc = nowUtc;
        Guid correlationId = this.PendingDetailsCorrelationId.Value;
        this.ClearPendingAllocationAmendment();
        this.Version++;
        this.UpdatedAtUtc = nowUtc;
        this.RaiseDomainEvent(new ReservationDetailsChangedDomainEvent(
            detailsEventId,
            nowUtc,
            this.ScopeId,
            this.Id,
            this.PropertyId,
            fromRevision,
            this.DetailsRevision,
            this.LastDetailsChangeOrigin,
            this.LastDetailsActorId,
            this.LastDetailsAdapterConnectionId,
            this.LastDetailsExternalOperationId,
            correlationId,
            changedFields,
            before,
            this.CaptureDetails()));
        this.RaiseGuestStayChanged(detailsEventId, nowUtc);
        return Result.Success();
    }

    public Result RejectAllocationAmendment(
        Guid inventoryRequestId,
        Guid allocationId,
        int rejectionCode,
        DateTimeOffset nowUtc)
    {
        if (this.PendingInventoryAmendmentRequestId != inventoryRequestId || this.AllocationId != allocationId)
        {
            return Result.Failure(ReservationsDomainErrors.AllocationCorrelationMismatch);
        }

        this.ClearPendingAllocationAmendment();
        this.LastAllocationAmendmentRejectionCode = rejectionCode;
        this.Version++;
        this.UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

}

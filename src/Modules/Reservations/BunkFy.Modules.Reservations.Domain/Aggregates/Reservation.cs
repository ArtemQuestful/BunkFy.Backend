namespace BunkFy.Modules.Reservations.Domain.Aggregates;

using Gma.Framework.Domain.Models;
using Gma.Framework.Results;
using BunkFy.Modules.Reservations.Domain.Entities;
using BunkFy.Modules.Reservations.Domain.Errors;
using BunkFy.Modules.Reservations.Domain.Events;

public sealed partial class Reservation : ScopedAggregateRoot<Guid>
{
    public const int PrimaryGuestNameMaxLength = 200;
    public const int EmailMaxLength = 320;
    public const int PhoneMaxLength = 50;
    public const int SourceSystemMaxLength = 100;
    public const int SourceReferenceMaxLength = 200;
    public const int NotesMaxLength = 2000;
    public const int ActorIdMaxLength = 200;
    public const int RequestFingerprintLength = 64;
    public const int PendingInventoryUnitIdsMaxLength = MaximumRequestedUnits * 33;
    public const int MaximumRequestedUnits = 100;
    public const string AnonymisedGuestName = "Anonymised guest";

    private readonly List<RequestedInventoryUnit> requestedUnits = [];
    private readonly List<ReservationGuest> guests = [];

    private Reservation() { }

    private Reservation(
        Guid reservationId,
        string scopeId,
        Guid propertyId,
        Guid allocationRequestId,
        DateOnly arrival,
        DateOnly departure,
        TimeOnly? expectedArrivalTime,
        TimeOnly? expectedDepartureTime,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        string primaryGuestName,
        string? email,
        string? phone,
        int guestCount,
        ReservationSource source,
        string? sourceSystem,
        string? sourceReference,
        string? notes,
        DateTimeOffset createdAtUtc)
        : base(reservationId, scopeId)
    {
        this.PropertyId = propertyId;
        this.AllocationRequestId = allocationRequestId;
        this.Arrival = arrival;
        this.Departure = departure;
        this.ExpectedArrivalTime = expectedArrivalTime;
        this.ExpectedDepartureTime = expectedDepartureTime;
        this.PrimaryGuestName = primaryGuestName;
        this.PrimaryGuestNameSearch = NormalizeSearch(primaryGuestName)!;
        this.Email = email;
        this.EmailSearch = NormalizeSearch(email);
        this.Phone = phone;
        this.PhoneSearch = NormalizeSearch(phone);
        this.GuestCount = guestCount;
        this.Source = source;
        this.SourceSystem = sourceSystem;
        this.SourceReference = sourceReference;
        this.Notes = notes;
        this.DetailsRevision = 1;
        this.LastDetailsChangeOrigin = ReservationDetailsChangeOrigin.Staff;
        this.LastDetailsChangedAtUtc = createdAtUtc;
        this.CreatedAtUtc = createdAtUtc;
        this.requestedUnits.AddRange(inventoryUnitIds.Select(unitId => new RequestedInventoryUnit(unitId, this.ScopeId, this.Id)));
    }

    public Guid PropertyId { get; private set; }
    public Guid AllocationRequestId { get; private set; }
    public Guid? AllocationId { get; private set; }
    public long? AllocationVersion { get; private set; }
    public ReservationAllocationRejection? AllocationRejection { get; private set; }
    public Guid? ReleaseRequestId { get; private set; }
    public int? LastReleaseRejectionCode { get; private set; }
    public string? PendingCancellationActorId { get; private set; }
    public DateOnly? PendingStayBusinessDate { get; private set; }
    public string? PendingStayActorId { get; private set; }
    public DateOnly? CheckedInBusinessDate { get; private set; }
    public DateTimeOffset? CheckedInAtUtc { get; private set; }
    public string? CheckedInBy { get; private set; }
    public DateOnly? NoShowBusinessDate { get; private set; }
    public DateTimeOffset? NoShowAtUtc { get; private set; }
    public string? NoShowBy { get; private set; }
    public DateOnly? CheckedOutBusinessDate { get; private set; }
    public DateTimeOffset? CheckedOutAtUtc { get; private set; }
    public string? CheckedOutBy { get; private set; }
    public DateOnly Arrival { get; private set; }
    public DateOnly Departure { get; private set; }
    public TimeOnly? ExpectedArrivalTime { get; private set; }
    public TimeOnly? ExpectedDepartureTime { get; private set; }
    public Guid? PendingAllocationAmendmentId { get; private set; }
    public Guid? PendingInventoryAmendmentRequestId { get; private set; }
    public string? PendingAllocationAmendmentRequestFingerprint { get; private set; }
    public DateOnly? PendingArrival { get; private set; }
    public DateOnly? PendingDeparture { get; private set; }
    public TimeOnly? PendingExpectedArrivalTime { get; private set; }
    public TimeOnly? PendingExpectedDepartureTime { get; private set; }
    public string? PendingInventoryUnitIds { get; private set; }
    public string? PendingPrimaryGuestName { get; private set; }
    public string? PendingPrimaryGuestNameSearch { get; private set; }
    public string? PendingEmail { get; private set; }
    public string? PendingEmailSearch { get; private set; }
    public string? PendingPhone { get; private set; }
    public string? PendingPhoneSearch { get; private set; }
    public int? PendingGuestCount { get; private set; }
    public string? PendingNotes { get; private set; }
    public ReservationDetailsChangeOrigin PendingDetailsChangeOrigin { get; private set; }
    public string? PendingDetailsActorId { get; private set; }
    public Guid? PendingDetailsAdapterConnectionId { get; private set; }
    public Guid? PendingDetailsExternalOperationId { get; private set; }
    public Guid? PendingDetailsCorrelationId { get; private set; }
    public int? LastAllocationAmendmentRejectionCode { get; private set; }
    public string PrimaryGuestName { get; private set; } = string.Empty;
    public string PrimaryGuestNameSearch { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? EmailSearch { get; private set; }
    public string? Phone { get; private set; }
    public string? PhoneSearch { get; private set; }
    public int GuestCount { get; private set; }
    public ReservationSource Source { get; private set; }
    public string? SourceSystem { get; private set; }
    public string? SourceReference { get; private set; }
    public string? Notes { get; private set; }
    public long DetailsRevision { get; private set; }
    public ReservationDetailsChangeOrigin LastDetailsChangeOrigin { get; private set; }
    public string? LastDetailsActorId { get; private set; }
    public Guid? LastDetailsAdapterConnectionId { get; private set; }
    public Guid? LastDetailsExternalOperationId { get; private set; }
    public DateTimeOffset LastDetailsChangedAtUtc { get; private set; }
    public ReservationState Status { get; private set; } = ReservationState.PendingAllocation;
    public long Version { get; private set; } = 1;
    public long ProjectionOrdinal { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public DateTimeOffset? TerminalAtUtc { get; private set; }
    public DateTimeOffset? AnonymisedAtUtc { get; private set; }
    public bool IsAnonymised { get; private set; }
    public IReadOnlyCollection<RequestedInventoryUnit> RequestedUnits => this.requestedUnits.AsReadOnly();
    public IReadOnlyCollection<ReservationGuest> Guests => this.guests.AsReadOnly();

    private static Result ValidateStayProvenance(string actorId, Guid eventId)
    {
        string normalizedActor = actorId?.Trim() ?? string.Empty;
        return normalizedActor.Length is 0 or > ActorIdMaxLength || eventId == Guid.Empty
            ? Result.Failure(ReservationsDomainErrors.StayProvenanceInvalid)
            : Result.Success();
    }

    private static Result ValidateStayReleaseProvenance(
        string actorId,
        Guid releaseRequestId,
        Guid eventId) =>
        releaseRequestId == Guid.Empty
            ? Result.Failure(ReservationsDomainErrors.StayProvenanceInvalid)
            : ValidateStayProvenance(actorId, eventId);

    private static string NormalizeRequired(string? value) => value?.Trim() ?? string.Empty;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeSearch(string? value) =>
        NormalizeOptional(value)?.ToUpperInvariant();

    private void MarkTerminal(DateTimeOffset occurredAtUtc)
    {
        if (!this.TerminalAtUtc.HasValue)
        {
            this.TerminalAtUtc = occurredAtUtc.ToUniversalTime();
        }
    }

    private void ClearPendingAllocationAmendment()
    {
        this.PendingAllocationAmendmentId = null;
        this.PendingInventoryAmendmentRequestId = null;
        this.PendingAllocationAmendmentRequestFingerprint = null;
        this.PendingArrival = null;
        this.PendingDeparture = null;
        this.PendingExpectedArrivalTime = null;
        this.PendingExpectedDepartureTime = null;
        this.PendingInventoryUnitIds = null;
        this.PendingPrimaryGuestName = null;
        this.PendingPrimaryGuestNameSearch = null;
        this.PendingEmail = null;
        this.PendingEmailSearch = null;
        this.PendingPhone = null;
        this.PendingPhoneSearch = null;
        this.PendingGuestCount = null;
        this.PendingNotes = null;
        this.PendingDetailsChangeOrigin = ReservationDetailsChangeOrigin.Unknown;
        this.PendingDetailsActorId = null;
        this.PendingDetailsAdapterConnectionId = null;
        this.PendingDetailsExternalOperationId = null;
        this.PendingDetailsCorrelationId = null;
    }

    private ReservationDetailsSnapshot CaptureDetails() => new(
        this.Arrival,
        this.Departure,
        this.requestedUnits.Select(unit => unit.InventoryUnitId).ToArray(),
        this.PrimaryGuestName,
        this.Email,
        this.Phone,
        this.GuestCount,
        this.Notes,
        this.ExpectedArrivalTime,
        this.ExpectedDepartureTime);

    private void RaiseGuestStayChanged(Guid eventId, DateTimeOffset occurredAtUtc)
    {
        ReservationGuest? guest = this.guests.SingleOrDefault(item => item.IsCurrent);
        if (guest is null)
        {
            return;
        }

        this.RaiseDomainEvent(new ReservationGuestStayChangedDomainEvent(
            eventId,
            occurredAtUtc,
            this.ScopeId,
            this.Id,
            this.PropertyId,
            guest.GuestId,
            guest.Role,
            this.Arrival,
            this.Departure,
            this.Status,
            this.CheckedInBusinessDate,
            this.NoShowBusinessDate,
            this.CheckedOutBusinessDate,
            IsCurrentParticipant: true,
            ReservationVersion: this.Version));
    }

    private void RaiseGuestStayChanged(
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        ReservationGuest guest) =>
        this.RaiseDomainEvent(new ReservationGuestStayChangedDomainEvent(
            eventId,
            occurredAtUtc,
            this.ScopeId,
            this.Id,
            this.PropertyId,
            guest.GuestId,
            guest.Role,
            this.Arrival,
            this.Departure,
            this.Status,
            this.CheckedInBusinessDate,
            this.NoShowBusinessDate,
            this.CheckedOutBusinessDate,
            guest.IsCurrent,
            this.Version));

    private static void AddChanged<T>(List<string> changedFields, string field, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            changedFields.Add(field);
        }
    }

    private static bool HasMinutePrecision(TimeOnly? value) =>
        !value.HasValue || value.Value.Ticks % TimeSpan.TicksPerMinute == 0;
}

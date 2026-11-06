namespace BunkFy.Modules.Reservations.Application.Ports;

using BunkFy.Modules.Reservations.Contracts.Stations;

public interface IReservationManagementOperationRepository
{
    Task<ReservationManagementOperationRecord?> GetAsync(
        Guid reservationId,
        Guid operationId,
        CancellationToken cancellationToken);

    Task AddAsync(
        ReservationManagementOperationRecord operation,
        CancellationToken cancellationToken);

    Task<ReservationStationAttributionRead> GetStationAttributionAsync(Guid reservationId, Guid operationId,
        CancellationToken cancellationToken) => Task.FromResult(new ReservationStationAttributionRead(false));

    Task<StationCheckOutOutcomeState> ReadCheckOutStateAsync(Guid propertyId, Guid reservationId, long resultingVersion,
        CancellationToken cancellationToken) => Task.FromResult(StationCheckOutOutcomeState.Unavailable);

    Task AddStationAsync(ReservationManagementOperationRecord operation, StationCheckInProvenance provenance,
        long resultingVersion, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Station attribution is unavailable.");
}

public sealed record ReservationStationAttributionRead(bool Supported, StationCheckInProvenance? Provenance = null, long ResultingVersion = 0);

public sealed record ReservationManagementOperationRecord(
    Guid OperationId,
    string ScopeId,
    Guid PropertyId,
    Guid ReservationId,
    ReservationManagementOperationKind Kind,
    long? ExpectedVersion,
    long? ExpectedDetailsRevision,
    DateOnly? BusinessDate,
    DateTimeOffset CreatedAtUtc,
    string? RequestFingerprint = null)
{
    public bool MatchesLifecycle(
        ReservationManagementOperationKind kind,
        long expectedVersion,
        DateOnly? businessDate) =>
        this.Kind == kind &&
        this.ExpectedVersion == expectedVersion &&
        this.ExpectedDetailsRevision is null &&
        this.BusinessDate == businessDate &&
        this.RequestFingerprint is null;

    public bool MatchesGuestDetails(long expectedDetailsRevision) =>
        this.Kind == ReservationManagementOperationKind.GuestDetails &&
        this.ExpectedVersion is null &&
        this.ExpectedDetailsRevision == expectedDetailsRevision &&
        this.BusinessDate is null &&
        this.RequestFingerprint is null;

    public bool MatchesInventoryAmendment(
        long expectedDetailsRevision,
        string requestFingerprint) =>
        this.Kind == ReservationManagementOperationKind.InventoryAmendment &&
        this.ExpectedVersion is null &&
        this.ExpectedDetailsRevision == expectedDetailsRevision &&
        this.BusinessDate is null &&
        string.Equals(
            this.RequestFingerprint,
            requestFingerprint,
            StringComparison.Ordinal);

    public bool MatchesStayAmendment(
        long expectedDetailsRevision,
        string requestFingerprint) =>
        this.Kind == ReservationManagementOperationKind.StayAmendment &&
        this.ExpectedVersion is null &&
        this.ExpectedDetailsRevision == expectedDetailsRevision &&
        this.BusinessDate is null &&
        string.Equals(
            this.RequestFingerprint,
            requestFingerprint,
            StringComparison.Ordinal);
}

public enum ReservationManagementOperationKind
{
    Unknown = 0,
    Cancel = 1,
    CheckIn = 2,
    NoShow = 3,
    CheckOut = 4,
    GuestDetails = 5,
    InventoryAmendment = 6,
    StayAmendment = 7
}

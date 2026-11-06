namespace BunkFy.Modules.Stations.Api;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;

// Primary management targets are reauthorized by the existing management service.
// No enrollment, issuer, expiry, credential digest or permission can be supplied here.
public sealed record StationManagementRequest(Guid OperationId, StationOperationKind Kind,
    Guid? StationId = null, Guid? BrowserSessionId = null, Guid? StaffMemberId = null,
    Guid? SetupGrantId = null, long ExpectedVersion = 0, string? Label = null);

public sealed record StationOwnPinRequest(Guid OperationId, long ExpectedRevision,
    [property: DataType(DataType.Password)] string Pin)
{
    public override string ToString() => nameof(StationOwnPinRequest);
}

// StaffMemberId is only a roster selection for a deliberate PIN attempt, never authority.
public sealed record StationUnlockRequest(Guid OperationId, Guid StaffMemberId, long ExpectedGeneration,
    [property: DataType(DataType.Password)] string Pin)
{
    public override string ToString() => nameof(StationUnlockRequest);
}
public sealed record StationLockRequest(Guid OperationId, long ExpectedGeneration);
public sealed record StationActivityRequest(Guid OperationId, Guid ActorSessionId, long ExpectedGeneration);
public sealed record StationRedeemSetupRequest(Guid OperationId, Guid SetupGrantId,
    [property: DataType(DataType.Password)] string Pin)
{
    public override string ToString() => nameof(StationRedeemSetupRequest);
}
public sealed record StationCheckInRequest(Guid OperationId, Guid ReservationId, long ExpectedVersion,
    Guid ActorSessionId, long ExpectedGeneration);
// Historical concurrency references only. Current pairing and persisted original provenance are server-validated.
public sealed record StationCheckInOutcomeRequest(Guid OperationId, Guid ReservationId, long ExpectedVersion,
    Guid BrowserSessionId, Guid ActorSessionId, long ExpectedGeneration);

public sealed record StationApiFailure(string Code);
/// <summary>Operation-specific public receipt; internal issuer identity is never an HTTP field.</summary>
public sealed record StationManagementHttpReceipt(StationOperationKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? StationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? BrowserSessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PropertyId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? StaffMemberId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SetupGrantId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OriginalIssuerSessionId = null);
public sealed record StationManagementHttpResponse(StationManagementState State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StationManagementHttpReceipt? Receipt = null);
public sealed record StationStaffStatusResponse(StationManagementState State, StationStaffManagementItem? Item = null);
public sealed record StationCurrentResponse(StationRuntimeResponse Runtime, string? CsrfToken = null,
    DateTimeOffset? CsrfExpiresAtUtc = null, string? PropertyName = null, string? StaffDisplayName = null, StationJobAvailability? Jobs = null)
{
    public override string ToString() => nameof(StationCurrentResponse);
}
public sealed record StationArrivalsResponse(StationReservationState State, IReadOnlyList<StationFirstJobArrival> Items,
    string? Continuation = null, Guid? PropertyId = null, DateOnly? PropertyLocalDate = null);

public sealed record StationDeparturesResponse(StationReservationState State, IReadOnlyList<StationFirstJobDeparture> Items,
    string? Continuation = null, Guid? PropertyId = null, DateOnly? PropertyLocalDate = null);
public sealed record StationCheckOutRequest(Guid OperationId, Guid ReservationId, long ExpectedVersion,
    Guid ActorSessionId, long ExpectedGeneration);
public sealed record StationCheckOutOutcomeRequest(Guid OperationId, Guid ReservationId, long ExpectedVersion,
    Guid BrowserSessionId, Guid ActorSessionId, long ExpectedGeneration);

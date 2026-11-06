namespace BunkFy.Modules.Stations.Contracts;

public enum StationAuthorityKind { Unknown = 0, LinkedStation = 1, StationOnly = 2 }
public enum StationOperationKind { Unknown = 0, Register = 1, RedeemSetup = 2, Unlock = 3, Lock = 4, Reset = 5, RevokeGrant = 6, RevokeStation = 7, ForegroundActivity = 8, Pair = 9, RegisterStaff = 10, UnregisterStaff = 11, GrantCheckIn = 12, IssueSetup = 13, CancelSetup = 14, OwnPin = 15, GrantCheckOut = 16, RevokeCheckOutGrant = 17 }
public enum StationCoreOutcome { Unknown = 0, Applied = 1, Rejected = 2, Throttled = 3, Conflict = 4, Unavailable = 5 }
public sealed record StationCoreResult(StationCoreOutcome Outcome, Guid? ActorSessionId = null, long? Generation = null, DateTimeOffset? RetryAfterUtc = null, StationManagementReceipt? Management = null);
/// <summary>Non-secret concurrency coordinates. Not proof of authorization.</summary>
public sealed record StationActorCoordinate(Guid StaffMemberId, Guid ActorSessionId, long Generation, StationAuthorityKind AuthorityKind);
public sealed record StationSessionSnapshot(Guid StationId, Guid PropertyId, Guid BrowserSessionId,
    long Generation, StationActorCoordinate? Actor, DateTimeOffset PairingExpiresAtUtc,
    DateTimeOffset? ActorIdleExpiresAtUtc = null, DateTimeOffset? ActorAbsoluteExpiresAtUtc = null,
    string? StationLabel = null);

public enum StationSessionState { Invalid = 0, Locked = 1, Active = 2, Unavailable = 3, StateChanged = 4, HandoffPending = 5 }
/// <summary>No actor/scope details on invalid, unavailable or changed authority. Not an Auth principal.</summary>
public sealed record StationRuntimeResponse(StationSessionState State, StationSessionSnapshot? Session = null,
    StationCoreOutcome? Outcome = null, DateTimeOffset? RetryAfterUtc = null);
/// <summary>Display context only, never authority. Suppressed unless all current owner reads agree.</summary>
public sealed record StationCurrentView(StationRuntimeResponse Runtime, string? PropertyName = null, string? StaffDisplayName = null);

public enum StationSetupIssuerKind { Unknown = 0, Manager = 1, Self = 2 }
public sealed record StationManagementReceipt(Guid? StationId = null, Guid? BrowserSessionId = null,
    Guid? PropertyId = null, Guid? StaffMemberId = null, Guid? SetupGrantId = null, long? Version = null,
    StationOperationKind Kind = StationOperationKind.Unknown, StationSetupIssuerKind IssuerKind = StationSetupIssuerKind.Unknown,
    string? IssuerSubjectId = null, Guid? IssuerSessionId = null);
public enum StationManagementState { Applied = 0, Denied = 1, StateChanged = 2, Unavailable = 3, CapacityReached = 4, NotFound = 5 }
public sealed record StationManagementResponse(StationManagementState State, StationManagementReceipt? Receipt = null);
public sealed record StationListItem(Guid StationId, Guid PropertyId, string Label, long Version, bool Revoked);
public sealed record StationListResponse(StationManagementState State, IReadOnlyList<StationListItem> Items,
    int Page = 1, int PageSize = 25, bool HasMore = false);
public enum StationPinState { NotSet = 0, Set = 1, Revoked = 2, ReEnrollmentNeeded = 3 }
public sealed record StationOwnPinStatus(StationPinState Pin, long Revision);
public sealed record StationOwnPinStatusResponse(StationManagementState State, StationOwnPinStatus? Status = null);
public enum StationSetupState { None = 0, Pending = 1, Expired = 2, Consumed = 3, Cancelled = 4 }
public sealed record StationStaffManagementItem(Guid StaffMemberId, long RosterReference, bool Registered,
    long RegistrationVersion, StationPinState Pin, bool LocalGrantPresent, bool LocalGrantRevoked,
    long? LocalGrantRevision, StationSetupState Setup, Guid? SetupGrantId, long PinRevision = 0,
    bool CanIssueStationOnlySetup = false, bool CheckOutGrantPresent = false,
    bool CheckOutGrantRevoked = false, long? CheckOutGrantRevision = null);
public sealed record StationRosterItem(Guid StaffMemberId, string DisplayName, long RosterReference);
public sealed record StationRosterResponse(StationSessionState State, IReadOnlyList<StationRosterItem> Items,
    int Page = 1, int PageSize = 25, bool HasMore = false);

public sealed record StationWorkspaceSettingsResponse(
    bool SharedStationPinEnabled,
    long Revision);

public sealed record UpdateStationWorkspaceSettingsRequest(
    bool SharedStationPinEnabled,
    long ExpectedRevision);

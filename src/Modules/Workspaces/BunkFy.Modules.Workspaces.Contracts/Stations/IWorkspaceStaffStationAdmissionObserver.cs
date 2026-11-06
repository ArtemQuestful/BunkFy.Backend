namespace BunkFy.Modules.Workspaces.Contracts;

/// <summary>Internal, non-enumerating observations only. Not authentication or a reusable admission grant.</summary>
public interface IWorkspaceStaffStationAdmissionObserver
{
    Task<WorkspaceStaffStationObservation> ObserveAsync(string scopeId, Guid propertyId,
        Guid staffMemberId, WorkspaceStaffStationAction action, CancellationToken cancellationToken = default);
}

public enum WorkspaceStaffStationAction
{
    Unknown = 0,
    ReservationCheckIn = 1,
    ReservationCheckOut = 2,
    StationIdentity = 3
}

public enum WorkspaceStaffStationObservationStatus
{
    Unknown = 0,
    PrerequisitesNotObserved = 1,
    ObservationStale = 2,
    Unavailable = 3,
    StationOnlyAccessUnsupported = 4,
    LinkedAccountPrerequisitesObserved = 5
}

public enum WorkspaceStaffStationObservationReason
{
    InvalidRequest = 0,
    OwnerRecordMissing = 1,
    OwnerEvidenceUnavailable = 2,
    StaffOrPropertyInactive = 3,
    AssignmentNotEffective = 4,
    StaffProcessingRestricted = 5,
    StationOnlyAccessNotImplemented = 6,
    AuthAccountNotActive = 7,
    MembershipNotActive = 8,
    WorkspaceClosed = 9,
    PermissionNotObserved = 10,
    ChangedDuringObservation = 11,
    LinkedAccountPrerequisites = 12
}

/// <summary>
/// Coordinates describe when prerequisites were observed, not validity/expiry of credentials or a grant.
/// Account existence checks do not prove primary-session validity, assurance, lockout or atomic authority.
/// </summary>
public sealed record WorkspaceStaffStationObservation(
    WorkspaceStaffStationObservationStatus Status,
    WorkspaceStaffStationObservationReason Reason,
    DateTimeOffset ObservedAtUtc,
    DateOnly? PropertyLocalDate,
    string? CanonicalTimeZoneId,
    string CatalogVersion,
    string TzdbVersion);

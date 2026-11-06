namespace BunkFy.Modules.Staff.Contracts;

/// <summary>Exact Staff-owned facts, not station eligibility or Auth admission.</summary>
public interface IStaffStationEligibilitySource
{
    Task<StaffStationEligibilitySnapshot?> FindAsync(
        string scopeId,
        Guid propertyId,
        Guid staffMemberId,
        CancellationToken cancellationToken = default);
}

public sealed record StaffStationEligibilitySnapshot(
    string ScopeId,
    Guid PropertyId,
    Guid StaffMemberId,
    StaffStatus Status,
    long Version,
    StaffStationAuthLinkState AuthLinkState,
    string? AuthSubjectId,
    StaffStationAssignmentState AssignmentState,
    StaffStationAssignmentFacts? OpenAssignment,
    StaffProcessingRestrictionGateResult ProcessingRestriction);

/// <summary>Linked means one exact Staff back-link, not an active Auth account.</summary>
public enum StaffStationAuthLinkState
{
    Unlinked = 0,
    Linked = 1,
    Malformed = 2,
    Ambiguous = 3
}

/// <summary>
/// Describes an open owner record (IsCurrent), not an assignment effective now.
/// Future eligibility must evaluate EffectiveFrom using the authoritative property-local date.
/// </summary>
public enum StaffStationAssignmentState
{
    None = 0,
    Open = 1,
    Malformed = 2,
    Ambiguous = 3
}

/// <summary>An open assignment's original coordinates; EffectiveFrom may be in the future.</summary>
public sealed record StaffStationAssignmentFacts(
    Guid AssignmentId,
    Guid PropertyId,
    long AssignedAtVersion,
    DateOnly EffectiveFrom);

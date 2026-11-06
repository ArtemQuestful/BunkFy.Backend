namespace BunkFy.Modules.Staff.Contracts;

using System.Text.Json.Serialization;

public interface IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
{
    Task<IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome>>
        ReadAsync(
            IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                requests,
            CancellationToken cancellationToken = default);

    async Task<StaffWorkspaceOnboardingIdentityAnchorOutcome> ReadAsync(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest request,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome> outcomes =
            await this.ReadAsync([request], cancellationToken)
                .ConfigureAwait(false);
        return outcomes.Count == 1 &&
            outcomes[0].ApplicationId == request.ApplicationId
                ? outcomes[0]
                : throw new InvalidOperationException(
                    "Staff identity-anchor outcome coordinates are invalid.");
    }
}

public sealed record StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
    Guid ApplicationId,
    [property: JsonIgnore] string ExpectedAuthSubjectId);

public interface IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder
{
    Task<StaffWorkspaceOnboardingIdentityAnchorResolutionResult> RecordAsync(
        StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StaffWorkspaceOnboardingIdentityAnchorOutcome(
    Guid ApplicationId,
    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus Status,
    Guid? StaffMemberId,
    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle TargetLifecycle,
    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch SubjectMatch,
    long? WorkspaceApplicationVersion,
    StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition?
        ResolutionDisposition,
    Guid? ResolutionEventId = null);

public sealed record StaffWorkspaceOnboardingIdentityAnchorResolutionRequest(
    Guid ResolutionEventId,
    Guid ApplicationId,
    Guid StaffMemberId,
    long WorkspaceApplicationVersion,
    StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition Disposition,
    DateTimeOffset ResolvedAtUtc);

public sealed record StaffWorkspaceOnboardingIdentityAnchorResolutionResult(
    StaffWorkspaceOnboardingIdentityAnchorResolutionStatus Status);

public static class StaffWorkspaceOnboardingIdentityAnchorLifecycleLimits
{
    public const int MaximumBatchSize = 500;
}

public enum StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
{
    Unknown = 0,
    Absent = 1,
    Unresolved = 2,
    Resolved = 3,
    Corrupt = 4
}

public enum StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
{
    Unknown = 0,
    Active = 1,
    Suspended = 2,
    Departed = 3,
    Anonymised = 4,
    Missing = 5
}

public enum StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
{
    Unknown = 0,
    Exact = 1,
    Missing = 2,
    Mismatch = 3
}

public enum StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
{
    Unknown = 0,
    CompletedRedacted = 1,
    RejectedRedacted = 2,
    SupersededRedacted = 3,
    ExpiredRedacted = 4,
    WithdrawnRedacted = 5
}

public enum StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
{
    Unknown = 0,
    Recorded = 1,
    AlreadyRecorded = 2,
    AnchorAbsent = 3,
    Conflict = 4
}

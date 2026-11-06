namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.ValueObjects;
using Gma.Framework.Results;

internal sealed class StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator(
    IStaffIdentityProvisioningAnchorRepository anchors,
    IStaffIdentityProvisioningAnchorResolutionRepository resolutions,
    IStaffMemberRepository members,
    IStaffCreationOperationLock creationLock)
{
    public async Task<Result<IReadOnlyList<
        StaffWorkspaceOnboardingIdentityAnchorOutcome>>> ReadAsync(
            IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                requests,
            CancellationToken cancellationToken)
    {
        if (requests is null || requests.Count is < 1 or
            > StaffWorkspaceOnboardingIdentityAnchorLifecycleLimits
                .MaximumBatchSize ||
            requests.Any(request => !IsValid(request)) ||
            requests.Select(request => request.ApplicationId)
                .Distinct().Count() != requests.Count)
        {
            return Result.Failure<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>(
                    StaffApplicationErrors
                        .IdentityAnchorLifecycleRequestInvalid);
        }

        StaffIdentityProvisioningSourceKey[] sourceKeys = requests
            .Select(request => new StaffIdentityProvisioningSourceKey(
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
                request.ApplicationId))
            .ToArray();
        IReadOnlyList<StaffIdentityProvisioningAnchorRecord> loadedAnchors =
            await anchors.ListAsync(sourceKeys, cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<StaffIdentityProvisioningAnchorResolutionRecord>
            loadedResolutions = await resolutions.ListAsync(
                requests.Select(request => request.ApplicationId).ToArray(),
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StaffMemberSafetyEvidence> loadedTargets =
            await members.ListSafetyEvidenceAsync(
                loadedAnchors.Select(anchor => anchor.StaffMemberId)
                    .Distinct()
                    .Order()
                    .ToArray(),
                cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, StaffIdentityProvisioningAnchorRecord>
            anchorsBySource = loadedAnchors.ToDictionary(
                anchor => anchor.SourceId);
        Dictionary<Guid, StaffIdentityProvisioningAnchorResolutionRecord>
            resolutionsBySource = loadedResolutions.ToDictionary(
                resolution => resolution.SourceId);
        Dictionary<Guid, StaffMemberSafetyEvidence> targetsById =
            loadedTargets.ToDictionary(target => target.StaffMemberId);

        StaffWorkspaceOnboardingIdentityAnchorOutcome[] outcomes =
            requests.Select(request => Outcome(
                request,
                anchorsBySource,
                resolutionsBySource,
                targetsById)).ToArray();
        return Result.Success<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>>(outcomes);
    }

    public async Task<Result<
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>> RecordAsync(
            string scopeId,
            StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return Result.Failure<
                StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>(
                    StaffApplicationErrors
                        .IdentityAnchorLifecycleRequestInvalid);
        }

        await creationLock.AcquireAsync(
                scopeId,
                request.ApplicationId,
                cancellationToken).ConfigureAwait(false);
        StaffIdentityProvisioningAnchorRecord? anchor = await anchors.GetAsync(
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
            request.ApplicationId,
            cancellationToken).ConfigureAwait(false);
        if (anchor is null)
        {
            return Result.Success(
                StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
                    .AnchorAbsent);
        }

        if (!string.Equals(
                anchor.ScopeId,
                scopeId,
                StringComparison.Ordinal) ||
            anchor.StaffMemberId != request.StaffMemberId ||
            !anchor.ResolutionEventId.HasValue ||
            anchor.ResolutionEventId.Value == Guid.Empty ||
            anchor.ResolutionEventId.Value != request.ResolutionEventId)
        {
            return Result.Success(
                StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
                    .Conflict);
        }

        StaffIdentityProvisioningAnchorResolutionRecord? existing =
            await resolutions.GetAsync(
                request.ApplicationId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Result.Success(Matches(
                existing,
                request,
                scopeId,
                anchor.AnchoredAtUtc)
                ? StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
                    .AlreadyRecorded
                : StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
                    .Conflict);
        }

        await resolutions.AddAsync(
            new StaffIdentityProvisioningAnchorResolutionRecord(
                scopeId,
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
                request.ApplicationId,
                request.StaffMemberId,
                request.WorkspaceApplicationVersion,
                request.Disposition,
                request.ResolutionEventId,
                ResolutionTime(
                    request.ResolvedAtUtc,
                    anchor.AnchoredAtUtc)),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(
            StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.Recorded);
    }

    private static StaffWorkspaceOnboardingIdentityAnchorOutcome Outcome(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest request,
        Dictionary<Guid, StaffIdentityProvisioningAnchorRecord> anchors,
        Dictionary<Guid,
            StaffIdentityProvisioningAnchorResolutionRecord> resolutions,
        Dictionary<Guid, StaffMemberSafetyEvidence> targets)
    {
        if (!anchors.TryGetValue(
                request.ApplicationId,
                out StaffIdentityProvisioningAnchorRecord? anchor))
        {
            return new(
                request.ApplicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent,
                null,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                    .Unknown,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown,
                null,
                null,
                null);
        }

        bool anchorMatchesProtocol =
            anchor.SourceKind ==
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding &&
            anchor.ResolutionEventId.HasValue &&
            anchor.ResolutionEventId.Value != Guid.Empty &&
            anchor.ResolutionEventId.Value != anchor.SourceId;

        if (!targets.TryGetValue(
                anchor.StaffMemberId,
                out StaffMemberSafetyEvidence? target))
        {
            return new(
                request.ApplicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Corrupt,
                anchor.StaffMemberId,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Missing,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Missing,
                null,
                null,
                anchor.ResolutionEventId);
        }

        bool hasResolution = resolutions.TryGetValue(
            request.ApplicationId,
            out StaffIdentityProvisioningAnchorResolutionRecord? resolution);
        bool resolutionMatches = anchorMatchesProtocol &&
            (!hasResolution ||
            (resolution!.SourceKind ==
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding &&
             resolution.StaffMemberId == anchor.StaffMemberId &&
             resolution.ResolutionEventId ==
                anchor.ResolutionEventId &&
             string.Equals(
                 resolution.ScopeId,
                 anchor.ScopeId,
                 StringComparison.Ordinal)));
        return new(
            request.ApplicationId,
            resolutionMatches
                ? hasResolution
                    ? StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                        .Resolved
                    : StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                        .Unresolved
                : StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Corrupt,
            anchor.StaffMemberId,
            ToLifecycle(target.Status),
            SubjectMatch(target.AuthSubjectId, request.ExpectedAuthSubjectId),
            hasResolution && resolutionMatches
                ? resolution!.WorkspaceApplicationVersion
                : null,
            hasResolution && resolutionMatches
                ? resolution!.Disposition
                : null,
            anchor.ResolutionEventId);
    }

    private static StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
        ToLifecycle(StaffMemberState status) => status switch
        {
            StaffMemberState.Active =>
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
            StaffMemberState.Suspended =>
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                    .Suspended,
            StaffMemberState.Departed =>
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Departed,
            StaffMemberState.Anonymised =>
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                    .Anonymised,
            _ => StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown
        };

    private static StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
        SubjectMatch(
            string? actualAuthSubjectId,
            string expectedAuthSubjectId) =>
        actualAuthSubjectId is null
            ? StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Missing
            : string.Equals(
                actualAuthSubjectId,
                expectedAuthSubjectId.Trim(),
                StringComparison.Ordinal)
                ? StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact
                : StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Mismatch;

    private static bool IsValid(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest? request)
    {
        if (request is null || request.ApplicationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ExpectedAuthSubjectId) ||
            request.ExpectedAuthSubjectId.Any(char.IsControl))
        {
            return false;
        }

        Result<StaffAuthSubject> subject = StaffAuthSubject.Create(
            request.ExpectedAuthSubjectId);
        return subject.IsSuccess && subject.Value.Value is not null;
    }

    private static bool IsValid(
        StaffWorkspaceOnboardingIdentityAnchorResolutionRequest? request) =>
        request is not null &&
        request.ResolutionEventId != Guid.Empty &&
        request.ApplicationId != Guid.Empty &&
        request.ResolutionEventId != request.ApplicationId &&
        request.StaffMemberId != Guid.Empty &&
        request.WorkspaceApplicationVersion >= 1 &&
        request.Disposition is >=
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted and <=
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .WithdrawnRedacted &&
        request.ResolvedAtUtc != default;

    private static bool Matches(
        StaffIdentityProvisioningAnchorResolutionRecord existing,
        StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request,
        string scopeId,
        DateTimeOffset anchoredAtUtc) =>
        string.Equals(existing.ScopeId, scopeId, StringComparison.Ordinal) &&
        existing.SourceKind ==
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding &&
        existing.SourceId == request.ApplicationId &&
        existing.StaffMemberId == request.StaffMemberId &&
        existing.WorkspaceApplicationVersion ==
            request.WorkspaceApplicationVersion &&
        existing.Disposition == request.Disposition &&
        existing.ResolutionEventId == request.ResolutionEventId &&
        existing.ResolvedAtUtc.ToUniversalTime() ==
            ResolutionTime(request.ResolvedAtUtc, anchoredAtUtc);

    private static DateTimeOffset ResolutionTime(
        DateTimeOffset requestedAtUtc,
        DateTimeOffset anchoredAtUtc)
    {
        DateTimeOffset normalizedRequest =
            NormalizeResolutionTime(requestedAtUtc);
        DateTimeOffset normalizedAnchor =
            NormalizeResolutionTime(anchoredAtUtc);
        return normalizedRequest >= normalizedAnchor
            ? normalizedRequest
            : normalizedAnchor;
    }

    private static DateTimeOffset NormalizeResolutionTime(
        DateTimeOffset resolvedAtUtc)
    {
        DateTimeOffset utc = resolvedAtUtc.ToUniversalTime();
        return new DateTimeOffset(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
    }
}

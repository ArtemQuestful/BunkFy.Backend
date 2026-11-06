namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.ValueObjects;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class StaffWorkspaceOnboardingAnchorCutoverCoordinator(
    IStaffIdentityProvisioningAnchorRepository anchors,
    IStaffIdentityProvisioningAnchorWriter anchorWriter,
    IStaffOnboardingProvisioningOperationRepository operations,
    IStaffMemberRepository members,
    IStaffCreationOperationLock creationLock,
    StaffMemberMutationCoordinator mutations,
    ISystemClock clock)
{
    public async Task<Result<IReadOnlyList<
        StaffIdentityProvisioningAnchorCandidateInspection>>> InspectAsync(
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken)
    {
        Result<StaffIdentityProvisioningAnchorCandidate[]> validated =
            Validate(candidates, requireSeedableOwnerTarget: false);
        if (validated.IsFailure)
        {
            return Result.Failure<IReadOnlyList<
                StaffIdentityProvisioningAnchorCandidateInspection>>(
                    validated.Error);
        }

        CutoverEvidence evidence = await this.LoadEvidenceAsync(
            validated.Value,
            cancellationToken).ConfigureAwait(false);
        StaffIdentityProvisioningAnchorCandidateInspection[] results =
            validated.Value.Select(candidate => InspectOne(
                candidate,
                evidence)).ToArray();

        return Result.Success<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>>(results);
    }

    public async Task<Result<StaffIdentityProvisioningAnchorApplySummary>>
        ApplyAsync(
            string scopeId,
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken)
    {
        Result<StaffIdentityProvisioningAnchorCandidate[]> validated =
            Validate(candidates, requireSeedableOwnerTarget: true);
        if (validated.IsFailure)
        {
            return Result.Failure<StaffIdentityProvisioningAnchorApplySummary>(
                validated.Error);
        }

        foreach (Guid sourceId in validated.Value
            .Select(candidate => candidate.SourceId)
            .Distinct()
            .Order())
        {
            await creationLock.AcquireAsync(
                scopeId,
                sourceId,
                cancellationToken).ConfigureAwait(false);
        }

        CutoverEvidence beforeTargetLocks = await this.LoadEvidenceAsync(
            validated.Value,
            cancellationToken).ConfigureAwait(false);

        foreach (Guid targetId in beforeTargetLocks.TargetIds.Order())
        {
            if (await mutations.AcquireSafetyTransitionAsync(
                    targetId,
                    cancellationToken).ConfigureAwait(false) is null)
            {
                return Result.Failure<
                    StaffIdentityProvisioningAnchorApplySummary>(
                        StaffApplicationErrors.IdentityAnchorCutoverBlocked);
            }
        }

        CutoverEvidence lockedEvidence = await this.LoadEvidenceAsync(
            validated.Value,
            cancellationToken).ConfigureAwait(false);
        (StaffIdentityProvisioningAnchorCandidate Candidate,
            StaffIdentityProvisioningAnchorCandidateInspection Inspection)[]
            inspected = validated.Value.Select(candidate => (
                candidate,
                InspectOne(candidate, lockedEvidence))).ToArray();

        if (inspected.Any(item => item.Inspection.Disposition is
            StaffIdentityProvisioningAnchorCutoverDisposition.Ambiguous or
            StaffIdentityProvisioningAnchorCutoverDisposition.Conflict))
        {
            return Result.Failure<StaffIdentityProvisioningAnchorApplySummary>(
                StaffApplicationErrors.IdentityAnchorCutoverBlocked);
        }

        int applied = 0;
        int already = 0;
        DateTimeOffset nowUtc = clock.UtcNow;
        foreach ((StaffIdentityProvisioningAnchorCandidate candidate,
            StaffIdentityProvisioningAnchorCandidateInspection inspection) in
            inspected)
        {
            if (inspection.Disposition ==
                StaffIdentityProvisioningAnchorCutoverDisposition.AlreadyAnchored)
            {
                already++;
                continue;
            }

            lockedEvidence.Receipts.TryGetValue(
                candidate.SourceId,
                out StaffMemberMutationOperationRecord? receipt);
            Guid staffMemberId = candidate.StaffMemberId!.Value;
            DateTimeOffset anchoredAtUtc = receipt?.CompletedAtUtc ?? nowUtc;
            await anchorWriter.AddAsync(
                new StaffIdentityProvisioningAnchorRecord(
                    scopeId,
                    ToPersistenceKind(candidate.SourceKind),
                    candidate.SourceId,
                    staffMemberId,
                    anchoredAtUtc),
                cancellationToken).ConfigureAwait(false);
            applied++;
        }

        return Result.Success(
            new StaffIdentityProvisioningAnchorApplySummary(applied, already));
    }

    private async Task<CutoverEvidence> LoadEvidenceAsync(
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
        CancellationToken cancellationToken)
    {
        StaffIdentityProvisioningSourceKey[] sourceKeys = candidates
            .Select(candidate => new StaffIdentityProvisioningSourceKey(
                ToPersistenceKind(candidate.SourceKind),
                candidate.SourceId))
            .ToArray();
        IReadOnlyList<StaffIdentityProvisioningAnchorRecord> anchorRecords =
            await anchors.ListAsync(sourceKeys, cancellationToken)
                .ConfigureAwait(false);
        Guid[] workspaceSourceIds = candidates
            .Where(candidate => candidate.SourceKind ==
                StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding)
            .Select(candidate => candidate.SourceId)
            .ToArray();
        IReadOnlyList<StaffMemberMutationOperationRecord> receiptRecords =
            await operations.ListAsync(
                workspaceSourceIds,
                cancellationToken).ConfigureAwait(false);
        Dictionary<(StaffIdentityProvisioningSourceKind, Guid),
            StaffIdentityProvisioningAnchorRecord> anchorsBySource =
            anchorRecords.ToDictionary(
                anchor => (anchor.SourceKind, anchor.SourceId));
        Dictionary<Guid, StaffMemberMutationOperationRecord> receiptsBySource =
            receiptRecords.ToDictionary(receipt => receipt.OperationId);
        HashSet<Guid> targetIds = candidates
            .Where(candidate => candidate.StaffMemberId.HasValue)
            .Select(candidate => candidate.StaffMemberId!.Value)
            .Concat(anchorRecords.Select(anchor => anchor.StaffMemberId))
            .Concat(receiptRecords.Select(receipt => receipt.StaffMemberId))
            .ToHashSet();
        IReadOnlyList<StaffMemberSafetyEvidence> targetRecords =
            await members.ListSafetyEvidenceAsync(
                targetIds.Order().ToArray(),
                cancellationToken).ConfigureAwait(false);
        return new(
            anchorsBySource,
            receiptsBySource,
            targetRecords.ToDictionary(target => target.StaffMemberId),
            targetIds);
    }

    private static StaffIdentityProvisioningAnchorCandidateInspection
        InspectOne(
            StaffIdentityProvisioningAnchorCandidate candidate,
            CutoverEvidence evidence)
    {
        evidence.Anchors.TryGetValue(
            (ToPersistenceKind(candidate.SourceKind), candidate.SourceId),
            out StaffIdentityProvisioningAnchorRecord? anchor);
        StaffMemberMutationOperationRecord? receipt = null;
        if (candidate.SourceKind ==
            StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding)
        {
            evidence.Receipts.TryGetValue(
                candidate.SourceId,
                out receipt);
        }

        Guid? resolvedTarget = anchor?.StaffMemberId ??
            candidate.StaffMemberId ?? receipt?.StaffMemberId;
        StaffMemberSafetyEvidence? target = resolvedTarget.HasValue &&
            evidence.Targets.TryGetValue(
                resolvedTarget.Value,
                out StaffMemberSafetyEvidence? loadedTarget)
                ? loadedTarget
                : null;

        if (resolvedTarget.HasValue && target is null)
        {
            return Inspection(
                candidate,
                StaffIdentityProvisioningAnchorCutoverDisposition.Conflict);
        }

        if (anchor is not null)
        {
            bool conflicts =
                (candidate.StaffMemberId.HasValue &&
                 candidate.StaffMemberId.Value != anchor.StaffMemberId) ||
                (receipt is not null &&
                 receipt.StaffMemberId != anchor.StaffMemberId) ||
                (candidate.SourceKind ==
                    StaffIdentityProvisioningAnchorSourceKind
                        .WorkspaceOnboarding &&
                 ((!candidate.StaffMemberId.HasValue && receipt is null) ||
                  !string.Equals(
                      target!.AuthSubjectId,
                      candidate.ExpectedAuthSubjectId,
                      StringComparison.Ordinal))) ||
                (candidate.SourceKind ==
                    StaffIdentityProvisioningAnchorSourceKind
                        .OrganizationMembership &&
                 target!.AuthSubjectId is not null &&
                 !string.Equals(
                     target.AuthSubjectId,
                     candidate.ExpectedAuthSubjectId,
                     StringComparison.Ordinal));
            return Inspection(
                candidate,
                conflicts
                    ? StaffIdentityProvisioningAnchorCutoverDisposition.Conflict
                    : StaffIdentityProvisioningAnchorCutoverDisposition.AlreadyAnchored);
        }

        if (!candidate.StaffMemberId.HasValue)
        {
            return Inspection(
                candidate,
                receipt is null
                    ? StaffIdentityProvisioningAnchorCutoverDisposition
                        .Ambiguous
                    : StaffIdentityProvisioningAnchorCutoverDisposition
                        .Conflict);
        }

        if (candidate.SourceKind ==
            StaffIdentityProvisioningAnchorSourceKind.OrganizationMembership)
        {
            bool subjectMatches = target!.AuthSubjectId is null
                ? candidate.ReviewedErasedTarget
                : !candidate.ReviewedErasedTarget &&
                  string.Equals(
                      target.AuthSubjectId,
                      candidate.ExpectedAuthSubjectId,
                      StringComparison.Ordinal);
            return Inspection(
                candidate,
                subjectMatches
                    ? StaffIdentityProvisioningAnchorCutoverDisposition
                        .SeedableFromReviewedOwnerMap
                    : StaffIdentityProvisioningAnchorCutoverDisposition
                        .Conflict);
        }

        if (candidate.StaffMemberId.HasValue)
        {
            bool subjectMatches = string.Equals(
                target!.AuthSubjectId,
                candidate.ExpectedAuthSubjectId,
                StringComparison.Ordinal);
            return Inspection(
                candidate,
                !subjectMatches ||
                (receipt is not null &&
                 receipt.StaffMemberId != candidate.StaffMemberId.Value)
                    ? StaffIdentityProvisioningAnchorCutoverDisposition.Conflict
                    : StaffIdentityProvisioningAnchorCutoverDisposition
                        .SeedableFromWorkspace);
        }

        throw new InvalidOperationException(
            "The Staff identity-anchor cutover candidate is invalid.");
    }

    private static Result<StaffIdentityProvisioningAnchorCandidate[]> Validate(
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidate>? candidates,
        bool requireSeedableOwnerTarget)
    {
        if (candidates is null || candidates.Count is < 1 or
            > StaffWorkspaceOnboardingAnchorCutoverLimits.MaximumBatchSize)
        {
            return Result.Failure<StaffIdentityProvisioningAnchorCandidate[]>(
                StaffApplicationErrors.IdentityAnchorCutoverRequestInvalid);
        }

        List<StaffIdentityProvisioningAnchorCandidate> normalized =
            new(candidates.Count);
        foreach (StaffIdentityProvisioningAnchorCandidate candidate in
            candidates)
        {
            Result<StaffAuthSubject> subject = StaffAuthSubject.Create(
                candidate.ExpectedAuthSubjectId);
            if (subject.IsFailure || subject.Value.Value is null)
            {
                return Result.Failure<
                    StaffIdentityProvisioningAnchorCandidate[]>(
                        StaffApplicationErrors
                            .IdentityAnchorCutoverRequestInvalid);
            }

            normalized.Add(candidate with
            {
                ExpectedAuthSubjectId = subject.Value.Value
            });
        }

        StaffIdentityProvisioningAnchorCandidate[] ordered = normalized
            .OrderBy(candidate => candidate.SourceKind)
            .ThenBy(candidate => candidate.SourceId)
            .ToArray();
        if (ordered.Any(candidate =>
                candidate.SourceKind is not (
                    StaffIdentityProvisioningAnchorSourceKind
                        .WorkspaceOnboarding or
                    StaffIdentityProvisioningAnchorSourceKind
                        .OrganizationMembership) ||
                candidate.SourceId == Guid.Empty ||
                candidate.StaffMemberId == Guid.Empty ||
                (candidate.SourceKind ==
                    StaffIdentityProvisioningAnchorSourceKind
                        .WorkspaceOnboarding &&
                 (string.IsNullOrWhiteSpace(
                      candidate.ExpectedAuthSubjectId) ||
                  candidate.ReviewedErasedTarget)) ||
                (candidate.SourceKind ==
                    StaffIdentityProvisioningAnchorSourceKind
                        .OrganizationMembership &&
                 (string.IsNullOrWhiteSpace(
                      candidate.ExpectedAuthSubjectId) ||
                  (requireSeedableOwnerTarget &&
                   !candidate.StaffMemberId.HasValue) ||
                  (!candidate.StaffMemberId.HasValue &&
                   candidate.ReviewedErasedTarget)))) ||
            ordered.Select(candidate =>
                    (candidate.SourceKind, candidate.SourceId))
                .Distinct()
                .Count() != ordered.Length)
        {
            return Result.Failure<StaffIdentityProvisioningAnchorCandidate[]>(
                StaffApplicationErrors.IdentityAnchorCutoverRequestInvalid);
        }

        return Result.Success(ordered);
    }

    private static StaffIdentityProvisioningAnchorCandidateInspection Inspection(
        StaffIdentityProvisioningAnchorCandidate candidate,
        StaffIdentityProvisioningAnchorCutoverDisposition disposition) =>
        new(candidate.SourceKind, candidate.SourceId, disposition);

    private static StaffIdentityProvisioningSourceKind ToPersistenceKind(
        StaffIdentityProvisioningAnchorSourceKind sourceKind) =>
        sourceKind switch
        {
            StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding =>
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
            StaffIdentityProvisioningAnchorSourceKind.OrganizationMembership =>
                StaffIdentityProvisioningSourceKind.OrganizationMembership,
            _ => StaffIdentityProvisioningSourceKind.Unknown
        };

    private sealed record CutoverEvidence(
        IReadOnlyDictionary<(StaffIdentityProvisioningSourceKind, Guid),
            StaffIdentityProvisioningAnchorRecord> Anchors,
        IReadOnlyDictionary<Guid, StaffMemberMutationOperationRecord> Receipts,
        IReadOnlyDictionary<Guid, StaffMemberSafetyEvidence> Targets,
        IReadOnlySet<Guid> TargetIds);
}

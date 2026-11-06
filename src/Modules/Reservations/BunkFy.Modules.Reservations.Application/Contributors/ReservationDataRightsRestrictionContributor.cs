namespace BunkFy.Modules.Reservations.Application.Contributors;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Mapping;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class ReservationDataRightsRestrictionContributor(
    IRequestDispatcher dispatcher,
    IReservationProcessingRestrictionProjectionRepository projections,
    IReservationProcessingRestrictionRepository restrictions,
    ISystemClock clock)
    : IDataRightsRestrictionContributor
{
    private static readonly PageRequest ActiveRestrictionPage = new(1, 2);
    private static readonly PageRequest ReleaseTargetPage = new(
        1,
        DataRightsRestrictionContract.MaxReleaseTargets + 1);

    public string OwnerKey => ReservationsDataRightsCoordinates.Owner;

    public int ContractVersion => DataRightsRestrictionContract.CurrentVersion;

    public async Task<DataRightsRestrictionTargetResolutionResult>
        ResolveReleaseTargetsAsync(
            DataRightsRestrictionTargetResolutionRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsValid(request, clock.UtcNow))
        {
            return DataRightsRestrictionTargetResolutionResult.Failed(
                ReservationsApplicationErrors
                    .ProcessingRestrictionRequestInvalid.Code);
        }

        Guid propertyId = request.PropertyId!.Value;
        if (request.TargetOwnerOperationId is Guid targetId)
        {
            ReservationProcessingRestriction? target =
                await restrictions.GetAsync(
                    propertyId,
                    targetId,
                    cancellationToken).ConfigureAwait(false);
            if (target is null ||
                target.ReservationId != request.Coordinate.RecordId)
            {
                return DataRightsRestrictionTargetResolutionResult.NotFound();
            }

            if (target.Status != ReservationProcessingRestrictionStatus.Active ||
                target.Version != request.TargetOwnerOperationVersion)
            {
                return DataRightsRestrictionTargetResolutionResult.Stale();
            }

            return DataRightsRestrictionTargetResolutionResult.Completed(
                [ToReleaseTarget(target)]);
        }

        IReadOnlyCollection<ReservationProcessingRestriction> active =
            await restrictions.ListActiveAsync(
                propertyId,
                request.Coordinate.RecordId,
                ReleaseTargetPage,
                cancellationToken).ConfigureAwait(false);
        bool limitReached =
            active.Count > DataRightsRestrictionContract.MaxReleaseTargets;
        return DataRightsRestrictionTargetResolutionResult.Completed(
            active.Take(DataRightsRestrictionContract.MaxReleaseTargets)
                .Select(ToReleaseTarget)
                .ToArray(),
            limitReached);
    }

    public async Task<DataRightsRestrictionContributionResult> ExecuteAsync(
        DataRightsRestrictionContributionRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request, clock.UtcNow))
        {
            return DataRightsRestrictionContributionResult.Failed(
                ReservationsApplicationErrors
                    .ProcessingRestrictionRequestInvalid.Code);
        }

        Guid propertyId = request.PropertyId!.Value;
        string actorId = NormalizeActor(request.ExecutingActorId)!;
        ReservationProcessingRestrictionReceipt? replay =
            await restrictions.FindReceiptByIdempotencyKeyAsync(
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            ReservationProcessingRestriction? replayRestriction =
                await restrictions.GetAsync(
                    replay.PropertyId,
                    replay.RestrictionId,
                    cancellationToken).ConfigureAwait(false);
            string? committedActor = ResolveCommittedActor(
                replay,
                replayRestriction);
            return ToResult(request, replay.ToDto(), committedActor);
        }

        ReservationProcessingRestrictionProjection? projection =
            await projections.GetAsync(
                propertyId,
                request.Coordinate.RecordId,
                cancellationToken).ConfigureAwait(false);
        if (projection is null ||
            projection.ContractVersion !=
                ReservationProcessingRestrictionContract.CurrentVersion)
        {
            return DataRightsRestrictionContributionResult.Blocked(
                ReservationsApplicationErrors
                    .ProcessingRestrictionProjectionUnavailable.Code);
        }

        ReservationProcessingRestriction? releaseTarget = null;
        if (request.Directive == DataRightsRestrictionDirective.Release)
        {
            if (request.TargetOwnerOperationId is Guid targetId)
            {
                releaseTarget = await restrictions.GetAsync(
                    propertyId,
                    targetId,
                    cancellationToken).ConfigureAwait(false);
                if (releaseTarget is null ||
                    releaseTarget.ReservationId != request.Coordinate.RecordId ||
                    releaseTarget.Status !=
                        ReservationProcessingRestrictionStatus.Active ||
                    releaseTarget.Version != request.TargetOwnerOperationVersion)
                {
                    return DataRightsRestrictionContributionResult.Blocked(
                        ReservationsApplicationErrors
                            .ProcessingRestrictionActiveStateInvalid.Code);
                }
            }
            else
            {
                IReadOnlyCollection<ReservationProcessingRestriction> active =
                    await restrictions.ListActiveAsync(
                        propertyId,
                        request.Coordinate.RecordId,
                        ActiveRestrictionPage,
                        cancellationToken).ConfigureAwait(false);
                if (active.Count != 1)
                {
                    return DataRightsRestrictionContributionResult.Blocked(
                        ReservationsApplicationErrors
                            .ProcessingRestrictionActiveStateInvalid.Code);
                }

                releaseTarget = active.Single();
            }
        }

        Result<ReservationProcessingRestrictionReceiptDto> executed =
            request.Directive switch
            {
                DataRightsRestrictionDirective.Apply =>
                    await dispatcher.SendAsync(
                        new ApplyReservationProcessingRestrictionCommand(
                            request.IdempotencyKey,
                            propertyId,
                            request.CaseId,
                            request.ApprovalRevision,
                            request.Coordinate.RecordId,
                            request.Coordinate.RecordVersion,
                            projection.Revision,
                            actorId),
                        cancellationToken).ConfigureAwait(false),
                DataRightsRestrictionDirective.Release
                    when releaseTarget is not null =>
                    await dispatcher.SendAsync(
                        new ReleaseReservationProcessingRestrictionCommand(
                            request.IdempotencyKey,
                            propertyId,
                            releaseTarget.Id,
                            request.CaseId,
                            request.ApprovalRevision,
                            request.Coordinate.RecordId,
                            request.Coordinate.RecordVersion,
                            releaseTarget.Version,
                            projection.Revision,
                            actorId,
                            LegacyUnboundTarget:
                                request.TargetOwnerOperationId is null),
                        cancellationToken).ConfigureAwait(false),
                _ => Result.Failure<ReservationProcessingRestrictionReceiptDto>(
                    ReservationsApplicationErrors
                        .ProcessingRestrictionActiveStateInvalid)
            };

        if (executed.IsFailure)
        {
            return IsBlocked(executed.Error.Code)
                ? DataRightsRestrictionContributionResult.Blocked(
                    executed.Error.Code)
                : DataRightsRestrictionContributionResult.Failed(
                    executed.Error.Code);
        }

        return ToResult(request, executed.Value, actorId);
    }

    private static DataRightsRestrictionContributionResult ToResult(
        DataRightsRestrictionContributionRequest request,
        ReservationProcessingRestrictionReceiptDto receipt,
        string? committedActor)
    {
        if (!Matches(request, receipt, committedActor))
        {
            return DataRightsRestrictionContributionResult.Failed(
                ReservationsApplicationErrors
                    .ProcessingRestrictionOwnerProofInvalid.Code);
        }

        return DataRightsRestrictionContributionResult.Completed(
            new DataRightsRestrictionOwnerProof(
                ReservationProcessingRestrictionContract.CurrentVersion,
                receipt.ReceiptId,
                receipt.RestrictionId,
                receipt.RestrictionVersion,
                receipt.ProjectionRevision,
                receipt.EffectiveRestricted,
                ComputeDigest(receipt, committedActor!),
                receipt.CompletedAtUtc));
    }

    private static bool IsValid(
        DataRightsRestrictionContributionRequest? request,
        DateTimeOffset nowUtc) =>
        request is not null &&
        request.ContractVersion == DataRightsRestrictionContract.CurrentVersion &&
        request.CaseType == DataRightsCaseType.GuestRights &&
        !string.IsNullOrWhiteSpace(request.TenantId) &&
        request.IdempotencyKey != Guid.Empty &&
        request.PropertyId is Guid propertyId &&
        propertyId != Guid.Empty &&
        request.CaseId != Guid.Empty &&
        request.ApprovalRevision > 0 &&
        request.Coordinate is not null &&
        string.Equals(
            request.Coordinate.OwnerKey,
            ReservationsDataRightsCoordinates.Owner,
            StringComparison.Ordinal) &&
        string.Equals(
            request.Coordinate.RecordType,
            ReservationsDataRightsCoordinates.ReservationRecordType,
            StringComparison.Ordinal) &&
        request.Coordinate.RecordId != Guid.Empty &&
        request.Coordinate.RecordVersion > 0 &&
        HasValidExecutionTarget(
            request.Directive,
            request.TargetOwnerOperationId,
            request.TargetOwnerOperationVersion) &&
        NormalizeActor(request.ExecutingActorId) is not null &&
        request.DeadlineUtc > nowUtc;

    private static bool IsValid(
        DataRightsRestrictionTargetResolutionRequest? request,
        DateTimeOffset nowUtc) =>
        request is not null &&
        request.ContractVersion == DataRightsRestrictionContract.CurrentVersion &&
        request.CaseType == DataRightsCaseType.GuestRights &&
        !string.IsNullOrWhiteSpace(request.TenantId) &&
        request.PropertyId is Guid propertyId &&
        propertyId != Guid.Empty &&
        request.CaseId != Guid.Empty &&
        request.Coordinate is not null &&
        string.Equals(
            request.Coordinate.OwnerKey,
            ReservationsDataRightsCoordinates.Owner,
            StringComparison.Ordinal) &&
        string.Equals(
            request.Coordinate.RecordType,
            ReservationsDataRightsCoordinates.ReservationRecordType,
            StringComparison.Ordinal) &&
        request.Coordinate.RecordId != Guid.Empty &&
        request.Coordinate.RecordVersion > 0 &&
        HasValidResolutionTarget(
            request.TargetOwnerOperationId,
            request.TargetOwnerOperationVersion) &&
        request.DeadlineUtc > nowUtc;

    private static bool Matches(
        DataRightsRestrictionContributionRequest request,
        ReservationProcessingRestrictionReceiptDto receipt,
        string? committedActor)
    {
        ReservationProcessingRestrictionActionDto expectedAction =
            request.Directive == DataRightsRestrictionDirective.Apply
                ? ReservationProcessingRestrictionActionDto.Apply
                : ReservationProcessingRestrictionActionDto.Release;
        bool targetMatches = request.Directive switch
        {
            DataRightsRestrictionDirective.Apply =>
                request.TargetOwnerOperationId is null &&
                request.TargetOwnerOperationVersion is null &&
                receipt.RestrictionVersion == 1 &&
                receipt.EffectiveRestricted,
            DataRightsRestrictionDirective.Release
                when request.TargetOwnerOperationId is null =>
                request.TargetOwnerOperationVersion is null &&
                !receipt.EffectiveRestricted,
            DataRightsRestrictionDirective.Release =>
                receipt.RestrictionId == request.TargetOwnerOperationId &&
                receipt.RestrictionVersion ==
                    request.TargetOwnerOperationVersion + 1,
            _ => false
        };
        return receipt.ReceiptId != Guid.Empty &&
            receipt.RestrictionId != Guid.Empty &&
            receipt.Action == expectedAction &&
            receipt.PropertyId == request.PropertyId!.Value &&
            receipt.ReservationId == request.Coordinate.RecordId &&
            receipt.CaseId == request.CaseId &&
            receipt.ApprovalRevision == request.ApprovalRevision &&
            receipt.SelectedReservationVersion ==
                request.Coordinate.RecordVersion &&
            receipt.ContractVersion ==
                ReservationProcessingRestrictionContract.CurrentVersion &&
            receipt.RestrictionVersion > 0 &&
            receipt.ProjectionRevision > 0 &&
            targetMatches &&
            string.Equals(
                committedActor,
                NormalizeActor(request.ExecutingActorId),
                StringComparison.Ordinal) &&
            receipt.EventId != Guid.Empty &&
            receipt.CompletedAtUtc != default &&
            receipt.CompletedAtUtc <= request.DeadlineUtc;
    }

    private static bool HasValidExecutionTarget(
        DataRightsRestrictionDirective directive,
        Guid? targetId,
        long? targetVersion) =>
        directive switch
        {
            DataRightsRestrictionDirective.Apply =>
                targetId is null && targetVersion is null,
            DataRightsRestrictionDirective.Release =>
                (targetId is null && targetVersion is null) ||
                (targetId is Guid id &&
                 id != Guid.Empty &&
                 targetVersion is long version &&
                 version is > 0 and < long.MaxValue),
            _ => false
        };

    private static bool HasValidResolutionTarget(
        Guid? targetId,
        long? targetVersion) =>
        (targetId is null && targetVersion is null) ||
        (targetId is Guid id &&
         id != Guid.Empty &&
         targetVersion is long version &&
         version is > 0 and < long.MaxValue);

    private static DataRightsRestrictionReleaseTarget ToReleaseTarget(
        ReservationProcessingRestriction restriction) =>
        new(
            restriction.Id,
            restriction.Version,
            restriction.ApplyCaseId,
            restriction.AppliedAtUtc);

    private static string? ResolveCommittedActor(
        ReservationProcessingRestrictionReceipt receipt,
        ReservationProcessingRestriction? restriction)
    {
        if (restriction is null ||
            restriction.Id != receipt.RestrictionId ||
            restriction.PropertyId != receipt.PropertyId ||
            restriction.ReservationId != receipt.ReservationId)
        {
            return null;
        }

        return receipt.Action switch
        {
            ReservationProcessingRestrictionAction.Apply
                when restriction.ApplyCaseId == receipt.CaseId &&
                     restriction.ApplyApprovalRevision ==
                        receipt.ApprovalRevision =>
                NormalizeActor(restriction.AppliedBy),
            ReservationProcessingRestrictionAction.Release
                when restriction.ReleaseCaseId == receipt.CaseId &&
                     restriction.ReleaseApprovalRevision ==
                        receipt.ApprovalRevision =>
                NormalizeActor(restriction.ReleasedBy),
            _ => null
        };
    }

    private static string? NormalizeActor(string? actorId)
    {
        string? normalized = actorId?.Trim();
        return normalized is { Length: > 0 } &&
            normalized.Length <= Reservation.ActorIdMaxLength
            ? normalized
            : null;
    }

    private static bool IsBlocked(string code) =>
        code is "Reservations.ProcessingRestrictionActiveStateInvalid"
            or "Reservations.ProcessingRestrictionProjectionUnavailable"
            or "Reservations.ProcessingRestrictionReservationVersionConflict"
            or "Reservations.ProcessingRestrictionProjectionVersionConflict";

    private static string ComputeDigest(
        ReservationProcessingRestrictionReceiptDto receipt,
        string actorId)
    {
        string canonical = string.Join(
            "|",
            receipt.ReceiptId.ToString("D"),
            receipt.RestrictionId.ToString("D"),
            ((int)receipt.Action).ToString(CultureInfo.InvariantCulture),
            receipt.PropertyId.ToString("D"),
            receipt.ReservationId.ToString("D"),
            receipt.CaseId.ToString("D"),
            receipt.ApprovalRevision.ToString(CultureInfo.InvariantCulture),
            receipt.SelectedReservationVersion.ToString(
                CultureInfo.InvariantCulture),
            receipt.ContractVersion.ToString(CultureInfo.InvariantCulture),
            receipt.RestrictionVersion.ToString(CultureInfo.InvariantCulture),
            receipt.ProjectionRevision.ToString(CultureInfo.InvariantCulture),
            receipt.EffectiveRestricted ? "1" : "0",
            actorId,
            receipt.EventId.ToString("D"),
            receipt.CompletedAtUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

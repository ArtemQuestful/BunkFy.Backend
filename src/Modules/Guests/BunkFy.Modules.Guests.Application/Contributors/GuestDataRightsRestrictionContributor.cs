namespace BunkFy.Modules.Guests.Application.Contributors;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Mapping;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class GuestDataRightsRestrictionContributor(
    IRequestDispatcher dispatcher,
    IGuestProcessingRestrictionProjectionRepository projections,
    IGuestProcessingRestrictionRepository restrictions,
    ISystemClock clock)
    : IDataRightsRestrictionContributor
{
    private static readonly PageRequest ActiveRestrictionPage = new(1, 2);
    private static readonly PageRequest ReleaseTargetPage = new(
        1,
        DataRightsRestrictionContract.MaxReleaseTargets + 1);

    public string OwnerKey => GuestsDataRightsCoordinates.Owner;

    public int ContractVersion => DataRightsRestrictionContract.CurrentVersion;

    public async Task<DataRightsRestrictionTargetResolutionResult>
        ResolveReleaseTargetsAsync(
            DataRightsRestrictionTargetResolutionRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsValid(request, clock.UtcNow))
        {
            return DataRightsRestrictionTargetResolutionResult.Failed(
                GuestsApplicationErrors.RestrictionRequestInvalid.Code);
        }

        Guid propertyId = request.PropertyId!.Value;
        if (request.TargetOwnerOperationId is Guid targetId)
        {
            GuestProcessingRestriction? target = await restrictions.GetAsync(
                propertyId,
                targetId,
                cancellationToken).ConfigureAwait(false);
            if (target is null || target.GuestId != request.Coordinate.RecordId)
            {
                return DataRightsRestrictionTargetResolutionResult.NotFound();
            }

            if (target.Status != GuestProcessingRestrictionState.Active ||
                target.Version != request.TargetOwnerOperationVersion)
            {
                return DataRightsRestrictionTargetResolutionResult.Stale();
            }

            return DataRightsRestrictionTargetResolutionResult.Completed(
                [ToReleaseTarget(target)]);
        }

        IReadOnlyCollection<GuestProcessingRestriction> active =
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
                GuestsApplicationErrors.RestrictionRequestInvalid.Code);
        }
        Guid propertyId = request.PropertyId!.Value;

        GuestProcessingRestrictionReceipt? replay =
            await restrictions.FindReceiptByIdempotencyKeyAsync(
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return ToResult(request, replay.ToDto());
        }

        GuestProcessingRestrictionProjection? projection = await projections.GetAsync(
            propertyId,
            request.Coordinate.RecordId,
            cancellationToken).ConfigureAwait(false);
        if (projection is null)
        {
            return DataRightsRestrictionContributionResult.Blocked(
                GuestsApplicationErrors.RestrictionProjectionUnavailable.Code);
        }

        GuestProcessingRestriction? releaseTarget = null;
        if (request.Directive == DataRightsRestrictionDirective.Release)
        {
            if (request.TargetOwnerOperationId is Guid targetId)
            {
                releaseTarget = await restrictions.GetAsync(
                    propertyId,
                    targetId,
                    cancellationToken).ConfigureAwait(false);
                if (releaseTarget is null ||
                    releaseTarget.GuestId != request.Coordinate.RecordId ||
                    releaseTarget.Status != GuestProcessingRestrictionState.Active ||
                    releaseTarget.Version != request.TargetOwnerOperationVersion)
                {
                    return DataRightsRestrictionContributionResult.Blocked(
                        GuestsApplicationErrors.RestrictionActiveStateInvalid.Code);
                }
            }
            else
            {
                IReadOnlyCollection<GuestProcessingRestriction> active =
                    await restrictions.ListActiveAsync(
                        propertyId,
                        request.Coordinate.RecordId,
                        ActiveRestrictionPage,
                        cancellationToken).ConfigureAwait(false);
                if (active.Count != 1)
                {
                    return DataRightsRestrictionContributionResult.Blocked(
                        GuestsApplicationErrors.RestrictionActiveStateInvalid.Code);
                }

                releaseTarget = active.Single();
            }
        }

        Result<GuestProcessingRestrictionReceiptDto> executed =
            request.Directive switch
            {
                DataRightsRestrictionDirective.Apply =>
                    await dispatcher.SendAsync(
                        new ApplyGuestProcessingRestrictionCommand(
                            request.IdempotencyKey,
                            propertyId,
                            request.CaseId,
                            request.ApprovalRevision,
                            request.Coordinate.RecordId,
                            request.Coordinate.RecordVersion,
                            projection.Revision,
                            request.ExecutingActorId),
                        cancellationToken).ConfigureAwait(false),
                DataRightsRestrictionDirective.Release when releaseTarget is not null =>
                    await this.ReleaseAsync(
                        request,
                        projection,
                        releaseTarget,
                        cancellationToken).ConfigureAwait(false),
                _ =>
                    Result.Failure<GuestProcessingRestrictionReceiptDto>(
                        GuestsApplicationErrors.RestrictionActiveStateInvalid)
            };

        if (executed.IsFailure)
        {
            return IsBlocked(executed.Error.Code)
                ? DataRightsRestrictionContributionResult.Blocked(executed.Error.Code)
                : DataRightsRestrictionContributionResult.Failed(executed.Error.Code);
        }

        return ToResult(request, executed.Value);
    }

    private static DataRightsRestrictionContributionResult ToResult(
        DataRightsRestrictionContributionRequest request,
        GuestProcessingRestrictionReceiptDto receipt)
    {
        if (!Matches(request, receipt))
        {
            return DataRightsRestrictionContributionResult.Failed(
                GuestsApplicationErrors.RestrictionOwnerProofInvalid.Code);
        }

        return DataRightsRestrictionContributionResult.Completed(
            new DataRightsRestrictionOwnerProof(
                GuestProcessingRestrictionContract.CurrentVersion,
                receipt.ReceiptId,
                receipt.RestrictionId,
                receipt.RestrictionVersion,
                receipt.ProjectionRevision,
                receipt.EffectiveRestricted,
                ComputeDigest(receipt),
                receipt.CompletedAtUtc));
    }

    private async Task<Result<GuestProcessingRestrictionReceiptDto>> ReleaseAsync(
        DataRightsRestrictionContributionRequest request,
        GuestProcessingRestrictionProjection projection,
        GuestProcessingRestriction restriction,
        CancellationToken cancellationToken) =>
        await dispatcher.SendAsync(
            new ReleaseGuestProcessingRestrictionCommand(
                request.IdempotencyKey,
                request.PropertyId!.Value,
                restriction.Id,
                request.CaseId,
                request.ApprovalRevision,
                request.Coordinate.RecordId,
                request.Coordinate.RecordVersion,
                restriction.Version,
                projection.Revision,
                request.ExecutingActorId,
                LegacyUnboundTarget: request.TargetOwnerOperationId is null),
            cancellationToken).ConfigureAwait(false);

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
            GuestsDataRightsCoordinates.Owner,
            StringComparison.Ordinal) &&
        string.Equals(
            request.Coordinate.RecordType,
            GuestsDataRightsCoordinates.GuestProfileRecordType,
            StringComparison.Ordinal) &&
        request.Coordinate.RecordId != Guid.Empty &&
        request.Coordinate.RecordVersion > 0 &&
        request.Directive is DataRightsRestrictionDirective.Apply
            or DataRightsRestrictionDirective.Release &&
        HasValidTarget(
            request.Directive,
            request.TargetOwnerOperationId,
            request.TargetOwnerOperationVersion) &&
        !string.IsNullOrWhiteSpace(request.ExecutingActorId) &&
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
            GuestsDataRightsCoordinates.Owner,
            StringComparison.Ordinal) &&
        string.Equals(
            request.Coordinate.RecordType,
            GuestsDataRightsCoordinates.GuestProfileRecordType,
            StringComparison.Ordinal) &&
        request.Coordinate.RecordId != Guid.Empty &&
        request.Coordinate.RecordVersion > 0 &&
        HasValidTarget(
            DataRightsRestrictionDirective.Release,
            request.TargetOwnerOperationId,
            request.TargetOwnerOperationVersion) &&
        request.DeadlineUtc > nowUtc;

    private static bool Matches(
        DataRightsRestrictionContributionRequest request,
        GuestProcessingRestrictionReceiptDto receipt)
    {
        GuestProcessingRestrictionActionDto expectedAction =
            request.Directive == DataRightsRestrictionDirective.Apply
                ? GuestProcessingRestrictionActionDto.Apply
                : GuestProcessingRestrictionActionDto.Release;
        bool targetMatches = request.Directive switch
        {
            DataRightsRestrictionDirective.Apply =>
                request.TargetOwnerOperationId is null &&
                request.TargetOwnerOperationVersion is null &&
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
            receipt.GuestId == request.Coordinate.RecordId &&
            receipt.CaseId == request.CaseId &&
            receipt.ApprovalRevision == request.ApprovalRevision &&
            receipt.SelectedGuestVersion == request.Coordinate.RecordVersion &&
            receipt.RestrictionVersion > 0 &&
            receipt.ProjectionRevision > 0 &&
            targetMatches &&
            string.Equals(
                receipt.ActorId,
                request.ExecutingActorId.Trim(),
                StringComparison.Ordinal) &&
            receipt.EventId != Guid.Empty &&
            receipt.CompletedAtUtc != default &&
            receipt.CompletedAtUtc <= request.DeadlineUtc;
    }

    private static bool HasValidTarget(
        DataRightsRestrictionDirective directive,
        Guid? targetId,
        long? targetVersion) =>
        directive == DataRightsRestrictionDirective.Apply
            ? targetId is null && targetVersion is null
            : (targetId is null && targetVersion is null) ||
              (targetId is Guid id &&
               id != Guid.Empty &&
               targetVersion is long version &&
               version is > 0 and < long.MaxValue);

    private static DataRightsRestrictionReleaseTarget ToReleaseTarget(
        GuestProcessingRestriction restriction) =>
        new(
            restriction.Id,
            restriction.Version,
            restriction.ApplyCaseId,
            restriction.AppliedAtUtc);

    private static bool IsBlocked(string code) =>
        code is "Guests.RestrictionActiveStateInvalid"
            or "Guests.RestrictionProjectionUnavailable"
            or "Guests.RestrictionGuestVersionConflict"
            or "Guests.RestrictionProjectionVersionConflict";

    private static string ComputeDigest(GuestProcessingRestrictionReceiptDto receipt)
    {
        string canonical = string.Join(
            "|",
            receipt.ReceiptId.ToString("D"),
            receipt.RestrictionId.ToString("D"),
            ((int)receipt.Action).ToString(CultureInfo.InvariantCulture),
            receipt.PropertyId.ToString("D"),
            receipt.GuestId.ToString("D"),
            receipt.CaseId.ToString("D"),
            receipt.ApprovalRevision.ToString(CultureInfo.InvariantCulture),
            receipt.SelectedGuestVersion.ToString(CultureInfo.InvariantCulture),
            receipt.RestrictionVersion.ToString(CultureInfo.InvariantCulture),
            receipt.ProjectionRevision.ToString(CultureInfo.InvariantCulture),
            receipt.EffectiveRestricted ? "1" : "0",
            receipt.ActorId,
            receipt.EventId.ToString("D"),
            receipt.CompletedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

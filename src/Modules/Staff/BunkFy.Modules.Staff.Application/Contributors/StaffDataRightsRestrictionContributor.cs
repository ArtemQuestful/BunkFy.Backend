namespace BunkFy.Modules.Staff.Application.Contributors;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Mapping;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class StaffDataRightsRestrictionContributor(
    IRequestDispatcher dispatcher,
    IStaffProcessingRestrictionProjectionRepository projections,
    IStaffProcessingRestrictionRepository restrictions,
    ISystemClock clock)
    : IDataRightsRestrictionContributor
{
    private static readonly PageRequest ActiveRestrictionPage = new(1, 2);
    private static readonly PageRequest ReleaseTargetPage = new(
        1,
        DataRightsRestrictionContract.MaxReleaseTargets + 1);

    public string OwnerKey => StaffDataRightsCoordinates.Owner;

    public int ContractVersion => DataRightsRestrictionContract.CurrentVersion;

    public async Task<DataRightsRestrictionTargetResolutionResult>
        ResolveReleaseTargetsAsync(
            DataRightsRestrictionTargetResolutionRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsValid(request, clock.UtcNow))
        {
            return DataRightsRestrictionTargetResolutionResult.Failed(
                StaffApplicationErrors.RestrictionRequestInvalid.Code);
        }

        if (request.TargetOwnerOperationId is Guid targetId)
        {
            StaffProcessingRestriction? target = await restrictions.GetAsync(
                targetId,
                cancellationToken).ConfigureAwait(false);
            if (target is null ||
                target.StaffMemberId != request.Coordinate.RecordId)
            {
                return DataRightsRestrictionTargetResolutionResult.NotFound();
            }

            if (target.Status != StaffProcessingRestrictionState.Active ||
                target.Version != request.TargetOwnerOperationVersion)
            {
                return DataRightsRestrictionTargetResolutionResult.Stale();
            }

            return DataRightsRestrictionTargetResolutionResult.Completed(
                [ToReleaseTarget(target)]);
        }

        IReadOnlyCollection<StaffProcessingRestriction> active =
            await restrictions.ListActiveAsync(
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
                StaffApplicationErrors.RestrictionRequestInvalid.Code);
        }

        StaffProcessingRestrictionReceipt? replay =
            await restrictions.FindReceiptByIdempotencyKeyAsync(
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return ToResult(request, replay.ToDto());
        }

        StaffProcessingRestrictionProjection? projection =
            await projections.GetAsync(
                request.Coordinate.RecordId,
                cancellationToken).ConfigureAwait(false);
        if (projection is null)
        {
            return DataRightsRestrictionContributionResult.Blocked(
                StaffApplicationErrors.RestrictionProjectionUnavailable.Code);
        }

        StaffProcessingRestriction? releaseTarget = null;
        if (request.Directive == DataRightsRestrictionDirective.Release)
        {
            if (request.TargetOwnerOperationId is Guid targetId)
            {
                releaseTarget = await restrictions.GetAsync(
                    targetId,
                    cancellationToken).ConfigureAwait(false);
                if (releaseTarget is null ||
                    releaseTarget.StaffMemberId != request.Coordinate.RecordId ||
                    releaseTarget.Status != StaffProcessingRestrictionState.Active ||
                    releaseTarget.Version != request.TargetOwnerOperationVersion)
                {
                    return DataRightsRestrictionContributionResult.Blocked(
                        StaffApplicationErrors.RestrictionActiveStateInvalid.Code);
                }
            }
            else
            {
                IReadOnlyCollection<StaffProcessingRestriction> active =
                    await restrictions.ListActiveAsync(
                        request.Coordinate.RecordId,
                        ActiveRestrictionPage,
                        cancellationToken).ConfigureAwait(false);
                if (active.Count != 1)
                {
                    return DataRightsRestrictionContributionResult.Blocked(
                        StaffApplicationErrors.RestrictionActiveStateInvalid.Code);
                }

                releaseTarget = active.Single();
            }
        }

        Result<StaffProcessingRestrictionReceiptDto> executed =
            request.Directive switch
            {
                DataRightsRestrictionDirective.Apply =>
                    await dispatcher.SendAsync(
                        new ApplyStaffProcessingRestrictionCommand(
                            request.IdempotencyKey,
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
                    Result.Failure<StaffProcessingRestrictionReceiptDto>(
                        StaffApplicationErrors.RestrictionActiveStateInvalid)
            };

        if (executed.IsFailure)
        {
            return IsBlocked(executed.Error.Code)
                ? DataRightsRestrictionContributionResult.Blocked(
                    executed.Error.Code)
                : DataRightsRestrictionContributionResult.Failed(
                    executed.Error.Code);
        }

        return ToResult(request, executed.Value);
    }

    private static DataRightsRestrictionContributionResult ToResult(
        DataRightsRestrictionContributionRequest request,
        StaffProcessingRestrictionReceiptDto receipt)
    {
        if (!Matches(request, receipt))
        {
            return DataRightsRestrictionContributionResult.Failed(
                StaffApplicationErrors.RestrictionOwnerProofInvalid.Code);
        }

        return DataRightsRestrictionContributionResult.Completed(
            new DataRightsRestrictionOwnerProof(
                StaffProcessingRestrictionContract.CurrentVersion,
                receipt.ReceiptId,
                receipt.RestrictionId,
                receipt.RestrictionVersion,
                receipt.ProjectionRevision,
                receipt.EffectiveRestricted,
                ComputeDigest(receipt),
                receipt.CompletedAtUtc));
    }

    private async Task<Result<StaffProcessingRestrictionReceiptDto>> ReleaseAsync(
        DataRightsRestrictionContributionRequest request,
        StaffProcessingRestrictionProjection projection,
        StaffProcessingRestriction restriction,
        CancellationToken cancellationToken) =>
        await dispatcher.SendAsync(
            new ReleaseStaffProcessingRestrictionCommand(
                request.IdempotencyKey,
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
        request.CaseType == DataRightsCaseType.StaffRights &&
        !string.IsNullOrWhiteSpace(request.TenantId) &&
        request.IdempotencyKey != Guid.Empty &&
        request.PropertyId is null &&
        request.CaseId != Guid.Empty &&
        request.ApprovalRevision > 0 &&
        request.Coordinate is not null &&
        string.Equals(
            request.Coordinate.OwnerKey,
            StaffDataRightsCoordinates.Owner,
            StringComparison.Ordinal) &&
        string.Equals(
            request.Coordinate.RecordType,
            StaffDataRightsCoordinates.StaffMemberRecordType,
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
        request.CaseType == DataRightsCaseType.StaffRights &&
        !string.IsNullOrWhiteSpace(request.TenantId) &&
        request.PropertyId is null &&
        request.CaseId != Guid.Empty &&
        request.Coordinate is not null &&
        string.Equals(
            request.Coordinate.OwnerKey,
            StaffDataRightsCoordinates.Owner,
            StringComparison.Ordinal) &&
        string.Equals(
            request.Coordinate.RecordType,
            StaffDataRightsCoordinates.StaffMemberRecordType,
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
        StaffProcessingRestrictionReceiptDto receipt)
    {
        StaffProcessingRestrictionActionDto expectedAction =
            request.Directive == DataRightsRestrictionDirective.Apply
                ? StaffProcessingRestrictionActionDto.Apply
                : StaffProcessingRestrictionActionDto.Release;
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
            receipt.StaffMemberId == request.Coordinate.RecordId &&
            receipt.CaseId == request.CaseId &&
            receipt.ApprovalRevision == request.ApprovalRevision &&
            receipt.SelectedStaffVersion == request.Coordinate.RecordVersion &&
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
        StaffProcessingRestriction restriction) =>
        new(
            restriction.Id,
            restriction.Version,
            restriction.ApplyCaseId,
            restriction.AppliedAtUtc);

    private static bool IsBlocked(string code) =>
        code is "Staff.RestrictionActiveStateInvalid"
            or "Staff.RestrictionProjectionUnavailable"
            or "Staff.RestrictionStaffVersionConflict"
            or "Staff.RestrictionProjectionVersionConflict";

    private static string ComputeDigest(
        StaffProcessingRestrictionReceiptDto receipt)
    {
        string canonical = string.Join(
            "|",
            receipt.ReceiptId.ToString("D"),
            receipt.RestrictionId.ToString("D"),
            ((int)receipt.Action).ToString(CultureInfo.InvariantCulture),
            receipt.StaffMemberId.ToString("D"),
            receipt.CaseId.ToString("D"),
            receipt.ApprovalRevision.ToString(CultureInfo.InvariantCulture),
            receipt.SelectedStaffVersion.ToString(CultureInfo.InvariantCulture),
            receipt.RestrictionVersion.ToString(CultureInfo.InvariantCulture),
            receipt.ProjectionRevision.ToString(CultureInfo.InvariantCulture),
            receipt.EffectiveRestricted ? "1" : "0",
            receipt.ActorId,
            receipt.EventId.ToString("D"),
            receipt.CompletedAtUtc.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

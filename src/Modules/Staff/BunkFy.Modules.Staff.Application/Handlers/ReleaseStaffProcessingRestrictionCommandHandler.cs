namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Mapping;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ReleaseStaffProcessingRestrictionCommandHandler(
    IStaffMemberRepository members,
    IStaffProcessingRestrictionProjectionRepository projections,
    IStaffProcessingRestrictionRepository restrictions,
    IStaffOperationLock operationLock,
    IDataRightsOperationApprovalGate approvalGate,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<
        ReleaseStaffProcessingRestrictionCommand,
        StaffProcessingRestrictionReceiptDto>
{
    public async Task<Result<StaffProcessingRestrictionReceiptDto>> HandleAsync(
        ReleaseStaffProcessingRestrictionCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.TenantRequired);
        }

        string? actorId = NormalizeActor(command.ActorId);
        if (command.IdempotencyKey == Guid.Empty ||
            command.RestrictionId == Guid.Empty ||
            command.CaseId == Guid.Empty ||
            command.ApprovalRevision < 1 ||
            command.StaffMemberId == Guid.Empty ||
            command.ExpectedStaffVersion < 1 ||
            command.ExpectedRestrictionVersion < 1 ||
            command.ExpectedProjectionRevision < 1 ||
            actorId is null)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.RestrictionRequestInvalid);
        }

        StaffProcessingRestrictionReceipt? existing =
            await restrictions.FindReceiptByIdempotencyKeyAsync(
                command.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, command, actorId);
        }

        DataRightsOperationApprovalResult approval =
            await approvalGate.EvaluateAsync(
                new DataRightsOperationApprovalRequest(
                    scopeContext.ScopeId,
                    PropertyId: null,
                    command.CaseId,
                    command.ApprovalRevision,
                    DataRightsOperation.Restriction,
                    StaffDataRightsCoordinates.Owner,
                    StaffDataRightsCoordinates.StaffMemberRecordType,
                    command.StaffMemberId,
                    command.ExpectedStaffVersion,
                    DataRightsRestrictionDirective.Release,
                    ExecutingActorId: actorId,
                    CaseType: DataRightsCaseType.StaffRights,
                    RestrictionTargetOwnerOperationId:
                        command.LegacyUnboundTarget ? null : command.RestrictionId,
                    RestrictionTargetOwnerOperationVersion:
                        command.LegacyUnboundTarget
                            ? null
                            : command.ExpectedRestrictionVersion),
                cancellationToken).ConfigureAwait(false);
        if (!approval.IsApproved)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.DataRightsApprovalRequired);
        }

        if (!await operationLock.TryAcquireStaffMemberAsync(
                scopeContext.ScopeId,
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<
                StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.StaffMemberNotFound);
        }

        existing = await restrictions
            .FindReceiptByIdempotencyKeyAsync(
                command.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, command, actorId);
        }

        StaffMember? member = await members.GetForDataRightsAsync(
            command.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.StaffMemberNotFound);
        }

        if (member.Version != command.ExpectedStaffVersion)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.RestrictionStaffVersionConflict);
        }

        StaffProcessingRestriction? priorRelease =
            await restrictions.FindByReleaseApprovalAsync(
                command.StaffMemberId,
                command.CaseId,
                command.ApprovalRevision,
                cancellationToken).ConfigureAwait(false);
        if (priorRelease is not null)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.RestrictionApprovalAlreadyUsed);
        }

        StaffProcessingRestriction? restriction =
            await restrictions.GetAsync(
                command.RestrictionId,
                cancellationToken).ConfigureAwait(false);
        if (restriction is null ||
            restriction.StaffMemberId != command.StaffMemberId)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.RestrictionNotFound);
        }

        StaffProcessingRestrictionProjection? projection =
            await projections.GetAsync(
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (projection is null ||
            projection.ContractVersion !=
                StaffProcessingRestrictionContract.CurrentVersion)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.RestrictionProjectionUnavailable);
        }

        DateTimeOffset nowUtc = ToPersistencePrecision(clock.UtcNow);
        Result restrictionValidation = restriction.ValidateRelease(
            command.CaseId,
            command.ApprovalRevision,
            command.ExpectedStaffVersion,
            command.ExpectedRestrictionVersion,
            actorId,
            nowUtc);
        if (restrictionValidation.IsFailure)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                restrictionValidation.Error);
        }

        Result projectionValidation = projection.ValidateRelease(
            command.ExpectedProjectionRevision,
            StaffProcessingRestrictionContract.CurrentVersion,
            nowUtc);
        if (projectionValidation.IsFailure)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                projectionValidation.Error);
        }

        _ = restriction.Release(
            command.CaseId,
            command.ApprovalRevision,
            command.ExpectedStaffVersion,
            command.ExpectedRestrictionVersion,
            actorId,
            nowUtc);
        _ = projection.Release(
            command.ExpectedProjectionRevision,
            StaffProcessingRestrictionContract.CurrentVersion,
            nowUtc);

        Result<StaffProcessingRestrictionReceipt> receipt =
            StaffProcessingRestrictionReceipt.Create(
                ids.NewId(),
                scopeContext.ScopeId,
                command.IdempotencyKey,
                restriction.Id,
                StaffProcessingRestrictionAction.Release,
                command.StaffMemberId,
                command.CaseId,
                command.ApprovalRevision,
                command.ExpectedStaffVersion,
                StaffProcessingRestrictionContract.CurrentVersion,
                restriction.Version,
                projection.Revision,
                projection.IsRestricted,
                actorId,
                ids.NewId(),
                nowUtc);
        if (receipt.IsFailure)
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                receipt.Error);
        }

        await restrictions.AddReceiptAsync(
            receipt.Value,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt.Value.ToDto());
    }

    private static Result<StaffProcessingRestrictionReceiptDto> Replay(
        StaffProcessingRestrictionReceipt receipt,
        ReleaseStaffProcessingRestrictionCommand command,
        string actorId)
    {
        if (receipt.Action != StaffProcessingRestrictionAction.Release ||
            receipt.RestrictionId != command.RestrictionId ||
            receipt.StaffMemberId != command.StaffMemberId ||
            receipt.CaseId != command.CaseId ||
            receipt.ApprovalRevision != command.ApprovalRevision ||
            receipt.SelectedStaffVersion != command.ExpectedStaffVersion ||
            receipt.ResultingRestrictionVersion - 1 !=
                command.ExpectedRestrictionVersion ||
            receipt.ResultingProjectionRevision - 1 !=
                command.ExpectedProjectionRevision ||
            !string.Equals(
                receipt.ActorId,
                actorId,
                StringComparison.Ordinal))
        {
            return Result.Failure<StaffProcessingRestrictionReceiptDto>(
                StaffApplicationErrors.RestrictionIdempotencyConflict);
        }

        return Result.Success(receipt.ToDto());
    }

    private static string? NormalizeActor(string? actorId)
    {
        string? normalized = actorId?.Trim();
        return normalized is { Length: > 0 } &&
            normalized.Length <= StaffMember.ActorIdMaxLength
            ? normalized
            : null;
    }

    private static DateTimeOffset ToPersistencePrecision(
        DateTimeOffset value)
    {
        const long ticksPerMicrosecond =
            TimeSpan.TicksPerMillisecond / 1000;
        return new(
            value.Ticks - (value.Ticks % ticksPerMicrosecond),
            value.Offset);
    }
}

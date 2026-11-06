namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Mapping;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class
    ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommandHandler(
        IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
            projections,
        IWorkspaceStaffOnboardingProcessingRestrictionRepository restrictions,
        WorkspaceStaffOnboardingMutationCoordinator mutations,
        IDataRightsOperationApprovalGate approvalGate,
        IScopeContext scopeContext,
        ISystemClock clock,
        IIdGenerator ids)
    : ICommandHandler<
        ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommand,
        WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>
{
    public async Task<
        Result<WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>>
        HandleAsync(
            ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommand
                command,
            CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors.ScopeRequired);
        }

        string? actorId = NormalizeActor(command.ActorId);
        if (command.IdempotencyKey == Guid.Empty ||
            command.RestrictionId == Guid.Empty ||
            command.CaseId == Guid.Empty ||
            command.ApprovalRevision < 1 ||
            command.ApplicationId == Guid.Empty ||
            command.ExpectedOnboardingVersion < 1 ||
            command.ExpectedRestrictionVersion < 1 ||
            command.ExpectedProjectionRevision < 1 ||
            actorId is null)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionRequestInvalid);
        }

        WorkspaceStaffOnboardingProcessingRestrictionReceipt? existing =
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
                    WorkspacesDataRightsCoordinates.Owner,
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    command.ApplicationId,
                    command.ExpectedOnboardingVersion,
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
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionApprovalRequired);
        }

        WorkspaceStaffOnboardingMutationLease lease =
            await mutations.AcquireExistingAsync(
                command.ApplicationId,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: false,
                cancellationToken).ConfigureAwait(false);

        existing = await restrictions.FindReceiptByIdempotencyKeyAsync(
            command.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, command, actorId);
        }

        WorkspaceStaffOnboarding? application = lease.Application;
        if (application is null)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .ApplicationNotFound);
        }

        if (application.Version != command.ExpectedOnboardingVersion)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionOnboardingVersionConflict);
        }

        WorkspaceStaffOnboardingProcessingRestriction? priorRelease =
            await restrictions.FindByReleaseApprovalAsync(
                command.ApplicationId,
                command.CaseId,
                command.ApprovalRevision,
                cancellationToken).ConfigureAwait(false);
        if (priorRelease is not null)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionApprovalAlreadyUsed);
        }

        WorkspaceStaffOnboardingProcessingRestriction? restriction =
            await restrictions.GetAsync(
                command.RestrictionId,
                cancellationToken).ConfigureAwait(false);
        if (restriction is null ||
            restriction.ApplicationId != command.ApplicationId)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionNotFound);
        }

        WorkspaceStaffOnboardingProcessingRestrictionProjection? projection =
            await projections.GetAsync(
                command.ApplicationId,
                cancellationToken).ConfigureAwait(false);
        if (projection is null ||
            projection.ContractVersion !=
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionProjectionUnavailable);
        }

        DateTimeOffset nowUtc = ToPersistencePrecision(clock.UtcNow);
        Result restrictionValidation = restriction.ValidateRelease(
            command.CaseId,
            command.ApprovalRevision,
            command.ExpectedOnboardingVersion,
            command.ExpectedRestrictionVersion,
            actorId,
            nowUtc);
        if (restrictionValidation.IsFailure)
        {
            return Failure(restrictionValidation.Error);
        }

        Result projectionValidation = projection.ValidateRelease(
            command.ExpectedProjectionRevision,
            WorkspaceStaffOnboardingProcessingRestrictionContract
                .CurrentVersion,
            nowUtc);
        if (projectionValidation.IsFailure)
        {
            return Failure(projectionValidation.Error);
        }

        _ = restriction.Release(
            command.CaseId,
            command.ApprovalRevision,
            command.ExpectedOnboardingVersion,
            command.ExpectedRestrictionVersion,
            actorId,
            nowUtc);
        _ = projection.Release(
            command.ExpectedProjectionRevision,
            WorkspaceStaffOnboardingProcessingRestrictionContract
                .CurrentVersion,
            nowUtc);

        Result<WorkspaceStaffOnboardingProcessingRestrictionReceipt> receipt =
            WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                ids.NewId(),
                scopeContext.ScopeId,
                command.IdempotencyKey,
                restriction.Id,
                WorkspaceStaffOnboardingProcessingRestrictionAction.Release,
                command.ApplicationId,
                command.CaseId,
                command.ApprovalRevision,
                command.ExpectedOnboardingVersion,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                restriction.Version,
                projection.Revision,
                projection.IsRestricted,
                actorId,
                ids.NewId(),
                nowUtc);
        if (receipt.IsFailure)
        {
            return Failure(receipt.Error);
        }

        await restrictions.AddReceiptAsync(
            receipt.Value,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt.Value.ToDto());
    }

    private static Result<
        WorkspaceStaffOnboardingProcessingRestrictionReceiptDto> Replay(
        WorkspaceStaffOnboardingProcessingRestrictionReceipt receipt,
        ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommand command,
        string actorId)
    {
        if (receipt.Action !=
                WorkspaceStaffOnboardingProcessingRestrictionAction.Release ||
            receipt.RestrictionId != command.RestrictionId ||
            receipt.ApplicationId != command.ApplicationId ||
            receipt.CaseId != command.CaseId ||
            receipt.ApprovalRevision != command.ApprovalRevision ||
            receipt.SelectedOnboardingVersion !=
                command.ExpectedOnboardingVersion ||
            receipt.ResultingRestrictionVersion - 1 !=
                command.ExpectedRestrictionVersion ||
            receipt.ResultingProjectionRevision - 1 !=
                command.ExpectedProjectionRevision ||
            !string.Equals(
                receipt.ActorId,
                actorId,
                StringComparison.Ordinal))
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionIdempotencyConflict);
        }

        return Result.Success(receipt.ToDto());
    }

    private static Result<
        WorkspaceStaffOnboardingProcessingRestrictionReceiptDto> Failure(
        Error error) =>
        Result.Failure<
            WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>(error);

    private static string? NormalizeActor(string? actorId)
    {
        string? normalized = actorId?.Trim();
        return normalized is { Length: > 0 } &&
            normalized.Length <=
                WorkspaceStaffOnboardingRules.ActorIdMaxLength
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

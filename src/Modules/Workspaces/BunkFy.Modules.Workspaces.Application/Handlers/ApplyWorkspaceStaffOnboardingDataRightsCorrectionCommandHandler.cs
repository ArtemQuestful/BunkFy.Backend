namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Authorization;
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
using Gma.Modules.Organizations.Contracts;

internal sealed class
    ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler(
        IWorkspaceStaffOnboardingCorrectionReceiptRepository receipts,
        WorkspaceStaffOnboardingMutationCoordinator mutations,
        WorkspaceStaffOnboardingIdentityAnchorConvergence anchorConvergence,
        WorkspaceStaffOnboardingDataRightsCorrectionAuthorizer authorizer,
        IOrganizationEnrollmentClaimInspector claims,
        IScopeContext scopeContext,
        ISystemClock clock,
        IIdGenerator ids)
    : ICommandHandler<
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand,
        WorkspaceStaffOnboardingDataRightsCorrectionOutcome>
{
    public async Task<Result<
        WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>> HandleAsync(
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand command,
        CancellationToken cancellationToken) =>
        Map(
            await this.HandleWithAuthorityOutcomeAsync(
                command,
                cancellationToken).ConfigureAwait(false));

    Task<Result<WorkspaceStaffOnboardingDataRightsCorrectionOutcome>>
        ICommandHandler<
            ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand,
            WorkspaceStaffOnboardingDataRightsCorrectionOutcome>.HandleAsync(
            ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand command,
            CancellationToken cancellationToken) =>
            this.HandleWithAuthorityOutcomeAsync(command, cancellationToken);

    public async Task<
        Result<WorkspaceStaffOnboardingDataRightsCorrectionOutcome>>
        HandleWithAuthorityOutcomeAsync(
            ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand command,
            CancellationToken cancellationToken)
    {
        Result valid = authorizer.Validate(
            command.ExecutionId,
            command.CaseId,
            command.ApprovalRevision,
            command.ApplicationId,
            command.ExpectedVersion,
            command.ActorId);
        if (valid.IsFailure)
        {
            return Failure(valid.Error);
        }

        Result<WorkspaceStaffApplicantProfile> requested =
            WorkspaceStaffApplicantProfile.Create(
                command.DisplayName,
                command.LegalName,
                command.WorkEmail,
                command.WorkPhone,
                command.EmployeeNumber,
                command.JobTitle,
                command.Department);
        if (requested.IsFailure)
        {
            return Failure(requested.Error);
        }

        string requestSha256 =
            WorkspaceStaffOnboardingDataRightsCorrectionFingerprint.Compute(
                requested.Value);
        WorkspaceStaffOnboardingCorrectionReceipt? existing =
            await receipts.FindByExecutionIdAsync(
                command.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, command, requestSha256);
        }

        Result authorized = await authorizer.AuthorizeAsync(
            command.ExecutionId,
            command.CaseId,
            command.ApprovalRevision,
            command.ApplicationId,
            command.ExpectedVersion,
            command.ActorId,
            cancellationToken).ConfigureAwait(false);
        if (authorized.IsFailure)
        {
            return Failure(authorized.Error);
        }

        WorkspaceStaffOnboardingMutationLease lease =
            await mutations.AcquireExistingAsync(
                command.ApplicationId,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: false,
                cancellationToken).ConfigureAwait(false);

        existing = await receipts.FindByExecutionIdAsync(
            command.ExecutionId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, command, requestSha256);
        }

        WorkspaceStaffOnboarding? application = lease.Application;
        if (application is null)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .ApplicationNotFound);
        }

        Result<WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>
            converged = await anchorConvergence.ConvergeAcquiredAsync(
                application,
                cancellationToken).ConfigureAwait(false);
        if (converged.IsFailure)
        {
            return Failure(converged.Error);
        }

        if (converged.Value.Outcome !=
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Absent)
        {
            return Result.Success(
                WorkspaceStaffOnboardingDataRightsCorrectionOutcome
                    .AuthorityMovedToStaff());
        }

        if (WorkspaceStaffOnboardingProfileMutationAuthority
            .HasLocalIdentityAnchorCoordinates(application))
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .IdentityAnchorConflict);
        }

        if (command.ExpectedVersion != application.Version)
        {
            return Failure(
                WorkspaceStaffOnboardingErrors.CorrectionVersionConflict);
        }

        if (application.Status != WorkspaceStaffOnboardingState.Submitted)
        {
            return Failure(WorkspaceStaffOnboardingErrors.CorrectionUnavailable);
        }

        DateTimeOffset nowUtc = ToPersistencePrecision(clock.UtcNow);
        Guid organizationId = Guid.TryParse(
            application.ScopeId,
            out Guid parsedOrganizationId)
                ? parsedOrganizationId
                : Guid.Empty;
        if (await WorkspaceStaffOnboardingProfileMutationAuthority
            .IsFencedAsync(
                claims,
                application.SourceKind,
                organizationId,
                application.SourceId,
                application.SubjectId,
                nowUtc,
                cancellationToken).ConfigureAwait(false))
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .CorrectionTargetUnavailable);
        }

        Result<WorkspaceStaffOnboardingCorrectionOutcome> updated =
            application.ApplyDataRightsCorrection(
                requested.Value,
                command.ExpectedVersion,
                ids.NewId(),
                nowUtc);
        if (updated.IsFailure)
        {
            return Failure(updated.Error);
        }

        Result<WorkspaceStaffOnboardingCorrectionReceipt> created =
            WorkspaceStaffOnboardingCorrectionReceipt.Create(
                ids.NewId(),
                scopeContext.ScopeId!,
                command.ExecutionId,
                command.CaseId,
                command.ApprovalRevision,
                command.ApplicationId,
                updated.Value.PreviousVersion,
                updated.Value.CurrentVersion,
                updated.Value.ChangedFields,
                requestSha256,
                updated.Value.ApplicantEventId,
                ids.NewId(),
                updated.Value.OccurredAtUtc);
        if (created.IsFailure)
        {
            return Failure(created.Error);
        }

        await receipts.AddAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
        return Applied(created.Value);
    }

    private static Result<
        WorkspaceStaffOnboardingDataRightsCorrectionOutcome> Replay(
        WorkspaceStaffOnboardingCorrectionReceipt receipt,
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand command,
        string requestSha256) =>
        receipt.MatchesReplay(
            command.CaseId,
            command.ApprovalRevision,
            command.ApplicationId,
            command.ExpectedVersion,
            requestSha256)
            ? Applied(receipt)
            : Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionOutcome>(
                WorkspaceStaffOnboardingApplicationErrors
                    .CorrectionIdempotencyConflict);

    private static Result<WorkspaceStaffOnboardingDataRightsCorrectionOutcome>
        Applied(WorkspaceStaffOnboardingCorrectionReceipt receipt) =>
        Result.Success(
            WorkspaceStaffOnboardingDataRightsCorrectionOutcome.Applied(
                receipt.ToDto()));

    private static Result<WorkspaceStaffOnboardingDataRightsCorrectionOutcome>
        Failure(Error error) =>
        Result.Failure<WorkspaceStaffOnboardingDataRightsCorrectionOutcome>(
            error);

    private static Result<
        WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> Map(
        Result<WorkspaceStaffOnboardingDataRightsCorrectionOutcome> outcome)
    {
        if (outcome.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                outcome.Error);
        }

        return outcome.Value.Kind switch
        {
            WorkspaceStaffOnboardingDataRightsCorrectionOutcomeKind.Applied
                when outcome.Value.Receipt is not null =>
                Result.Success(outcome.Value.Receipt),
            WorkspaceStaffOnboardingDataRightsCorrectionOutcomeKind
                .AuthorityMovedToStaff
                when outcome.Value.Receipt is null =>
                Result.Failure<
                    WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .CorrectionTargetUnavailable),
            _ => Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .IdentityAnchorConflict)
        };
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

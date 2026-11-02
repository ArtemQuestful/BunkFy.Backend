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
        WorkspaceStaffOnboardingDataRightsCorrectionAuthorizer authorizer,
        IOrganizationEnrollmentClaimInspector claims,
        IScopeContext scopeContext,
        ISystemClock clock,
        IIdGenerator ids)
    : ICommandHandler<
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand,
        WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>
{
    public async Task<
        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>>
        HandleAsync(
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
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                valid.Error);
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
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                requested.Error);
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
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                authorized.Error);
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
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .ApplicationNotFound);
        }

        if (command.ExpectedVersion != application.Version)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                WorkspaceStaffOnboardingErrors.CorrectionVersionConflict);
        }

        if (application.Status != WorkspaceStaffOnboardingState.Submitted)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                WorkspaceStaffOnboardingErrors.CorrectionUnavailable);
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
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
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
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                updated.Error);
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
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                created.Error);
        }

        await receipts.AddAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(created.Value.ToDto());
    }

    private static Result<
        WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> Replay(
            WorkspaceStaffOnboardingCorrectionReceipt receipt,
            ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand command,
            string requestSha256) =>
        receipt.MatchesReplay(
            command.CaseId,
            command.ApprovalRevision,
            command.ApplicationId,
            command.ExpectedVersion,
            requestSha256)
            ? Result.Success(receipt.ToDto())
            : Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .CorrectionIdempotencyConflict);

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

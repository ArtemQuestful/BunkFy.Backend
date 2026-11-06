namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Authorization;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class
    GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQueryHandler(
        WorkspaceStaffOnboardingMutationCoordinator mutations,
        IWorkspaceStaffOnboardingSerializedReadBoundary readBoundary,
        IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader anchorOutcomes,
        WorkspaceStaffOnboardingDataRightsCorrectionAuthorizer authorizer)
    : IQueryHandler<
        GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQuery,
        WorkspaceStaffOnboardingDataRightsCorrectionTargetDto>
{
    public async Task<
        Result<WorkspaceStaffOnboardingDataRightsCorrectionTargetDto>>
        HandleAsync(
            GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQuery query,
            CancellationToken cancellationToken)
    {
        Result authorized = await authorizer.AuthorizeAsync(
            query.ExecutionId,
            query.CaseId,
            query.ApprovalRevision,
            query.ApplicationId,
            query.ExpectedVersion,
            query.ActorId,
            cancellationToken).ConfigureAwait(false);
        if (authorized.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingDataRightsCorrectionTargetDto>(
                authorized.Error);
        }

        return await readBoundary.RunAsync(
            async readToken =>
        {
            WorkspaceStaffOnboardingMutationLease lease =
                await mutations.AcquireExistingAsync(
                    query.ApplicationId,
                    WorkspaceStaffOnboardingSourceLockMode.Read,
                    requireOperational: false,
                    readToken).ConfigureAwait(false);
            WorkspaceStaffOnboarding? application = lease.Application;
            if (application is null)
            {
                return Result.Failure<
                    WorkspaceStaffOnboardingDataRightsCorrectionTargetDto>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .ApplicationNotFound);
            }

            StaffWorkspaceOnboardingIdentityAnchorOutcome outcome =
                await anchorOutcomes.ReadAsync(
                    new StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                        application.Id,
                        application.SubjectId),
                    readToken).ConfigureAwait(false);
            if (!WorkspaceStaffOnboardingProfileMutationAuthority
                .IsExactAbsent(application, outcome) ||
                application.Status != WorkspaceStaffOnboardingState.Submitted ||
                application.Version != query.ExpectedVersion ||
                string.IsNullOrWhiteSpace(application.DisplayName))
            {
                return Result.Failure<
                    WorkspaceStaffOnboardingDataRightsCorrectionTargetDto>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .CorrectionTargetUnavailable);
            }

            return Result.Success(
                new WorkspaceStaffOnboardingDataRightsCorrectionTargetDto(
                    application.Id,
                    application.Version,
                    application.DisplayName,
                    application.LegalName,
                    application.WorkEmail,
                    application.WorkPhone,
                    application.EmployeeNumber,
                    application.JobTitle,
                    application.Department));
        }, cancellationToken).ConfigureAwait(false);
    }
}

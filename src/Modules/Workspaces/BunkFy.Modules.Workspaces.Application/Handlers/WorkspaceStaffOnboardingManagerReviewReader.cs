namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Mapping;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed class WorkspaceStaffOnboardingManagerReviewReader(
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    IWorkspaceStaffOnboardingSerializedReadBoundary readBoundary,
    IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader anchorOutcomes,
    WorkspaceOperationalAdmissionEvaluator operationalAdmission,
    IScopeContext scopeContext)
{
    public async Task<Result<WorkspaceStaffOnboardingDto>> ReadReviewAsync(
        Guid applicationId,
        CancellationToken cancellationToken)
    {
        Result<WorkspaceStaffOnboardingManagerReview> result = await this.ReadAsync(
            applicationId, actionableOnly: false, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? Result.Failure<WorkspaceStaffOnboardingDto>(result.Error)
            : Result.Success(result.Value.Application!);
    }

    public Task<Result<WorkspaceStaffOnboardingManagerReview>> ReadLegacyActionableAsync(
        Guid applicationId,
        CancellationToken cancellationToken) => this.ReadAsync(
            applicationId, actionableOnly: true, cancellationToken);

    private async Task<Result<WorkspaceStaffOnboardingManagerReview>> ReadAsync(
        Guid applicationId,
        bool actionableOnly,
        CancellationToken cancellationToken)
        => await readBoundary.RunAsync(
            async readToken =>
        {
            Result admitted = WorkspaceOperationalAdmissionGuard.RequireAllowed(
                await operationalAdmission.EvaluateAsync(
                    scopeContext.ScopeId ?? string.Empty,
                    readToken).ConfigureAwait(false));
            if (admitted.IsFailure)
            {
                return Result.Failure<WorkspaceStaffOnboardingManagerReview>(
                    admitted.Error);
            }

            WorkspaceStaffOnboardingMutationLease lease =
                await mutations.AcquireExistingAsync(
                    applicationId,
                    WorkspaceStaffOnboardingSourceLockMode.Read,
                    requireOperational: true,
                    readToken).ConfigureAwait(false);
            if (lease.CoordinateExists && lease.Application is null)
            {
                return Result.Failure<WorkspaceStaffOnboardingManagerReview>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .ProcessingRestricted);
            }

            WorkspaceStaffOnboarding? application = lease.Application;
            if (application is null)
            {
                return actionableOnly
                    ? Result.Success(new WorkspaceStaffOnboardingManagerReview(null))
                    : Result.Failure<WorkspaceStaffOnboardingManagerReview>(
                        WorkspaceStaffOnboardingApplicationErrors
                            .ApplicationNotFound);
            }

            // A row may have completed while its bounded page was being read.
            // Decide under the same lock, before touching any staging profile.
            if (actionableOnly &&
                !((application.Status == WorkspaceStaffOnboardingState.PendingApproval &&
                    application.ClaimId.HasValue) ||
                  application.Status == WorkspaceStaffOnboardingState.Failed))
            {
                return Result.Success(new WorkspaceStaffOnboardingManagerReview(null));
            }

            // The minimal list owns ongoing status. A manager may review staging
            // only before Staff owns the identity; the applicant's status DTO can
            // contain Staff/subject coordinates and is not a manager fallback.
            if (WorkspaceStaffOnboardingProfileMutationAuthority
                .HasLocalIdentityAnchorCoordinates(application))
            {
                return Result.Failure<WorkspaceStaffOnboardingManagerReview>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .ProfileReviewUnavailable);
            }

            StaffWorkspaceOnboardingIdentityAnchorOutcome outcome =
                await anchorOutcomes.ReadAsync(
                    new StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                        application.Id,
                        application.SubjectId),
                    readToken).ConfigureAwait(false);
            return WorkspaceStaffOnboardingProfileMutationAuthority
                .IsExactAbsent(application, outcome)
                    ? Result.Success(new WorkspaceStaffOnboardingManagerReview(application.ToDto()))
                    : Result.Failure<WorkspaceStaffOnboardingManagerReview>(
                        WorkspaceStaffOnboardingApplicationErrors
                            .ProfileReviewUnavailable);
        }, cancellationToken).ConfigureAwait(false);
}

internal readonly record struct WorkspaceStaffOnboardingManagerReview(
    WorkspaceStaffOnboardingDto? Application);

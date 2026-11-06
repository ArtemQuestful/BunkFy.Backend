namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Mapping;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed class GetOwnWorkspaceStaffOnboardingQueryHandler(
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    IWorkspaceStaffOnboardingSerializedReadBoundary readBoundary,
    IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader anchorOutcomes,
    WorkspaceOperationalAdmissionEvaluator operationalAdmission,
    IScopeContext scopeContext)
    : IQueryHandler<GetOwnWorkspaceStaffOnboardingQuery, WorkspaceStaffOnboardingDto>
{
    public async Task<Result<WorkspaceStaffOnboardingDto>> HandleAsync(
        GetOwnWorkspaceStaffOnboardingQuery query,
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
                return Result.Failure<WorkspaceStaffOnboardingDto>(
                    admitted.Error);
            }

            WorkspaceStaffOnboardingMutationLease lease =
                await mutations.AcquireApplicantAsync(
                    query.SourceKind.ToDomain(),
                    query.SourceId,
                    query.SubjectId,
                    WorkspaceStaffOnboardingSourceLockMode.Read,
                    requireOperational: true,
                    readToken).ConfigureAwait(false);
            if (lease.CoordinateExists && lease.Application is null)
            {
                return Result.Failure<WorkspaceStaffOnboardingDto>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .ProcessingRestricted);
            }

            WorkspaceStaffOnboarding? application = lease.Application;
            if (application is null)
            {
                return Result.Failure<WorkspaceStaffOnboardingDto>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .ApplicationNotFound);
            }

            StaffWorkspaceOnboardingIdentityAnchorOutcome outcome =
                await anchorOutcomes.ReadAsync(
                    new StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                        application.Id,
                        application.SubjectId),
                    readToken).ConfigureAwait(false);
            // Workspaces owns status even after Staff owns the identity. Keep
            // polling and recovery visible while withholding every staging
            // profile field unless exact Staff absence is proven under lock.
            return Result.Success(
                WorkspaceStaffOnboardingProfileMutationAuthority
                    .IsExactAbsent(application, outcome)
                        ? application.ToDto()
                        : application.ToStatusDto());
        }, cancellationToken).ConfigureAwait(false);
}

internal sealed class ListActionableWorkspaceStaffOnboardingQueryHandler(
    IWorkspaceStaffOnboardingRepository applications,
    WorkspaceStaffOnboardingManagerReviewReader reviews)
    : IQueryHandler<ListActionableWorkspaceStaffOnboardingQuery, WorkspaceStaffOnboardingListResponse>
{
    public async Task<Result<WorkspaceStaffOnboardingListResponse>> HandleAsync(
        ListActionableWorkspaceStaffOnboardingQuery query,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffOnboardingSummaryListResponse page =
            await applications.ListActionableAsync(
                PageRequest.Normalize(query.Page, query.PageSize),
                cancellationToken).ConfigureAwait(false);
        List<WorkspaceStaffOnboardingDto> items = new(page.Items.Count);
        foreach (WorkspaceStaffOnboardingActionableSummaryDto summary in page.Items)
        {
            // Release each serialized source/application/Staff read before the
            // next row. Never hold locks for multiple applicants at once.
            Result<WorkspaceStaffOnboardingManagerReview> review =
                await reviews.ReadLegacyActionableAsync(
                    summary.ApplicationId, cancellationToken).ConfigureAwait(false);
            if (review.IsFailure)
            {
                // Older clients must not receive an opaque approvable row or a
                // stale profile. New clients can still read minimal summaries
                // and use the receipted recovery flow for Staff-owned failures.
                return Result.Failure<WorkspaceStaffOnboardingListResponse>(
                    review.Error);
            }

            if (review.Value.Application is not null)
            {
                items.Add(review.Value.Application);
            }
        }

        return Result.Success(new WorkspaceStaffOnboardingListResponse(
            items, page.Page, page.PageSize, page.HasMore));
    }
}

internal sealed class ListActionableWorkspaceStaffOnboardingSummariesQueryHandler(
    IWorkspaceStaffOnboardingRepository applications)
    : IQueryHandler<ListActionableWorkspaceStaffOnboardingSummariesQuery, WorkspaceStaffOnboardingSummaryListResponse>
{
    public async Task<Result<WorkspaceStaffOnboardingSummaryListResponse>> HandleAsync(
        ListActionableWorkspaceStaffOnboardingSummariesQuery query,
        CancellationToken cancellationToken) => Result.Success(
        await applications.ListActionableAsync(
            PageRequest.Normalize(query.Page, query.PageSize),
            cancellationToken).ConfigureAwait(false));
}

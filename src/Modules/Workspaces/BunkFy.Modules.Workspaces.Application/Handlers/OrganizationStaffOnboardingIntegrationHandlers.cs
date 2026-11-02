namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Logging;

[IntegrationEventHandler(WorkspacesModuleMetadata.InvitationChangedHandlerName)]
internal sealed class OrganizationInvitationStaffOnboardingHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    WorkspaceStaffOnboardingProcessor processor,
    ISystemClock clock,
    ILogger<OrganizationInvitationStaffOnboardingHandler> logger)
    : IIntegrationEventHandler<OrganizationInvitationChangedIntegrationEvent>
{
    public async Task HandleAsync(
        OrganizationInvitationChangedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        if (integrationEvent.Change == OrganizationInvitationChange.Accepted &&
            !string.IsNullOrWhiteSpace(integrationEvent.AcceptedSubjectId))
        {
            WorkspaceStaffOnboardingMutationLease lease =
                await mutations.AcquireApplicantAsync(
                    WorkspaceStaffOnboardingSource.Invitation,
                    integrationEvent.InvitationId,
                    integrationEvent.AcceptedSubjectId,
                    WorkspaceStaffOnboardingSourceLockMode.Read,
                    requireOperational: false,
                    cancellationToken).ConfigureAwait(false);
            await ProcessWhenPresentAsync(
                    lease.Application,
                    processor,
                    logger,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (integrationEvent.Status is OrganizationInvitationStatus.Revoked or
            OrganizationInvitationStatus.Superseded)
        {
            await mutations.AcquireSourceAsync(
                    integrationEvent.InvitationId,
                    WorkspaceStaffOnboardingSourceLockMode.Write,
                    cancellationToken).ConfigureAwait(false);
            WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
                integrationEvent.InvitationId,
                cancellationToken).ConfigureAwait(false);
            plan?.Supersede(clock.UtcNow);
            IReadOnlyList<WorkspaceStaffOnboarding> active = await applications.ListActiveBySourceAsync(
                WorkspaceStaffOnboardingSource.Invitation,
                integrationEvent.InvitationId,
                cancellationToken).ConfigureAwait(false);
            foreach (WorkspaceStaffOnboarding application in active)
            {
                application.Supersede(clock.UtcNow);
            }
        }
    }

    private static async Task ProcessWhenPresentAsync(
        WorkspaceStaffOnboarding? application,
        WorkspaceStaffOnboardingProcessor processor,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (application is null)
        {
            logger.LogWarning("An accepted organization invitation had no BunkFy Staff onboarding application.");
            return;
        }

        Result result = await processor
            .ProcessAcquiredInvitationAcceptanceAsync(
                application,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            logger.LogWarning(
                "Staff onboarding remains recoverable after {ErrorCode}.",
                result.Error.Code);
        }
    }
}

[IntegrationEventHandler(WorkspacesModuleMetadata.EnrollmentClaimChangedHandlerName)]
internal sealed class OrganizationEnrollmentClaimStaffOnboardingHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    WorkspaceStaffOnboardingProcessor processor,
    ISystemClock clock,
    ILogger<OrganizationEnrollmentClaimStaffOnboardingHandler> logger)
    : IIntegrationEventHandler<OrganizationEnrollmentClaimChangedIntegrationEvent>
{
    public async Task HandleAsync(
        OrganizationEnrollmentClaimChangedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffOnboardingMutationLease lease =
            await mutations.AcquireApplicantAsync(
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                integrationEvent.EnrollmentLinkId,
                integrationEvent.SubjectId,
                WorkspaceStaffOnboardingSourceLockMode.Write,
                requireOperational: false,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffOnboarding? application = lease.Application;
        if (application is null)
        {
            WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
                integrationEvent.EnrollmentLinkId,
                cancellationToken).ConfigureAwait(false);
            WorkspaceStaffDeferredClaimWithdrawal? deferred =
                await deferredWithdrawals.GetAsync(
                    integrationEvent.ClaimId,
                    cancellationToken).ConfigureAwait(false);
            if (plan is not null)
            {
                OrganizationEnrollmentClaimExpiredStaffOnboardingHandler
                    .EnsurePlanMatches(
                        plan,
                        integrationEvent.ScopeId,
                        integrationEvent.OrganizationId,
                        integrationEvent.EnrollmentLinkId,
                        "changed organization enrollment claim");
            }

            if (plan is not null || deferred is not null)
            {
                throw new InvalidOperationException(
                    "A product-owned organization enrollment claim had no BunkFy Staff onboarding application.");
            }

            logger.LogWarning("An organization enrollment claim had no BunkFy Staff onboarding application.");
            return;
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (integrationEvent.Change == OrganizationEnrollmentClaimChange.Requested)
        {
            Result requested = application.ObserveClaimRequested(
                integrationEvent.ClaimId,
                integrationEvent.ClaimVersion,
                nowUtc);
            EnsureObserved(requested, "claim request");

            if (await this.ObserveDeferredWithdrawalAsync(
                    application,
                    integrationEvent,
                    nowUtc,
                    cancellationToken).ConfigureAwait(false))
            {
                await this.FinalizePlanAsync(
                    application.SourceId,
                    nowUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (await this.ObserveDeferredWithdrawalAsync(
                application,
                integrationEvent,
                nowUtc,
                cancellationToken).ConfigureAwait(false))
        {
            await this.FinalizePlanAsync(
                application.SourceId,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (integrationEvent.Change == OrganizationEnrollmentClaimChange.Rejected)
        {
            Result rejected = application.ObserveClaimRejected(
                integrationEvent.ClaimId,
                integrationEvent.ClaimVersion,
                nowUtc);
            EnsureObserved(rejected, "claim rejection");
            await OrganizationEnrollmentClaimExpiredStaffOnboardingHandler
                .ExpirePlanWhenUnusedUnderSourceLockAsync(
                    applications,
                    plans,
                    deferredWithdrawals,
                    application.SourceId,
                    nowUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (integrationEvent.Change == OrganizationEnrollmentClaimChange.Accepted)
        {
            Result processed = await processor
                .ProcessAcquiredEnrollmentClaimAcceptanceAsync(
                    application,
                    integrationEvent.ClaimId,
                    integrationEvent.ClaimVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            if (processed.IsFailure)
            {
                logger.LogWarning(
                    "Staff onboarding remains recoverable after {ErrorCode}.",
                    processed.Error.Code);
            }

            await OrganizationEnrollmentClaimExpiredStaffOnboardingHandler
                .ExpirePlanWhenUnusedUnderSourceLockAsync(
                    applications,
                    plans,
                    deferredWithdrawals,
                    application.SourceId,
                    nowUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void EnsureObserved(Result result, string observation)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Staff onboarding could not observe {observation}: '{result.Error.Code}'.");
        }
    }

    private async Task<bool> ObserveDeferredWithdrawalAsync(
        WorkspaceStaffOnboarding application,
        OrganizationEnrollmentClaimChangedIntegrationEvent integrationEvent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffDeferredClaimWithdrawal? deferred =
            await deferredWithdrawals.GetAsync(
                integrationEvent.ClaimId,
                cancellationToken).ConfigureAwait(false);
        if (deferred is null)
        {
            return false;
        }

        if (deferred.Id != integrationEvent.ClaimId ||
            deferred.OrganizationId != integrationEvent.OrganizationId ||
            deferred.EnrollmentLinkId != integrationEvent.EnrollmentLinkId ||
            !string.Equals(
                deferred.ScopeId,
                integrationEvent.ScopeId,
                StringComparison.Ordinal) ||
            deferred.ClaimVersion <= integrationEvent.ClaimVersion)
        {
            throw new InvalidOperationException(
                "A deferred organization enrollment claim withdrawal did not follow its changed claim coordinate.");
        }

        Result withdrawn = application.ObserveClaimWithdrawn(
            deferred.Id,
            deferred.ClaimVersion,
            nowUtc);
        EnsureObserved(withdrawn, "deferred claim withdrawal");
        deferredWithdrawals.Remove(deferred);
        return true;
    }

    private Task FinalizePlanAsync(
        Guid enrollmentLinkId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        OrganizationEnrollmentClaimExpiredStaffOnboardingHandler
            .ExpirePlanWhenUnusedUnderSourceLockAsync(
                applications,
                plans,
                deferredWithdrawals,
                enrollmentLinkId,
                nowUtc,
                cancellationToken);
}

[IntegrationEventHandler(WorkspacesModuleMetadata.EnrollmentLinkChangedHandlerName)]
internal sealed class OrganizationEnrollmentLinkStaffOnboardingHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    ISystemClock clock)
    : IIntegrationEventHandler<OrganizationEnrollmentLinkChangedIntegrationEvent>
{
    public async Task HandleAsync(
        OrganizationEnrollmentLinkChangedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        if (integrationEvent.Status is not (OrganizationEnrollmentLinkStatus.Disabled or
            OrganizationEnrollmentLinkStatus.Rotated))
        {
            return;
        }

        await mutations.AcquireSourceAsync(
                integrationEvent.EnrollmentLinkId,
                WorkspaceStaffOnboardingSourceLockMode.Write,
                cancellationToken).ConfigureAwait(false);

        IReadOnlyList<WorkspaceStaffOnboarding> active = await applications.ListActiveBySourceAsync(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            integrationEvent.EnrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
        foreach (WorkspaceStaffOnboarding application in active)
        {
            application.Supersede(clock.UtcNow);
        }

        WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
            integrationEvent.EnrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
        bool hasDeferred = await deferredWithdrawals.AnyBySourceAsync(
            integrationEvent.EnrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            if (active.Count > 0 || hasDeferred)
            {
                throw new InvalidOperationException(
                    "A terminal organization enrollment link retained BunkFy Staff onboarding state without its access plan.");
            }

            return;
        }

        OrganizationEnrollmentClaimExpiredStaffOnboardingHandler.EnsurePlanMatches(
            plan,
            integrationEvent.ScopeId,
            integrationEvent.OrganizationId,
            integrationEvent.EnrollmentLinkId,
            "terminal organization enrollment link");
        plan.Supersede(clock.UtcNow);
        await deferredWithdrawals.RemoveBySourceAsync(
            integrationEvent.EnrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
    }
}

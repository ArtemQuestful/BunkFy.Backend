namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Modules.Organizations.Contracts;

[IntegrationEventHandler(WorkspacesModuleMetadata.InvitationExpiredHandlerName)]
internal sealed class OrganizationInvitationExpiredStaffOnboardingHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    ISystemClock clock)
    : IIntegrationEventHandler<OrganizationInvitationExpiredIntegrationEvent>
{
    public async Task HandleAsync(
        OrganizationInvitationExpiredIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = clock.UtcNow;
        await mutations.AcquireSourceAsync(
                integrationEvent.InvitationId,
                WorkspaceStaffOnboardingSourceLockMode.Write,
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WorkspaceStaffOnboarding> active = await applications.ListActiveBySourceAsync(
            WorkspaceStaffOnboardingSource.Invitation,
            integrationEvent.InvitationId,
            cancellationToken).ConfigureAwait(false);
        foreach (WorkspaceStaffOnboarding application in active)
        {
            EnsureObserved(application.Expire(nowUtc), "invitation expiry");
        }

        WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
            integrationEvent.InvitationId,
            cancellationToken).ConfigureAwait(false);
        EnsureObserved(
            plan?.ObserveSourceExpired(integrationEvent.ExpiresAtUtc, nowUtc) ??
                Result.Success(),
            "invitation access-plan source expiry");
        EnsureObserved(plan?.Expire(nowUtc) ?? Result.Success(), "invitation access-plan expiry");
    }

    private static void EnsureObserved(Result result, string observation)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Staff onboarding could not observe {observation}: '{result.Error.Code}'.");
        }
    }
}

[IntegrationEventHandler(WorkspacesModuleMetadata.EnrollmentClaimExpiredHandlerName)]
internal sealed class OrganizationEnrollmentClaimExpiredStaffOnboardingHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    ISystemClock clock)
    : IIntegrationEventHandler<OrganizationEnrollmentClaimExpiredIntegrationEvent>
{
    public async Task HandleAsync(
        OrganizationEnrollmentClaimExpiredIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        await mutations.AcquireSourceAsync(
                integrationEvent.EnrollmentLinkId,
                WorkspaceStaffOnboardingSourceLockMode.Write,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffOnboarding? application = await applications.GetByClaimAsync(
            integrationEvent.ClaimId,
            cancellationToken).ConfigureAwait(false);
        if (application is null)
        {
            IReadOnlyList<WorkspaceStaffOnboarding> active =
                await applications.ListActiveBySourceAsync(
                    WorkspaceStaffOnboardingSource.EnrollmentLink,
                    integrationEvent.EnrollmentLinkId,
                    cancellationToken).ConfigureAwait(false);
            WorkspaceStaffAccessPlan? unboundPlan = await plans.GetAsync(
                integrationEvent.EnrollmentLinkId,
                cancellationToken).ConfigureAwait(false);
            bool hasDeferred = await deferredWithdrawals.AnyBySourceAsync(
                integrationEvent.EnrollmentLinkId,
                cancellationToken).ConfigureAwait(false);
            if (unboundPlan is null && active.Count == 0 && !hasDeferred)
            {
                return;
            }

            throw new InvalidOperationException(
                "An expired product-owned organization enrollment claim had no BunkFy Staff onboarding application.");
        }

        if (!await mutations.AcquireTrackedUnderSourceAsync(
                application,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "An expired organization enrollment claim lost its BunkFy Staff onboarding application.");
        }

        if (application.SourceKind != WorkspaceStaffOnboardingSource.EnrollmentLink ||
            application.SourceId != integrationEvent.EnrollmentLinkId ||
            !string.Equals(
                application.ScopeId,
                integrationEvent.ScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "An expired organization enrollment claim did not match its BunkFy Staff onboarding source.");
        }

        WorkspaceStaffAccessPlan? applicationPlan = await plans.GetAsync(
            integrationEvent.EnrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
        EnsurePlanMatches(
            applicationPlan,
            integrationEvent.ScopeId,
            integrationEvent.OrganizationId,
            integrationEvent.EnrollmentLinkId,
            "expired organization enrollment claim");
        if (await deferredWithdrawals.GetAsync(
                integrationEvent.ClaimId,
                cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(
                "An expired organization enrollment claim conflicted with a durable withdrawal observation.");
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result expired = application.ObserveClaimExpired(
            integrationEvent.ClaimId,
            integrationEvent.ClaimVersion,
            nowUtc);
        EnsureObserved(expired, "claim expiry");

        await ExpirePlanWhenUnusedUnderSourceLockAsync(
            applications,
            plans,
            deferredWithdrawals,
            application.SourceId,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureObserved(Result result, string observation)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Staff onboarding could not observe {observation}: '{result.Error.Code}'.");
        }
    }

    internal static async Task ExpirePlanWhenUnusedUnderSourceLockAsync(
        IWorkspaceStaffOnboardingRepository applications,
        IWorkspaceStaffAccessPlanRepository plans,
        IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
        Guid enrollmentLinkId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkspaceStaffOnboarding> active = await applications.ListActiveBySourceAsync(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            enrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
        if (active.Any(application => application.IsActive))
        {
            return;
        }

        WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
            enrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
        if (plan?.SourceExpiredAtUtc is null)
        {
            return;
        }

        EnsureObserved(plan.Expire(nowUtc), "enrollment access-plan expiry");
        await deferredWithdrawals.RemoveBySourceAsync(
            enrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
    }

    internal static void EnsurePlanMatches(
        WorkspaceStaffAccessPlan? plan,
        string scopeId,
        Guid organizationId,
        Guid enrollmentLinkId,
        string observation)
    {
        if (plan is null ||
            plan.Id != enrollmentLinkId ||
            plan.SourceKind != WorkspaceStaffOnboardingSource.EnrollmentLink ||
            !string.Equals(plan.ScopeId, scopeId, StringComparison.Ordinal) ||
            !Guid.TryParse(scopeId, out Guid scopedOrganizationId) ||
            scopedOrganizationId != organizationId)
        {
            throw new InvalidOperationException(
                $"A product-owned {observation} did not match its BunkFy Staff access plan.");
        }
    }
}

[IntegrationEventHandler(WorkspacesModuleMetadata.EnrollmentLinkExpiredHandlerName)]
internal sealed class OrganizationEnrollmentLinkExpiredStaffOnboardingHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    ISystemClock clock)
    : IIntegrationEventHandler<OrganizationEnrollmentLinkExpiredIntegrationEvent>
{
    public async Task HandleAsync(
        OrganizationEnrollmentLinkExpiredIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = clock.UtcNow;
        await mutations.AcquireSourceAsync(
                integrationEvent.EnrollmentLinkId,
                WorkspaceStaffOnboardingSourceLockMode.Write,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
            integrationEvent.EnrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            IReadOnlyList<WorkspaceStaffOnboarding> active =
                await applications.ListActiveBySourceAsync(
                    WorkspaceStaffOnboardingSource.EnrollmentLink,
                    integrationEvent.EnrollmentLinkId,
                    cancellationToken).ConfigureAwait(false);
            bool hasDeferred = await deferredWithdrawals.AnyBySourceAsync(
                integrationEvent.EnrollmentLinkId,
                cancellationToken).ConfigureAwait(false);
            if (active.Count > 0 || hasDeferred)
            {
                throw new InvalidOperationException(
                    "An expired organization enrollment link retained BunkFy Staff onboarding state without its access plan.");
            }

            return;
        }

        OrganizationEnrollmentClaimExpiredStaffOnboardingHandler.EnsurePlanMatches(
            plan,
            integrationEvent.ScopeId,
            integrationEvent.OrganizationId,
            integrationEvent.EnrollmentLinkId,
            "expired organization enrollment link");
        EnsureObserved(
            plan.ObserveSourceExpired(integrationEvent.ExpiresAtUtc, nowUtc),
            "enrollment access-plan source expiry");

        await OrganizationEnrollmentClaimExpiredStaffOnboardingHandler
            .ExpirePlanWhenUnusedUnderSourceLockAsync(
            applications,
            plans,
            deferredWithdrawals,
            integrationEvent.EnrollmentLinkId,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureObserved(Result result, string observation)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Staff onboarding could not observe {observation}: '{result.Error.Code}'.");
        }
    }
}

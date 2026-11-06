namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Modules.Organizations.Contracts;

[IntegrationEventHandler(WorkspacesModuleMetadata.EnrollmentClaimWithdrawnHandlerName)]
internal sealed class OrganizationEnrollmentClaimWithdrawnStaffOnboardingHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    ISystemClock clock)
    : IIntegrationEventHandler<OrganizationEnrollmentClaimWithdrawnIntegrationEvent>
{
    public async Task HandleAsync(
        OrganizationEnrollmentClaimWithdrawnIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        await mutations.AcquireSourceAsync(
                integrationEvent.EnrollmentLinkId,
                WorkspaceStaffOnboardingSourceLockMode.Write,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffOnboarding? application = await applications.GetByClaimAsync(
            integrationEvent.ClaimId,
            cancellationToken).ConfigureAwait(false);
        WorkspaceStaffDeferredClaimWithdrawal? deferred =
            await deferredWithdrawals.GetAsync(
                integrationEvent.ClaimId,
                cancellationToken).ConfigureAwait(false);
        if (application is null)
        {
            IReadOnlyList<WorkspaceStaffOnboarding> active =
                await applications.ListActiveBySourceAsync(
                    WorkspaceStaffOnboardingSource.EnrollmentLink,
                    integrationEvent.EnrollmentLinkId,
                    cancellationToken).ConfigureAwait(false);
            WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
                integrationEvent.EnrollmentLinkId,
                cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                if (active.Count > 0 || deferred is not null)
                {
                    throw new InvalidOperationException(
                        "A product-owned organization enrollment claim withdrawal lost its BunkFy Staff access plan.");
                }

                return;
            }

            EnsurePlanMatches(plan, integrationEvent);
            if (plan.Status != WorkspaceStaffAccessPlanState.Active)
            {
                if (active.Count > 0)
                {
                    throw new InvalidOperationException(
                        "A terminal BunkFy Staff access plan retained an active onboarding application.");
                }

                if (deferred is not null)
                {
                    EnsureDeferredMatches(deferred, integrationEvent);
                    deferredWithdrawals.Remove(deferred);
                }

                return;
            }

            if (deferred is not null)
            {
                EnsureDeferredMatches(deferred, integrationEvent);
                return;
            }

            Result<WorkspaceStaffDeferredClaimWithdrawal> created =
                WorkspaceStaffDeferredClaimWithdrawal.Create(
                    integrationEvent.ScopeId,
                    integrationEvent.OrganizationId,
                    integrationEvent.EnrollmentLinkId,
                    integrationEvent.ClaimId,
                    integrationEvent.ClaimVersion,
                    integrationEvent.EventId,
                    integrationEvent.OccurredAtUtc);
            if (created.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Staff onboarding could not defer claim withdrawal: '{created.Error.Code}'.");
            }

            await deferredWithdrawals.AddAsync(
                created.Value,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await mutations.AcquireTrackedUnderSourceAsync(
                application,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "A withdrawn organization enrollment claim lost its BunkFy Staff onboarding application.");
        }

        if (application.SourceKind != WorkspaceStaffOnboardingSource.EnrollmentLink ||
            application.SourceId != integrationEvent.EnrollmentLinkId ||
            !string.Equals(
                application.ScopeId,
                integrationEvent.ScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A withdrawn organization enrollment claim did not match its BunkFy Staff onboarding source.");
        }

        WorkspaceStaffAccessPlan? applicationPlan = await plans.GetAsync(
            integrationEvent.EnrollmentLinkId,
            cancellationToken).ConfigureAwait(false);
        if (applicationPlan is null)
        {
            throw new InvalidOperationException(
                "A withdrawn organization enrollment claim matched a BunkFy Staff onboarding application without its access plan.");
        }

        EnsurePlanMatches(applicationPlan, integrationEvent);
        if (deferred is not null)
        {
            EnsureDeferredMatches(deferred, integrationEvent);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result withdrawn = application.ObserveClaimWithdrawn(
            integrationEvent.ClaimId,
            integrationEvent.ClaimVersion,
            nowUtc);
        if (withdrawn.IsFailure)
        {
            throw new InvalidOperationException(
                $"Staff onboarding could not observe claim withdrawal: '{withdrawn.Error.Code}'.");
        }

        if (deferred is not null)
        {
            deferredWithdrawals.Remove(deferred);
        }

        await OrganizationEnrollmentClaimExpiredStaffOnboardingHandler
            .ExpirePlanWhenUnusedUnderSourceLockAsync(
                applications,
                plans,
                deferredWithdrawals,
                application.SourceId,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
    }

    private static void EnsurePlanMatches(
        WorkspaceStaffAccessPlan plan,
        OrganizationEnrollmentClaimWithdrawnIntegrationEvent integrationEvent)
    {
        if (plan.Id != integrationEvent.EnrollmentLinkId ||
            plan.SourceKind != WorkspaceStaffOnboardingSource.EnrollmentLink ||
            !string.Equals(
                plan.ScopeId,
                integrationEvent.ScopeId,
                StringComparison.Ordinal) ||
            !Guid.TryParse(integrationEvent.ScopeId, out Guid organizationId) ||
            organizationId != integrationEvent.OrganizationId)
        {
            throw new InvalidOperationException(
                "A withdrawn organization enrollment claim did not match its BunkFy Staff access plan.");
        }
    }

    private static void EnsureDeferredMatches(
        WorkspaceStaffDeferredClaimWithdrawal deferred,
        OrganizationEnrollmentClaimWithdrawnIntegrationEvent integrationEvent)
    {
        if (!deferred.Matches(
                integrationEvent.ScopeId,
                integrationEvent.OrganizationId,
                integrationEvent.EnrollmentLinkId,
                integrationEvent.ClaimId,
                integrationEvent.ClaimVersion,
                integrationEvent.EventId,
                integrationEvent.OccurredAtUtc))
        {
            throw new InvalidOperationException(
                "A duplicate organization enrollment claim withdrawal conflicted with its durable BunkFy observation.");
        }
    }
}

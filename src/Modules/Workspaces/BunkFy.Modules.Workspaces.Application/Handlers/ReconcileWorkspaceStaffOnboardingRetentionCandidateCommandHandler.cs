namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Options;

internal sealed class ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    WorkspaceStaffOnboardingRetentionExecutionCoordinator retentionExecutions,
    IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    IOrganizationEnrollmentClaimInspector claims,
    WorkspaceStaffOnboardingProcessor processor,
    IOptions<WorkspaceStaffOnboardingRetentionOptions> options,
    ISystemClock clock)
    : ICommandHandler<
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand,
        WorkspaceStaffOnboardingRetentionReconciliation>
{
    public async Task<Result<WorkspaceStaffOnboardingRetentionReconciliation>> HandleAsync(
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand command,
        CancellationToken cancellationToken)
    {
        Result<WorkspaceStaffOnboardingRetentionExecution> execution =
            await retentionExecutions.AcquireRunningAsync(
                command.ExecutionId,
                command.Attempt,
                cancellationToken).ConfigureAwait(false);
        if (execution.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingRetentionReconciliation>(
                    execution.Error);
        }

        Result<WorkspaceStaffOnboardingRetentionReconciliation> reconciled =
            await this.ReconcileAsync(command, cancellationToken)
                .ConfigureAwait(false);
        if (reconciled.IsFailure)
        {
            return reconciled;
        }

        Result recorded = execution.Value.RecordCandidate(
            command.Attempt,
            reconciled.Value.Affected);
        return recorded.IsSuccess
            ? reconciled
            : Result.Failure<
                WorkspaceStaffOnboardingRetentionReconciliation>(
                    recorded.Error);
    }

    private async Task<Result<WorkspaceStaffOnboardingRetentionReconciliation>>
        ReconcileAsync(
            ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand command,
            CancellationToken cancellationToken)
    {
        WorkspaceStaffOnboardingMutationLease lease =
            await mutations.AcquireExistingAsync(
                command.ApplicationId,
                WorkspaceStaffOnboardingSourceLockMode.Write,
                requireOperational: false,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffOnboarding? application = lease.Application;
        if (application is null ||
            application.Version != command.ExpectedVersion ||
            application.SourceKind != WorkspaceStaffOnboardingSource.EnrollmentLink ||
            !IsEligibleState(application))
        {
            return Unchanged();
        }

        WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
            application.SourceId,
            cancellationToken).ConfigureAwait(false);
        if (plan is null ||
            plan.SourceKind != WorkspaceStaffOnboardingSource.EnrollmentLink ||
            !string.Equals(
                plan.ScopeId,
                application.ScopeId,
                StringComparison.Ordinal))
        {
            return Result.Failure<WorkspaceStaffOnboardingRetentionReconciliation>(
                WorkspaceStaffOnboardingApplicationErrors.RetentionPlanInconsistent);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        DateTimeOffset? sourceExpiredAtUtc = plan.SourceExpiredAtUtc;
        WorkspaceStaffOnboardingRetentionOptions settings = options.Value;
        if (!sourceExpiredAtUtc.HasValue ||
            sourceExpiredAtUtc.Value > nowUtc - settings.GracePeriod)
        {
            return Unchanged();
        }

        if (!Guid.TryParse(application.ScopeId, out Guid organizationId))
        {
            return Result.Failure<WorkspaceStaffOnboardingRetentionReconciliation>(
                WorkspaceStaffOnboardingApplicationErrors.RetentionCoordinateInvalid);
        }

        OrganizationEnrollmentClaimDto? claim = await claims.FindAsync(
            organizationId,
            application.SourceId,
            application.SubjectId,
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            if (await deferredWithdrawals.AnyBySourceAsync(
                    application.SourceId,
                    cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure<WorkspaceStaffOnboardingRetentionReconciliation>(
                    WorkspaceStaffOnboardingApplicationErrors.RetentionClaimInconsistent);
            }

            if (sourceExpiredAtUtc.Value <= nowUtc - settings.AuthorityWindow)
            {
                return Success(
                    WorkspaceStaffOnboardingRetentionOutcome.AuthorityLapsed,
                    affected: false);
            }

            Result expired = application.Expire(nowUtc);
            if (expired.IsFailure)
            {
                return Failure(expired);
            }

            await this.FinalizePlanAsync(application.SourceId, nowUtc, cancellationToken)
                .ConfigureAwait(false);
            return Success(
                WorkspaceStaffOnboardingRetentionOutcome.Expired,
                affected: true);
        }

        if (!IsConsistent(claim, application, organizationId))
        {
            return Result.Failure<WorkspaceStaffOnboardingRetentionReconciliation>(
                WorkspaceStaffOnboardingApplicationErrors.RetentionClaimInconsistent);
        }

        if (plan.Status != WorkspaceStaffAccessPlanState.Active)
        {
            return Result.Failure<WorkspaceStaffOnboardingRetentionReconciliation>(
                WorkspaceStaffOnboardingApplicationErrors.RetentionPlanInconsistent);
        }

        WorkspaceStaffDeferredClaimWithdrawal? deferred =
            await deferredWithdrawals.GetAsync(
                claim.ClaimId,
                cancellationToken).ConfigureAwait(false);
        if (deferred is not null &&
            claim.Status != OrganizationEnrollmentClaimStatus.Withdrawn)
        {
            return Result.Failure<WorkspaceStaffOnboardingRetentionReconciliation>(
                WorkspaceStaffOnboardingApplicationErrors.RetentionClaimInconsistent);
        }

        return claim.Status switch
        {
            OrganizationEnrollmentClaimStatus.Pending =>
                ObservePending(application, claim, nowUtc),
            OrganizationEnrollmentClaimStatus.Rejected =>
                await this.ObserveRejectedAsync(
                    application, claim, nowUtc, cancellationToken).ConfigureAwait(false),
            OrganizationEnrollmentClaimStatus.Expired =>
                await this.ObserveExpiredAsync(
                    application, claim, nowUtc, cancellationToken).ConfigureAwait(false),
            OrganizationEnrollmentClaimStatus.Withdrawn =>
                await this.ObserveWithdrawnAsync(
                    application,
                    claim,
                    deferred,
                    nowUtc,
                    cancellationToken).ConfigureAwait(false),
            OrganizationEnrollmentClaimStatus.Accepted =>
                await this.ObserveAcceptedAsync(
                    application, claim, nowUtc, cancellationToken).ConfigureAwait(false),
            _ => Result.Failure<WorkspaceStaffOnboardingRetentionReconciliation>(
                WorkspaceStaffOnboardingApplicationErrors.RetentionClaimInconsistent)
        };
    }

    private static Result<WorkspaceStaffOnboardingRetentionReconciliation> ObservePending(
        WorkspaceStaffOnboarding application,
        OrganizationEnrollmentClaimDto claim,
        DateTimeOffset nowUtc)
    {
        long versionBefore = application.Version;
        Result observed = application.ObserveClaimRequested(
            claim.ClaimId,
            claim.Version,
            nowUtc);
        return observed.IsFailure
            ? Failure(observed)
            : Success(
                WorkspaceStaffOnboardingRetentionOutcome.ClaimPending,
                affected: application.Version != versionBefore);
    }

    private async Task<Result<WorkspaceStaffOnboardingRetentionReconciliation>> ObserveRejectedAsync(
        WorkspaceStaffOnboarding application,
        OrganizationEnrollmentClaimDto claim,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        Result observed = application.ObserveClaimRejected(
            claim.ClaimId,
            claim.Version,
            nowUtc);
        if (observed.IsFailure)
        {
            return Failure(observed);
        }

        await this.FinalizePlanAsync(application.SourceId, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        return Success(
            WorkspaceStaffOnboardingRetentionOutcome.ClaimRejected,
            affected: true);
    }

    private async Task<Result<WorkspaceStaffOnboardingRetentionReconciliation>> ObserveExpiredAsync(
        WorkspaceStaffOnboarding application,
        OrganizationEnrollmentClaimDto claim,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        Result observed = application.ObserveClaimExpired(
            claim.ClaimId,
            claim.Version,
            nowUtc);
        if (observed.IsFailure)
        {
            return Failure(observed);
        }

        await this.FinalizePlanAsync(application.SourceId, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        return Success(
            WorkspaceStaffOnboardingRetentionOutcome.ClaimExpired,
            affected: true);
    }

    private async Task<Result<WorkspaceStaffOnboardingRetentionReconciliation>> ObserveAcceptedAsync(
        WorkspaceStaffOnboarding application,
        OrganizationEnrollmentClaimDto claim,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        Result observed = application.ObserveClaimAccepted(
            claim.ClaimId,
            claim.Version,
            nowUtc);
        if (observed.IsFailure)
        {
            return Failure(observed);
        }

        Result processed = await processor.ProcessAcquiredAsync(application, cancellationToken)
            .ConfigureAwait(false);
        await this.FinalizePlanAsync(application.SourceId, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        return Success(
            processed.IsSuccess
                ? WorkspaceStaffOnboardingRetentionOutcome.ClaimAccepted
                : WorkspaceStaffOnboardingRetentionOutcome
                    .ClaimAcceptedRecoveryRequired,
            affected: true);
    }

    private async Task<Result<WorkspaceStaffOnboardingRetentionReconciliation>> ObserveWithdrawnAsync(
        WorkspaceStaffOnboarding application,
        OrganizationEnrollmentClaimDto claim,
        WorkspaceStaffDeferredClaimWithdrawal? deferred,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (deferred is not null)
        {
            if (deferred.Id != claim.ClaimId ||
                deferred.ClaimVersion != claim.Version ||
                deferred.OrganizationId != claim.OrganizationId ||
                deferred.EnrollmentLinkId != claim.EnrollmentLinkId ||
                !string.Equals(
                    deferred.ScopeId,
                    application.ScopeId,
                    StringComparison.Ordinal))
            {
                return Result.Failure<WorkspaceStaffOnboardingRetentionReconciliation>(
                    WorkspaceStaffOnboardingApplicationErrors.RetentionClaimInconsistent);
            }
        }

        Result observed = application.ObserveClaimWithdrawn(
            claim.ClaimId,
            claim.Version,
            nowUtc);
        if (observed.IsFailure)
        {
            return Failure(observed);
        }

        if (deferred is not null)
        {
            deferredWithdrawals.Remove(deferred);
        }

        await this.FinalizePlanAsync(application.SourceId, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        return Success(
            WorkspaceStaffOnboardingRetentionOutcome.ClaimWithdrawn,
            affected: true);
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

    private static bool IsConsistent(
        OrganizationEnrollmentClaimDto claim,
        WorkspaceStaffOnboarding application,
        Guid organizationId) =>
        claim.ClaimId != Guid.Empty &&
        claim.Version > 0 &&
        claim.OrganizationId == organizationId &&
        claim.EnrollmentLinkId == application.SourceId &&
        (!application.ClaimId.HasValue ||
            (application.ClaimId.Value == claim.ClaimId &&
             application.ClaimVersion.HasValue &&
             claim.Version >= application.ClaimVersion.Value)) &&
        string.Equals(
            claim.SubjectId,
            application.SubjectId,
            StringComparison.Ordinal);

    private static bool IsEligibleState(
        WorkspaceStaffOnboarding application) =>
        (application.Status == WorkspaceStaffOnboardingState.Submitted &&
         !application.ClaimId.HasValue &&
         !application.ClaimVersion.HasValue) ||
        (application.Status == WorkspaceStaffOnboardingState.PendingApproval &&
         application.ClaimId.HasValue &&
         application.ClaimVersion.HasValue);

    private static Result<WorkspaceStaffOnboardingRetentionReconciliation> Unchanged() =>
        Success(WorkspaceStaffOnboardingRetentionOutcome.Unchanged, affected: false);

    private static Result<WorkspaceStaffOnboardingRetentionReconciliation> Success(
        WorkspaceStaffOnboardingRetentionOutcome outcome,
        bool affected) =>
        Result.Success(new WorkspaceStaffOnboardingRetentionReconciliation(
            outcome,
            affected));

    private static Result<WorkspaceStaffOnboardingRetentionReconciliation> Failure(
        Result result) =>
        Result.Failure<WorkspaceStaffOnboardingRetentionReconciliation>(result.Error);
}

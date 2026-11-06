namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Logging;

internal sealed class WorkspaceStaffOnboardingProcessor(
    IStaffOnboardingProvisioner staff,
    IStaffPropertyAssignmentProvisioner staffProperties,
    IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
        restrictionProjections,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    WorkspaceStaffOnboardingIdentityAnchorConvergence anchorConvergence,
    IWorkspaceStaffAccessPlanRepository plans,
    WorkspaceStaffAccessPlanPolicy planPolicy,
    WorkspaceAccessProvisioner access,
    WorkspaceOperationalAdmissionEvaluator operationalAdmission,
    WorkspaceOrganizationMembershipObserver membershipObserver,
    ISystemClock clock,
    IIdGenerator ids,
    ILogger<WorkspaceStaffOnboardingProcessor> logger)
{
    private const string AccessProvisioningFailureCode =
        "Workspaces.AccessProvisioningFailed";
    private const string MembershipStateConflictCode =
        "Workspaces.StaffOnboardingMembershipStateConflict";

    // A captured partial-provisioning failure belongs to receipted owner
    // recovery. Admission/lifecycle preconditions and infrastructure faults
    // remain retryable without acknowledging an unsuccessful continuation.
    internal static bool RequiresOwnerRecovery(
        WorkspaceStaffOnboarding application,
        Result processed) =>
        processed.IsFailure &&
        application.Status == WorkspaceStaffOnboardingState.Failed &&
        application.StaffMemberId.HasValue &&
        (processed.Error.Code == WorkspaceStaffOnboardingApplicationErrors
            .ProvisioningFailed.Code ||
         processed.Error.Code == WorkspaceStaffOnboardingApplicationErrors
            .RecoveryAttemptRequired.Code);

    internal WorkspaceStaffOnboardingProcessor(
        IStaffOnboardingProvisioner staff,
        IStaffPropertyAssignmentProvisioner staffProperties,
        IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
            restrictionProjections,
        WorkspaceStaffOnboardingMutationCoordinator mutations,
        WorkspaceStaffOnboardingIdentityAnchorConvergence anchorConvergence,
        IWorkspaceStaffAccessPlanRepository plans,
        WorkspaceStaffAccessPlanPolicy planPolicy,
        WorkspaceAccessProvisioner access,
        WorkspaceOperationalAdmissionEvaluator operationalAdmission,
        ISystemClock clock,
        IIdGenerator ids,
        ILogger<WorkspaceStaffOnboardingProcessor> logger)
        : this(
            staff,
            staffProperties,
            restrictionProjections,
            mutations,
            anchorConvergence,
            plans,
            planPolicy,
            access,
            operationalAdmission,
            new WorkspaceOrganizationMembershipObserver([], [], clock),
            clock,
            ids,
            logger)
    {
    }

    public Task<Result> ProcessAsync(
        WorkspaceStaffOnboarding application,
        CancellationToken cancellationToken) =>
        this.ProcessAsync(
            application,
            prepare: null,
            WorkspaceStaffOnboardingSourceLockMode.Read,
            cancellationToken);

    public Task<Result> ProcessForSourceFinalizationAsync(
        WorkspaceStaffOnboarding application,
        CancellationToken cancellationToken) =>
        this.ProcessAsync(
            application,
            prepare: null,
            WorkspaceStaffOnboardingSourceLockMode.Write,
            cancellationToken);

    public Task<Result> ProcessIdentityAnchorContinuationAsync(
        WorkspaceStaffOnboarding application,
        Guid expectedStaffMemberId,
        CancellationToken cancellationToken) =>
        this.ProcessAsync(
            application,
            prepare: null,
            WorkspaceStaffOnboardingSourceLockMode.Write,
            cancellationToken,
            expectedStaffMemberId);

    public Task<Result> ProcessAnchorCreatedAsync(
        WorkspaceStaffOnboarding application,
        Guid expectedStaffMemberId,
        Guid expectedResolutionEventId,
        CancellationToken cancellationToken) =>
        this.ProcessAsync(
            application,
            prepare: null,
            WorkspaceStaffOnboardingSourceLockMode.Read,
            cancellationToken,
            expectedStaffMemberId,
            expectedResolutionEventId);

    public Task<Result> ProcessInvitationAcceptanceAsync(
        WorkspaceStaffOnboarding application,
        CancellationToken cancellationToken) =>
        this.ProcessAsync(
            application,
            candidate => candidate.ObserveInvitationAccepted(clock.UtcNow),
            WorkspaceStaffOnboardingSourceLockMode.Read,
            cancellationToken);

    public Task<Result> ProcessEnrollmentClaimAcceptanceAsync(
        WorkspaceStaffOnboarding application,
        Guid claimId,
        long claimVersion,
        CancellationToken cancellationToken) =>
        this.ProcessAsync(
            application,
            candidate => candidate.ObserveClaimAccepted(
                claimId,
                claimVersion,
                clock.UtcNow),
            WorkspaceStaffOnboardingSourceLockMode.Write,
            cancellationToken);

    public Task<Result> ProcessAcquiredInvitationAcceptanceAsync(
        WorkspaceStaffOnboarding application,
        CancellationToken cancellationToken) =>
        this.ProcessAcquiredAsync(
            application,
            candidate => candidate.ObserveInvitationAccepted(clock.UtcNow),
            cancellationToken);

    public Task<Result> ProcessAcquiredEnrollmentClaimAcceptanceAsync(
        WorkspaceStaffOnboarding application,
        Guid claimId,
        long claimVersion,
        CancellationToken cancellationToken) =>
        this.ProcessAcquiredAsync(
            application,
            candidate => candidate.ObserveClaimAccepted(
                claimId,
                claimVersion,
                clock.UtcNow),
            cancellationToken);

    public Task<Result> ProcessAcquiredAsync(
        WorkspaceStaffOnboarding application,
        CancellationToken cancellationToken) =>
        this.ProcessAcquiredAsync(application, prepare: null, cancellationToken);

    private async Task<Result> ProcessAsync(
        WorkspaceStaffOnboarding application,
        Func<WorkspaceStaffOnboarding, Result>? prepare,
        WorkspaceStaffOnboardingSourceLockMode sourceLockMode,
        CancellationToken cancellationToken,
        Guid? expectedStaffMemberId = null,
        Guid? expectedResolutionEventId = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!await mutations.AcquireTrackedAsync(
                application,
                sourceLockMode,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .ApplicationNotFound);
        }

        return await this.ProcessAcquiredAsync(
                application,
                prepare,
                cancellationToken,
                expectedStaffMemberId,
                expectedResolutionEventId).ConfigureAwait(false);
    }

    private async Task<Result> ProcessAcquiredAsync(
        WorkspaceStaffOnboarding application,
        Func<WorkspaceStaffOnboarding, Result>? prepare,
        CancellationToken cancellationToken,
        Guid? expectedStaffMemberId = null,
        Guid? expectedResolutionEventId = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (prepare is not null)
        {
            Result prepared = prepare(application);
            if (prepared.IsFailure)
            {
                return prepared;
            }
        }

        Result<WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>
            anchor = await anchorConvergence.ConvergeAcquiredAsync(
                application,
                cancellationToken,
                expectedStaffMemberId,
                expectedResolutionEventId).ConfigureAwait(false);
        if (anchor.IsFailure)
        {
            return Result.Failure(anchor.Error);
        }

        if (anchor.Value.Outcome is
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ConvergedNow or
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending or
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionObserved)
        {
            return Result.Success();
        }

        if (application.Status == WorkspaceStaffOnboardingState.Completed)
        {
            return Result.Success();
        }

        bool isFailedRecovery =
            application.Status == WorkspaceStaffOnboardingState.Failed &&
            application.StaffMemberId.HasValue;

        if (isFailedRecovery)
        {
            return Result.Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RecoveryAttemptRequired);
        }

        Result admitted = await this.RequireOperationalAsync(
            application.ScopeId,
            cancellationToken).ConfigureAwait(false);
        if (admitted.IsFailure)
        {
            return admitted;
        }

        WorkspaceStaffOnboardingProcessingRestrictionProjection? restriction =
            await restrictionProjections.GetAsync(
                application.Id,
                cancellationToken).ConfigureAwait(false);
        if (restriction is null ||
            restriction.ContractVersion !=
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion)
        {
            return Result.Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionProjectionUnavailable);
        }

        if (restriction.IsRestricted)
        {
            return Result.Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .ProcessingRestricted);
        }

        Result membershipBound = await this.EnsureMembershipBindingAsync(
            application,
            requireExistingBinding: false,
            cancellationToken).ConfigureAwait(false);
        if (membershipBound.IsFailure)
        {
            return membershipBound;
        }

        Result started = application.BeginProvisioning(clock.UtcNow);
        if (started.IsFailure)
        {
            return started;
        }

        WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
            application.SourceId,
            cancellationToken).ConfigureAwait(false);
        if (plan is null || plan.SourceKind != application.SourceKind ||
            plan.Status != WorkspaceStaffAccessPlanState.Active)
        {
            application.Fail(
                WorkspaceStaffOnboardingApplicationErrors.AccessPlanUnavailable.Code,
                clock.UtcNow);
            return Result.Failure(
                WorkspaceStaffOnboardingApplicationErrors.AccessPlanUnavailable);
        }

        Guid[] propertyIds = plan.Properties
            .Select(property => property.PropertyId)
            .Order()
            .ToArray();
        Result<Gma.Modules.AccessControl.Contracts.AccessProfileDto> validated =
            await planPolicy.ValidateAsync(
                application.ScopeId,
                plan.SourceKind,
                plan.ProfileKey,
                propertyIds,
                plan.CreatedBySubjectId,
                cancellationToken).ConfigureAwait(false);
        if (validated.IsFailure || validated.Value.Id != plan.ProfileId)
        {
            string failureCode = validated.IsFailure
                ? validated.Error.Code
                : WorkspaceStaffAccessPlanApplicationErrors.ProfileUnavailable.Code;
            application.Fail(failureCode, clock.UtcNow);
            return Result.Failure(
                WorkspaceStaffOnboardingApplicationErrors.AccessPlanUnavailable);
        }

        if (!application.StaffMemberId.HasValue)
        {
            if (string.IsNullOrWhiteSpace(application.DisplayName))
            {
                return Result.Failure(WorkspaceStaffOnboardingErrors.StateConflict);
            }

            admitted = await this.RequireOperationalAsync(
                application.ScopeId,
                cancellationToken).ConfigureAwait(false);
            if (admitted.IsFailure)
            {
                return admitted;
            }

            StaffOnboardingProvisioningResult provisioned = await staff.ProvisionAsync(
                new StaffOnboardingProvisioningRequest(
                    application.Id,
                    application.SubjectId,
                    application.DisplayName,
                    application.LegalName,
                    application.WorkEmail,
                    application.WorkPhone,
                    application.EmployeeNumber,
                    application.JobTitle,
                    application.Department,
                    "integration:organizations"),
                cancellationToken).ConfigureAwait(false);
            if (!provisioned.IsSuccess || !provisioned.StaffMemberId.HasValue)
            {
                string failureCode = provisioned.ErrorCode ??
                    WorkspaceStaffOnboardingApplicationErrors.ProvisioningFailed.Code;
                application.Fail(failureCode, clock.UtcNow);
                logger.LogWarning(
                    "Staff onboarding could not provision Staff: {ErrorCode}.",
                    failureCode);
                return Result.Failure(WorkspaceStaffOnboardingApplicationErrors.ProvisioningFailed);
            }

            if (!provisioned.ResolutionEventId.HasValue ||
                provisioned.ResolutionEventId.Value == Guid.Empty ||
                provisioned.ResolutionEventId.Value == application.Id)
            {
                return Result.Failure(
                    WorkspaceStaffOnboardingApplicationErrors
                        .IdentityAnchorConflict);
            }

            Guid continuationEventId = this.CreateContinuationEventId(
                application.Id,
                provisioned.ResolutionEventId.Value);
            if (continuationEventId == Guid.Empty)
            {
                return Result.Failure(
                    WorkspaceStaffOnboardingApplicationErrors
                        .IdentityAnchorConflict);
            }

            Result ready = application.MarkStaffReady(
                provisioned.StaffMemberId.Value,
                provisioned.ResolutionEventId.Value,
                continuationEventId,
                clock.UtcNow);
            if (ready.IsFailure)
            {
                return ready;
            }

            return Result.Success();
        }
        else if (application.Status == WorkspaceStaffOnboardingState.Provisioning)
        {
            if (!application.IdentityAnchorExpectedResolutionEventId.HasValue ||
                !application.IdentityAnchorContinuationEventId.HasValue)
            {
                return Result.Failure(
                    WorkspaceStaffOnboardingApplicationErrors
                        .IdentityAnchorConflict);
            }

            Result ready = application.MarkStaffReady(
                application.StaffMemberId.Value,
                application.IdentityAnchorExpectedResolutionEventId.Value,
                application.IdentityAnchorContinuationEventId.Value,
                clock.UtcNow);
            if (ready.IsFailure)
            {
                return ready;
            }
        }

        admitted = await this.RequireOperationalAsync(
            application.ScopeId,
            cancellationToken).ConfigureAwait(false);
        if (admitted.IsFailure)
        {
            return admitted;
        }

        StaffPropertyAssignmentProvisioningResult assignments =
            await staffProperties.ReconcileAsync(
                new StaffPropertyAssignmentProvisioningRequest(
                    application.StaffMemberId.Value,
                    propertyIds,
                    "integration:workspaces",
                    "Workspace Staff access plan applied."),
                cancellationToken).ConfigureAwait(false);
        if (!assignments.IsSuccess)
        {
            Result<WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>
                assignmentFailureFence =
                    await anchorConvergence.FenceActiveGrantAcquiredAsync(
                        application,
                        application.StaffMemberId.Value,
                        cancellationToken).ConfigureAwait(false);
            if (assignmentFailureFence.IsFailure)
            {
                return Result.Failure(assignmentFailureFence.Error);
            }

            if (assignmentFailureFence.Value.Outcome !=
                WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Active)
            {
                return Result.Success();
            }

            string failureCode = assignments.ErrorCode ??
                WorkspaceStaffOnboardingApplicationErrors.ProvisioningFailed.Code;
            application.Fail(failureCode, clock.UtcNow);
            logger.LogWarning(
                "Staff onboarding could not reconcile properties: {ErrorCode}.",
                failureCode);
            return Result.Failure(WorkspaceStaffOnboardingApplicationErrors.ProvisioningFailed);
        }

        Result<WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>
            grantFence = await anchorConvergence.FenceActiveGrantAcquiredAsync(
                application,
                application.StaffMemberId.Value,
                cancellationToken).ConfigureAwait(false);
        if (grantFence.IsFailure)
        {
            return Result.Failure(grantFence.Error);
        }

        if (grantFence.Value.Outcome !=
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Active)
        {
            return Result.Success();
        }

        try
        {
            Result membershipRechecked = await this.EnsureMembershipBindingAsync(
                application,
                requireExistingBinding: true,
                cancellationToken).ConfigureAwait(false);
            if (membershipRechecked.IsFailure)
            {
                return membershipRechecked;
            }

            admitted = await this.RequireOperationalAsync(
                application.ScopeId,
                cancellationToken).ConfigureAwait(false);
            if (admitted.IsFailure)
            {
                return admitted;
            }

            Result provisionedAccess = await access.ProvisionMemberAsync(
                    application.ScopeId,
                    application.SubjectId,
                    plan.ProfileId,
                    propertyIds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (provisionedAccess.IsFailure)
            {
                application.Fail(AccessProvisioningFailureCode, clock.UtcNow);
                logger.LogWarning(
                    "Staff onboarding access provisioning failed at {FailureStage} with {ErrorCode}.",
                    "profile-reconciliation",
                    provisionedAccess.Error.Code);
                return Result.Failure(
                    WorkspaceStaffOnboardingApplicationErrors.ProvisioningFailed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            application.Fail(AccessProvisioningFailureCode, clock.UtcNow);
            logger.LogWarning(
                "Staff onboarding access provisioning failed at {FailureStage} with {ErrorCode}; {ExceptionType} was raised.",
                "access-bootstrap",
                AccessProvisioningFailureCode,
                exception.GetType().Name);
            return Result.Failure(WorkspaceStaffOnboardingApplicationErrors.ProvisioningFailed);
        }

        return application.Complete(clock.UtcNow);
    }

    private async Task<Result> EnsureMembershipBindingAsync(
        WorkspaceStaffOnboarding application,
        bool requireExistingBinding,
        CancellationToken cancellationToken)
    {
        if (requireExistingBinding &&
            (!application.AcceptedMembershipId.HasValue ||
             !application.AcceptedMembershipVersion.HasValue))
        {
            application.Fail(MembershipStateConflictCode, clock.UtcNow);
            return Result.Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .MembershipStateConflict);
        }

        WorkspaceOrganizationMembershipObservation observation =
            await membershipObserver.ObserveAsync(
                application.ScopeId,
                application.SubjectId,
                cancellationToken).ConfigureAwait(false);
        if (observation.Status != WorkspaceRecoveryVerificationStatus.Equivalent ||
            !observation.MembershipId.HasValue ||
            !observation.MembershipVersion.HasValue)
        {
            string code = observation.Status ==
                WorkspaceRecoveryVerificationStatus.RetryLater
                    ? WorkspaceStaffOnboardingApplicationErrors
                        .MembershipStateUnavailable.Code
                    : MembershipStateConflictCode;
            application.Fail(code, clock.UtcNow);
            return Result.Failure(
                observation.Status == WorkspaceRecoveryVerificationStatus.RetryLater
                    ? WorkspaceStaffOnboardingApplicationErrors
                        .MembershipStateUnavailable
                    : WorkspaceStaffOnboardingApplicationErrors
                        .MembershipStateConflict);
        }

        if (application.AcceptedMembershipId.HasValue ||
            application.AcceptedMembershipVersion.HasValue)
        {
            if (application.AcceptedMembershipId != observation.MembershipId ||
                application.AcceptedMembershipVersion !=
                    observation.MembershipVersion)
            {
                application.Fail(MembershipStateConflictCode, clock.UtcNow);
                return Result.Failure(
                    WorkspaceStaffOnboardingApplicationErrors
                        .MembershipStateConflict);
            }

            return Result.Success();
        }

        return application.BindAcceptedMembership(
            observation.MembershipId.Value,
            observation.MembershipVersion.Value,
            clock.UtcNow);
    }

    private async ValueTask<Result> RequireOperationalAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        WorkspaceOperationalAdmissionGuard.RequireAllowed(
            await operationalAdmission.EvaluateAsync(
                tenantId,
                cancellationToken).ConfigureAwait(false));

    private Guid CreateContinuationEventId(
        Guid applicationId,
        Guid resolutionEventId)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Guid candidate = ids.NewId();
            if (candidate != Guid.Empty &&
                candidate != applicationId &&
                candidate != resolutionEventId)
            {
                return candidate;
            }
        }

        return Guid.Empty;
    }
}

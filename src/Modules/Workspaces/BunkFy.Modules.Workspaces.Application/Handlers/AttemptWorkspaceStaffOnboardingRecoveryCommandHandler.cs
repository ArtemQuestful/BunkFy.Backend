namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Mapping;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.AccessControl;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Modules.AccessControl.Contracts;

internal sealed class AttemptWorkspaceStaffOnboardingRecoveryCommandHandler(
    IWorkspaceStaffOnboardingRecoveryOperationLock recoveryLock,
    IWorkspaceStaffOnboardingRecoveryReceiptRepository receipts,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
        restrictionProjections,
    IWorkspaceStaffAccessPlanRepository plans,
    WorkspaceStaffAccessPlanPolicy planPolicy,
    WorkspaceOrganizationMembershipObserver membershipObserver,
    IEnumerable<IStaffOnboardingStateVerifier> staffVerifiers,
    IAccessControlSubjectAuthoritySnapshotReader accessSnapshots,
    ISystemClock clock)
    : ICommandHandler<
        AttemptWorkspaceStaffOnboardingRecoveryCommand,
        WorkspaceStaffOnboardingRecoveryAttemptDto>
{
    private readonly IStaffOnboardingStateVerifier? staffVerifier =
        staffVerifiers.SingleOrDefault();

    public async Task<Result<WorkspaceStaffOnboardingRecoveryAttemptDto>>
        HandleAsync(
            AttemptWorkspaceStaffOnboardingRecoveryCommand command,
            CancellationToken cancellationToken)
    {
        string actorId = command.RecoveryActorId?.Trim() ?? string.Empty;
        if (command.ApplicationId == Guid.Empty ||
            command.OperationId == Guid.Empty ||
            command.ExpectedApplicationVersion < 1 ||
            actorId.Length is 0 or >
                WorkspaceStaffOnboardingRecoveryReceipt.ActorIdMaxLength)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RecoveryRequestInvalid);
        }

        await recoveryLock.AcquireAsync(
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        string fingerprint = WorkspaceStaffOnboardingRecoveryFingerprint.Compute(
            command.ApplicationId,
            command.OperationId,
            command.ExpectedApplicationVersion,
            actorId);
        WorkspaceStaffOnboardingRecoveryReceipt? replay =
            await receipts.FindByOperationAsync(
                command.OperationId,
                cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (!replay.MatchesReplay(fingerprint))
            {
                return Failure(
                    WorkspaceStaffOnboardingApplicationErrors
                        .RecoveryIdempotencyConflict);
            }

            WorkspaceStaffOnboardingRecoveryMetrics.Record(
                replay,
                replay: true);
            return Result.Success(ToDto(replay));
        }

        WorkspaceStaffOnboardingMutationLease lease =
            await mutations.AcquireExistingAsync(
                command.ApplicationId,
                WorkspaceStaffOnboardingSourceLockMode.Write,
                requireOperational: false,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffOnboarding? application = lease.Application;
        if (!lease.CoordinateExists || application is null)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors.ApplicationNotFound);
        }

        if (application.Version != command.ExpectedApplicationVersion)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RecoveryApplicationVersionConflict);
        }

        if (application.Status != WorkspaceStaffOnboardingState.Failed ||
            !application.StaffMemberId.HasValue)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors.RecoveryIneligible);
        }

        WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
            application.SourceId,
            cancellationToken).ConfigureAwait(false);
        if (plan is null ||
            plan.SourceKind != application.SourceKind ||
            plan.Status != WorkspaceStaffAccessPlanState.Active)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors.AccessPlanUnavailable);
        }

        Guid[] propertyIds = plan.Properties
            .Select(property => property.PropertyId)
            .Order()
            .ToArray();
        Result<AccessProfileDto> profileResult = await planPolicy.ValidateAsync(
            application.ScopeId,
            plan.SourceKind,
            plan.ProfileKey,
            propertyIds,
            plan.CreatedBySubjectId,
            cancellationToken).ConfigureAwait(false);
        if (profileResult.IsFailure ||
            profileResult.Value.Id != plan.ProfileId ||
            profileResult.Value.Status != AccessProfileStatus.Active)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors.AccessPlanUnavailable);
        }

        AccessProfileDto currentProfile = profileResult.Value;
        AccessScope workspaceScope = WorkspaceAccessScopes.Create(
            application.ScopeId);
        AccessScope[] targetScopes = propertyIds.Length == 0
            ? [workspaceScope]
            : propertyIds.Select(propertyId =>
                WorkspaceAccessScopes.CreateProperty(
                    application.ScopeId,
                    propertyId)).ToArray();
        string targetDigest = WorkspaceStaffOnboardingRecoveryFingerprint
            .ComputeTargetSet(targetScopes.Select(scope => scope.Value));
        WorkspaceStaffOnboardingProcessingRestrictionProjection? restriction =
            await restrictionProjections.GetAsync(
                application.Id,
                cancellationToken).ConfigureAwait(false);
        if (restriction is null ||
            restriction.ContractVersion !=
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionProjectionUnavailable);
        }

        if (restriction.IsRestricted)
        {
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                WorkspaceStaffOnboardingRecoveryReason.ProcessingRestricted,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceRecoveryVerification.Unknown,
                organization: null,
                staff: null,
                access: null,
                cancellationToken).ConfigureAwait(false);
        }

        if (!application.AcceptedMembershipId.HasValue ||
            !application.AcceptedMembershipVersion.HasValue)
        {
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                WorkspaceStaffOnboardingRecoveryReason
                    .LegacyMembershipBindingMissing,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceRecoveryVerification.Unknown,
                organization: null,
                staff: null,
                access: null,
                cancellationToken).ConfigureAwait(false);
        }

        WorkspaceOrganizationMembershipObservation organizationFirst =
            await membershipObserver.ObserveAsync(
                application.ScopeId,
                application.SubjectId,
                cancellationToken).ConfigureAwait(false);
        WorkspaceRecoveryVerification organizationVerification = Map(
            organizationFirst.Status);
        if (organizationVerification != WorkspaceRecoveryVerification.Equivalent ||
            organizationFirst.MembershipId != application.AcceptedMembershipId ||
            organizationFirst.MembershipVersion !=
                application.AcceptedMembershipVersion)
        {
            bool retry = organizationVerification ==
                WorkspaceRecoveryVerification.RetryLater;
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                retry
                    ? WorkspaceStaffOnboardingRecoveryOutcome.RetryLater
                    : WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                retry
                    ? WorkspaceStaffOnboardingRecoveryReason
                        .MembershipUnavailable
                    : WorkspaceStaffOnboardingRecoveryReason
                        .MembershipConflict,
                retry
                    ? WorkspaceRecoveryVerification.RetryLater
                    : WorkspaceRecoveryVerification.ReviewRequired,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceRecoveryVerification.Unknown,
                organizationFirst,
                staff: null,
                access: null,
                cancellationToken).ConfigureAwait(false);
        }

        if (this.staffVerifier is null)
        {
            return Failure(
                WorkspaceStaffOnboardingApplicationErrors
                    .RecoveryVerifierUnavailable);
        }

        StaffOnboardingStateVerificationRequest staffRequest = new(
            application.ScopeId,
            application.Id,
            application.StaffMemberId.Value,
            application.SubjectId,
            propertyIds);
        StaffOnboardingStateVerificationResult staffFirst =
            await this.staffVerifier.VerifyAsync(
                staffRequest,
                cancellationToken).ConfigureAwait(false);
        WorkspaceRecoveryVerification staffVerification = Map(
            staffFirst.Status);
        if (staffVerification != WorkspaceRecoveryVerification.Equivalent)
        {
            bool retry = staffVerification == WorkspaceRecoveryVerification.RetryLater;
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                retry
                    ? WorkspaceStaffOnboardingRecoveryOutcome.RetryLater
                    : WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                retry
                    ? WorkspaceStaffOnboardingRecoveryReason.StaffUnavailable
                    : WorkspaceStaffOnboardingRecoveryReason.StaffConflict,
                WorkspaceRecoveryVerification.Equivalent,
                staffVerification,
                WorkspaceRecoveryVerification.Unknown,
                organizationFirst,
                staffFirst,
                access: null,
                cancellationToken).ConfigureAwait(false);
        }

        AccessControlSubjectAuthoritySnapshotRequest accessRequest = new(
            AccessSubjectKind.User,
            application.SubjectId,
            workspaceScope,
            targetScopes);
        AccessControlSubjectAuthoritySnapshot accessFirst = await accessSnapshots
            .ReadAsync(
                accessRequest,
                cancellationToken).ConfigureAwait(false);
        WorkspaceRecoveryVerification accessVerification =
            WorkspaceStaffOnboardingRecoveryAccessEvaluator.Evaluate(
                accessFirst,
                currentProfile,
                application.SubjectId,
                application.ScopeId,
                propertyIds);
        if (accessVerification != WorkspaceRecoveryVerification.Equivalent)
        {
            bool retry = accessVerification == WorkspaceRecoveryVerification.RetryLater;
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                retry
                    ? WorkspaceStaffOnboardingRecoveryOutcome.RetryLater
                    : WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                retry
                    ? WorkspaceStaffOnboardingRecoveryReason.AccessUnavailable
                    : WorkspaceStaffOnboardingRecoveryReason.AccessConflict,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.Equivalent,
                accessVerification,
                organizationFirst,
                staffFirst,
                accessFirst,
                cancellationToken).ConfigureAwait(false);
        }

        WorkspaceOrganizationMembershipObservation organizationSecond =
            await membershipObserver.ObserveAsync(
                application.ScopeId,
                application.SubjectId,
                cancellationToken).ConfigureAwait(false);
        WorkspaceRecoveryVerification secondOrganizationVerification = Map(
            organizationSecond.Status);
        bool secondBindingChanged =
            organizationSecond.MembershipId != application.AcceptedMembershipId ||
            organizationSecond.MembershipVersion !=
                application.AcceptedMembershipVersion;
        if (secondOrganizationVerification !=
                WorkspaceRecoveryVerification.Equivalent ||
            secondBindingChanged)
        {
            bool retry = secondOrganizationVerification ==
                WorkspaceRecoveryVerification.RetryLater;
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                retry
                    ? WorkspaceStaffOnboardingRecoveryOutcome.RetryLater
                    : WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                retry
                    ? WorkspaceStaffOnboardingRecoveryReason
                        .MembershipUnavailable
                    : WorkspaceStaffOnboardingRecoveryReason
                        .MembershipConflict,
                retry
                    ? WorkspaceRecoveryVerification.RetryLater
                    : WorkspaceRecoveryVerification.ReviewRequired,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.Equivalent,
                organizationSecond,
                staffFirst,
                accessFirst,
                cancellationToken).ConfigureAwait(false);
        }

        if (!WorkspaceStaffOnboardingRecoveryProofComparer.Matches(
                organizationFirst,
                organizationSecond))
        {
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                WorkspaceStaffOnboardingRecoveryOutcome.RetryLater,
                WorkspaceStaffOnboardingRecoveryReason.MembershipUnavailable,
                WorkspaceRecoveryVerification.RetryLater,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.Equivalent,
                organizationSecond,
                staffFirst,
                accessFirst,
                cancellationToken).ConfigureAwait(false);
        }

        StaffOnboardingStateVerificationResult staffSecond =
            await this.staffVerifier.VerifyAsync(
                staffRequest,
                cancellationToken).ConfigureAwait(false);
        WorkspaceRecoveryVerification secondStaffVerification = Map(
            staffSecond.Status);
        if (secondStaffVerification != WorkspaceRecoveryVerification.Equivalent)
        {
            bool retry = secondStaffVerification ==
                WorkspaceRecoveryVerification.RetryLater;
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                retry
                    ? WorkspaceStaffOnboardingRecoveryOutcome.RetryLater
                    : WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                retry
                    ? WorkspaceStaffOnboardingRecoveryReason.StaffUnavailable
                    : WorkspaceStaffOnboardingRecoveryReason.StaffConflict,
                WorkspaceRecoveryVerification.Equivalent,
                secondStaffVerification,
                WorkspaceRecoveryVerification.Equivalent,
                organizationSecond,
                staffSecond,
                accessFirst,
                cancellationToken).ConfigureAwait(false);
        }

        if (!WorkspaceStaffOnboardingRecoveryProofComparer.Matches(
                staffFirst,
                staffSecond))
        {
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                WorkspaceStaffOnboardingRecoveryOutcome.RetryLater,
                WorkspaceStaffOnboardingRecoveryReason.StaffUnavailable,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.RetryLater,
                WorkspaceRecoveryVerification.Equivalent,
                organizationSecond,
                staffSecond,
                accessFirst,
                cancellationToken).ConfigureAwait(false);
        }

        AccessControlSubjectAuthoritySnapshot accessSecond =
            await accessSnapshots.ReadAsync(
                accessRequest,
                cancellationToken).ConfigureAwait(false);
        WorkspaceRecoveryVerification secondAccessVerification =
            WorkspaceStaffOnboardingRecoveryAccessEvaluator.Evaluate(
                accessSecond,
                currentProfile,
                application.SubjectId,
                application.ScopeId,
                propertyIds);
        if (secondAccessVerification != WorkspaceRecoveryVerification.Equivalent)
        {
            bool retry = secondAccessVerification ==
                WorkspaceRecoveryVerification.RetryLater;
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                retry
                    ? WorkspaceStaffOnboardingRecoveryOutcome.RetryLater
                    : WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                retry
                    ? WorkspaceStaffOnboardingRecoveryReason.AccessUnavailable
                    : WorkspaceStaffOnboardingRecoveryReason.AccessConflict,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.Equivalent,
                secondAccessVerification,
                organizationSecond,
                staffSecond,
                accessSecond,
                cancellationToken).ConfigureAwait(false);
        }

        if (!WorkspaceStaffOnboardingRecoveryProofComparer.Matches(
                accessFirst,
                accessSecond))
        {
            return await this.RecordAsync(
                application,
                plan,
                currentProfile,
                propertyIds.Length,
                targetDigest,
                command,
                actorId,
                fingerprint,
                WorkspaceStaffOnboardingRecoveryOutcome.RetryLater,
                WorkspaceStaffOnboardingRecoveryReason.AccessUnavailable,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.RetryLater,
                organizationSecond,
                staffSecond,
                accessSecond,
                cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset recordedAtUtc = ToPersistencePrecision(clock.UtcNow);
        Result recovered = application.RecoverCompletedExact(recordedAtUtc);
        if (recovered.IsFailure)
        {
            return Result.Failure<WorkspaceStaffOnboardingRecoveryAttemptDto>(
                recovered.Error);
        }

        return await this.RecordAsync(
            application,
            plan,
            currentProfile,
            propertyIds.Length,
            targetDigest,
            command,
            actorId,
            fingerprint,
            WorkspaceStaffOnboardingRecoveryOutcome.Recovered,
            WorkspaceStaffOnboardingRecoveryReason.ExactStateRecovered,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerification.Equivalent,
            organizationSecond,
            staffSecond,
            accessSecond,
            cancellationToken,
            selectedVersion: command.ExpectedApplicationVersion,
            selectedStatus: WorkspaceStaffOnboardingState.Failed,
            recordedAtUtc: recordedAtUtc)
            .ConfigureAwait(false);
    }

    private async Task<Result<WorkspaceStaffOnboardingRecoveryAttemptDto>>
        RecordAsync(
            WorkspaceStaffOnboarding application,
            WorkspaceStaffAccessPlan plan,
            AccessProfileDto currentProfile,
            int propertyCount,
            string targetDigest,
            AttemptWorkspaceStaffOnboardingRecoveryCommand command,
            string actorId,
            string fingerprint,
            WorkspaceStaffOnboardingRecoveryOutcome outcome,
            WorkspaceStaffOnboardingRecoveryReason reason,
            WorkspaceRecoveryVerification organizationVerification,
            WorkspaceRecoveryVerification staffVerification,
            WorkspaceRecoveryVerification accessVerification,
            WorkspaceOrganizationMembershipObservation? organization,
            StaffOnboardingStateVerificationResult? staff,
            AccessControlSubjectAuthoritySnapshot? access,
            CancellationToken cancellationToken,
            long? selectedVersion = null,
            WorkspaceStaffOnboardingState? selectedStatus = null,
            DateTimeOffset? recordedAtUtc = null)
    {
        DateTimeOffset decisionAtUtc = recordedAtUtc ??
            ToPersistencePrecision(clock.UtcNow);
        long selected = selectedVersion ?? application.Version;
        WorkspaceStaffOnboardingState status = selectedStatus ?? application.Status;
        Result<WorkspaceStaffOnboardingRecoveryReceipt> created =
            WorkspaceStaffOnboardingRecoveryReceipt.Create(
                new WorkspaceStaffOnboardingRecoveryReceiptValues(
                    Guid.NewGuid(),
                    application.ScopeId,
                    command.OperationId,
                    application.Id,
                    application.SourceId,
                    application.StaffMemberId!.Value,
                    WorkspaceStaffOnboardingRecoveryTriggerKind.ManualOperator,
                    command.OperationId,
                    application.SubjectId,
                    plan.CreatedBySubjectId,
                    actorId,
                    WorkspaceAccessProvisioner.ProvisioningActorId,
                    fingerprint,
                    command.ExpectedApplicationVersion,
                    selected,
                    status,
                    application.Version,
                    application.Status,
                    currentProfile.Id,
                    currentProfile.Version,
                    propertyCount,
                    targetDigest,
                    organization?.OrganizationRevision,
                    organization?.MembershipId,
                    organization?.MembershipVersion,
                    organization?.MembershipStatus is { } membershipStatus
                        ? (int)membershipStatus
                        : null,
                    staff?.TenantRevision,
                    staff?.CurrentMemberVersion,
                    staff?.StateProofSha256,
                    staff?.PropertySetSha256,
                    access?.ManagementRevision,
                    access?.FactsSha256,
                    organizationVerification,
                    staffVerification,
                    accessVerification,
                    outcome,
                    reason,
                    AccessMutationPerformed: false,
                    NormalizeObservation(organization?.ObservedAtUtc),
                    NormalizeObservation(staff?.ObservedAtUtc),
                    NormalizeObservation(access?.ObservedAtUtc),
                    decisionAtUtc));
        if (created.IsFailure)
        {
            return Result.Failure<WorkspaceStaffOnboardingRecoveryAttemptDto>(
                created.Error);
        }

        await receipts.AddAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
        WorkspaceStaffOnboardingRecoveryMetrics.Record(
            created.Value,
            replay: false);
        return Result.Success(ToDto(created.Value));
    }

    private static WorkspaceRecoveryVerification Map(
        WorkspaceRecoveryVerificationStatus status) => status switch
        {
            WorkspaceRecoveryVerificationStatus.Equivalent =>
                WorkspaceRecoveryVerification.Equivalent,
            WorkspaceRecoveryVerificationStatus.RetryLater =>
                WorkspaceRecoveryVerification.RetryLater,
            _ => WorkspaceRecoveryVerification.ReviewRequired
        };

    private static WorkspaceRecoveryVerification Map(
        StaffOnboardingStateVerificationStatus status) => status switch
        {
            StaffOnboardingStateVerificationStatus.Equivalent =>
                WorkspaceRecoveryVerification.Equivalent,
            StaffOnboardingStateVerificationStatus.RetryLater =>
                WorkspaceRecoveryVerification.RetryLater,
            _ => WorkspaceRecoveryVerification.ReviewRequired
        };

    internal static WorkspaceStaffOnboardingRecoveryAttemptDto ToDtoForQuery(
        WorkspaceStaffOnboardingRecoveryReceipt receipt) => new(
        receipt.OperationId,
        receipt.ApplicationId,
        receipt.Outcome switch
        {
            WorkspaceStaffOnboardingRecoveryOutcome.Recovered =>
                WorkspaceStaffOnboardingRecoveryAttemptOutcome.Recovered,
            WorkspaceStaffOnboardingRecoveryOutcome.RetryLater =>
                WorkspaceStaffOnboardingRecoveryAttemptOutcome.RetryLater,
            _ => WorkspaceStaffOnboardingRecoveryAttemptOutcome.ReviewRequired
        },
        receipt.Reason switch
        {
            WorkspaceStaffOnboardingRecoveryReason.ExactStateRecovered =>
                WorkspaceStaffOnboardingRecoveryAttemptReason.ExactStateRecovered,
            WorkspaceStaffOnboardingRecoveryReason.LegacyMembershipBindingMissing =>
                WorkspaceStaffOnboardingRecoveryAttemptReason
                    .LegacyMembershipBindingMissing,
            WorkspaceStaffOnboardingRecoveryReason.MembershipConflict =>
                WorkspaceStaffOnboardingRecoveryAttemptReason.MembershipConflict,
            WorkspaceStaffOnboardingRecoveryReason.MembershipUnavailable =>
                WorkspaceStaffOnboardingRecoveryAttemptReason.MembershipUnavailable,
            WorkspaceStaffOnboardingRecoveryReason.StaffConflict =>
                WorkspaceStaffOnboardingRecoveryAttemptReason.StaffConflict,
            WorkspaceStaffOnboardingRecoveryReason.StaffUnavailable =>
                WorkspaceStaffOnboardingRecoveryAttemptReason.StaffUnavailable,
            WorkspaceStaffOnboardingRecoveryReason.AccessConflict =>
                WorkspaceStaffOnboardingRecoveryAttemptReason.AccessConflict,
            WorkspaceStaffOnboardingRecoveryReason.AccessUnavailable =>
                WorkspaceStaffOnboardingRecoveryAttemptReason.AccessUnavailable,
            _ => WorkspaceStaffOnboardingRecoveryAttemptReason.ProcessingRestricted
        },
        receipt.ResultingApplicationStatus.ToContractStatus(),
        receipt.ResultingApplicationVersion,
        receipt.Outcome == WorkspaceStaffOnboardingRecoveryOutcome.RetryLater
            ? WorkspaceStaffOnboardingRecoveryRetryDirective
                .NewOperationAfterDelay
            : WorkspaceStaffOnboardingRecoveryRetryDirective.None,
        receipt.Outcome == WorkspaceStaffOnboardingRecoveryOutcome.RetryLater
            ? WorkspaceStaffOnboardingRecoveryAttemptDto
                .RetryLaterDelaySeconds
            : null,
        receipt.RecordedAtUtc);

    private static WorkspaceStaffOnboardingRecoveryAttemptDto ToDto(
        WorkspaceStaffOnboardingRecoveryReceipt receipt) =>
        ToDtoForQuery(receipt);

    private static DateTimeOffset ToPersistencePrecision(
        DateTimeOffset value)
    {
        const long ticksPerMicrosecond =
            TimeSpan.TicksPerMillisecond / 1000;
        return new(
            value.Ticks - (value.Ticks % ticksPerMicrosecond),
            value.Offset);
    }

    private static DateTimeOffset? NormalizeObservation(
        DateTimeOffset? value) => value.HasValue
        ? ToPersistencePrecision(value.Value)
        : null;

    private static Result<WorkspaceStaffOnboardingRecoveryAttemptDto> Failure(
        Error error) =>
        Result.Failure<WorkspaceStaffOnboardingRecoveryAttemptDto>(error);
}

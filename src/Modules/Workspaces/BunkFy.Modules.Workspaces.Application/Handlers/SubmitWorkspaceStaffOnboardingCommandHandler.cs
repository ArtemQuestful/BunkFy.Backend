namespace BunkFy.Modules.Workspaces.Application.Handlers;

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
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Options;

internal sealed class SubmitWorkspaceStaffOnboardingCommandHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
        restrictionProjections,
    WorkspaceStaffOnboardingMutationCoordinator mutations,
    IWorkspaceStaffAccessPlanRepository plans,
    WorkspaceStaffJoinTokenAuthorityResolver authorityResolver,
    IAuthMemberAdmissionReader admissions,
    IOrganizationEnrollmentClaimInspector claims,
    IOptions<WorkspaceStaffOnboardingOptions> options,
    WorkspaceOperationalAdmissionEvaluator operationalAdmission,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<SubmitWorkspaceStaffOnboardingCommand, WorkspaceStaffOnboardingDto>
{
    public async Task<Result<WorkspaceStaffOnboardingDto>> HandleAsync(
        SubmitWorkspaceStaffOnboardingCommand command,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffJoinTokenAuthority? authority = await authorityResolver.ResolveAsync(
            command.SourceKind,
            command.Token,
            cancellationToken).ConfigureAwait(false);
        if (!authority.HasValue)
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors.JoinTokenInvalid);
        }

        if (!string.Equals(
            scopeContext.ScopeId,
            authority.Value.OrganizationId.ToString("D"),
            StringComparison.Ordinal))
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors.AccessPlanUnavailable);
        }

        Result admitted = WorkspaceOperationalAdmissionGuard.RequireAllowed(
            await operationalAdmission.EvaluateAsync(
                authority.Value.OrganizationId.ToString("D"),
                cancellationToken).ConfigureAwait(false));
        if (admitted.IsFailure)
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(admitted.Error);
        }

        WorkspaceStaffOnboardingSource sourceKind = command.SourceKind.ToDomain();
        if (!Guid.TryParse(command.SubjectId, out Guid memberId))
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors.VerifiedIdentityRequired);
        }

        WorkspaceStaffOnboardingMutationLease lease =
            await mutations.AcquireApplicantAsync(
                sourceKind,
                authority.Value.SourceId,
                command.SubjectId,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: true,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffOnboarding? application = lease.Application;
        if (lease.CoordinateExists && application is null)
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .ProcessingRestricted);
        }

        AuthMemberAdmission? admission = await admissions.FindActiveAsync(
                options.Value.GlobalAuthScopeId,
                memberId,
                cancellationToken)
            .ConfigureAwait(false);
        string? verifiedEmail = admission?.PreferredVerifiedEmail;
        if (string.IsNullOrWhiteSpace(verifiedEmail))
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors.VerifiedIdentityRequired);
        }

        if (application is not null &&
            (!authority.Value.AllowsSubmissionMutation ||
                application.Status != WorkspaceStaffOnboardingState.Submitted))
        {
            return Result.Success(application.ToDto());
        }

        if (application is null && !authority.Value.AllowsSubmissionMutation)
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors.JoinTokenInvalid);
        }

        WorkspaceStaffAccessPlan? plan = await plans.GetAsync(
            authority.Value.SourceId,
            cancellationToken).ConfigureAwait(false);
        if (plan is null || plan.SourceKind != sourceKind ||
            plan.Status != WorkspaceStaffAccessPlanState.Active)
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors.AccessPlanUnavailable);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (await WorkspaceStaffOnboardingProfileMutationAuthority
            .IsFencedAsync(
                claims,
                sourceKind,
                authority.Value.OrganizationId,
                authority.Value.SourceId,
                memberId.ToString("D"),
                nowUtc,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .ProfileMutationAuthorityUnavailable);
        }

        if (application is null)
        {
            Result<WorkspaceStaffOnboarding> created = WorkspaceStaffOnboarding.Create(
                ids.NewId(),
                authority.Value.OrganizationId.ToString("D"),
                sourceKind,
                authority.Value.SourceId,
                command.SubjectId,
                verifiedEmail,
                command.DisplayName,
                command.LegalName,
                command.WorkEmail,
                command.WorkPhone,
                command.EmployeeNumber,
                command.JobTitle,
                command.Department,
                nowUtc);
            if (created.IsFailure)
            {
                return Result.Failure<WorkspaceStaffOnboardingDto>(created.Error);
            }

            application = created.Value;
            await applications.AddAsync(application, cancellationToken).ConfigureAwait(false);
            Result<
                WorkspaceStaffOnboardingProcessingRestrictionProjection>
                baseline =
                    WorkspaceStaffOnboardingProcessingRestrictionProjection
                        .Create(
                            application.ScopeId,
                            application.Id,
                            WorkspaceStaffOnboardingProcessingRestrictionContract
                                .CurrentVersion,
                            nowUtc);
            if (baseline.IsFailure)
            {
                return Result.Failure<WorkspaceStaffOnboardingDto>(
                    baseline.Error);
            }

            await restrictionProjections.AddAsync(
                baseline.Value,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Result updated = application.UpdateSubmission(
                verifiedEmail,
                command.DisplayName,
                command.LegalName,
                command.WorkEmail,
                command.WorkPhone,
                command.EmployeeNumber,
                command.JobTitle,
                command.Department,
                nowUtc);
            if (updated.IsFailure)
            {
                return Result.Failure<WorkspaceStaffOnboardingDto>(updated.Error);
            }
        }

        return Result.Success(application.ToDto());
    }
}

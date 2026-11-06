namespace BunkFy.Modules.Workspaces.Application;

using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Microsoft.Extensions.DependencyInjection;

public interface IWorkspaceStaffOnboardingSubmitter
{
    Task<Result<WorkspaceStaffOnboardingSubmissionOutcome>>
        SubmitWithAuthorityOutcomeAsync(
            SubmitWorkspaceStaffOnboardingCommand command,
            CancellationToken cancellationToken = default);

    Task<Result<WorkspaceStaffOnboardingDto>> SubmitAsync(
        SubmitWorkspaceStaffOnboardingCommand command,
        CancellationToken cancellationToken = default);
}

internal sealed class WorkspaceStaffOnboardingSubmitter(
    WorkspaceStaffJoinTokenAuthorityResolver authorityResolver,
    IWorkspaceAuthoritativeScope authoritativeScope)
    : IWorkspaceStaffOnboardingSubmitter
{
    public async Task<Result<WorkspaceStaffOnboardingDto>> SubmitAsync(
        SubmitWorkspaceStaffOnboardingCommand command,
        CancellationToken cancellationToken = default)
    {
        Result<WorkspaceStaffOnboardingSubmissionOutcome> outcome =
            await this.SubmitWithAuthorityOutcomeAsync(
                command,
                cancellationToken).ConfigureAwait(false);
        return Map(outcome);
    }

    public async Task<Result<WorkspaceStaffOnboardingSubmissionOutcome>>
        SubmitWithAuthorityOutcomeAsync(
        SubmitWorkspaceStaffOnboardingCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkspaceStaffJoinTokenAuthority? authority = await authorityResolver.ResolveAsync(
            command.SourceKind,
            command.Token,
            cancellationToken).ConfigureAwait(false);
        if (!authority.HasValue)
        {
            return Result.Failure<WorkspaceStaffOnboardingSubmissionOutcome>(
                WorkspaceStaffOnboardingApplicationErrors.JoinTokenInvalid);
        }

        return await authoritativeScope.RunAsync(
            authority.Value.OrganizationId,
            async services =>
            {
                WorkspaceOperationalAdmissionDecision decision =
                    await services
                        .GetRequiredService<
                            WorkspaceOperationalAdmissionEvaluator>()
                        .EvaluateAsync(
                            authority.Value.OrganizationId.ToString("D"),
                            cancellationToken)
                        .ConfigureAwait(false);
                Result admitted =
                    WorkspaceOperationalAdmissionGuard.RequireAllowed(decision);
                return admitted.IsFailure
                    ? Result.Failure<
                        WorkspaceStaffOnboardingSubmissionOutcome>(
                        admitted.Error)
                    : await services
                        .GetRequiredService<IRequestDispatcher>()
                        .SendAsync(command, cancellationToken)
                        .ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    internal static Result<WorkspaceStaffOnboardingDto> Map(
        Result<WorkspaceStaffOnboardingSubmissionOutcome> outcome)
    {
        if (outcome.IsFailure)
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(outcome.Error);
        }

        return outcome.Value.Kind switch
        {
            WorkspaceStaffOnboardingSubmissionOutcomeKind.Applied
                when outcome.Value.Application is not null =>
                Result.Success(outcome.Value.Application),
            WorkspaceStaffOnboardingSubmissionOutcomeKind
                .AuthorityMovedToStaff
                when outcome.Value.Application is null =>
                Result.Failure<WorkspaceStaffOnboardingDto>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .ProfileMutationAuthorityUnavailable),
            _ => Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .IdentityAnchorConflict)
        };
    }
}

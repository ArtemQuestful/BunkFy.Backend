namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Naming;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class
    ScrubWorkspaceStaffRetentionCorrelationCommandHandler(
    IWorkspaceStaffRetentionCorrelationRepository repository,
    IWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence
        identityAnchors,
    IWorkspaceCrossGraphMutationLock crossGraphLock,
    WorkspaceStaffAccessMutationCoordinator mutations,
    IWorkspaceStaffRetentionAccessClosure accessClosure,
    ISystemClock clock,
    IIdGenerator ids,
    IScopeContext scopeContext)
    : ICommandHandler<
        ScrubWorkspaceStaffRetentionCorrelationCommand,
        WorkspaceStaffRetentionCorrelationReceipt>
{
    public async Task<Result<WorkspaceStaffRetentionCorrelationReceipt>>
        HandleAsync(
            ScrubWorkspaceStaffRetentionCorrelationCommand command,
            CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled ||
            !TenantIds.TryNormalize(
                scopeContext.ScopeId,
                out string? activeScopeId) ||
            !TenantIds.TryNormalize(
                command.TenantId,
                out string? tenantId) ||
            !string.Equals(
                activeScopeId,
                tenantId,
                StringComparison.Ordinal) ||
            command.ExecutionId == Guid.Empty ||
            command.StaffMemberId == Guid.Empty ||
            command.SelectedStaffVersion <= 0)
        {
            return Result.Failure<
                WorkspaceStaffRetentionCorrelationReceipt>(
                WorkspaceStaffRetentionErrors.RequestInvalid);
        }

        await crossGraphLock.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await mutations.AcquireStaffAsync(
                command.StaffMemberId,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffAccessClosureResult closure =
            await accessClosure.EnsureClosedAsync(
                tenantId,
                command.StaffMemberId,
                command.SelectedStaffVersion,
                cancellationToken).ConfigureAwait(false);
        if (closure.Status != WorkspaceStaffAccessClosureStatus.Completed)
        {
            return Result.Failure<
                WorkspaceStaffRetentionCorrelationReceipt>(
                new(
                    closure.Code,
                    closure.Status ==
                        WorkspaceStaffAccessClosureStatus.Blocked
                        ? "The workspace Staff retention prerequisite is blocked."
                        : "The workspace Staff retention prerequisite is temporarily unavailable."));
        }

        if (!await identityAnchors.CanMutateAsync(
                tenantId,
                closure.SubjectId,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<
                WorkspaceStaffRetentionCorrelationReceipt>(
                    WorkspaceStaffRetentionErrors
                        .IdentityAnchorUnavailable);
        }

        return await repository.ScrubAsync(
            new WorkspaceStaffRetentionCorrelationScrubRequest(
                ids.NewId(),
                command.ExecutionId,
                tenantId,
                command.StaffMemberId,
                command.SelectedStaffVersion,
                closure.SubjectId,
                ToPersistencePrecision(
                    clock.UtcNow.ToUniversalTime())),
            cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset ToPersistencePrecision(
        DateTimeOffset value)
    {
        const long ticksPerMicrosecond =
            TimeSpan.TicksPerMillisecond / 1000;
        return new(
            value.Ticks - (value.Ticks % ticksPerMicrosecond),
            value.Offset);
    }
}

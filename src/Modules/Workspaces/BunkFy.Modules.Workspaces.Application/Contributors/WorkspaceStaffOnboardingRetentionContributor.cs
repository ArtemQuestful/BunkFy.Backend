namespace BunkFy.Modules.Workspaces.Application.Contributors;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal sealed class WorkspaceStaffOnboardingRetentionContributor
    : IRetentionExecutionContributor
{
    private readonly IRequestDispatcher dispatcher;
    private readonly WorkspaceStaffOnboardingRetentionOptions options;
    private readonly ISystemClock clock;
    private readonly ILogger<WorkspaceStaffOnboardingRetentionContributor> logger;

    public WorkspaceStaffOnboardingRetentionContributor(
        IRequestDispatcher dispatcher,
        IOptions<WorkspaceStaffOnboardingRetentionOptions> options,
        ISystemClock clock,
        ILogger<WorkspaceStaffOnboardingRetentionContributor> logger)
    {
        this.dispatcher = dispatcher;
        this.options = options.Value;
        this.clock = clock;
        this.logger = logger;
        this.Schedule = new(
            WorkspaceStaffOnboardingRetentionCoordinates.OwnerKey,
            WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
            RetentionTargetScopeKind.Tenant,
            WorkspaceStaffOnboardingRetentionCoordinates.ExecutionPolicyVersion,
            this.options.Interval,
            maxAttempts: 3,
            executionTimeout: TimeSpan.FromMinutes(10));
    }

    public RetentionScheduleDescriptor Schedule { get; }

    public async Task<RetentionContributionResult> ExecuteAsync(
        RetentionContributionRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = this.clock.UtcNow;
        if (!IsExpectedRequest(request))
        {
            return Result(
                RetentionContributionStatus.Failed,
                0,
                0,
                0,
                WorkspaceStaffOnboardingRetentionCoordinates.CoordinateInvalidOutcome,
                nowUtc);
        }

        Result<WorkspaceStaffOnboardingRetentionExecutionStart> started =
            await this.dispatcher.SendAsync(
                new BeginWorkspaceStaffOnboardingRetentionExecutionCommand(
                    request),
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffOnboardingRetentionExecutionStart start = Require(started);
        if (!start.DispatchRequired)
        {
            return start.CompletedResult!;
        }

        Result<IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate>> listed =
            await this.dispatcher.SendAsync(
                new ListWorkspaceStaffOnboardingRetentionCandidatesCommand(
                    request.ExecutionId,
                    request.Attempt,
                    nowUtc - this.options.GracePeriod,
                    checked(this.options.BatchSize + 1)),
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate> eligible =
            Require(listed);
        WorkspaceStaffOnboardingRetentionCandidate[] batch =
            eligible.Take(this.options.BatchSize).ToArray();

        int attemptedCount = 0;
        bool authorityLapsed = false;
        foreach (WorkspaceStaffOnboardingRetentionCandidate candidate in batch)
        {
            attemptedCount++;
            Result<WorkspaceStaffOnboardingRetentionReconciliation> reconciled =
                await this.dispatcher.SendAsync(
                    new ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand(
                        request.ExecutionId,
                        request.Attempt,
                        candidate.ApplicationId,
                        candidate.ApplicationVersion),
                    cancellationToken).ConfigureAwait(false);
            if (reconciled.IsFailure)
            {
                this.logger.LogWarning(
                    "Workspace Staff onboarding staging reconciliation failed with {ErrorCode}.",
                    reconciled.Error.Code);
                return await this.CompleteAsync(
                    request,
                    WorkspaceStaffOnboardingRetentionExecutionState.Failed,
                    checked(start.ScannedCount + attemptedCount),
                    remainingCount: 1,
                    WorkspaceStaffOnboardingRetentionCoordinates
                        .ReconciliationFailedOutcome,
                    cancellationToken).ConfigureAwait(false);
            }

            authorityLapsed |= reconciled.Value.Outcome ==
                WorkspaceStaffOnboardingRetentionOutcome.AuthorityLapsed;
        }

        int remainingCount = eligible.Count > this.options.BatchSize ||
            authorityLapsed
                ? 1
                : 0;
        if (authorityLapsed)
        {
            return await this.CompleteAsync(
                request,
                WorkspaceStaffOnboardingRetentionExecutionState.Failed,
                checked(start.ScannedCount + attemptedCount),
                remainingCount,
                WorkspaceStaffOnboardingRetentionCoordinates.AuthorityLapsedOutcome,
                cancellationToken).ConfigureAwait(false);
        }

        return await this.CompleteAsync(
            request,
            WorkspaceStaffOnboardingRetentionExecutionState.Completed,
            checked(start.ScannedCount + attemptedCount),
            remainingCount,
            remainingCount > 0
                ? WorkspaceStaffOnboardingRetentionCoordinates.BacklogOutcome
                : WorkspaceStaffOnboardingRetentionCoordinates.CompletedOutcome,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsExpectedRequest(RetentionContributionRequest request) =>
        request.ContractVersion == RetentionExecutionContract.CurrentVersion &&
        request.ExecutionId != Guid.Empty &&
        request.PropertyId is null &&
        request.Attempt > 0 &&
        request.StartedAtUtc != default &&
        request.DeadlineUtc > request.StartedAtUtc &&
        request.ExecutionPolicyVersion ==
            WorkspaceStaffOnboardingRetentionCoordinates.ExecutionPolicyVersion &&
        string.Equals(
            request.OwnerKey,
            WorkspaceStaffOnboardingRetentionCoordinates.OwnerKey,
            StringComparison.Ordinal) &&
        string.Equals(
            request.DataClassKey,
            WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
            StringComparison.Ordinal) &&
        Guid.TryParse(request.TenantId, out _);

    private async Task<RetentionContributionResult> CompleteAsync(
        RetentionContributionRequest request,
        WorkspaceStaffOnboardingRetentionExecutionState state,
        int scannedCount,
        int remainingCount,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        Result<RetentionContributionResult> completed =
            await this.dispatcher.SendAsync(
                new CompleteWorkspaceStaffOnboardingRetentionExecutionCommand(
                    request.ExecutionId,
                    request.Attempt,
                    state,
                    scannedCount,
                    remainingCount,
                    outcomeCode,
                    this.clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
        return Require(completed);
    }

    private static T Require<T>(Result<T> result) => result.IsSuccess
        ? result.Value
        : throw new InvalidOperationException(
            $"{result.Error.Code}: {result.Error.Message}");

    private static RetentionContributionResult Result(
        RetentionContributionStatus status,
        int scannedCount,
        int affectedCount,
        int remainingCount,
        string outcomeCode,
        DateTimeOffset completedAtUtc) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            status,
            scannedCount,
            affectedCount,
            remainingCount,
            outcomeCode,
            completedAtUtc);
}

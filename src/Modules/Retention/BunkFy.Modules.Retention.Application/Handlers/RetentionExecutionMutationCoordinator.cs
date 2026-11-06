namespace BunkFy.Modules.Retention.Application.Handlers;

using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Scoping;

internal sealed record RetentionExecutionStartLease(
    bool TargetAvailable,
    bool ScheduleAvailable,
    RetentionExecution? Execution);

internal sealed class RetentionExecutionMutationCoordinator(
    IRetentionMutationLock mutationLock,
    IRetentionExecutionRepository executions,
    IRetentionScheduleHealthReader scheduleHealth,
    IRetentionScopeRepository scopes,
    IScopeContext scopeContext)
{
    public async Task<RetentionExecutionStartLease> AcquireStartAsync(
        BeginRetentionExecutionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string tenantId = this.RequireTenant(command.TenantId);
        ValidateTarget(command.TargetScopeKind, command.PropertyId);

        await mutationLock.AcquireTenantTargetReadAsync(
                tenantId,
                cancellationToken)
            .ConfigureAwait(false);
        if (command.PropertyId is Guid propertyId)
        {
            await mutationLock.AcquirePropertyTargetReadAsync(
                    tenantId,
                    propertyId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await mutationLock.AcquireExecutionAsync(
                tenantId,
                command.ExecutionId,
                cancellationToken)
            .ConfigureAwait(false);
        await mutationLock.AcquireScheduleAsync(
                tenantId,
                command.OwnerKey,
                command.DataClassKey,
                command.PropertyId,
                command.ExecutionPolicyVersion,
                cancellationToken)
            .ConfigureAwait(false);

        bool targetAvailable = await scopes.IsActiveTargetAsync(
                command.TargetScopeKind,
                command.PropertyId,
                cancellationToken)
            .ConfigureAwait(false);
        RetentionExecution? execution = targetAvailable
            ? await executions.GetAsync(
                    command.ExecutionId,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        RetentionScheduleStateSnapshot? schedule = targetAvailable
            ? await scheduleHealth.GetAsync(
                    tenantId,
                    command.OwnerKey,
                    command.DataClassKey,
                    command.PropertyId,
                    command.ExecutionPolicyVersion,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        bool scheduleAvailable = schedule is null ||
            schedule.State != (int)RetentionExecutionState.Running ||
            schedule.LastExecutionId == command.ExecutionId;
        return new(targetAvailable, scheduleAvailable, execution);
    }

    public async Task<RetentionExecution?> AcquireCompletionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        string tenantId = this.RequireTenant(expectedTenantId: null);
        await mutationLock.AcquireExecutionAsync(
                tenantId,
                executionId,
                cancellationToken)
            .ConfigureAwait(false);

        RetentionExecution? execution = await executions.GetAsync(
                executionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (execution is null)
        {
            return null;
        }

        await mutationLock.AcquireScheduleAsync(
                tenantId,
                execution.OwnerKey,
                execution.DataClassKey,
                execution.PropertyId,
                execution.ExecutionPolicyVersion,
                cancellationToken)
            .ConfigureAwait(false);
        return execution;
    }

    private string RequireTenant(string? expectedTenantId)
    {
        string tenantId = scopeContext.IsEnabled
            ? scopeContext.ScopeId?.Trim() ?? string.Empty
            : string.Empty;
        string expected = expectedTenantId?.Trim() ?? tenantId;
        if (tenantId.Length == 0 ||
            !string.Equals(tenantId, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A Retention execution mutation requires the current tenant scope.");
        }

        return tenantId;
    }

    private static void ValidateTarget(
        RetentionTargetScopeKind targetScopeKind,
        Guid? propertyId)
    {
        bool valid = targetScopeKind switch
        {
            RetentionTargetScopeKind.Tenant => propertyId is null,
            RetentionTargetScopeKind.Property =>
                propertyId is not null && propertyId != Guid.Empty,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException(
                "A Retention execution mutation requires a valid target coordinate.");
        }
    }
}

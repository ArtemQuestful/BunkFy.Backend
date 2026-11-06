namespace BunkFy.Modules.Retention.Tests.Application;

using System.Reflection;
using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Application.Errors;
using BunkFy.Modules.Retention.Application.Handlers;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionMutationCoordinatorTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_locks_target_execution_and_schedule_before_reload()
    {
        Guid propertyId = Guid.Parse(
            "10000000-0000-0000-0000-000000000001");
        Guid executionId = Guid.Parse(
            "20000000-0000-0000-0000-000000000001");
        List<string> calls = [];
        RetentionExecution execution = CreateExecution(executionId, propertyId);
        RetentionExecutionMutationCoordinator coordinator = new(
            new RecordingMutationLock(calls),
            new RecordingExecutionRepository(execution, calls),
            new RecordingScheduleHealthReader(snapshot: null, calls),
            new RecordingScopeRepository(calls),
            new TestScopeContext());

        RetentionExecutionStartLease lease = await coordinator.AcquireStartAsync(
            CreateStartCommand(executionId, propertyId),
            CancellationToken.None);

        Assert.True(lease.TargetAvailable);
        Assert.True(lease.ScheduleAvailable);
        Assert.Same(execution, lease.Execution);
        Assert.Equal(
            [
                "tenant-read",
                $"property-read:{propertyId:N}",
                $"execution:{executionId:N}",
                $"schedule:guests:guest-operational:{propertyId:N}:1",
                "target-read",
                $"execution-read:{executionId:N}",
                $"schedule-read:{propertyId:N}"
            ],
            calls);
    }

    [Fact]
    public async Task Start_rejects_a_different_execution_while_the_schedule_is_running()
    {
        Guid propertyId = Guid.Parse(
            "10000000-0000-0000-0000-000000000004");
        Guid executionId = Guid.Parse(
            "20000000-0000-0000-0000-000000000004");
        Guid activeExecutionId = Guid.Parse(
            "30000000-0000-0000-0000-000000000004");
        List<string> calls = [];
        RecordingExecutionRepository executions = new(null, calls);
        RetentionExecutionMutationCoordinator coordinator = new(
            new RecordingMutationLock(calls),
            executions,
            new RecordingScheduleHealthReader(
                RunningSchedule(activeExecutionId, propertyId),
                calls),
            new RecordingScopeRepository(calls),
            new TestScopeContext());
        BeginRetentionExecutionCommandHandler handler = new(
            coordinator,
            executions,
            new NoopScheduleStateRepository());

        Result<RetentionExecutionStart> result = await handler.HandleAsync(
            CreateStartCommand(executionId, propertyId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(RetentionApplicationErrors.ExecutionConflict, result.Error);
    }

    [Fact]
    public async Task Completion_locks_execution_then_authoritative_schedule()
    {
        Guid propertyId = Guid.Parse(
            "10000000-0000-0000-0000-000000000002");
        Guid executionId = Guid.Parse(
            "20000000-0000-0000-0000-000000000002");
        List<string> calls = [];
        RetentionExecution execution = CreateExecution(executionId, propertyId);
        RetentionExecutionMutationCoordinator coordinator = new(
            new RecordingMutationLock(calls),
            new RecordingExecutionRepository(execution, calls),
            new RecordingScheduleHealthReader(snapshot: null, calls),
            new RecordingScopeRepository(calls),
            new TestScopeContext());

        RetentionExecution? result = await coordinator.AcquireCompletionAsync(
            executionId,
            CancellationToken.None);

        Assert.Same(execution, result);
        Assert.Equal(
            [
                $"execution:{executionId:N}",
                $"execution-read:{executionId:N}",
                $"schedule:guests:guest-operational:{propertyId:N}:1"
            ],
            calls);
    }

    [Fact]
    public async Task Reclaimed_lease_replays_terminal_execution()
    {
        Guid propertyId = Guid.Parse(
            "10000000-0000-0000-0000-000000000004");
        Guid executionId = Guid.Parse(
            "20000000-0000-0000-0000-000000000004");
        List<string> calls = [];
        RetentionExecution execution = CreateExecution(
            executionId,
            propertyId);
        Assert.True(execution.Complete(
            RetentionExecutionState.Completed,
            attempt: 1,
            scannedCount: 2,
            affectedCount: 1,
            remainingCount: 0,
            "guests.guest-operational.completed",
            Now.AddMinutes(1),
            holdReviewDueAtUtc: null).IsSuccess);
        RecordingExecutionRepository executions = new(
            execution,
            calls);
        RetentionExecutionMutationCoordinator coordinator = new(
            new RecordingMutationLock(calls),
            executions,
            new RecordingScheduleHealthReader(snapshot: null, calls),
            new RecordingScopeRepository(calls),
            new TestScopeContext());
        RecordingScheduleStateRepository schedules = new();
        BeginRetentionExecutionCommandHandler handler = new(
            coordinator,
            executions,
            schedules);
        BeginRetentionExecutionCommand reclaimed =
            CreateStartCommand(executionId, propertyId) with
            {
                Attempt = 2,
                StartedAtUtc = Now.AddMinutes(2),
                DeadlineUtc = Now.AddMinutes(7)
            };

        Result<RetentionExecutionStart> result =
            await handler.HandleAsync(
                reclaimed,
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.DispatchRequired);
        Assert.Equal(
            RetentionExecutionState.Completed,
            result.Value.State);
        Assert.Equal(1, result.Value.Request.Attempt);
        Assert.Equal(1, execution.Attempt);
        Assert.Equal(0, schedules.StartedCount);
    }

    [Fact]
    public async Task Property_projection_locks_before_merge()
    {
        Guid propertyId = Guid.Parse(
            "10000000-0000-0000-0000-000000000003");
        List<string> calls = [];
        RetentionScopeMutationCoordinator coordinator = new(
            new RecordingMutationLock(calls),
            new RecordingScopeRepository(calls),
            new TestScopeContext());

        await coordinator.ApplyPropertyPolicyAsync(
            new(TenantId, propertyId, true, 2, 3),
            CancellationToken.None);

        Assert.Equal(
            [
                $"property-write:{propertyId:N}",
                $"property-policy:{propertyId:N}:3"
            ],
            calls);
    }

    [Fact]
    public async Task Cross_scope_start_fails_before_locking()
    {
        List<string> calls = [];
        RetentionExecutionMutationCoordinator coordinator = new(
            new RecordingMutationLock(calls),
            new RecordingExecutionRepository(null, calls),
            new RecordingScheduleHealthReader(snapshot: null, calls),
            new RecordingScopeRepository(calls),
            new TestScopeContext());
        BeginRetentionExecutionCommand command = CreateStartCommand(
            Guid.NewGuid(),
            Guid.NewGuid()) with
        {
            TenantId = "tenant-b"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AcquireStartAsync(command, CancellationToken.None));

        Assert.Empty(calls);
    }

    [Fact]
    public void All_online_writers_require_their_mutation_coordinator()
    {
        AssertCoordinator<RetentionExecutionMutationCoordinator>(
            typeof(BeginRetentionExecutionCommandHandler),
            typeof(CompleteRetentionExecutionCommandHandler));
        AssertCoordinator<RetentionScopeMutationCoordinator>(
            typeof(RetentionOrganizationChangedHandler),
            typeof(RetentionPropertyCreatedHandler),
            typeof(RetentionPropertyUpdatedHandler),
            typeof(RetentionPropertyRetiredHandler),
            typeof(RetentionPropertyProcessingPolicyActivatedHandler),
            typeof(RetentionPropertyProcessingSuspendedHandler));
    }

    private static void AssertCoordinator<TCoordinator>(params Type[] writerTypes)
    {
        foreach (Type writerType in writerTypes)
        {
            bool found = writerType
                .GetConstructors(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(TCoordinator));
            Assert.True(
                found,
                $"{writerType.Name} must serialize through " +
                $"{typeof(TCoordinator).Name}.");
        }
    }

    private static BeginRetentionExecutionCommand CreateStartCommand(
        Guid executionId,
        Guid propertyId) => new(
        executionId,
        TenantId,
        "guests",
        "guest-operational",
        RetentionTargetScopeKind.Property,
        propertyId,
        1,
        1,
        Now,
        Now.AddMinutes(5),
        Now.AddHours(1));

    private static RetentionExecution CreateExecution(
        Guid executionId,
        Guid propertyId) => RetentionExecution.Start(
        executionId,
        TenantId,
        "guests",
        "guest-operational",
        RetentionExecutionTargetKind.Property,
        propertyId,
        1,
        1,
        Now,
        Now.AddMinutes(5)).Value;

    private static RetentionScheduleStateSnapshot RunningSchedule(
        Guid executionId,
        Guid propertyId) => new(
            "guests",
            "guest-operational",
            propertyId,
            ExecutionPolicyVersion: 1,
            Version: 1,
            State: (int)RetentionExecutionState.Running,
            LastExecutionId: executionId,
            LastStartedAtUtc: Now,
            LastCompletedAtUtc: null,
            NextDueAtUtc: Now.AddHours(1),
            ConsecutiveFailures: 0,
            LastScannedCount: null,
            LastAffectedCount: null,
            LastRemainingCount: null,
            OutcomeCode: null,
            HoldReviewDueAtUtc: null,
            Retry: null);

    private sealed class RecordingMutationLock(List<string> calls)
        : IRetentionMutationLock
    {
        public Task AcquireTenantTargetReadAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            calls.Add("tenant-read");
            return Task.CompletedTask;
        }

        public Task AcquireTenantTargetWriteAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            calls.Add("tenant-write");
            return Task.CompletedTask;
        }

        public Task AcquirePropertyTargetReadAsync(
            string tenantId,
            Guid propertyId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            calls.Add($"property-read:{propertyId:N}");
            return Task.CompletedTask;
        }

        public Task AcquirePropertyTargetWriteAsync(
            string tenantId,
            Guid propertyId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            calls.Add($"property-write:{propertyId:N}");
            return Task.CompletedTask;
        }

        public Task AcquireExecutionAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            calls.Add($"execution:{executionId:N}");
            return Task.CompletedTask;
        }

        public Task AcquireScheduleAsync(
            string tenantId,
            string ownerKey,
            string dataClassKey,
            Guid? propertyId,
            int executionPolicyVersion,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            calls.Add(
                $"schedule:{ownerKey}:{dataClassKey}:" +
                $"{propertyId:N}:{executionPolicyVersion}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutionRepository(
        RetentionExecution? execution,
        List<string> calls)
        : IRetentionExecutionRepository
    {
        public Task AddAsync(
            RetentionExecution value,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RetentionExecution?> GetAsync(
            Guid executionId,
            CancellationToken cancellationToken)
        {
            calls.Add($"execution-read:{executionId:N}");
            return Task.FromResult(
                execution?.Id == executionId ? execution : null);
        }
    }

    private sealed class RecordingScheduleHealthReader(
        RetentionScheduleStateSnapshot? snapshot,
        List<string> calls)
        : IRetentionScheduleHealthReader
    {
        public Task<RetentionScheduleStateSnapshot?> GetAsync(
            string tenantId,
            string ownerKey,
            string dataClassKey,
            Guid? propertyId,
            int executionPolicyVersion,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            Assert.Equal("guests", ownerKey);
            Assert.Equal("guest-operational", dataClassKey);
            Assert.Equal(1, executionPolicyVersion);
            calls.Add($"schedule-read:{propertyId:N}");
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<RetentionScheduleStateSnapshot>> ListAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RetentionScheduleStateSnapshot?> GetByLastExecutionIdAsync(
            string tenantId,
            Guid lastExecutionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoopScheduleStateRepository
        : IRetentionScheduleStateRepository
    {
        public Task RecordStartedAsync(
            RetentionExecution execution,
            DateTimeOffset nextDueAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordCompletedAsync(
            RetentionExecution execution,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingScopeRepository(List<string> calls)
        : IRetentionScopeRepository
    {
        public Task ApplyOrganizationAsync(
            RetentionOrganizationWriteModel organization,
            CancellationToken cancellationToken)
        {
            calls.Add($"organization:{organization.SourceVersion}");
            return Task.CompletedTask;
        }

        public Task ApplyPropertyTopologyAsync(
            RetentionPropertyTopologyWriteModel property,
            CancellationToken cancellationToken)
        {
            calls.Add($"property-topology:{property.PropertyId:N}:" +
                property.SourceVersion);
            return Task.CompletedTask;
        }

        public Task ApplyPropertyPolicyAsync(
            RetentionPropertyPolicyWriteModel property,
            CancellationToken cancellationToken)
        {
            calls.Add($"property-policy:{property.PropertyId:N}:" +
                property.SourceVersion);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetentionScheduleTarget>> ListActiveTargetsAsync(
            RetentionTargetScopeKind targetKind,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetentionScheduleTarget>>([]);

        public Task<IReadOnlyList<RetentionScheduleTarget>>
            ListCurrentActiveTargetsAsync(
                RetentionTargetScopeKind targetKind,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetentionScheduleTarget>>([]);

        public Task<bool> IsActiveTargetAsync(
            RetentionTargetScopeKind targetKind,
            Guid? propertyId,
            CancellationToken cancellationToken)
        {
            calls.Add("target-read");
            return Task.FromResult(true);
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class RecordingScheduleStateRepository
        : IRetentionScheduleStateRepository
    {
        public int StartedCount { get; private set; }

        public Task RecordStartedAsync(
            RetentionExecution execution,
            DateTimeOffset nextDueAtUtc,
            CancellationToken cancellationToken)
        {
            this.StartedCount++;
            return Task.CompletedTask;
        }

        public Task RecordCompletedAsync(
            RetentionExecution execution,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

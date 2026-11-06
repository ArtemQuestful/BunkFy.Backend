namespace BunkFy.Modules.Workspaces.Tests.Application;

using System.Runtime.CompilerServices;
using System.Text.Json;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Application.Tasks;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffIdentityAnchorSweepRuntimeWiringTests
{
    private const string FirstTenantId =
        "abcdef00-0000-0000-0000-000000000001";
    private const string SecondTenantId =
        "abcdef00-0000-0000-0000-000000000002";

    [Fact]
    public void Descriptor_and_task_services_expose_the_same_runtime_contract()
    {
        ModuleTaskDescriptor descriptor = Assert.Single(
            WorkspacesModuleMetadata.Descriptor.GetTasks(),
            task => task.Name ==
                ReconcileWorkspaceStaffIdentityAnchorsPayload.TaskName);
        Assert.Equal(
            ReconcileWorkspaceStaffIdentityAnchorsPayload.WorkerGroup,
            descriptor.WorkerGroup);
        Assert.Equal(ModuleTaskKind.Recurring, descriptor.Kind);
        Assert.Equal(
            ReconcileWorkspaceStaffIdentityAnchorsPayload.PayloadVersion,
            descriptor.PayloadVersion);
        Assert.True(descriptor.IsScopeAware());

        ServiceCollection services = new();
        services.AddSingleton<ITaskCommandDispatcher,
            StubTaskCommandDispatcher>();
        services.AddSingleton<
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            StubOutcomeReader>();
        services.AddSingleton<
            IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder,
            StubResolutionRecorder>();
        services.AddSingleton<IIdGenerator, StubIdGenerator>();
        services.AddSingleton<IScopeContext>(
            new TestScopeContext(FirstTenantId));
        services.AddSingleton<IWorkspaceStaffIdentityAnchorSweepRepository>(
            new StubSweepRepository([FirstTenantId]));
        services.AddWorkspacesTaskHandlers();
        services.AddWorkspacesTaskHandlers();

        using ServiceProvider provider = services.BuildServiceProvider();
        TaskHandlerRegistration registration = provider
            .GetRequiredService<ITaskHandlerRegistry>()
            .Find(
                WorkspacesModuleMetadata.Name,
                ReconcileWorkspaceStaffIdentityAnchorsPayload.TaskName,
                ReconcileWorkspaceStaffIdentityAnchorsPayload.PayloadVersion)!;
        Assert.Equal(
            typeof(WorkspaceStaffIdentityAnchorSweepTaskHandler),
            registration.HandlerType);
        Assert.Equal(descriptor.WorkerGroup, registration.WorkerGroup);
        Assert.True(registration.IsScopeAware());

        using IServiceScope scope = provider.CreateScope();
        Assert.IsType<WorkspaceStaffIdentityAnchorSweepTaskHandler>(
            scope.ServiceProvider.GetRequiredService(
                registration.HandlerType));
        Assert.Single(
            scope.ServiceProvider.GetServices<ITaskScheduleProvider>(),
            schedule => schedule is
                WorkspaceStaffIdentityAnchorSweepScheduleProvider);
    }

    [Fact]
    public async Task Schedule_provider_emits_claimable_tenant_scoped_runs()
    {
        WorkspaceStaffIdentityAnchorSweepScheduleProvider provider = new(
            new StubSweepRepository([FirstTenantId, SecondTenantId]));

        List<ScheduledTaskDefinition> schedules = [];
        await foreach (ScheduledTaskDefinition schedule in provider
            .GetSchedulesAsync(CancellationToken.None))
        {
            schedules.Add(schedule);
        }

        Assert.Equal(
            [FirstTenantId, SecondTenantId],
            schedules.Select(schedule => schedule.ScopeId));
        Assert.All(schedules, schedule =>
        {
            Assert.Equal(WorkspacesModuleMetadata.Name, schedule.ModuleName);
            Assert.Equal(
                ReconcileWorkspaceStaffIdentityAnchorsPayload.TaskName,
                schedule.TaskName);
            Assert.Equal(
                ReconcileWorkspaceStaffIdentityAnchorsPayload.WorkerGroup,
                schedule.WorkerGroup);
            Assert.Equal(TimeSpan.FromMinutes(5), schedule.Interval);
            Assert.Equal(5, schedule.MaxAttempts);
            Assert.True(schedule.RunOnStart);
            ReconcileWorkspaceStaffIdentityAnchorsPayload payload =
                JsonSerializer.Deserialize<
                    ReconcileWorkspaceStaffIdentityAnchorsPayload>(
                    schedule.PayloadJson)!;
            Assert.Equal(
                ReconcileWorkspaceStaffIdentityAnchorsPayload
                    .DefaultBatchSize,
                payload.BatchSize);
            Assert.Equal(
                ReconcileWorkspaceStaffIdentityAnchorsPayload
                    .DefaultMaxBatches,
                payload.MaxBatches);
        });
    }

    [Fact]
    public async Task Noncanonical_query_scope_fails_without_reading_status()
    {
        string noncanonical = FirstTenantId.ToUpperInvariant();
        StubSweepRepository repository = new([]);
        GetWorkspaceStaffIdentityAnchorSweepStatusQueryHandler handler = new(
            repository,
            new TestScopeContext(noncanonical));

        Result<WorkspaceStaffIdentityAnchorSweepStatus> result =
            await handler.HandleAsync(
                new GetWorkspaceStaffIdentityAnchorSweepStatusQuery(),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.ScopeRequired.Code,
            result.Error.Code);
        Assert.Equal(0, repository.StatusReadCount);
    }

    [Fact]
    public async Task Noncanonical_prepare_scope_fails_without_starting_cycle()
    {
        string noncanonical = FirstTenantId.ToUpperInvariant();
        StubSweepRepository repository = new([]);
        RecordingWorkspaceCrossGraphMutationLock crossGraphLock = new();
        PrepareWorkspaceStaffIdentityAnchorSweepPageCommandHandler handler =
            new(
                repository,
                crossGraphLock,
                new TestScopeContext(noncanonical),
                new TestClock());

        Result<WorkspaceStaffIdentityAnchorSweepPage> result =
            await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ReconcileWorkspaceStaffIdentityAnchorsPayload
                        .DefaultBatchSize),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.ScopeRequired.Code,
            result.Error.Code);
        Assert.Equal(0, repository.PrepareCount);
        Assert.Equal(0, crossGraphLock.AcquireCount);
    }

    [Fact]
    public void Persistence_registers_repository_and_exposes_checkpoint_set()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            "Host=localhost;Database=unused;Username=unused;Password=unused";

        builder.AddWorkspacesPersistence();

        ServiceDescriptor registration = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType ==
                typeof(IWorkspaceStaffIdentityAnchorSweepRepository));
        Assert.Equal(
            "WorkspaceStaffIdentityAnchorSweepRepository",
            registration.ImplementationType?.Name);
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.NotNull(typeof(WorkspacesDbContext).GetProperty(
            nameof(WorkspacesDbContext.StaffIdentityAnchorSweepCheckpoints)));
    }

    private sealed class StubSweepRepository(
        IReadOnlyList<string> scheduleScopes)
        : IWorkspaceStaffIdentityAnchorSweepRepository
    {
        public int PrepareCount { get; private set; }
        public int StatusReadCount { get; private set; }

        public Task<Result<WorkspaceStaffIdentityAnchorSweepPage>>
            PreparePageAsync(
                string scopeId,
                Guid checkpointId,
                Guid cycleId,
                Guid emptyAdvanceId,
                Guid runId,
                int batchSize,
                DateTimeOffset nowUtc,
                CancellationToken cancellationToken)
        {
            this.PrepareCount++;
            throw new InvalidOperationException("Unexpected prepare.");
        }

        public Task<Result> AdvanceAsync(
            WorkspaceStaffIdentityAnchorSweepAdvance advance,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected advance.");

        public Task<WorkspaceStaffIdentityAnchorSweepStatus> GetStatusAsync(
            string scopeId,
            CancellationToken cancellationToken)
        {
            this.StatusReadCount++;
            throw new InvalidOperationException("Unexpected status read.");
        }

        public async IAsyncEnumerable<string> StreamScheduleScopeIdsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string scopeId in scheduleScopes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return scopeId;
            }
        }
    }

    private sealed class StubTaskCommandDispatcher : ITaskCommandDispatcher
    {
        public Task<Result<TResponse>> DispatchAsync<TCommand, TResponse>(
            TaskExecutionContext context,
            TCommand command,
            CancellationToken cancellationToken)
            where TCommand : ICommand<TResponse> =>
            throw new InvalidOperationException("Unexpected dispatch.");
    }

    private sealed class StubOutcomeReader
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
    {
        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
                IReadOnlyList<
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                        requests,
                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected outcome read.");
    }

    private sealed class StubResolutionRecorder
        : IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder
    {
        public Task<StaffWorkspaceOnboardingIdentityAnchorResolutionResult>
            RecordAsync(
                StaffWorkspaceOnboardingIdentityAnchorResolutionRequest
                    request,
                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected record.");
    }

    private sealed class StubIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    }
}

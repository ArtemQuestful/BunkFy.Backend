namespace Integration.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Tasks;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.UnitOfWork;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Scoping.Infrastructure;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class
    WorkspaceStaffIdentityAnchorSweepTransactionIntegrationTests
{
    private const string TenantId =
        "b9000000-0000-0000-0000-000000000001";
    private const string SubjectId = "account-sweep-poison";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 18, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Failed_candidate_scope_is_disposed_before_checkpoint_advance()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_sweep_scope_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        PoisonMutationProbe poison = new();
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            poison);
        Guid applicationId = Guid.NewGuid();
        await CreateSchemaAndSeedAsync(services, applicationId)
            .ConfigureAwait(false);

        await using (AsyncServiceScope executionScope =
            services.CreateAsyncScope())
        {
            IScopeContextAccessor scopeContext = executionScope
                .ServiceProvider
                .GetRequiredService<IScopeContextAccessor>();
            scopeContext.SetScope(TenantId);
            try
            {
                TaskHandlerRegistration registration = services
                    .GetRequiredService<ITaskHandlerRegistry>()
                    .Find(
                        WorkspacesModuleMetadata.Name,
                        ReconcileWorkspaceStaffIdentityAnchorsPayload.TaskName,
                        ReconcileWorkspaceStaffIdentityAnchorsPayload
                            .PayloadVersion)!;
                ITaskHandler<ReconcileWorkspaceStaffIdentityAnchorsPayload>
                    handler = Assert.IsType<ITaskHandler<
                        ReconcileWorkspaceStaffIdentityAnchorsPayload>>(
                            executionScope.ServiceProvider
                                .GetRequiredService(registration.HandlerType),
                            exactMatch: false);
                await handler.HandleAsync(
                    new(BatchSize: 10, MaxBatches: 1),
                    new TaskExecutionContext(
                        Guid.NewGuid(),
                        WorkspacesModuleMetadata.Name,
                        ReconcileWorkspaceStaffIdentityAnchorsPayload.TaskName,
                        ReconcileWorkspaceStaffIdentityAnchorsPayload
                            .WorkerGroup,
                        "worker-1",
                        "node-1",
                        attempt: 1,
                        scopeId: TenantId),
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                scopeContext.ClearScope();
            }
        }

        await using AsyncServiceScope verificationScope =
            services.CreateAsyncScope();
        IScopeContextAccessor verificationContext = verificationScope
            .ServiceProvider
            .GetRequiredService<IScopeContextAccessor>();
        verificationContext.SetScope(TenantId);
        WorkspacesDbContext database = verificationScope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding application = await database
            .StaffOnboardingApplications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == applicationId)
            .ConfigureAwait(false);
        WorkspaceStaffIdentityAnchorSweepCheckpoint checkpoint =
            await database.StaffIdentityAnchorSweepCheckpoints
                .AsNoTracking()
                .SingleAsync()
                .ConfigureAwait(false);

        Assert.Equal(
            WorkspaceStaffOnboardingState.Submitted,
            application.Status);
        Assert.Equal(1, application.Version);
        Assert.False(await database.OutboxMessages.AsNoTracking()
            .AnyAsync(message => message.Id == poison.OutboxId)
            .ConfigureAwait(false));
        Assert.False(checkpoint.HasActiveCycle);
        Assert.Equal(1, checkpoint.LastCompletedScannedCount);
        Assert.Equal(1, checkpoint.LastCompletedConflictCount);
        Assert.Equal(0, checkpoint.LastCompletedDeferredCount);
        Assert.NotNull(checkpoint.LastCompletedCycleId);
        Assert.NotNull(checkpoint.LastCompletedAtUtc);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Cycle_high_water_drains_prior_writer_and_fences_later_writer()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_sweep_barrier_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        PoisonMutationProbe poison = new();
        SweepBarrierProbe barrier = new();
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            poison,
            barrier);
        await MigrateActualSchemaAsync(services).ConfigureAwait(false);

        Guid priorApplicationId = Guid.NewGuid();
        await using AsyncServiceScope priorWriter = services.CreateAsyncScope();
        SetScope(priorWriter.ServiceProvider);
        WorkspacesDbContext priorDatabase = priorWriter.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
            priorTransaction = await priorDatabase.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
        WorkspaceStaffOnboarding prior = CreateApplication(
            priorApplicationId,
            "account-sweep-prior");
        priorDatabase.StaffOnboardingApplications.Add(prior);
        await priorDatabase.SaveChangesAsync().ConfigureAwait(false);
        Assert.True(prior.IdentityAnchorSweepOrdinal > 0);

        await using AsyncServiceScope sweepScope = services.CreateAsyncScope();
        SetScope(sweepScope.ServiceProvider);
        IRequestDispatcher dispatcher = sweepScope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>();
        Task<Result<WorkspaceStaffIdentityAnchorSweepPage>> prepareTask =
            dispatcher.SendAsync(
                new PrepareWorkspaceStaffIdentityAnchorSweepPageCommand(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    BatchSize: 10),
                CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(barrier.Acquired.Task.IsCompleted);

        await priorTransaction.CommitAsync().ConfigureAwait(false);
        await barrier.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        Guid laterApplicationId = Guid.NewGuid();
        await using AsyncServiceScope laterWriter = services.CreateAsyncScope();
        SetScope(laterWriter.ServiceProvider);
        WorkspacesDbContext laterDatabase = laterWriter.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding later = CreateApplication(
            laterApplicationId,
            "account-sweep-later");
        laterDatabase.StaffOnboardingApplications.Add(later);
        Task<int> laterSave = laterDatabase.SaveChangesAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(laterSave.IsCompleted);

        barrier.Release.TrySetResult();
        Result<WorkspaceStaffIdentityAnchorSweepPage> prepared =
            await prepareTask.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        Assert.True(prepared.IsSuccess);
        Assert.Equal(
            prior.IdentityAnchorSweepOrdinal,
            prepared.Value.UpperOrdinal);
        WorkspaceStaffIdentityAnchorSweepCandidate selected = Assert.Single(
            prepared.Value.Candidates);
        Assert.Equal(priorApplicationId, selected.ApplicationId);

        await laterSave.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        Assert.True(
            later.IdentityAnchorSweepOrdinal >
            prepared.Value.UpperOrdinal);
        Assert.DoesNotContain(
            prepared.Value.Candidates,
            candidate => candidate.ApplicationId == laterApplicationId);
    }

    private static ServiceProvider CreateProvider(
        string connectionString,
        PoisonMutationProbe poison,
        SweepBarrierProbe? barrier = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Scoping:Enabled"] = "true";
        builder.AddScopingInfrastructure();
        builder.AddApplicationEventsInfrastructure();
        builder.AddTaskCqrs();
        builder.Services.AddDbContext<WorkspacesDbContext>(options =>
            options.UseNpgsql(connectionString, postgres =>
                postgres.MigrationsAssembly(WorkspacesMigrations.PostgreSqlAssembly)));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IUnitOfWork, WorkspacesUnitOfWork>());
        builder.Services.AddScoped<
            IWorkspaceStaffIdentityAnchorSweepRepository,
            WorkspaceStaffIdentityAnchorSweepRepository>();
        builder.Services.AddScoped<WorkspaceCrossGraphMutationLock>();
        builder.Services.AddScoped<IWorkspaceCrossGraphMutationLock>(provider =>
            barrier is null
                ? provider.GetRequiredService<WorkspaceCrossGraphMutationLock>()
                : new PausingCrossGraphMutationLock(
                    provider.GetRequiredService<
                        WorkspaceCrossGraphMutationLock>(),
                    barrier));
        builder.Services.AddScoped<ICommandHandler<
            PrepareWorkspaceStaffIdentityAnchorSweepPageCommand,
            WorkspaceStaffIdentityAnchorSweepPage>,
            PrepareWorkspaceStaffIdentityAnchorSweepPageCommandHandler>();
        builder.Services.AddScoped<ICommandHandler<
            AdvanceWorkspaceStaffIdentityAnchorSweepCommand,
            Unit>,
            AdvanceWorkspaceStaffIdentityAnchorSweepCommandHandler>();
        builder.Services.AddScoped<ICommandHandler<
            ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand,
            WorkspaceStaffIdentityAnchorSweepCandidateResult>,
            MutatingFailureCandidateHandler>();
        builder.Services.AddSingleton(poison);
        builder.Services.AddSingleton<ISystemClock>(new FixedClock());
        builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        builder.Services.AddSingleton<
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            UnresolvedOutcomeReader>();
        builder.Services.AddSingleton<
            IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder,
            UnexpectedResolutionRecorder>();
        builder.Services.AddWorkspacesTaskHandlers();
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task CreateSchemaAndSeedAsync(
        ServiceProvider services,
        Guid applicationId)
    {
        await MigrateActualSchemaAsync(services).ConfigureAwait(false);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        SetScope(scope.ServiceProvider);
        WorkspacesDbContext database = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding application = CreateApplication(
            applicationId,
            SubjectId);
        database.StaffOnboardingApplications.Add(application);
        await database.SaveChangesAsync().ConfigureAwait(false);
        Assert.True(application.IdentityAnchorSweepOrdinal > 0);
    }

    private static async Task MigrateActualSchemaAsync(ServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        SetScope(scope.ServiceProvider);
        WorkspacesDbContext database = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        await database.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static WorkspaceStaffOnboarding CreateApplication(
        Guid applicationId,
        string subjectId) => WorkspaceStaffOnboarding.Create(
            applicationId,
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            subjectId,
            $"{subjectId}@example.test",
            "Sweep Candidate",
            null,
            null,
            null,
            null,
            null,
            null,
            Now).Value;

    private static void SetScope(IServiceProvider provider) => provider
        .GetRequiredService<IScopeContextAccessor>()
        .SetScope(TenantId);

    private sealed class MutatingFailureCandidateHandler(
        WorkspacesDbContext database,
        PoisonMutationProbe poison)
        : ICommandHandler<
            ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand,
            WorkspaceStaffIdentityAnchorSweepCandidateResult>
    {
        public async Task<Result<
            WorkspaceStaffIdentityAnchorSweepCandidateResult>> HandleAsync(
                ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand
                    command,
                CancellationToken cancellationToken)
        {
            WorkspaceStaffOnboarding application = await database
                .StaffOnboardingApplications
                .SingleAsync(
                    candidate => candidate.Id == command.ApplicationId,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert.True(application.ObserveInvitationAccepted(Now).IsSuccess);
            database.OutboxMessages.Add(new OutboxMessage(
                poison.OutboxId,
                "bunkfy.workspaces.sweep-poison.v1",
                "sweep-poison",
                version: 1,
                TenantId,
                Now,
                "{}",
                Now));
            return Result.Failure<
                WorkspaceStaffIdentityAnchorSweepCandidateResult>(
                    new Error(
                        "Workspaces.SweepPoisonFailure",
                        "The injected candidate fails after mutation."));
        }
    }

    private sealed class UnresolvedOutcomeReader
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
    {
        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
                IReadOnlyList<
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                        requests,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>(
                    requests.Select(request => new
                        StaffWorkspaceOnboardingIdentityAnchorOutcome(
                            request.ApplicationId,
                            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                                .Unresolved,
                            Guid.NewGuid(),
                            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                                .Active,
                            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                                .Exact,
                            WorkspaceApplicationVersion: null,
                            ResolutionDisposition: null,
                            Guid.NewGuid()))
                        .ToArray());
    }

    private sealed class UnexpectedResolutionRecorder
        : IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder
    {
        public Task<StaffWorkspaceOnboardingIdentityAnchorResolutionResult>
            RecordAsync(
                StaffWorkspaceOnboardingIdentityAnchorResolutionRequest
                    request,
                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The failed candidate must not request a resolution record.");
    }

    private sealed class PoisonMutationProbe
    {
        public Guid OutboxId { get; } = Guid.NewGuid();
    }

    private sealed class SweepBarrierProbe
    {
        public TaskCompletionSource Acquired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PausingCrossGraphMutationLock(
        IWorkspaceCrossGraphMutationLock inner,
        SweepBarrierProbe probe)
        : IWorkspaceCrossGraphMutationLock
    {
        public async Task AcquireAsync(CancellationToken cancellationToken)
        {
            await inner.AcquireAsync(cancellationToken).ConfigureAwait(false);
            probe.Acquired.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class FixedClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class GuidIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
}

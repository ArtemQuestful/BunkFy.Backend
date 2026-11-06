namespace Integration.Tests.Hosts;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.ProjectionRebuild;
using Gma.Framework.Runtime.Infrastructure;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Infrastructure;
using Gma.Framework.Tenancy;
using Gma.Framework.Tenancy.Infrastructure;
using Gma.Framework.Tenancy.Scoping;
using Gma.Framework.Tenancy.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;
using DomainAcknowledgement = BunkFy.Modules.Properties.Domain.ValueObjects.PropertyGovernanceAcknowledgement;

public sealed class ReservationsPropertyRebuildWorkerIntegrationTests(ITestOutputHelper output)
{
    private static readonly string[] Tenants = ["tenant-a", "tenant-b"];

    [DockerFact]
    [Trait("Category", "Docker")]
    public async Task Actual_task_rebuild_is_owned_and_replay_safe_in_both_registration_orders()
    {
        foreach (bool workspacesFirst in new[] { true, false })
        {
            await using PostgreSqlContainer postgres = Database();
            await postgres.StartAsync();
            output.WriteLine($"PostgreSQL {postgres.Id}; Workspaces first={workspacesFirst}");
            using ServiceProvider provider = CreateProvider(postgres.GetConnectionString(), workspacesFirst);
            await MigrateAndSeedAsync(provider);

            foreach (string tenant in Tenants)
            {
                Guid runId = Guid.NewGuid();
                await ExecuteTaskAsync(provider, tenant, runId);
                await AssertStoredAsync(provider, tenant, [runId]);
                // Fresh scopes model retries without retaining tracked projections/checkpoints.
                await ExecuteTaskAsync(provider, tenant, runId);
                await AssertStoredAsync(provider, tenant, [runId]);
                Guid correlatedRunId = Guid.NewGuid();
                await ExecuteTaskAsync(provider, tenant, correlatedRunId);
                await AssertStoredAsync(provider, tenant, [runId, correlatedRunId]);
                output.WriteLine($"PASS {tenant}: actual handler, same-run replay, new-run rebuild; two projections, two completed checkpoints.");
            }

            await using AsyncServiceScope verify = provider.CreateAsyncScope();
            ReservationsDbContext db = verify.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            var rows = await db.PropertyProjections.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            Assert.Equal(4, rows.Count);
            Assert.All(Tenants, tenant => Assert.Equal(2, rows.Count(row => row.ScopeId == tenant)));
            Assert.Equal(4, await db.ProjectionRebuildCheckpoints.IgnoreQueryFilters().CountAsync());
            Assert.Empty(await verify.ServiceProvider.GetRequiredService<WorkspacesDbContext>()
                .PropertyProjections.IgnoreQueryFilters().ToListAsync());
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    public async Task Real_transaction_rolls_back_projection_and_checkpoint_together()
    {
        await using PostgreSqlContainer postgres = Database();
        await postgres.StartAsync();
        using ServiceProvider provider = CreateProvider(postgres.GetConnectionString(), workspacesFirst: true);
        await MigrateAndSeedAsync(provider);
        foreach (string tenant in Tenants)
        {
            await using (AsyncServiceScope scope = TenantScope(provider, tenant))
            {
                IServiceProvider services = scope.ServiceProvider;
                ReservationsDbContext db = services.GetRequiredService<ReservationsDbContext>();
                ProjectionRebuildRequest request = Request();
                ProjectionReadBatch<PropertyTopologyProjectionExport> batch = await services
                    .GetRequiredService<IPropertiesTopologyProjectionExportSource>()
                    .ReadAsync(request, null, CancellationToken.None);
                var boundary = services.GetServices<IProjectionRebuildTransactionBoundary>()
                    .Single(item => item.ModuleName == ReservationsModuleMetadata.Name);
                var store = services.GetServices<IProjectionRebuildCheckpointStore>()
                    .Single(item => item.ModuleName == ReservationsModuleMetadata.Name);
                var writer = OwnedWriter(services);
                await Assert.ThrowsAsync<CheckpointRollbackProbeException>(() => boundary.ExecuteAsync<int>(async ct =>
                {
                    Assert.NotNull(db.Database.CurrentTransaction);
                    Assert.Null(services.GetRequiredService<WorkspacesDbContext>().Database.CurrentTransaction);
                    ProjectionWriteResult result = await writer.WriteAsync(request, batch.Snapshots, ct);
                    Assert.Equal(2, result.WrittenCount);
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    await store.SaveAsync(new(ReservationsModuleMetadata.Name, Guid.NewGuid(), request.ProjectionName, tenant),
                        ProjectionRebuildCheckpoint.Start(request.ProjectionVersion, now)
                            .Advance(batch.NextCursor, batch.Snapshots.Count, result, now).Complete(now), ct);
                    Assert.Equal(2, await db.PropertyProjections.CountAsync(ct));
                    Assert.Equal(1, await db.ProjectionRebuildCheckpoints.CountAsync(ct));
                    throw new CheckpointRollbackProbeException();
                }, CancellationToken.None));
            }
            await AssertNoWritesAsync(provider);
            output.WriteLine($"PASS {tenant}: actual Reservations transaction held both saved projections and checkpoint; injected pre-commit failure rolled back both.");
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    public async Task Owned_writer_keeps_foreign_scope_and_missing_transaction_guards()
    {
        await using PostgreSqlContainer postgres = Database();
        await postgres.StartAsync();
        using ServiceProvider provider = CreateProvider(postgres.GetConnectionString(), workspacesFirst: false);
        await MigrateAndSeedAsync(provider);
        PropertyTopologyProjectionExport[] foreign;
        await using (AsyncServiceScope read = TenantScope(provider, "tenant-b"))
        {
            foreign = [.. (await read.ServiceProvider.GetRequiredService<IPropertiesTopologyProjectionExportSource>()
                .ReadAsync(Request(), null, CancellationToken.None)).Snapshots];
        }
        await using (AsyncServiceScope write = TenantScope(provider, "tenant-a"))
        {
            IServiceProvider services = write.ServiceProvider;
            var writer = OwnedWriter(services);
            var boundary = services.GetServices<IProjectionRebuildTransactionBoundary>()
                .Single(item => item.ModuleName == ReservationsModuleMetadata.Name);
            InvalidOperationException crossedScope = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                boundary.ExecuteAsync(ct => writer.WriteAsync(Request(), foreign, ct), CancellationToken.None));
            Assert.Equal("A Reservations property projection lock requires valid scoped coordinates.", crossedScope.Message);
            ProjectionReadBatch<PropertyTopologyProjectionExport> own = await services
                .GetRequiredService<IPropertiesTopologyProjectionExportSource>().ReadAsync(Request(), null, CancellationToken.None);
            InvalidOperationException missingTransaction = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                writer.WriteAsync(Request(), own.Snapshots, CancellationToken.None));
            Assert.Equal("A Reservations property projection lock requires an active database transaction.", missingTransaction.Message);
        }
        await AssertNoWritesAsync(provider);
        output.WriteLine("PASS: crossed-scope and missing relational transaction writes rejected; no projection or checkpoint mutations.");
    }

    private static async Task ExecuteTaskAsync(ServiceProvider provider, string tenant, Guid runId)
    {
        await using AsyncServiceScope taskScope = provider.CreateAsyncScope();
        IServiceProvider services = taskScope.ServiceProvider;
        TaskHandlerRegistration registration = Registration(services);
        Assert.True(registration.IsTenantScoped());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TaskRunLease lease = new(runId, registration.ModuleName, registration.TaskName,
            registration.WorkerGroup, "worker-owned-writer", "synthetic-node", "{}", 1, now, now.AddMinutes(2), tenant);
        TaskExecutionContext context = lease.CreateExecutionContext();
        TaskExecutionContextPreparationContext preparation = new(lease, registration, context);
        ITaskExecutionContextContributor contributor = services.GetServices<ITaskExecutionContextContributor>().Single();
        Assert.True((await contributor.PrepareAsync(preparation, CancellationToken.None)).IsSuccess);
        try
        {
            Assert.Equal(tenant, services.GetRequiredService<IScopeContext>().ScopeId);
            Assert.Equal(2, await services.GetRequiredService<PropertiesDbContext>().Properties.CountAsync());
            Assert.Equal(1, await services.GetRequiredService<PropertiesDbContext>().Rooms.CountAsync());
            Assert.Equal("WorkspacePropertiesProjectionRebuildWriter", services
                .GetRequiredService<IProjectionRebuildWriter<PropertyTopologyProjectionExport>>().GetType().Name);
            var handler = (ITaskHandler<RebuildReservationPropertiesPayload>)services.GetRequiredService(registration.HandlerType);
            // Run first so this unchanged success regression also reproduces the old DI transaction failure.
            await handler.HandleAsync(new(), context, CancellationToken.None);
            Assert.Equal("ReservationPropertyProjectionRebuildWriter", OwnedWriter(services).GetType().Name);
        }
        finally
        {
            await contributor.CleanupAsync(preparation, CancellationToken.None);
            Assert.Equal("default", services.GetRequiredService<IScopeContext>().ScopeId);
        }
    }

    private static IProjectionRebuildWriter<PropertyTopologyProjectionExport> OwnedWriter(IServiceProvider services)
    {
        // Inspect the handler's actual injected service, not a parallel test-only writer registration.
        Type writerPort = Registration(services).HandlerType.GetConstructors().Single().GetParameters()[1].ParameterType;
        Assert.Equal("BunkFy.Modules.Reservations.Application.Ports.IReservationPropertyProjectionRebuildWriter", writerPort.FullName);
        return (IProjectionRebuildWriter<PropertyTopologyProjectionExport>)services.GetRequiredService(writerPort);
    }

    private static TaskHandlerRegistration Registration(IServiceProvider services) =>
        services.GetRequiredService<ITaskHandlerRegistry>()
            .Find(ReservationsModuleMetadata.Name, RebuildReservationPropertiesPayload.TaskName)
        ?? throw new InvalidOperationException("Reservations rebuild task is not registered.");

    private static async Task AssertStoredAsync(ServiceProvider provider, string tenant, Guid[] runIds)
    {
        await using AsyncServiceScope verify = TenantScope(provider, tenant);
        IServiceProvider services = verify.ServiceProvider;
        var source = await services.GetRequiredService<IPropertiesTopologyProjectionExportSource>()
            .ReadAsync(Request(), null, CancellationToken.None);
        ReservationsDbContext db = services.GetRequiredService<ReservationsDbContext>();
        var projections = await db.PropertyProjections.AsNoTracking().Include(row => row.GovernancePolicy)
            .ThenInclude(policy => policy!.Acknowledgements).ToListAsync();
        Assert.Equal(2, projections.Count);
        Assert.All(source.Snapshots, expected =>
        {
            ReservationPropertyProjection actual = Assert.Single(projections, row => row.Id == expected.PropertyId);
            Assert.Equal(tenant, actual.ScopeId);
            Assert.True(actual.IsKnown);
            Assert.Equal(expected.TimeZoneId, actual.TimeZoneId);
            Assert.Equal(expected.Status == PropertyStatus.Active, actual.IsActive);
            Assert.Equal(expected.Version, actual.TopologySourceVersion);
            Assert.Equal(expected.Version, actual.PolicySourceVersion);
            Assert.Equal(expected.ProcessingStatus, actual.ProcessingStatus);
            Assert.Equal(expected.GovernancePolicy?.PolicyId, actual.GovernancePolicy?.PolicyId);
            Assert.Equal(expected.GovernancePolicy?.ContentSha256, actual.GovernancePolicy?.ContentSha256);
            if (expected.GovernancePolicy is not null)
            {
                Assert.NotNull(actual.GovernancePolicy);
                Assert.Equal(expected.GovernancePolicy.OperatingCountryCode, actual.GovernancePolicy.OperatingCountryCode);
                Assert.Equal(expected.GovernancePolicy.PolicyVersion, actual.GovernancePolicy.PolicyVersion);
                Assert.Equal(expected.GovernancePolicy.DataRegionId, actual.GovernancePolicy.DataRegionId);
                Assert.Equal(expected.GovernancePolicy.TransferProfileId, actual.GovernancePolicy.TransferProfileId);
                Assert.Equal(expected.GovernancePolicy.RetentionPolicyId, actual.GovernancePolicy.RetentionPolicyId);
                Assert.Equal(expected.GovernancePolicy.RetentionPolicyVersion, actual.GovernancePolicy.RetentionPolicyVersion);
                Assert.Equal(expected.GovernancePolicy.PolicyEffectiveAtUtc, actual.GovernancePolicy.PolicyEffectiveAtUtc);
                Assert.Equal(expected.GovernancePolicy.PolicyExpiresAtUtc, actual.GovernancePolicy.PolicyExpiresAtUtc);
                Assert.Equal(expected.GovernancePolicy.ActivatedAtUtc, actual.GovernancePolicy.ActivatedAtUtc);
                Assert.Equal(expected.GovernancePolicy.Acknowledgements.Select(item => (item.AcknowledgementId, item.AcknowledgementVersion)),
                    actual.GovernancePolicy.Acknowledgements.Select(item => (item.AcknowledgementId, item.AcknowledgementVersion)));
            }
        });
        var checkpoints = await db.ProjectionRebuildCheckpoints.AsNoTracking().ToListAsync();
        Assert.Equal(runIds.Order(), checkpoints.Select(row => row.RunId).Order());
        Assert.All(checkpoints, row =>
        {
            Assert.Equal(tenant, row.ScopeId);
            Assert.Equal(ReservationsModuleMetadata.PropertyProjectionName, row.ProjectionName);
            Assert.Equal(ReservationsModuleMetadata.PropertyProjectionVersion, row.ProjectionVersion);
            Assert.Equal(2, row.ProcessedCount);
            Assert.Equal(2, row.WrittenCount);
            Assert.Equal(0, row.SkippedCount);
            Assert.Equal(0, row.FailedCount);
            Assert.NotNull(row.CompletedAtUtc);
        });
    }

    private static async Task AssertNoWritesAsync(ServiceProvider provider)
    {
        await using AsyncServiceScope verify = provider.CreateAsyncScope();
        ReservationsDbContext db = verify.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        Assert.Equal(4, await verify.ServiceProvider.GetRequiredService<PropertiesDbContext>().Properties.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await db.PropertyProjections.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.ProjectionRebuildCheckpoints.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await verify.ServiceProvider.GetRequiredService<WorkspacesDbContext>().PropertyProjections.IgnoreQueryFilters().ToListAsync());
    }

    private static ProjectionRebuildRequest Request() => new(ReservationsModuleMetadata.PropertyProjectionName,
        ReservationsModuleMetadata.PropertyProjectionVersion, 100);

    private static AsyncServiceScope TenantScope(ServiceProvider provider, string tenant)
    {
        AsyncServiceScope scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenant);
        return scope;
    }

    private static async Task MigrateAndSeedAsync(ServiceProvider provider)
    {
        foreach (string tenant in Tenants)
        {
            await using AsyncServiceScope scope = TenantScope(provider, tenant);
            IServiceProvider services = scope.ServiceProvider;
            PropertiesDbContext db = services.GetRequiredService<PropertiesDbContext>();
            await services.GetRequiredService<WorkspacesDbContext>().Database.MigrateAsync();
            await db.Database.MigrateAsync();
            await services.GetRequiredService<ReservationsDbContext>().Database.MigrateAsync();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Property active = Property.Create(Guid.NewGuid(), tenant, "Active " + tenant, "active-" + tenant, "Etc/UTC", Guid.NewGuid(), now).Value;
            Property retired = Property.Create(Guid.NewGuid(), tenant, "Retired " + tenant, "retired-" + tenant, "Etc/UTC", Guid.NewGuid(), now).Value;
            foreach (Property property in new[] { active, retired })
            {
                await services.GetRequiredService<IPropertyRepository>().AddAsync(property, CancellationToken.None);
                await services.GetRequiredService<IPropertyTimeZoneRevisionWriter>().AppendAsync(
                    new PropertyTimeZoneRevisionWriteModel(Guid.NewGuid(), tenant, property.Id, property.Id,
                        PropertyTimeZoneChangeKind.Created, property.TimeZoneId.Value, null, property.TimeZoneId.Value,
                        BunkFy.TimeZones.TimeZoneCatalog.Default.CatalogVersion, 0, property.Version, "user:synthetic", now), CancellationToken.None);
            }
            await db.SaveChangesAsync();
            PropertyGovernanceBinding binding = PropertyGovernanceBinding.Create("GB", "synthetic-policy", 1, "eu-west", "standard-transfer", "hostel-retention", 1,
                new string('a', 64), now.AddDays(-30), now.AddDays(30), now).Value;
            Assert.True(active.ActivateProcessing(binding, [DomainAcknowledgement.Create("controller-terms", 1).Value], active.Version, Guid.NewGuid(), now, "user:synthetic").IsSuccess);
            Assert.True(retired.Retire(retired.Version, Guid.NewGuid(), now).IsSuccess);
            Room room = Room.Create(Guid.NewGuid(), tenant, active.Id, "101", null, null, Guid.NewGuid(), now).Value;
            Assert.True(room.AddBed(Guid.NewGuid(), "A", room.Version, Guid.NewGuid(), now).IsSuccess);
            await services.GetRequiredService<IRoomRepository>().AddAsync(room, CancellationToken.None);
            await services.GetRequiredService<IPropertyGovernanceRevisionWriter>().AppendAsync(
                new PropertyGovernanceRevisionWriteModel(Guid.NewGuid(), tenant, active.Id, active.Version,
                    PropertyGovernanceRevisionAction.Activated, "policy-allowed", null,
                    new PropertyGovernanceRevisionCoordinates(binding.OperatingCountryCode, binding.PolicyId, binding.PolicyVersion,
                        binding.DataRegionId, binding.TransferProfileId, binding.RetentionPolicyId, binding.RetentionPolicyVersion,
                        binding.ContentSha256, new string('a', 64)), "user:synthetic", now), CancellationToken.None);
            await db.SaveChangesAsync();
        }
    }

    private static ServiceProvider CreateProvider(string connectionString, bool workspacesFirst)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Integration" });
        builder.Configuration["ApplicationIdentity:Namespace"] = "reservations-writer-regression";
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] = connectionString;
        builder.Configuration["Tenancy:Enabled"] = "true";
        builder.Configuration["Tenancy:LocalDefaultTenantId"] = "default";
        builder.AddTenancyInfrastructure();
        builder.AddTenantScoping();
        builder.AddRuntimeInfrastructure();
        if (workspacesFirst)
        {
            builder.AddWorkspacesPersistence();
        }
        builder.AddPropertiesPersistence();
        builder.AddReservationsPersistence();
        if (!workspacesFirst)
        {
            builder.AddWorkspacesPersistence();
        }
        builder.Services.AddReservationsApplication();
        builder.Services.AddReservationsTaskHandlers();
        builder.AddTenantTaskExecutionContext();
        // External queue/report transport only; handler, source, writer, checkpoints and transactions are real.
        builder.Services.AddSingleton<ITaskRuntimeReporter, LocalReporter>();
        builder.Services.AddSingleton<ITaskControlLoop, NoExternalControl>();
        return builder.Services.BuildServiceProvider();
    }

    private static PostgreSqlContainer Database() => new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithName($"bunkfy-reservations-writer-{Guid.NewGuid():N}")
        .WithLabel("bunkfy.test.grant", "0104-reservations-owned-writer")
        .WithDatabase("reservations_writer_regression")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .WithCreateParameterModifier(parameters =>
        {
            Assert.NotNull(parameters.HostConfig);
            Assert.NotNull(parameters.HostConfig.PortBindings);
            foreach (var bindings in parameters.HostConfig.PortBindings.Values)
            {
                foreach (var binding in bindings)
                {
                    binding.HostIP = "127.0.0.1";
                }
            }
        }).Build();

    private sealed class CheckpointRollbackProbeException : Exception;

    private sealed class LocalReporter : ITaskRuntimeReporter
    {
        public Task<TaskRunMutationOutcome> ReportHeartbeatAsync(TaskExecutionContext context, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) => Task.FromResult(TaskRunMutationOutcome.Applied);
        public Task<TaskRunMutationOutcome> ReportProgressAsync(TaskExecutionContext context, TaskProgress progress, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) => Task.FromResult(TaskRunMutationOutcome.Applied);
    }

    private sealed class NoExternalControl : ITaskControlLoop
    {
        public Task<TaskControlPollResult> PollAsync(TaskExecutionContext context, int maxMessages, CancellationToken cancellationToken) => Task.FromResult(new TaskControlPollResult([]));
        public Task MarkHandledAsync(TaskExecutionContext context, TaskControlMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkFailedAsync(TaskExecutionContext context, TaskControlMessage message, string error, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

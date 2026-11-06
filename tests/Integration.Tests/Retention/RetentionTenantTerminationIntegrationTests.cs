namespace Integration.Tests.Retention;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class RetentionTenantTerminationIntegrationTests
{
    private const string TenantA =
        "10000000-0000-0000-0000-000000000001";
    private const string TenantB =
        "10000000-0000-0000-0000-000000000002";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid ExecutionA =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ExecutionB =
        Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid PropertyA =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyB =
        Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset ExportNowUtc =
        new(2026, 8, 2, 15, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FrozenAtUtc =
        ExportNowUtc.AddMinutes(-1);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_export_is_complete_isolated_and_freezes_writes()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_retention_termination_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        using ServiceProvider tenantAProvider = CreateProvider(
            postgreSql.GetConnectionString(),
            TenantA);
        using (IServiceScope migrationScope =
            tenantAProvider.CreateScope())
        {
            await migrationScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>()
                .Database.MigrateAsync()
                .ConfigureAwait(false);
            await migrationScope.ServiceProvider
                .GetRequiredService<RetentionDbContext>()
                .Database.MigrateAsync()
                .ConfigureAwait(false);
        }

        await SeedAsync(
            tenantAProvider,
            TenantA,
            PropertyA,
            ExecutionA).ConfigureAwait(false);
        using ServiceProvider tenantBProvider = CreateProvider(
            postgreSql.GetConnectionString(),
            TenantB);
        await SeedAsync(
            tenantBProvider,
            TenantB,
            PropertyB,
            ExecutionB).ConfigureAwait(false);

        using IServiceScope scope = tenantAProvider.CreateScope();
        WorkspacesDbContext workspaces = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceTerminationFence fence = CreateTerminationFence();
        workspaces.WorkspaceTerminationFences.Add(fence);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);

        RetentionDbContext retention = scope.ServiceProvider
            .GetRequiredService<RetentionDbContext>();
        Assert.Equal(
            1,
            await ReadTenantRevisionAsync(retention, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await ReadTenantRevisionAsync(retention, TenantB)
                .ConfigureAwait(false));
        ITenantTerminationExportContributor contributor =
            scope.ServiceProvider
                .GetServices<ITenantTerminationExportContributor>()
                .Single(candidate =>
                    candidate.ExportDescriptor.ExportSchemaId ==
                        RetentionTenantTerminationMetadata.ExportSchemaId);
        CollectingSink first = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(fence),
                first,
                CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("retention.termination.exported", result.ResultCode);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(1, result.ResultingProofRevision);
        Assert.Equal(
            [
                RetentionTenantTerminationMetadata.ExecutionRecordType,
                RetentionTenantTerminationMetadata.ScheduleStateRecordType
            ],
            first.Records.Select(record => record.RecordType).ToArray());
        Assert.DoesNotContain(
            first.Records,
            record => record.RecordId == ExecutionB);

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                Request(fence),
                replay,
                CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            first.Records.Select(Identity).ToArray(),
            replay.Records.Select(Identity).ToArray());

        await AssertExportSerializesMutationAsync(
            contributor,
            tenantAProvider,
            fence).ConfigureAwait(false);
    }

    private static async Task SeedAsync(
        IServiceProvider services,
        string tenantId,
        Guid propertyId,
        Guid executionId)
    {
        using IServiceScope scope = services.CreateScope();
        RetentionDbContext context = scope.ServiceProvider
            .GetRequiredService<RetentionDbContext>();
        RetentionExecution execution = RetentionExecution.Start(
            executionId,
            tenantId,
            "ingestion",
            "raw-source-evidence",
            RetentionExecutionTargetKind.Property,
            propertyId,
            executionPolicyVersion: 1,
            attempt: 1,
            ExportNowUtc.AddDays(-1),
            ExportNowUtc.AddDays(-1).AddMinutes(10)).Value;
        RetentionScheduleState schedule = new(
            execution,
            ExportNowUtc.AddHours(1));
        Assert.True(execution.Complete(
            RetentionExecutionState.Completed,
            attempt: 1,
            scannedCount: 2,
            affectedCount: 1,
            remainingCount: 0,
            "ingestion.raw-payload.completed",
            ExportNowUtc.AddDays(-1).AddMinutes(5),
            holdReviewDueAtUtc: null).IsSuccess);
        schedule.RecordCompleted(execution);

        context.Executions.Add(execution);
        context.ScheduleStates.Add(schedule);
        context.TenantProjections.Add(new(
            tenantId,
            Guid.NewGuid(),
            isActive: true,
            sourceVersion: 1));
        RetentionPropertyProjection property = new(
            tenantId,
            propertyId,
            isActive: true,
            topologySourceVersion: 1);
        property.ApplyPolicy(
            isProcessingEnabled: true,
            retentionPolicyVersion: 1,
            sourceVersion: 1);
        context.PropertyProjections.Add(property);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AssertExportSerializesMutationAsync(
        ITenantTerminationExportContributor contributor,
        IServiceProvider services,
        WorkspaceTerminationFence fence)
    {
        BlockingSink sink = new();
        Task<TenantTerminationContributionResult> export =
            contributor.ExportAsync(
                Request(fence),
                sink,
                CancellationToken.None);
        Assert.Same(
            sink.FirstRecordObserved,
            await Task.WhenAny(sink.FirstRecordObserved, export)
                .ConfigureAwait(false));

        Task write = AttemptOperationalWriteAsync(services);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250))
                .ConfigureAwait(false);
            Assert.False(write.IsCompleted);
        }
        finally
        {
            sink.Release();
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            (await export.ConfigureAwait(false)).Status);
        InvalidOperationException failure =
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => write).ConfigureAwait(false);
        Assert.Equal(
            "The workspace is not accepting Retention mutations.",
            failure.Message);

        using IServiceScope verificationScope = services.CreateScope();
        RetentionDbContext verification = verificationScope.ServiceProvider
            .GetRequiredService<RetentionDbContext>();
        Assert.True(
            (await verification.TenantProjections
                .SingleAsync()
                .ConfigureAwait(false)).IsActive);
        Assert.Equal(
            1,
            await ReadTenantRevisionAsync(verification, TenantA)
                .ConfigureAwait(false));
    }

    private static async Task AttemptOperationalWriteAsync(
        IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        RetentionDbContext context = scope.ServiceProvider
            .GetRequiredService<RetentionDbContext>();
        RetentionTenantProjection projection =
            await context.TenantProjections.SingleAsync()
                .ConfigureAwait(false);
        projection.Apply(
            projection.OrganizationId,
            isActive: false,
            sourceVersion: 2);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static Task<long> ReadTenantRevisionAsync(
        RetentionDbContext context,
        string tenantId) =>
        context.Database
            .SqlQuery<long>($"""
                SELECT "Revision" AS "Value"
                FROM retention.tenant_revisions
                WHERE "ScopeId" = {tenantId}
                """)
            .SingleAsync();

    private static ServiceProvider CreateProvider(
        string connectionString,
        string tenantId)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(
            new TestScopeContext(tenantId));
        builder.Services.AddSingleton<ISystemClock>(
            new TestClock());
        builder.AddWorkspacesPersistence();
        builder.Services.AddSingleton<
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            UnusedStaffOnboardingOutcomeReader>();
        builder.AddRetentionPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private static WorkspaceTerminationFence CreateTerminationFence() =>
        WorkspaceTerminationFence.Freeze(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            TenantA,
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            approvalRevision: 1,
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            Digest,
            "termination-operator",
            FrozenAtUtc).Value;

    private static TenantTerminationExportRequest Request(
        WorkspaceTerminationFence fence) =>
        new(
            new TenantTerminationContributionRequest(
                TenantTerminationContract.CurrentVersion,
                TenantA,
                fence.ProcessId,
                fence.CaseId,
                fence.ApprovalRevision,
                OperationRevision: 2,
                fence.TerminationEpoch,
                TenantTerminationContributionPhase.Export,
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                fence.PolicyEvidenceSha256,
                "termination-exporter",
                ExportNowUtc.AddMinutes(30)),
            FreezeOperationRevision: 1,
            fence.Version,
            Digest,
            FrozenAtUtc);

    private static string Identity(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|{record.RecordVersion}";

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => ExportNowUtc;
    }

    private sealed class CollectingSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSink : IDataRightsExportSink
    {
        private readonly TaskCompletionSource firstRecordObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int blocked;

        public Task FirstRecordObserved => this.firstRecordObserved.Task;

        public async ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref this.blocked, 1) == 0)
            {
                this.firstRecordObserved.TrySetResult();
                await this.release.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public void Release() => this.release.TrySetResult();
    }
}

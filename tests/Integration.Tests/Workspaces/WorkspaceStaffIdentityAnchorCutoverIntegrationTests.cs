namespace Integration.Tests;

using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Gma.Framework.Scoping.Infrastructure;
using Gma.Modules.Organizations.Contracts;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class WorkspaceStaffIdentityAnchorCutoverIntegrationTests
{
    private const string TenantId =
        "b7000000-0000-0000-0000-000000000001";
    private const string EvidenceSha256 =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        12,
        0,
        0,
        TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Nonempty_reconcile_releases_Workspaces_lock_before_real_Staff_apply()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_cutover_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString());
        await MigrateAsync(services).ConfigureAwait(false);
        Guid staffMemberId = Guid.NewGuid();
        Guid applicationId = Guid.NewGuid();
        await SeedCompletedApplicationAsync(
            services,
            staffMemberId,
            applicationId).ConfigureAwait(false);

        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        Result<WorkspaceStaffIdentityAnchorCutoverStatus> before =
            await QueryAsync(
                services,
                TenantId,
                new GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
                    manifest)).ConfigureAwait(false);
        Assert.True(before.IsSuccess, before.Error.Code);
        Assert.Equal(1, before.Value.SeedableWorkspaceCount);

        Result<WorkspaceStaffIdentityAnchorReconcileResult> reconciled =
            await SendAsync(
                    services,
                    TenantId,
                    new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                        before.Value.SourceEvidenceSha256,
                        before.Value.AnchorStateSha256,
                        manifest,
                        Assert.IsType<string>(
                            before.Value.OwnerManifestSha256),
                        BatchSize: 1))
                .WaitAsync(TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);

        Assert.True(reconciled.IsSuccess, reconciled.Error.Code);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorReconcileOutcome.AppliedAndVerified,
            reconciled.Value.Outcome);
        Assert.Equal(1, reconciled.Value.AppliedCount);
        Assert.False(reconciled.Value.MustRerunStatus);
        Assert.NotNull(reconciled.Value.Status);
        Assert.True(reconciled.Value.Status!.IsReady);
        Assert.Equal(1, reconciled.Value.Status.AlreadyAnchoredCount);

        using IServiceScope verificationScope = CreateTenantScope(
            services,
            TenantId);
        StaffDbContext staff = verificationScope.ServiceProvider
            .GetRequiredService<StaffDbContext>();
        Assert.Equal(
            1,
            await staff.IdentityProvisioningAnchors.AsNoTracking()
                .CountAsync()
                .ConfigureAwait(false));

        Result<WorkspaceStaffIdentityAnchorReconcileResult> stale =
            await SendAsync(
                services,
                TenantId,
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                    before.Value.SourceEvidenceSha256,
                    before.Value.AnchorStateSha256,
                    manifest,
                    Assert.IsType<string>(before.Value.OwnerManifestSha256),
                    BatchSize: 1)).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.AnchorStateChanged,
            stale.Error);

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> noncanonical =
            await QueryAsync(
                services,
                TenantId.ToUpperInvariant(),
                new GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
                    manifest)).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.TenantRequired,
            noncanonical.Error);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Lost_response_after_real_Staff_commit_is_unknown_and_fresh_state_is_exact()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_lost_response_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        StaffCutoverProbe probe = new() { ThrowAfterSuccessfulApply = true };
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            probe);
        await MigrateAsync(services).ConfigureAwait(false);
        await SeedCompletedApplicationAsync(
            services,
            Guid.NewGuid(),
            Guid.NewGuid()).ConfigureAwait(false);
        await SeedCompletedApplicationAsync(
            services,
            Guid.NewGuid(),
            Guid.NewGuid()).ConfigureAwait(false);

        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        Result<WorkspaceStaffIdentityAnchorCutoverStatus> before =
            await QueryAsync(
                services,
                TenantId,
                new GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
                    manifest)).ConfigureAwait(false);
        Assert.True(before.IsSuccess, before.Error.Code);
        Assert.Equal(2, before.Value.SeedableWorkspaceCount);

        Result<WorkspaceStaffIdentityAnchorReconcileResult> reconciled =
            await SendAsync(
                    services,
                    TenantId,
                    new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                        before.Value.SourceEvidenceSha256,
                        before.Value.AnchorStateSha256,
                        manifest,
                        Assert.IsType<string>(
                            before.Value.OwnerManifestSha256),
                        BatchSize: 2))
                .WaitAsync(TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);

        Assert.True(reconciled.IsSuccess, reconciled.Error.Code);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorReconcileOutcome.ApplyOutcomeUnknown,
            reconciled.Value.Outcome);
        Assert.Null(reconciled.Value.AppliedCount);
        Assert.True(reconciled.Value.MustRerunStatus);
        Assert.Equal(
            before.Value.SourceEvidenceSha256,
            reconciled.Value.AcceptedSourceEvidenceSha256);
        Assert.Equal(
            before.Value.AnchorStateSha256,
            reconciled.Value.AcceptedAnchorStateSha256);
        Assert.Equal(
            before.Value.OwnerManifestSha256,
            reconciled.Value.AcceptedOwnerManifestSha256);
        Assert.NotNull(reconciled.Value.Status);
        Assert.True(reconciled.Value.Status!.IsReady);
        Assert.Equal(2, reconciled.Value.Status.AlreadyAnchoredCount);
        Assert.Equal(0, reconciled.Value.Status.SeedableWorkspaceCount);
        Assert.Equal(1, probe.ApplyCalls);

        using (IServiceScope verificationScope = CreateTenantScope(
            services,
            TenantId))
        {
            StaffDbContext staff = verificationScope.ServiceProvider
                .GetRequiredService<StaffDbContext>();
            Assert.Equal(
                2,
                await staff.IdentityProvisioningAnchors.AsNoTracking()
                    .CountAsync()
                    .ConfigureAwait(false));
        }

        Result<WorkspaceStaffIdentityAnchorReconcileResult> staleRetry =
            await SendAsync(
                services,
                TenantId,
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                    before.Value.SourceEvidenceSha256,
                    before.Value.AnchorStateSha256,
                    manifest,
                    Assert.IsType<string>(before.Value.OwnerManifestSha256),
                    BatchSize: 2)).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.AnchorStateChanged,
            staleRetry.Error);
        Assert.Equal(1, probe.ApplyCalls);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Cancelled_preflight_rolls_back_exclusive_lock_before_shared_admission()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_cancel_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        TestOrganizationAuthority organizations = new();
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            organizationAuthority: organizations);
        await MigrateAsync(services).ConfigureAwait(false);
        await SeedCompletedApplicationAsync(
            services,
            Guid.NewGuid(),
            Guid.NewGuid()).ConfigureAwait(false);
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        Result<WorkspaceStaffIdentityAnchorCutoverStatus> before =
            await QueryAsync(
                services,
                TenantId,
                new GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
                    manifest)).ConfigureAwait(false);
        Assert.True(before.IsSuccess, before.Error.Code);

        organizations.BlockNextSnapshot();
        using IServiceScope reconcileScope = CreateTenantScope(
            services,
            TenantId);
        using CancellationTokenSource cancellation = new();
        Task<Result<WorkspaceStaffIdentityAnchorReconcileResult>> reconcile =
            reconcileScope.ServiceProvider
                .GetRequiredService<IRequestDispatcher>()
                .SendAsync(
                    new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                        before.Value.SourceEvidenceSha256,
                        before.Value.AnchorStateSha256,
                        manifest,
                        Assert.IsType<string>(
                            before.Value.OwnerManifestSha256),
                        BatchSize: 1),
                    cancellation.Token);
        await organizations.SnapshotBlocked.Task
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await reconcile.ConfigureAwait(false))
            .ConfigureAwait(false);

        using IServiceScope writerScope = CreateTenantScope(
            services,
            TenantId);
        WorkspacesDbContext writer = writerScope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding submitted = CreateSubmittedApplication(
            "after-cancel");
        writer.StaffOnboardingApplications.Add(submitted);
        await writer.SaveChangesAsync()
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        using IServiceScope verificationScope = CreateTenantScope(
            services,
            TenantId);
        StaffDbContext staff = verificationScope.ServiceProvider
            .GetRequiredService<StaffDbContext>();
        Assert.Empty(await staff.IdentityProvisioningAnchors
            .AsNoTracking()
            .ToArrayAsync()
            .ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Fresh_verification_holds_exclusive_lock_across_status_and_exact_inspection()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_fresh_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();

        StaffCutoverProbe probe = new() { BlockInspectionCall = 4 };
        await using ServiceProvider services = CreateProvider(
            connectionString,
            probe);
        await MigrateAsync(services).ConfigureAwait(false);
        await SeedCompletedApplicationAsync(
            services,
            Guid.NewGuid(),
            Guid.NewGuid()).ConfigureAwait(false);
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        Result<WorkspaceStaffIdentityAnchorCutoverStatus> before =
            await QueryAsync(
                services,
                TenantId,
                new GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
                    manifest)).ConfigureAwait(false);
        Assert.True(before.IsSuccess, before.Error.Code);

        Task<Result<WorkspaceStaffIdentityAnchorReconcileResult>> reconcile =
            SendAsync(
                services,
                TenantId,
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                    before.Value.SourceEvidenceSha256,
                    before.Value.AnchorStateSha256,
                    manifest,
                    Assert.IsType<string>(before.Value.OwnerManifestSha256),
                    BatchSize: 1));
        await probe.InspectionBlocked.Task
            .WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);

        Task writer = WriteSubmittedApplicationAsync(
            services,
            "during-fresh-verification");
        try
        {
            await WaitForSharedAdmissionWaitAsync(connectionString)
                .ConfigureAwait(false);
            Assert.False(writer.IsCompleted);
        }
        finally
        {
            probe.ReleaseInspection.TrySetResult();
        }

        Result<WorkspaceStaffIdentityAnchorReconcileResult> result =
            await reconcile.WaitAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
        await writer.WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorReconcileOutcome.AppliedAndVerified,
            result.Value.Outcome);
        Assert.Equal(4, probe.InspectCalls);
        Assert.Equal(1, probe.ApplyCalls);

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> after =
            await QueryAsync(
                services,
                TenantId,
                new GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
                    manifest)).ConfigureAwait(false);
        Assert.True(after.IsSuccess, after.Error.Code);
        Assert.NotEqual(
            result.Value.AcceptedSourceEvidenceSha256,
            after.Value.SourceEvidenceSha256);
        Assert.Equal(2, after.Value.WorkspaceSourceCount);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Source_drift_after_preflight_before_real_apply_returns_unknown()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_drift_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        StaffCutoverProbe probe = new()
        {
            CommitWorkspaceDriftBeforeApply = true
        };
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            probe);
        await MigrateAsync(services).ConfigureAwait(false);
        await SeedCompletedApplicationAsync(
            services,
            Guid.NewGuid(),
            Guid.NewGuid()).ConfigureAwait(false);
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        Result<WorkspaceStaffIdentityAnchorCutoverStatus> before =
            await QueryAsync(
                services,
                TenantId,
                new GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
                    manifest)).ConfigureAwait(false);
        Assert.True(before.IsSuccess, before.Error.Code);

        Result<WorkspaceStaffIdentityAnchorReconcileResult> result =
            await SendAsync(
                    services,
                    TenantId,
                    new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                        before.Value.SourceEvidenceSha256,
                        before.Value.AnchorStateSha256,
                        manifest,
                        Assert.IsType<string>(
                            before.Value.OwnerManifestSha256),
                        BatchSize: 1))
                .WaitAsync(TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorReconcileOutcome.ApplyOutcomeUnknown,
            result.Value.Outcome);
        Assert.Equal(1, result.Value.AppliedCount);
        Assert.True(result.Value.MustRerunStatus);
        Assert.NotNull(result.Value.Status);
        Assert.NotEqual(
            result.Value.AcceptedSourceEvidenceSha256,
            result.Value.Status!.SourceEvidenceSha256);
        Assert.Equal(2, result.Value.Status.WorkspaceSourceCount);
        Assert.Equal(1, probe.ApplyCalls);
        Assert.Equal(1, probe.DriftWrites);
    }

    private static async Task WriteSubmittedApplicationAsync(
        ServiceProvider services,
        string label)
    {
        using IServiceScope scope = CreateTenantScope(services, TenantId);
        WorkspacesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        dbContext.StaffOnboardingApplications.Add(
            CreateSubmittedApplication(label));
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static WorkspaceStaffOnboarding CreateSubmittedApplication(
        string label) =>
        WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            "account-" + label,
            label + "@example.test",
            label,
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            Now.AddHours(1)).Value;

    private static async Task WaitForSharedAdmissionWaitAsync(
        string connectionString)
    {
        await using NpgsqlConnection observer = new(connectionString);
        await observer.OpenAsync().ConfigureAwait(false);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using NpgsqlCommand command = new(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND state = 'active'
                      AND wait_event_type = 'Lock'
                      AND query ILIKE
                          '%pg_advisory_xact_lock_shared%');
                """,
                observer);
            if ((bool)(await command.ExecuteScalarAsync()
                    .ConfigureAwait(false))!)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25))
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The Workspaces writer did not wait for shared tenant admission.");
    }

    private static async Task SeedCompletedApplicationAsync(
        ServiceProvider services,
        Guid staffMemberId,
        Guid applicationId)
    {
        using (IServiceScope staffScope = CreateTenantScope(
            services,
            TenantId))
        {
            StaffDbContext dbContext = staffScope.ServiceProvider
                .GetRequiredService<StaffDbContext>();
            string suffix = staffMemberId.ToString("N");
            string subjectId = "account-cutover-" + suffix;
            StaffMember member = StaffMember.Create(
                staffMemberId,
                TenantId,
                "Cutover Staff",
                legalName: null,
                workEmail: $"cutover-{suffix}@example.test",
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                authSubjectId: subjectId,
                actorId: "system:integration-test",
                eventId: Guid.NewGuid(),
                Now).Value;
            await staffScope.ServiceProvider
                .GetRequiredService<IStaffMemberRepository>()
                .AddAsync(member, CancellationToken.None)
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        using IServiceScope workspaceScope = CreateTenantScope(
            services,
            TenantId);
        WorkspacesDbContext workspaces = workspaceScope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        string workspaceSuffix = staffMemberId.ToString("N");
        string workspaceSubjectId = "account-cutover-" + workspaceSuffix;
        WorkspaceStaffOnboarding onboarding = WorkspaceStaffOnboarding.Create(
            applicationId,
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            workspaceSubjectId,
            $"cutover-{workspaceSuffix}@example.test",
            "Cutover Staff",
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            Now).Value;
        workspaces.StaffOnboardingApplications.Add(onboarding);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);

        Assert.True(onboarding.ObserveInvitationAccepted(
            Now.AddMinutes(1)).IsSuccess);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);

        Assert.True(onboarding.MarkStaffReady(
            staffMemberId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);

        Assert.True(onboarding.Complete(Now.AddMinutes(3)).IsSuccess);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);
    }

    private static WorkspaceStaffIdentityAnchorOwnerManifest EmptyManifest() =>
        new(
            ContractVersion: 1,
            TenantId,
            ReviewedAtUtc: Now,
            new WorkspaceStaffIdentityAnchorHistoricalEvidence(
                WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                    .ReviewedNoHistoricalOwnerSources,
                EvidenceSha256),
            Bindings: []);

    private static async Task<Result<TResponse>> QueryAsync<TResponse>(
        ServiceProvider services,
        string tenantId,
        IQuery<TResponse> query)
    {
        using IServiceScope scope = CreateTenantScope(services, tenantId);
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .QueryAsync(query, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<Result<TResponse>> SendAsync<TResponse>(
        ServiceProvider services,
        string tenantId,
        ICommand<TResponse> command)
    {
        using IServiceScope scope = CreateTenantScope(services, tenantId);
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static IServiceScope CreateTenantScope(
        ServiceProvider services,
        string tenantId)
    {
        IServiceScope scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>()
            .SetScope(tenantId);
        return scope;
    }

    private static async Task MigrateAsync(ServiceProvider services)
    {
        using IServiceScope scope = CreateTenantScope(services, TenantId);
        await scope.ServiceProvider.GetRequiredService<StaffDbContext>()
            .Database.GetService<IMigrator>()
            .MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>()
            .Database.GetService<IMigrator>()
            .MigrateAsync()
            .ConfigureAwait(false);
    }

    private static ServiceProvider CreateProvider(
        string connectionString,
        StaffCutoverProbe? probe = null,
        TestOrganizationAuthority? organizationAuthority = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Configuration["Scoping:Enabled"] = "true";
        builder.AddScopingInfrastructure();
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        TestOrganizationAuthority organizations =
            organizationAuthority ?? new TestOrganizationAuthority();
        builder.Services.AddSingleton<IOrganizationScopeLifecycle>(
            organizations);
        builder.Services.AddSingleton<IOrganizationMembershipInspector>(
            organizations);
        builder.Services.AddStaffApplication();
        builder.AddStaffPersistence();
        if (probe is not null)
        {
            builder.Services.AddSingleton(probe);
            builder.Services.Replace(ServiceDescriptor.Scoped<
                IStaffIdentityProvisioningAnchorCutover,
                DispatcherStaffCutover>());
        }

        builder.Services.AddWorkspacesApplication(
            builder.Configuration,
            "global");
        builder.AddWorkspacesPersistence();
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class DispatcherStaffCutover(
        IRequestDispatcher dispatcher,
        StaffCutoverProbe probe,
        IServiceScopeFactory scopeFactory)
        : IStaffIdentityProvisioningAnchorCutover
    {
        public async Task<StaffIdentityProvisioningAnchorInspection>
            InspectAsync(
                IReadOnlyList<StaffIdentityProvisioningAnchorCandidate>
                    candidates,
                CancellationToken cancellationToken = default)
        {
            int inspectionCall = Interlocked.Increment(
                ref probe.InspectCalls);
            Result<IReadOnlyList<
                StaffIdentityProvisioningAnchorCandidateInspection>> result =
                await dispatcher.QueryAsync(
                    new InspectStaffIdentityProvisioningAnchorsQuery(
                        candidates),
                    cancellationToken).ConfigureAwait(false);
            if (inspectionCall == probe.BlockInspectionCall)
            {
                probe.InspectionBlocked.TrySetResult();
                await probe.ReleaseInspection.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return result.IsSuccess
                ? new(true, result.Value, ErrorCode: null)
                : new(false, [], result.Error.Code);
        }

        public async Task<StaffIdentityProvisioningAnchorApplyResult>
            ApplyAsync(
                IReadOnlyList<StaffIdentityProvisioningAnchorCandidate>
                    candidates,
                CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.ApplyCalls);
            if (probe.CommitWorkspaceDriftBeforeApply &&
                Interlocked.Exchange(ref probe.DriftWrites, 1) == 0)
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                scope.ServiceProvider
                    .GetRequiredService<IScopeContextAccessor>()
                    .SetScope(TenantId);
                WorkspacesDbContext workspaces = scope.ServiceProvider
                    .GetRequiredService<WorkspacesDbContext>();
                workspaces.StaffOnboardingApplications.Add(
                    CreateSubmittedApplication("before-apply-drift"));
                await workspaces.SaveChangesAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            Result<StaffIdentityProvisioningAnchorApplySummary> result =
                await dispatcher.SendAsync(
                    new ApplyStaffIdentityProvisioningAnchorsCommand(
                        candidates),
                    cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess && probe.ThrowAfterSuccessfulApply)
            {
                throw new LostStaffApplyResponseException();
            }

            return result.IsSuccess
                ? new(
                    true,
                    result.Value.AppliedCount,
                    result.Value.AlreadyAnchoredCount,
                    ErrorCode: null)
                : new(false, 0, 0, result.Error.Code);
        }
    }

    private sealed class StaffCutoverProbe
    {
        public bool ThrowAfterSuccessfulApply { get; init; }
        public bool CommitWorkspaceDriftBeforeApply { get; init; }
        public int BlockInspectionCall { get; init; }
        public TaskCompletionSource InspectionBlocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseInspection { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int ApplyCalls;
        public int InspectCalls;
        public int DriftWrites;
    }

    private sealed class LostStaffApplyResponseException()
        : Exception("The Staff apply response was lost after commit.");

    private sealed class TestOrganizationAuthority :
        IOrganizationScopeLifecycle,
        IOrganizationMembershipInspector
    {
        private int blockNextSnapshot;

        public TaskCompletionSource SnapshotBlocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextSnapshot() =>
            Interlocked.Exchange(ref this.blockNextSnapshot, 1);

        public async Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref this.blockNextSnapshot, 0) == 1)
            {
                this.SnapshotBlocked.TrySetResult();
                await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new OrganizationScopeSnapshot(
                OrganizationScopeStatus.Open,
                Revision: 1);
        }

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<OrganizationScopeExportRecord> records =
                request.Store == OrganizationScopeExportStore.Organization
                    ?
                    [
                        new OrganizationScopeOrganizationExportRecord(
                            request.OrganizationId,
                            "Cutover organization",
                            "cutover-organization",
                            OrganizationStatus.Active,
                            ActiveOwnerCount: 0,
                            Version: 1,
                            CreatedBy: "system:integration-test",
                            CreatedAtUtc: Now,
                            LastChangedBy: "system:integration-test",
                            LastChangedAtUtc: Now)
                    ]
                    : [];
            return Task.FromResult(new OrganizationScopeExportPage(
                OrganizationScopeExportStatus.Completed,
                ScopeRevision: 1,
                request.Store,
                records,
                NextCursor: null,
                HasMore: false));
        }

        public Task<OrganizationMembershipSnapshot?> FindAsync(
            Guid organizationId,
            Guid membershipId,
            string subjectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationMembershipSnapshot?>(null);

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

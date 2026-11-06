namespace Integration.Tests;

using BunkFy.Host.Worker;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Tenancy;
using Gma.Modules.Organizations.Contracts;
using Gma.Modules.Organizations.Persistence;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class WorkspaceStaffOnboardingExpiryPersistenceTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Withdrawal_before_requested_survives_redelivery_and_converges_under_the_source_lock()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
            "postgres:16-alpine")
            .WithDatabase("bunkfy_workspace_withdrawal_order_tests")
            .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        using IHost worker = CreateWorker(postgreSql.GetConnectionString());
        Guid organizationId = Guid.NewGuid();
        string scopeId = organizationId.ToString("D");
        Guid linkId = Guid.NewGuid();
        Guid claimId = Guid.NewGuid();
        DateTimeOffset nowUtc = new(
            2026,
            8,
            11,
            8,
            0,
            0,
            TimeSpan.Zero);
        string subjectId = Guid.NewGuid().ToString("D");

        using (IServiceScope seedScope = worker.Services.CreateScope())
        {
            seedScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(scopeId);
            WorkspacesDbContext seed = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await seed.Database.MigrateAsync().ConfigureAwait(false);
            (WorkspaceStaffOnboarding application, WorkspaceStaffAccessPlan plan) =
                CreateUnboundApplicationAndPlan(
                    scopeId,
                    linkId,
                    subjectId,
                    nowUtc,
                    "ordered");
            seed.StaffOnboardingApplications.Add(application);
            seed.StaffAccessPlans.Add(plan);
            await seed.SaveChangesAsync().ConfigureAwait(false);
        }

        OrganizationEnrollmentClaimWithdrawnIntegrationEvent withdrawal = new(
            Guid.NewGuid(),
            nowUtc.AddMinutes(3).AddTicks(1),
            scopeId,
            organizationId,
            linkId,
            claimId,
            2);
        for (int delivery = 0; delivery < 6; delivery++)
        {
            using IServiceScope deliveryScope = worker.Services.CreateScope();
            deliveryScope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(scopeId);
            WorkspacesDbContext deliveryDb = deliveryScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            IIntegrationEventHandler<
                OrganizationEnrollmentClaimWithdrawnIntegrationEvent> handler =
                GetHandler<OrganizationEnrollmentClaimWithdrawnIntegrationEvent>(
                    deliveryScope.ServiceProvider,
                    WorkspacesModuleMetadata.EnrollmentClaimWithdrawnHandlerName);
            await HandleInTransactionAsync(
                deliveryDb,
                () => handler.HandleAsync(
                    withdrawal,
                    CancellationToken.None)).ConfigureAwait(false);
        }

        using (IServiceScope verificationScope = worker.Services.CreateScope())
        {
            verificationScope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(scopeId);
            WorkspacesDbContext verification = verificationScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            WorkspaceStaffDeferredClaimWithdrawal deferred =
                await verification.StaffDeferredClaimWithdrawals.SingleAsync()
                    .ConfigureAwait(false);
            Assert.Equal(nowUtc.AddMinutes(3), deferred.OccurredAtUtc);
            Assert.True(deferred.Matches(
                withdrawal.ScopeId,
                withdrawal.OrganizationId,
                withdrawal.EnrollmentLinkId,
                withdrawal.ClaimId,
                withdrawal.ClaimVersion,
                withdrawal.EventId,
                withdrawal.OccurredAtUtc));
        }

        using (IServiceScope requestedScope = worker.Services.CreateScope())
        {
            requestedScope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(scopeId);
            WorkspacesDbContext requestedDb = requestedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            IIntegrationEventHandler<
                OrganizationEnrollmentClaimChangedIntegrationEvent> handler =
                GetHandler<OrganizationEnrollmentClaimChangedIntegrationEvent>(
                    requestedScope.ServiceProvider,
                    WorkspacesModuleMetadata.EnrollmentClaimChangedHandlerName);
            await HandleInTransactionAsync(
                requestedDb,
                () => handler.HandleAsync(
                    new OrganizationEnrollmentClaimChangedIntegrationEvent(
                        Guid.NewGuid(),
                        nowUtc.AddMinutes(2),
                        scopeId,
                        organizationId,
                        linkId,
                        claimId,
                        subjectId,
                        OrganizationEnrollmentClaimChange.Requested,
                        OrganizationEnrollmentClaimStatus.Pending,
                        membershipId: null,
                        claimVersion: 1),
                    CancellationToken.None)).ConfigureAwait(false);
        }

        Guid racingLinkId = Guid.NewGuid();
        Guid racingClaimId = Guid.NewGuid();
        string racingSubjectId = Guid.NewGuid().ToString("D");
        using (IServiceScope raceSeedScope = worker.Services.CreateScope())
        {
            raceSeedScope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(scopeId);
            WorkspacesDbContext raceSeed = raceSeedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            (WorkspaceStaffOnboarding application, WorkspaceStaffAccessPlan plan) =
                CreateUnboundApplicationAndPlan(
                    scopeId,
                    racingLinkId,
                    racingSubjectId,
                    nowUtc,
                    "racing");
            raceSeed.StaffOnboardingApplications.Add(application);
            raceSeed.StaffAccessPlans.Add(plan);
            await raceSeed.SaveChangesAsync().ConfigureAwait(false);
        }

        using IServiceScope racingRequestedScope = worker.Services.CreateScope();
        racingRequestedScope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(scopeId);
        WorkspacesDbContext racingRequestedDb = racingRequestedScope
            .ServiceProvider.GetRequiredService<WorkspacesDbContext>();
        await using var racingRequestedTransaction = await racingRequestedDb
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        await racingRequestedScope.ServiceProvider
            .GetRequiredService<IWorkspaceStaffOnboardingOperationLock>()
            .AcquireSourceWriteAsync(
                racingLinkId,
                CancellationToken.None).ConfigureAwait(false);

        TaskCompletionSource<int> withdrawalBackend = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task concurrentWithdrawal = Task.Run(async () =>
        {
            using IServiceScope withdrawalScope = worker.Services.CreateScope();
            withdrawalScope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(scopeId);
            WorkspacesDbContext withdrawalDb = withdrawalScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await using var transaction = await withdrawalDb.Database
                .BeginTransactionAsync().ConfigureAwait(false);
            int backendPid = await withdrawalDb.Database.SqlQueryRaw<int>(
                    "SELECT pg_backend_pid() AS \"Value\"")
                .SingleAsync().ConfigureAwait(false);
            withdrawalBackend.SetResult(backendPid);
            IIntegrationEventHandler<
                OrganizationEnrollmentClaimWithdrawnIntegrationEvent> handler =
                GetHandler<OrganizationEnrollmentClaimWithdrawnIntegrationEvent>(
                    withdrawalScope.ServiceProvider,
                    WorkspacesModuleMetadata.EnrollmentClaimWithdrawnHandlerName);
            await handler.HandleAsync(
                new OrganizationEnrollmentClaimWithdrawnIntegrationEvent(
                    Guid.NewGuid(),
                    nowUtc.AddMinutes(6),
                    scopeId,
                    organizationId,
                    racingLinkId,
                    racingClaimId,
                    2),
                CancellationToken.None).ConfigureAwait(false);
            await withdrawalDb.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        });
        int withdrawalBackendPid = await withdrawalBackend.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        bool waitingForSourceLock = false;
        for (int attempt = 0; attempt < 100 && !waitingForSourceLock; attempt++)
        {
            waitingForSourceLock = await racingRequestedDb.Database
                .SqlQueryRaw<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_stat_activity " +
                    "WHERE pid = {0} AND wait_event_type = 'Lock') AS \"Value\"",
                    withdrawalBackendPid)
                .SingleAsync().ConfigureAwait(false);
            if (!waitingForSourceLock)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        if (!waitingForSourceLock)
        {
            await racingRequestedTransaction.RollbackAsync().ConfigureAwait(false);
            await concurrentWithdrawal.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            Assert.Fail("The withdrawal handler never reached the source lock wait.");
        }

        IIntegrationEventHandler<OrganizationEnrollmentClaimChangedIntegrationEvent>
            racingRequestedHandler =
                GetHandler<OrganizationEnrollmentClaimChangedIntegrationEvent>(
                    racingRequestedScope.ServiceProvider,
                    WorkspacesModuleMetadata.EnrollmentClaimChangedHandlerName);
        await racingRequestedHandler.HandleAsync(
            new OrganizationEnrollmentClaimChangedIntegrationEvent(
                Guid.NewGuid(),
                nowUtc.AddMinutes(5),
                scopeId,
                organizationId,
                racingLinkId,
                racingClaimId,
                racingSubjectId,
                OrganizationEnrollmentClaimChange.Requested,
                OrganizationEnrollmentClaimStatus.Pending,
                membershipId: null,
                claimVersion: 1),
            CancellationToken.None).ConfigureAwait(false);
        await racingRequestedDb.SaveChangesAsync().ConfigureAwait(false);
        await racingRequestedTransaction.CommitAsync().ConfigureAwait(false);
        await concurrentWithdrawal.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        using IServiceScope finalScope = worker.Services.CreateScope();
        finalScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(scopeId);
        WorkspacesDbContext finalDb = finalScope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding[] terminal = await finalDb
            .StaffOnboardingApplications.OrderBy(application => application.Id)
            .ToArrayAsync().ConfigureAwait(false);
        Assert.Equal(2, terminal.Length);
        Assert.All(terminal, application =>
        {
            Assert.Equal(WorkspaceStaffOnboardingState.Withdrawn, application.Status);
            Assert.Null(application.VerifiedAccountEmail);
            Assert.Null(application.DisplayName);
        });
        Assert.Empty(await finalDb.StaffDeferredClaimWithdrawals.ToArrayAsync()
            .ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Claim_terminal_facts_are_persisted_redacted_and_finalize_expired_sources()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_workspace_expiry_tests")
            .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        using IHost worker = CreateWorker(postgreSql.GetConnectionString());
        Guid organizationId = Guid.NewGuid();
        string scopeId = organizationId.ToString("D");
        Guid linkId = Guid.NewGuid();
        Guid claimId = Guid.NewGuid();
        Guid applicationId = Guid.NewGuid();
        DateTimeOffset nowUtc = new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);

        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(scopeId);
        WorkspacesDbContext dbContext = scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>();
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
        OrganizationsDbContext organizationsDbContext =
            scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
        await organizationsDbContext.Database.MigrateAsync().ConfigureAwait(false);

        WorkspaceStaffOnboarding application = WorkspaceStaffOnboarding.Create(
            applicationId,
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            linkId,
            Guid.NewGuid().ToString("D"),
            "applicant@example.test",
            "Expiry Applicant",
            "Expiry Applicant",
            "staff@example.test",
            "+1 555 0199",
            "EMP-EXPIRY",
            "Receptionist",
            "Front desk",
            nowUtc).Value;
        Assert.True(application.ObserveClaimRequested(claimId, 1, nowUtc.AddMinutes(1)).IsSuccess);
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            linkId,
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "front-desk",
            [],
            Guid.NewGuid().ToString("D"),
            nowUtc).Value;
        Assert.True(plan.Activate(nowUtc.AddMinutes(1)).IsSuccess);
        dbContext.StaffOnboardingApplications.Add(application);
        dbContext.StaffAccessPlans.Add(plan);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();

        IIntegrationEventSubscriptionRegistry subscriptions =
            scope.ServiceProvider.GetRequiredService<IIntegrationEventSubscriptionRegistry>();
        var linkHandler = (IIntegrationEventHandler<OrganizationEnrollmentLinkExpiredIntegrationEvent>)
            scope.ServiceProvider.GetRequiredService(subscriptions.Subscriptions.Single(subscription =>
                subscription.ConsumerModule == WorkspacesModuleMetadata.Name &&
                subscription.HandlerName == WorkspacesModuleMetadata.EnrollmentLinkExpiredHandlerName).HandlerType);
        await using (var transaction = await dbContext.Database
            .BeginTransactionAsync().ConfigureAwait(false))
        {
            await linkHandler.HandleAsync(
                new OrganizationEnrollmentLinkExpiredIntegrationEvent(
                    Guid.NewGuid(),
                    nowUtc.AddMinutes(2),
                    scopeId,
                    organizationId,
                    linkId,
                    nowUtc.AddMinutes(2),
                    2),
                CancellationToken.None).ConfigureAwait(false);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        dbContext.ChangeTracker.Clear();

        WorkspaceStaffAccessPlan sourceExpiredPlan =
            await dbContext.StaffAccessPlans.SingleAsync().ConfigureAwait(false);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, sourceExpiredPlan.Status);
        Assert.Equal(nowUtc.AddMinutes(2), sourceExpiredPlan.SourceExpiredAtUtc);
        dbContext.ChangeTracker.Clear();

        var claimHandler = (IIntegrationEventHandler<OrganizationEnrollmentClaimExpiredIntegrationEvent>)
            scope.ServiceProvider.GetRequiredService(subscriptions.Subscriptions.Single(subscription =>
                subscription.ConsumerModule == WorkspacesModuleMetadata.Name &&
                subscription.HandlerName == WorkspacesModuleMetadata.EnrollmentClaimExpiredHandlerName).HandlerType);
        await using (var transaction = await dbContext.Database
            .BeginTransactionAsync().ConfigureAwait(false))
        {
            await claimHandler.HandleAsync(
                new OrganizationEnrollmentClaimExpiredIntegrationEvent(
                    Guid.NewGuid(),
                    nowUtc.AddMinutes(3),
                    scopeId,
                    organizationId,
                    linkId,
                    claimId,
                    nowUtc.AddMinutes(3),
                    2),
                CancellationToken.None).ConfigureAwait(false);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        dbContext.ChangeTracker.Clear();

        WorkspaceStaffOnboarding persisted = await dbContext.StaffOnboardingApplications
            .SingleAsync().ConfigureAwait(false);
        WorkspaceStaffAccessPlan persistedPlan = await dbContext.StaffAccessPlans
            .SingleAsync().ConfigureAwait(false);
        IWorkspaceStaffOnboardingRepository repository =
            scope.ServiceProvider.GetRequiredService<IWorkspaceStaffOnboardingRepository>();
        IReadOnlyList<WorkspaceStaffOnboarding> active = await repository.ListActiveBySourceAsync(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            linkId,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(WorkspaceStaffOnboardingState.Expired, persisted.Status);
        Assert.Equal(2, persisted.ClaimVersion);
        Assert.Null(persisted.VerifiedAccountEmail);
        Assert.Null(persisted.DisplayName);
        Assert.Null(persisted.WorkEmail);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, persistedPlan.Status);
        Assert.Empty(active);

        Guid withdrawnLinkId = Guid.NewGuid();
        Guid withdrawnClaimId = Guid.NewGuid();
        Guid withdrawnApplicationId = Guid.NewGuid();
        WorkspaceStaffOnboarding withdrawnApplication = WorkspaceStaffOnboarding.Create(
            withdrawnApplicationId,
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            withdrawnLinkId,
            Guid.NewGuid().ToString("D"),
            "withdrawn@example.test",
            "Withdrawn Applicant",
            null,
            "withdrawn.staff@example.test",
            null,
            null,
            null,
            null,
            nowUtc).Value;
        Assert.True(withdrawnApplication.ObserveClaimRequested(
            withdrawnClaimId,
            1,
            nowUtc.AddMinutes(1)).IsSuccess);
        WorkspaceStaffAccessPlan withdrawnPlan = WorkspaceStaffAccessPlan.Create(
            withdrawnLinkId,
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "front-desk",
            [],
            Guid.NewGuid().ToString("D"),
            nowUtc).Value;
        Assert.True(withdrawnPlan.Activate(nowUtc.AddMinutes(1)).IsSuccess);
        dbContext.StaffOnboardingApplications.Add(withdrawnApplication);
        dbContext.StaffAccessPlans.Add(withdrawnPlan);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();

        var withdrawalHandler =
            (IIntegrationEventHandler<OrganizationEnrollmentClaimWithdrawnIntegrationEvent>)
            scope.ServiceProvider.GetRequiredService(subscriptions.Subscriptions.Single(subscription =>
                subscription.ConsumerModule == WorkspacesModuleMetadata.Name &&
                subscription.HandlerName ==
                    WorkspacesModuleMetadata.EnrollmentClaimWithdrawnHandlerName).HandlerType);
        await using (var transaction = await dbContext.Database
            .BeginTransactionAsync().ConfigureAwait(false))
        {
            await withdrawalHandler.HandleAsync(
                new OrganizationEnrollmentClaimWithdrawnIntegrationEvent(
                    Guid.NewGuid(),
                    nowUtc.AddMinutes(3),
                    scopeId,
                    organizationId,
                    withdrawnLinkId,
                    withdrawnClaimId,
                    2),
                CancellationToken.None).ConfigureAwait(false);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        dbContext.ChangeTracker.Clear();
        WorkspaceStaffOnboarding withdrawnPersisted =
            await dbContext.StaffOnboardingApplications.SingleAsync(
                item => item.Id == withdrawnApplicationId).ConfigureAwait(false);
        WorkspaceStaffAccessPlan reusablePlan = await dbContext.StaffAccessPlans
            .SingleAsync(item => item.Id == withdrawnLinkId).ConfigureAwait(false);
        IReadOnlyList<WorkspaceStaffOnboarding> withdrawnActive =
            await repository.ListActiveBySourceAsync(
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                withdrawnLinkId,
                CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(WorkspaceStaffOnboardingState.Withdrawn, withdrawnPersisted.Status);
        Assert.Equal(2, withdrawnPersisted.ClaimVersion);
        Assert.Null(withdrawnPersisted.VerifiedAccountEmail);
        Assert.Null(withdrawnPersisted.DisplayName);
        Assert.Null(withdrawnPersisted.WorkEmail);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, reusablePlan.Status);
        Assert.Empty(withdrawnActive);

        Guid abandonedLinkId = Guid.NewGuid();
        Guid abandonedApplicationId = Guid.NewGuid();
        DateTimeOffset retentionNowUtc = DateTimeOffset.UtcNow;
        WorkspaceStaffOnboarding abandoned = WorkspaceStaffOnboarding.Create(
            abandonedApplicationId,
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            abandonedLinkId,
            Guid.NewGuid().ToString("D"),
            "abandoned@example.test",
            "Abandoned Applicant",
            null,
            "abandoned.staff@example.test",
            null,
            null,
            null,
            null,
            retentionNowUtc.AddHours(-4)).Value;
        WorkspaceStaffAccessPlan abandonedPlan = WorkspaceStaffAccessPlan.Create(
            abandonedLinkId,
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "front-desk",
            [],
            Guid.NewGuid().ToString("D"),
            retentionNowUtc.AddHours(-4)).Value;
        Assert.True(abandonedPlan.Activate(
            retentionNowUtc.AddHours(-4).AddSeconds(1)).IsSuccess);
        Assert.True(abandonedPlan.ObserveSourceExpired(
            retentionNowUtc.AddHours(-3),
            retentionNowUtc.AddHours(-3)).IsSuccess);
        dbContext.StaffOnboardingApplications.Add(abandoned);
        dbContext.StaffAccessPlans.Add(abandonedPlan);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();

        IRetentionExecutionContributor contributor = scope.ServiceProvider
            .GetServices<IRetentionExecutionContributor>()
            .Single(item =>
                item.Schedule.OwnerKey == "workspaces" &&
                item.Schedule.DataClassKey == "staff-onboarding-staging");
        DateTimeOffset retentionStartedAtUtc = DateTimeOffset.UtcNow;
        RetentionContributionResult retentionResult = await contributor.ExecuteAsync(
            new(
                RetentionExecutionContract.CurrentVersion,
                Guid.NewGuid(),
                scopeId,
                null,
                "workspaces",
                "staff-onboarding-staging",
                1,
                1,
                retentionStartedAtUtc,
                retentionStartedAtUtc.AddMinutes(10)),
            CancellationToken.None).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();

        WorkspaceStaffOnboarding redacted = await dbContext.StaffOnboardingApplications
            .SingleAsync(item => item.Id == abandonedApplicationId)
            .ConfigureAwait(false);
        WorkspaceStaffAccessPlan finalizedPlan = await dbContext.StaffAccessPlans
            .SingleAsync(item => item.Id == abandonedLinkId)
            .ConfigureAwait(false);

        Assert.Equal(RetentionContributionStatus.Completed, retentionResult.Status);
        Assert.Equal(1, retentionResult.ScannedCount);
        Assert.Equal(1, retentionResult.AffectedCount);
        Assert.Equal(WorkspaceStaffOnboardingState.Expired, redacted.Status);
        Assert.Null(redacted.VerifiedAccountEmail);
        Assert.Null(redacted.DisplayName);
        Assert.Null(redacted.WorkEmail);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, finalizedPlan.Status);
    }

    private static IHost CreateWorker(string connectionString)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Integration" });
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] = connectionString;
        builder.Configuration["Tenancy:Enabled"] = "true";
        builder.Configuration["Caching:Enabled"] = "false";
        builder.Configuration["NatsJetStream:Enabled"] = "false";
        builder.Configuration["NatsConsumers:Enabled"] = "false";
        builder.Configuration["Tasks:Worker:Enabled"] = "false";
        builder.Configuration["Worker:Modules:AccessControl"] = "true";
        builder.Configuration["Worker:Modules:Auth"] = "true";
        builder.Configuration["Worker:Modules:Organizations"] = "true";
        builder.Configuration["Worker:Modules:Properties"] = "true";
        builder.Configuration["Worker:Modules:Staff"] = "true";
        AuthTestConfiguration.ConfigureTokenHashing(builder.Configuration);
        builder.Logging.ClearProviders();

        builder.AddWorkerHost();
        ModuleCompositionValidationResult result = builder.ValidateModuleComposition();
        Assert.True(result.IsValid, result.Report);
        return builder.Build();
    }

    private static (WorkspaceStaffOnboarding Application, WorkspaceStaffAccessPlan Plan)
        CreateUnboundApplicationAndPlan(
            string scopeId,
            Guid linkId,
            string subjectId,
            DateTimeOffset nowUtc,
            string label)
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            linkId,
            subjectId,
            $"{label}@example.test",
            $"{label} Applicant",
            $"{label} Applicant Legal",
            $"{label}.staff@example.test",
            "+1 555 0177",
            $"EMP-{label}",
            "Receptionist",
            "Front desk",
            nowUtc).Value;
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            linkId,
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "front-desk",
            [],
            Guid.NewGuid().ToString("D"),
            nowUtc).Value;
        Assert.True(plan.Activate(nowUtc.AddMinutes(1)).IsSuccess);
        return (application, plan);
    }

    private static IIntegrationEventHandler<TIntegrationEvent> GetHandler<TIntegrationEvent>(
        IServiceProvider services,
        string handlerName)
        where TIntegrationEvent : IntegrationEvent
    {
        IIntegrationEventSubscriptionRegistry subscriptions = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>();
        Type handlerType = subscriptions.Subscriptions.Single(subscription =>
            subscription.ConsumerModule == WorkspacesModuleMetadata.Name &&
            subscription.HandlerName == handlerName).HandlerType;
        return (IIntegrationEventHandler<TIntegrationEvent>)services
            .GetRequiredService(handlerType);
    }

    private static async Task HandleInTransactionAsync(
        WorkspacesDbContext dbContext,
        Func<Task> handle)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync().ConfigureAwait(false);
        await handle().ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }
}

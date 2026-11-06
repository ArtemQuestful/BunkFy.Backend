namespace Integration.Tests;

using System.Globalization;
using System.Text.Json;
using BunkFy.Adapter.Abstractions;
using BunkFy.Host.Worker;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.LegalHolds;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Retention;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Domain.Models;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Domain.Retention;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Infrastructure;
using Gma.Framework.Tenancy;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Organizations.Contracts;
using Gma.Modules.Organizations.Domain.Aggregates;
using Gma.Modules.Organizations.Persistence;
using Gma.Modules.TaskRuntime.Persistence;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;
using OrganizationMembershipDomainRole =
    Gma.Modules.Organizations.Domain.Enums.OrganizationMembershipRole;

public sealed partial class RetentionControlPlaneIntegrationTests
{
    private const string HeldTenantId =
        "9b000000-0000-0000-0000-000000000001";
    private const string UnheldTenantId =
        "9b000000-0000-0000-0000-000000000002";
    private const string SensitiveHistoryDataClass =
        "sensitive-reservation-history";
    private const string RawPayloadDataClass = "raw-source-evidence";
    private const string GuestOperationalDataClass = "guest-operational";
    private const string GuestRetentionCompletedOutcome =
        "guests.guest-operational.completed";
    private const string GuestRetentionOwner = "guests";
    private const string ReservationOperationalDataClass =
        "reservation-operational";
    private const string ReservationRetentionCompletedOutcome =
        "reservations.reservation-operational.completed";
    private const string ReservationRetentionOwner = "reservations";
    private const string StaffEmploymentDataClass = "staff-employment";
    private const string StaffRetentionCompletedOutcome =
        "staff.staff-employment.completed";
    private const string StaffRetentionOwner = "staff";
    private static readonly Guid HeldPropertyId =
        Guid.Parse("9b000000-0000-0000-0000-000000000011");
    private static readonly Guid UnheldPropertyId =
        Guid.Parse("9b000000-0000-0000-0000-000000000012");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Scheduler_isolates_tenants_and_converges_after_legal_hold_release()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_retention_control_plane_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        using IHost worker = CreateWorker(postgreSql.GetConnectionString());
        await MigrateAsync(worker).ConfigureAwait(false);
        SeededCandidate held = await SeedTenantAsync(
            worker,
            HeldTenantId,
            HeldPropertyId,
            placeLegalHold: true,
            seedGuest: false).ConfigureAwait(false);
        SeededCandidate unheld = await SeedTenantAsync(
            worker,
            UnheldTenantId,
            UnheldPropertyId,
            placeLegalHold: false,
            seedGuest: true).ConfigureAwait(false);
        int expectedScheduleCount =
            await CountExpectedSchedulesAsync(worker)
                .ConfigureAwait(false);

        bool workerStarted = false;
        await worker.StartAsync().ConfigureAwait(false);
        workerStarted = true;
        try
        {
            IReadOnlyList<TaskRun> scheduledRuns =
                await WaitForScheduledRunsAsync(
                    worker,
                    expectedScheduleCount,
                    TimeSpan.FromMinutes(3)).ConfigureAwait(false);

            Assert.All(
                scheduledRuns,
                run => Assert.Equal(TaskRunStatus.Succeeded, run.Status));
            Assert.Equal(
                [HeldTenantId, UnheldTenantId],
                scheduledRuns
                    .Select(run => run.ScopeId!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray());

            await AssertInitialOutcomesAsync(
                worker,
                held,
                unheld).ConfigureAwait(false);
            await AssertGuestRetentionOutcomeAsync(
                worker,
                held).ConfigureAwait(false);
            await AssertGuestRetentionOutcomeAsync(
                worker,
                unheld).ConfigureAwait(false);
            await AssertReservationRetentionOutcomeAsync(
                worker,
                held).ConfigureAwait(false);
            await AssertReservationRetentionOutcomeAsync(
                worker,
                unheld).ConfigureAwait(false);
            await AssertStaffRetentionOutcomeAsync(
                worker,
                held).ConfigureAwait(false);
            await AssertStaffRetentionOutcomeAsync(
                worker,
                unheld).ConfigureAwait(false);

            await ReleaseLegalHoldAsync(worker, held).ConfigureAwait(false);
            Guid retryRunId = await EnqueueSensitiveHistoryRunAsync(
                worker,
                HeldTenantId).ConfigureAwait(false);
            TaskRun retryRun = await WaitForRunAsync(
                worker,
                retryRunId,
                TaskRunStatus.Succeeded,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            Assert.Equal(1, retryRun.Attempts);
            await AssertReleasedHoldOutcomeAsync(worker, held)
                .ConfigureAwait(false);
        }
        finally
        {
            if (workerStarted)
            {
                await worker.StopAsync().ConfigureAwait(false);
            }
        }
    }

    private static IHost CreateWorker(
        string connectionString,
        ISystemClock? systemClock = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                EnvironmentName = "Integration"
            });
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Configuration["Tenancy:Enabled"] = "true";
        builder.Configuration["NatsJetStream:Enabled"] = "false";
        builder.Configuration["NatsConsumers:Enabled"] = "false";
        builder.Configuration["Tasks:Worker:Enabled"] = "true";
        builder.Configuration["Tasks:Worker:WorkerGroups:0"] =
            RetentionModuleMetadata.WorkerGroup;
        builder.Configuration["Tasks:Worker:BatchSize"] = "1";
        builder.Configuration["Tasks:Worker:MaxConcurrency"] = "1";
        builder.Configuration["Tasks:Worker:PollInterval"] =
            "00:00:00.100";
        builder.Configuration["Tasks:Worker:LeaseDuration"] =
            "00:00:30";
        builder.Configuration["Tasks:Worker:HandlerTimeout"] =
            "00:00:30";
        builder.Configuration["Tasks:Worker:RetryBaseDelay"] =
            "00:00:00.100";
        builder.Configuration["Tasks:Worker:RetryMaxDelay"] =
            "00:00:01";
        builder.Configuration["Tasks:Worker:WorkerId"] =
            "retention-worker-test";
        builder.Configuration["Tasks:Worker:NodeId"] =
            "retention-worker-node";
        builder.Configuration["Tasks:Worker:TimeoutScannerEnabled"] =
            "false";
        builder.Configuration["Tasks:Worker:MetricsSamplerEnabled"] =
            "false";
        builder.Configuration["Tasks:Scheduler:Enabled"] = "true";
        builder.Configuration["Tasks:Scheduler:PollInterval"] =
            "00:00:00.100";
        builder.Configuration["Tasks:Scheduler:RequestedBy"] =
            "retention-integration-scheduler";
        builder.Configuration["Worker:Modules:Guests"] = "true";
        builder.Configuration["Worker:Modules:Ingestion"] = "true";
        builder.Configuration["Worker:Modules:Inventory"] = "true";
        builder.Configuration["Worker:Modules:AccessControl"] = "true";
        builder.Configuration["Worker:Modules:Auth"] = "true";
        builder.Configuration["Worker:Modules:Organizations"] = "true";
        builder.Configuration["Worker:Modules:Properties"] = "true";
        builder.Configuration["Worker:Modules:Reservations"] = "true";
        builder.Configuration["Worker:Modules:Retention"] = "true";
        builder.Configuration["Worker:Modules:Staff"] = "true";
        builder.Configuration["Worker:Modules:TaskRuntime"] = "true";
        builder.Configuration["Worker:Modules:Workspaces"] = "true";
        builder.Configuration["FileManagement:Enabled"] = "true";
        builder.Configuration["FileManagement:Provider"] = "Minio";
        builder.Configuration["FileManagement:AllowedContentTypes:0"] =
            "application/json";
        builder.Configuration["FileManagement:Minio:Endpoint"] =
            "localhost:9000";
        builder.Configuration["FileManagement:Minio:AccessKey"] = "test";
        builder.Configuration["FileManagement:Minio:SecretKey"] =
            "test-secret";
        builder.Configuration["FileManagement:Minio:BucketName"] =
            "retention-test";
        builder.Configuration["FileManagement:Minio:UseSsl"] = "false";
        builder.Configuration[
            "FileManagement:Minio:CreateBucketIfMissing"] = "false";
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
            options.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.Logging.AddFilter(
            "Microsoft.EntityFrameworkCore",
            LogLevel.None);

        AuthTestConfiguration.ConfigureTokenHashing(
            builder.Configuration);
        builder.AddWorkerHost();
        if (systemClock is not null)
        {
            builder.Services.RemoveAll<ISystemClock>();
            builder.Services.AddSingleton(systemClock);
        }

        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
        ModuleCompositionValidationResult composition =
            builder.ValidateModuleComposition();
        Assert.True(composition.IsValid, composition.Report);
        return builder.Build();
    }

    private static async Task MigrateAsync(IHost worker)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<AccessControlDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<IngestionDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<GuestsDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<InventoryDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<OrganizationsDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<RetentionDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<StaffDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<TaskRuntimeDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>()
            .Database.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task<SeededCandidate> SeedTenantAsync(
        IHost worker,
        string tenantId,
        Guid propertyId,
        bool placeLegalHold,
        bool seedGuest)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(tenantId);
        IIntegrationEventSubscriptionRegistry subscriptions =
            scope.ServiceProvider
                .GetRequiredService<IIntegrationEventSubscriptionRegistry>();

        IntegrationEventSubscription organizationSubscription =
            subscriptions.Subscriptions.Single(item =>
                item.ConsumerModule == RetentionModuleMetadata.Name &&
                item.EventType ==
                typeof(OrganizationChangedIntegrationEvent));
        var organizationHandler =
            (IIntegrationEventHandler<OrganizationChangedIntegrationEvent>)
            scope.ServiceProvider.GetRequiredService(
                organizationSubscription.HandlerType);
        RetentionDbContext retention =
            scope.ServiceProvider.GetRequiredService<RetentionDbContext>();
        await using (var transaction = await retention.Database
            .BeginTransactionAsync().ConfigureAwait(false))
        {
            await organizationHandler.HandleAsync(
                new OrganizationChangedIntegrationEvent(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    tenantId,
                    Guid.NewGuid(),
                    OrganizationChange.Created,
                    OrganizationStatus.Active,
                    organizationVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
            await retention.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        var propertyCreated = new PropertyCreatedIntegrationEvent(
            Guid.NewGuid(),
            tenantId,
            DateTimeOffset.UtcNow,
            propertyId,
            "Retention Test Property",
            $"retention-{propertyId:N}",
            "Etc/UTC",
            PropertyStatus.Active,
            propertyVersion: 1);
        IntegrationEventSubscription propertySubscription =
            subscriptions.Subscriptions.Single(item =>
                item.ConsumerModule == IngestionModuleMetadata.Name &&
                item.EventType == typeof(PropertyCreatedIntegrationEvent));
        var propertyHandler =
            (IIntegrationEventHandler<PropertyCreatedIntegrationEvent>)
            scope.ServiceProvider.GetRequiredService(
                propertySubscription.HandlerType);
        IngestionDbContext ingestion =
            scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            ingestion,
            token => propertyHandler.HandleAsync(propertyCreated, token))
            .ConfigureAwait(false);
        await ApplyGuestPropertyCreatedAsync(
            scope.ServiceProvider,
            subscriptions,
            propertyCreated).ConfigureAwait(false);
        await ApplyReservationPropertyCreatedAsync(
            scope.ServiceProvider,
            subscriptions,
            propertyCreated).ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            GuestsModuleMetadata.Name,
            tenantId,
            propertyId,
            propertyVersion: 2).ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            ReservationsModuleMetadata.Name,
            tenantId,
            propertyId,
            propertyVersion: 2).ConfigureAwait(false);

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        Guid reservationId = await SeedReservationAsync(
            scope.ServiceProvider,
            tenantId,
            propertyId,
            nowUtc).ConfigureAwait(false);
        SeededStaff seededStaff = await SeedStaffAsync(
            scope.ServiceProvider,
            tenantId,
            nowUtc).ConfigureAwait(false);
        DateTimeOffset proposalCreatedAtUtc = nowUtc.AddDays(-10);
        DateTimeOffset proposalCompletedAtUtc =
            proposalCreatedAtUtc.AddDays(1);
        Guid connectionId = Guid.NewGuid();
        Guid receiptId = Guid.NewGuid();
        Guid payloadFileId = Guid.NewGuid();
        ingestion.AdapterConnections.Add(AdapterConnection.Create(
            connectionId,
            tenantId,
            propertyId,
            "retention.integration",
            AdapterExecutionMode.Push,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://retention-integration",
            secretReference: null,
            nowUtc).Value);

        ObservationCountryPolicyEvidence evidence =
            ObservationCountryPolicyEvidence.Create(
                "GB",
                "integration-policy",
                1,
                "eu",
                "eu-only",
                "integration-retention",
                1,
                new string('a', 64),
                "reservation-import",
                "adapter-ingress",
                "property-policy",
                proposalCreatedAtUtc,
                nowUtc.AddDays(30),
                proposalCreatedAtUtc).Value;
        ObservationReceipt receipt = ObservationReceipt.Create(
            receiptId,
            tenantId,
            propertyId,
            connectionId,
            runId: null,
            Guid.NewGuid(),
            "reservation",
            $"external-{receiptId:N}",
            "revision-1",
            $"reservation:{receiptId:N}:revision-1",
            new string('b', 64),
            evidence,
            payloadFileId,
            nowUtc.AddDays(30),
            proposalCreatedAtUtc,
            proposalCreatedAtUtc,
            proposalCreatedAtUtc).Value;
        Assert.True(
            receipt.MarkProcessed(
                proposalCreatedAtUtc.AddMinutes(1)).IsSuccess);
        ingestion.ObservationReceipts.Add(receipt);

        const string sensitiveDiff = /*lang=json,strict*/ "{\"guest\":\"Sensitive\"}";
        ChangeProposal proposal = ChangeProposal.Create(
            Guid.NewGuid(),
            tenantId,
            propertyId,
            connectionId,
            receiptId,
            Guid.NewGuid(),
            payloadFileId,
            1,
            "retention-integration",
            sensitiveDiff,
            proposalCreatedAtUtc).Value;
        Assert.True(proposal.BeginApply(
            "integration:retention",
            Guid.NewGuid(),
            proposal.Version,
            proposalCompletedAtUtc.AddMinutes(-1)).IsSuccess);
        Assert.True(proposal.MarkFailed(
            "Integration retention terminal state",
            proposal.Version,
            proposalCompletedAtUtc.AddDays(1),
            proposalCompletedAtUtc).IsSuccess);
        ingestion.ChangeProposals.Add(proposal);

        LegalHold? legalHold = null;
        if (placeLegalHold)
        {
            legalHold = LegalHold.Place(
                Guid.NewGuid(),
                tenantId,
                propertyId,
                "Integration legal hold",
                "integration:retention",
                nowUtc.AddHours(-1)).Value;
            ingestion.LegalHolds.Add(legalHold);
        }

        Guid? guestId = null;
        if (seedGuest)
        {
            guestId = Guid.NewGuid();
            DateOnly checkedOutBusinessDate =
                DateOnly.FromDateTime(nowUtc.UtcDateTime.AddDays(-400));
            GuestProfile guest = GuestProfile.Create(
                guestId.Value,
                tenantId,
                propertyId,
                "Retention Candidate",
                "Retention Candidate Legal",
                "retention-candidate@example.test",
                "+44 20 7946 0958",
                new DateOnly(1990, 1, 1),
                "GB",
                "en-GB",
                "Sensitive retention integration note",
                "integration:retention",
                Guid.NewGuid(),
                nowUtc.AddDays(-500)).Value;
            GuestsDbContext guests =
                scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
            guests.GuestProfiles.Add(guest);
            guests.StayHistory.Add(new(
                tenantId,
                guestId.Value,
                Guid.NewGuid(),
                propertyId,
                GuestStayRole.Primary,
                checkedOutBusinessDate.AddDays(-2),
                checkedOutBusinessDate,
                GuestStayStatus.CheckedOut,
                checkedOutBusinessDate.AddDays(-2),
                noShowBusinessDate: null,
                checkedOutBusinessDate,
                isCurrentParticipant: false,
                reservationVersion: 1,
                GuestsModuleMetadata.StayHistoryProjectionVersion));
            await guests.SaveChangesAsync().ConfigureAwait(false);
        }

        await ingestion.SaveChangesAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<RetentionDbContext>()
            .SaveChangesAsync().ConfigureAwait(false);
        return new(
            tenantId,
            propertyId,
            proposal.Id,
            legalHold?.Id,
            guestId,
            reservationId,
            seededStaff.StaffMemberId,
            seededStaff.SubjectId,
            seededStaff.OnboardingId,
            seededStaff.AccessProcessId,
            seededStaff.AccessPlanId);
    }

    private static async Task<SeededStaff> SeedStaffAsync(
        IServiceProvider services,
        string tenantId,
        DateTimeOffset nowUtc)
    {
        DateTimeOffset departedAtUtc = nowUtc.AddDays(-400);
        string subjectId =
            $"staff-retention-{Guid.NewGuid():N}";
        StaffMember member = StaffMember.Create(
            Guid.NewGuid(),
            tenantId,
            "Staff Retention Candidate",
            "Staff Retention Candidate Legal",
            "staff-retention@example.test",
            "+44 20 7946 0789",
            "RETENTION-STAFF",
            "Operations manager",
            "Operations",
            subjectId,
            "integration:retention",
            Guid.NewGuid(),
            nowUtc.AddDays(-500)).Value;
        Assert.True(member.Depart(
            DateOnly.FromDateTime(departedAtUtc.UtcDateTime),
            member.Version,
            "integration:retention",
            "Employment ended",
            Guid.NewGuid(),
            [],
            departedAtUtc).IsSuccess);
        member.ClearDomainEvents();

        await services.GetRequiredService<IStaffMemberRepository>()
            .AddAsync(member, CancellationToken.None)
            .ConfigureAwait(false);
        StaffEmploymentGovernance governance =
            StaffEmploymentGovernance.Configure(
                tenantId,
                member.Id,
                member.Version,
                CountryPolicyIntegrationTestData
                    .CreateStaffGovernanceBinding(nowUtc),
                CountryPolicyIntegrationTestData
                    .CreateStaffGovernanceAcknowledgements(),
                "integration:retention",
                nowUtc).Value;
        StaffDbContext staff =
            services.GetRequiredService<StaffDbContext>();
        staff.EmploymentGovernance.Add(governance);
        await staff.SaveChangesAsync().ConfigureAwait(false);
        return await SeedWorkspaceStaffCorrelationAsync(
                services,
                tenantId,
                member,
                subjectId,
                nowUtc)
            .ConfigureAwait(false);
    }

    private static async Task<SeededStaff>
        SeedWorkspaceStaffCorrelationAsync(
            IServiceProvider services,
            string tenantId,
            StaffMember member,
            string subjectId,
            DateTimeOffset nowUtc)
    {
        Guid organizationId = Guid.Parse(tenantId);
        DateTimeOffset onboardingAtUtc = nowUtc.AddDays(-500).AddMinutes(1);
        const string actorId = "system:retention-integration";
        OrganizationsDbContext organizations =
            services.GetRequiredService<OrganizationsDbContext>();
        organizations.Organizations.Add(
            Organization.Create(
                organizationId,
                "Retention test workspace",
                $"retention-{organizationId:N}",
                actorId,
                Guid.NewGuid(),
                onboardingAtUtc.AddSeconds(-1)).Value);
        organizations.Memberships.Add(
            OrganizationMembership.Create(
                Guid.NewGuid(),
                organizationId,
                $"owner-{organizationId:N}",
                OrganizationMembershipDomainRole.Owner,
                actorId,
                Guid.NewGuid(),
                onboardingAtUtc.AddSeconds(-1)).Value);
        organizations.Memberships.Add(
            OrganizationMembership.Create(
                Guid.NewGuid(),
                organizationId,
                subjectId,
                OrganizationMembershipDomainRole.Member,
                actorId,
                Guid.NewGuid(),
                onboardingAtUtc.AddSeconds(-1)).Value);
        await organizations.SaveChangesAsync().ConfigureAwait(false);

        Guid sourceId = Guid.NewGuid();
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                Guid.NewGuid(),
                tenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                subjectId,
                "staff-retention@example.test",
                "Staff Retention Candidate",
                "Staff Retention Candidate Legal",
                "staff-retention@example.test",
                "+44 20 7946 0789",
                "RETENTION-STAFF",
                "Operations manager",
                "Operations",
                onboardingAtUtc).Value;
        WorkspacesDbContext workspaces =
            services.GetRequiredService<WorkspacesDbContext>();
        workspaces.StaffOnboardingApplications.Add(onboarding);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);

        // Model the committed provisioning history before the Staff departure.
        // The journal and anchor belong to the application, not the invitation.
        Guid resolutionEventId = Guid.NewGuid();
        StaffDbContext staff = services.GetRequiredService<StaffDbContext>();
        await services.GetRequiredService<
                IStaffOnboardingProvisioningOperationRepository>()
            .AddAsync(new StaffMemberMutationOperationRecord(
                onboarding.Id,
                tenantId,
                member.Id,
                StaffMemberMutationKind.OnboardingProvision,
                ExpectedVersion: 1,
                RequestFingerprint: new string('a', 64),
                StaffStatus.Active,
                ResultVersion: 1,
                onboardingAtUtc.AddSeconds(1)), CancellationToken.None)
            .ConfigureAwait(false);
        await services.GetRequiredService<
                IStaffIdentityProvisioningAnchorRepository>()
            .AddAsync(new StaffIdentityProvisioningAnchorRecord(
                tenantId,
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
                onboarding.Id,
                member.Id,
                onboardingAtUtc.AddSeconds(1),
                resolutionEventId), CancellationToken.None)
            .ConfigureAwait(false);
        await staff.SaveChangesAsync().ConfigureAwait(false);

        Assert.True(onboarding.ObserveInvitationAccepted(
            onboardingAtUtc.AddSeconds(1)).IsSuccess);
        Assert.True(onboarding.MarkStaffReady(
            member.Id,
            resolutionEventId,
            Guid.NewGuid(),
            onboardingAtUtc.AddSeconds(2)).IsSuccess);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);
        Assert.True(onboarding.Complete(
            onboardingAtUtc.AddSeconds(3)).IsSuccess);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);

        await services.GetRequiredService<
                IStaffIdentityProvisioningAnchorResolutionRepository>()
            .AddAsync(new StaffIdentityProvisioningAnchorResolutionRecord(
                tenantId,
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
                onboarding.Id,
                member.Id,
                onboarding.Version,
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .CompletedRedacted,
                resolutionEventId,
                onboardingAtUtc.AddSeconds(4)), CancellationToken.None)
            .ConfigureAwait(false);
        await staff.SaveChangesAsync().ConfigureAwait(false);
        Assert.True(onboarding.ObserveResolution(
            resolutionEventId,
            member.Id,
            onboarding.Version,
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            onboardingAtUtc.AddSeconds(5)).IsSuccess);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);

        WorkspaceStaffAccessPlan plan =
            WorkspaceStaffAccessPlan.Create(
                sourceId,
                tenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                Guid.NewGuid(),
                "retention-integration",
                [],
                subjectId,
                nowUtc).Value;
        Assert.True(
            plan.Supersede(
                nowUtc.AddSeconds(3)).IsSuccess);

        WorkspaceStaffAccessProcess process =
            WorkspaceStaffAccessProcess.Create(
                Guid.NewGuid(),
                tenantId,
                member.Id,
                subjectId,
                WorkspaceStaffAccessTargetState.Departed,
                member.Version,
                member.DepartureEffectiveOn!.Value,
                subjectId,
                [],
                nowUtc).Value;
        Assert.True(
            process.MarkAwaitingStaffCommit(
                nowUtc.AddSeconds(1)).IsSuccess);
        Assert.True(
            process.ObserveStaffCommit(
                nowUtc.AddSeconds(2)).IsSuccess);

        workspaces.StaffAccessPlans.Add(plan);
        workspaces.StaffAccessProcesses.Add(process);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);
        return new(
            member.Id,
            subjectId,
            onboarding.Id,
            process.Id,
            plan.Id);
    }

    private static async Task ApplyGuestPropertyCreatedAsync(
        IServiceProvider services,
        IIntegrationEventSubscriptionRegistry subscriptions,
        PropertyCreatedIntegrationEvent propertyCreated)
    {
        IntegrationEventSubscription subscription =
            subscriptions.Subscriptions.Single(item =>
                item.ConsumerModule == GuestsModuleMetadata.Name &&
                item.EventType == typeof(PropertyCreatedIntegrationEvent));
        var handler =
            (IIntegrationEventHandler<PropertyCreatedIntegrationEvent>)
            services.GetRequiredService(subscription.HandlerType);
        GuestsDbContext guests =
            services.GetRequiredService<GuestsDbContext>();
        await using var transaction = await guests.Database
            .BeginTransactionAsync().ConfigureAwait(false);
        await handler.HandleAsync(
            propertyCreated,
            CancellationToken.None).ConfigureAwait(false);
        await guests.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task ApplyReservationPropertyCreatedAsync(
        IServiceProvider services,
        IIntegrationEventSubscriptionRegistry subscriptions,
        PropertyCreatedIntegrationEvent propertyCreated)
    {
        IntegrationEventSubscription subscription =
            subscriptions.Subscriptions.Single(item =>
                item.ConsumerModule ==
                    ReservationsModuleMetadata.Name &&
                item.EventType ==
                    typeof(PropertyCreatedIntegrationEvent));
        var handler =
            (IIntegrationEventHandler<PropertyCreatedIntegrationEvent>)
            services.GetRequiredService(subscription.HandlerType);
        ReservationsDbContext reservations =
            services.GetRequiredService<ReservationsDbContext>();
        IDbContextTransaction? ownedTransaction =
            reservations.Database.CurrentTransaction is null
            ? await reservations.Database.BeginTransactionAsync()
                .ConfigureAwait(false)
            : null;

        try
        {
            await handler.HandleAsync(
                propertyCreated,
                CancellationToken.None).ConfigureAwait(false);
            if (ownedTransaction is not null)
            {
                await reservations.SaveChangesAsync().ConfigureAwait(false);
                await ownedTransaction.CommitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<Guid> SeedReservationAsync(
        IServiceProvider services,
        string tenantId,
        Guid propertyId,
        DateTimeOffset nowUtc)
    {
        Guid detailsEventId = Guid.NewGuid();
        DateOnly departure = DateOnly.FromDateTime(
            nowUtc.UtcDateTime.AddDays(-400));
        Reservation reservation = Reservation.Create(
            Guid.NewGuid(),
            tenantId,
            propertyId,
            Guid.NewGuid(),
            departure.AddDays(-2),
            departure,
            [Guid.NewGuid()],
            "Reservation Retention Candidate",
            "reservation-retention@example.test",
            "+44 20 7946 0123",
            guestCount: 1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: "Sensitive reservation retention note",
            Guid.NewGuid(),
            detailsEventId,
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: "integration:retention",
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            nowUtc.AddDays(-500)).Value;
        ReservationDetailsChangedDomainEvent detailsChanged =
            Assert.Single(
                reservation.DomainEvents
                    .OfType<ReservationDetailsChangedDomainEvent>());
        Assert.True(reservation.RejectAllocation(
            reservation.AllocationRequestId,
            ReservationAllocationRejection.AllocationConflict,
            Guid.NewGuid(),
            nowUtc.AddDays(-400)).IsSuccess);

        await services.GetRequiredService<IReservationRepository>()
            .AddAsync(reservation, CancellationToken.None)
            .ConfigureAwait(false);
        await services
            .GetRequiredService<IReservationDetailsHistoryWriter>()
            .AppendAsync(detailsChanged, CancellationToken.None)
            .ConfigureAwait(false);
        await services.GetRequiredService<ReservationsDbContext>()
            .SaveChangesAsync().ConfigureAwait(false);
        return reservation.Id;
    }

    private static async Task<int> CountExpectedSchedulesAsync(
        IHost worker)
    {
        ScheduledTaskDefinition[] schedules =
            await GetExpectedRetentionSchedulesAsync(worker)
                .ConfigureAwait(false);
        List<string> staffScheduleScopes = [];
        foreach (ScheduledTaskDefinition definition in schedules)
        {
            ExecuteRetentionSchedulePayload payload =
                JsonSerializer.Deserialize<
                    ExecuteRetentionSchedulePayload>(
                    definition.PayloadJson)
                ?? throw new InvalidOperationException(
                    "Retention schedule payload is invalid.");
            if (payload.OwnerKey == StaffRetentionOwner &&
                payload.DataClassKey == StaffEmploymentDataClass)
            {
                staffScheduleScopes.Add(
                    definition.ScopeId ??
                    throw new InvalidOperationException(
                        "Tenant retention schedule is missing its scope."));
            }
        }

        Assert.Equal(2, staffScheduleScopes.Count);
        Assert.Equal(
            [HeldTenantId, UnheldTenantId],
            staffScheduleScopes
                .Order(StringComparer.Ordinal)
                .ToArray());
        return schedules.Length;
    }

    private static async Task<ScheduledTaskDefinition[]>
        GetExpectedRetentionSchedulesAsync(IHost worker)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        ScheduledTaskDefinition[][] providerSchedules =
            await Task.WhenAll(
                scope.ServiceProvider
                    .GetServices<ITaskScheduleProvider>()
                    .Select(async provider =>
                        await provider
                            .GetSchedulesAsync(CancellationToken.None)
                            .ToArrayAsync(CancellationToken.None)))
                .ConfigureAwait(false);
        return providerSchedules
            .SelectMany(definitions => definitions)
            .Where(definition =>
                definition.ModuleName ==
                    RetentionModuleMetadata.Name &&
                definition.TaskName ==
                    ExecuteRetentionSchedulePayload.TaskName)
            .ToArray();
    }

    private static async Task<IReadOnlyList<TaskRun>>
        WaitForScheduledRunsAsync(
            IHost worker,
            int expectedCount,
            TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        TaskRun[] observedRuns = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = worker.Services.CreateScope();
            observedRuns = await scope.ServiceProvider
                .GetRequiredService<TaskRuntimeDbContext>()
                .TaskRuns.AsNoTracking()
                .Where(run =>
                    run.ModuleName == RetentionModuleMetadata.Name &&
                    run.TaskName ==
                    ExecuteRetentionSchedulePayload.TaskName)
                .OrderBy(run => run.CreatedAtUtc)
                .ToArrayAsync().ConfigureAwait(false);
            TaskRun? failed = observedRuns.FirstOrDefault(run =>
                run.Status is TaskRunStatus.Failed or
                    TaskRunStatus.Canceled or
                    TaskRunStatus.TimedOut);
            if (failed is not null)
            {
                string details = await DescribeFailedRunAsync(
                    worker,
                    failed).ConfigureAwait(false);
                Assert.True(failed is null, details);
            }

            if (observedRuns.Length == expectedCount &&
                observedRuns.All(
                    run => run.Status == TaskRunStatus.Succeeded))
            {
                return observedRuns;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Expected {expectedCount} successful scheduled retention runs. " +
            $"Observed {observedRuns.Length}: " +
            DescribeScheduledRuns(observedRuns));
    }

    private static string DescribeScheduledRuns(IEnumerable<TaskRun> runs) =>
        string.Join(
            ", ",
            runs.Select(run =>
            {
                ExecuteRetentionSchedulePayload? payload =
                    JsonSerializer.Deserialize<ExecuteRetentionSchedulePayload>(
                        run.Payload);
                return $"{run.ScopeId}:{payload?.OwnerKey}/" +
                    $"{payload?.DataClassKey}={run.Status}";
            }));

    private static async Task<string> DescribeFailedRunAsync(
        IHost worker,
        TaskRun run)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(run.ScopeId!);
        RetentionExecution? central = await scope.ServiceProvider
            .GetRequiredService<RetentionDbContext>()
            .Executions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == run.Id)
            .ConfigureAwait(false);
        IngestionRetentionExecution? owner = await scope.ServiceProvider
            .GetRequiredService<IngestionDbContext>()
            .RetentionExecutions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == run.Id)
            .ConfigureAwait(false);
        GuestRetentionExecution? guestOwner = await scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>()
            .RetentionExecutions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == run.Id)
            .ConfigureAwait(false);
        ReservationRetentionExecution? reservationOwner =
            await scope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>()
                .RetentionExecutions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == run.Id)
                .ConfigureAwait(false);

        return $"Retention run {run.Id} failed: {run.LastError}; " +
            $"scope={run.ScopeId}; attempts={run.Attempts}; " +
            $"payload={run.Payload}; " +
            $"central={Describe(central)}; " +
            $"ingestionOwner={Describe(owner)}; " +
            $"guestOwner={Describe(guestOwner)}; " +
            $"reservationOwner={Describe(reservationOwner)}";
    }

    private static string Describe(RetentionExecution? execution) =>
        execution is null
            ? "missing"
            : $"state:{execution.State},attempt:{execution.Attempt}," +
              $"outcome:{execution.OutcomeCode ?? "none"}";

    private static string Describe(IngestionRetentionExecution? execution) =>
        execution is null
            ? "missing"
            : $"state:{execution.State},attempt:{execution.Attempt}," +
              $"affected:{execution.AffectedCount}," +
              $"remaining:{execution.RemainingCount?.ToString(CultureInfo.InvariantCulture) ?? "none"}," +
              $"outcome:{execution.OutcomeCode ?? "none"}";

    private static string Describe(GuestRetentionExecution? execution) =>
        execution is null
            ? "missing"
            : $"state:{execution.State},attempt:{execution.Attempt}," +
              $"affected:{execution.AffectedCount}," +
              $"scanned:{execution.ScannedCount?.ToString(CultureInfo.InvariantCulture) ?? "none"}," +
              $"remaining:{execution.RemainingCount?.ToString(CultureInfo.InvariantCulture) ?? "none"}," +
              $"outcome:{execution.OutcomeCode ?? "none"}";

    private static string Describe(
        ReservationRetentionExecution? execution) =>
        execution is null
            ? "missing"
            : $"state:{execution.State},attempt:{execution.Attempt}," +
              $"affected:{execution.AffectedCount}," +
              $"scanned:{execution.ScannedCount?.ToString(CultureInfo.InvariantCulture) ?? "none"}," +
              $"remaining:{execution.RemainingCount?.ToString(CultureInfo.InvariantCulture) ?? "none"}," +
              $"outcome:{execution.OutcomeCode ?? "none"}";

    private static async Task<TaskRun> WaitForRunAsync(
        IHost worker,
        Guid runId,
        TaskRunStatus expectedStatus,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = worker.Services.CreateScope();
            TaskRun? run = await scope.ServiceProvider
                .GetRequiredService<TaskRuntimeDbContext>()
                .TaskRuns.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == runId)
                .ConfigureAwait(false);
            if (run?.Status == expectedStatus)
            {
                return run;
            }

            if (run?.Status is TaskRunStatus.Failed or
                TaskRunStatus.Canceled or
                TaskRunStatus.TimedOut)
            {
                throw new InvalidOperationException(
                    await DescribeFailedRunAsync(worker, run)
                        .ConfigureAwait(false));
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Retention run {runId} did not reach {expectedStatus}.");
    }

    private static async Task AssertInitialOutcomesAsync(
        IHost worker,
        SeededCandidate held,
        SeededCandidate unheld)
    {
        await AssertTenantOutcomeAsync(
            worker,
            held,
            RetentionExecutionState.Blocked,
            "ingestion.sensitive-history.legal-hold",
            expectedAffectedCount: 0,
            expectedRemainingCount: 1,
            expectedProposalRedacted: false,
            expectedOwnerReceiptCount: 2).ConfigureAwait(false);
        await AssertTenantOutcomeAsync(
            worker,
            unheld,
            RetentionExecutionState.Completed,
            "ingestion.sensitive-history.completed",
            expectedAffectedCount: 1,
            expectedRemainingCount: 0,
            expectedProposalRedacted: true,
            expectedOwnerReceiptCount: 2).ConfigureAwait(false);
    }

    private static async Task AssertReleasedHoldOutcomeAsync(
        IHost worker,
        SeededCandidate held) =>
        await AssertTenantOutcomeAsync(
            worker,
            held,
            RetentionExecutionState.Completed,
            "ingestion.sensitive-history.completed",
            expectedAffectedCount: 1,
            expectedRemainingCount: 0,
            expectedProposalRedacted: true,
            expectedOwnerReceiptCount: 3).ConfigureAwait(false);

    private static async Task AssertTenantOutcomeAsync(
        IHost worker,
        SeededCandidate candidate,
        RetentionExecutionState expectedState,
        string expectedOutcomeCode,
        int expectedAffectedCount,
        int expectedRemainingCount,
        bool expectedProposalRedacted,
        int expectedOwnerReceiptCount)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(candidate.TenantId);
        RetentionDbContext retention =
            scope.ServiceProvider.GetRequiredService<RetentionDbContext>();
        RetentionScheduleState[] scheduleStates =
            await retention.ScheduleStates
                .AsNoTracking()
                .OrderBy(state => state.DataClassKey)
                .ToArrayAsync().ConfigureAwait(false);
        int expectedScheduleCount =
            (await GetExpectedRetentionSchedulesAsync(worker)
                .ConfigureAwait(false))
            .Count(definition =>
                definition.ScopeId == candidate.TenantId);
        Assert.Equal(expectedScheduleCount, scheduleStates.Length);
        Assert.Contains(
            scheduleStates,
            state => state.DataClassKey == RawPayloadDataClass);
        Assert.Contains(
            scheduleStates,
            state =>
                state.OwnerKey == GuestRetentionOwner &&
                state.DataClassKey == GuestOperationalDataClass);
        Assert.Contains(
            scheduleStates,
            state =>
                state.OwnerKey == ReservationRetentionOwner &&
                state.DataClassKey ==
                    ReservationOperationalDataClass);
        RetentionScheduleState sensitive = Assert.Single(
            scheduleStates,
            state => state.DataClassKey == SensitiveHistoryDataClass);
        Assert.Equal(expectedState, sensitive.State);
        Assert.Equal(expectedOutcomeCode, sensitive.OutcomeCode);
        Assert.Equal(expectedAffectedCount, sensitive.LastAffectedCount);
        Assert.Equal(expectedRemainingCount, sensitive.LastRemainingCount);
        Assert.Equal(
            expectedState == RetentionExecutionState.Blocked,
            sensitive.HoldReviewDueAtUtc.HasValue);

        IngestionDbContext ingestion =
            scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        ChangeProposal proposal = await ingestion.ChangeProposals
            .AsNoTracking()
            .SingleAsync(item => item.Id == candidate.ProposalId)
            .ConfigureAwait(false);
        Assert.Equal(expectedProposalRedacted, proposal.Diff is null);
        Assert.Equal(
            expectedOwnerReceiptCount,
            await ingestion.RetentionExecutions.CountAsync()
                .ConfigureAwait(false));
        IngestionRetentionExecution ownerReceipt =
            await ingestion.RetentionExecutions
                .AsNoTracking()
                .Where(item =>
                    item.DataClassKey == SensitiveHistoryDataClass)
                .OrderByDescending(item => item.StartedAtUtc)
                .FirstAsync().ConfigureAwait(false);
        Assert.Equal(expectedOutcomeCode, ownerReceipt.OutcomeCode);
        Assert.Equal(expectedAffectedCount, ownerReceipt.AffectedCount);
        Assert.Equal(expectedRemainingCount, ownerReceipt.RemainingCount);
    }

    private static async Task AssertGuestRetentionOutcomeAsync(
        IHost worker,
        SeededCandidate candidate)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(candidate.TenantId);
        RetentionScheduleState schedule = await scope.ServiceProvider
            .GetRequiredService<RetentionDbContext>()
            .ScheduleStates.AsNoTracking()
            .SingleAsync(state =>
                state.OwnerKey == GuestRetentionOwner &&
                state.DataClassKey == GuestOperationalDataClass)
            .ConfigureAwait(false);
        int expectedAffectedCount = candidate.GuestId.HasValue ? 1 : 0;
        Assert.Equal(RetentionExecutionState.Completed, schedule.State);
        Assert.Equal(GuestRetentionCompletedOutcome, schedule.OutcomeCode);
        Assert.Equal(expectedAffectedCount, schedule.LastAffectedCount);
        Assert.Equal(expectedAffectedCount, schedule.LastScannedCount);
        Assert.Equal(0, schedule.LastRemainingCount);

        GuestsDbContext guests =
            scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        GuestRetentionExecution owner = await guests.RetentionExecutions
            .AsNoTracking()
            .SingleAsync(execution => execution.Id == schedule.LastExecutionId)
            .ConfigureAwait(false);
        Assert.Equal(
            GuestRetentionExecutionState.Completed,
            owner.State);
        Assert.Equal(GuestRetentionCompletedOutcome, owner.OutcomeCode);
        Assert.Equal(expectedAffectedCount, owner.AffectedCount);
        Assert.Equal(expectedAffectedCount, owner.ScannedCount);
        Assert.Equal(0, owner.RemainingCount);

        if (!candidate.GuestId.HasValue)
        {
            Assert.Empty(await guests.GuestProfiles.AsNoTracking()
                .ToArrayAsync().ConfigureAwait(false));
            Assert.Empty(await guests.RetentionAnonymisationReceipts
                .AsNoTracking()
                .ToArrayAsync().ConfigureAwait(false));
            return;
        }

        GuestProfile profile = await guests.GuestProfiles
            .AsNoTracking()
            .SingleAsync(item => item.Id == candidate.GuestId.Value)
            .ConfigureAwait(false);
        Assert.Equal(GuestProfileState.Anonymised, profile.Status);
        Assert.Equal(GuestProfile.AnonymisedDisplayName, profile.DisplayName);
        Assert.Null(profile.LegalName);
        Assert.Null(profile.Email);
        Assert.Null(profile.Phone);
        Assert.Null(profile.DateOfBirth);
        Assert.Null(profile.NationalityCountryCode);
        Assert.Null(profile.PreferredLanguageTag);
        Assert.Null(profile.Notes);

        GuestRetentionAnonymisationReceipt receipt =
            await guests.RetentionAnonymisationReceipts
                .AsNoTracking()
                .SingleAsync(item =>
                    item.GuestId == candidate.GuestId.Value)
                .ConfigureAwait(false);
        Assert.Equal(owner.Id, receipt.ExecutionId);
        GuestAnonymisationTombstone tombstone =
            await guests.AnonymisationTombstones
                .AsNoTracking()
                .SingleAsync(item =>
                    item.Id == candidate.GuestId.Value)
                .ConfigureAwait(false);
        Assert.Equal(
            GuestAnonymisationAuthority.Retention,
            tombstone.Authority);
        Assert.True(tombstone.MatchesRetention(receipt));
    }

    private static async Task AssertStaffRetentionOutcomeAsync(
        IHost worker,
        SeededCandidate candidate)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(candidate.TenantId);
        RetentionScheduleState schedule = await scope.ServiceProvider
            .GetRequiredService<RetentionDbContext>()
            .ScheduleStates.AsNoTracking()
            .SingleAsync(state =>
                state.OwnerKey == StaffRetentionOwner &&
                state.DataClassKey == StaffEmploymentDataClass)
            .ConfigureAwait(false);
        Assert.Equal(RetentionExecutionState.Completed, schedule.State);
        Assert.Equal(
            StaffRetentionCompletedOutcome,
            schedule.OutcomeCode);
        Assert.Equal(1, schedule.LastAffectedCount);
        Assert.Equal(1, schedule.LastScannedCount);
        Assert.Equal(0, schedule.LastRemainingCount);

        StaffDbContext staff =
            scope.ServiceProvider.GetRequiredService<StaffDbContext>();
        StaffRetentionExecution owner = await staff.RetentionExecutions
            .AsNoTracking()
            .SingleAsync(execution =>
                execution.Id == schedule.LastExecutionId)
            .ConfigureAwait(false);
        Assert.Equal(
            StaffRetentionExecutionState.Completed,
            owner.State);
        Assert.Equal(StaffRetentionCompletedOutcome, owner.OutcomeCode);
        Assert.Equal(1, owner.AffectedCount);
        Assert.Equal(1, owner.ScannedCount);
        Assert.Equal(0, owner.RemainingCount);

        StaffMember member = await staff.StaffMembers
            .AsNoTracking()
            .SingleAsync(item =>
                item.Id == candidate.StaffMemberId)
            .ConfigureAwait(false);
        Assert.Equal(StaffMemberState.Anonymised, member.Status);
        Assert.Equal(
            StaffMember.AnonymisedDisplayName,
            member.DisplayName);
        Assert.Null(member.LegalName);
        Assert.Null(member.WorkEmail);
        Assert.Null(member.WorkPhone);
        Assert.Null(member.EmployeeNumber);
        Assert.Null(member.JobTitle);
        Assert.Null(member.Department);
        Assert.Null(member.AuthSubjectId);

        StaffRetentionAnonymisationReceipt receipt =
            await staff.RetentionAnonymisationReceipts
                .AsNoTracking()
                .SingleAsync(item =>
                    item.StaffMemberId ==
                        candidate.StaffMemberId)
                .ConfigureAwait(false);
        Assert.Equal(owner.Id, receipt.ExecutionId);
        StaffAnonymisationTombstone tombstone =
            await staff.AnonymisationTombstones
                .AsNoTracking()
                .SingleAsync(item =>
                    item.Id == candidate.StaffMemberId)
                .ConfigureAwait(false);
        Assert.Equal(
            StaffAnonymisationAuthority.Retention,
            tombstone.Authority);
        Assert.True(tombstone.MatchesRetention(receipt));

        await AssertWorkspaceStaffRetentionCorrelationAsync(
            scope.ServiceProvider,
            candidate,
            receipt).ConfigureAwait(false);
    }

    private static async Task
        AssertWorkspaceStaffRetentionCorrelationAsync(
            IServiceProvider services,
            SeededCandidate candidate,
            StaffRetentionAnonymisationReceipt staffReceipt)
    {
        WorkspacesDbContext workspaces =
            services.GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffRetentionCorrelationReceipt correlationReceipt =
            await workspaces.StaffRetentionCorrelationReceipts
                .AsNoTracking()
                .SingleAsync(item =>
                    item.StaffMemberId ==
                        candidate.StaffMemberId)
                .ConfigureAwait(false);
        Assert.Equal(
            staffReceipt.ExecutionId,
            correlationReceipt.ExecutionId);
        Assert.Equal(1, correlationReceipt.OnboardingRecordsScrubbed);
        Assert.Equal(
            1,
            correlationReceipt.AccessProcessRecordsScrubbed);
        Assert.Equal(1, correlationReceipt.AccessPlanRecordsScrubbed);
        Assert.True(correlationReceipt.HasValidCanonicalProof());
        string pseudonym =
            correlationReceipt.CreateSubjectPseudonym();
        Assert.NotEqual(candidate.StaffSubjectId, pseudonym);

        WorkspaceStaffOnboarding onboarding =
            await workspaces.StaffOnboardingApplications
                .AsNoTracking()
                .SingleAsync(item =>
                    item.Id == candidate.StaffOnboardingId)
                .ConfigureAwait(false);
        Assert.Equal(pseudonym, onboarding.SubjectId);
        // The durable resolution observation precedes the retention scrub.
        Assert.Equal(6, onboarding.Version);
        Assert.Equal(
            correlationReceipt.CompletedAtUtc,
            onboarding.LastChangedAtUtc);

        WorkspaceStaffAccessProcess process =
            await workspaces.StaffAccessProcesses
                .AsNoTracking()
                .SingleAsync(item =>
                    item.Id == candidate.StaffAccessProcessId)
                .ConfigureAwait(false);
        Assert.Equal(pseudonym, process.SubjectId);
        Assert.Equal(pseudonym, process.RequestedBy);
        Assert.Equal(4, process.Version);
        Assert.Equal(
            correlationReceipt.CompletedAtUtc,
            process.LastChangedAtUtc);

        WorkspaceStaffAccessPlan plan =
            await workspaces.StaffAccessPlans
                .AsNoTracking()
                .SingleAsync(item =>
                    item.Id == candidate.StaffAccessPlanId)
                .ConfigureAwait(false);
        Assert.Equal(pseudonym, plan.CreatedBySubjectId);
        Assert.Equal(3, plan.Version);
        Assert.Equal(
            correlationReceipt.CompletedAtUtc,
            plan.LastChangedAtUtc);
        Assert.False(
            await workspaces.StaffOnboardingApplications
                .AsNoTracking()
                .AnyAsync(item =>
                    item.SubjectId ==
                        candidate.StaffSubjectId)
                .ConfigureAwait(false));
        Assert.False(
            await workspaces.StaffAccessProcesses
                .AsNoTracking()
                .AnyAsync(item =>
                    item.SubjectId ==
                        candidate.StaffSubjectId ||
                    item.RequestedBy ==
                        candidate.StaffSubjectId)
                .ConfigureAwait(false));
        Assert.False(
            await workspaces.StaffAccessPlans
                .AsNoTracking()
                .AnyAsync(item =>
                    item.CreatedBySubjectId ==
                        candidate.StaffSubjectId)
                .ConfigureAwait(false));

        if (candidate.TenantId == UnheldTenantId)
        {
            await AssertWorkspaceReceiptMutationRejectedAsync(
                workspaces,
                correlationReceipt.Id,
                delete: false).ConfigureAwait(false);
            await AssertWorkspaceReceiptMutationRejectedAsync(
                workspaces,
                correlationReceipt.Id,
                delete: true).ConfigureAwait(false);
        }
    }

    private static async Task
        AssertWorkspaceReceiptMutationRejectedAsync(
            WorkspacesDbContext workspaces,
            Guid receiptId,
            bool delete)
    {
        Exception exception =
            await Assert.ThrowsAnyAsync<Exception>(
                () => delete
                    ? workspaces.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        DELETE FROM "workspaces"."staff_retention_correlation_receipts"
                        WHERE "Id" = {receiptId}
                        """)
                    : workspaces.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        UPDATE "workspaces"."staff_retention_correlation_receipts"
                        SET "CanonicalSha256" = {new string('f', 64)}
                        WHERE "Id" = {receiptId}
                        """));
        Assert.Contains(
            "workspace receipts are append-only",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static async Task AssertReservationRetentionOutcomeAsync(
        IHost worker,
        SeededCandidate candidate)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(candidate.TenantId);
        RetentionScheduleState schedule = await scope.ServiceProvider
            .GetRequiredService<RetentionDbContext>()
            .ScheduleStates.AsNoTracking()
            .SingleAsync(state =>
                state.OwnerKey == ReservationRetentionOwner &&
                state.DataClassKey ==
                    ReservationOperationalDataClass)
            .ConfigureAwait(false);
        Assert.Equal(RetentionExecutionState.Completed, schedule.State);
        Assert.Equal(
            ReservationRetentionCompletedOutcome,
            schedule.OutcomeCode);
        Assert.Equal(1, schedule.LastAffectedCount);
        Assert.Equal(1, schedule.LastScannedCount);
        Assert.Equal(0, schedule.LastRemainingCount);

        ReservationsDbContext reservations =
            scope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
        ReservationRetentionExecution owner =
            await reservations.RetentionExecutions
                .AsNoTracking()
                .SingleAsync(execution =>
                    execution.Id == schedule.LastExecutionId)
                .ConfigureAwait(false);
        Assert.Equal(
            ReservationRetentionExecutionState.Completed,
            owner.State);
        Assert.Equal(
            ReservationRetentionCompletedOutcome,
            owner.OutcomeCode);
        Assert.Equal(1, owner.AffectedCount);
        Assert.Equal(1, owner.ScannedCount);
        Assert.Equal(0, owner.RemainingCount);

        Reservation reservation = await reservations.Reservations
            .AsNoTracking()
            .SingleAsync(item =>
                item.Id == candidate.ReservationId)
            .ConfigureAwait(false);
        Assert.True(reservation.IsAnonymised);
        Assert.Equal(
            Reservation.AnonymisedGuestName,
            reservation.PrimaryGuestName);
        Assert.Null(reservation.Email);
        Assert.Null(reservation.Phone);
        Assert.Null(reservation.SourceReference);
        Assert.Null(reservation.Notes);
        Assert.NotNull(reservation.TerminalAtUtc);

        ReservationRetentionAnonymisationReceipt receipt =
            await reservations.RetentionAnonymisationReceipts
                .AsNoTracking()
                .SingleAsync(item =>
                    item.ReservationId ==
                        candidate.ReservationId)
                .ConfigureAwait(false);
        Assert.Equal(owner.Id, receipt.ExecutionId);
        Assert.Equal(2, receipt.RedactedHistoryCount);
        ReservationAnonymisationTombstone tombstone =
            await reservations.AnonymisationTombstones
                .AsNoTracking()
                .SingleAsync(item =>
                    item.Id == candidate.ReservationId)
                .ConfigureAwait(false);
        Assert.Equal(
            ReservationAnonymisationAuthority.Retention,
            tombstone.Authority);
        Assert.True(tombstone.MatchesRetention(receipt));

        ReservationDetailsHistoryEntry[] history =
            await reservations.ReservationDetailsHistory
                .AsNoTracking()
                .Where(item =>
                    item.ReservationId ==
                        candidate.ReservationId)
                .ToArrayAsync().ConfigureAwait(false);
        Assert.Equal(2, history.Length);
        string historyJson = string.Join(
            '\n',
            history.Select(item => item.AfterSnapshotJson));
        Assert.DoesNotContain(
            "Reservation Retention Candidate",
            historyJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "reservation-retention@example.test",
            historyJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Sensitive reservation retention note",
            historyJson,
            StringComparison.Ordinal);
    }

    private static async Task ReleaseLegalHoldAsync(
        IHost worker,
        SeededCandidate held)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(held.TenantId);
        IngestionDbContext ingestion =
            scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        LegalHold legalHold = await ingestion.LegalHolds.SingleAsync(
            item => item.Id == held.LegalHoldId).ConfigureAwait(false);
        Assert.True(legalHold.Release(
            legalHold.Version,
            "integration:retention",
            "Integration legal hold released",
            DateTimeOffset.UtcNow).IsSuccess);
        await ingestion.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<Guid> EnqueueSensitiveHistoryRunAsync(
        IHost worker,
        string tenantId)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        Guid runId = Guid.NewGuid();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        await scope.ServiceProvider.GetRequiredService<ITaskRunStore>()
            .EnqueueAsync(
                new TaskRunRequest(
                    runId,
                    RetentionModuleMetadata.Name,
                    ExecuteRetentionSchedulePayload.TaskName,
                    JsonSerializer.Serialize(
                        new ExecuteRetentionSchedulePayload(
                            IngestionModuleMetadata.Name,
                            SensitiveHistoryDataClass,
                            1,
                            RetentionTargetScopeKind.Tenant)),
                    nowUtc,
                    nowUtc,
                    RetentionModuleMetadata.WorkerGroup,
                    tenantId,
                    requestedBy: "retention-integration-test",
                    maxAttempts: 1,
                    payloadVersion:
                        ExecuteRetentionSchedulePayload.PayloadVersion,
                    deduplicationKey:
                        $"retention-release:{tenantId}:{runId:N}"),
                CancellationToken.None).ConfigureAwait(false);
        return runId;
    }

    private sealed record SeededCandidate(
        string TenantId,
        Guid PropertyId,
        Guid ProposalId,
        Guid? LegalHoldId,
        Guid? GuestId,
        Guid ReservationId,
        Guid StaffMemberId,
        string StaffSubjectId,
        Guid StaffOnboardingId,
        Guid StaffAccessProcessId,
        Guid StaffAccessPlanId);

    private sealed record SeededStaff(
        Guid StaffMemberId,
        string SubjectId,
        Guid OnboardingId,
        Guid AccessProcessId,
        Guid AccessPlanId);
}

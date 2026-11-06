namespace Integration.Tests;

using BunkFy.Host.Worker;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Results;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Infrastructure;
using Gma.Framework.Tenancy;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Organizations.Domain.Aggregates;
using Gma.Modules.Organizations.Domain.Enums;
using Gma.Modules.Organizations.Persistence;
using Gma.Modules.TaskRuntime.Persistence;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class StaffDataRightsAnonymisationIntegrationTests
{
    private const string TenantId =
        "8b000000-0000-0000-0000-000000000001";
    private const string StaffSubjectId = "private-auth-subject";
    private const string BeforeStaffAnonymisationOwnerProofMigration =
        "20260729161607_AddStaffEmploymentGovernanceAndDataHolds";
    private const string BeforeScopedAnonymisationOwnerProtocolMigration =
        "20260729191433_ScopeAnonymisationExecution";
    private const string BeforeStaffAnonymisationRestoreProofMigration =
        "20260729222732_AddStaffAnonymisationOwnerProof";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_v2_execution_and_v3_restore_close_access_and_persist_owner_proof()
    {
        await using IContainer nats =
            AuthTestContainers.CreateNatsContainer();
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase(
                    "bunkfy_staff_anonymisation_execution_tests")
                .Build();
        await using PostgreSqlContainer restoredPostgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase(
                    "bunkfy_staff_anonymisation_restore_tests")
                .Build();
        await Task.WhenAll(
                nats.StartAsync(),
                postgreSql.StartAsync(),
                restoredPostgreSql.StartAsync())
            .ConfigureAwait(false);

        string connectionString = postgreSql.GetConnectionString();
        string natsConnectionString =
            AuthTestContainers.GetNatsConnectionString(nats);
        string ledgerDeltaPath = Path.Combine(
            Path.GetTempPath(),
            $"bunkfy-staff-rights-delta-{Guid.NewGuid():N}");
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            disableOutboxPublisher: false);
        await api.MigrateStaffAuthorizationDatabaseAsync()
            .ConfigureAwait(false);
        await api.MigrateGuestDataRightsAuthorizationDatabaseAsync()
            .ConfigureAwait(false);

        using IHost worker = CreateWorker(
            connectionString,
            natsConnectionString,
            ledgerDeltaPath);
        await MigrateTaskRuntimeAsync(worker).ConfigureAwait(false);
        (StaffMember member, DataRightsCase dataRightsCase) =
            await SeedExecutionCandidateAsync(api).ConfigureAwait(false);
        DataRightsExecutionDto execution =
            await ApproveAndStartExecutionAsync(api, dataRightsCase)
                .ConfigureAwait(false);

        DataRightsExecutionWorkItemDto selected =
            Assert.Single(execution.WorkItems);
        Assert.Equal(
            DataRightsAnonymisationContractV2.CurrentVersion,
            selected.OwnerContractVersion);
        Assert.Equal(DataRightsCaseType.StaffRights, selected.CaseType);
        Assert.Null(selected.PropertyId);
        await WaitForPreparedOutboxAsync(
            api,
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        bool workerStarted = false;
        try
        {
            await worker.StartAsync().ConfigureAwait(false);
            workerStarted = true;
            TaskRun taskRun = await WaitForTaskRunAsync(
                worker,
                dataRightsCase.Id,
                TimeSpan.FromSeconds(40)).ConfigureAwait(false);
            await WaitForCompletionAsync(
                worker,
                dataRightsCase.Id,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            Assert.Equal(
                ExecuteDataRightsAnonymisationPayloadV2.TaskName,
                taskRun.TaskName);
            Assert.Equal(
                ExecuteDataRightsAnonymisationPayloadV2.PayloadVersion,
                taskRun.PayloadVersion);
            Assert.Equal(TenantId, taskRun.ScopeId);
            Assert.DoesNotContain(
                "Private Staff Name",
                taskRun.Payload,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "private.staff@example.test",
                taskRun.Payload,
                StringComparison.Ordinal);

            using IServiceScope scope = worker.Services.CreateScope();
            scope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            StaffDbContext staff =
                scope.ServiceProvider.GetRequiredService<StaffDbContext>();
            DataRightsDbContext dataRights = scope.ServiceProvider
                .GetRequiredService<DataRightsDbContext>();
            StaffMember anonymised = await staff.StaffMembers
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == member.Id)
                .ConfigureAwait(false);
            StaffAnonymisationReceipt receipt =
                await staff.AnonymisationReceipts
                    .AsNoTracking()
                    .SingleAsync()
                    .ConfigureAwait(false);
            StaffAnonymisationTombstone tombstone =
                await staff.AnonymisationTombstones
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == member.Id)
                    .ConfigureAwait(false);
            DataRightsExecutionWorkItem workItem =
                await dataRights.ExecutionWorkItems
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.Id == selected.Id)
                    .ConfigureAwait(false);
            DataRightsProcessingLedgerEntry ledger =
                await dataRights.ProcessingLedgerEntries
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.WorkItemId == workItem.Id)
                    .ConfigureAwait(false);
            DataRightsCase persistedCase = await dataRights.Cases
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.Id == dataRightsCase.Id)
                .ConfigureAwait(false);
            OrganizationMembership membership = await scope.ServiceProvider
                .GetRequiredService<OrganizationsDbContext>()
                .Memberships.AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.OrganizationId == Guid.Parse(TenantId) &&
                    candidate.SubjectId == StaffSubjectId)
                .ConfigureAwait(false);
            IStaffMemberRepository members = scope.ServiceProvider
                .GetRequiredService<IStaffMemberRepository>();

            Assert.True(anonymised.MatchesAnonymisedState(
                receipt.ResultingStaffVersion,
                receipt.CompletedAtUtc));
            Assert.True(tombstone.Matches(receipt));
            Assert.Null(await members.GetAsync(
                member.Id,
                CancellationToken.None).ConfigureAwait(false));
            Assert.NotNull(await members.GetForDataRightsAsync(
                member.Id,
                CancellationToken.None).ConfigureAwait(false));
            Assert.Equal(
                DataRightsExecutionWorkItem.ScopedOwnerContractVersion,
                workItem.OwnerContractVersion);
            Assert.Equal(
                DataRightsExecutionWorkItemState.Completed,
                workItem.State);
            Assert.Equal(receipt.Id, workItem.OwnerReceiptId);
            Assert.Equal(
                receipt.ResultingStaffVersion,
                workItem.ResultingRecordVersion);
            Assert.Equal(
                receipt.CanonicalSha256,
                workItem.OwnerReceiptSha256);
            Assert.Equal(receipt.Id, ledger.OwnerReceiptId);
            Assert.Equal(
                DataRightsCaseState.Completed,
                persistedCase.Status);
            Assert.Equal(
                OrganizationMembershipState.Removed,
                membership.Status);
            Assert.Single(
                staff.OutboxMessages,
                message =>
                    message.EventType ==
                    typeof(StaffMemberAnonymisedIntegrationEvent)
                        .FullName);
            string protectedFiles = string.Join(
                '\n',
                Directory.GetFiles(
                        ledgerDeltaPath,
                        "*",
                        SearchOption.AllDirectories)
                    .Select(File.ReadAllText));
            Assert.DoesNotContain(
                member.Id.ToString("N"),
                protectedFiles,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "Private Staff Name",
                protectedFiles,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "private.staff@example.test",
                protectedFiles,
                StringComparison.Ordinal);

            await worker.StopAsync().ConfigureAwait(false);
            workerStarted = false;

            await AssertReceiptMutationRejectedAsync(
                api,
                receipt.Id,
                delete: false).ConfigureAwait(false);
            await AssertReceiptMutationRejectedAsync(
                api,
                receipt.Id,
                delete: true).ConfigureAwait(false);
            await AssertUnsafeDowngradesRejectedAsync(api)
                .ConfigureAwait(false);

            using IHost restoredWorker = CreateWorker(
                restoredPostgreSql.GetConnectionString(),
                natsConnectionString,
                ledgerDeltaPath);
            await MigrateRestoreDatabasesAsync(restoredWorker)
                .ConfigureAwait(false);
            await SeedPreAnonymisationSnapshotAsync(
                    restoredWorker,
                    member.Id,
                    receipt.ResultingStaffVersion - 1)
                .ConfigureAwait(false);

            await restoredWorker.StartAsync().ConfigureAwait(false);
            try
            {
                using IServiceScope restoredScope =
                    restoredWorker.Services.CreateScope();
                restoredScope.ServiceProvider
                    .GetRequiredService<ITenantContextAccessor>()
                    .SetTenant(TenantId);
                StaffDbContext restoredStaff = restoredScope.ServiceProvider
                    .GetRequiredService<StaffDbContext>();
                DataRightsDbContext restoredDataRights =
                    restoredScope.ServiceProvider
                        .GetRequiredService<DataRightsDbContext>();
                StaffMember restoredMember =
                    await restoredStaff.StaffMembers
                        .AsNoTracking()
                        .SingleAsync(candidate =>
                            candidate.Id == member.Id)
                        .ConfigureAwait(false);
                StaffAnonymisationRestoreReceipt restoreReceipt =
                    await restoredStaff.AnonymisationRestoreReceipts
                        .AsNoTracking()
                        .SingleAsync()
                        .ConfigureAwait(false);
                StaffAnonymisationTombstone restoredTombstone =
                    await restoredStaff.AnonymisationTombstones
                        .AsNoTracking()
                        .SingleAsync(candidate =>
                            candidate.Id == member.Id)
                        .ConfigureAwait(false);
                DataRightsRestoreCheckpoint restoreCheckpoint =
                    await restoredDataRights.RestoreCheckpoints
                        .AsNoTracking()
                        .SingleAsync()
                        .ConfigureAwait(false);
                OrganizationMembership restoredMembership =
                    await restoredScope.ServiceProvider
                        .GetRequiredService<OrganizationsDbContext>()
                        .Memberships.AsNoTracking()
                        .SingleAsync(candidate =>
                            candidate.OrganizationId ==
                                Guid.Parse(TenantId) &&
                            candidate.SubjectId == StaffSubjectId)
                        .ConfigureAwait(false);
                DataRightsRestoreReadinessSnapshot readiness =
                    restoredScope.ServiceProvider
                        .GetRequiredService<IDataRightsRestoreReadiness>()
                        .Snapshot;

                Assert.True(restoredMember.MatchesAnonymisedState(
                    receipt.ResultingStaffVersion,
                    receipt.CompletedAtUtc));
                Assert.Equal(ledger.Id, restoreReceipt.LedgerEntryId);
                Assert.Equal(receipt.Id, restoreReceipt.OwnerReceiptId);
                Assert.Equal(
                    receipt.CanonicalSha256,
                    restoreReceipt.OwnerReceiptSha256);
                Assert.Equal(
                    receipt.ResultingStaffVersion,
                    restoreReceipt.ResultingStaffVersion);
                Assert.True(restoredTombstone.MatchesRestore(
                    member.Id,
                    ledger.Id,
                    receipt.CompletedAtUtc,
                    receipt.CanonicalSha256));
                Assert.Equal(
                    OrganizationMembershipState.Removed,
                    restoredMembership.Status);
                Assert.Equal(1, restoreCheckpoint.TenantSequence);
                Assert.Equal(
                    ledger.EntrySha256,
                    restoreCheckpoint.EntrySha256);
                Assert.True(readiness.IsReady);
                Assert.Equal(
                    "data-rights.restore.ready",
                    readiness.StatusCode);
            }
            finally
            {
                await restoredWorker.StopAsync().ConfigureAwait(false);
            }

            await AssertRestoreReceiptMutationRejectedAsync(
                restoredWorker,
                ledger.Id,
                delete: false).ConfigureAwait(false);
            await AssertRestoreReceiptMutationRejectedAsync(
                restoredWorker,
                ledger.Id,
                delete: true).ConfigureAwait(false);
            await AssertRestoreDowngradeRejectedAsync(restoredWorker)
                .ConfigureAwait(false);
        }
        finally
        {
            if (workerStarted)
            {
                await worker.StopAsync().ConfigureAwait(false);
            }

            if (Directory.Exists(ledgerDeltaPath))
            {
                Directory.Delete(ledgerDeltaPath, recursive: true);
            }
        }
    }

    private static IHost CreateWorker(
        string connectionString,
        string natsConnectionString,
        string ledgerDeltaPath)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                EnvironmentName = "Integration"
            });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ApplicationIdentity:DisplayName"] =
                    "BunkFy Staff anonymisation integration worker",
                ["ApplicationIdentity:Namespace"] = "bunkfy",
                ["Persistence:Provider"] = "PostgreSql",
                ["ConnectionStrings:PostgreSql"] = connectionString,
                ["ConnectionStrings:nats"] = natsConnectionString,
                ["DataRights:LedgerDelta:Provider"] = "LocalFile",
                ["DataRights:LedgerDelta:LocalFilePath"] =
                    ledgerDeltaPath,
                ["DataRights:LedgerDelta:ActiveIntegrityKeyVersion"] =
                    "1",
                ["DataRights:LedgerDelta:IntegrityKeys:1"] =
                    "aWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWk=",
                ["Tenancy:Enabled"] = "true",
                ["Caching:Enabled"] = "false",
                ["NatsJetStream:Enabled"] = "true",
                ["NatsConsumers:Enabled"] = "true",
                ["NatsConsumers:FetchBatchSize"] = "10",
                ["NatsConsumers:PollInterval"] = "00:00:00.100",
                ["NatsConsumers:AckWait"] = "00:00:05",
                ["NatsConsumers:AckProgressInterval"] =
                    "00:00:01",
                ["NatsConsumers:HandlerTimeout"] = "00:00:10",
                ["NatsConsumers:NakDelay"] = "00:00:00.100",
                ["Outbox:PollIntervalMilliseconds"] = "100",
                ["Outbox:LockDurationMilliseconds"] = "5000",
                ["Tasks:Worker:Enabled"] = "true",
                ["Tasks:Worker:WorkerGroups:0"] =
                    DataRightsModuleMetadata.AnonymisationWorkerGroup,
                ["Tasks:Worker:BatchSize"] = "1",
                ["Tasks:Worker:MaxConcurrency"] = "1",
                ["Tasks:Worker:PollInterval"] = "00:00:00.100",
                ["Tasks:Worker:LeaseDuration"] = "00:00:30",
                ["Tasks:Worker:HandlerTimeout"] = "00:00:30",
                ["Tasks:Worker:RetryBaseDelay"] = "00:00:00.100",
                ["Tasks:Worker:RetryMaxDelay"] = "00:00:01",
                ["Tasks:Worker:WorkerId"] =
                    "staff-anonymisation-test",
                ["Tasks:Worker:NodeId"] =
                    "staff-anonymisation-node",
                ["Tasks:Worker:TimeoutScannerEnabled"] = "false",
                ["Tasks:Worker:MetricsSamplerEnabled"] = "false",
                ["Worker:Modules:AccessControl"] = "true",
                ["Worker:Modules:Auth"] = "true",
                ["Worker:Modules:Organizations"] = "true",
                ["Worker:Modules:Properties"] = "true",
                ["Worker:Modules:Staff"] = "true",
                ["Worker:Modules:DataRights"] = "true",
                ["Worker:Modules:TaskRuntime"] = "true"
            });
        builder.Logging.ClearProviders();
        AuthTestConfiguration.ConfigureTokenHashing(
            builder.Configuration);
        builder.AddWorkerHost();
        CountryPolicyIntegrationTestData.InstallRegistry(
            builder.Services);
        ModuleCompositionValidationResult composition =
            builder.ValidateModuleComposition();
        Assert.True(composition.IsValid, composition.Report);
        return builder.Build();
    }

    private static async Task MigrateTaskRuntimeAsync(IHost worker)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<TaskRuntimeDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
    }

    private static async Task MigrateRestoreDatabasesAsync(IHost worker)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<AuthDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<AccessControlDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<OrganizationsDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<StaffDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<DataRightsDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<TaskRuntimeDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
    }

    private static async Task SeedPreAnonymisationSnapshotAsync(
        IHost worker,
        Guid staffMemberId,
        long expectedDepartedVersion)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        DateTimeOffset departedAtUtc =
            DateTimeOffset.UtcNow.AddDays(-2);
        StaffMember member = CreateDepartedStaffMember(
            staffMemberId,
            departedAtUtc,
            "Restored Staff Name",
            "restored.staff@example.test");
        Assert.Equal(expectedDepartedVersion, member.Version);

        await scope.ServiceProvider
            .GetRequiredService<IStaffMemberRepository>()
            .AddAsync(member, CancellationToken.None)
            .ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<StaffDbContext>()
            .SaveChangesAsync()
            .ConfigureAwait(false);
        await SeedWorkspaceAccessProofAsync(
                scope.ServiceProvider,
                member,
                departedAtUtc.AddMinutes(3))
            .ConfigureAwait(false);
    }

    private static StaffMember CreateDepartedStaffMember(
        Guid staffMemberId,
        DateTimeOffset departedAtUtc,
        string displayName,
        string workEmail)
    {
        StaffMember member = StaffMember.Create(
            staffMemberId,
            TenantId,
            displayName,
            $"{displayName} Legal",
            workEmail,
            "+44 20 1234 5678",
            "PRIVATE-EMPLOYEE",
            "Private job title",
            "Private department",
            StaffSubjectId,
            "user:staff-creator",
            Guid.NewGuid(),
            departedAtUtc.AddDays(-1)).Value;
        Assert.True(member.Depart(
            DateOnly.FromDateTime(departedAtUtc.UtcDateTime),
            member.Version,
            "user:staff-manager",
            "employment-ended",
            Guid.NewGuid(),
            [],
            departedAtUtc).IsSuccess);
        member.ClearDomainEvents();
        return member;
    }

    private static async Task SeedWorkspaceAccessProofAsync(
        IServiceProvider services,
        StaffMember member,
        DateTimeOffset nowUtc)
    {
        Guid organizationId = Guid.Parse(TenantId);
        const string actorId = "user:workspace-owner";
        OrganizationsDbContext organizations =
            services.GetRequiredService<OrganizationsDbContext>();
        organizations.Organizations.Add(Organization.Create(
            organizationId,
            "Staff restore workspace",
            "staff-restore-workspace",
            actorId,
            Guid.NewGuid(),
            nowUtc).Value);
        organizations.Memberships.Add(OrganizationMembership.Create(
            Guid.NewGuid(),
            organizationId,
            "workspace-owner",
            OrganizationMembershipRole.Owner,
            actorId,
            Guid.NewGuid(),
            nowUtc).Value);
        organizations.Memberships.Add(OrganizationMembership.Create(
            Guid.NewGuid(),
            organizationId,
            StaffSubjectId,
            OrganizationMembershipRole.Member,
            actorId,
            Guid.NewGuid(),
            nowUtc).Value);
        await organizations.SaveChangesAsync().ConfigureAwait(false);

        WorkspaceStaffAccessProcess process =
            WorkspaceStaffAccessProcess.Create(
                Guid.NewGuid(),
                TenantId,
                member.Id,
                StaffSubjectId,
                WorkspaceStaffAccessTargetState.Departed,
                member.Version,
                member.DepartureEffectiveOn!.Value,
                actorId,
                [],
                nowUtc).Value;
        Assert.True(
            process.MarkAwaitingStaffCommit(
                nowUtc.AddSeconds(1)).IsSuccess);
        Assert.True(
            process.ObserveStaffCommit(
                nowUtc.AddSeconds(2)).IsSuccess);
        Assert.Equal(
            WorkspaceStaffAccessProcessState.Completed,
            process.State);
        WorkspacesDbContext workspaces =
            services.GetRequiredService<WorkspacesDbContext>();
        workspaces.StaffAccessProcesses.Add(process);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<(StaffMember Member, DataRightsCase Case)>
        SeedExecutionCandidateAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        DateTimeOffset departedAtUtc =
            DateTimeOffset.UtcNow.AddDays(-2);
        StaffMember member = CreateDepartedStaffMember(
            Guid.NewGuid(),
            departedAtUtc,
            "Private Staff Name",
            "private.staff@example.test");

        StaffDbContext staff =
            scope.ServiceProvider.GetRequiredService<StaffDbContext>();
        await scope.ServiceProvider
            .GetRequiredService<IStaffMemberRepository>()
            .AddAsync(member, CancellationToken.None)
            .ConfigureAwait(false);
        StaffEmploymentGovernance governance =
            StaffEmploymentGovernance.Configure(
                TenantId,
                member.Id,
                member.Version,
                CountryPolicyIntegrationTestData
                    .CreateStaffGovernanceBinding(
                        departedAtUtc.AddMinutes(1)),
                CountryPolicyIntegrationTestData
                    .CreateStaffGovernanceAcknowledgements(),
                "user:privacy-reviewer",
                departedAtUtc.AddMinutes(2)).Value;
        staff.EmploymentGovernance.Add(governance);
        await staff.SaveChangesAsync().ConfigureAwait(false);
        await SeedWorkspaceAccessProofAsync(
                scope.ServiceProvider,
                member,
                departedAtUtc.AddMinutes(3))
            .ConfigureAwait(false);

        DateTimeOffset caseStartedAtUtc =
            DateTimeOffset.UtcNow.AddMinutes(-10);
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:privacy-reviewer",
            caseStartedAtUtc).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            caseStartedAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            StaffDataRightsCoordinates.Owner,
            StaffDataRightsCoordinates.StaffMemberRecordType,
            member.Id,
            member.Version,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            caseStartedAtUtc.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            caseStartedAtUtc.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            caseStartedAtUtc.AddMinutes(4)).IsSuccess);
        DataRightsDbContext dataRights = scope.ServiceProvider
            .GetRequiredService<DataRightsDbContext>();
        dataRights.Cases.Add(dataRightsCase);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return (member, dataRightsCase);
    }

    private static async Task<DataRightsExecutionDto>
        ApproveAndStartExecutionAsync(
            AuthTestApplication api,
            DataRightsCase dataRightsCase)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        IRequestDispatcher dispatcher =
            scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();
        Result<DataRightsCaseDto> approved =
            await dispatcher.SendAsync(
                new RecordDataRightsDecisionCommand(
                    DataRightsCaseScope.Staff,
                    dataRightsCase.Id,
                    DataRightsDecisionOutcome.Approved,
                    DataRightsDecisionReason.RequestValidated,
                    dataRightsCase.Version,
                    "user:decision-maker"),
                CancellationToken.None).ConfigureAwait(false);
        Assert.True(approved.IsSuccess, approved.Error.Code);
        Assert.NotNull(approved.Value.ApprovalEvidence);

        Result<DataRightsExecutionDto> started =
            await dispatcher.SendAsync(
                new StartDataRightsAnonymisationExecutionCommand(
                    DataRightsCaseScope.Staff,
                    dataRightsCase.Id,
                    Guid.NewGuid(),
                    approved.Value.Version,
                    "user:privacy-executor"),
                CancellationToken.None).ConfigureAwait(false);
        Assert.True(started.IsSuccess, started.Error.Code);
        return started.Value;
    }

    private static async Task WaitForPreparedOutboxAsync(
        AuthTestApplication api,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            bool published = await scope.ServiceProvider
                .GetRequiredService<DataRightsDbContext>()
                .OutboxMessages.AsNoTracking()
                .AnyAsync(message =>
                    message.EventType ==
                    typeof(
                        DataRightsAnonymisationExecutionPreparedIntegrationEventV2)
                    .FullName &&
                    message.ProcessedAtUtc != null)
                .ConfigureAwait(false);
            if (published)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The scoped anonymisation event was not published.");
    }

    private static async Task<TaskRun> WaitForTaskRunAsync(
        IHost worker,
        Guid caseId,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = worker.Services.CreateScope();
            TaskRun? taskRun = await scope.ServiceProvider
                .GetRequiredService<TaskRuntimeDbContext>()
                .TaskRuns.AsNoTracking()
                .SingleOrDefaultAsync(candidate =>
                    candidate.CorrelationId == caseId)
                .ConfigureAwait(false);
            if (taskRun?.Status == TaskRunStatus.Succeeded)
            {
                return taskRun;
            }

            if (taskRun?.Status is
                TaskRunStatus.Failed or
                TaskRunStatus.Canceled or
                TaskRunStatus.TimedOut)
            {
                Assert.Fail(taskRun.LastError);
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The scoped anonymisation task did not succeed.");
    }

    private static async Task WaitForCompletionAsync(
        IHost worker,
        Guid caseId,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = worker.Services.CreateScope();
            scope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            DataRightsDbContext dataRights = scope.ServiceProvider
                .GetRequiredService<DataRightsDbContext>();
            DataRightsCase? dataRightsCase =
                await dataRights.Cases.AsNoTracking()
                    .SingleOrDefaultAsync(candidate =>
                        candidate.Id == caseId)
                    .ConfigureAwait(false);
            DataRightsExecutionWorkItem? workItem =
                await dataRights.ExecutionWorkItems.AsNoTracking()
                    .SingleOrDefaultAsync(candidate =>
                        candidate.CaseId == caseId)
                    .ConfigureAwait(false);
            if (dataRightsCase?.Status ==
                    DataRightsCaseState.Completed &&
                workItem?.State ==
                    DataRightsExecutionWorkItemState.Completed)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The scoped anonymisation proof did not reconcile.");
    }

    private static async Task AssertReceiptMutationRejectedAsync(
        AuthTestApplication api,
        Guid receiptId,
        bool delete)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        StaffDbContext staff =
            scope.ServiceProvider.GetRequiredService<StaffDbContext>();
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => delete
                ? staff.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    DELETE FROM "staff"."staff_anonymisation_receipts"
                    WHERE "Id" = {receiptId}
                    """)
                : staff.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE "staff"."staff_anonymisation_receipts"
                    SET "ActorId" = {"tampered"}
                    WHERE "Id" = {receiptId}
                    """));
        Assert.Contains(
            "Staff receipts are append-only",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static async Task AssertUnsafeDowngradesRejectedAsync(
        AuthTestApplication api)
    {
        await AssertDowngradeRejectedAsync<StaffDbContext>(
            api,
            "Cannot downgrade while Staff anonymisation state or proof exists.",
            BeforeStaffAnonymisationOwnerProofMigration)
            .ConfigureAwait(false);
        await AssertDowngradeRejectedAsync<DataRightsDbContext>(
            api,
            "Cannot downgrade while scoped anonymisation owner work exists.",
            BeforeScopedAnonymisationOwnerProtocolMigration)
            .ConfigureAwait(false);
    }

    private static async Task AssertDowngradeRejectedAsync<TContext>(
        AuthTestApplication api,
        string expectedMessage,
        string targetMigration)
        where TContext : DbContext =>
        await AssertDowngradeRejectedAsync<TContext>(
                api.Services,
                expectedMessage,
                targetMigration)
            .ConfigureAwait(false);

    private static async Task AssertDowngradeRejectedAsync<TContext>(
        IServiceProvider services,
        string expectedMessage,
        string targetMigration)
        where TContext : DbContext
    {
        using IServiceScope scope = services.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        TContext dbContext =
            scope.ServiceProvider.GetRequiredService<TContext>();
        string[] applied = (await dbContext.Database
                .GetAppliedMigrationsAsync()
                .ConfigureAwait(false))
            .ToArray();
        Assert.Contains(targetMigration, applied);
        IMigrator migrator =
            dbContext.Database.GetService<IMigrator>();
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => migrator.MigrateAsync(targetMigration));
        Assert.Contains(
            expectedMessage,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static async Task AssertRestoreReceiptMutationRejectedAsync(
        IHost worker,
        Guid receiptId,
        bool delete)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        StaffDbContext staff =
            scope.ServiceProvider.GetRequiredService<StaffDbContext>();
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => delete
                ? staff.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    DELETE FROM "staff"."staff_anonymisation_restore_receipts"
                    WHERE "Id" = {receiptId}
                    """)
                : staff.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE "staff"."staff_anonymisation_restore_receipts"
                    SET "CanonicalSha256" = {new string('f', 64)}
                    WHERE "Id" = {receiptId}
                    """));
        Assert.Contains(
            "Staff receipts are append-only",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static Task AssertRestoreDowngradeRejectedAsync(
        IHost worker) =>
        AssertDowngradeRejectedAsync<StaffDbContext>(
            worker.Services,
            "Cannot downgrade Staff anonymisation restore proof while restore evidence exists.",
            BeforeStaffAnonymisationRestoreProofMigration);
}

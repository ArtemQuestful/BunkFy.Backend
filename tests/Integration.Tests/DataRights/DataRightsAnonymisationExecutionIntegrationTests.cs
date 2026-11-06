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
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Models;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Workspaces.Persistence;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Results;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Infrastructure;
using Gma.Framework.Tenancy;
using Gma.Modules.TaskRuntime.Persistence;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class DataRightsAnonymisationExecutionIntegrationTests
{
    private const string TenantId = "8a000000-0000-0000-0000-000000000001";
    private static readonly Guid PropertyId =
        Guid.Parse("8a000000-0000-0000-0000-000000000002");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Durable_event_executes_after_worker_start_and_records_owner_proof()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_data_rights_anonymisation_execution_tests")
            .Build();
        await using PostgreSqlContainer restoredPostgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_data_rights_restore_gate_tests")
                .Build();
        await Task.WhenAll(
                nats.StartAsync(),
                postgreSql.StartAsync(),
                restoredPostgreSql.StartAsync())
            .ConfigureAwait(false);

        string connectionString = postgreSql.GetConnectionString();
        string natsConnectionString = AuthTestContainers.GetNatsConnectionString(nats);
        string ledgerDeltaPath = Path.Combine(
            Path.GetTempPath(),
            $"bunkfy-data-rights-delta-{Guid.NewGuid():N}");
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            disableOutboxPublisher: false);
        await api.MigrateGuestDataRightsAuthorizationDatabaseAsync().ConfigureAwait(false);
        using HttpClient client = api.CreateClient();
        _ = client;

        using IHost worker = CreateWorker(
            connectionString,
            natsConnectionString,
            ledgerDeltaPath);
        await MigrateTaskRuntimeAsync(worker).ConfigureAwait(false);
        (GuestProfile guest, DataRightsCase dataRightsCase) =
            await SeedExecutionCandidateAsync(api).ConfigureAwait(false);
        DataRightsExecutionDto execution =
            await ApproveAndStartExecutionAsync(api, dataRightsCase).ConfigureAwait(false);

        await WaitForDataRightsOutboxAsync(
            api,
            expectedEventCount: 1,
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        await AssertNoTaskRunAsync(worker, dataRightsCase.Id).ConfigureAwait(false);

        bool workerStarted = false;
        await worker.StartAsync().ConfigureAwait(false);
        workerStarted = true;
        try
        {
            TaskRun taskRun = await WaitForTaskRunAsync(
                worker,
                dataRightsCase.Id,
                TaskRunStatus.Succeeded,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await WaitForExecutionCompletionAsync(
                worker,
                dataRightsCase.Id,
                expectedWorkItemCount: 1,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            Assert.Equal(DataRightsModuleMetadata.Name, taskRun.ModuleName);
            Assert.Equal(ExecuteDataRightsAnonymisationPayload.TaskName, taskRun.TaskName);
            Assert.Equal(DataRightsModuleMetadata.AnonymisationWorkerGroup, taskRun.WorkerGroup);
            Assert.Equal(TenantId, taskRun.ScopeId);
            Assert.Equal(dataRightsCase.Id, taskRun.CorrelationId);
            Assert.Equal(1, taskRun.Attempts);
            Assert.Null(taskRun.LastError);
            Assert.DoesNotContain("Durable Guest", taskRun.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("@example.test", taskRun.Payload, StringComparison.Ordinal);

            using IServiceScope scope = worker.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            GuestsDbContext guests = scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
            DataRightsDbContext dataRights =
                scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();

            GuestProfile anonymised = await guests.GuestProfiles
                .AsNoTracking()
                .SingleAsync(item => item.Id == guest.Id)
                .ConfigureAwait(false);
            GuestAnonymisationReceipt receipt = await guests.AnonymisationReceipts
                .AsNoTracking()
                .SingleAsync()
                .ConfigureAwait(false);
            GuestAnonymisationTombstone tombstone = await guests.AnonymisationTombstones
                .AsNoTracking()
                .SingleAsync(item => item.Id == guest.Id)
                .ConfigureAwait(false);
            DataRightsExecutionWorkItem workItem = await dataRights.ExecutionWorkItems
                .AsNoTracking()
                .SingleAsync(item => item.Id == Assert.Single(execution.WorkItems).Id)
                .ConfigureAwait(false);
            DataRightsCase persistedCase = await dataRights.Cases
                .AsNoTracking()
                .SingleAsync(item => item.Id == dataRightsCase.Id)
                .ConfigureAwait(false);
            DataRightsProcessingLedgerEntry ledger =
                await dataRights.ProcessingLedgerEntries
                    .AsNoTracking()
                    .SingleAsync(item => item.WorkItemId == workItem.Id)
                    .ConfigureAwait(false);
            IDataRightsLedgerDeltaStore deltaStore =
                scope.ServiceProvider.GetRequiredService<
                    IDataRightsLedgerDeltaStore>();
            DataRightsLedgerDeltaCheckpoint checkpoint =
                await deltaStore.ReadTrustedCheckpointAsync(
                    TenantId,
                    CancellationToken.None).ConfigureAwait(false);
            DataRightsLedgerDeltaPage deltaPage =
                await deltaStore.ReadAfterAsync(
                    TenantId,
                    DataRightsLedgerDeltaCursor.Genesis,
                    pageSize: 10,
                    CancellationToken.None).ConfigureAwait(false);

            Assert.Equal(GuestProfileState.Anonymised, anonymised.Status);
            Assert.True(tombstone.Matches(receipt));
            Assert.Equal(DataRightsExecutionWorkItemState.Completed, workItem.State);
            Assert.Equal(workItem.IdempotencyKey, receipt.IdempotencyKey);
            Assert.Equal(taskRun.Id, workItem.TaskRunId);
            Assert.Equal(taskRun.Attempts, workItem.LastTaskAttempt);
            Assert.Equal(receipt.Id, workItem.OwnerReceiptId);
            Assert.Equal(receipt.ResultingGuestVersion, workItem.ResultingRecordVersion);
            Assert.Equal(receipt.CanonicalSha256, workItem.OwnerReceiptSha256);
            Assert.Equal(receipt.Id, ledger.OwnerReceiptId);
            Assert.Equal(ledger.EntrySha256, checkpoint.Cursor.EntrySha256);
            Assert.Equal(1, checkpoint.Cursor.TenantSequence);
            DataRightsLedgerDelta delta = Assert.Single(deltaPage.Deltas);
            Assert.Equal(ledger.Id, delta.Ledger.EntryId);
            Assert.False(deltaPage.HasMore);
            string protectedFiles = string.Join(
                '\n',
                Directory.GetFiles(
                        ledgerDeltaPath,
                        "*",
                        SearchOption.AllDirectories)
                    .Select(File.ReadAllText));
            Assert.DoesNotContain(
                guest.Id.ToString("N"),
                protectedFiles,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DataRightsCaseState.Completed, persistedCase.Status);

            await worker.StopAsync().ConfigureAwait(false);
            workerStarted = false;

            using IHost restoredWorker = CreateWorker(
                restoredPostgreSql.GetConnectionString(),
                natsConnectionString,
                ledgerDeltaPath);
            await MigrateRestoreDatabasesAsync(restoredWorker)
                .ConfigureAwait(false);
            await SeedPreAnonymisationSnapshotAsync(restoredWorker, guest.Id)
                .ConfigureAwait(false);

            await restoredWorker.StartAsync().ConfigureAwait(false);
            try
            {
                using IServiceScope restoredScope =
                    restoredWorker.Services.CreateScope();
                restoredScope.ServiceProvider
                    .GetRequiredService<ITenantContextAccessor>()
                    .SetTenant(TenantId);
                GuestsDbContext restoredGuests = restoredScope.ServiceProvider
                    .GetRequiredService<GuestsDbContext>();
                DataRightsDbContext restoredDataRights =
                    restoredScope.ServiceProvider
                        .GetRequiredService<DataRightsDbContext>();

                GuestProfile restoredProfile =
                    await restoredGuests.GuestProfiles
                        .AsNoTracking()
                        .SingleAsync(item => item.Id == guest.Id)
                        .ConfigureAwait(false);
                GuestAnonymisationRestoreReceipt restoreReceipt =
                    await restoredGuests.AnonymisationRestoreReceipts
                        .AsNoTracking()
                        .SingleAsync()
                        .ConfigureAwait(false);
                GuestAnonymisationTombstone restoredTombstone =
                    await restoredGuests.AnonymisationTombstones
                        .AsNoTracking()
                        .SingleAsync(item => item.Id == guest.Id)
                        .ConfigureAwait(false);
                DataRightsRestoreCheckpoint restoreCheckpoint =
                    await restoredDataRights.RestoreCheckpoints
                        .AsNoTracking()
                        .SingleAsync()
                        .ConfigureAwait(false);
                DataRightsRestoreReadinessSnapshot readiness =
                    restoredScope.ServiceProvider
                        .GetRequiredService<IDataRightsRestoreReadiness>()
                        .Snapshot;

                Assert.True(
                    restoredProfile.MatchesAnonymisedState(
                        receipt.CompletedAtUtc));
                Assert.Equal(ledger.Id, restoreReceipt.LedgerEntryId);
                Assert.Equal(receipt.Id, restoreReceipt.OwnerReceiptId);
                Assert.Equal(
                    receipt.CanonicalSha256,
                    restoreReceipt.OwnerReceiptSha256);
                Assert.True(restoredTombstone.MatchesRestore(
                    guest.Id,
                    ledger.Id,
                    receipt.CompletedAtUtc,
                    receipt.CanonicalSha256));
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

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Durable_reservation_execution_restores_owner_proof_before_readiness()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_execution_tests")
                .Build();
        await using PostgreSqlContainer restoredPostgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_restore_tests")
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
            $"bunkfy-reservation-rights-delta-{Guid.NewGuid():N}");
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            disableOutboxPublisher: false);
        await api.MigrateGuestDataRightsAuthorizationDatabaseAsync()
            .ConfigureAwait(false);

        using IHost worker = CreateWorker(
            connectionString,
            natsConnectionString,
            ledgerDeltaPath);
        await MigrateTaskRuntimeAsync(worker).ConfigureAwait(false);
        (Reservation reservation, DataRightsCase dataRightsCase) =
            await SeedReservationExecutionCandidateAsync(api)
                .ConfigureAwait(false);
        DataRightsExecutionDto execution =
            await ApproveAndStartExecutionAsync(api, dataRightsCase)
                .ConfigureAwait(false);

        await WaitForDataRightsOutboxAsync(
            api,
            expectedEventCount: 1,
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        await worker.StartAsync().ConfigureAwait(false);
        bool workerStarted = true;
        try
        {
            TaskRun taskRun = await WaitForTaskRunAsync(
                worker,
                dataRightsCase.Id,
                TaskRunStatus.Succeeded,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await WaitForExecutionCompletionAsync(
                worker,
                dataRightsCase.Id,
                expectedWorkItemCount: 1,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            using IServiceScope scope = worker.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            ReservationsDbContext reservations = scope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            DataRightsDbContext dataRights = scope.ServiceProvider
                .GetRequiredService<DataRightsDbContext>();
            Reservation anonymised = await reservations.Reservations
                .AsNoTracking()
                .SingleAsync(item => item.Id == reservation.Id)
                .ConfigureAwait(false);
            ReservationAnonymisationReceipt receipt =
                await reservations.AnonymisationReceipts
                    .AsNoTracking()
                    .SingleAsync()
                    .ConfigureAwait(false);
            ReservationAnonymisationTombstone tombstone =
                await reservations.AnonymisationTombstones
                    .AsNoTracking()
                    .SingleAsync()
                    .ConfigureAwait(false);
            DataRightsExecutionWorkItem workItem =
                await dataRights.ExecutionWorkItems
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == Assert.Single(execution.WorkItems).Id)
                    .ConfigureAwait(false);
            DataRightsProcessingLedgerEntry ledger =
                await dataRights.ProcessingLedgerEntries
                    .AsNoTracking()
                    .SingleAsync(item => item.WorkItemId == workItem.Id)
                    .ConfigureAwait(false);

            Assert.True(anonymised.IsAnonymised);
            Assert.Equal(
                Reservation.AnonymisedGuestName,
                anonymised.PrimaryGuestName);
            Assert.Null(anonymised.Email);
            Assert.Null(anonymised.Phone);
            Assert.Null(anonymised.Notes);
            Assert.Empty(anonymised.Guests);
            Assert.True(tombstone.Matches(receipt));
            Assert.Equal(taskRun.Id, workItem.TaskRunId);
            Assert.Equal(receipt.Id, workItem.OwnerReceiptId);
            Assert.Equal(
                receipt.ResultingReservationVersion,
                workItem.ResultingRecordVersion);
            Assert.Equal(
                receipt.ResultingReservationVersion,
                ledger.ResultingRecordVersion);
            Assert.Equal(2, ledger.ContractVersion);

            string protectedFiles = string.Join(
                '\n',
                Directory.GetFiles(
                        ledgerDeltaPath,
                        "*",
                        SearchOption.AllDirectories)
                    .Select(File.ReadAllText));
            Assert.DoesNotContain(
                reservation.Id.ToString("N"),
                protectedFiles,
                StringComparison.OrdinalIgnoreCase);

            await worker.StopAsync().ConfigureAwait(false);
            workerStarted = false;

            using IHost restoredWorker = CreateWorker(
                restoredPostgreSql.GetConnectionString(),
                natsConnectionString,
                ledgerDeltaPath);
            await MigrateRestoreDatabasesAsync(restoredWorker)
                .ConfigureAwait(false);
            await SeedPreAnonymisationReservationSnapshotAsync(
                    restoredWorker,
                    reservation.Id)
                .ConfigureAwait(false);

            await restoredWorker.StartAsync().ConfigureAwait(false);
            try
            {
                using IServiceScope restoredScope =
                    restoredWorker.Services.CreateScope();
                restoredScope.ServiceProvider
                    .GetRequiredService<ITenantContextAccessor>()
                    .SetTenant(TenantId);
                ReservationsDbContext restoredReservations =
                    restoredScope.ServiceProvider
                        .GetRequiredService<ReservationsDbContext>();
                DataRightsDbContext restoredDataRights =
                    restoredScope.ServiceProvider
                        .GetRequiredService<DataRightsDbContext>();
                Reservation restoredReservation =
                    await restoredReservations.Reservations
                        .AsNoTracking()
                        .SingleAsync(item => item.Id == reservation.Id)
                        .ConfigureAwait(false);
                ReservationAnonymisationRestoreReceipt restoreReceipt =
                    await restoredReservations.AnonymisationRestoreReceipts
                        .AsNoTracking()
                        .SingleAsync()
                        .ConfigureAwait(false);
                ReservationAnonymisationTombstone restoredTombstone =
                    await restoredReservations.AnonymisationTombstones
                        .AsNoTracking()
                        .SingleAsync(item => item.Id == reservation.Id)
                        .ConfigureAwait(false);
                DataRightsRestoreCheckpoint restoreCheckpoint =
                    await restoredDataRights.RestoreCheckpoints
                        .AsNoTracking()
                        .SingleAsync()
                        .ConfigureAwait(false);
                DataRightsRestoreReadinessSnapshot readiness =
                    restoredScope.ServiceProvider
                        .GetRequiredService<IDataRightsRestoreReadiness>()
                        .Snapshot;

                Assert.True(restoredReservation.IsAnonymised);
                Assert.Equal(
                    Reservation.AnonymisedGuestName,
                    restoredReservation.PrimaryGuestName);
                Assert.Null(restoredReservation.Email);
                Assert.Null(restoredReservation.Phone);
                Assert.Null(restoredReservation.Notes);
                Assert.Empty(restoredReservation.Guests);
                Assert.Equal(ledger.Id, restoreReceipt.LedgerEntryId);
                Assert.Equal(receipt.Id, restoreReceipt.OwnerReceiptId);
                Assert.Equal(
                    receipt.CanonicalSha256,
                    restoreReceipt.OwnerReceiptSha256);
                Assert.Equal(ledger.Id, restoredTombstone.LedgerEntryId);
                Assert.Equal(
                    receipt.CanonicalSha256,
                    restoredTombstone.OwnerReceiptSha256);
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

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Multi_owner_execution_completes_only_after_every_owner_records_durable_proof()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_multi_owner_execution_tests")
                .Build();
        await Task.WhenAll(nats.StartAsync(), postgreSql.StartAsync())
            .ConfigureAwait(false);

        string connectionString = postgreSql.GetConnectionString();
        string natsConnectionString =
            AuthTestContainers.GetNatsConnectionString(nats);
        string ledgerDeltaPath = Path.Combine(
            Path.GetTempPath(),
            $"bunkfy-multi-owner-rights-delta-{Guid.NewGuid():N}");
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            disableOutboxPublisher: false);
        await api.MigrateGuestDataRightsAuthorizationDatabaseAsync()
            .ConfigureAwait(false);

        using IHost worker = CreateWorker(
            connectionString,
            natsConnectionString,
            ledgerDeltaPath);
        await MigrateTaskRuntimeAsync(worker).ConfigureAwait(false);
        (GuestProfile guest, Reservation reservation, DataRightsCase dataRightsCase) =
            await SeedMultiOwnerExecutionCandidateAsync(api).ConfigureAwait(false);
        DataRightsExecutionDto execution =
            await ApproveAndStartExecutionAsync(api, dataRightsCase)
                .ConfigureAwait(false);

        Assert.Equal(2, execution.Batch.SelectedSubjectCount);
        Assert.Equal(2, execution.WorkItems.Count);
        await WaitForDataRightsOutboxAsync(
            api,
            expectedEventCount: 2,
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        await worker.StartAsync().ConfigureAwait(false);
        bool workerStarted = true;
        try
        {
            IReadOnlyList<TaskRun> taskRuns = await WaitForTaskRunsAsync(
                worker,
                dataRightsCase.Id,
                expectedCount: 2,
                TaskRunStatus.Succeeded,
                TimeSpan.FromSeconds(40)).ConfigureAwait(false);
            await WaitForExecutionCompletionAsync(
                worker,
                dataRightsCase.Id,
                expectedWorkItemCount: 2,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            using IServiceScope scope = worker.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            GuestProfile anonymisedGuest = await scope.ServiceProvider
                .GetRequiredService<GuestsDbContext>()
                .GuestProfiles.AsNoTracking()
                .SingleAsync(item => item.Id == guest.Id)
                .ConfigureAwait(false);
            Reservation anonymisedReservation = await scope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>()
                .Reservations.AsNoTracking()
                .SingleAsync(item => item.Id == reservation.Id)
                .ConfigureAwait(false);
            DataRightsDbContext dataRights = scope.ServiceProvider
                .GetRequiredService<DataRightsDbContext>();
            List<DataRightsExecutionWorkItem> workItems =
                await dataRights.ExecutionWorkItems
                    .AsNoTracking()
                    .Where(item => item.CaseId == dataRightsCase.Id)
                    .OrderBy(item => item.OwnerKey)
                    .ToListAsync()
                    .ConfigureAwait(false);
            List<DataRightsProcessingLedgerEntry> ledgerEntries =
                await dataRights.ProcessingLedgerEntries
                    .AsNoTracking()
                    .Where(item => item.CaseId == dataRightsCase.Id)
                    .OrderBy(item => item.TenantSequence)
                    .ToListAsync()
                    .ConfigureAwait(false);

            Assert.Equal(GuestProfileState.Anonymised, anonymisedGuest.Status);
            Assert.True(anonymisedReservation.IsAnonymised);
            Assert.Equal(2, taskRuns.Count);
            Assert.Equal(2, workItems.Count);
            Assert.Equal(
                2,
                workItems.Select(item => item.IdempotencyKey).Distinct().Count());
            Assert.All(
                workItems,
                item => Assert.Equal(DataRightsExecutionWorkItemState.Completed, item.State));
            Assert.Equal(2, ledgerEntries.Count);
            Assert.Equal([1L, 2L], ledgerEntries.Select(item => item.TenantSequence));
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
            new HostApplicationBuilderSettings { EnvironmentName = "Integration" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationIdentity:DisplayName"] = "BunkFy DataRights execution worker",
            ["ApplicationIdentity:Namespace"] = "bunkfy",
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:PostgreSql"] = connectionString,
            ["ConnectionStrings:nats"] = natsConnectionString,
            ["DataRights:LedgerDelta:Provider"] = "LocalFile",
            ["DataRights:LedgerDelta:LocalFilePath"] = ledgerDeltaPath,
            ["DataRights:LedgerDelta:ActiveIntegrityKeyVersion"] = "1",
            ["DataRights:LedgerDelta:IntegrityKeys:1"] =
                "aWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWk=",
            ["Tenancy:Enabled"] = "true",
            ["Caching:Enabled"] = "false",
            ["NatsJetStream:Enabled"] = "true",
            ["NatsConsumers:Enabled"] = "true",
            ["NatsConsumers:FetchBatchSize"] = "10",
            ["NatsConsumers:PollInterval"] = "00:00:00.100",
            ["NatsConsumers:AckWait"] = "00:00:05",
            ["NatsConsumers:AckProgressInterval"] = "00:00:01",
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
            ["Tasks:Worker:WorkerId"] = "data-rights-execution-test",
            ["Tasks:Worker:NodeId"] = "data-rights-execution-node",
            ["Tasks:Worker:TimeoutScannerEnabled"] = "false",
            ["Tasks:Worker:MetricsSamplerEnabled"] = "false",
            ["Worker:Modules:Properties"] = "true",
            ["Worker:Modules:Inventory"] = "true",
            ["Worker:Modules:Reservations"] = "true",
            ["Worker:Modules:Guests"] = "true",
            ["Worker:Modules:DataRights"] = "true",
            ["Worker:Modules:TaskRuntime"] = "true"
        });
        builder.Logging.ClearProviders();
        builder.AddWorkerHost();
        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
        ModuleCompositionValidationResult composition = builder.ValidateModuleComposition();
        Assert.True(composition.IsValid, composition.Report);
        return builder.Build();
    }

    private static async Task MigrateTaskRuntimeAsync(IHost worker)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TaskRuntimeDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
    }

    private static async Task MigrateRestoreDatabasesAsync(IHost worker)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<GuestsDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<ReservationsDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<DataRightsDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<TaskRuntimeDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
    }

    private static async Task SeedPreAnonymisationSnapshotAsync(
        IHost worker,
        Guid guestId)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        GuestsDbContext guests =
            scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        GuestProfile profile = GuestProfile.Create(
            guestId,
            TenantId,
            PropertyId,
            "Restored Guest",
            "Restored Legal Name",
            "restored-guest@example.test",
            "+44 20 7946 0958",
            new DateOnly(1990, 1, 1),
            "GB",
            "en-GB",
            "This data must be scrubbed before readiness",
            "user:restore-test",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(-1)).Value;
        guests.GuestProfiles.Add(profile);
        await guests.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SeedPreAnonymisationReservationSnapshotAsync(
        IHost worker,
        Guid reservationId)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        ReservationsDbContext reservations =
            scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        Reservation reservation = CreateReservation(
            reservationId,
            DateTimeOffset.UtcNow.AddHours(-2));
        reservations.Reservations.Add(reservation);
        await reservations.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<(GuestProfile Guest, DataRightsCase Case)>
        SeedExecutionCandidateAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        PropertyCreatedIntegrationEvent propertyCreated = new(
            Guid.NewGuid(),
            TenantId,
            DateTimeOffset.UtcNow,
            PropertyId,
            "Durable execution house",
            "durable-execution-house",
            "UTC",
            PropertyStatus.Active,
            1);

        GuestsDbContext guests = scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        await using (var transaction = await guests.Database.BeginTransactionAsync()
                         .ConfigureAwait(false))
        {
            await ResolveHandler<PropertyCreatedIntegrationEvent>(
                    scope.ServiceProvider,
                    GuestsModuleMetadata.Name)
                .HandleAsync(propertyCreated, CancellationToken.None)
                .ConfigureAwait(false);
            await CountryPolicyIntegrationTestData.ApplyActivationAsync(
                scope.ServiceProvider,
                GuestsModuleMetadata.Name,
                TenantId,
                PropertyId,
                2).ConfigureAwait(false);
            await guests.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        GuestProfile guest = GuestProfile.Create(
            Guid.NewGuid(),
            TenantId,
            PropertyId,
            "Durable Guest",
            "Durable Legal Name",
            "durable-guest@example.test",
            "+44 20 7946 0958",
            new DateOnly(1990, 1, 1),
            "GB",
            "en-GB",
            "Must not cross the task boundary",
            "user:guest-operator",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-20)).Value;
        guests.GuestProfiles.Add(guest);
        await guests.SaveChangesAsync().ConfigureAwait(false);

        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            dataRights,
            token => ResolveHandler<PropertyCreatedIntegrationEvent>(
                    scope.ServiceProvider,
                    DataRightsModuleMetadata.Name)
                .HandleAsync(propertyCreated, token))
            .ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            DataRightsModuleMetadata.Name,
            TenantId,
            PropertyId,
            2).ConfigureAwait(false);

        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            PropertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:privacy-reviewer",
            startedAtUtc).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            GuestsDataRightsCoordinates.Owner,
            GuestsDataRightsCoordinates.GuestProfileRecordType,
            guest.Id,
            guest.Version,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            startedAtUtc.AddMinutes(4)).IsSuccess);
        dataRights.Cases.Add(dataRightsCase);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return (guest, dataRightsCase);
    }

    private static async Task<(Reservation Reservation, DataRightsCase Case)>
        SeedReservationExecutionCandidateAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        PropertyCreatedIntegrationEvent propertyCreated = new(
            Guid.NewGuid(),
            TenantId,
            DateTimeOffset.UtcNow.AddDays(-2),
            PropertyId,
            "Durable reservation execution house",
            "durable-reservation-execution-house",
            "UTC",
            PropertyStatus.Active,
            1);

        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            reservations,
            token => ResolveHandler<PropertyCreatedIntegrationEvent>(
                    scope.ServiceProvider,
                    ReservationsModuleMetadata.Name)
                .HandleAsync(propertyCreated, token))
            .ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            ReservationsModuleMetadata.Name,
            TenantId,
            PropertyId,
            2).ConfigureAwait(false);
        Reservation reservation = CreateReservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-20));
        await scope.ServiceProvider.GetRequiredService<IReservationRepository>()
            .AddAsync(reservation, CancellationToken.None)
            .ConfigureAwait(false);
        await reservations.SaveChangesAsync().ConfigureAwait(false);

        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            dataRights,
            token => ResolveHandler<PropertyCreatedIntegrationEvent>(
                    scope.ServiceProvider,
                    DataRightsModuleMetadata.Name)
                .HandleAsync(propertyCreated, token))
            .ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            DataRightsModuleMetadata.Name,
            TenantId,
            PropertyId,
            2).ConfigureAwait(false);

        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            PropertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:privacy-reviewer",
            startedAtUtc).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            ReservationsDataRightsCoordinates.Owner,
            ReservationsDataRightsCoordinates.ReservationRecordType,
            reservation.Id,
            reservation.Version,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            startedAtUtc.AddMinutes(4)).IsSuccess);
        dataRights.Cases.Add(dataRightsCase);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return (reservation, dataRightsCase);
    }

    private static async Task<(
        GuestProfile Guest,
        Reservation Reservation,
        DataRightsCase Case)> SeedMultiOwnerExecutionCandidateAsync(
        AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        PropertyCreatedIntegrationEvent propertyCreated = new(
            Guid.NewGuid(),
            TenantId,
            DateTimeOffset.UtcNow.AddDays(-2),
            PropertyId,
            "Multi-owner execution house",
            "multi-owner-execution-house",
            "UTC",
            PropertyStatus.Active,
            1);

        GuestsDbContext guests = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        await using (var transaction = await guests.Database.BeginTransactionAsync()
                         .ConfigureAwait(false))
        {
            await ResolveHandler<PropertyCreatedIntegrationEvent>(
                    scope.ServiceProvider,
                    GuestsModuleMetadata.Name)
                .HandleAsync(propertyCreated, CancellationToken.None)
                .ConfigureAwait(false);
            await CountryPolicyIntegrationTestData.ApplyActivationAsync(
                scope.ServiceProvider,
                GuestsModuleMetadata.Name,
                TenantId,
                PropertyId,
                2).ConfigureAwait(false);
            await guests.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        GuestProfile guest = GuestProfile.Create(
            Guid.NewGuid(),
            TenantId,
            PropertyId,
            "Multi-owner Guest",
            "Multi-owner Legal Name",
            "multi-owner-guest@example.test",
            "+44 20 7946 0958",
            new DateOnly(1990, 1, 1),
            "GB",
            "en-GB",
            "Must be removed by the guest owner",
            "user:guest-operator",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-20)).Value;
        guests.GuestProfiles.Add(guest);
        await guests.SaveChangesAsync().ConfigureAwait(false);

        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            reservations,
            token => ResolveHandler<PropertyCreatedIntegrationEvent>(
                    scope.ServiceProvider,
                    ReservationsModuleMetadata.Name)
                .HandleAsync(propertyCreated, token))
            .ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            ReservationsModuleMetadata.Name,
            TenantId,
            PropertyId,
            2).ConfigureAwait(false);
        Reservation reservation = CreateReservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-20));
        await scope.ServiceProvider.GetRequiredService<IReservationRepository>()
            .AddAsync(reservation, CancellationToken.None)
            .ConfigureAwait(false);
        await reservations.SaveChangesAsync().ConfigureAwait(false);

        DataRightsDbContext dataRights = scope.ServiceProvider
            .GetRequiredService<DataRightsDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            dataRights,
            token => ResolveHandler<PropertyCreatedIntegrationEvent>(
                    scope.ServiceProvider,
                    DataRightsModuleMetadata.Name)
                .HandleAsync(propertyCreated, token))
            .ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            DataRightsModuleMetadata.Name,
            TenantId,
            PropertyId,
            2).ConfigureAwait(false);

        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            PropertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:privacy-reviewer",
            startedAtUtc).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            GuestsDataRightsCoordinates.Owner,
            GuestsDataRightsCoordinates.GuestProfileRecordType,
            guest.Id,
            guest.Version,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            ReservationsDataRightsCoordinates.Owner,
            ReservationsDataRightsCoordinates.ReservationRecordType,
            reservation.Id,
            reservation.Version,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(4)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            startedAtUtc.AddMinutes(5)).IsSuccess);
        dataRights.Cases.Add(dataRightsCase);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return (guest, reservation, dataRightsCase);
    }

    private static Reservation CreateReservation(
        Guid reservationId,
        DateTimeOffset createdAtUtc)
    {
        Reservation reservation = Reservation.Create(
            reservationId,
            TenantId,
            PropertyId,
            Guid.NewGuid(),
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [Guid.NewGuid()],
            "Durable Reservation Guest",
            "durable-reservation@example.test",
            "+44 20 1234 5678",
            guestCount: 1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: "Must be scrubbed before readiness",
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: null,
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            createdAtUtc).Value;
        Assert.True(reservation.LinkGuest(
            Guid.NewGuid(),
            ReservationGuestRole.Primary,
            replaceExistingRole: false,
            reservation.Version,
            "user:front-desk",
            Guid.NewGuid(),
            createdAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(reservation.RejectAllocation(
            reservation.AllocationRequestId,
            ReservationAllocationRejection.AllocationConflict,
            Guid.NewGuid(),
            createdAtUtc.AddMinutes(2)).IsSuccess);
        return reservation;
    }

    private static async Task<DataRightsExecutionDto> ApproveAndStartExecutionAsync(
        AuthTestApplication api,
        DataRightsCase dataRightsCase)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        IRequestDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();
        Result<DataRightsCaseDto> approved = await dispatcher.SendAsync(
            new RecordDataRightsDecisionCommand(
                DataRightsCaseScope.ForProperty(PropertyId),
                dataRightsCase.Id,
                DataRightsDecisionOutcome.Approved,
                DataRightsDecisionReason.RequestValidated,
                dataRightsCase.Version,
                "user:decision-maker"),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(approved.IsSuccess, approved.Error.Code);
        Assert.NotNull(approved.Value.ApprovalEvidence);

        Result<DataRightsExecutionDto> started = await dispatcher.SendAsync(
            new StartDataRightsAnonymisationExecutionCommand(
                DataRightsCaseScope.ForProperty(PropertyId),
                dataRightsCase.Id,
                Guid.NewGuid(),
                approved.Value.Version,
                "user:privacy-executor"),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(started.IsSuccess, started.Error.Code);
        Assert.NotEmpty(started.Value.WorkItems);
        Assert.All(
            started.Value.WorkItems,
            workItem => Assert.Equal(
                DataRightsExecutionWorkItemStatus.Prepared,
                workItem.Status));
        return started.Value;
    }

    private static IIntegrationEventHandler<TEvent> ResolveHandler<TEvent>(
        IServiceProvider services,
        string consumerModule)
        where TEvent : IIntegrationEvent
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions.Single(item =>
                item.ConsumerModule == consumerModule &&
                item.EventType == typeof(TEvent));
        return (IIntegrationEventHandler<TEvent>)services.GetRequiredService(
            subscription.HandlerType);
    }

    private static async Task WaitForDataRightsOutboxAsync(
        AuthTestApplication api,
        int expectedEventCount,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            int processedCount = await scope.ServiceProvider.GetRequiredService<DataRightsDbContext>()
                .OutboxMessages.AsNoTracking()
                .CountAsync(message =>
                    message.EventType ==
                        typeof(DataRightsAnonymisationExecutionPreparedIntegrationEvent).FullName &&
                    message.ProcessedAtUtc != null)
                .ConfigureAwait(false);
            if (processedCount >= expectedEventCount)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The DataRights outbox did not publish the anonymisation execution event.");
    }

    private static async Task<IReadOnlyList<TaskRun>> WaitForTaskRunsAsync(
        IHost worker,
        Guid caseId,
        int expectedCount,
        TaskRunStatus status,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = worker.Services.CreateScope();
            List<TaskRun> taskRuns = await scope.ServiceProvider
                .GetRequiredService<TaskRuntimeDbContext>()
                .TaskRuns.AsNoTracking()
                .Where(run =>
                    run.ModuleName == DataRightsModuleMetadata.Name &&
                    run.TaskName == ExecuteDataRightsAnonymisationPayload.TaskName &&
                    run.CorrelationId == caseId)
                .OrderBy(run => run.Id)
                .ToListAsync()
                .ConfigureAwait(false);
            TaskRun? failed = taskRuns.FirstOrDefault(run =>
                run.Status is TaskRunStatus.Failed or TaskRunStatus.Canceled);
            if (failed is not null)
            {
                Assert.Fail(
                    $"DataRights anonymisation task ended as {failed.Status}: " +
                    failed.LastError);
            }

            if (taskRuns.Count == expectedCount &&
                taskRuns.All(run => run.Status == status))
            {
                return taskRuns;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The DataRights anonymisation tasks did not reach the expected state.");
    }

    private static async Task WaitForExecutionCompletionAsync(
        IHost worker,
        Guid caseId,
        int expectedWorkItemCount,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = worker.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            DataRightsDbContext dataRights = scope.ServiceProvider
                .GetRequiredService<DataRightsDbContext>();
            DataRightsCase? dataRightsCase = await dataRights.Cases
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == caseId)
                .ConfigureAwait(false);
            List<DataRightsExecutionWorkItem> workItems =
                await dataRights.ExecutionWorkItems
                    .AsNoTracking()
                    .Where(item => item.CaseId == caseId)
                    .ToListAsync()
                    .ConfigureAwait(false);
            if (dataRightsCase?.Status == DataRightsCaseState.Completed &&
                workItems.Count == expectedWorkItemCount &&
                workItems.All(item =>
                    item.State == DataRightsExecutionWorkItemState.Completed))
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The DataRights execution did not reconcile to completion.");
    }

    private static async Task AssertNoTaskRunAsync(IHost worker, Guid caseId)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        bool exists = await scope.ServiceProvider.GetRequiredService<TaskRuntimeDbContext>()
            .TaskRuns.AsNoTracking()
            .AnyAsync(run =>
                run.ModuleName == DataRightsModuleMetadata.Name &&
                run.CorrelationId == caseId)
            .ConfigureAwait(false);
        Assert.False(exists);
    }

    private static async Task<TaskRun> WaitForTaskRunAsync(
        IHost worker,
        Guid caseId,
        TaskRunStatus status,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = worker.Services.CreateScope();
            TaskRun? taskRun = await scope.ServiceProvider.GetRequiredService<TaskRuntimeDbContext>()
                .TaskRuns.AsNoTracking()
                .SingleOrDefaultAsync(run =>
                    run.ModuleName == DataRightsModuleMetadata.Name &&
                    run.TaskName == ExecuteDataRightsAnonymisationPayload.TaskName &&
                    run.CorrelationId == caseId)
                .ConfigureAwait(false);
            if (taskRun?.Status == status)
            {
                return taskRun;
            }

            if (taskRun?.Status is TaskRunStatus.Failed or TaskRunStatus.Canceled)
            {
                Assert.Fail(
                    $"DataRights anonymisation task ended as {taskRun.Status}: " +
                    taskRun.LastError);
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The DataRights anonymisation task did not reach the expected state.");
    }
}

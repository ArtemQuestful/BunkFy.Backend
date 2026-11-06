namespace Integration.Tests;

using System.Security.Cryptography;
using BunkFy.Adapter.Abstractions;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Ingestion.Application;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.DataRights;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.FileManagement.LocalStorage;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class IngestionDataRightsIntegrationTests
{
    private const string TenantId = "tenant-ingestion-data-rights";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 18, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Approved_anonymisation_executes_replays_and_restores_on_postgresql()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase(
                    "bunkfy_ingestion_anonymisation_execution_tests")
                .Build();
        await postgreSql.StartAsync();

        string fileRoot = Path.Combine(
            Path.GetTempPath(),
            $"bunkfy-ingestion-anonymisation-{Guid.NewGuid():N}");
        try
        {
            TestApprovalGate approvalGate = new();
            using ServiceProvider provider = CreatePersistenceProvider(
                postgreSql.GetConnectionString(),
                fileRoot,
                approvalGate);
            Guid propertyId = Guid.NewGuid();
            Guid reservationId = Guid.NewGuid();
            Guid connectionId = Guid.NewGuid();
            Guid receiptId = Guid.NewGuid();
            Guid payloadFileId = Guid.NewGuid();
            byte[] payload =
                """{"guest":"Maya Chen","reference":"provider-execution-42"}"""u8
                    .ToArray();
            string payloadSha256 = Sha256(payload);
            DataRightsAnonymisationContributionRequest request;
            DataRightsAnonymisationContributionResult executed;

            using (IServiceScope scope = provider.CreateScope())
            {
                IngestionDbContext dbContext = scope.ServiceProvider
                    .GetRequiredService<IngestionDbContext>();
                await dbContext.Database.MigrateAsync();
                IIngestionPropertyProjectionRepository properties =
                    scope.ServiceProvider.GetRequiredService<
                        IIngestionPropertyProjectionRepository>();
                await using (var transaction =
                    await dbContext.Database.BeginTransactionAsync())
                {
                    await properties.ApplySnapshotAsync(
                        new(
                            TenantId,
                            propertyId,
                            "Execution House",
                            "execution-house",
                            IsActive: true,
                            PropertyProcessingStatus.Unconfigured,
                            GovernancePolicy: null,
                            SourceVersion: 1),
                        CancellationToken.None);
                    await dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();
                }

                await CountryPolicyIntegrationTestData
                    .ApplyActivationAsync(
                        scope.ServiceProvider,
                        IngestionModuleMetadata.Name,
                        TenantId,
                        propertyId,
                        propertyVersion: 2);

                AdapterConnection connection = AdapterConnection.Create(
                    connectionId,
                    TenantId,
                    propertyId,
                    "booking.com",
                    AdapterExecutionMode.Push,
                    IngestionConflictPolicy.SuggestionsOnly,
                    "configuration://anonymisation",
                    secretReference: null,
                    Now).Value;
                ObservationReceipt observation = CreateReceipt(
                    receiptId,
                    propertyId,
                    connectionId,
                    payloadFileId,
                    payloadSha256);
                ReservationSourceLink sourceLink = CreateLinkedSource(
                    propertyId,
                    reservationId,
                    connectionId,
                    receiptId,
                    payloadSha256,
                    cancelled: true);
                dbContext.AddRange(
                    connection,
                    observation,
                    sourceLink);
                await dbContext.SaveChangesAsync();
                IRawPayloadStore rawPayloadStore =
                    scope.ServiceProvider
                        .GetRequiredService<IRawPayloadStore>();
                await rawPayloadStore.StoreAsync(
                    new(
                        payloadFileId,
                        TenantId,
                        connectionId,
                        "application/json",
                        payload,
                        payloadSha256),
                    CancellationToken.None);

                IngestionPropertyProjection property =
                    await dbContext.PropertyProjections
                        .AsNoTracking()
                        .SingleAsync(item => item.Id == propertyId);
                DataRightsApprovalEvidence approval =
                    CreateApproval(property);
                approvalGate.Approval = approval;
                request = new(
                    DataRightsAnonymisationContract.CurrentVersion,
                    TenantId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    propertyId,
                    Guid.NewGuid(),
                    ApprovalRevision: 2,
                    OperationRevision: 3,
                    new(
                        IngestionDataRightsCoordinates.Owner,
                        IngestionDataRightsCoordinates
                            .ReservationSourceLinkRecordType,
                        sourceLink.Id,
                        sourceLink.Version),
                    approval,
                    "user:privacy-executor",
                    Now.AddHours(1));
                IDataRightsAnonymisationContributor contributor =
                    scope.ServiceProvider
                        .GetServices<
                            IDataRightsAnonymisationContributor>()
                        .Single(item =>
                            item.OwnerKey ==
                            IngestionDataRightsCoordinates.Owner);

                executed = await contributor.ExecuteAsync(
                    request,
                    CancellationToken.None);

                Assert.True(
                    executed.Status ==
                    DataRightsAnonymisationContributionStatus.Completed,
                    executed.OutcomeCode);
                Assert.NotNull(executed.OwnerProof);
                dbContext.ChangeTracker.Clear();
                ReservationSourceLink reducedLink =
                    await dbContext.ReservationSourceLinks
                        .SingleAsync(item =>
                            item.Id == sourceLink.Id);
                ObservationReceipt reducedObservation =
                    await dbContext.ObservationReceipts
                        .SingleAsync(item =>
                            item.Id == observation.Id);
                IngestionAnonymisationTombstone tombstone =
                    await dbContext.AnonymisationTombstones
                        .SingleAsync(item =>
                            item.Id == sourceLink.Id);
                IngestionAnonymisationReceipt ownerReceipt =
                    await dbContext.AnonymisationReceipts
                        .SingleAsync(item =>
                            item.Id == executed.OwnerProof.ReceiptId);
                Assert.Equal(
                    ReservationSourceLinkState.Anonymised,
                    reducedLink.State);
                Assert.Equal(
                    RawPayloadRetentionState.Purged,
                    reducedObservation.RawPayloadRetentionState);
                Assert.Equal(
                    IngestionAnonymisationOrigin.LiveExecution,
                    tombstone.Origin);
                Assert.Equal(
                    IngestionAnonymisationTombstoneState.Completed,
                    tombstone.State);
                Assert.True(tombstone.MatchesOwnerProof(
                    propertyId,
                    ownerReceipt.ContractVersion,
                    ownerReceipt.Id,
                    ownerReceipt.CanonicalSha256,
                    ownerReceipt.ResultingSourceLinkVersion,
                    ownerReceipt.CompletedAtUtc));
                Assert.Equal(
                    ownerReceipt.CanonicalSha256,
                    executed.OwnerProof.ReceiptSha256);
                Assert.Null(await rawPayloadStore.ReadAsync(
                    payloadFileId,
                    TenantId,
                    connectionId,
                    CancellationToken.None));
            }

            using (IServiceScope restart = provider.CreateScope())
            {
                IDataRightsAnonymisationContributor contributor =
                    restart.ServiceProvider
                        .GetServices<
                            IDataRightsAnonymisationContributor>()
                        .Single(item =>
                            item.OwnerKey ==
                            IngestionDataRightsCoordinates.Owner);
                DataRightsAnonymisationContributionResult replayed =
                    await contributor.ExecuteAsync(
                        request,
                        CancellationToken.None);
                Assert.Equal(executed, replayed);

                Guid ledgerEntryId = Guid.NewGuid();
                IDataRightsAnonymisationRestoreContributor restore =
                    restart.ServiceProvider
                        .GetServices<
                            IDataRightsAnonymisationRestoreContributor>()
                        .Single(item =>
                            item.OwnerKey ==
                            IngestionDataRightsCoordinates.Owner);
                DataRightsAnonymisationRestoreResult restored =
                    await restore.RestoreAsync(
                        new(
                            DataRightsAnonymisationRestoreContract
                                .CurrentVersion,
                            TenantId,
                            ledgerEntryId,
                            TenantSequence: 21,
                            new string('f', 64),
                            propertyId,
                            IngestionDataRightsCoordinates.Owner,
                            IngestionDataRightsCoordinates
                                .ReservationSourceLinkRecordType,
                            request.Coordinate.RecordId,
                            executed.OwnerProof!.ReceiptContractVersion,
                            executed.OwnerProof.ReceiptId,
                            executed.OwnerProof.ReceiptSha256,
                            executed.OwnerProof.ResultingRecordVersion,
                            executed.OwnerProof.CompletedAtUtc),
                        CancellationToken.None);
                Assert.Equal(
                    DataRightsAnonymisationRestoreStatus.Completed,
                    restored.Status);
                Assert.NotNull(restored.Proof);

                IngestionDbContext dbContext =
                    restart.ServiceProvider
                        .GetRequiredService<IngestionDbContext>();
                dbContext.ChangeTracker.Clear();
                IngestionAnonymisationTombstone tombstone =
                    await dbContext.AnonymisationTombstones
                        .SingleAsync(item =>
                            item.Id == request.Coordinate.RecordId);
                Assert.Equal(ledgerEntryId, tombstone.LedgerEntryId);
                Assert.NotNull(tombstone.LastReplayedAtUtc);
                Assert.Equal(
                    executed.OwnerProof.ReceiptSha256,
                    tombstone.OwnerReceiptSha256);
            }
        }
        finally
        {
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Ingestion_contributors_stream_exact_provider_evidence_on_postgresql()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_ingestion_data_rights_tests")
            .Build();
        await postgreSql.StartAsync();

        string fileRoot = Path.Combine(
            Path.GetTempPath(),
            $"bunkfy-ingestion-data-rights-{Guid.NewGuid():N}");
        try
        {
            using ServiceProvider provider = CreatePersistenceProvider(
                postgreSql.GetConnectionString(),
                fileRoot);
            using IServiceScope scope = provider.CreateScope();
            IngestionDbContext dbContext =
                scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
            await dbContext.Database.MigrateAsync();

            Guid propertyId = Guid.NewGuid();
            Guid reservationId = Guid.NewGuid();
            Guid connectionId = Guid.NewGuid();
            Guid receiptId = Guid.NewGuid();
            Guid payloadFileId = Guid.NewGuid();
            IIngestionPropertyProjectionRepository properties =
                scope.ServiceProvider
                    .GetRequiredService<IIngestionPropertyProjectionRepository>();
            await using (var transaction =
                await dbContext.Database.BeginTransactionAsync())
            {
                await properties.ApplySnapshotAsync(
                    new IngestionPropertyProjectionWriteModel(
                        TenantId,
                        propertyId,
                        "Provider House",
                        "provider-house",
                        IsActive: true,
                        PropertyProcessingStatus.Unconfigured,
                        GovernancePolicy: null,
                        SourceVersion: 1),
                    CancellationToken.None);
                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }

            AdapterConnection connection = AdapterConnection.Create(
                connectionId,
                TenantId,
                propertyId,
                "fake.http",
                AdapterExecutionMode.Push,
                IngestionConflictPolicy.SuggestionsOnly,
                "configuration://data-rights",
                secretReference: null,
                Now).Value;
            byte[] payload = Enumerable.Range(0, 18_765)
                .Select(index => (byte)(index % 239))
                .ToArray();
            string hash = Sha256(payload);
            ObservationReceipt receipt = CreateReceipt(
                receiptId,
                propertyId,
                connectionId,
                payloadFileId,
                hash);
            ReservationSourceLink sourceLink = CreateLinkedSource(
                propertyId,
                reservationId,
                connectionId,
                receiptId,
                hash);
            dbContext.AdapterConnections.Add(connection);
            dbContext.ObservationReceipts.Add(receipt);
            dbContext.ReservationSourceLinks.Add(sourceLink);
            await dbContext.SaveChangesAsync();

            IRawPayloadStore rawPayloadStore =
                scope.ServiceProvider.GetRequiredService<IRawPayloadStore>();
            await rawPayloadStore.StoreAsync(
                new RawPayloadWrite(
                    payloadFileId,
                    TenantId,
                    connectionId,
                    "application/json",
                    payload,
                    hash),
                CancellationToken.None);

            IDataRightsSubjectDiscoveryContributor discovery = scope.ServiceProvider
                .GetServices<IDataRightsSubjectDiscoveryContributor>()
                .Single(contributor => contributor.OwnerKey == "ingestion");
            DataRightsSubjectDiscoveryResult discovered = await discovery.DiscoverAsync(
                new DataRightsSubjectDiscoveryRequest(
                    TenantId,
                    DataRightsCaseType.GuestRights,
                    propertyId,
                    new DataRightsSubjectLookup(
                        reservationId,
                        Email: null,
                        Phone: null,
                        Name: null,
                        DateOfBirth: null),
                    DataRightsSubjectDiscoveryLimits.MaxCandidates),
                CancellationToken.None);
            DataRightsSubjectCandidate candidate = Assert.Single(discovered.Candidates);

            IDataRightsSubjectExportContributor exporter = scope.ServiceProvider
                .GetServices<IDataRightsSubjectExportContributor>()
                .Single(contributor => contributor.OwnerKey == "ingestion");
            CollectingSink sink = new();
            DataRightsSubjectExportResult exported = await exporter.ExportAsync(
                new DataRightsSubjectExportRequest(
                    TenantId,
                    DataRightsCaseType.GuestRights,
                    propertyId,
                    candidate.Coordinate),
                sink,
                CancellationToken.None);

            Assert.Equal(DataRightsSubjectExportStatus.Succeeded, exported.Status);
            Assert.Equal(exported.RecordCount, sink.Records.Count);
            Assert.Equal(
                sourceLink.SourceReference,
                Field(
                    Assert.Single(
                        sink.Records,
                        record => record.RecordType == "reservation-source-link"),
                    "ingestion.operations.source-reference").GetString());
            byte[] reconstructed = sink.Records
                .Where(record =>
                    record.RecordType ==
                    "ingestion-raw-payload-chunk")
                .OrderBy(record =>
                    Field(record, "ingestion.operations.raw-payload-chunk-index")
                        .GetInt32())
                .SelectMany(record =>
                    Field(record, "ingestion.raw-source.content")
                        .GetBytesFromBase64())
                .ToArray();
            Assert.Equal(payload, reconstructed);

            IIngestionAnonymisationEligibilityEvaluator evaluator =
                scope.ServiceProvider.GetRequiredService<
                    IIngestionAnonymisationEligibilityEvaluator>();
            IngestionAnonymisationEligibilityResult eligibility =
                await evaluator.EvaluateAsync(
                    new(
                        IngestionAnonymisationEligibilityContract.CurrentVersion,
                        TenantId,
                        Guid.NewGuid(),
                        ApprovalRevision: 1,
                        OperationRevision: 2,
                        propertyId,
                        sourceLink.Id,
                        sourceLink.Version,
                        new(
                            PropertyPolicySourceVersion: 1,
                            "GB",
                            "integration-hostel-baseline",
                            PolicyVersion: 1,
                            "integration-guest-operational",
                            RetentionPolicyVersion: 1,
                            new string('a', 64),
                            "data-rights-anonymisation",
                            "erasure",
                            "authorized-workspace-operator",
                            Now)),
                    CancellationToken.None);
            Assert.Equal(
                IngestionAnonymisationBlockerCode.PropertyPolicyUnavailable,
                eligibility.BlockerCode);
        }
        finally
        {
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Ingestion_restore_is_durable_isolated_and_replay_safe_on_postgresql()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_ingestion_restore_tests")
                .Build();
        await postgreSql.StartAsync();

        string fileRoot = Path.Combine(
            Path.GetTempPath(),
            $"bunkfy-ingestion-restore-{Guid.NewGuid():N}");
        try
        {
            using ServiceProvider provider = CreatePersistenceProvider(
                postgreSql.GetConnectionString(),
                fileRoot);
            using IServiceScope scope = provider.CreateScope();
            IngestionDbContext dbContext = scope.ServiceProvider
                .GetRequiredService<IngestionDbContext>();
            await dbContext.Database.MigrateAsync();

            Guid propertyId = Guid.NewGuid();
            Guid reservationId = Guid.NewGuid();
            Guid connectionId = Guid.NewGuid();
            Guid receiptId = Guid.NewGuid();
            Guid payloadFileId = Guid.NewGuid();
            IIngestionPropertyProjectionRepository properties =
                scope.ServiceProvider
                    .GetRequiredService<IIngestionPropertyProjectionRepository>();
            await using (var transaction =
                await dbContext.Database.BeginTransactionAsync())
            {
                await properties.ApplySnapshotAsync(
                    new IngestionPropertyProjectionWriteModel(
                        TenantId,
                        propertyId,
                        "Restore House",
                        "restore-house",
                        IsActive: true,
                        PropertyProcessingStatus.Unconfigured,
                        GovernancePolicy: null,
                        SourceVersion: 1),
                    CancellationToken.None);
                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            AdapterConnection connection = AdapterConnection.Create(
                connectionId,
                TenantId,
                propertyId,
                "booking.com",
                AdapterExecutionMode.Push,
                IngestionConflictPolicy.SuggestionsOnly,
                "configuration://restore",
                secretReference: null,
                Now).Value;
            byte[] payload =
                """{"guest":"Maya Chen","reference":"provider-restore-42"}"""u8
                    .ToArray();
            string hash = Sha256(payload);
            ObservationReceipt receipt = CreateReceipt(
                receiptId,
                propertyId,
                connectionId,
                payloadFileId,
                hash);
            ReservationSourceLink sourceLink = CreateLinkedSource(
                propertyId,
                reservationId,
                connectionId,
                receiptId,
                hash,
                cancelled: true);
            dbContext.AddRange(connection, receipt, sourceLink);
            await dbContext.SaveChangesAsync();

            IRawPayloadStore rawPayloadStore = scope.ServiceProvider
                .GetRequiredService<IRawPayloadStore>();
            await rawPayloadStore.StoreAsync(
                new(
                    payloadFileId,
                    TenantId,
                    connectionId,
                    "application/json",
                    payload,
                    hash),
                CancellationToken.None);
            IDataRightsSubjectDiscoveryContributor discovery =
                scope.ServiceProvider
                    .GetServices<IDataRightsSubjectDiscoveryContributor>()
                    .Single(item =>
                        item.OwnerKey ==
                        IngestionDataRightsCoordinates.Owner);
            DataRightsSubjectCandidate candidate = Assert.Single(
                (await discovery.DiscoverAsync(
                    new DataRightsSubjectDiscoveryRequest(
                        TenantId,
                        DataRightsCaseType.GuestRights,
                        propertyId,
                        new DataRightsSubjectLookup(
                            reservationId,
                            Email: null,
                            Phone: null,
                            Name: null,
                            DateOfBirth: null),
                        DataRightsSubjectDiscoveryLimits.MaxCandidates),
                    CancellationToken.None)).Candidates);
            DataRightsAnonymisationRestoreRequest request = new(
                DataRightsAnonymisationRestoreContract.CurrentVersion,
                TenantId,
                Guid.NewGuid(),
                TenantSequence: 17,
                new string('d', 64),
                propertyId,
                IngestionDataRightsCoordinates.Owner,
                IngestionDataRightsCoordinates
                    .ReservationSourceLinkRecordType,
                sourceLink.Id,
                OwnerReceiptContractVersion: 1,
                Guid.NewGuid(),
                new string('e', 64),
                sourceLink.Version + 1,
                Now.AddHours(1));
            IDataRightsAnonymisationRestoreContributor contributor =
                scope.ServiceProvider
                    .GetServices<
                        IDataRightsAnonymisationRestoreContributor>()
                    .Single(item =>
                        item.OwnerKey ==
                        IngestionDataRightsCoordinates.Owner);

            DataRightsAnonymisationRestoreResult restored =
                await contributor.RestoreAsync(
                    request,
                    CancellationToken.None);

            Assert.Equal(
                DataRightsAnonymisationRestoreStatus.Completed,
                restored.Status);
            Assert.NotNull(restored.Proof);
            dbContext.ChangeTracker.Clear();
            ReservationSourceLink reducedLink = await dbContext
                .ReservationSourceLinks
                .SingleAsync(item => item.Id == sourceLink.Id);
            ObservationReceipt reducedReceipt = await dbContext
                .ObservationReceipts
                .SingleAsync(item => item.Id == receipt.Id);
            IngestionAnonymisationTombstone tombstone = await dbContext
                .AnonymisationTombstones
                .SingleAsync(item => item.Id == sourceLink.Id);
            Assert.Equal(
                ReservationSourceLinkState.Anonymised,
                reducedLink.State);
            Assert.Equal(
                $"anonymised:{sourceLink.Id:N}",
                reducedLink.SourceReference);
            Assert.Equal(
                $"anonymised:{receipt.Id:N}",
                reducedReceipt.ExternalId);
            Assert.Equal(
                RawPayloadRetentionState.Purged,
                reducedReceipt.RawPayloadRetentionState);
            Assert.Equal(
                IngestionAnonymisationTombstoneState.Completed,
                tombstone.State);
            Assert.Equal(
                2,
                await dbContext.AnonymisationFingerprints.CountAsync());
            Assert.Equal(
                2,
                await dbContext.AnonymisationRecordPlan.CountAsync());
            Assert.Null(await rawPayloadStore.ReadAsync(
                payloadFileId,
                TenantId,
                connectionId,
                CancellationToken.None));

            DataRightsAnonymisationRestoreResult replayed =
                await contributor.RestoreAsync(
                    request,
                    CancellationToken.None);
            Assert.Equal(
                DataRightsAnonymisationRestoreStatus.Completed,
                replayed.Status);
            Assert.Equal(restored.Proof, replayed.Proof);
            Assert.Empty(
                (await discovery.DiscoverAsync(
                    new DataRightsSubjectDiscoveryRequest(
                        TenantId,
                        DataRightsCaseType.GuestRights,
                        propertyId,
                        new DataRightsSubjectLookup(
                            reservationId,
                            Email: null,
                            Phone: null,
                            Name: null,
                            DateOfBirth: null),
                        DataRightsSubjectDiscoveryLimits.MaxCandidates),
                    CancellationToken.None)).Candidates);
            IDataRightsSubjectExportContributor exporter =
                scope.ServiceProvider
                    .GetServices<IDataRightsSubjectExportContributor>()
                    .Single(item =>
                        item.OwnerKey ==
                        IngestionDataRightsCoordinates.Owner);
            CollectingSink postRestoreSink = new();
            DataRightsSubjectExportResult postRestoreExport =
                await exporter.ExportAsync(
                    new DataRightsSubjectExportRequest(
                        TenantId,
                        DataRightsCaseType.GuestRights,
                        propertyId,
                        candidate.Coordinate),
                    postRestoreSink,
                    CancellationToken.None);
            Assert.Equal(
                DataRightsSubjectExportStatus.NotFound,
                postRestoreExport.Status);
            Assert.Empty(postRestoreSink.Records);

            await using IngestionDbContext otherTenant =
                CreateDbContext(
                    postgreSql.GetConnectionString(),
                    "tenant-other");
            Assert.Empty(await otherTenant.AnonymisationTombstones
                .ToArrayAsync());
            Assert.Single(await otherTenant.AnonymisationTombstones
                .IgnoreQueryFilters()
                .ToArrayAsync());

            IngestionAnonymisationTombstone concurrent =
                CreateReducingTombstone();
            dbContext.AnonymisationTombstones.Add(concurrent);
            await dbContext.SaveChangesAsync();
            await using IngestionDbContext first =
                CreateDbContext(postgreSql.GetConnectionString(), TenantId);
            await using IngestionDbContext second =
                CreateDbContext(postgreSql.GetConnectionString(), TenantId);
            IngestionAnonymisationTombstone firstCopy =
                await first.AnonymisationTombstones
                    .SingleAsync(item => item.Id == concurrent.Id);
            IngestionAnonymisationTombstone secondCopy =
                await second.AnonymisationTombstones
                    .SingleAsync(item => item.Id == concurrent.Id);
            DateTimeOffset completedAt = Now.AddHours(2);
            Assert.True(firstCopy.CompleteRestore(completedAt).IsSuccess);
            Assert.True(secondCopy.CompleteRestore(completedAt).IsSuccess);
            await first.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => second.SaveChangesAsync());

            string[] tombstoneColumns = await dbContext.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT column_name AS "Value"
                    FROM information_schema.columns
                    WHERE table_schema = 'ingestion'
                      AND table_name = 'anonymisation_tombstones'
                    ORDER BY ordinal_position
                    """)
                .ToArrayAsync();
            Assert.DoesNotContain(
                tombstoneColumns,
                column => column.Contains(
                    "external",
                    StringComparison.OrdinalIgnoreCase) ||
                    column.Contains(
                        "source_reference",
                        StringComparison.OrdinalIgnoreCase) ||
                    column.Contains(
                        "source_system",
                        StringComparison.OrdinalIgnoreCase));
            string protectedLedgerText = await dbContext.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT concat_ws(
                        '|',
                        "ScopeId",
                        "OwnerReceiptSha256",
                        "LedgerEntrySha256") AS "Value"
                    FROM ingestion.anonymisation_tombstones
                    WHERE "Id" = {0}
                    """,
                    sourceLink.Id)
                .SingleAsync();
            Assert.DoesNotContain(
                "provider-restore-42",
                protectedLedgerText,
                StringComparison.Ordinal);

            IngestionAnonymisationFingerprint persistedFingerprint =
                await dbContext.AnonymisationFingerprints
                    .AsNoTracking()
                    .FirstAsync();
            await using IngestionDbContext duplicateContext =
                CreateDbContext(postgreSql.GetConnectionString(), TenantId);
            duplicateContext.AnonymisationFingerprints.Add(
                IngestionAnonymisationFingerprint.Create(
                    Guid.NewGuid(),
                    TenantId,
                    persistedFingerprint.TombstoneId,
                    persistedFingerprint.Purpose,
                    persistedFingerprint.KeyVersion,
                    persistedFingerprint.Sha256,
                    Now.AddMinutes(1))
                .Value);
            await Assert.ThrowsAsync<DbUpdateException>(
                () => duplicateContext.SaveChangesAsync());
        }
        finally
        {
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
    }

    private static ServiceProvider CreatePersistenceProvider(
        string connectionString,
        string fileRoot,
        TestApprovalGate? approvalGate = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] = connectionString;
        builder.Configuration["FileManagement:Enabled"] = "true";
        builder.Configuration["FileManagement:Provider"] = "LocalStorage";
        builder.Configuration["FileManagement:MaximumObjectBytes"] = "5242880";
        builder.Configuration["FileManagement:AllowedContentTypes:0"] = "application/json";
        builder.Configuration["FileManagement:LocalStorage:RootPath"] = fileRoot;
        builder.Configuration[
            "Ingestion:AnonymisationFingerprints:ActiveKeyVersion"] = "7";
        builder.Configuration[
            "Ingestion:AnonymisationFingerprints:Keys:7"] =
            Convert.ToBase64String(
                Enumerable.Range(1, 32)
                    .Select(value => (byte)value)
                    .ToArray());
        builder.Services.AddSingleton<IScopeContext>(new TestScopeContext(TenantId));
        builder.Services.AddSingleton<ISystemClock>(new TestClock());
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            NoTerminationFenceReader.Instance);
        if (approvalGate is not null)
        {
            builder.Services.AddSingleton<
                IDataRightsOperationApprovalGate>(approvalGate);
        }

        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddLocalFileStorage();
        builder.Services.AddIngestionApplication();
        builder.AddIngestionPersistence();
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true
            });
    }

    private static DataRightsApprovalEvidence CreateApproval(
        IngestionPropertyProjection property)
    {
        Assert.Equal(
            PropertyProcessingStatus.Enabled,
            property.ProcessingStatus);
        IngestionPropertyPolicyBinding policy =
            Assert.IsType<IngestionPropertyPolicyBinding>(
                property.GovernancePolicy);
        return new(
            SchemaVersion: 1,
            property.Id,
            PropertyVersion: property.PolicySourceVersion,
            policy.OperatingCountryCode,
            policy.PolicyId,
            policy.PolicyVersion,
            policy.RetentionPolicyId,
            policy.RetentionPolicyVersion,
            policy.ContentSha256,
            PurposeCode: "data-rights-anonymisation",
            Surface: "erasure",
            SourceProvenance: "authorized-workspace-operator",
            EvaluatedAtUtc: Now,
            RequiresDistinctExecutor: true);
    }

    private static ReservationSourceLink CreateLinkedSource(
        Guid propertyId,
        Guid reservationId,
        Guid connectionId,
        Guid receiptId,
        string hash,
        bool cancelled = false)
    {
        ReservationSourceLink sourceLink = ReservationSourceLink.Create(
            Guid.NewGuid(),
            TenantId,
            propertyId,
            connectionId,
            "booking-com",
            "provider-42",
            Now).Value;
        Assert.True(sourceLink.Observe(
            receiptId,
            "revision-1",
            1,
            Now,
            hash,
            Now).IsSuccess);
        Guid operationId = Guid.NewGuid();
        Assert.True(sourceLink.BeginDispatch(operationId, Now).IsSuccess);
        Assert.True(sourceLink.CompleteDispatch(
            operationId,
            receiptId,
            "revision-1",
            1,
            cancelled
                ? null
                : "{\"primaryGuestName\":\"Maya Chen\"}",
            reservationId,
            1,
            keepActive: false,
            applied: true,
            cancellationPending: false,
            cancelled,
            Now).IsSuccess);
        return sourceLink;
    }

    private static IngestionAnonymisationTombstone
        CreateReducingTombstone() =>
        IngestionAnonymisationTombstone.BeginRestore(
            TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            selectedSourceLinkVersion: 1,
            resultingSourceLinkVersion: 2,
            ownerReceiptContractVersion: 1,
            Guid.NewGuid(),
            new string('a', 64),
            Now,
            Guid.NewGuid(),
            tenantSequence: 1,
            new string('b', 64),
            graphRecordCount: 1,
            fingerprintCount: 1,
            rawPayloadCount: 0,
            Now.AddMinutes(1))
        .Value;

    private static ObservationReceipt CreateReceipt(
        Guid receiptId,
        Guid propertyId,
        Guid connectionId,
        Guid payloadFileId,
        string hash)
    {
        ObservationCountryPolicyEvidence evidence =
            ObservationCountryPolicyEvidence.Create(
                "GB",
                "policy",
                1,
                "eu",
                "eu-only",
                "retention",
                1,
                new string('a', 64),
                "reservation-import",
                "adapter-ingress",
                "property-policy",
                Now.AddDays(-1),
                Now.AddDays(30),
                Now).Value;
        ObservationReceipt receipt = ObservationReceipt.Create(
            receiptId,
            TenantId,
            propertyId,
            connectionId,
            runId: null,
            Guid.NewGuid(),
            "reservation",
            "provider-42",
            "revision-1",
            "reservation:provider-42:revision-1",
            hash,
            evidence,
            payloadFileId,
            Now.AddDays(30),
            Now,
            Now,
            Now).Value;
        Assert.True(receipt.MarkProcessed(Now.AddMinutes(1)).IsSuccess);
        return receipt;
    }

    private static System.Text.Json.JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(record.Fields, field => field.FieldId == fieldId).Value;

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static IngestionDbContext CreateDbContext(
        string connectionString,
        string tenantId)
    {
        DbContextOptions<IngestionDbContext> options =
            new DbContextOptionsBuilder<IngestionDbContext>()
                .UseNpgsql(
                    connectionString,
                    provider => provider
                        .MigrationsAssembly(
                            IngestionMigrations.PostgreSqlAssembly)
                        .MigrationsHistoryTable(
                            IngestionMigrations.HistoryTable,
                            IngestionMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(tenantId),
            NoTerminationFenceReader.Instance);
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

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class NoTerminationFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public static NoTerminationFenceReader Instance { get; } = new();

        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<WorkspaceTerminationFenceSnapshot?>(null);
        }
    }

    private sealed class TestApprovalGate
        : IDataRightsOperationApprovalGate
    {
        public DataRightsApprovalEvidence? Approval { get; set; }

        public Task<DataRightsOperationApprovalResult> EvaluateAsync(
            DataRightsOperationApprovalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                this.Approval is { } approval
                    ? DataRightsOperationApprovalResult
                        .ApprovedWithEvidence(approval)
                    : DataRightsOperationApprovalResult.Denied(
                        DataRightsOperationApprovalDenial
                            .ApprovalEvidenceMissing));
        }
    }
}

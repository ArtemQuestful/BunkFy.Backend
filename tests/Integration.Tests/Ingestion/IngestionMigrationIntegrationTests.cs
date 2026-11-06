namespace Integration.Tests;

using System.Text.Json;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Credentials;
using BunkFy.Modules.Ingestion.Domain.DataRights;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using BunkFy.Modules.Ingestion.Domain.Runs;
using BunkFy.Modules.Ingestion.Persistence;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class IngestionMigrationIntegrationTests
{
    private const string PreRetentionMigration = "20260712021246_AddOperationsAndPropertyProjection";
    private const string PreviousAnonymisationExecutionMigration =
        "20260726112353_AddIngestionAnonymisationRestore";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Anonymisation_execution_migration_preserves_legacy_proof_and_blocks_unsafe_downgrade()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase(
                    "bunkfy_ingestion_anonymisation_migration_tests")
                .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        Guid sourceLinkId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid ownerReceiptId = Guid.NewGuid();
        Guid ledgerEntryId = Guid.NewGuid();
        DateTimeOffset completedAtUtc =
            new(2026, 7, 26, 18, 0, 0, TimeSpan.Zero);
        await using (IngestionDbContext previous =
            CreateDbContext(connectionString))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(
                    PreviousAnonymisationExecutionMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO ingestion.anonymisation_tombstones (
                    "Id", "ContractVersion", "Revision", "State",
                    "PropertyId", "ConnectionId",
                    "SelectedSourceLinkVersion",
                    "ResultingSourceLinkVersion",
                    "OwnerReceiptContractVersion", "OwnerReceiptId",
                    "OwnerReceiptSha256", "OriginallyCompletedAtUtc",
                    "LedgerEntryId", "TenantSequence",
                    "LedgerEntrySha256", "GraphRecordCount",
                    "FingerprintCount", "RawPayloadCount",
                    "ReplayStartedAtUtc", "LastReplayedAtUtc",
                    "ScopeId")
                VALUES (
                    {sourceLinkId}, {1}, {2L}, {2},
                    {propertyId}, {connectionId}, {4L}, {5L},
                    {1}, {ownerReceiptId}, {new string('a', 64)},
                    {completedAtUtc}, {ledgerEntryId}, {12L},
                    {new string('b', 64)}, {3}, {2}, {1},
                    {completedAtUtc.AddMinutes(1)},
                    {completedAtUtc.AddMinutes(2)}, {"tenant-a"});
                """);
        }

        await using (IngestionDbContext upgraded =
            CreateDbContext(connectionString))
        {
            await upgraded.Database.MigrateAsync();
            IngestionAnonymisationTombstone legacy =
                await upgraded.AnonymisationTombstones
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == sourceLinkId);

            Assert.Equal(
                IngestionAnonymisationTombstone
                    .CurrentContractVersion,
                legacy.ContractVersion);
            Assert.Equal(
                IngestionAnonymisationOrigin.ProtectedLedgerReplay,
                legacy.Origin);
            Assert.Equal(ownerReceiptId, legacy.OwnerReceiptId);
            Assert.Equal(new string('a', 64), legacy.OwnerReceiptSha256);
            Assert.Equal(ledgerEntryId, legacy.LedgerEntryId);
            Assert.Equal(new string('b', 64), legacy.LedgerEntrySha256);
            Assert.Equal(Guid.Empty, legacy.WorkItemId);
            Assert.Equal(Guid.Empty, legacy.IdempotencyKey);

            string[] protectedIndexDefinitions =
                await upgraded.Database.SqlQueryRaw<string>(
                    """
                    SELECT indexdef AS "Value"
                    FROM pg_indexes
                    WHERE schemaname = 'ingestion'
                      AND indexname IN (
                        'IX_anonymisation_tombstones_ScopeId_IdempotencyKey',
                        'IX_anonymisation_tombstones_ScopeId_LedgerEntryId')
                    ORDER BY indexname
                    """)
                    .ToArrayAsync();
            Assert.Equal(2, protectedIndexDefinitions.Length);
            Assert.All(
                protectedIndexDefinitions,
                definition =>
                {
                    Assert.Contains(
                        "UNIQUE",
                        definition,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        "00000000-0000-0000-0000-000000000000",
                        definition,
                        StringComparison.Ordinal);
                });

            await upgraded.Database.GetService<IMigrator>()
                .MigrateAsync(
                    PreviousAnonymisationExecutionMigration);
            int downgradedContract =
                await upgraded.Database.SqlQueryRaw<int>(
                    """
                    SELECT "ContractVersion" AS "Value"
                    FROM ingestion.anonymisation_tombstones
                    LIMIT 1
                    """)
                    .SingleAsync();
            Assert.Equal(1, downgradedContract);
        }

        await using (IngestionDbContext live =
            CreateDbContext(connectionString))
        {
            await live.Database.MigrateAsync();
            Guid liveSourceLinkId = Guid.NewGuid();
            Guid workItemId = Guid.NewGuid();
            Guid idempotencyKey = Guid.NewGuid();
            Guid caseId = Guid.NewGuid();
            Guid liveOwnerReceiptId = Guid.NewGuid();
            IngestionAnonymisationTombstone tombstone =
                IngestionAnonymisationTombstone.BeginExecution(
                    "tenant-a",
                    liveSourceLinkId,
                    propertyId,
                    connectionId,
                    selectedSourceLinkVersion: 1,
                    workItemId,
                    idempotencyKey,
                    caseId,
                    approvalRevision: 2,
                    operationRevision: 3,
                    liveOwnerReceiptId,
                    new string('c', 64),
                    new string('d', 64),
                    new string('e', 64),
                    "user:migration-test",
                    graphRecordCount: 1,
                    fingerprintCount: 1,
                    rawPayloadCount: 0,
                    completedAtUtc.AddHours(1))
                .Value;
            IngestionAnonymisationReceipt receipt =
                IngestionAnonymisationReceipt.Create(
                    liveOwnerReceiptId,
                    "tenant-a",
                    workItemId,
                    idempotencyKey,
                    propertyId,
                    caseId,
                    approvalRevision: 2,
                    operationRevision: 3,
                    liveSourceLinkId,
                    selectedSourceLinkVersion: 1,
                    resultingSourceLinkVersion: 2,
                    graphRecordCount: 1,
                    fingerprintCount: 1,
                    rawPayloadCount: 0,
                    new string('c', 64),
                    new string('d', 64),
                    new string('e', 64),
                    "user:migration-test",
                    completedAtUtc.AddHours(1).AddMinutes(1))
                .Value;
            Assert.True(tombstone.CompleteExecution(receipt).IsSuccess);
            live.AnonymisationTombstones.Add(tombstone);
            await live.SaveChangesAsync();
            live.AnonymisationReceipts.Add(receipt);
            await live.SaveChangesAsync();

            PostgresException unsafeDowngrade =
                await Assert.ThrowsAsync<PostgresException>(
                    () => live.Database.GetService<IMigrator>()
                        .MigrateAsync(
                            PreviousAnonymisationExecutionMigration));
            Assert.Equal(
                PostgresErrorCodes.RaiseException,
                unsafeDowngrade.SqlState);
            Assert.Contains(
                "Cannot downgrade ingestion anonymisation execution",
                unsafeDowngrade.MessageText,
                StringComparison.Ordinal);
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Latest_migrations_preserve_existing_receipts_and_leave_connections_unscheduled()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_ingestion_retention_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid propertyId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
        Guid connectionId = Guid.Parse("a2000000-0000-0000-0000-000000000001");
        Guid runId = Guid.Parse("a2500000-0000-0000-0000-000000000001");
        Guid failedRunId = Guid.Parse("a2500000-0000-0000-0000-000000000002");
        Guid taskRunId = Guid.Parse("a2600000-0000-0000-0000-000000000001");
        Guid failedTaskRunId = Guid.Parse("a2600000-0000-0000-0000-000000000002");
        Guid receiptId = Guid.Parse("a3000000-0000-0000-0000-000000000001");
        Guid secondReceiptId = Guid.Parse("a3000000-0000-0000-0000-000000000002");
        Guid validLinkId = Guid.Parse("a5000000-0000-0000-0000-000000000001");
        Guid cancelledLinkId = Guid.Parse("a5000000-0000-0000-0000-000000000002");
        Guid validLinkOperationId = Guid.Parse("a5500000-0000-0000-0000-000000000001");
        Guid cancelledLinkOperationId = Guid.Parse("a5500000-0000-0000-0000-000000000002");
        Guid activeProposalId = Guid.Parse("a6000000-0000-0000-0000-000000000001");
        Guid terminalProposalId = Guid.Parse("a6000000-0000-0000-0000-000000000002");
        Guid activeDispatchId = Guid.Parse("a7000000-0000-0000-0000-000000000001");
        Guid terminalDispatchId = Guid.Parse("a7000000-0000-0000-0000-000000000002");
        DateTimeOffset receivedAtUtc = new(2026, 7, 12, 1, 0, 0, TimeSpan.Zero);
        DateTimeOffset terminalAtUtc = receivedAtUtc.AddDays(1);
        const string validSnapshot = "{\"Kind\":1,\"SourceSequence\":1,\"Arrival\":\"2026-08-01\",\"Departure\":\"2026-08-03\",\"InventoryUnitIds\":[\"20000000-0000-0000-0000-000000000002\",\"10000000-0000-0000-0000-000000000001\"],\"PrimaryGuestName\":\"Ada Sensitive\",\"Email\":\"ada@example.test\",\"Phone\":\"+1-555\",\"GuestCount\":1,\"Notes\":\"private\"}";
        const string cancelledSnapshot = "{\"Kind\":2,\"SourceSequence\":2,\"Arrival\":null,\"Departure\":null,\"InventoryUnitIds\":[],\"PrimaryGuestName\":null,\"Email\":null,\"Phone\":null,\"GuestCount\":null,\"Notes\":null}";

        await using (IngestionDbContext initial = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await initial.Database.GetService<IMigrator>().MigrateAsync(PreRetentionMigration);
            await initial.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ingestion.property_projection (
                    "Id", "ScopeId", "Name", "Code", "IsActive", "IsKnown", "SourceVersion")
                VALUES (
                    {propertyId}, {"tenant-a"}, {"Migration property"}, {"migration-property"},
                    {true}, {true}, {1L});

                INSERT INTO ingestion.adapter_connections (
                    "Id", "PropertyId", "AdapterType", "ExecutionMode", "ConflictPolicy",
                    "ConfigurationReference", "State", "Version", "CreatedAtUtc", "ScopeId")
                VALUES (
                    {connectionId}, {propertyId}, {"fake.http"}, {1}, {1},
                    {"configuration://migration"}, {1}, {1L}, {receivedAtUtc}, {"tenant-a"});

                INSERT INTO ingestion.runs (
                    "Id", "ConnectionId", "PropertyId", "TaskRunId", "TaskAttempt", "State",
                    "ObservedCount", "AcceptedCount", "RejectedCount", "ErrorMessage", "Version",
                    "StartedAtUtc", "CompletedAtUtc", "ScopeId")
                VALUES
                    ({runId}, {connectionId}, {propertyId}, {taskRunId}, {1}, {2},
                     {1}, {1}, {0}, {null}, {2L}, {receivedAtUtc}, {receivedAtUtc.AddMinutes(1)}, {"tenant-a"}),
                    ({failedRunId}, {connectionId}, {propertyId}, {failedTaskRunId}, {1}, {4},
                     {0}, {0}, {0}, {"Provider said guest Ada failed at /private/path"}, {2L},
                     {receivedAtUtc}, {receivedAtUtc.AddMinutes(2)}, {"tenant-a"});

                INSERT INTO ingestion.observation_receipts (
                    "Id", "PropertyId", "ConnectionId", "OperationId", "SourceRecordType",
                    "ExternalId", "SourceRevision", "DeduplicationKey", "ContentHash",
                    "RawPayloadFileId", "ObservedAtUtc", "State", "ReceivedAtUtc", "ProcessedAtUtc", "ScopeId")
                VALUES
                (
                    {receiptId}, {propertyId}, {connectionId}, {Guid.NewGuid()}, {"reservation.v1"},
                    {"booking-migration-1"}, {"1"}, {"reservation.v1|booking-migration-1|1"},
                    {new string('a', 64)}, {receiptId}, {receivedAtUtc}, {2}, {receivedAtUtc},
                    {receivedAtUtc.AddMinutes(1)}, {"tenant-a"}),
                (
                    {secondReceiptId}, {propertyId}, {connectionId}, {Guid.NewGuid()}, {"reservation.v1"},
                    {"booking-migration-2"}, {"1"}, {"reservation.v1|booking-migration-2|1"},
                    {new string('e', 64)}, {secondReceiptId}, {receivedAtUtc}, {2}, {receivedAtUtc},
                    {receivedAtUtc.AddMinutes(1)}, {"tenant-a"});

                INSERT INTO ingestion.reservation_source_links (
                    "Id", "PropertyId", "ConnectionId", "SourceSystem", "SourceReference", "ReservationId",
                    "State", "LastObservedReceiptId", "LastObservedContentHash", "LastAppliedReceiptId",
                    "LastAppliedSourceRevision", "LastAppliedSourceSequence", "LastAppliedReservationDetailsRevision",
                    "LastAppliedNormalizedSnapshot", "LastProductOperationId", "Version", "CreatedAtUtc",
                    "UpdatedAtUtc", "ScopeId")
                VALUES
                    ({validLinkId}, {propertyId}, {connectionId}, {"fake.http:migration"}, {"valid"}, {Guid.NewGuid()},
                     {2}, {receiptId}, {new string('b', 64)}, {receiptId}, {"1"}, {1L}, {1L},
                     {validSnapshot}, {validLinkOperationId},
                     {2L}, {receivedAtUtc}, {receivedAtUtc.AddMinutes(1)}, {"tenant-a"}),
                    ({cancelledLinkId}, {propertyId}, {connectionId}, {"fake.http:migration"}, {"cancelled"}, {Guid.NewGuid()},
                     {4}, {receiptId}, {new string('c', 64)}, {receiptId}, {"2"}, {2L}, {2L},
                     {cancelledSnapshot}, {cancelledLinkOperationId},
                     {2L}, {receivedAtUtc}, {receivedAtUtc.AddMinutes(1)}, {"tenant-a"});

                INSERT INTO ingestion.change_proposals (
                    "Id", "PropertyId", "ConnectionId", "ReceiptId", "ReservationId", "SourcePayloadFileId",
                    "BaseReservationDetailsRevision", "Diff", "State", "DecisionActor", "DecisionReason",
                    "ProductOperationId", "Version", "CreatedAtUtc", "DecidedAtUtc", "CompletedAtUtc", "ScopeId")
                VALUES
                    ({activeProposalId}, {propertyId}, {connectionId}, {receiptId}, {Guid.NewGuid()}, {receiptId},
                     {1L}, {/*lang=json,strict*/ "{\"Reason\":\"legacy-active\",\"IncomingSnapshot\":{}}"}, {1}, {null}, {null},
                     {null}, {1L}, {receivedAtUtc}, {null}, {null}, {"tenant-a"}),
                    ({terminalProposalId}, {propertyId}, {connectionId}, {secondReceiptId}, {Guid.NewGuid()}, {secondReceiptId},
                     {1L}, {/*lang=json,strict*/ "{\"Reason\":\"legacy-terminal\",\"IncomingSnapshot\":{}}"}, {4}, {"staff:1"}, {"outdated"},
                     {null}, {2L}, {receivedAtUtc}, {terminalAtUtc}, {terminalAtUtc}, {"tenant-a"});

                INSERT INTO ingestion.reservation_dispatches (
                    "Id", "SourceLinkId", "TriggerKind", "TriggerId", "ReceiptId", "ConnectionId", "PropertyId",
                    "ReservationId", "Kind", "SourceRevision", "SourceSequence", "NormalizedSnapshot",
                    "ExpectedDetailsRevision", "State", "ResultDetailsRevision", "ResultReservationVersion",
                    "ErrorCode", "Version", "CreatedAtUtc", "CompletedAtUtc", "ScopeId")
                VALUES
                    ({activeDispatchId}, {validLinkId}, {1}, {Guid.NewGuid()}, {receiptId}, {connectionId}, {propertyId},
                     {null}, {1}, {"1"}, {1L}, {/*lang=json,strict*/ "{\"guest\":\"active\"}"}, {null}, {1}, {null}, {null},
                     {null}, {1L}, {receivedAtUtc}, {null}, {"tenant-a"}),
                    ({terminalDispatchId}, {validLinkId}, {1}, {Guid.NewGuid()}, {secondReceiptId}, {connectionId}, {propertyId},
                     {Guid.NewGuid()}, {1}, {"2"}, {2L}, {/*lang=json,strict*/ "{\"guest\":\"terminal\"}"}, {null}, {3}, {1L}, {1L},
                     {null}, {2L}, {receivedAtUtc}, {terminalAtUtc}, {"tenant-a"});
                """);
        }

        await using IngestionDbContext upgraded = CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();
        int dataRightsLookupIndexCount = await upgraded.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*)::integer AS "Value"
            FROM pg_indexes
            WHERE schemaname = 'ingestion'
              AND indexname = 'IX_observation_receipts_ScopeId_ConnectionId_ExternalId_Receiv~'
            """).SingleAsync();
        ObservationReceipt receipt = await upgraded.ObservationReceipts.SingleAsync(item => item.Id == receiptId);
        AdapterConnection connection = await upgraded.AdapterConnections.SingleAsync(item => item.Id == connectionId);
        IngestionRun run = await upgraded.Runs.SingleAsync(item => item.Id == runId);
        IngestionRun failedRun = await upgraded.Runs.SingleAsync(item => item.Id == failedRunId);
        ReservationSourceLink validLink = await upgraded.ReservationSourceLinks.SingleAsync(item => item.Id == validLinkId);
        ReservationSourceLink cancelledLink = await upgraded.ReservationSourceLinks.SingleAsync(item => item.Id == cancelledLinkId);
        ChangeProposal activeProposal = await upgraded.ChangeProposals.SingleAsync(item => item.Id == activeProposalId);
        ChangeProposal terminalProposal = await upgraded.ChangeProposals.SingleAsync(item => item.Id == terminalProposalId);
        ReservationDispatch activeDispatch = await upgraded.ReservationDispatches.SingleAsync(item => item.Id == activeDispatchId);
        ReservationDispatch terminalDispatch = await upgraded.ReservationDispatches.SingleAsync(item => item.Id == terminalDispatchId);
        IngestionPropertyProjection propertyProjection = await upgraded.PropertyProjections.SingleAsync(
            item => item.Id == propertyId);

        Assert.Equal(RawPayloadRetentionState.Available, receipt.RawPayloadRetentionState);
        Assert.Equal(1, receipt.RawPayloadVersion);
        Assert.Equal(receivedAtUtc.AddDays(30), receipt.RawPayloadRetainUntilUtc);
        Assert.Null(receipt.RawPayloadPurgeClaimId);
        Assert.Null(receipt.RawPayloadPurgedAtUtc);
        Assert.Null(receipt.AnonymisedAtUtc);
        Assert.Null(validLink.AnonymisedAtUtc);
        Assert.Empty(await upgraded.AnonymisationTombstones.ToArrayAsync());
        Assert.Empty(await upgraded.AnonymisationFingerprints.ToArrayAsync());
        Assert.Empty(await upgraded.AnonymisationRecordPlan.ToArrayAsync());
        bool outputVersionDefaultsToOne = await upgraded.Database
            .SqlQueryRaw<bool>(
                """
                SELECT column_default LIKE '1%' AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'ingestion'
                  AND table_name = 'observation_reprocessing_outputs'
                  AND column_name = 'Version'
                """)
            .SingleAsync();
        Assert.True(outputVersionDefaultsToOne);
        Assert.Null(connection.PollingIntervalSeconds);
        Assert.Null(connection.PollingScheduleMaxAttempts);
        Assert.Null(connection.PollingScheduleConfiguredAtUtc);
        Assert.Equal(IngestionRunExecutionKind.TaskRuntime, run.ExecutionKind);
        Assert.Equal(taskRunId, run.TaskRunId);
        Assert.Equal(1, run.TaskAttempt);
        Assert.Null(run.RemoteLeaseId);
        Assert.Null(run.RemoteLeaseEpoch);
        Assert.Equal("ingestion.legacy-adapter-failure", failedRun.ErrorCode);
        Assert.Empty(await upgraded.AdapterIngressCredentials.ToArrayAsync());
        Assert.NotNull(validLink.LastAppliedOperationalBaseline);
        using (JsonDocument baseline = JsonDocument.Parse(validLink.LastAppliedOperationalBaseline))
        {
            Assert.Equal(1, baseline.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("2026-08-01", baseline.RootElement.GetProperty("arrival").GetString());
            Assert.Equal("2026-08-03", baseline.RootElement.GetProperty("departure").GetString());
            Assert.Equal(
                [
                    "10000000-0000-0000-0000-000000000001",
                    "20000000-0000-0000-0000-000000000002"
                ],
                baseline.RootElement.GetProperty("inventoryUnitIds").EnumerateArray()
                    .Select(item => item.GetString()!).ToArray());
        }
        Assert.DoesNotContain("Ada Sensitive", validLink.LastAppliedOperationalBaseline, StringComparison.Ordinal);
        Assert.DoesNotContain("ada@example.test", validLink.LastAppliedOperationalBaseline, StringComparison.Ordinal);
        Assert.Null(cancelledLink.LastAppliedOperationalBaseline);
        Assert.Equal("legacy-active", activeProposal.ReasonCode);
        Assert.Null(activeProposal.SensitiveDataRetainUntilUtc);
        Assert.NotNull(activeProposal.Diff);
        Assert.Equal("legacy-terminal", terminalProposal.ReasonCode);
        Assert.Equal(terminalAtUtc.AddDays(90), terminalProposal.SensitiveDataRetainUntilUtc);
        Assert.NotNull(terminalProposal.Diff);
        Assert.Null(activeDispatch.SensitiveDataRetainUntilUtc);
        Assert.NotNull(activeDispatch.NormalizedSnapshot);
        Assert.Equal(terminalAtUtc.AddDays(90), terminalDispatch.SensitiveDataRetainUntilUtc);
        Assert.NotNull(terminalDispatch.NormalizedSnapshot);
        Assert.Equal(0, propertyProjection.RetentionFenceVersion);
        Assert.Empty(await upgraded.LegalHolds.ToArrayAsync());
        Assert.Equal(1, dataRightsLookupIndexCount);

        PostgresException invalidActiveDeadline = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingestion.change_proposals
                SET "SensitiveDataRetainUntilUtc" = {terminalAtUtc.AddDays(90)}
                WHERE "Id" = {activeProposalId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidActiveDeadline.SqlState);

        PostgresException invalidRedactionShape = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingestion.reservation_dispatches
                SET "NormalizedSnapshot" = NULL
                WHERE "Id" = {terminalDispatchId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidRedactionShape.SqlState);

        Guid credentialId = Guid.Parse("a4000000-0000-0000-0000-000000000001");
        await upgraded.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ingestion.adapter_ingress_credentials (
                "Id", "ConnectionId", "Slot", "Label", "SecretHashAlgorithm", "SecretHash", "State",
                "ExpiresAtUtc", "CreatedBy", "CreatedAtUtc", "Version", "ScopeId",
                "AdapterType", "AdapterProtocolVersion", "ConfigurationSchemaVersion", "SourceSystem")
            VALUES (
                {credentialId}, {connectionId}, {1}, {"valid credential"},
                {AdapterIngressCredential.Sha256HashAlgorithm},
                {new byte[AdapterIngressCredential.SecretHashLength]}, {1},
                {receivedAtUtc.AddDays(30)}, {"migration-test"}, {receivedAtUtc}, {1L}, {"tenant-a"},
                {"fake.http"}, {1}, {1}, {"fake.http"});
            """);

        PostgresException duplicateActiveSlot = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ingestion.adapter_ingress_credentials (
                    "Id", "ConnectionId", "Slot", "Label", "SecretHashAlgorithm", "SecretHash", "State",
                    "ExpiresAtUtc", "CreatedBy", "CreatedAtUtc", "Version", "ScopeId",
                    "AdapterType", "AdapterProtocolVersion", "ConfigurationSchemaVersion", "SourceSystem")
                VALUES (
                    {Guid.NewGuid()}, {connectionId}, {1}, {"duplicate slot"},
                    {AdapterIngressCredential.Sha256HashAlgorithm},
                    {new byte[AdapterIngressCredential.SecretHashLength]}, {1},
                    {receivedAtUtc.AddDays(30)}, {"migration-test"}, {receivedAtUtc}, {1L}, {"tenant-a"},
                    {"fake.http"}, {1}, {1}, {"fake.http"});
                """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateActiveSlot.SqlState);

        PostgresException invalidCredential = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ingestion.adapter_ingress_credentials (
                    "Id", "ConnectionId", "Slot", "Label", "SecretHashAlgorithm", "SecretHash", "State",
                    "ExpiresAtUtc", "CreatedBy", "CreatedAtUtc", "Version", "ScopeId",
                    "AdapterType", "AdapterProtocolVersion", "ConfigurationSchemaVersion", "SourceSystem")
                VALUES (
                    {Guid.NewGuid()}, {connectionId}, {2}, {"invalid digest"},
                    {AdapterIngressCredential.Sha256HashAlgorithm}, {new byte[31]}, {1},
                    {receivedAtUtc.AddDays(30)}, {"migration-test"}, {receivedAtUtc}, {1L}, {"tenant-a"},
                    {"fake.http"}, {1}, {1}, {"fake.http"});
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidCredential.SqlState);

        PostgresException invalidSchedule = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingestion.adapter_connections
                SET "PollingIntervalSeconds" = {59},
                    "PollingScheduleMaxAttempts" = {3},
                    "PollingScheduleConfiguredAtUtc" = {receivedAtUtc}
                WHERE "Id" = {connectionId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidSchedule.SqlState);

        Guid legalHoldId = Guid.Parse("a8000000-0000-0000-0000-000000000001");
        await upgraded.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ingestion.legal_holds (
                "Id", "PropertyId", "Reason", "State", "PlacedBy", "PlacedAtUtc", "Version", "ScopeId")
            VALUES (
                {legalHoldId}, {propertyId}, {"Migration legal matter"}, {1},
                {"migration-test"}, {receivedAtUtc}, {1L}, {"tenant-a"});
            """);

        PostgresException invalidLegalHoldLifecycle = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingestion.legal_holds
                SET "State" = {2}
                WHERE "Id" = {legalHoldId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidLegalHoldLifecycle.SqlState);

        PostgresException invalidLegalHoldVersion = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingestion.legal_holds
                SET "Version" = {0L}
                WHERE "Id" = {legalHoldId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidLegalHoldVersion.SqlState);

        PostgresException invalidRetentionFence = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingestion.property_projection
                SET "RetentionFenceVersion" = {-1L}
                WHERE "Id" = {propertyId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidRetentionFence.SqlState);

        PostgresException successfulRunWithError = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingestion.runs
                SET "ErrorCode" = {"provider.failure"}
                WHERE "Id" = {runId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, successfulRunWithError.SqlState);

        PostgresException freeFormRunError = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingestion.runs
                SET "ErrorCode" = {"Provider failed with guest Ada"}
                WHERE "Id" = {failedRunId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, freeFormRunError.SqlState);

        PostgresException failedRunWithoutError = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingestion.runs
                SET "ErrorCode" = NULL
                WHERE "Id" = {failedRunId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failedRunWithoutError.SqlState);
    }

    private static IngestionDbContext CreateDbContext(string connectionString)
    {
        DbContextOptions<IngestionDbContext> options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(connectionString, provider => provider
                .MigrationsAssembly(IngestionMigrations.PostgreSqlAssembly)
                .MigrationsHistoryTable(IngestionMigrations.HistoryTable, IngestionMigrations.Schema))
            .Options;
        return new(
            options,
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}

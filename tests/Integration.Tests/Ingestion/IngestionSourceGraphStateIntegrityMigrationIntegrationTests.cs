namespace Integration.Tests;

using BunkFy.Adapter.Abstractions;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reprocessing;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class IngestionSourceGraphStateIntegrityMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260809070250_AddIngestionCredentialMutationOperations";
    private const string ScopeId = "tenant-a";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Source_graph_migration_preserves_valid_state_and_rejects_malformed_writes()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_ingestion_source_graph_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid propertyId = Guid.Parse(
            "31000000-0000-0000-0000-000000000050");
        Guid connectionId = Guid.Parse(
            "51000000-0000-0000-0000-000000000050");
        Guid sourceLinkId = Guid.Parse(
            "52000000-0000-0000-0000-000000000050");
        Guid dispatchId = Guid.Parse(
            "53000000-0000-0000-0000-000000000050");
        Guid proposalId = Guid.Parse(
            "54000000-0000-0000-0000-000000000050");
        Guid reprocessingAttemptId = Guid.Parse(
            "55000000-0000-0000-0000-000000000050");
        Guid reservationId = Guid.Parse(
            "61000000-0000-0000-0000-000000000050");
        DateTimeOffset createdAtUtc = new(
            2026,
            8,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);
        DateTimeOffset completedAtUtc = createdAtUtc.AddHours(3);
        DateTimeOffset retainUntilUtc = createdAtUtc.AddDays(90);

        await using (IngestionDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                PreviousMigration);

            AdapterConnection connection = AdapterConnection.Create(
                connectionId,
                ScopeId,
                propertyId,
                "fake.http",
                AdapterExecutionMode.Push,
                IngestionConflictPolicy.SuggestionsOnly,
                "configuration://source-graph-integrity",
                secretReference: null,
                createdAtUtc).Value;
            ObservationReceipt dispatchReceipt = CreateReceipt(
                propertyId,
                connectionId,
                "dispatch",
                createdAtUtc);
            ObservationReceipt proposalReceipt = CreateReceipt(
                propertyId,
                connectionId,
                "proposal",
                createdAtUtc.AddMinutes(1));
            ObservationReceipt reprocessingReceipt = CreateReceipt(
                propertyId,
                connectionId,
                "reprocessing",
                createdAtUtc.AddMinutes(2));
            Assert.True(dispatchReceipt.MarkProcessed(
                completedAtUtc).IsSuccess);
            Assert.True(proposalReceipt.MarkProcessed(
                completedAtUtc).IsSuccess);
            Assert.True(reprocessingReceipt.Reject(
                "ingestion.parser-version-unsupported",
                completedAtUtc).IsSuccess);

            ReservationSourceLink sourceLink = ReservationSourceLink.Create(
                sourceLinkId,
                ScopeId,
                propertyId,
                connectionId,
                "fake.http",
                "booking-source-graph-50",
                createdAtUtc).Value;
            Assert.True(sourceLink.Observe(
                dispatchReceipt.Id,
                "revision-1",
                1,
                createdAtUtc,
                dispatchReceipt.ContentHash,
                createdAtUtc.AddHours(1)).IsSuccess);
            Assert.True(sourceLink.BeginDispatch(
                dispatchId,
                createdAtUtc.AddHours(2)).IsSuccess);
            Assert.True(sourceLink.CompleteDispatch(
                dispatchId,
                dispatchReceipt.Id,
                "revision-1",
                1,
                operationalBaseline: null,
                reservationId,
                detailsRevision: 2,
                keepActive: false,
                applied: true,
                cancellationPending: false,
                cancelled: true,
                completedAtUtc).IsSuccess);

            ReservationDispatch dispatch = ReservationDispatch.Create(
                dispatchId,
                ScopeId,
                sourceLinkId,
                ReservationDispatchTriggerKind.Observation,
                dispatchReceipt.Id,
                dispatchReceipt.Id,
                connectionId,
                propertyId,
                reservationId,
                ReservationDispatchKind.Cancel,
                "revision-1",
                1,
                "{\"operation\":\"cancel\"}",
                expectedDetailsRevision: 1,
                createdAtUtc.AddHours(2)).Value;
            Assert.True(dispatch.Complete(
                ReservationDispatchState.Applied,
                reservationId,
                detailsRevision: 2,
                reservationVersion: 3,
                errorCode: null,
                retainUntilUtc,
                completedAtUtc).IsSuccess);

            ChangeProposal proposal = ChangeProposal.Create(
                proposalId,
                ScopeId,
                propertyId,
                connectionId,
                proposalReceipt.Id,
                reservationId,
                proposalReceipt.RawPayloadFileId,
                baseReservationDetailsRevision: 1,
                "staff-conflict",
                "{\"guest\":\"Updated guest\"}",
                createdAtUtc.AddHours(1)).Value;
            Assert.True(proposal.Reject(
                "staff:owner",
                "Already corrected locally",
                expectedVersion: 1,
                retainUntilUtc,
                completedAtUtc).IsSuccess);

            ObservationReprocessingAttempt attempt =
                ObservationReprocessingAttempt.Create(
                    reprocessingAttemptId,
                    ScopeId,
                    propertyId,
                    connectionId,
                    reprocessingReceipt.Id,
                    reprocessingAttemptId,
                    "reservation-json",
                    parserVersion: 2,
                    "staff:owner",
                    createdAtUtc,
                    createdAtUtc.AddDays(1)).Value;
            Assert.True(attempt.Start(
                reprocessingAttemptId,
                taskAttempt: 1,
                createdAtUtc.AddHours(1),
                createdAtUtc.AddDays(2)).IsSuccess);
            Assert.True(attempt.Complete(
                parsedCount: 2,
                acceptedCount: 1,
                duplicateCount: 1,
                rejectedCount: 0,
                noMatch: false,
                reasonCode: null,
                completedAtUtc).IsSuccess);

            connection.ClearDomainEvents();
            dispatchReceipt.ClearDomainEvents();
            proposalReceipt.ClearDomainEvents();
            reprocessingReceipt.ClearDomainEvents();
            sourceLink.ClearDomainEvents();
            dispatch.ClearDomainEvents();
            proposal.ClearDomainEvents();
            attempt.ClearDomainEvents();
            previous.AddRange(
                connection,
                dispatchReceipt,
                proposalReceipt,
                reprocessingReceipt,
                sourceLink,
                dispatch,
                proposal,
                attempt);
            await previous.SaveChangesAsync();
        }

        await using IngestionDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        ReservationSourceLink retainedLink = await upgraded.ReservationSourceLinks
            .SingleAsync(item => item.Id == sourceLinkId);
        ReservationDispatch retainedDispatch = await upgraded.ReservationDispatches
            .SingleAsync(item => item.Id == dispatchId);
        ChangeProposal retainedProposal = await upgraded.ChangeProposals
            .SingleAsync(item => item.Id == proposalId);
        ObservationReprocessingAttempt retainedAttempt =
            await upgraded.ObservationReprocessingAttempts
                .SingleAsync(item => item.Id == reprocessingAttemptId);
        Assert.Equal(ReservationSourceLinkState.Cancelled, retainedLink.State);
        Assert.Equal(ReservationDispatchState.Applied, retainedDispatch.State);
        Assert.Equal(ChangeProposalState.Rejected, retainedProposal.State);
        Assert.Equal(ObservationReprocessingState.Succeeded, retainedAttempt.State);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_source_links_coordinates",
            $"""
            UPDATE ingestion.reservation_source_links
            SET "SourceSystem" = {" "}
            WHERE "Id" = {sourceLinkId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_source_links_observation_shape",
            $"""
            UPDATE ingestion.reservation_source_links
            SET "LastObservedSourceSequence" = {-1L}
            WHERE "Id" = {sourceLinkId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_source_links_applied_shape",
            $"""
            UPDATE ingestion.reservation_source_links
            SET "LastProductOperationId" = NULL
            WHERE "Id" = {sourceLinkId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_source_links_lifecycle",
            $"""
            UPDATE ingestion.reservation_source_links
            SET "Version" = {1L}
            WHERE "Id" = {sourceLinkId};
            """);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_dispatches_coordinates",
            $"""
            UPDATE ingestion.reservation_dispatches
            SET "SourceSequence" = {-1L}
            WHERE "Id" = {dispatchId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_dispatches_kind_shape",
            $"""
            UPDATE ingestion.reservation_dispatches
            SET "ExpectedDetailsRevision" = NULL
            WHERE "Id" = {dispatchId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_dispatches_lifecycle",
            $"""
            UPDATE ingestion.reservation_dispatches
            SET "Version" = {1L}
            WHERE "Id" = {dispatchId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_dispatches_sensitive_history_lifecycle",
            $"""
            UPDATE ingestion.reservation_dispatches
            SET "NormalizedSnapshot" = NULL
            WHERE "Id" = {dispatchId};
            """);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_change_proposals_coordinates",
            $"""
            UPDATE ingestion.change_proposals
            SET "BaseReservationDetailsRevision" = {0L}
            WHERE "Id" = {proposalId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_change_proposals_reason_code",
            $"""
            UPDATE ingestion.change_proposals
            SET "ReasonCode" = {" "}
            WHERE "Id" = {proposalId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_change_proposals_lifecycle",
            $"""
            UPDATE ingestion.change_proposals
            SET "ProductOperationId" = {Guid.NewGuid()}
            WHERE "Id" = {proposalId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_change_proposals_sensitive_history_lifecycle",
            $"""
            UPDATE ingestion.change_proposals
            SET "Diff" = NULL
            WHERE "Id" = {proposalId};
            """);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_observation_reprocessing_attempts_coordinates",
            $"""
            UPDATE ingestion.observation_reprocessing_attempts
            SET "ParserVersion" = {0}
            WHERE "Id" = {reprocessingAttemptId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_observation_reprocessing_attempts_counters",
            $"""
            UPDATE ingestion.observation_reprocessing_attempts
            SET "ParsedCount" = {3}
            WHERE "Id" = {reprocessingAttemptId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_observation_reprocessing_attempts_lifecycle",
            $"""
            UPDATE ingestion.observation_reprocessing_attempts
            SET "State" = {(int)ObservationReprocessingState.Running}
            WHERE "Id" = {reprocessingAttemptId};
            """);

        DateTimeOffset anonymisedAtUtc = completedAtUtc.AddHours(1);
        Assert.True(retainedLink.Anonymise(
            retainedLink.Version,
            anonymisedAtUtc).IsSuccess);
        Assert.True(retainedDispatch.Anonymise(anonymisedAtUtc).IsSuccess);
        Assert.True(retainedProposal.Anonymise(anonymisedAtUtc).IsSuccess);
        await upgraded.SaveChangesAsync();
        Assert.True(anonymisedAtUtc < retainUntilUtc);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_source_links_anonymised_shape",
            $"""
            UPDATE ingestion.reservation_source_links
            SET "SourceReference" = {"provider-reference-leak"}
            WHERE "Id" = {sourceLinkId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_reservation_dispatches_anonymised_shape",
            $"""
            UPDATE ingestion.reservation_dispatches
            SET "SourceRevision" = {"provider-revision-leak"}
            WHERE "Id" = {dispatchId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_change_proposals_anonymised_shape",
            $"""
            UPDATE ingestion.change_proposals
            SET "DecisionReason" = {"guest-specific reason leak"}
            WHERE "Id" = {proposalId};
            """);

        PostgresException unsafeDowngrade =
            await Assert.ThrowsAsync<PostgresException>(
                () => upgraded.Database.GetService<IMigrator>()
                    .MigrateAsync(PreviousMigration));
        Assert.Equal(PostgresErrorCodes.RaiseException, unsafeDowngrade.SqlState);
        Assert.Contains(
            "Cannot downgrade ingestion source-graph integrity after early anonymisation",
            unsafeDowngrade.MessageText,
            StringComparison.Ordinal);
    }

    private static ObservationReceipt CreateReceipt(
        Guid propertyId,
        Guid connectionId,
        string suffix,
        DateTimeOffset receivedAtUtc)
    {
        Guid receiptId = Guid.NewGuid();
        ObservationCountryPolicyEvidence evidence =
            ObservationCountryPolicyEvidence.Create(
                "GB",
                "gb-hostel",
                policyVersion: 1,
                "eu-west-2",
                "uk-no-transfer",
                "guest-operational",
                retentionPolicyVersion: 1,
                new string('a', ObservationCountryPolicyEvidence.ContentSha256Length),
                "reservation-ingestion",
                "adapter-ingress",
                "configured-policy",
                receivedAtUtc.AddDays(-1),
                receivedAtUtc.AddDays(30),
                receivedAtUtc).Value;
        return ObservationReceipt.Create(
            receiptId,
            ScopeId,
            propertyId,
            connectionId,
            runId: null,
            Guid.NewGuid(),
            "reservation.v1",
            $"booking-{suffix}",
            "revision-1",
            $"reservation.v1|booking-{suffix}|revision-1",
            new string('b', ObservationReceipt.ContentHashLength),
            evidence,
            receiptId,
            receivedAtUtc.AddDays(30),
            receivedAtUtc,
            receivedAtUtc,
            receivedAtUtc).Value;
    }

    private static async Task AssertConstraintViolationAsync(
        IngestionDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static IngestionDbContext CreateDbContext(string connectionString)
    {
        DbContextOptions<IngestionDbContext> options =
            new DbContextOptionsBuilder<IngestionDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(IngestionMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        IngestionMigrations.HistoryTable,
                        IngestionMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => IngestionSourceGraphStateIntegrityMigrationIntegrationTests.ScopeId;
    }
}

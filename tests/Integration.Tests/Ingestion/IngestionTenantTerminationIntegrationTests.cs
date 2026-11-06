namespace Integration.Tests;

using BunkFy.Adapter.Abstractions;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Controls;
using BunkFy.Modules.Ingestion.Domain.Credentials;
using BunkFy.Modules.Ingestion.Domain.DataRights;
using BunkFy.Modules.Ingestion.Domain.LegalHolds;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reprocessing;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using BunkFy.Modules.Ingestion.Domain.Retention;
using BunkFy.Modules.Ingestion.Domain.Runs;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class IngestionTenantTerminationIntegrationTests
{
    private const string TenantA =
        "10000000-0000-0000-0000-000000000001";
    private const string TenantB =
        "10000000-0000-0000-0000-000000000002";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private const string SecretReference =
        "vault://ingestion/tenant-export-secret";
    private static readonly Guid PropertyId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantBPropertyId =
        Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid CaseId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset SeedNowUtc =
        new(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FrozenAtUtc =
        SeedNowUtc.AddHours(1);
    private static readonly DateTimeOffset ExportNowUtc =
        FrozenAtUtc.AddMinutes(5);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_export_is_complete_isolated_and_freezes_writes()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_ingestion_tenant_export_tests")
                .Build();
        await postgreSql.StartAsync();

        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            postgreSql.GetConnectionString(),
            TenantA);
        SeedProof proof;
        using (IServiceScope seedScope = tenantAProvider.CreateScope())
        {
            IngestionDbContext ingestion = seedScope.ServiceProvider
                .GetRequiredService<IngestionDbContext>();
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await workspaces.Database.MigrateAsync();
            await ingestion.Database.MigrateAsync();
            proof = await SeedGraphAsync(
                seedScope.ServiceProvider,
                ingestion);
        }

        await AssertDirectCredentialMutationIsCoordinatedAsync(
            tenantAProvider,
            proof.CredentialId);

        Guid tenantBConnectionId;
        using (ServiceProvider tenantBProvider = CreatePersistenceProvider(
                   postgreSql.GetConnectionString(),
                   TenantB))
        using (IServiceScope tenantBScope = tenantBProvider.CreateScope())
        {
            IngestionDbContext tenantBContext = tenantBScope.ServiceProvider
                .GetRequiredService<IngestionDbContext>();
            AdapterConnection tenantBConnection = CreateConnection(
                TenantB,
                Guid.NewGuid(),
                Guid.NewGuid(),
                secretReference: null);
            tenantBConnectionId = tenantBConnection.Id;
            tenantBContext.AdapterConnections.Add(tenantBConnection);
            await tenantBContext.SaveChangesAsync();
        }

        using IServiceScope scope = tenantAProvider.CreateScope();
        WorkspacesDbContext workspacesDbContext = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceTerminationFence fence = CreateTerminationFence();
        workspacesDbContext.WorkspaceTerminationFences.Add(fence);
        await workspacesDbContext.SaveChangesAsync();

        ITenantTerminationExportContributor contributor =
            scope.ServiceProvider
                .GetServices<ITenantTerminationExportContributor>()
                .Single(candidate =>
                    candidate.ExportDescriptor.ExportSchemaId ==
                    IngestionTenantTerminationMetadata.ExportSchemaId);
        CollectingSink first = new();
        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                TenantTerminationRequest(fence),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("ingestion.termination.exported", result.ResultCode);
        Assert.Equal(first.Records.Count, result.AffectedCount);
        Assert.Equal(3, result.SelectedProofRevision);
        Assert.Equal(3, result.ResultingProofRevision);
        Assert.Equal(
            IngestionTenantTerminationMetadata.RecordTypes,
            first.Records
                .Select(record => record.RecordType)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        Assert.Contains(
            first.Records,
            record => record.RecordId == proof.ConnectionId);
        Assert.DoesNotContain(
            first.Records,
            record => record.RecordId == tenantBConnectionId);

        string exportedJson = string.Join(
            '\n',
            first.Records.Select(Snapshot));
        Assert.DoesNotContain(TenantB, exportedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SecretReference,
            exportedJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            AdapterIngressCredential.Sha256HashAlgorithm,
            exportedJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToBase64String(proof.SecretHash),
            exportedJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            first.Records.SelectMany(record => record.Fields),
            field => field.FieldId.Contains(
                "raw-payload-content",
                StringComparison.Ordinal));

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                TenantTerminationRequest(fence),
                replay,
                CancellationToken.None);
        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            first.Records.Select(Snapshot).ToArray(),
            replay.Records.Select(Snapshot).ToArray());

        await AssertExportSerializesOperationalMutationAsync(
            contributor,
            tenantAProvider,
            fence);
        await AssertOwnerProofIsDatabaseProtectedAsync(
            scope.ServiceProvider,
            proof);
    }

    private static async Task<SeedProof> SeedGraphAsync(
        IServiceProvider services,
        IngestionDbContext context)
    {
        string tenantId = services.GetRequiredService<IScopeContext>().ScopeId ??
            throw new InvalidOperationException(
                "The integration-test scope is required.");
        Guid propertyId = string.Equals(
            tenantId,
            TenantA,
            StringComparison.Ordinal)
            ? PropertyId
            : TenantBPropertyId;
        IIngestionPropertyProjectionRepository properties = services
            .GetRequiredService<IIngestionPropertyProjectionRepository>();
        await using (var transaction =
            await context.Database.BeginTransactionAsync())
        {
            await properties.ApplySnapshotAsync(
                new(
                    tenantId,
                    propertyId,
                    "Tenant Export House",
                    "tenant-export-house",
                    IsActive: true,
                    PropertyProcessingStatus.Unconfigured,
                    GovernancePolicy: null,
                    SourceVersion: 1),
                CancellationToken.None);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Guid connectionId = Guid.NewGuid();
        AdapterConnection connection = CreateConnection(
            tenantId,
            connectionId,
            propertyId,
            SecretReference);
        IngestionConnectionManagementOperation connectionOperation = new(
            new IngestionConnectionManagementOperationRecord(
                connectionId,
                tenantId,
                propertyId,
                connectionId,
                IngestionConnectionManagementMutationKind.ConnectionCreate,
                ExpectedVersion: 0,
                Digest,
                ResultVersion: 1,
                SeedNowUtc));
        byte[] secretHash = Enumerable
            .Repeat((byte)0xa5, AdapterIngressCredential.SecretHashLength)
            .ToArray();
        AdapterIngressCredential credential =
            AdapterIngressCredential.Create(
                Guid.NewGuid(),
                tenantId,
                connectionId,
                "fake.http",
                adapterProtocolVersion: 1,
                configurationSchemaVersion: 1,
                "booking",
                slot: 1,
                "primary",
                AdapterIngressCredential.Sha256HashAlgorithm,
                secretHash,
                SeedNowUtc.AddMinutes(10),
                "user:owner",
                SeedNowUtc).Value;
        AdapterIngressTenantControl tenantControl =
            AdapterIngressTenantControl.CreateSuspended(
                tenantId,
                "termination-test",
                "user:owner",
                SeedNowUtc.AddMinutes(1)).Value;

        IngestionRun run = IngestionRun.Start(
            Guid.NewGuid(),
            tenantId,
            connectionId,
            propertyId,
            Guid.NewGuid(),
            taskAttempt: 1,
            "checkpoint-1",
            SeedNowUtc.AddMinutes(2)).Value;
        Guid receiptId = Guid.NewGuid();
        Guid payloadFileId = Guid.NewGuid();
        ObservationReceipt receipt = ObservationReceipt.Create(
            receiptId,
            tenantId,
            propertyId,
            connectionId,
            run.Id,
            Guid.NewGuid(),
            "reservation.changed",
            "booking-123",
            "revision-1",
            "reservation.changed|booking-123|revision-1",
            new string('a', ObservationReceipt.ContentHashLength),
            CreateCountryPolicyEvidence(),
            payloadFileId,
            SeedNowUtc.AddDays(30),
            SeedNowUtc.AddMinutes(2),
            SeedNowUtc.AddMinutes(2),
            SeedNowUtc.AddMinutes(3)).Value;
        Assert.True(receipt.MarkProcessed(
            SeedNowUtc.AddMinutes(4)).IsSuccess);

        Guid attemptId = Guid.NewGuid();
        ObservationReprocessingAttempt attempt =
            ObservationReprocessingAttempt.Create(
                attemptId,
                tenantId,
                propertyId,
                connectionId,
                receiptId,
                attemptId,
                "reservation-json",
                parserVersion: 2,
                "user:operator",
                SeedNowUtc.AddMinutes(5),
                SeedNowUtc.AddMinutes(35)).Value;
        Assert.True(attempt.Start(
            attemptId,
            taskAttempt: 1,
            SeedNowUtc.AddMinutes(6),
            SeedNowUtc.AddMinutes(35)).IsSuccess);
        Assert.True(attempt.Complete(
            parsedCount: 1,
            acceptedCount: 1,
            duplicateCount: 0,
            rejectedCount: 0,
            noMatch: false,
            reasonCode: null,
            SeedNowUtc.AddMinutes(7)).IsSuccess);
        Guid derivedReceiptId = Guid.NewGuid();
        Guid derivedPayloadFileId = Guid.NewGuid();
        ObservationReceipt derivedReceipt = ObservationReceipt.Create(
            derivedReceiptId,
            tenantId,
            propertyId,
            connectionId,
            runId: null,
            Guid.NewGuid(),
            "reservation.changed",
            "booking-123-derived",
            "revision-2",
            "reservation.changed|booking-123-derived|revision-2",
            new string('b', ObservationReceipt.ContentHashLength),
            CreateCountryPolicyEvidence(),
            derivedPayloadFileId,
            SeedNowUtc.AddDays(30),
            SeedNowUtc.AddMinutes(6),
            SeedNowUtc.AddMinutes(6),
            SeedNowUtc.AddMinutes(7),
            receiptId,
            attemptId,
            "reservation-json",
            parserVersion: 2,
            parserOutputIndex: 0).Value;
        Assert.True(derivedReceipt.MarkProcessed(
            SeedNowUtc.AddMinutes(8)).IsSuccess);
        ObservationReprocessingOutput output =
            ObservationReprocessingOutput.Create(
                Guid.NewGuid(),
                tenantId,
                attemptId,
                outputIndex: 0,
                derivedReceiptId,
                ObservationReprocessingOutputDisposition.Accepted,
                "reservation.changed",
                "booking-123",
                "revision-1",
                new string('b', ObservationReceipt.ContentHashLength),
                errorCode: null,
                SeedNowUtc.AddMinutes(7)).Value;

        Guid reservationId = Guid.NewGuid();
        string largeDiff = new('x', 24_101);
        ChangeProposal proposal = ChangeProposal.Create(
            Guid.NewGuid(),
            tenantId,
            propertyId,
            connectionId,
            receiptId,
            reservationId,
            payloadFileId,
            baseReservationDetailsRevision: 1,
            "staff-conflict",
            largeDiff,
            SeedNowUtc.AddMinutes(8)).Value;

        ReservationSourceLink sourceLink =
            ReservationSourceLink.Create(
                Guid.NewGuid(),
                tenantId,
                propertyId,
                connectionId,
                "booking",
                "booking-123",
                SeedNowUtc.AddMinutes(8)).Value;
        Assert.True(sourceLink.Observe(
            receiptId,
            "revision-1",
            sourceSequence: 1,
            SeedNowUtc.AddMinutes(2),
            new string('a', ObservationReceipt.ContentHashLength),
            SeedNowUtc.AddMinutes(9)).IsSuccess);
        Guid dispatchId = Guid.NewGuid();
        Assert.True(sourceLink.BeginDispatch(
            dispatchId,
            SeedNowUtc.AddMinutes(9)).IsSuccess);
        Assert.True(sourceLink.CompleteDispatch(
            dispatchId,
            receiptId,
            "revision-1",
            sourceSequence: 1,
            "{\"guest\":\"Maya Chen\"}",
            reservationId,
            detailsRevision: 1,
            keepActive: false,
            applied: true,
            cancellationPending: false,
            cancelled: false,
            SeedNowUtc.AddMinutes(10)).IsSuccess);
        ReservationDispatch dispatch = ReservationDispatch.Create(
            dispatchId,
            tenantId,
            sourceLink.Id,
            ReservationDispatchTriggerKind.Observation,
            receiptId,
            receiptId,
            connectionId,
            propertyId,
            reservationId,
            ReservationDispatchKind.Amend,
            "revision-1",
            sourceSequence: 1,
            "{\"guest\":\"Maya Chen\",\"status\":\"confirmed\"}",
            expectedDetailsRevision: 1,
            SeedNowUtc.AddMinutes(9)).Value;
        Assert.True(dispatch.Complete(
            ReservationDispatchState.Applied,
            reservationId,
            detailsRevision: 2,
            reservationVersion: 2,
            errorCode: null,
            SeedNowUtc.AddDays(30),
            SeedNowUtc.AddMinutes(10)).IsSuccess);

        LegalHold legalHold = LegalHold.Place(
            Guid.NewGuid(),
            tenantId,
            propertyId,
            "Regulatory evidence preservation.",
            "user:privacy",
            SeedNowUtc.AddMinutes(11)).Value;
        IngestionRetentionExecution retention =
            IngestionRetentionExecution.Start(
                Guid.NewGuid(),
                tenantId,
                "observation-payload",
                executionPolicyVersion: 1,
                attempt: 1,
                SeedNowUtc.AddMinutes(12),
                SeedNowUtc.AddMinutes(42)).Value;
        Assert.True(retention.RecordAffected(attempt: 1, count: 1).IsSuccess);
        Assert.True(retention.Complete(
            IngestionRetentionExecutionState.Completed,
            attempt: 1,
            remainingCount: 0,
            outcomeCode: "completed",
            completedAtUtc: SeedNowUtc.AddMinutes(13),
            holdReviewDueAtUtc: null).IsSuccess);

        IngestionAnonymisationReceipt anonymisationReceipt =
            CreateAnonymisationReceipt(
                tenantId,
                propertyId,
                sourceLink.Id);
        IngestionAnonymisationTombstone tombstone =
            CreateAnonymisationTombstone(
                tenantId,
                connectionId,
                anonymisationReceipt);

        context.AddRange(
            connection,
            connectionOperation,
            credential,
            tenantControl,
            run,
            receipt,
            attempt,
            derivedReceipt,
            output,
            proposal,
            sourceLink,
            dispatch,
            legalHold,
            retention,
            tombstone,
            anonymisationReceipt);
        await context.SaveChangesAsync();

        Guid ledgerEntryId = Guid.NewGuid();
        Assert.True(tombstone.BeginProtectedReplay(
            propertyId,
            anonymisationReceipt.ContractVersion,
            anonymisationReceipt.Id,
            anonymisationReceipt.CanonicalSha256,
            anonymisationReceipt.ResultingSourceLinkVersion,
            anonymisationReceipt.CompletedAtUtc,
            ledgerEntryId,
            tenantSequence: 1,
            Digest,
            SeedNowUtc.AddMinutes(15)).IsSuccess);
        Assert.True(tombstone.CompleteRestore(
            SeedNowUtc.AddMinutes(16)).IsSuccess);
        await context.SaveChangesAsync();

        Assert.Equal(
            2,
            await ReadTenantRevisionAsync(context));
        Assert.Equal(4, tombstone.Revision);
        Assert.NotNull(tombstone.LastReplayedAtUtc);
        return new(
            connection.Id,
            credential.Id,
            receipt.Id,
            receipt.RawPayloadFileId,
            derivedReceipt.RawPayloadFileId,
            anonymisationReceipt.Id,
            tombstone.Id,
            secretHash);
    }

    private static async Task AssertDirectCredentialMutationIsCoordinatedAsync(
        IServiceProvider rootServices,
        Guid credentialId)
    {
        using IServiceScope scope = rootServices.CreateScope();
        IAdapterIngressCredentialRepository credentials =
            scope.ServiceProvider.GetRequiredService<
                IAdapterIngressCredentialRepository>();
        int? slot = await credentials.GetAvailableSlotAsync(
            (await scope.ServiceProvider
                .GetRequiredService<IngestionDbContext>()
                .AdapterIngressCredentials
                .AsNoTracking()
                .SingleAsync(credential => credential.Id == credentialId))
            .ConnectionId,
            SeedNowUtc.AddMinutes(20),
            CancellationToken.None);
        Assert.Equal(1, slot);

        IngestionDbContext context = scope.ServiceProvider
            .GetRequiredService<IngestionDbContext>();
        AdapterIngressCredential expired = await context
            .AdapterIngressCredentials
            .AsNoTracking()
            .SingleAsync(credential => credential.Id == credentialId);
        Assert.Equal(AdapterIngressCredentialState.Expired, expired.State);
        Assert.Equal(2, expired.Version);
        Assert.Equal(
            3,
            await ReadTenantRevisionAsync(context));
    }

    private static async Task AssertExportSerializesOperationalMutationAsync(
        ITenantTerminationExportContributor contributor,
        IServiceProvider rootServices,
        WorkspaceTerminationFence fence)
    {
        BlockingSink sink = new();
        Task<TenantTerminationContributionResult> export =
            contributor.ExportAsync(
                TenantTerminationRequest(fence),
                sink,
                CancellationToken.None);
        Assert.Same(
            sink.FirstRecordObserved,
            await Task.WhenAny(sink.FirstRecordObserved, export));

        Task write = AttemptOperationalWriteAsync(rootServices);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(write.IsCompleted);
        }
        finally
        {
            sink.Release();
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            (await export).Status);
        InvalidOperationException failure =
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => write);
        Assert.Equal(
            "The workspace is not accepting Ingestion mutations.",
            failure.Message);
    }

    private static async Task AttemptOperationalWriteAsync(
        IServiceProvider rootServices)
    {
        using IServiceScope scope = rootServices.CreateScope();
        IngestionDbContext context = scope.ServiceProvider
            .GetRequiredService<IngestionDbContext>();
        context.AdapterConnections.Add(CreateConnection(
            TenantA,
            Guid.NewGuid(),
            PropertyId,
            secretReference: null));
        await context.SaveChangesAsync();
    }

    private static async Task AssertOwnerProofIsDatabaseProtectedAsync(
        IServiceProvider services,
        SeedProof proof)
    {
        IngestionDbContext context = services
            .GetRequiredService<IngestionDbContext>();
        await AssertReceiptTriggerRejectedAsync(
            context,
            $"""
            UPDATE ingestion.anonymisation_receipts
            SET "ApprovalRevision" = "ApprovalRevision"
            WHERE "Id" = {proof.AnonymisationReceiptId};
            """);
        await AssertReceiptTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM ingestion.anonymisation_receipts
            WHERE "Id" = {proof.AnonymisationReceiptId};
            """);

        PostgresException tombstoneDelete = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM ingestion.anonymisation_tombstones
                WHERE "Id" = {proof.TombstoneId};
                """));
        Assert.Equal("P0001", tombstoneDelete.SqlState);
        Assert.Contains(
            "ingestion anonymisation tombstones cannot be deleted",
            tombstoneDelete.MessageText,
            StringComparison.Ordinal);
    }

    private static async Task AssertReceiptTriggerRejectedAsync(
        IngestionDbContext context,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Contains(
            "ingestion anonymisation receipts are append-only",
            failure.MessageText,
            StringComparison.Ordinal);
    }

    private static Task<long> ReadTenantRevisionAsync(
        IngestionDbContext context) =>
        context.Database
            .SqlQuery<long>($"""
                SELECT "Revision" AS "Value"
                FROM ingestion.tenant_revisions
                WHERE "ScopeId" = {TenantA}
                """)
            .SingleAsync();

    private static AdapterConnection CreateConnection(
        string tenantId,
        Guid connectionId,
        Guid propertyId,
        string? secretReference) =>
        AdapterConnection.Create(
            connectionId,
            tenantId,
            propertyId,
            "fake.http",
            AdapterExecutionMode.Push,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://tenant-export",
            secretReference,
            SeedNowUtc).Value;

    private static ObservationCountryPolicyEvidence
        CreateCountryPolicyEvidence() =>
        ObservationCountryPolicyEvidence.Create(
            "GB",
            "ingestion-test",
            policyVersion: 1,
            "eu-west-2",
            "uk-no-transfer",
            "ingestion-evidence",
            retentionPolicyVersion: 1,
            Digest,
            "reservation-operations",
            "adapter-ingress",
            "integration-test",
            SeedNowUtc.AddDays(-1),
            SeedNowUtc.AddYears(1),
            SeedNowUtc).Value;

    private static IngestionAnonymisationReceipt
        CreateAnonymisationReceipt(
            string tenantId,
            Guid propertyId,
            Guid sourceLinkId) =>
        IngestionAnonymisationReceipt.Create(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            propertyId,
            CaseId,
            approvalRevision: 1,
            operationRevision: 2,
            sourceLinkId,
            selectedSourceLinkVersion: 1,
            resultingSourceLinkVersion: 2,
            graphRecordCount: 1,
            fingerprintCount: 1,
            rawPayloadCount: 0,
            Digest,
            Digest,
            Digest,
            "user:privacy",
            SeedNowUtc.AddMinutes(14)).Value;

    private static IngestionAnonymisationTombstone
        CreateAnonymisationTombstone(
            string tenantId,
            Guid connectionId,
            IngestionAnonymisationReceipt receipt)
    {
        IngestionAnonymisationTombstone tombstone =
            IngestionAnonymisationTombstone.BeginExecution(
                tenantId,
                receipt.SourceLinkId,
                receipt.PropertyId,
                connectionId,
                receipt.SelectedSourceLinkVersion,
                receipt.WorkItemId,
                receipt.IdempotencyKey,
                receipt.CaseId,
                receipt.ApprovalRevision,
                receipt.OperationRevision,
                receipt.Id,
                receipt.ApprovalEvidenceSha256,
                receipt.PolicyEvidenceSha256,
                receipt.OperationFenceSha256,
                receipt.ActorId,
                receipt.GraphRecordCount,
                receipt.FingerprintCount,
                receipt.RawPayloadCount,
                SeedNowUtc.AddMinutes(13)).Value;
        Assert.True(tombstone.CompleteExecution(receipt).IsSuccess);
        return tombstone;
    }

    private static WorkspaceTerminationFence CreateTerminationFence() =>
        WorkspaceTerminationFence.Freeze(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            TenantA,
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            CaseId,
            approvalRevision: 1,
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            Digest,
            "termination-operator",
            FrozenAtUtc).Value;

    private static TenantTerminationExportRequest TenantTerminationRequest(
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
                Guid.Parse("70000000-0000-0000-0000-000000000001"),
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                fence.PolicyEvidenceSha256,
                "termination-exporter",
                ExportNowUtc.AddMinutes(30)),
            FreezeOperationRevision: 1,
            fence.Version,
            Digest,
            FrozenAtUtc);

    private static string Snapshot(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|" +
        $"{record.RecordVersion}|{FieldsJson(record)}";

    private static string FieldsJson(DataRightsExportRecord record) =>
        string.Join(
            '|',
            record.Fields
                .OrderBy(field => field.FieldId, StringComparer.Ordinal)
                .Select(field =>
                    $"{field.FieldId}:{field.Value.GetRawText()}"));

    private static ServiceProvider CreatePersistenceProvider(
        string connectionString,
        string tenantId,
        TestClock? clock = null,
        TestRawPayloadStore? rawPayloads = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(
            new TestScopeContext(tenantId));
        builder.Services.AddSingleton<ISystemClock>(
            clock ?? new TestClock(ExportNowUtc));
        builder.Services.AddSingleton<IIdGenerator, TestIdGenerator>();
        builder.Services.AddSingleton(rawPayloads ?? new TestRawPayloadStore());
        builder.Services.AddSingleton<IRawPayloadStore>(services =>
            services.GetRequiredService<TestRawPayloadStore>());
        builder.AddWorkspacesPersistence();
        builder.Services.AddSingleton<IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            UnusedStaffOnboardingOutcomeReader>();
        builder.AddIngestionPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private sealed record SeedProof(
        Guid ConnectionId,
        Guid CredentialId,
        Guid ObservationReceiptId,
        Guid RawPayloadFileId,
        Guid DerivedRawPayloadFileId,
        Guid AnonymisationReceiptId,
        Guid TombstoneId,
        byte[] SecretHash);

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class TestClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
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
        private readonly TaskCompletionSource firstRecord = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int recordCount;

        public Task FirstRecordObserved => this.firstRecord.Task;

        public async ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (Interlocked.Increment(ref this.recordCount) == 1)
            {
                this.firstRecord.TrySetResult();
                await this.release.Task.WaitAsync(cancellationToken);
            }
        }

        public void Release() => this.release.TrySetResult();
    }
}

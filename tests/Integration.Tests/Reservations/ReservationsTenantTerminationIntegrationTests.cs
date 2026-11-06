namespace Integration.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Domain.GuestRecords;
using BunkFy.Modules.Reservations.Domain.Models;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using DomainDataHoldAction =
    BunkFy.Modules.Reservations.Domain.Models.ReservationDataHoldAction;

public sealed class ReservationsTenantTerminationIntegrationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid PropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantBPropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset ExportNowUtc =
        new(2026, 7, 31, 18, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FrozenAtUtc =
        ExportNowUtc.AddMinutes(-1);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_export_is_repeatable_isolated_and_freezes_writes()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_tenant_export_tests")
                .Build();
        await postgreSql.StartAsync();

        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            postgreSql.GetConnectionString(),
            TenantA);
        ProofIds proofIds;
        Guid tenantAReservationId;
        using (IServiceScope seedScope = tenantAProvider.CreateScope())
        {
            ReservationsDbContext reservations = seedScope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await reservations.Database.MigrateAsync();
            await workspaces.Database.MigrateAsync();
            (tenantAReservationId, proofIds) = await SeedGraphAsync(
                seedScope.ServiceProvider,
                TenantA,
                PropertyId);
        }

        Guid tenantBReservationId;
        using (ServiceProvider tenantBProvider = CreatePersistenceProvider(
                   postgreSql.GetConnectionString(),
                   TenantB))
        using (IServiceScope tenantBScope = tenantBProvider.CreateScope())
        {
            ReservationsDbContext tenantBContext = tenantBScope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            Reservation tenantBReservation = CreateReservation(
                TenantB,
                PropertyId,
                "Other tenant guest");
            tenantBReservationId = tenantBReservation.Id;
            tenantBContext.Reservations.Add(tenantBReservation);
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
                    ReservationsTenantTerminationMetadata.ExportSchemaId);
        CollectingSink first = new();
        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                TenantTerminationRequest(fence),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(
            "reservations.termination.exported",
            result.ResultCode);
        Assert.Contains(
            first.Records,
            record => record.RecordId == tenantAReservationId);
        Assert.DoesNotContain(
            first.Records,
            record => record.RecordId == tenantBReservationId);
        Assert.Equal(
            ReservationsTenantTerminationMetadata.RecordTypes,
            first.Records
                .Select(record => record.RecordType)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        DataRightsExportRecord stayAmendment = Assert.Single(
            first.Records,
            record => record.RecordType ==
                    ReservationsTenantTerminationMetadata
                        .StayAmendmentOperationRecordType &&
                record.RecordId ==
                DataRightsExportRecordIds.CreateDeterministicChild(
                    proofIds.StayAmendmentReservationId,
                    proofIds.StayAmendmentOperationId.ToString("N")));
        Assert.Equal(
            "applied",
            Field(stayAmendment, "reservations.stay-amendment-operation")
                .GetProperty("outcome")
                .GetString());
        Assert.Equal(
            1,
            Field(stayAmendment, "reservations.stay-amendment-operation")
                .GetProperty("resultingAllocationVersion")
                .GetInt64());

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                TenantTerminationRequest(fence),
                replay,
                CancellationToken.None);
        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            first.Records.Select(RecordIdentity).ToArray(),
            replay.Records.Select(RecordIdentity).ToArray());

        await AssertExportSerializesOperationalMutationAsync(
            contributor,
            tenantAProvider,
            fence);
        await AssertOwnerProofIsDatabaseProtectedAsync(
            scope.ServiceProvider,
            proofIds);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_destroy_is_bounded_resumable_immutable_and_isolated()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_tenant_destroy_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();

        TestClock tenantAClock = new(ExportNowUtc);
        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            connectionString,
            TenantA,
            tenantAClock);
        using (IServiceScope seedScope = tenantAProvider.CreateScope())
        {
            ReservationsDbContext reservations = seedScope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            await reservations.Database.MigrateAsync();
            await workspaces.Database.MigrateAsync();
            await SeedGraphAsync(
                seedScope.ServiceProvider,
                TenantA,
                PropertyId);
        }

        WorkspaceTerminationFence tenantAFence =
            await AddFenceAsync(tenantAProvider, TenantA);
        using (IServiceScope blockedScope = tenantAProvider.CreateScope())
        {
            ITenantTerminationContributor contributor =
                ResolveContributor(blockedScope.ServiceProvider);
            TenantTerminationContributionResult blocked =
                await contributor.ExecuteAsync(
                    TenantDestroyRequest(
                        tenantAFence,
                        TenantA,
                        Guid.Parse(
                            "a1000000-0000-0000-0000-000000000001")),
                    CancellationToken.None);

            Assert.Equal(
                TenantTerminationContributionStatus.Blocked,
                blocked.Status);
            Assert.Equal(
                "reservations.termination.destroy-active-hold",
                blocked.ResultCode);
            Assert.Equal(1, blocked.RemainingActiveCount);
        }

        Assert.Equal(
            0,
            await CountForTenantAsync(
                connectionString,
                "reservations.tenant_destroy_operations",
                TenantA));

        TestClock tenantBClock = new(ExportNowUtc);
        using ServiceProvider tenantBProvider = CreatePersistenceProvider(
            connectionString,
            TenantB,
            tenantBClock);
        using (IServiceScope seedScope = tenantBProvider.CreateScope())
        {
            ReservationsDbContext reservations = seedScope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            await SeedGraphAsync(
                seedScope.ServiceProvider,
                TenantB,
                TenantBPropertyId);
            ReservationDataHold hold = await reservations.DataHolds
                .SingleAsync(candidate =>
                    candidate.State == ReservationDataHoldState.Active);
            Assert.True(hold.Release(
                hold.Version,
                "user:privacy-controller",
                ExportNowUtc.AddMinutes(-1)).IsSuccess);
            await reservations.SaveChangesAsync();

            OutboxMessage[] messages = Enumerable.Range(0, 501)
                .Select(_ => new OutboxMessage(
                    Guid.NewGuid(),
                    "bunkfy.reservations.termination-test.v1",
                    "termination-test",
                    version: 1,
                    TenantB,
                    tenantBClock.UtcNow,
                    "{}",
                    tenantBClock.UtcNow))
                .ToArray();
            messages[0].MarkClaimed(
                "termination-test-worker",
                tenantBClock.UtcNow,
                TimeSpan.FromMinutes(1));
            reservations.OutboxMessages.AddRange(messages);
            await reservations.SaveChangesAsync();
        }

        long selectedRevision = await ScalarForTenantAsync(
            connectionString,
            """
            SELECT "Revision"::bigint
            FROM reservations.tenant_revisions
            WHERE "ScopeId" = @tenantId
            """,
            TenantB);

        WorkspaceTerminationFence tenantBFence =
            await AddFenceAsync(tenantBProvider, TenantB);
        Guid operationId = Guid.Parse(
            "b1000000-0000-0000-0000-000000000001");
        TenantTerminationContributionRequest request = TenantDestroyRequest(
            tenantBFence,
            TenantB,
            operationId);
        using IServiceScope destroyScope = tenantBProvider.CreateScope();
        ITenantTerminationContributor destroyContributor =
            ResolveContributor(destroyScope.ServiceProvider);

        TenantTerminationContributionResult busy =
            await destroyContributor.ExecuteAsync(
                request,
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            busy.Status);
        Assert.Equal(
            "reservations.termination.destroy-outbox-busy",
            busy.ResultCode);
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "reservations.tenant_destroy_operations",
                TenantB));
        Assert.Equal(
            501,
            await CountForTenantAsync(
                connectionString,
                "reservations.outbox_messages",
                TenantB));

        tenantBClock.UtcNow = tenantBClock.UtcNow.AddMinutes(2);
        TenantTerminationContributionResult firstBatch =
            await destroyContributor.ExecuteAsync(
                request,
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            firstBatch.Status);
        Assert.Equal(
            "reservations.termination.destroy-in-progress",
            firstBatch.ResultCode);
        Assert.Equal(500, firstBatch.AffectedCount);
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "reservations.outbox_messages",
                TenantB));

        TenantTerminationContributionResult completed = firstBatch;
        int attempts = 1;
        while (completed.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 100)
        {
            completed = await destroyContributor.ExecuteAsync(
                request,
                CancellationToken.None);
            attempts++;
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            completed.Status);
        Assert.Equal(
            "reservations.termination.destroyed",
            completed.ResultCode);
        Assert.True(completed.AffectedCount > 501);
        Assert.Equal(selectedRevision, completed.SelectedProofRevision);
        Assert.Equal(
            selectedRevision + 1,
            completed.ResultingProofRevision);
        Assert.Equal(
            0,
            await CountForTenantAsync(
                connectionString,
                "reservations.tenant_destroy_operations",
                TenantB));
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "reservations.tenant_destroy_receipts",
                TenantB));
        Assert.Equal(
            0,
            await CountForTenantAsync(
                connectionString,
                "reservations.reservations",
                TenantB));
        Assert.Equal(
            0,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT (
                    (SELECT COUNT(*) FROM reservations.reservation_data_rights_correction_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM reservations.reservation_processing_restriction_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM reservations.reservation_data_hold_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM reservations.reservation_anonymisation_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM reservations.reservation_anonymisation_restore_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM reservations.reservation_retention_anonymisation_receipts WHERE "ScopeId" = @tenantId) +
                    (SELECT COUNT(*) FROM reservations.reservation_anonymisation_tombstones WHERE "ScopeId" = @tenantId)
                )::bigint
                """,
                TenantB));
        Assert.Equal(
            3,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT "LifecycleStatus"::bigint
                FROM reservations.tenant_revisions
                WHERE "ScopeId" = @tenantId
                """,
                TenantB));

        TenantTerminationContributionResult replay =
            await destroyContributor.ExecuteAsync(
                request,
                CancellationToken.None);
        Assert.Equal(completed, replay);
        TenantTerminationContributionResult conflict =
            await destroyContributor.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);

        ReservationsDbContext closedContext = destroyScope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        closedContext.ChangeTracker.Clear();
        closedContext.OutboxMessages.Add(new OutboxMessage(
            Guid.NewGuid(),
            "bunkfy.reservations.termination-test.v1",
            "termination-test",
            version: 1,
            TenantB,
            tenantBClock.UtcNow,
            "{}",
            tenantBClock.UtcNow));
        InvalidOperationException closedFailure =
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => closedContext.SaveChangesAsync());
        Assert.Equal(
            "The workspace is not accepting Reservations mutations.",
            closedFailure.Message);
        closedContext.ChangeTracker.Clear();

        PostgresException receiptMutation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                closedContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE reservations.tenant_destroy_receipts
                    SET "RemovedRecordCount" = "RemovedRecordCount"
                    WHERE "ScopeId" = {TenantB};
                    """));
        Assert.Equal("P0001", receiptMutation.SqlState);
        Assert.Contains(
            "reservation data-rights receipts are append-only",
            receiptMutation.MessageText,
            StringComparison.Ordinal);

        Assert.True(await CountForTenantAsync(
            connectionString,
            "reservations.reservations",
            TenantA) > 0);
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "reservations.stay_amendment_operations",
                TenantA));
        Assert.Equal(
            2,
            await CountForTenantAsync(
                connectionString,
                "reservations.management_operations",
                TenantA));
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "reservations.external_operations",
                TenantA));
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "reservations.reservation_details_history",
                TenantA));
        Assert.Equal(
            1,
            await CountForTenantAsync(
                connectionString,
                "reservations.guest_profile_projection",
                TenantA));
        Assert.Equal(
            1,
            await ScalarForTenantAsync(
                connectionString,
                """
                SELECT "LifecycleStatus"::bigint
                FROM reservations.tenant_revisions
                WHERE "ScopeId" = @tenantId
                """,
                TenantA));
    }

    private static async Task<(Guid ReservationId, ProofIds ProofIds)>
        SeedGraphAsync(
            IServiceProvider services,
            string tenantId,
            Guid propertyId)
    {
        ReservationsDbContext context = services
            .GetRequiredService<ReservationsDbContext>();
        IReservationDetailsHistoryWriter history = services
            .GetRequiredService<IReservationDetailsHistoryWriter>();
        IReservationExternalOperationRepository externalOperations = services
            .GetRequiredService<IReservationExternalOperationRepository>();
        IReservationManagementOperationRepository managementOperations = services
            .GetRequiredService<IReservationManagementOperationRepository>();
        IReservationArrivalReminderRepository reminders = services
            .GetRequiredService<IReservationArrivalReminderRepository>();
        await using var transaction =
            await context.Database.BeginTransactionAsync();
        Reservation reservation = CreateReservation(
            tenantId,
            propertyId,
            "Maya Chen");
        context.Set<ReservationGuestProfileProjection>().Add(
            new ReservationGuestProfileProjection(
                tenantId,
                Guid.NewGuid(),
                propertyId,
                BunkFy.Modules.Guests.Contracts.GuestStatus.Active,
                version: 1));
        Assert.True(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            ExportNowUtc.AddMinutes(-10)).IsSuccess);
        Assert.True(reservation.LinkGuest(
            Guid.NewGuid(),
            ReservationGuestRole.Primary,
            replaceExistingRole: false,
            reservation.Version,
            "user:owner",
            Guid.NewGuid(),
            ExportNowUtc.AddMinutes(-9)).IsSuccess);
        Guid adapterConnectionId = Guid.NewGuid();
        Guid externalOperationId = Guid.NewGuid();
        Assert.True(reservation.BeginAllocationAmendment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', Reservation.RequestFingerprintLength),
            reservation.Arrival,
            reservation.Departure.AddDays(1),
            reservation.RequestedUnits
                .Select(unit => unit.InventoryUnitId)
                .ToArray(),
            "Maya Chen Updated",
            "maya.updated@example.test",
            "+44 20 9999 4321",
            reservation.GuestCount,
            "Adapter note",
            reservation.DetailsRevision,
            ReservationDetailsChangeOrigin.Adapter,
            "adapter:test",
            adapterConnectionId,
            externalOperationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ExportNowUtc.AddMinutes(-8)).IsSuccess);
        ReservationDetailsChangedDomainEvent detailsChanged = reservation
            .DomainEvents
            .OfType<ReservationDetailsChangedDomainEvent>()
            .First();
        context.Reservations.Add(reservation);
        ReservationGuestRecordLinkProcess guestRecordLinkProcess =
            ReservationGuestRecordLinkProcess.Prepare(
                Guid.NewGuid(),
                tenantId,
                propertyId,
                reservation.Id,
                Guid.NewGuid(),
                reservation.Version,
                "user:owner",
                ExportNowUtc.AddMinutes(-7)).Value;
        context.GuestRecordLinkProcesses.Add(guestRecordLinkProcess);
        ReservationProcessingRestrictionProjection restrictionProjection =
            ReservationProcessingRestrictionProjection.Create(
                tenantId,
                propertyId,
                reservation.Id,
                ReservationProcessingRestrictionContract.CurrentVersion,
                ExportNowUtc.AddMinutes(-10)).Value;
        context.ProcessingRestrictionProjections.Add(
            restrictionProjection);
        await reminders.ApplyPropertyAsync(
            new(
                tenantId,
                propertyId,
                "Europe/Moscow",
                IsActive: true,
                SourceVersion: 1,
                ExportNowUtc.AddDays(-1)),
            CancellationToken.None);
        await context.SaveChangesAsync();

        await history.AppendAsync(
            detailsChanged,
            CancellationToken.None);
        await externalOperations.AddAsync(
            new ReservationExternalOperationRecord(
                externalOperationId,
                tenantId,
                Guid.NewGuid(),
                adapterConnectionId,
                propertyId,
                ExternalReservationOperationKind.Amend,
                new string('b', Reservation.RequestFingerprintLength),
                ExternalReservationOperationOutcome.Accepted,
                reservation.Id,
                reservation.DetailsRevision,
                reservation.Version,
                ErrorCode: null,
                ExportNowUtc.AddMinutes(-7)),
            CancellationToken.None);
        await managementOperations.AddAsync(
            new ReservationManagementOperationRecord(
                Guid.NewGuid(),
                tenantId,
                propertyId,
                reservation.Id,
                ReservationManagementOperationKind.GuestDetails,
                ExpectedVersion: null,
                ExpectedDetailsRevision: reservation.DetailsRevision,
                BusinessDate: null,
                CreatedAtUtc: ExportNowUtc.AddMinutes(-7)),
            CancellationToken.None);
        Reservation stayEvidenceReservation = CreateReservation(
            tenantId,
            propertyId,
            "Stay amendment evidence guest");
        Assert.True(stayEvidenceReservation.ConfirmAllocation(
            stayEvidenceReservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            ExportNowUtc.AddMinutes(-6)).IsSuccess);
        stayEvidenceReservation.ClearDomainEvents();
        Guid stayAmendmentOperationId = Guid.NewGuid();
        Guid[] stayUnits = stayEvidenceReservation.RequestedUnits
            .Select(unit => unit.InventoryUnitId)
            .ToArray();
        string stayFingerprint = StayAmendmentFingerprint(
            stayEvidenceReservation,
            stayAmendmentOperationId,
            stayUnits);
        context.Reservations.Add(stayEvidenceReservation);
        await context.SaveChangesAsync();
        Assert.True(
            stayEvidenceReservation.AdvanceStayAmendmentEvidenceCoordinate(
                ExportNowUtc.AddMinutes(-5)).IsSuccess);
        Assert.Equal(
            EntityState.Modified,
            context.Entry(stayEvidenceReservation).State);
        Assert.True(
            context.Entry(stayEvidenceReservation)
                .Property(candidate => candidate.Version)
                .IsModified);
        await managementOperations.AddAsync(
            new ReservationManagementOperationRecord(
                stayAmendmentOperationId,
                tenantId,
                propertyId,
                stayEvidenceReservation.Id,
                ReservationManagementOperationKind.StayAmendment,
                ExpectedVersion: null,
                stayEvidenceReservation.DetailsRevision,
                BusinessDate: null,
                ExportNowUtc.AddMinutes(-5),
                stayFingerprint),
            CancellationToken.None);
        context.Set<ReservationStayAmendmentOperation>().Add(
            ReservationStayAmendmentOperation.CreateAppliedNoOp(
                stayAmendmentOperationId,
                tenantId,
                propertyId,
                stayEvidenceReservation.Id,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                stayFingerprint,
                stayEvidenceReservation.Arrival,
                stayEvidenceReservation.Departure,
                stayEvidenceReservation.ExpectedArrivalTime,
                stayEvidenceReservation.ExpectedDepartureTime,
                stayUnits,
                stayEvidenceReservation.DetailsRevision,
                "user:stay-operator",
                stayEvidenceReservation.DetailsRevision,
                stayEvidenceReservation.Version,
                stayEvidenceReservation.AllocationVersion!.Value,
                ExportNowUtc.AddMinutes(-5)).Value);
        await reminders.RefreshReservationAsync(
            new(
                tenantId,
                reservation.Id,
                propertyId,
                reservation.Arrival,
                reservation.ExpectedArrivalTime,
                reservation.DetailsRevision),
            CancellationToken.None);

        ReservationDataRightsCorrectionReceipt correction =
            ReservationDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                propertyId,
                Guid.NewGuid(),
                approvalRevision: 1,
                reservation.Id,
                new(
                    PreviousRecordVersion: 1,
                    CurrentRecordVersion: 2,
                    PreviousDetailsRevision: 0,
                    CurrentDetailsRevision: 1,
                    [ReservationDetailsField.Email],
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ExportNowUtc.AddMinutes(-8)),
                Guid.NewGuid(),
                Guid.NewGuid()).Value;
        context.DataRightsCorrectionReceipts.Add(correction);

        ReservationProcessingRestriction restriction =
            ReservationProcessingRestriction.Create(
                Guid.NewGuid(),
                tenantId,
                propertyId,
                reservation.Id,
                Guid.NewGuid(),
                applyApprovalRevision: 1,
                reservation.Version,
                "user:owner",
                ExportNowUtc.AddMinutes(-7)).Value;
        Assert.True(restrictionProjection.Apply(
            expectedRevision: 0,
            ReservationProcessingRestrictionContract.CurrentVersion,
            ExportNowUtc.AddMinutes(-7)).IsSuccess);
        ReservationProcessingRestrictionReceipt restrictionReceipt =
            ReservationProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                restriction.Id,
                ReservationProcessingRestrictionAction.Apply,
                propertyId,
                reservation.Id,
                restriction.ApplyCaseId,
                restriction.ApplyApprovalRevision,
                reservation.Version,
                ReservationProcessingRestrictionContract.CurrentVersion,
                restriction.Version,
                restrictionProjection.Revision,
                restrictionProjection.IsRestricted,
                Guid.NewGuid(),
                ExportNowUtc.AddMinutes(-7)).Value;
        context.ProcessingRestrictions.Add(restriction);
        context.ProcessingRestrictionReceipts.Add(restrictionReceipt);

        ReservationDataHold hold = ReservationDataHold.Place(
            Guid.NewGuid(),
            tenantId,
            propertyId,
            reservation.Id,
            ReservationDataHoldReasonCodes.RegulatoryRequest,
            "user:owner",
            ExportNowUtc.AddMinutes(-6)).Value;
        ReservationDataHoldReceipt holdReceipt =
            ReservationDataHoldReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                hold,
                DomainDataHoldAction.Place,
                reservation.Version,
                reservation.DetailsRevision,
                ExportNowUtc.AddMinutes(-6)).Value;
        context.DataHolds.Add(hold);
        context.DataHoldReceipts.Add(holdReceipt);

        (ReservationAnonymisationReceipt anonymisationReceipt,
            ReservationAnonymisationTombstone tombstone,
            ReservationAnonymisationRestoreReceipt restoreReceipt) =
            SeedAnonymisationProof(context, tenantId, propertyId);
        ReservationRetentionAnonymisationReceipt retentionReceipt =
            SeedRetentionProof(context, tenantId, propertyId);

        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return (
            reservation.Id,
            new(
                correction.Id,
                restrictionReceipt.Id,
                holdReceipt.Id,
                anonymisationReceipt.Id,
                restoreReceipt.Id,
                retentionReceipt.Id,
                tombstone.Id,
                stayEvidenceReservation.Id,
                stayAmendmentOperationId));
    }

    private static (
        ReservationAnonymisationReceipt Receipt,
        ReservationAnonymisationTombstone Tombstone,
        ReservationAnonymisationRestoreReceipt RestoreReceipt)
        SeedAnonymisationProof(
            ReservationsDbContext context,
            string tenantId,
            Guid propertyId)
    {
        Reservation reservation = CreateTerminalReservation(
            tenantId,
            propertyId,
            "Anonymised Guest");
        ReservationAnonymisationOutcome outcome = reservation.Anonymise(
            reservation.Version,
            reservation.DetailsRevision,
            "user:privacy-executor",
            Guid.NewGuid(),
            ExportNowUtc.AddMinutes(-5)).Value;
        ReservationAnonymisationReceipt receipt =
            ReservationAnonymisationReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                Guid.NewGuid(),
                propertyId,
                Guid.NewGuid(),
                approvalRevision: 1,
                operationRevision: 2,
                reservation.Id,
                outcome,
                redactedHistoryCount: 1,
                reducedExternalOperationCount: 0,
                suppressedReminderCount: 0,
                Digest,
                Digest).Value;
        ReservationAnonymisationTombstone tombstone =
            ReservationAnonymisationTombstone.Create(receipt).Value;
        Guid ledgerEntryId = Guid.NewGuid();
        DateTimeOffset replayedAtUtc = ExportNowUtc.AddMinutes(-4);
        Assert.True(tombstone.AttachRestoreProof(
            propertyId,
            receipt.ContractVersion,
            receipt.Id,
            receipt.CanonicalSha256,
            receipt.ResultingReservationVersion,
            receipt.ResultingDetailsRevision,
            receipt.CompletedAtUtc,
            ledgerEntryId,
            replayedAtUtc).IsSuccess);
        ReservationAnonymisationRestoreReceipt restoreReceipt =
            ReservationAnonymisationRestoreReceipt.Create(
                tenantId,
                ledgerEntryId,
                tenantSequence: 1,
                Digest,
                propertyId,
                reservation.Id,
                receipt.ContractVersion,
                receipt.Id,
                receipt.CanonicalSha256,
                receipt.ResultingReservationVersion,
                receipt.ResultingDetailsRevision,
                receipt.CompletedAtUtc,
                tombstone.Revision,
                replayedAtUtc).Value;
        context.Reservations.Add(reservation);
        context.AnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(tombstone);
        context.AnonymisationRestoreReceipts.Add(restoreReceipt);
        return (receipt, tombstone, restoreReceipt);
    }

    private static ReservationRetentionAnonymisationReceipt
        SeedRetentionProof(
            ReservationsDbContext context,
            string tenantId,
            Guid propertyId)
    {
        Reservation reservation = CreateTerminalReservation(
            tenantId,
            propertyId,
            "Retention Guest");
        ReservationRetentionExecution execution =
            ReservationRetentionExecution.Start(
                Guid.NewGuid(),
                tenantId,
                "reservation-operational",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 0,
                ExportNowUtc.AddMinutes(-5),
                ExportNowUtc.AddMinutes(5)).Value;
        ReservationAnonymisationOutcome outcome = reservation.Anonymise(
            reservation.Version,
            reservation.DetailsRevision,
            ReservationRetentionAnonymisationReceipt.SystemActorId,
            Guid.NewGuid(),
            ExportNowUtc.AddMinutes(-3)).Value;
        ReservationRetentionAnonymisationReceipt receipt =
            ReservationRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                tenantId,
                execution.Id,
                propertyId,
                reservation.Id,
                outcome,
                reservation.TerminalAtUtc!.Value,
                ExportNowUtc.AddDays(-1),
                Digest,
                redactedHistoryCount: 1,
                reducedExternalOperationCount: 0,
                suppressedReminderCount: 0).Value;
        context.Reservations.Add(reservation);
        context.RetentionExecutions.Add(execution);
        context.RetentionAnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(
            ReservationAnonymisationTombstone.CreateForRetention(receipt).Value);
        return receipt;
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
            "The workspace is not accepting Reservations mutations.",
            failure.Message);
    }

    private static async Task AttemptOperationalWriteAsync(
        IServiceProvider rootServices)
    {
        using IServiceScope scope = rootServices.CreateScope();
        ReservationsDbContext context = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        context.Reservations.Add(CreateReservation(
            TenantA,
            PropertyId,
            "Blocked Guest"));
        await context.SaveChangesAsync();
    }

    private static async Task AssertOwnerProofIsDatabaseProtectedAsync(
        IServiceProvider services,
        ProofIds ids)
    {
        ReservationsDbContext context = services
            .GetRequiredService<ReservationsDbContext>();
        await AssertTriggerRejectedAsync(
            context,
            $"""
            UPDATE reservations.reservation_data_rights_correction_receipts
            SET "ApprovalRevision" = "ApprovalRevision"
            WHERE "Id" = {ids.CorrectionReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM reservations.reservation_processing_restriction_receipts
            WHERE "Id" = {ids.RestrictionReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM reservations.reservation_data_hold_receipts
            WHERE "Id" = {ids.HoldReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM reservations.reservation_anonymisation_receipts
            WHERE "Id" = {ids.AnonymisationReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM reservations.reservation_anonymisation_restore_receipts
            WHERE "Id" = {ids.RestoreReceiptId};
            """);
        await AssertTriggerRejectedAsync(
            context,
            $"""
            DELETE FROM reservations.reservation_retention_anonymisation_receipts
            WHERE "Id" = {ids.RetentionReceiptId};
            """);

        PostgresException tombstoneDelete = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM reservations.reservation_anonymisation_tombstones
                WHERE "Id" = {ids.TombstoneId};
                """));
        Assert.Equal("P0001", tombstoneDelete.SqlState);
        Assert.Contains(
            "reservation anonymisation tombstones cannot be deleted",
            tombstoneDelete.MessageText,
            StringComparison.Ordinal);
    }

    private static async Task AssertTriggerRejectedAsync(
        ReservationsDbContext context,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<
            PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Contains(
            "reservation data-rights receipts are append-only",
            failure.MessageText,
            StringComparison.Ordinal);
    }

    private static Reservation CreateReservation(
        string tenantId,
        Guid propertyId,
        string guestName) =>
        Reservation.Create(
            Guid.NewGuid(),
            tenantId,
            propertyId,
            Guid.NewGuid(),
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [Guid.NewGuid()],
            guestName,
            "guest@example.test",
            "+44 20 1234 5678",
            guestCount: 1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: "Late arrival",
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: "user:owner",
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            ExportNowUtc.AddDays(-1),
            expectedArrivalTime: new TimeOnly(15, 0),
            expectedDepartureTime: new TimeOnly(11, 0)).Value;

    private static Reservation CreateTerminalReservation(
        string tenantId,
        Guid propertyId,
        string guestName)
    {
        Reservation reservation = Reservation.Create(
            Guid.NewGuid(),
            tenantId,
            propertyId,
            Guid.NewGuid(),
            new DateOnly(2025, 6, 1),
            new DateOnly(2025, 6, 3),
            [Guid.NewGuid()],
            guestName,
            "historical@example.test",
            null,
            guestCount: 1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: "user:owner",
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            ExportNowUtc.AddDays(-401)).Value;
        Assert.True(reservation.RejectAllocation(
            reservation.AllocationRequestId,
            ReservationAllocationRejection.UnitNotSellable,
            Guid.NewGuid(),
            ExportNowUtc.AddDays(-400)).IsSuccess);
        reservation.ClearDomainEvents();
        return reservation;
    }

    private static WorkspaceTerminationFence CreateTerminationFence() =>
        CreateTerminationFence(TenantA);

    private static WorkspaceTerminationFence CreateTerminationFence(
        string tenantId) =>
        WorkspaceTerminationFence.Freeze(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            approvalRevision: 1,
            Guid.NewGuid(),
            Digest,
            "termination-operator",
            FrozenAtUtc).Value;

    private static async Task<WorkspaceTerminationFence> AddFenceAsync(
        ServiceProvider provider,
        string tenantId)
    {
        using IServiceScope scope = provider.CreateScope();
        WorkspacesDbContext context = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceTerminationFence fence = CreateTerminationFence(tenantId);
        context.WorkspaceTerminationFences.Add(fence);
        await context.SaveChangesAsync();
        return fence;
    }

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
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                Guid.Parse("a0000000-0000-0000-0000-000000000001"),
                fence.PolicyEvidenceSha256,
                "termination-exporter",
                ExportNowUtc.AddMinutes(5)),
            FreezeOperationRevision: 1,
            fence.Version,
            Digest,
            FrozenAtUtc);

    private static TenantTerminationContributionRequest TenantDestroyRequest(
        WorkspaceTerminationFence fence,
        string tenantId,
        Guid idempotencyKey) =>
        new(
            TenantTerminationContract.CurrentVersion,
            tenantId,
            fence.ProcessId,
            fence.CaseId,
            fence.ApprovalRevision,
            OperationRevision: 2,
            fence.TerminationEpoch,
            TenantTerminationContributionPhase.Destroy,
            Guid.NewGuid(),
            idempotencyKey,
            fence.PolicyEvidenceSha256,
            "termination-executor",
            ExportNowUtc.AddHours(2));

    private static ITenantTerminationContributor ResolveContributor(
        IServiceProvider services) =>
        services.GetServices<ITenantTerminationContributor>()
            .Single(contributor => string.Equals(
                contributor.Descriptor.OwnerKey,
                ReservationsTenantTerminationMetadata.OwnerKey,
                StringComparison.Ordinal));

    private static Task<long> CountForTenantAsync(
        string connectionString,
        string qualifiedTable,
        string tenantId) =>
        ScalarForTenantAsync(
            connectionString,
            $"SELECT COUNT(*) FROM {qualifiedTable} " +
            "WHERE \"ScopeId\" = @tenantId",
            tenantId);

    private static async Task<long> ScalarForTenantAsync(
        string connectionString,
        string commandText,
        string tenantId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(commandText, connection);
        command.Parameters.AddWithValue("tenantId", tenantId);
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(
            value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string RecordIdentity(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|{record.RecordVersion}";

    private static JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(
            record.Fields,
            field => field.FieldId == fieldId).Value;

    private static string StayAmendmentFingerprint(
        Reservation reservation,
        Guid operationId,
        IReadOnlyCollection<Guid> unitIds)
    {
        string canonical = string.Join(
            '|',
            "v2",
            $"reservation={reservation.Id:N}",
            $"operation={operationId:N}",
            $"expected-details-revision={reservation.DetailsRevision.ToString(CultureInfo.InvariantCulture)}",
            $"arrival={reservation.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"departure={reservation.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"expected-arrival-time={reservation.ExpectedArrivalTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "-"}",
            $"expected-departure-time={reservation.ExpectedDepartureTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "-"}",
            $"units={string.Join(',', unitIds.Order().Select(id => id.ToString("N")))}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static ServiceProvider CreatePersistenceProvider(
        string connectionString,
        string tenantId,
        TestClock? clock = null)
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
        builder.AddWorkspacesPersistence();
        builder.Services.AddSingleton<IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            UnusedStaffOnboardingOutcomeReader>();
        builder.AddReservationsPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private sealed record ProofIds(
        Guid CorrectionReceiptId,
        Guid RestrictionReceiptId,
        Guid HoldReceiptId,
        Guid AnonymisationReceiptId,
        Guid RestoreReceiptId,
        Guid RetentionReceiptId,
        Guid TombstoneId,
        Guid StayAmendmentReservationId,
        Guid StayAmendmentOperationId);

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

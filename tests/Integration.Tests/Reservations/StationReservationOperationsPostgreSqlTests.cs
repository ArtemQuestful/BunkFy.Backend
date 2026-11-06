namespace Integration.Tests;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Reservations-owned PostgreSQL persistence proof, not station admission, HTTP or transport proof.
/// Clock, tenant scope, open termination fence and Inventory release outcomes are explicit fixtures.
/// </summary>
public sealed partial class StationReservationOperationsPostgreSqlTests(ITestOutputHelper output)
{
    private const string TenantA = "aa000000-0000-0000-0000-000000000001";
    private const string TenantB = "bb000000-0000-0000-0000-000000000002";
    private const string PreviousMigration = "20260821234300_AddReservationRetentionControlProofAuditIntegrity";
    private const string AttributionMigration = "20260928003437_AddReservationStationAttribution";
    private const string AttributionForeignKey = "FK_station_attributions_management_operations_ScopeId_Reservat~";
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 28);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migration_preserves_primary_history_and_primary_dispatch_after_upgrade()
    {
        await using Fixture fixture = await Fixture.CreateAsync(PreviousMigration);
        SeededReservation historical = CreateReservation();
        Guid historicalOperation = Guid.NewGuid();
        long historicalExpectedVersion = historical.Reservation.Version;
        Assert.True(historical.Reservation.CheckIn(historicalExpectedVersion, BusinessDate,
            "user:synthetic-primary", Guid.NewGuid(), Now).IsSuccess);
        historical.Reservation.ClearDomainEvents();
        await SeedAsync(fixture.Services, historical, new ReservationManagementOperationRecord(
            historicalOperation, TenantA, historical.PropertyId, historical.Reservation.Id,
            ReservationManagementOperationKind.CheckIn, historicalExpectedVersion, null, BusinessDate, Now));
        string before = await SnapshotAsync(fixture.ConnectionString, historical.Reservation.Id, includeAttribution: false);
        using (JsonDocument history = JsonDocument.Parse(before))
        {
            Assert.Single(history.RootElement.GetProperty("history").EnumerateArray());
            Assert.Single(history.RootElement.GetProperty("management").EnumerateArray());
        }

        // No current lifecycle dispatcher runs against the schema that lacks station_attributions.
        await MigrateAsync(fixture.Services);
        Assert.Equal(before, await SnapshotAsync(fixture.ConnectionString, historical.Reservation.Id));
        using (IServiceScope scope = fixture.Services.CreateScope())
        {
            ReservationsDbContext db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            Assert.Contains(AttributionMigration, await db.Database.GetAppliedMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }

        await using (NpgsqlConnection connection = new(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand query = new("""
                SELECT conname, pg_get_constraintdef(oid)
                FROM pg_constraint WHERE conrelid = 'reservations.station_attributions'::regclass
                ORDER BY conname
                """, connection);
            await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();
            Dictionary<string, string> constraints = [];
            while (await reader.ReadAsync())
            {
                constraints.Add(reader.GetString(0), reader.GetString(1));
            }
            Assert.Equal(6, constraints.Count);
            Assert.Contains("PRIMARY KEY (\"ScopeId\", \"ReservationId\", \"OperationId\")",
                constraints["PK_station_attributions"], StringComparison.Ordinal);
            Assert.Contains("REFERENCES reservations.management_operations(\"ScopeId\", \"ReservationId\", \"Id\") ON DELETE CASCADE",
                constraints[AttributionForeignKey], StringComparison.Ordinal);
            Assert.Contains("\"Authority\"", constraints["CK_station_attribution_authority"], StringComparison.Ordinal);
            Assert.Contains("\"Generation\" > 0", constraints["CK_station_attribution_versions"], StringComparison.Ordinal);
            Assert.Contains("\"ResultingVersion\" > 0", constraints["CK_station_attribution_versions"], StringComparison.Ordinal);
            foreach (string identity in new[] { "ReservationId", "OperationId", "StationId", "BrowserSessionId", "StaffMemberId", "ActorSessionId" })
            {
                Assert.Contains(identity, constraints["CK_station_attribution_identity"], StringComparison.Ordinal);
            }
        }

        SeededReservation current = CreateReservation();
        await SeedAsync(fixture.Services, current);
        CheckInReservationCommand command = PrimaryCommand(current);
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
        string committed = await SnapshotAsync(fixture.ConnectionString, current.Reservation.Id);
        using JsonDocument document = JsonDocument.Parse(committed);
        Assert.Empty(document.RootElement.GetProperty("attribution").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("management").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("outbox").EnumerateArray());
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
        Assert.Equal(committed, await SnapshotAsync(fixture.ConnectionString, current.Reservation.Id));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Both_station_authorities_commit_one_correlated_graph_and_exact_replay_is_silent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        foreach (StationReservationAuthority authority in new[] { StationReservationAuthority.LinkedStation, StationReservationAuthority.StationOnly })
        {
            SeededReservation seed = CreateReservation();
            await SeedAsync(fixture.Services, seed);
            StationCheckInReservationCommand command = StationCommand(seed, authority);
            AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
            await AssertCommittedAsync(fixture.ConnectionString, command);
            string committed = await SnapshotAsync(fixture.ConnectionString, command.ReservationId);
            AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
            Assert.Equal(committed, await SnapshotAsync(fixture.ConnectionString, command.ReservationId));
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Exact_station_replay_after_completed_checkout_returns_current_state_without_rewriting_provenance()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SeededReservation seed = CreateReservation();
        await SeedAsync(fixture.Services, seed);
        StationCheckInReservationCommand command = StationCommand(seed);
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
        using JsonDocument initial = JsonDocument.Parse(await SnapshotAsync(fixture.ConnectionString, command.ReservationId));
        string originalAttribution = initial.RootElement.GetProperty("attribution").GetRawText();
        CheckOutReservationCommand checkout = new(Guid.NewGuid(), command.PropertyId, command.ReservationId,
            BusinessDate.AddDays(1), command.ExpectedVersion + 1, "user:synthetic-checkout");
        AssertSuccess(await SendAsync(fixture.Services, checkout), ReservationStatus.CheckoutPending, command.ExpectedVersion + 2);

        // Real registered Reservations continuation, with a synthetic Inventory outcome; no transport claim.
        using (IServiceScope scope = fixture.Services.CreateScope())
        {
            ReservationsDbContext db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            Reservation reservation = await db.Reservations.AsNoTracking().SingleAsync(row => row.Id == command.ReservationId);
            IntegrationEventSubscription subscription = scope.ServiceProvider.GetRequiredService<IIntegrationEventSubscriptionRegistry>()
                .Subscriptions.Single(item => item.ConsumerModule == ReservationsModuleMetadata.Name &&
                    item.EventType == typeof(InventoryAllocationReleasedIntegrationEvent));
            var handler = (IIntegrationEventHandler<InventoryAllocationReleasedIntegrationEvent>)scope.ServiceProvider.GetRequiredService(subscription.HandlerType);
            await handler.HandleAsync(new InventoryAllocationReleasedIntegrationEvent(Guid.NewGuid(), TenantA, Now,
                reservation.AllocationId!.Value, reservation.Id, reservation.ReleaseRequestId!.Value,
                reservation.AllocationVersion!.Value + 1), CancellationToken.None);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        string checkedOut = await SnapshotAsync(fixture.ConnectionString, command.ReservationId);
        using (JsonDocument completed = JsonDocument.Parse(checkedOut))
        {
            JsonElement root = completed.RootElement;
            Assert.Equal((int)ReservationStatus.CheckedOut, root.GetProperty("reservation").GetProperty("Status").GetInt32());
            Assert.Equal(originalAttribution, root.GetProperty("attribution").GetRawText());
            Assert.Equal(3, root.GetProperty("outbox").GetArrayLength());
            Assert.Equal(2, root.GetProperty("management").GetArrayLength());
        }
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedOut, command.ExpectedVersion + 3);
        Assert.Equal(checkedOut, await SnapshotAsync(fixture.ConnectionString, command.ReservationId));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Primary_station_and_changed_operation_provenance_collisions_leave_committed_state_unchanged()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SeededReservation primarySeed = CreateReservation();
        await SeedAsync(fixture.Services, primarySeed);
        CheckInReservationCommand primary = PrimaryCommand(primarySeed);
        AssertSuccess(await SendAsync(fixture.Services, primary), ReservationStatus.CheckedIn, primary.ExpectedVersion + 1);
        string primaryBefore = await SnapshotAsync(fixture.ConnectionString, primary.ReservationId);
        AssertConflict(await SendAsync(fixture.Services, StationCommand(primarySeed) with { OperationId = primary.OperationId }));
        Assert.Equal(primaryBefore, await SnapshotAsync(fixture.ConnectionString, primary.ReservationId));

        SeededReservation stationSeed = CreateReservation();
        await SeedAsync(fixture.Services, stationSeed);
        StationCheckInReservationCommand station = StationCommand(stationSeed);
        AssertSuccess(await SendAsync(fixture.Services, station), ReservationStatus.CheckedIn, station.ExpectedVersion + 1);
        string before = await SnapshotAsync(fixture.ConnectionString, station.ReservationId);
        AssertConflict(await SendAsync(fixture.Services, PrimaryCommand(stationSeed) with { OperationId = station.OperationId }));
        StationCheckInProvenance provenance = station.Provenance;
        foreach (StationCheckInProvenance changed in new[]
        {
            provenance with { StationId = Guid.NewGuid() }, provenance with { BrowserSessionId = Guid.NewGuid() },
            provenance with { StaffMemberId = Guid.NewGuid() }, provenance with { ActorSessionId = Guid.NewGuid() },
            provenance with { Generation = provenance.Generation + 1 }, provenance with { Authority = StationReservationAuthority.StationOnly, JobGrantRevision = 1 }
        })
        {
            AssertConflict(await SendAsync(fixture.Services, station with { Provenance = changed }));
            Assert.Equal(before, await SnapshotAsync(fixture.ConnectionString, station.ReservationId));
        }
        AssertConflict(await SendAsync(fixture.Services, station with { BusinessDate = BusinessDate.AddDays(1) }));
        AssertConflict(await SendAsync(fixture.Services, station with { ExpectedVersion = station.ExpectedVersion + 1 }));
        AssertConflict(await SendAsync(fixture.Services, new CheckOutReservationCommand(station.OperationId,
            station.PropertyId, station.ReservationId, BusinessDate, station.ExpectedVersion, "user:synthetic-primary")));
        Result<ReservationMutationReceiptDto> wrongProperty = await SendAsync(fixture.Services, station with { PropertyId = Guid.NewGuid() });
        Assert.True(wrongProperty.IsFailure);
        Assert.Equal(ReservationsApplicationErrors.ReservationNotFound.Code, wrongProperty.Error.Code);
        Result<ReservationMutationReceiptDto> newOperation = await SendAsync(fixture.Services, station with { OperationId = Guid.NewGuid() });
        Assert.True(newOperation.IsFailure);
        Assert.Equal(before, await SnapshotAsync(fixture.ConnectionString, station.ReservationId));

        SeededReservation unmodified = CreateReservation();
        await SeedAsync(fixture.Services, unmodified);
        StationCheckInReservationCommand wrongAllocation = StationCommand(unmodified) with { AllocationId = Guid.NewGuid() };
        string allocationBefore = await SnapshotAsync(fixture.ConnectionString, unmodified.Reservation.Id);
        AssertConflict(await SendAsync(fixture.Services, wrongAllocation));
        Assert.Equal(allocationBefore, await SnapshotAsync(fixture.ConnectionString, unmodified.Reservation.Id));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Actual_attribution_insert_failure_rolls_back_transition_parent_outbox_and_locks_then_retry_succeeds()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SeededReservation seed = CreateReservation();
        await SeedAsync(fixture.Services, seed);
        StationCheckInReservationCommand command = StationCommand(seed);
        string before = await SnapshotAsync(fixture.ConnectionString, command.ReservationId);
        string infrastructureBefore = await InfrastructureSnapshotAsync(fixture.ConnectionString, command.ReservationId);
        // Guid interpolation is test-owned, not input; trigger exists only in this unique disposable database.
        await ExecuteAsync(fixture.ConnectionString, $$"""
            CREATE FUNCTION reservations.pg1_reject_attribution() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                RAISE EXCEPTION 'PG1 injected attribution failure' USING ERRCODE = '23514', CONSTRAINT = 'pg1_reject_attribution';
            END $body$;
            CREATE TRIGGER pg1_reject_attribution BEFORE INSERT ON reservations.station_attributions
            FOR EACH ROW WHEN (NEW."OperationId" = '{{command.OperationId:D}}'::uuid)
            EXECUTE FUNCTION reservations.pg1_reject_attribution();
            """);
        try
        {
            Exception? failure = await Record.ExceptionAsync(async () => { await SendAsync(fixture.Services, command); });
            PostgresException postgres = Assert.IsType<PostgresException>(FindPostgresException(failure));
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
            Assert.Equal("pg1_reject_attribution", postgres.ConstraintName);
            Assert.Equal(before, await SnapshotAsync(fixture.ConnectionString, command.ReservationId));
            Assert.Equal(infrastructureBefore, await InfrastructureSnapshotAsync(fixture.ConnectionString, command.ReservationId));
        }
        finally
        {
            await ExecuteAsync(fixture.ConnectionString, """
                DROP TRIGGER pg1_reject_attribution ON reservations.station_attributions;
                DROP FUNCTION reservations.pg1_reject_attribution();
                """);
        }
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
        await AssertCommittedAsync(fixture.ConnectionString, command);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Independent_connections_concurrently_replay_one_operation_without_duplicate_effects()
    {
        var barrier = new OperationLockBarrier();
        await using Fixture fixture = await Fixture.CreateAsync(interceptor: barrier);
        SeededReservation seed = CreateReservation();
        await SeedAsync(fixture.Services, seed);
        StationCheckInReservationCommand command = StationCommand(seed);
        barrier.Arm();
        Task<Attempt>[] attempts = [AttemptAsync(fixture.Services, command), AttemptAsync(fixture.Services, command)];
        Attempt[] results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(2, barrier.ConnectionIds.Distinct().Count());
        Assert.Equal(2, barrier.ContextIds.Distinct().Count());
        Assert.Contains(results, result => result.Receipt?.IsSuccess == true);
        foreach (Attempt result in results)
        {
            output.WriteLine("Concurrent attempt: {0}", result.Exception?.ToString() ??
                (result.Receipt!.IsSuccess ? "Success" : result.Receipt.Error.Code));
            if (result.Exception is not null)
            {
                PostgresException postgres = Assert.IsType<PostgresException>(FindPostgresException(result.Exception));
                Assert.Contains(postgres.SqlState, new[] { PostgresErrorCodes.SerializationFailure, PostgresErrorCodes.DeadlockDetected });
            }
            else
            {
                AssertSuccess(result.Receipt!, ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
            }
        }
        await AssertCommittedAsync(fixture.ConnectionString, command);
        string committed = await SnapshotAsync(fixture.ConnectionString, command.ReservationId);
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
        Assert.Equal(committed, await SnapshotAsync(fixture.ConnectionString, command.ReservationId));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_rejects_duplicate_orphan_cross_scope_and_invalid_attribution_rows()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SeededReservation seed = CreateReservation();
        await SeedAsync(fixture.Services, seed);
        StationCheckInReservationCommand command = StationCommand(seed);
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
        var row = AttributionRow.From(command);
        string before = await SnapshotAsync(fixture.ConnectionString, command.ReservationId);
        await AssertConstraintAsync(fixture.ConnectionString, row, PostgresErrorCodes.UniqueViolation, "PK_station_attributions");
        await AssertConstraintAsync(fixture.ConnectionString, row with { OperationId = Guid.NewGuid() }, PostgresErrorCodes.ForeignKeyViolation, AttributionForeignKey);
        await AssertConstraintAsync(fixture.ConnectionString, row with { ScopeId = TenantB }, PostgresErrorCodes.ForeignKeyViolation, AttributionForeignKey);
        SeededReservation other = CreateReservation();
        await SeedAsync(fixture.Services, other);
        await AssertConstraintAsync(fixture.ConnectionString, row with { ReservationId = other.Reservation.Id }, PostgresErrorCodes.ForeignKeyViolation, AttributionForeignKey);
        foreach (int authority in new[] { 0, 3, -1 })
        {
            await AssertConstraintAsync(fixture.ConnectionString, row with { Authority = authority }, PostgresErrorCodes.CheckViolation, "CK_station_attribution_authority");
        }
        foreach (AttributionRow invalid in new[] { row with { Generation = 0 }, row with { Generation = -1 }, row with { ResultingVersion = 0 }, row with { ResultingVersion = -1 } })
        {
            await AssertConstraintAsync(fixture.ConnectionString, invalid, PostgresErrorCodes.CheckViolation, "CK_station_attribution_versions");
        }
        foreach (AttributionRow invalid in new[]
        {
            row with { ReservationId = Guid.Empty }, row with { OperationId = Guid.Empty }, row with { StationId = Guid.Empty },
            row with { BrowserSessionId = Guid.Empty }, row with { StaffMemberId = Guid.Empty }, row with { ActorSessionId = Guid.Empty }
        })
        {
            await AssertConstraintAsync(fixture.ConnectionString, invalid, PostgresErrorCodes.CheckViolation, "CK_station_attribution_identity");
        }
        Assert.Equal(before, await SnapshotAsync(fixture.ConnectionString, command.ReservationId));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tracked_attribution_is_immutable_parent_reads_are_separate_and_parent_cascade_is_exactly_scoped()
    {
        var capture = new ParentQueryCapture();
        await using Fixture fixture = await Fixture.CreateAsync(interceptor: capture);
        await using ServiceProvider tenantB = CreateProvider(fixture.ConnectionString, TenantB);
        Guid sharedOperationId = Guid.NewGuid();
        // Reservation IDs have a global PK; only operation IDs can legitimately repeat across tenants.
        SeededReservation seedA = CreateReservation(TenantA);
        SeededReservation seedB = CreateReservation(TenantB);
        await SeedAsync(fixture.Services, seedA);
        await SeedAsync(tenantB, seedB);
        StationCheckInReservationCommand commandA = StationCommand(seedA) with { OperationId = sharedOperationId };
        StationCheckInReservationCommand commandB = StationCommand(seedB, StationReservationAuthority.StationOnly) with { OperationId = sharedOperationId };
        AssertSuccess(await SendAsync(fixture.Services, commandA), ReservationStatus.CheckedIn, commandA.ExpectedVersion + 1);
        AssertSuccess(await SendAsync(tenantB, commandB), ReservationStatus.CheckedIn, commandB.ExpectedVersion + 1);
        string beforeA = await SnapshotAsync(fixture.ConnectionString, commandA.ReservationId);
        string beforeB = await SnapshotAsync(fixture.ConnectionString, commandB.ReservationId, TenantB);
        foreach ((ServiceProvider provider, StationCheckInReservationCommand expected) in new[] { (fixture.Services, commandA), (tenantB, commandB) })
        {
            using IServiceScope scope = provider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IReservationManagementOperationRepository>();
            capture.Commands.Clear();
            ReservationManagementOperationRecord? parent = await repository.GetAsync(expected.ReservationId, sharedOperationId, CancellationToken.None);
            Assert.NotNull(parent);
            Assert.Equal(expected.PropertyId, parent.PropertyId);
            Assert.Null(parent.RequestFingerprint);
            if (provider == fixture.Services)
            {
                string query = Assert.Single(capture.Commands);
                Assert.Contains("management_operations", query, StringComparison.Ordinal);
                Assert.DoesNotContain("station_attributions", query, StringComparison.Ordinal);
            }
            ReservationStationAttributionRead attribution = await repository.GetStationAttributionAsync(expected.ReservationId, sharedOperationId, CancellationToken.None);
            Assert.True(attribution.Supported);
            Assert.Equal(expected.Provenance, attribution.Provenance);
            Guid otherReservation = expected == commandA ? commandB.ReservationId : commandA.ReservationId;
            Assert.Null(await repository.GetAsync(otherReservation, sharedOperationId, CancellationToken.None));
            Assert.Null((await repository.GetStationAttributionAsync(otherReservation, sharedOperationId, CancellationToken.None)).Provenance);
        }

        foreach (bool delete in new[] { false, true })
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            ReservationsDbContext db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            Type entityType = db.Model.GetEntityTypes().Single(type => type.GetTableName() == "station_attributions").ClrType;
            object? tracked = await db.FindAsync(entityType, TenantA, commandA.ReservationId, sharedOperationId);
            Assert.NotNull(tracked);
            if (delete)
            {
                db.Remove(tracked);
            }
            else
            {
                db.Entry(tracked).Property("Generation").CurrentValue = commandA.Provenance.Generation + 1;
            }
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("station attribution is immutable", failure.Message, StringComparison.Ordinal);
            Assert.Equal(beforeA, await SnapshotAsync(fixture.ConnectionString, commandA.ReservationId));
        }

        // Raw scoped parent deletion tests the reviewed FK; this is not a tenant-destruction admission test.
        await ExecuteAsync(fixture.ConnectionString, """
            DELETE FROM reservations.management_operations
            WHERE "ScopeId" = @scope AND "ReservationId" = @reservation AND "Id" = @operation
            """, ("scope", TenantA), ("reservation", commandA.ReservationId), ("operation", sharedOperationId));
        using JsonDocument afterA = JsonDocument.Parse(await SnapshotAsync(fixture.ConnectionString, commandA.ReservationId));
        using JsonDocument originalA = JsonDocument.Parse(beforeA);
        Assert.Empty(afterA.RootElement.GetProperty("attribution").EnumerateArray());
        Assert.Empty(afterA.RootElement.GetProperty("management").EnumerateArray());
        Assert.Equal(originalA.RootElement.GetProperty("reservation").GetRawText(), afterA.RootElement.GetProperty("reservation").GetRawText());
        Assert.Equal(originalA.RootElement.GetProperty("outbox").GetRawText(), afterA.RootElement.GetProperty("outbox").GetRawText());
        Assert.Equal(beforeB, await SnapshotAsync(fixture.ConnectionString, commandB.ReservationId, TenantB));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_export_includes_separate_attribution_repeatably_while_guest_export_excludes_it()
    {
        var fence = new MutableFenceReader();
        var capture = new ParentQueryCapture();
        await using Fixture fixture = await Fixture.CreateAsync(interceptor: capture, fence: fence);
        await using ServiceProvider tenantB = CreateProvider(fixture.ConnectionString, TenantB);
        SeededReservation attributed = CreateReservation();
        SeededReservation ordinary = CreateReservation();
        SeededReservation otherTenant = CreateReservation(TenantB);
        await SeedAsync(fixture.Services, attributed);
        await SeedAsync(fixture.Services, ordinary);
        await SeedAsync(tenantB, otherTenant);
        StationCheckInReservationCommand station = StationCommand(attributed, StationReservationAuthority.StationOnly);
        StationCheckInReservationCommand other = StationCommand(otherTenant);
        AssertSuccess(await SendAsync(fixture.Services, station), ReservationStatus.CheckedIn, station.ExpectedVersion + 1);
        CheckInReservationCommand primary = PrimaryCommand(ordinary);
        AssertSuccess(await SendAsync(fixture.Services, primary), ReservationStatus.CheckedIn, primary.ExpectedVersion + 1);
        AssertSuccess(await SendAsync(tenantB, other), ReservationStatus.CheckedIn, other.ExpectedVersion + 1);

        using IServiceScope scope = fixture.Services.CreateScope();
        ReservationsDbContext db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await scope.ServiceProvider.GetRequiredService<IReservationArrivalReminderRepository>()
                .ApplyPropertyAsync(new(TenantA, attributed.PropertyId, "UTC", true, 1, Now), CancellationToken.None);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var guestExporter = scope.ServiceProvider.GetServices<IDataRightsSubjectExportContributor>()
            .Single(x => x.OwnerKey == ReservationsTenantTerminationMetadata.OwnerKey);
        var guest = new ExportSink();
        capture.Commands.Clear();
        var guestResult = await guestExporter.ExportAsync(new DataRightsSubjectExportRequest(TenantA,
            DataRightsCaseType.GuestRights, attributed.PropertyId,
            new DataRightsSubjectCoordinate(ReservationsTenantTerminationMetadata.OwnerKey,
                ReservationsTenantTerminationMetadata.ReservationRecordType, attributed.Reservation.Id, station.ExpectedVersion + 1)),
            guest, CancellationToken.None);
        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, guestResult.Status);
        Assert.Equal(3, Assert.Single(guest.Records, row =>
            row.RecordType == "reservation-management-operation").RecordVersion);
        Assert.DoesNotContain(capture.Commands, sql => sql.Contains("station_attributions", StringComparison.Ordinal));
        Assert.DoesNotContain(guest.Records.SelectMany(x => x.Fields), field =>
            field.FieldId.Contains("station", StringComparison.OrdinalIgnoreCase) ||
            field.FieldId.Contains("staff-attribution", StringComparison.OrdinalIgnoreCase));
        string guestJson = JsonSerializer.Serialize(guest.Records);
        foreach (Guid coordinate in new[] { station.Provenance.StationId, station.Provenance.BrowserSessionId,
            station.Provenance.StaffMemberId, station.Provenance.ActorSessionId })
        {
            Assert.DoesNotContain(coordinate.ToString("D"), guestJson, StringComparison.OrdinalIgnoreCase);
        }

        Guid process = Guid.NewGuid(), epoch = Guid.NewGuid();
        fence.Current = new(process, epoch, WorkspaceTerminationFenceState.Frozen, 3);
        string digest = new('a', 64);
        var request = new TenantTerminationExportRequest(new TenantTerminationContributionRequest(
            TenantTerminationContract.CurrentVersion, TenantA, process, Guid.NewGuid(), 1, 2, epoch,
            TenantTerminationContributionPhase.Export, Guid.NewGuid(), Guid.NewGuid(), digest,
            "synthetic-station-exporter", Now.AddMinutes(5)), 1, 3, digest, Now.AddMinutes(-1));
        var tenantExporter = scope.ServiceProvider.GetServices<ITenantTerminationExportContributor>()
            .Single(x => x.ExportDescriptor.ExportSchemaId == ReservationsTenantTerminationMetadata.ExportSchemaId);
        var first = new ExportSink();
        capture.Commands.Clear();
        var result = await tenantExporter.ExportAsync(request, first, CancellationToken.None);
        Assert.Equal(TenantTerminationContributionStatus.Completed, result.Status);
        Assert.Single(capture.Commands, sql => sql.Contains("station_attributions", StringComparison.Ordinal));
        DataRightsExportRecord[] operations = first.Records
            .Where(x => x.RecordType == ReservationsTenantTerminationMetadata.ManagementOperationRecordType).ToArray();
        Assert.Equal(2, operations.Length);
        Assert.All(operations, row => Assert.Equal(5, row.RecordVersion));
        DataRightsExportRecord stationRecord = Assert.Single(operations, row =>
            ExportField(row, "reservations.record-id").GetGuid() == station.OperationId);
        JsonElement staff = ExportField(stationRecord, "reservations.staff-attribution");
        Assert.Equal(station.Provenance.StationId, staff.GetProperty("stationId").GetGuid());
        Assert.Equal(station.Provenance.BrowserSessionId, staff.GetProperty("browserSessionId").GetGuid());
        Assert.Equal(station.Provenance.StaffMemberId, staff.GetProperty("staffMemberId").GetGuid());
        Assert.Equal(station.Provenance.ActorSessionId, staff.GetProperty("actorSessionId").GetGuid());
        Assert.Equal(station.Provenance.Generation, staff.GetProperty("generation").GetInt64());
        Assert.Equal("station-only", staff.GetProperty("authority").GetString());
        Assert.Equal(station.Provenance.JobGrantRevision, staff.GetProperty("jobGrantRevision").GetInt64());
        Assert.Equal(station.ExpectedVersion + 1, staff.GetProperty("resultingVersion").GetInt64());
        Assert.DoesNotContain("staffMemberId", ExportField(stationRecord, "reservations.management-operation").GetRawText(), StringComparison.Ordinal);
        DataRightsExportRecord primaryRecord = Assert.Single(operations, row =>
            ExportField(row, "reservations.record-id").GetGuid() == primary.OperationId);
        Assert.Equal(JsonValueKind.Null, ExportField(primaryRecord, "reservations.staff-attribution").ValueKind);
        Assert.DoesNotContain(other.Provenance.StaffMemberId.ToString("D"), JsonSerializer.Serialize(first.Records), StringComparison.Ordinal);
        var replay = new ExportSink();
        var replayResult = await tenantExporter.ExportAsync(request, replay, CancellationToken.None);
        Assert.Equal(TenantTerminationContributionStatus.Completed, replayResult.Status);
        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(JsonSerializer.Serialize(first.Records), JsonSerializer.Serialize(replay.Records));
    }

    private static JsonElement ExportField(DataRightsExportRecord record, string id) =>
        Assert.Single(record.Fields, field => field.FieldId == id).Value;

    private static SeededReservation CreateReservation(string tenant = TenantA, Guid? reservationId = null)
    {
        Guid property = Guid.NewGuid();
        Guid unit = Guid.NewGuid();
        Reservation reservation = Reservation.Create(reservationId ?? Guid.NewGuid(), tenant, property, Guid.NewGuid(),
            BusinessDate, BusinessDate.AddDays(3), [unit], "Synthetic Station Guest", "station-guest@example.test", null,
            1, ReservationSource.Direct, null, null, null, Guid.NewGuid(), Guid.NewGuid(), ReservationDetailsChangeOrigin.Staff,
            "user:synthetic-seed", null, null, Guid.NewGuid(), Now.AddHours(-1), new TimeOnly(15, 0), new TimeOnly(11, 0)).Value;
        ReservationDetailsChangedDomainEvent history = Assert.Single(reservation.DomainEvents.OfType<ReservationDetailsChangedDomainEvent>());
        Assert.True(reservation.ConfirmAllocation(reservation.AllocationRequestId, Guid.NewGuid(), 1, Guid.NewGuid(), Now.AddMinutes(-59)).IsSuccess);
        reservation.ClearDomainEvents();
        return new(reservation, property, unit, reservation.Version, history);
    }

    private static StationCheckInReservationCommand StationCommand(SeededReservation seed,
        StationReservationAuthority authority = StationReservationAuthority.LinkedStation) =>
        new(Guid.NewGuid(), seed.PropertyId, seed.Reservation.Id, BusinessDate, seed.InitialVersion,
            seed.Reservation.AllocationId, seed.Reservation.AllocationVersion,
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, authority, authority == StationReservationAuthority.StationOnly ? 1 : null));

    private static CheckInReservationCommand PrimaryCommand(SeededReservation seed) =>
        new(Guid.NewGuid(), seed.PropertyId, seed.Reservation.Id, BusinessDate, seed.InitialVersion, "user:synthetic-primary");

    private static async Task SeedAsync(ServiceProvider services, SeededReservation seed, ReservationManagementOperationRecord? history = null)
    {
        using IServiceScope scope = services.CreateScope();
        ReservationsDbContext db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        await scope.ServiceProvider.GetRequiredService<IReservationRepository>().AddAsync(seed.Reservation, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IReservationDetailsHistoryWriter>().AppendAsync(seed.History, CancellationToken.None);
        db.InventoryUnitProjections.Add(ReservationInventoryUnitProjection.Create(new ReservationInventoryUnitWriteModel(
            seed.Reservation.ScopeId, seed.InventoryUnitId, seed.PropertyId, Guid.NewGuid(), null,
            InventoryUnitKind.Room, "Synthetic Room", true, true, 1, 1)));
        if (history is not null)
        {
            await scope.ServiceProvider.GetRequiredService<IReservationManagementOperationRepository>().AddAsync(history, CancellationToken.None);
        }
        await db.SaveChangesAsync();
    }

    private static async Task<Result<ReservationMutationReceiptDto>> SendAsync(ServiceProvider services, ICommand<ReservationMutationReceiptDto> command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRequestDispatcher>().SendAsync(command, CancellationToken.None);
    }

    private static void AssertSuccess(Result<ReservationMutationReceiptDto> result, ReservationStatus status, long version)
    {
        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(status, result.Value.Status);
        Assert.Equal(version, result.Value.Version);
    }

    private static void AssertConflict(Result<ReservationMutationReceiptDto> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ReservationsApplicationErrors.ManagementOperationConflict.Code, result.Error.Code);
    }

    private static async Task AssertCommittedAsync(string connectionString, StationCheckInReservationCommand command)
    {
        using JsonDocument document = JsonDocument.Parse(await SnapshotAsync(connectionString, command.ReservationId));
        JsonElement root = document.RootElement;
        JsonElement reservation = root.GetProperty("reservation");
        JsonElement parent = Assert.Single(root.GetProperty("management").EnumerateArray());
        JsonElement child = Assert.Single(root.GetProperty("attribution").EnumerateArray());
        JsonElement outbox = Assert.Single(root.GetProperty("outbox").EnumerateArray());
        Assert.Equal(command.ExpectedVersion + 1, reservation.GetProperty("Version").GetInt64());
        Assert.Equal((int)ReservationStatus.CheckedIn, reservation.GetProperty("Status").GetInt32());
        Assert.Equal(command.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reservation.GetProperty("CheckedInBusinessDate").GetString());
        Assert.Equal(command.Provenance.StaffMemberId.ToString("D"), reservation.GetProperty("CheckedInBy").GetString());
        Assert.Equal(command.OperationId, parent.GetProperty("Id").GetGuid());
        Assert.Equal(command.PropertyId, parent.GetProperty("PropertyId").GetGuid());
        Assert.Equal(2, parent.GetProperty("Kind").GetInt32());
        Assert.Equal(command.ExpectedVersion, parent.GetProperty("ExpectedVersion").GetInt64());
        Assert.Equal(JsonValueKind.Null, parent.GetProperty("ExpectedDetailsRevision").ValueKind);
        Assert.Equal(JsonValueKind.Null, parent.GetProperty("RequestFingerprint").ValueKind);
        Assert.Equal(command.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), parent.GetProperty("BusinessDate").GetString());
        Assert.Equal(Now, parent.GetProperty("CreatedAtUtc").GetDateTimeOffset());
        Assert.Equal(TenantA, child.GetProperty("ScopeId").GetString());
        Assert.Equal(command.ReservationId, child.GetProperty("ReservationId").GetGuid());
        Assert.Equal(command.OperationId, child.GetProperty("OperationId").GetGuid());
        Assert.Equal(command.Provenance.StationId, child.GetProperty("StationId").GetGuid());
        Assert.Equal(command.Provenance.BrowserSessionId, child.GetProperty("BrowserSessionId").GetGuid());
        Assert.Equal(command.Provenance.StaffMemberId, child.GetProperty("StaffMemberId").GetGuid());
        Assert.Equal(command.Provenance.ActorSessionId, child.GetProperty("ActorSessionId").GetGuid());
        Assert.Equal(command.Provenance.Generation, child.GetProperty("Generation").GetInt64());
        Assert.Equal((int)command.Provenance.Authority, child.GetProperty("Authority").GetInt32());
        Assert.Equal(command.ExpectedVersion + 1, child.GetProperty("ResultingVersion").GetInt64());
        Assert.Equal(typeof(ReservationCheckedInIntegrationEvent).FullName, outbox.GetProperty("EventType").GetString());
        using JsonDocument payload = JsonDocument.Parse(outbox.GetProperty("Payload").GetString()!);
        Assert.Equal(command.ReservationId, payload.RootElement.GetProperty("reservationId").GetGuid());
        Assert.Equal(command.PropertyId, payload.RootElement.GetProperty("propertyId").GetGuid());
        Assert.Equal(command.Provenance.StaffMemberId.ToString("D"), payload.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(command.ExpectedVersion + 1, payload.RootElement.GetProperty("reservationVersion").GetInt64());
        Assert.Equal(command.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), payload.RootElement.GetProperty("businessDate").GetString());

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand query = new("""
            SELECT xmin::text::bigint FROM reservations.reservations WHERE "ScopeId" = @scope AND "Id" = @reservation
            UNION ALL SELECT xmin::text::bigint FROM reservations.management_operations WHERE "ScopeId" = @scope AND "ReservationId" = @reservation
            UNION ALL SELECT xmin::text::bigint FROM reservations.station_attributions WHERE "ScopeId" = @scope AND "ReservationId" = @reservation
            UNION ALL SELECT xmin::text::bigint FROM reservations.outbox_messages WHERE "ScopeId" = @scope AND "Payload"::jsonb->>'reservationId' = @reservation::text
            """, connection);
        AddParameters(query, ("scope", TenantA), ("reservation", command.ReservationId));
        await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();
        List<long> transactionIds = [];
        while (await reader.ReadAsync())
        {
            transactionIds.Add(reader.GetInt64(0));
        }
        Assert.Equal(4, transactionIds.Count);
        Assert.Single(transactionIds.Distinct());
    }

    private static async Task<string> SnapshotAsync(string connectionString, Guid reservation, string scope = TenantA, bool includeAttribution = true)
    {
        string child = includeAttribution
            ? "(SELECT COALESCE(jsonb_agg(to_jsonb(a) ORDER BY a.\"OperationId\"), '[]'::jsonb) FROM reservations.station_attributions a WHERE a.\"ScopeId\" = @scope AND a.\"ReservationId\" = @reservation)"
            : "'[]'::jsonb";
        return await ScalarTextAsync(connectionString, $$"""
            SELECT jsonb_build_object(
                'reservation', (SELECT to_jsonb(r) FROM reservations.reservations r WHERE r."ScopeId" = @scope AND r."Id" = @reservation),
                'management', (SELECT COALESCE(jsonb_agg(to_jsonb(m) ORDER BY m."Id"), '[]'::jsonb) FROM reservations.management_operations m WHERE m."ScopeId" = @scope AND m."ReservationId" = @reservation),
                'attribution', {{child}},
                'history', (SELECT COALESCE(jsonb_agg(to_jsonb(h) ORDER BY h."Id"), '[]'::jsonb) FROM reservations.reservation_details_history h WHERE h."ScopeId" = @scope AND h."ReservationId" = @reservation),
                'outbox', (SELECT COALESCE(jsonb_agg(to_jsonb(o) ORDER BY o."Id"), '[]'::jsonb) FROM reservations.outbox_messages o WHERE o."ScopeId" = @scope AND o."Payload"::jsonb->>'reservationId' = @reservation::text)
            )::text
            """, ("scope", scope), ("reservation", reservation));
    }

    private static Task<string> InfrastructureSnapshotAsync(string connectionString, Guid reservation) =>
        ScalarTextAsync(connectionString, """
            SELECT jsonb_build_object(
                'lock', (SELECT to_jsonb(l) FROM reservations.reservation_operation_locks l WHERE l."ScopeId" = @scope AND l."ReservationId" = @reservation),
                'revision', (SELECT to_jsonb(t) FROM reservations.tenant_revisions t WHERE t."ScopeId" = @scope)
            )::text
            """, ("scope", TenantA), ("reservation", reservation));

    private static async Task<string> ScalarTextAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        AddParameters(command, parameters);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameters(NpgsqlCommand command, params (string Name, object Value)[] parameters)
    {
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static async Task AssertConstraintAsync(string connectionString, AttributionRow row, string sqlState, string constraint)
    {
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, """
            INSERT INTO reservations.station_attributions
                ("ScopeId", "ReservationId", "OperationId", "StationId", "BrowserSessionId", "StaffMemberId", "ActorSessionId", "Generation", "Authority", "ResultingVersion")
            VALUES (@scope, @reservation, @operation, @station, @browser, @staff, @actor, @generation, @authority, @version)
            """, ("scope", row.ScopeId), ("reservation", row.ReservationId), ("operation", row.OperationId),
            ("station", row.StationId), ("browser", row.BrowserSessionId), ("staff", row.StaffMemberId),
            ("actor", row.ActorSessionId), ("generation", row.Generation), ("authority", row.Authority), ("version", row.ResultingVersion)));
        Assert.Equal(sqlState, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    private static PostgresException? FindPostgresException(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is PostgresException postgres)
            {
                return postgres;
            }
            exception = exception.InnerException;
        }
        return null;
    }

    private static async Task<Attempt> AttemptAsync(ServiceProvider services, StationCheckInReservationCommand command)
    {
        try
        {
            return new(await SendAsync(services, command), null);
        }
        catch (Exception exception)
        {
            return new(null, exception);
        }
    }

    private static async Task MigrateAsync(ServiceProvider services, string? target = null)
    {
        using IServiceScope scope = services.CreateScope();
        ReservationsDbContext db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static ServiceProvider CreateProvider(string connectionString, string tenant = TenantA, IInterceptor? interceptor = null,
        IWorkspaceTerminationFenceReader? fence = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:PostgreSql"] = connectionString
        });
        builder.Services.AddSingleton<IScopeContext>(new TestScope(tenant));
        builder.Services.AddSingleton<ISystemClock>(new TestClock());
        builder.Services.AddSingleton<IIdGenerator, TestIds>();
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(fence ?? OpenWorkspaceTerminationFenceReader.Instance);
        if (interceptor is not null)
        {
            builder.Services.AddDbContext<ReservationsDbContext>(options => options.UseNpgsql(connectionString, provider => provider
                .MigrationsAssembly(ReservationsMigrations.PostgreSqlAssembly)
                .MigrationsHistoryTable(ReservationsMigrations.HistoryTable, ReservationsMigrations.Schema)).AddInterceptors(interceptor));
        }
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        builder.Services.AddReservationsApplication();
        // Explicit allow decision for owner-adapter tests; this does not replace station or Auth admission.
        builder.Services.AddSingleton<IReservationCountryPolicyAdmission, StationCountryAdmission>();
        builder.AddReservationsPersistence();
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class Fixture(PostgreSqlContainer container, ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public string ConnectionString => container.GetConnectionString();

        public static async Task<Fixture> CreateAsync(string? target = null, IInterceptor? interceptor = null,
            IWorkspaceTerminationFenceReader? fence = null)
        {
            PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithName($"bunkfy-pin-pg1-{Guid.NewGuid():N}")
                .WithLabel("bunkfy.test.grant", "PIN-PG1-20260928")
                .WithDatabase("bunkfy_station_reservation_tests").Build();
            ServiceProvider? services = null;
            try
            {
                await container.StartAsync();
                services = CreateProvider(container.GetConnectionString(), interceptor: interceptor, fence: fence);
                await MigrateAsync(services, target);
                return new(container, services);
            }
            catch
            {
                if (services is not null)
                {
                    await services.DisposeAsync();
                }
                await container.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await this.Services.DisposeAsync();
            }
            finally
            {
                await container.DisposeAsync();
            }
        }
    }

    private sealed record SeededReservation(Reservation Reservation, Guid PropertyId, Guid InventoryUnitId,
        long InitialVersion, ReservationDetailsChangedDomainEvent History);
    private sealed record Attempt(Result<ReservationMutationReceiptDto>? Receipt, Exception? Exception);
    private sealed record AttributionRow(string ScopeId, Guid ReservationId, Guid OperationId, Guid StationId,
        Guid BrowserSessionId, Guid StaffMemberId, Guid ActorSessionId, long Generation, int Authority, long ResultingVersion)
    {
        public static AttributionRow From(StationCheckInReservationCommand command) => new(TenantA, command.ReservationId,
            command.OperationId, command.Provenance.StationId, command.Provenance.BrowserSessionId, command.Provenance.StaffMemberId,
            command.Provenance.ActorSessionId, command.Provenance.Generation, (int)command.Provenance.Authority, command.ExpectedVersion + 1);
    }

    private sealed class TestScope(string tenant) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => tenant;
    }
    private sealed class TestClock : ISystemClock { public DateTimeOffset UtcNow => Now; }
    private sealed class TestIds : IIdGenerator { public Guid NewId() => Guid.NewGuid(); }

    private sealed class MutableFenceReader : IWorkspaceTerminationFenceReader
    {
        public WorkspaceTerminationFenceSnapshot? Current { get; set; }
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Current);
    }

    private sealed class ExportSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];
        public ValueTask WriteAsync(DataRightsExportRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ParentQueryCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                this.Commands.Add(command.CommandText);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class OperationLockBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        private bool armed;
        public ConcurrentBag<int> ConnectionIds { get; } = [];
        public ConcurrentBag<Guid> ContextIds { get; } = [];
        public void Arm() => this.armed = true;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (this.armed && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("reservation_operation_locks", StringComparison.Ordinal) && Interlocked.Increment(ref this.arrivals) <= 2)
            {
                this.ConnectionIds.Add(((NpgsqlConnection)command.Connection!).ProcessID);
                this.ContextIds.Add(eventData.Context!.ContextId.InstanceId);
                if (this.arrivals == 2)
                {
                    this.release.TrySetResult();
                }
                await this.release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}

namespace Integration.Tests;

using BunkFy.DataGovernance;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Persistence;
using Gma.Framework.Messaging;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

public sealed partial class StationReservationOperationsPostgreSqlTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Legacy_station_only_checkin_replays_through_prepare_and_dispatch_after_additive_upgrade()
    {
        await using Fixture fixture = await Fixture.CreateAsync(AttributionMigration);
        SeededReservation seed = CreateReservation();
        StationCheckInReservationCommand command = StationCommand(seed, StationReservationAuthority.StationOnly);
        Assert.True(seed.Reservation.CheckIn(command.ExpectedVersion, BusinessDate,
            command.Provenance.StaffMemberId.ToString("D"), Guid.NewGuid(), Now).IsSuccess);
        seed.Reservation.ClearDomainEvents();
        await SeedAsync(fixture.Services, seed, new(command.OperationId, TenantA, command.PropertyId,
            command.ReservationId, ReservationManagementOperationKind.CheckIn, command.ExpectedVersion, null, BusinessDate, Now));
        // A genuine old-schema row, not an edit to an immutable current attribution.
        await ExecuteAsync(fixture.ConnectionString, """
            INSERT INTO reservations.station_attributions
                ("ScopeId", "ReservationId", "OperationId", "StationId", "BrowserSessionId", "StaffMemberId", "ActorSessionId", "Generation", "Authority", "ResultingVersion")
            VALUES (@scope, @reservation, @operation, @station, @browser, @staff, @actor, @generation, 2, @version)
            """, ("scope", TenantA), ("reservation", command.ReservationId), ("operation", command.OperationId),
            ("station", command.Provenance.StationId), ("browser", command.Provenance.BrowserSessionId),
            ("staff", command.Provenance.StaffMemberId), ("actor", command.Provenance.ActorSessionId),
            ("generation", command.Provenance.Generation), ("version", command.ExpectedVersion + 1));
        await MigrateAsync(fixture.Services);
        string before = await SnapshotAsync(fixture.ConnectionString, command.ReservationId);
        using IServiceScope scope = fixture.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IStationReservationOperations>();
        var prepared = await operations.PrepareAsync(command.PropertyId, command.ReservationId, command.OperationId,
            command.ExpectedVersion, BusinessDate.AddDays(1), command.Provenance);
        Assert.Equal(StationReservationState.Ready, prepared.State);
        Assert.True(prepared.Replay);
        Assert.Equal(BusinessDate, prepared.BusinessDate);
        var result = await operations.CheckInAsync(command.PropertyId, command.ReservationId, command.OperationId,
            command.ExpectedVersion, prepared, command.Provenance);
        Assert.Equal(StationReservationState.Applied, result.State);
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckedIn, command.ExpectedVersion + 1);
        Assert.Equal(before, await SnapshotAsync(fixture.ConnectionString, command.ReservationId));
        var outcome = scope.ServiceProvider.GetRequiredService<IStationCheckInOutcomeReader>();
        Assert.Equal(StationCheckInOutcomeState.Applied, (await outcome.ResolveAsync(command.PropertyId,
            command.Provenance.StationId, command.Provenance.BrowserSessionId, command.Provenance.ActorSessionId,
            command.Provenance.Generation, command.ReservationId, command.OperationId, command.ExpectedVersion)).State);
        Assert.Equal(StationCheckInOutcomeState.Conflict, (await outcome.ResolveAsync(command.PropertyId,
            command.Provenance.StationId, command.Provenance.BrowserSessionId, Guid.NewGuid(),
            command.Provenance.Generation, command.ReservationId, command.OperationId, command.ExpectedVersion)).State);
        var missingRevision = command.Provenance with { JobGrantRevision = null };
        Assert.Equal(StationReservationState.Conflict, (await operations.PrepareAsync(command.PropertyId,
            command.ReservationId, command.OperationId, command.ExpectedVersion, BusinessDate, missingRevision)).State);
        var invalid = await SendAsync(fixture.Services, command with { Provenance = missingRevision });
        Assert.True(invalid.IsFailure);
        Assert.Equal(ReservationsApplicationErrors.ManagementOperationInvalid.Code, invalid.Error.Code);
        await Assert.ThrowsAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<IReservationManagementOperationRepository>()
            .AddStationAsync(new(Guid.NewGuid(), TenantA, command.PropertyId, command.ReservationId,
                ReservationManagementOperationKind.CheckIn, command.ExpectedVersion, null, BusinessDate, Now),
                missingRevision, command.ExpectedVersion + 1, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(fixture.ConnectionString, command.ReservationId));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Station_checkout_has_due_queue_attribution_silent_replay_and_owner_release_truth()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        foreach (bool reject in new[] { false, true })
        {
            SeededReservation seed = CreateReservation();
            await SeedAsync(fixture.Services, seed);
            StationCheckInReservationCommand checkIn = StationCommand(seed, StationReservationAuthority.StationOnly);
            AssertSuccess(await SendAsync(fixture.Services, checkIn), ReservationStatus.CheckedIn, checkIn.ExpectedVersion + 1);
            using IServiceScope scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            db.InventoryAllocationProjections.Add(ReservationInventoryAllocationProjection.Create(new(TenantA,
                seed.Reservation.AllocationId!.Value, seed.Reservation.Id, seed.PropertyId, seed.Reservation.Arrival,
                seed.Reservation.Departure, InventoryAllocationStatus.Active, [seed.InventoryUnitId], 1)));
            // A draining/non-sellable occupied place remains eligible to leave; topology/version still must be valid.
            var unit = await db.InventoryUnitProjections.SingleAsync(x => x.Id == seed.InventoryUnitId);
            unit.Apply(new(TenantA, unit.Id, seed.PropertyId, unit.RoomId, unit.BedId, unit.Kind, unit.Label, true, false, 2, 2));
            await db.SaveChangesAsync();
            var owner = scope.ServiceProvider.GetRequiredService<IStationDepartureOperations>();
            Assert.Empty((await owner.ListAsync(seed.PropertyId, seed.Reservation.Departure.AddDays(-1), 25, null)).Items);
            var departure = Assert.Single((await owner.ListAsync(seed.PropertyId, seed.Reservation.Departure.AddDays(1), 25, null)).Items);
            Assert.Equal(StationDepartureState.Due, departure.State);
            Assert.Equal(BusinessDate, departure.CheckedInBusinessDate);
            Guid operation = Guid.NewGuid();
            var provenance = checkIn.Provenance with { JobGrantRevision = 8 };
            var prepared = await owner.PrepareAsync(seed.PropertyId, seed.Reservation.Id, operation,
                departure.ExpectedVersion, seed.Reservation.Departure, provenance);
            Assert.Equal(StationReservationState.Ready, prepared.State);
            var requested = await owner.CheckOutAsync(seed.PropertyId, seed.Reservation.Id, operation,
                departure.ExpectedVersion, prepared, provenance);
            Assert.Equal(StationReservationState.Applied, requested.State);
            Assert.Equal(StationCheckOutOutcomeState.ReleasePending, requested.Checkout);
            Assert.Equal(ReservationStatus.CheckoutPending, requested.Receipt!.Status);
            Assert.Equal(StationDepartureState.ReleasePending,
                Assert.Single((await owner.ListAsync(seed.PropertyId, seed.Reservation.Departure, 25, null)).Items).State);
            string pending = await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id);
            var replay = await owner.PrepareAsync(seed.PropertyId, seed.Reservation.Id, operation,
                departure.ExpectedVersion, seed.Reservation.Departure.AddDays(1), provenance);
            Assert.True(replay.Replay);
            Assert.Equal(StationCheckOutOutcomeState.ReleasePending, (await owner.CheckOutAsync(seed.PropertyId,
                seed.Reservation.Id, operation, departure.ExpectedVersion, replay, provenance)).Checkout);
            Assert.Equal(pending, await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id));
            var reader = scope.ServiceProvider.GetRequiredService<IStationCheckOutOutcomeReader>();
            async Task<StationCheckOutOutcome> ReadOutcome(Guid actor) => await reader.ResolveAsync(seed.PropertyId,
                provenance.StationId, provenance.BrowserSessionId, actor, provenance.Generation,
                seed.Reservation.Id, operation, departure.ExpectedVersion);
            Assert.Equal(StationCheckOutOutcomeState.ReleasePending, (await ReadOutcome(provenance.ActorSessionId)).State);
            Assert.Equal(StationCheckOutOutcomeState.Conflict, (await ReadOutcome(Guid.NewGuid())).State);
            Guid release = await CompleteStationReleaseAsync(fixture.Services, seed.Reservation.Id, reject);
            string completed = await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id);
            await CompleteStationReleaseAsync(fixture.Services, seed.Reservation.Id, reject, release);
            Assert.Equal(completed, await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id));
            Assert.Equal(reject ? StationCheckOutOutcomeState.ReleaseRejected : StationCheckOutOutcomeState.Completed,
                (await ReadOutcome(provenance.ActorSessionId)).State);
            var finalPage = await owner.ListAsync(seed.PropertyId, seed.Reservation.Departure, 25, null);
            if (reject)
            {
                var retry = Assert.Single(finalPage.Items);
                Assert.Equal(StationDepartureState.ReleaseRejected, retry.State);
                Assert.Equal(departure.ExpectedVersion + 2, retry.ExpectedVersion);
            }
            else
            { Assert.Empty(finalPage.Items); }
            Assert.Equal(reject ? StationCheckOutOutcomeState.ReleaseRejected : StationCheckOutOutcomeState.Completed,
                (await owner.CheckOutAsync(seed.PropertyId, seed.Reservation.Id, operation,
                    departure.ExpectedVersion, replay, provenance)).Checkout);
            Assert.Equal(completed, await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id));
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Checkout_attribution_failure_rolls_back_pending_release_and_outbox_then_exact_retry_succeeds()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SeededReservation seed = CreateReservation();
        await SeedAsync(fixture.Services, seed);
        StationCheckInReservationCommand checkIn = StationCommand(seed, StationReservationAuthority.StationOnly);
        AssertSuccess(await SendAsync(fixture.Services, checkIn), ReservationStatus.CheckedIn, checkIn.ExpectedVersion + 1);
        var command = new StationCheckOutReservationCommand(Guid.NewGuid(), seed.PropertyId, seed.Reservation.Id,
            seed.Reservation.Departure, checkIn.ExpectedVersion + 1, seed.Reservation.AllocationId,
            seed.Reservation.AllocationVersion, checkIn.Provenance with { JobGrantRevision = 7 });
        string before = await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id);
        string infrastructure = await InfrastructureSnapshotAsync(fixture.ConnectionString, seed.Reservation.Id);
        await ExecuteAsync(fixture.ConnectionString, $$"""
            CREATE FUNCTION reservations.checkout_reject_attribution() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                RAISE EXCEPTION 'Injected checkout attribution failure' USING ERRCODE = '23514', CONSTRAINT = 'checkout_reject_attribution';
            END $body$;
            CREATE TRIGGER checkout_reject_attribution BEFORE INSERT ON reservations.station_attributions
            FOR EACH ROW WHEN (NEW."OperationId" = '{{command.OperationId:D}}'::uuid)
            EXECUTE FUNCTION reservations.checkout_reject_attribution();
            """);
        try
        {
            Exception? failure = await Record.ExceptionAsync(async () => { await SendAsync(fixture.Services, command); });
            Assert.Equal("checkout_reject_attribution", Assert.IsType<PostgresException>(FindPostgresException(failure)).ConstraintName);
            Assert.Equal(before, await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id));
            Assert.Equal(infrastructure, await InfrastructureSnapshotAsync(fixture.ConnectionString, seed.Reservation.Id));
        }
        finally
        {
            await ExecuteAsync(fixture.ConnectionString, """
                DROP TRIGGER checkout_reject_attribution ON reservations.station_attributions;
                DROP FUNCTION reservations.checkout_reject_attribution();
                """);
        }
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckoutPending, command.ExpectedVersion + 1);
        string committed = await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id);
        AssertSuccess(await SendAsync(fixture.Services, command), ReservationStatus.CheckoutPending, command.ExpectedVersion + 1);
        Assert.Equal(committed, await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id));
        var invalid = await SendAsync(fixture.Services, command with { Provenance = command.Provenance with { JobGrantRevision = null } });
        Assert.True(invalid.IsFailure);
        Assert.Equal(ReservationsApplicationErrors.ManagementOperationInvalid.Code, invalid.Error.Code);
        Assert.Equal(committed, await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id));
        var guarded = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(fixture.Services, AttributionMigration));
        Assert.Equal(PostgresErrorCodes.RaiseException, guarded.SqlState);
        Assert.Equal(committed, await SnapshotAsync(fixture.ConnectionString, seed.Reservation.Id));
    }

    private static async Task<Guid> CompleteStationReleaseAsync(ServiceProvider services, Guid reservationId, bool reject, Guid? originalRelease = null)
    {
        using IServiceScope scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var row = await db.Reservations.AsNoTracking().SingleAsync(x => x.Id == reservationId);
        // Duplicate delivery retains the original request coordinate after either terminal transition.
        Guid release = originalRelease ?? row.ReleaseRequestId!.Value;
        var registry = scope.ServiceProvider.GetRequiredService<IIntegrationEventSubscriptionRegistry>();
        if (reject)
        {
            var subscription = registry.Subscriptions.Single(x => x.ConsumerModule == ReservationsModuleMetadata.Name &&
                x.EventType == typeof(InventoryAllocationReleaseRejectedIntegrationEvent));
            var handler = (IIntegrationEventHandler<InventoryAllocationReleaseRejectedIntegrationEvent>)scope.ServiceProvider.GetRequiredService(subscription.HandlerType);
            await handler.HandleAsync(new(Guid.NewGuid(), TenantA, Now, row.AllocationId!.Value, row.Id, release,
                InventoryAllocationReleaseRejectionReason.VersionConflict), CancellationToken.None);
        }
        else
        {
            var subscription = registry.Subscriptions.Single(x => x.ConsumerModule == ReservationsModuleMetadata.Name &&
                x.EventType == typeof(InventoryAllocationReleasedIntegrationEvent));
            var handler = (IIntegrationEventHandler<InventoryAllocationReleasedIntegrationEvent>)scope.ServiceProvider.GetRequiredService(subscription.HandlerType);
            await handler.HandleAsync(new(Guid.NewGuid(), TenantA, Now, row.AllocationId!.Value, row.Id, release, 2), CancellationToken.None);
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return release;
    }

    private sealed class StationCountryAdmission : IReservationCountryPolicyAdmission
    {
        public Task<CountryPolicyDecision> EvaluateAsync(Guid propertyId, string purposeCode, CountryPolicySurface surface,
            string sourceProvenance, CancellationToken cancellationToken) => Task.FromResult(CountryPolicyDecision.Allow(
                new CountryPolicyEvidence("GB", "gb-hostel", 1, "eu-west-2", "uk-no-transfer", "reservation-operational", 1,
                    new string('a', 64), purposeCode, surface, sourceProvenance, CountryPolicyApprovalState.Approved,
                    Now.AddDays(-1), Now.AddDays(30), Now, [])));
    }
}

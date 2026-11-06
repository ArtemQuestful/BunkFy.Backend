namespace Integration.Tests;

using System.Data;
using System.Data.Common;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class ReservationOperationsSnapshotPersistenceIntegrationTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Observation =
        new(2026, 8, 13, 10, 30, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Snapshot_is_property_local_exact_bounded_private_and_repeatable()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
            "postgres:16-alpine")
            .WithDatabase("bunkfy_reservation_operations_snapshot_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        await using (ReservationsDbContext migration = CreateDbContext(
            connectionString))
        {
            await migration.Database.MigrateAsync();
        }

        Guid honoluluPropertyId = Guid.Parse(
            "01000000-0000-0000-0000-000000000001");
        Guid kiritimatiPropertyId = Guid.Parse(
            "01000000-0000-0000-0000-000000000002");
        Guid inactivePropertyId = Guid.Parse(
            "01000000-0000-0000-0000-000000000003");
        Guid invalidTimeZonePropertyId = Guid.Parse(
            "01000000-0000-0000-0000-000000000004");
        Guid unknownPropertyId = Guid.Parse(
            "01000000-0000-0000-0000-000000000005");
        Guid utcPropertyId = Guid.Parse(
            "01000000-0000-0000-0000-000000000006");
        Guid windowsTimeZonePropertyId = Guid.Parse(
            "01000000-0000-0000-0000-000000000007");
        Guid policyOnlyPropertyId = Guid.Parse(
            "01000000-0000-0000-0000-000000000008");
        DateOnly honoluluDate = new(2026, 8, 13);

        List<ReservationSeed> reservations = CreateStatusMatrix(
            honoluluPropertyId,
            honoluluDate);
        await using (ReservationsDbContext seed = CreateDbContext(
            connectionString))
        {
            await using IDbContextTransaction transaction =
                await seed.Database.BeginTransactionAsync();
            await SeedPropertyAsync(
                seed,
                honoluluPropertyId,
                "Pacific/Honolulu",
                isKnown: true,
                isActive: true);
            await SeedPropertyAsync(
                seed,
                kiritimatiPropertyId,
                "Pacific/Kiritimati",
                isKnown: true,
                isActive: true);
            await SeedPropertyAsync(
                seed,
                inactivePropertyId,
                "Europe/Paris",
                isKnown: true,
                isActive: false);
            await SeedPropertyAsync(
                seed,
                invalidTimeZonePropertyId,
                "Invalid/Property-Zone",
                isKnown: true,
                isActive: true);
            await SeedPropertyAsync(
                seed,
                unknownPropertyId,
                "Etc/UTC",
                isKnown: false,
                isActive: true);
            await SeedPropertyAsync(
                seed,
                utcPropertyId,
                "UTC",
                isKnown: true,
                isActive: true);
            await SeedPropertyAsync(
                seed,
                windowsTimeZonePropertyId,
                "Pacific Standard Time",
                isKnown: true,
                isActive: true);
            await SeedPropertyAsync(
                seed,
                policyOnlyPropertyId,
                "Etc/UTC",
                isKnown: true,
                isActive: false,
                topologySourceVersion: 0);

            foreach (ReservationSeed reservation in reservations)
            {
                await SeedReservationAsync(seed, reservation);
            }

            await transaction.CommitAsync();
        }

        QueryCaptureInterceptor capture = new();
        await using (ServiceProvider provider = CreateProvider(
            connectionString,
            capture))
        {
            ReservationOperationsSnapshotReadResult read = await ReadAsync(
                provider,
                honoluluPropertyId,
                explicitLocalDate: null,
                upcomingLimit: 50);
            ReservationOperationsSnapshotDto snapshot = AssertFound(read);

            Assert.Equal(honoluluDate, snapshot.LocalDate);
            Assert.Equal("Pacific/Honolulu", snapshot.TimeZoneId);
            Assert.Equal(
                ReservationOperationsDateSource.PropertyTimeZone,
                snapshot.DateSource);
            Assert.Equal(Observation, snapshot.ObservedAtUtc);
            Assert.Equal(
                new(4, 10),
                snapshot.Cohorts.ConfirmedArrivalsOnLocalDate);
            Assert.Equal(
                new(2, 9),
                snapshot.Cohorts.ScheduledDeparturesOnLocalDate);
            Assert.Equal(new(3, 15), snapshot.Cohorts.CurrentlyInHouse);
            Assert.Equal(new(101, 201), snapshot.Attention.PendingAllocation);
            Assert.Equal(new(1, 7), snapshot.Attention.AllocationRejected);
            Assert.Equal(new(1, 8), snapshot.Attention.CancellationPending);
            Assert.Equal(new(1, 9), snapshot.Attention.NoShowPending);
            Assert.Equal(new(1, 5), snapshot.Attention.CheckoutPending);
            Assert.Equal(
                new(1, 3),
                snapshot.Attention.ArrivalBeforeLocalDateStillConfirmed);
            Assert.Equal(
                new(1, 6),
                snapshot.Attention.DepartureBeforeLocalDateStillInHouse);
            Assert.Equal(new(107, 239), snapshot.Attention.Total);
            Assert.Equal(50, snapshot.Upcoming.Count);
            Assert.Equal(50, snapshot.UpcomingLimit);
            Assert.True(snapshot.HasMoreUpcoming);
            Assert.Equal(
                Enumerable.Range(1, 4).Select(UpcomingId),
                snapshot.Upcoming.Take(4).Select(item => item.ReservationId));
            Assert.All(
                snapshot.Upcoming.Take(4),
                item => Assert.True(item.HoldsInventory));
            Assert.False(
                snapshot.Upcoming.First(item =>
                    item.Status == ReservationStatus.PendingAllocation)
                    .HoldsInventory);

            Assert.Equal(IsolationLevel.RepeatableRead, capture.IsolationLevel);
            Assert.Collection(
                capture.SelectCommands,
                property => Assert.Contains(
                    "property_projection",
                    property,
                    StringComparison.Ordinal),
                aggregate =>
                {
                    Assert.Contains(
                        "GROUP BY",
                        aggregate,
                        StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(
                        "ORDER BY",
                        aggregate,
                        StringComparison.OrdinalIgnoreCase);
                },
                upcoming =>
                {
                    Assert.Contains(
                        "ORDER BY",
                        upcoming,
                        StringComparison.OrdinalIgnoreCase);
                    Assert.Contains(
                        "ExpectedArrivalTime\" IS NULL",
                        upcoming,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        "LIMIT",
                        upcoming,
                        StringComparison.OrdinalIgnoreCase);
                });
        }

        await using (ServiceProvider provider = CreateProvider(connectionString))
        {
            ReservationOperationsSnapshotDto three = AssertFound(await ReadAsync(
                provider,
                honoluluPropertyId,
                explicitLocalDate: honoluluDate,
                upcomingLimit: 3));
            Assert.Equal(
                ReservationOperationsDateSource.Explicit,
                three.DateSource);
            Assert.Equal("Pacific/Honolulu", three.TimeZoneId);
            Assert.Equal(
                Enumerable.Range(1, 3).Select(UpcomingId),
                three.Upcoming.Select(item => item.ReservationId));
            Assert.True(three.HasMoreUpcoming);

            ReservationOperationsSnapshotDto historicalReference =
                AssertFound(await ReadAsync(
                    provider,
                    honoluluPropertyId,
                    explicitLocalDate: honoluluDate.AddDays(-10),
                    upcomingLimit: 0));
            Assert.Equal(
                new(3, 15),
                historicalReference.Cohorts.CurrentlyInHouse);
            Assert.Equal(
                new(0, 0),
                historicalReference.Attention
                    .DepartureBeforeLocalDateStillInHouse);

            ReservationOperationsSnapshotDto futureReference =
                AssertFound(await ReadAsync(
                    provider,
                    honoluluPropertyId,
                    explicitLocalDate: honoluluDate.AddDays(20),
                    upcomingLimit: 0));
            Assert.Equal(
                new(3, 15),
                futureReference.Cohorts.CurrentlyInHouse);
            Assert.Equal(
                new(5, 13),
                futureReference.Attention
                    .ArrivalBeforeLocalDateStillConfirmed);
            Assert.Equal(
                new(2, 10),
                futureReference.Attention
                    .DepartureBeforeLocalDateStillInHouse);

            ReservationOperationsSnapshotDto zero = AssertFound(await ReadAsync(
                provider,
                honoluluPropertyId,
                explicitLocalDate: honoluluDate,
                upcomingLimit: 0));
            Assert.Empty(zero.Upcoming);
            Assert.True(zero.HasMoreUpcoming);

            ReservationOperationsSnapshotDto kiritimati = AssertFound(
                await ReadAsync(
                    provider,
                    kiritimatiPropertyId,
                    explicitLocalDate: null,
                    upcomingLimit: 0));
            Assert.Equal(new DateOnly(2026, 8, 14), kiritimati.LocalDate);
            Assert.Equal("Pacific/Kiritimati", kiritimati.TimeZoneId);

            ReservationOperationsSnapshotDto utc = AssertFound(await ReadAsync(
                provider,
                utcPropertyId,
                explicitLocalDate: null,
                upcomingLimit: 0));
            Assert.Equal(honoluluDate, utc.LocalDate);
            Assert.Equal("UTC", utc.TimeZoneId);

            Assert.Equal(
                ReservationOperationsSnapshotReadStatus.PropertyNotFound,
                (await ReadAsync(
                    provider,
                    Guid.Parse("01000000-0000-0000-0000-000000000099"),
                    explicitLocalDate: honoluluDate,
                    upcomingLimit: 0)).Status);
            Assert.Equal(
                ReservationOperationsSnapshotReadStatus.PropertyNotFound,
                (await ReadAsync(
                    provider,
                    policyOnlyPropertyId,
                    explicitLocalDate: honoluluDate,
                    upcomingLimit: 0)).Status);
            Assert.Equal(
                ReservationOperationsSnapshotReadStatus.PropertyNotFound,
                (await ReadAsync(
                    provider,
                    unknownPropertyId,
                    explicitLocalDate: honoluluDate,
                    upcomingLimit: 0)).Status);
            Assert.Equal(
                ReservationOperationsSnapshotReadStatus.PropertyInactive,
                (await ReadAsync(
                    provider,
                    inactivePropertyId,
                    explicitLocalDate: honoluluDate,
                    upcomingLimit: 0)).Status);
            Assert.Equal(
                ReservationOperationsSnapshotReadStatus
                    .PropertyTimeZoneUnavailable,
                (await ReadAsync(
                    provider,
                    invalidTimeZonePropertyId,
                    explicitLocalDate: honoluluDate,
                    upcomingLimit: 0)).Status);
            Assert.Equal(
                ReservationOperationsSnapshotReadStatus
                    .PropertyTimeZoneUnavailable,
                (await ReadAsync(
                    provider,
                    windowsTimeZonePropertyId,
                    explicitLocalDate: honoluluDate,
                    upcomingLimit: 0)).Status);
        }

        await AssertRepeatableReadSnapshotAsync(connectionString);
    }

    private static async Task AssertRepeatableReadSnapshotAsync(
        string connectionString)
    {
        Guid propertyId = Guid.Parse(
            "02000000-0000-0000-0000-000000000001");
        DateOnly honoluluDate = new(2026, 8, 13);
        ReservationSeed original = new(
            Guid.Parse("02000000-0000-0000-0000-000000000002"),
            propertyId,
            ReservationState.PendingAllocation,
            honoluluDate,
            honoluluDate.AddDays(2),
            GuestCount: 2,
            ExpectedArrivalTime: new(9, 0),
            CreatedAtUtc: new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await using (ReservationsDbContext seed = CreateDbContext(
            connectionString))
        {
            await SeedPropertyAsync(
                seed,
                propertyId,
                "Pacific/Honolulu",
                isKnown: true,
                isActive: true);
            await SeedReservationAsync(seed, original);
        }

        PropertyReadBarrierInterceptor barrier = new();
        await using ServiceProvider provider = CreateProvider(
            connectionString,
            barrier);
        Task<ReservationOperationsSnapshotReadResult> inFlight = ReadAsync(
            provider,
            propertyId,
            explicitLocalDate: null,
            upcomingLimit: 10);
        await barrier.PropertyRead.WaitAsync(TimeSpan.FromSeconds(20));

        ReservationSeed concurrent = new(
            Guid.Parse("02000000-0000-0000-0000-000000000003"),
            propertyId,
            ReservationState.PendingAllocation,
            honoluluDate.AddDays(1),
            honoluluDate.AddDays(3),
            GuestCount: 3,
            ExpectedArrivalTime: new(8, 0),
            CreatedAtUtc: Observation);
        try
        {
            await using ReservationsDbContext writer = CreateDbContext(
                connectionString);
            await using IDbContextTransaction transaction =
                await writer.Database.BeginTransactionAsync();
            await writer.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.property_projection
                SET "TimeZoneId" = {"Pacific/Kiritimati"},
                    "TopologySourceVersion" = "TopologySourceVersion" + 1
                WHERE "Id" = {propertyId};
                """);
            await SeedReservationAsync(writer, concurrent);
            await transaction.CommitAsync();
        }
        finally
        {
            barrier.Release();
        }

        ReservationOperationsSnapshotDto first = AssertFound(await inFlight);
        Assert.Equal(IsolationLevel.RepeatableRead, barrier.IsolationLevel);
        Assert.Equal("Pacific/Honolulu", first.TimeZoneId);
        Assert.Equal(honoluluDate, first.LocalDate);
        Assert.Equal(new(1, 2), first.Attention.PendingAllocation);
        Assert.Equal(
            [original.Id],
            first.Upcoming.Select(item => item.ReservationId));

        await using ServiceProvider laterProvider = CreateProvider(
            connectionString);
        ReservationOperationsSnapshotDto later = AssertFound(await ReadAsync(
            laterProvider,
            propertyId,
            explicitLocalDate: null,
            upcomingLimit: 10));
        Assert.Equal("Pacific/Kiritimati", later.TimeZoneId);
        Assert.Equal(honoluluDate.AddDays(1), later.LocalDate);
        Assert.Equal(new(2, 5), later.Attention.PendingAllocation);
        Assert.Equal(
            [concurrent.Id],
            later.Upcoming.Select(item => item.ReservationId));
    }

    private static List<ReservationSeed> CreateStatusMatrix(
        Guid propertyId,
        DateOnly localDate)
    {
        List<ReservationSeed> reservations =
        [
            new(UpcomingId(1), propertyId, ReservationState.Confirmed,
                localDate, localDate.AddDays(2), 1, new(7, 0)),
            new(UpcomingId(2), propertyId, ReservationState.Confirmed,
                localDate, localDate.AddDays(2), 2, new(8, 0)),
            new(UpcomingId(3), propertyId, ReservationState.Confirmed,
                localDate, localDate.AddDays(2), 3, new(8, 0)),
            new(UpcomingId(4), propertyId, ReservationState.Confirmed,
                localDate, localDate.AddDays(2), 4, ExpectedArrivalTime: null),
            new(Guid.Parse("03000000-0000-0000-0000-000000000001"),
                propertyId, ReservationState.Confirmed, localDate.AddDays(-1),
                localDate.AddDays(1), 3),
            new(Guid.Parse("03000000-0000-0000-0000-000000000002"),
                propertyId, ReservationState.CheckedIn, localDate.AddDays(-2),
                localDate, 4),
            new(Guid.Parse("03000000-0000-0000-0000-000000000003"),
                propertyId, ReservationState.CheckoutPending,
                localDate.AddDays(-3), localDate, 5),
            new(Guid.Parse("03000000-0000-0000-0000-000000000004"),
                propertyId, ReservationState.CheckedIn, localDate.AddDays(-4),
                localDate.AddDays(-1), 6),
            new(Guid.Parse("03000000-0000-0000-0000-000000000005"),
                propertyId, ReservationState.AllocationRejected,
                localDate.AddDays(2), localDate.AddDays(3), 7),
            new(Guid.Parse("03000000-0000-0000-0000-000000000006"),
                propertyId, ReservationState.CancellationPending,
                localDate.AddDays(2), localDate.AddDays(3), 8),
            new(Guid.Parse("03000000-0000-0000-0000-000000000007"),
                propertyId, ReservationState.NoShowPending,
                localDate.AddDays(-1), localDate.AddDays(1), 9),
            new(Guid.Parse("03000000-0000-0000-0000-000000000008"),
                propertyId, ReservationState.Cancelled,
                localDate.AddDays(-5), localDate.AddDays(-3), 10),
            new(Guid.Parse("03000000-0000-0000-0000-000000000009"),
                propertyId, ReservationState.NoShow,
                localDate.AddDays(-5), localDate.AddDays(-3), 11),
            new(Guid.Parse("03000000-0000-0000-0000-000000000010"),
                propertyId, ReservationState.CheckedOut,
                localDate.AddDays(-5), localDate.AddDays(-3), 12),
            new(Guid.Parse("03000000-0000-0000-0000-000000000011"),
                propertyId, ReservationState.PendingAllocation,
                localDate, localDate.AddDays(2), 50,
                Projection: ProjectionVisibility.Restricted),
            new(Guid.Parse("03000000-0000-0000-0000-000000000012"),
                propertyId, ReservationState.Confirmed,
                localDate, localDate.AddDays(2), 50,
                Projection: ProjectionVisibility.Unsupported),
            new(Guid.Parse("03000000-0000-0000-0000-000000000013"),
                propertyId, ReservationState.Confirmed,
                localDate, localDate.AddDays(2), 50,
                Projection: ProjectionVisibility.Missing),
            new(Guid.Parse("03000000-0000-0000-0000-000000000014"),
                propertyId, ReservationState.AllocationRejected,
                localDate.AddDays(2), localDate.AddDays(3), 50,
                IsAnonymised: true)
        ];

        DateTimeOffset oldest = new(2018, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int index = 0; index < 101; index++)
        {
            reservations.Add(new(
                Guid.Parse($"10000000-0000-0000-0000-{index + 1:D12}"),
                propertyId,
                ReservationState.PendingAllocation,
                localDate.AddDays(10),
                localDate.AddDays(12),
                (index % 3) + 1,
                ExpectedArrivalTime: null,
                CreatedAtUtc: oldest.AddMinutes(index)));
        }

        return reservations;
    }

    private static Guid UpcomingId(int ordinal) => Guid.Parse(
        $"00000000-0000-0000-0000-{ordinal:D12}");

    private static async Task<ReservationOperationsSnapshotReadResult> ReadAsync(
        ServiceProvider provider,
        Guid propertyId,
        DateOnly? explicitLocalDate,
        int upcomingLimit)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IReservationOperationsSnapshotReader>()
            .ReadAsync(
                propertyId,
                explicitLocalDate,
                Observation,
                upcomingLimit,
                CancellationToken.None);
    }

    private static ReservationOperationsSnapshotDto AssertFound(
        ReservationOperationsSnapshotReadResult read)
    {
        Assert.Equal(ReservationOperationsSnapshotReadStatus.Found, read.Status);
        return Assert.IsType<ReservationOperationsSnapshotDto>(read.Snapshot);
    }

    private static async Task SeedPropertyAsync(
        ReservationsDbContext context,
        Guid propertyId,
        string timeZoneId,
        bool isKnown,
        bool isActive,
        long topologySourceVersion = 1)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.property_projection (
                "Id", "ScopeId", "TimeZoneId", "IsActive", "IsKnown",
                "TopologySourceVersion")
            VALUES (
                {propertyId}, {TenantId}, {timeZoneId}, {isActive}, {isKnown},
                {topologySourceVersion});
            """);
    }

    private static async Task SeedReservationAsync(
        ReservationsDbContext context,
        ReservationSeed seed)
    {
        DateTimeOffset createdAtUtc = seed.CreatedAtUtc ?? Observation.AddDays(-1);
        bool checkedIn = seed.Status is
            ReservationState.CheckedIn or
            ReservationState.CheckoutPending or
            ReservationState.CheckedOut;
        bool pendingStay = seed.Status is
            ReservationState.NoShowPending or
            ReservationState.CheckoutPending;
        bool terminal = seed.Status is
            ReservationState.AllocationRejected or
            ReservationState.Cancelled or
            ReservationState.NoShow or
            ReservationState.CheckedOut;
        bool allocated = seed.Status is not (
            ReservationState.PendingAllocation or
            ReservationState.AllocationRejected);
        Guid? allocationId = allocated ? Guid.NewGuid() : null;
        long? allocationVersion = allocated ? 1L : null;
        int? allocationRejection = seed.Status ==
            ReservationState.AllocationRejected
                ? (int)ReservationAllocationRejection.AllocationConflict
                : null;
        Guid? releaseRequestId = seed.Status is
            ReservationState.CancellationPending or
            ReservationState.NoShowPending or
            ReservationState.CheckoutPending
                ? Guid.NewGuid()
                : null;
        DateOnly? checkedInBusinessDate = checkedIn ? seed.Arrival : null;
        DateTimeOffset? checkedInAtUtc = checkedIn ? createdAtUtc : null;
        string? checkedInBy = checkedIn ? "staff:check-in" : null;
        DateOnly? pendingStayBusinessDate = pendingStay ? seed.Arrival : null;
        string? pendingStayActorId = pendingStay ? "staff:pending-stay" : null;
        DateOnly? noShowBusinessDate = seed.Status == ReservationState.NoShow
            ? seed.Arrival
            : null;
        DateTimeOffset? noShowAtUtc = seed.Status == ReservationState.NoShow
            ? createdAtUtc
            : null;
        string? noShowBy = seed.Status == ReservationState.NoShow
            ? "staff:no-show"
            : null;
        DateOnly? checkedOutBusinessDate = seed.Status ==
            ReservationState.CheckedOut
                ? seed.Departure
                : null;
        DateTimeOffset? checkedOutAtUtc = seed.Status ==
            ReservationState.CheckedOut
                ? createdAtUtc
                : null;
        string? checkedOutBy = seed.Status == ReservationState.CheckedOut
            ? "staff:check-out"
            : null;
        DateTimeOffset? terminalAtUtc = terminal ? createdAtUtc : null;
        DateTimeOffset? anonymisedAtUtc = seed.IsAnonymised
            ? createdAtUtc
            : null;
        string primaryGuestName = seed.IsAnonymised
            ? Reservation.AnonymisedGuestName
            : $"Guest {seed.Id:N}";

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.reservations (
                "Id", "PropertyId", "AllocationRequestId", "AllocationId",
                "AllocationVersion", "AllocationRejection", "ReleaseRequestId",
                "Arrival", "Departure", "ExpectedArrivalTime",
                "PrimaryGuestName", "PrimaryGuestNameSearch", "GuestCount",
                "Source", "Status", "Version", "CreatedAtUtc", "ScopeId",
                "LastDetailsChangedAtUtc", "CheckedInBusinessDate",
                "CheckedInAtUtc", "CheckedInBy", "PendingStayBusinessDate",
                "PendingStayActorId", "NoShowBusinessDate", "NoShowAtUtc",
                "NoShowBy", "CheckedOutBusinessDate", "CheckedOutAtUtc",
                "CheckedOutBy", "TerminalAtUtc", "AnonymisedAtUtc",
                "IsAnonymised")
            VALUES (
                {seed.Id}, {seed.PropertyId}, {Guid.NewGuid()}, {allocationId},
                {allocationVersion}, {allocationRejection}, {releaseRequestId},
                {seed.Arrival}, {seed.Departure}, {seed.ExpectedArrivalTime},
                {primaryGuestName}, {primaryGuestName.ToUpperInvariant()},
                {seed.GuestCount}, {(int)ReservationSource.Direct},
                {(int)seed.Status}, {1L}, {createdAtUtc}, {TenantId},
                {createdAtUtc}, {checkedInBusinessDate}, {checkedInAtUtc},
                {checkedInBy}, {pendingStayBusinessDate}, {pendingStayActorId},
                {noShowBusinessDate}, {noShowAtUtc}, {noShowBy},
                {checkedOutBusinessDate}, {checkedOutAtUtc}, {checkedOutBy},
                {terminalAtUtc}, {anonymisedAtUtc}, {seed.IsAnonymised});
            """);

        if (seed.Projection == ProjectionVisibility.Missing)
        {
            return;
        }

        bool restricted = seed.Projection == ProjectionVisibility.Restricted;
        int contractVersion = seed.Projection == ProjectionVisibility.Unsupported
            ? ReservationProcessingRestrictionContract.CurrentVersion + 1
            : ReservationProcessingRestrictionContract.CurrentVersion;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.reservation_processing_restriction_state (
                "ScopeId", "PropertyId", "ReservationId", "ContractVersion",
                "Revision", "ActiveRestrictionCount", "IsRestricted",
                "LastTransitionAtUtc")
            VALUES (
                {TenantId}, {seed.PropertyId}, {seed.Id}, {contractVersion},
                {(restricted ? 1L : 0L)}, {(restricted ? 1 : 0)}, {restricted},
                {createdAtUtc});
            """);
    }

    private static ReservationsDbContext CreateDbContext(
        string connectionString)
    {
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(ReservationsMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        ReservationsMigrations.HistoryTable,
                        ReservationsMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private static ServiceProvider CreateProvider(
        string connectionString,
        IInterceptor? interceptor = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(new TestScopeContext());
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            OpenWorkspaceTerminationFenceReader.Instance);
        builder.Services.AddDbContext<ReservationsDbContext>(options =>
        {
            options.UseNpgsql(connectionString, provider => provider
                .MigrationsAssembly(ReservationsMigrations.PostgreSqlAssembly)
                .MigrationsHistoryTable(
                    ReservationsMigrations.HistoryTable,
                    ReservationsMigrations.Schema));
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });
        builder.AddReservationsPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private sealed class QueryCaptureInterceptor : DbCommandInterceptor
    {
        private readonly List<string> selectCommands = [];

        public IReadOnlyCollection<string> SelectCommands =>
            this.selectCommands.AsReadOnly();
        public IsolationLevel? IsolationLevel { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith(
                    "SELECT",
                    StringComparison.OrdinalIgnoreCase))
            {
                this.selectCommands.Add(command.CommandText);
                this.IsolationLevel ??= eventData.Context?.Database
                    .CurrentTransaction?.GetDbTransaction().IsolationLevel;
            }

            return base.ReaderExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }

    private sealed class PropertyReadBarrierInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource propertyRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int intercepted;

        public Task PropertyRead => this.propertyRead.Task;
        public IsolationLevel? IsolationLevel { get; private set; }

        public void Release() => this.release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "property_projection",
                    StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref this.intercepted, 1, 0) == 0)
            {
                this.IsolationLevel = eventData.Context?.Database
                    .CurrentTransaction?.GetDbTransaction().IsolationLevel;
                this.propertyRead.TrySetResult();
                await this.release.Task.WaitAsync(cancellationToken);
            }

            return await base.ReaderExecutedAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed record ReservationSeed(
        Guid Id,
        Guid PropertyId,
        ReservationState Status,
        DateOnly Arrival,
        DateOnly Departure,
        int GuestCount,
        TimeOnly? ExpectedArrivalTime = null,
        DateTimeOffset? CreatedAtUtc = null,
        ProjectionVisibility Projection = ProjectionVisibility.Allowed,
        bool IsAnonymised = false);

    private enum ProjectionVisibility
    {
        Allowed,
        Restricted,
        Unsupported,
        Missing
    }
}

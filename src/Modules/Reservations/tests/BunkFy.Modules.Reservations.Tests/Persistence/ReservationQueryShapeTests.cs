namespace BunkFy.Modules.Reservations.Tests.Persistence;

using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Reservations.Persistence.Repositories;
using BunkFy.Modules.Reservations.Contracts.Stations;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationQueryShapeTests
{
    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public void Aggregate_graph_explicitly_uses_split_query_execution(
        string provider)
    {
        using ReservationsDbContext dbContext = CreateRelationalContext(provider);

        string sql = dbContext.Reservations
            .WithAggregateGraph()
            .Where(reservation => reservation.Id == Guid.NewGuid())
            .ToQueryString();

        Assert.Contains("split-query", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public void Creation_replay_graph_excludes_guest_history(string provider)
    {
        using ReservationsDbContext dbContext = CreateRelationalContext(provider);

        string sql = dbContext.Reservations
            .WithCreationReplayGraph()
            .Where(reservation => reservation.Id == Guid.NewGuid())
            .ToQueryString();

        Assert.Contains("requested_inventory_units", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reservation_guests", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public void Primary_management_query_does_not_include_station_attribution(string provider)
    {
        using ReservationsDbContext db = CreateRelationalContext(provider);
        var repository = new ReservationManagementOperationRepository(db);
        string sql = repository.ParentQuery.Where(x => x.ReservationId == Guid.NewGuid()).ToQueryString();
        Assert.Contains("management_operations", sql, StringComparison.Ordinal);
        Assert.Contains("ScopeId", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("station_attributions", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSql_attribution_query_is_narrow_and_scoped()
    {
        using ReservationsDbContext db = CreateRelationalContext("PostgreSql");
        var repository = new ReservationManagementOperationRepository(db);
        Assert.NotNull(repository.AttributionQuery);
        string sql = repository.AttributionQuery
            .Where(x => x.ReservationId == Guid.NewGuid() && x.OperationId == Guid.NewGuid()).ToQueryString();
        Assert.Contains("station_attributions", sql, StringComparison.Ordinal);
        Assert.Contains("ScopeId", sql, StringComparison.Ordinal);
        Assert.Contains("OperationId", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServer_station_reads_fail_unsupported_before_database_access()
    {
        await using ReservationsDbContext db = CreateRelationalContext("SqlServer");
        var repository = new ReservationManagementOperationRepository(db);
        Assert.Null(repository.AttributionQuery);
        var result = await repository.GetStationAttributionAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.False(result.Supported);
        Assert.Null(result.Provenance);
        var arrivals = new StationDueArrivalRepository(db);
        Assert.False(arrivals.SupportsStationOperations);
        var page = await arrivals.ListAsync(Guid.NewGuid(), new DateOnly(2026, 9, 28), 25, null, CancellationToken.None);
        Assert.Equal(StationReservationState.Unsupported, page.State);
        Assert.Empty(page.Items);
        Assert.Null(await arrivals.FindAsync(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 9, 28), CancellationToken.None));
    }

    [Theory]
    [InlineData("PostgreSql", true)]
    [InlineData("SqlServer", false)]
    public void Tenant_management_export_translates_order_and_only_supported_provider_joins_attribution(string provider, bool attributed)
    {
        using ReservationsDbContext db = CreateRelationalContext(provider);
        var contributor = new ReservationsTenantTerminationContributor(db, new TestScopeContext(), new TestClock());
        string sql = contributor.ManagementExportQuery("tenant-query-shape").ToQueryString();
        Assert.Contains("management_operations", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ScopeId", sql, StringComparison.Ordinal);
        Assert.Equal(attributed, sql.Contains("station_attributions", StringComparison.Ordinal));
        Assert.Equal(attributed, sql.Contains("LEFT JOIN", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    }

    private static ReservationsDbContext CreateRelationalContext(string provider)
    {
        DbContextOptionsBuilder<ReservationsDbContext> builder =
            new();
        switch (provider)
        {
            case "PostgreSql":
                builder.UseNpgsql(
                    "Host=localhost;Database=bunkfy_query_shape;" +
                    "Username=query_shape;Password=not-used");
                break;
            case "SqlServer":
                builder.UseSqlServer(
                    "Server=localhost;Database=BunkFyQueryShape;" +
                    "User Id=query-shape;Password=not-used;" +
                    "TrustServerCertificate=True");
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(provider),
                    provider,
                    "Unsupported query-shape test provider.");
        }

        builder.ConfigureWarnings(warnings => warnings.Throw(
            RelationalEventId.MultipleCollectionIncludeWarning));
        return new(builder.Options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-query-shape";
    }
}

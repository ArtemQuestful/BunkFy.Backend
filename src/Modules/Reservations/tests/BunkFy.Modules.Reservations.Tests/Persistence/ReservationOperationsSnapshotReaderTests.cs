namespace BunkFy.Modules.Reservations.Tests.Persistence;

using System.Data;
using BunkFy.Modules.Reservations.Persistence.Repositories;
using Xunit;

public sealed class ReservationOperationsSnapshotReaderTests
{
    [Theory]
    [InlineData(
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        IsolationLevel.RepeatableRead)]
    [InlineData(
        "Microsoft.EntityFrameworkCore.SqlServer",
        IsolationLevel.Serializable)]
    public void Snapshot_isolation_is_provider_correct(
        string providerName,
        IsolationLevel expected)
    {
        Assert.Equal(
            expected,
            ReservationOperationsSnapshotReader
                .ResolveRelationalSnapshotIsolation(providerName));
    }

    [Fact]
    public void Unknown_relational_provider_fails_closed()
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ReservationOperationsSnapshotReader
                .ResolveRelationalSnapshotIsolation("Unsupported.Provider"));

        Assert.Contains("does not support", failure.Message, StringComparison.Ordinal);
    }
}

namespace BunkFy.Modules.Ingestion.Tests.Persistence;

using BunkFy.Modules.Ingestion.Persistence;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class IngestionExecutionLockTests
{
    [Fact]
    public async Task In_memory_provider_accepts_valid_execution_coordinates()
    {
        await using IngestionDbContext dbContext = CreateInMemoryDbContext();
        IngestionExecutionLock executionLock = new(dbContext);

        await executionLock.AcquireTaskExecutionAsync(
            "tenant-a",
            Guid.NewGuid(),
            1,
            CancellationToken.None);
        await executionLock.AcquireRetentionExecutionAsync(
            "tenant-a",
            Guid.NewGuid(),
            CancellationToken.None);
        await executionLock.AcquireConnectionReadAsync(
            "tenant-a",
            Guid.NewGuid(),
            CancellationToken.None);
        await executionLock.AcquireConnectionWriteAsync(
            "tenant-a",
            Guid.NewGuid(),
            CancellationToken.None);
        await executionLock.AcquireRunReadAsync(
            "tenant-a",
            Guid.NewGuid(),
            CancellationToken.None);
        await executionLock.AcquireRunWriteAsync(
            "tenant-a",
            Guid.NewGuid(),
            CancellationToken.None);
    }

    [Fact]
    public async Task Invalid_or_cross_scope_coordinates_fail_closed()
    {
        await using IngestionDbContext dbContext = CreateInMemoryDbContext();
        IngestionExecutionLock executionLock = new(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executionLock.AcquireConnectionReadAsync(
                "tenant-b",
                Guid.NewGuid(),
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executionLock.AcquireRunWriteAsync(
                "tenant-a",
                Guid.Empty,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executionLock.AcquireTaskExecutionAsync(
                "tenant-a",
                Guid.NewGuid(),
                0,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executionLock.AcquireRetentionExecutionAsync(
                "tenant-b",
                Guid.NewGuid(),
                CancellationToken.None));
    }

    [Fact]
    public async Task Relational_provider_requires_an_active_transaction()
    {
        await using IngestionDbContext dbContext = new(
            new DbContextOptionsBuilder<IngestionDbContext>()
                .UseNpgsql(
                    "Host=127.0.0.1;Port=1;Database=unused;" +
                    "Username=unused;Password=unused")
                .Options,
            new TestScope());
        IngestionExecutionLock executionLock = new(dbContext);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executionLock.AcquireConnectionWriteAsync(
                    "tenant-a",
                    Guid.NewGuid(),
                    CancellationToken.None));

        Assert.Equal(
            "An Ingestion execution lock requires an active database transaction.",
            failure.Message);
    }

    private static IngestionDbContext CreateInMemoryDbContext() => new(
        new DbContextOptionsBuilder<IngestionDbContext>()
            .UseInMemoryDatabase($"ingestion-execution-lock-{Guid.NewGuid():N}")
            .Options,
        new TestScope());

    private sealed class TestScope : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}

namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingRetentionExecutionLockTests
{
    private const string TenantId = "tenant-a";

    [Fact]
    public async Task Valid_non_relational_coordinate_is_a_no_op()
    {
        await using WorkspacesDbContext dbContext = CreateInMemoryDbContext();
        WorkspaceStaffOnboardingRetentionExecutionLock executionLock =
            new(dbContext);

        await executionLock.AcquireAsync(
            TenantId,
            Guid.NewGuid(),
            CancellationToken.None);
    }

    [Fact]
    public async Task Invalid_or_cross_tenant_coordinates_fail_closed()
    {
        await using WorkspacesDbContext dbContext = CreateInMemoryDbContext();
        WorkspaceStaffOnboardingRetentionExecutionLock executionLock =
            new(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executionLock.AcquireAsync(
                "tenant-b",
                Guid.NewGuid(),
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executionLock.AcquireAsync(
                TenantId,
                Guid.Empty,
                CancellationToken.None));
    }

    [Fact]
    public async Task Relational_lock_requires_an_active_transaction()
    {
        await using WorkspacesDbContext dbContext = new(
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=unused;Username=unused;Password=unused")
                .Options,
            new TestScopeContext());
        WorkspaceStaffOnboardingRetentionExecutionLock executionLock =
            new(dbContext);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executionLock.AcquireAsync(
                    TenantId,
                    Guid.NewGuid(),
                    CancellationToken.None));

        Assert.Contains(
            "active database transaction",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static WorkspacesDbContext CreateInMemoryDbContext() => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options,
        new TestScopeContext());

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}

namespace BunkFy.Modules.Staff.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffOperationalIdentityReaderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reader_returns_only_the_exact_tenant_identity_state()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        StaffMember staff = StaffMember.Create(
            Guid.NewGuid(),
            "tenant-a",
            "Former owner",
            null,
            null,
            null,
            null,
            null,
            null,
            "subject-a",
            "user:owner",
            Guid.NewGuid(),
            Now).Value;
        Assert.True(staff.Suspend(
            staff.Version,
            "user:owner",
            "Review",
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        dbContext.StaffMembers.Add(staff);
        await dbContext.SaveChangesAsync();
        StaffOperationalIdentityReader reader = new(dbContext);

        StaffOperationalIdentitySnapshot? snapshot = await reader.FindAsync(
            "tenant-a",
            "subject-a",
            CancellationToken.None);

        StaffOperationalIdentitySnapshot exact =
            Assert.IsType<StaffOperationalIdentitySnapshot>(snapshot);
        Assert.Equal(staff.Id, exact.StaffMemberId);
        Assert.Equal("subject-a", exact.AuthSubjectId);
        Assert.Equal(StaffStatus.Suspended, exact.Status);
        Assert.Equal(staff.Version, exact.Version);
        Assert.Null(await reader.FindAsync(
            "tenant-b",
            "subject-a",
            CancellationToken.None));
        Assert.Null(await reader.FindAsync(
            "tenant-a",
            "missing",
            CancellationToken.None));
    }

    private static StaffDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<StaffDbContext>()
            .UseInMemoryDatabase(
                $"staff-operational-identity-{Guid.NewGuid():N}")
            .Options,
        new TestScopeContext());

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}

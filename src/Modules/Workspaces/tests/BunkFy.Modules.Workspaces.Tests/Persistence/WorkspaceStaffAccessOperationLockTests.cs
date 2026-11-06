namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffAccessOperationLockTests
{
    [Fact]
    public async Task Non_relational_coordinate_locks_report_exact_existence()
    {
        await using WorkspacesDbContext dbContext = CreateDbContext();
        WorkspaceStaffAccessProcess process = CreateProcess();
        dbContext.StaffAccessProcesses.Add(process);
        await dbContext.SaveChangesAsync();
        WorkspaceStaffAccessOperationLock operationLock = new(dbContext);

        bool existing = await operationLock.TryAcquireProcessAsync(
            process.Id,
            CancellationToken.None);
        bool missing = await operationLock.TryAcquireProcessAsync(
            Guid.NewGuid(),
            CancellationToken.None);
        bool existingVersion = await operationLock.TryAcquireStaffVersionAsync(
            process.StaffMemberId,
            process.TargetStaffVersion,
            CancellationToken.None);
        bool missingVersion = await operationLock.TryAcquireStaffVersionAsync(
            process.StaffMemberId,
            process.TargetStaffVersion + 1,
            CancellationToken.None);
        await operationLock.AcquireSubjectAsync(
            process.SubjectId,
            CancellationToken.None);
        await operationLock.AcquireCoordinatesAsync(
            process.StaffMemberId,
            process.SubjectId,
            CancellationToken.None);

        Assert.True(existing);
        Assert.False(missing);
        Assert.True(existingVersion);
        Assert.False(missingVersion);
    }

    [Fact]
    public async Task Empty_coordinates_fail_closed()
    {
        await using WorkspacesDbContext dbContext = CreateDbContext();
        WorkspaceStaffAccessOperationLock operationLock = new(dbContext);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            operationLock.AcquireSubjectAsync(
                " ",
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            operationLock.AcquireSubjectAsync(
                new string('s', WorkspaceStaffAccessProcess.SubjectIdMaxLength + 1),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            operationLock.AcquireStaffAsync(
                Guid.Empty,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            operationLock.AcquireCoordinatesAsync(
                Guid.Empty,
                "subject-a",
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            operationLock.AcquireCoordinatesAsync(
                Guid.NewGuid(),
                " ",
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            operationLock.TryAcquireProcessAsync(
                Guid.Empty,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            operationLock.TryAcquireStaffVersionAsync(
                Guid.Empty,
                targetStaffVersion: 2,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            operationLock.TryAcquireStaffVersionAsync(
                Guid.NewGuid(),
                targetStaffVersion: 0,
                CancellationToken.None));
    }

    private static WorkspacesDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options,
        new TestScopeContext());

    private static WorkspaceStaffAccessProcess CreateProcess() =>
        WorkspaceStaffAccessProcess.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "subject-a",
            WorkspaceStaffAccessTargetState.Suspended,
            targetStaffVersion: 2,
            new DateOnly(2026, 8, 6),
            "user:owner",
            [],
            new DateTimeOffset(
                2026,
                8,
                6,
                14,
                0,
                0,
                TimeSpan.Zero)).Value;

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}

namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffIdentityAnchorCutoverSourceReaderTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task Full_universe_is_keyset_paged_with_trimmed_subjects_and_null_targets()
    {
        int count = WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize +
            3;
        Guid[] completedIds = Enumerable.Range(1, count)
            .Select(CreateDeterministicGuid)
            .OrderBy(id => id)
            .ToArray();
        await using WorkspacesDbContext context = CreateContext();
        context.StaffOnboardingApplications.AddRange(
            completedIds
                .Reverse()
                .Select((id, index) =>
                    CreateCompletedApplication(id, index + 1)));

        WorkspaceStaffOnboarding excluded = CreateApplication(
            CreateDeterministicGuid(count + 1),
            count + 1);
        Assert.True(excluded.Supersede(Now.AddMinutes(1)).IsSuccess);
        context.StaffOnboardingApplications.Add(excluded);
        await context.SaveChangesAsync();
        Guid[] expectedIds = completedIds
            .Append(excluded.Id)
            .Order()
            .ToArray();
        WorkspaceStaffIdentityAnchorCutoverSourceReader reader = new(context);

        WorkspaceStaffIdentityAnchorSourcePage first = await reader
            .ListRelevantPageAsync(
                afterApplicationId: null,
                WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize,
                CancellationToken.None);
        WorkspaceStaffIdentityAnchorSourcePage second = await reader
            .ListRelevantPageAsync(
                first.NextApplicationId,
                WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize,
                CancellationToken.None);
        WorkspaceStaffIdentityAnchorSourcePage terminal = await reader
            .ListRelevantPageAsync(
                second.NextApplicationId,
                WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize,
                CancellationToken.None);

        Assert.Equal(
            expectedIds.Take(
                WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize),
            first.Records.Select(record => record.ApplicationId));
        Assert.True(first.HasMore);
        Assert.Equal(first.Records[^1].ApplicationId, first.NextApplicationId);
        Assert.Equal(
            expectedIds.Skip(
                WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize),
            second.Records.Select(record => record.ApplicationId));
        Assert.False(second.HasMore);
        Assert.Equal(
            second.Records[^1].ApplicationId,
            second.NextApplicationId);
        Assert.Empty(terminal.Records);
        Assert.False(terminal.HasMore);
        Assert.Equal(second.NextApplicationId, terminal.NextApplicationId);
        WorkspaceStaffIdentityAnchorSourceRecord terminalRecord =
            Assert.Single(
                first.Records.Concat(second.Records),
                record => record.ApplicationId == excluded.Id);
        Assert.Null(terminalRecord.StaffMemberId);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Superseded,
            terminalRecord.Status);
        Assert.Equal($"subject-{count + 1}", terminalRecord.SubjectId);
        Assert.All(
            first.Records.Concat(second.Records),
            record => Assert.Equal(record.SubjectId.Trim(), record.SubjectId));
    }

    [Fact]
    public async Task Cursor_and_page_size_envelope_is_enforced()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffIdentityAnchorCutoverSourceReader reader = new(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ListRelevantPageAsync(
                Guid.Empty,
                pageSize: 1,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ListRelevantPageAsync(
                afterApplicationId: null,
                pageSize: 0,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ListRelevantPageAsync(
                afterApplicationId: null,
                WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize + 1,
                CancellationToken.None));
    }

    private static WorkspacesDbContext CreateContext() => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options,
        new TestScopeContext());

    private static WorkspaceStaffOnboarding CreateApplication(
        Guid applicationId,
        int seed) =>
        WorkspaceStaffOnboarding.Create(
            applicationId,
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            CreateDeterministicGuid(10_000 + seed),
            $"subject-{seed}",
            $"subject-{seed}@example.test",
            $"Subject {seed}",
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            Now).Value;

    private static WorkspaceStaffOnboarding CreateCompletedApplication(
        Guid applicationId,
        int seed)
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            applicationId,
            seed);
        Assert.True(
            application.ObserveInvitationAccepted(Now.AddMinutes(1))
                .IsSuccess);
        Assert.True(
            application.MarkStaffReady(
                    CreateDeterministicGuid(30_000 + seed),
                    CreateDeterministicGuid(40_000 + seed),
                    CreateDeterministicGuid(50_000 + seed),
                    Now.AddMinutes(2))
                .IsSuccess);
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        return application;
    }

    private static Guid CreateDeterministicGuid(int value) =>
        Guid.ParseExact(
            $"00000000-0000-0000-0000-{value:D12}",
            "D");

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}

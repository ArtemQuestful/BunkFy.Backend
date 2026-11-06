namespace BunkFy.Modules.Workspaces.Tests.Persistence;

using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffIdentityAnchorSweepRepositoryTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Model_uses_a_store_generated_positive_scoped_unique_ordinal()
    {
        using WorkspacesDbContext context = CreateContext();
        IEntityType onboarding = context.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(WorkspaceStaffOnboarding))!;
        IProperty ordinal = onboarding.FindProperty(
            nameof(WorkspaceStaffOnboarding.IdentityAnchorSweepOrdinal))!;

        Assert.Equal(typeof(long), ordinal.ClrType);
        Assert.False(ordinal.IsNullable);
        Assert.Equal(ValueGenerated.OnAdd, ordinal.ValueGenerated);
        Assert.True(typeof(WorkspaceStaffOnboarding).GetProperty(
                nameof(WorkspaceStaffOnboarding.IdentityAnchorSweepOrdinal))!
            .SetMethod!
            .IsPrivate);
        Assert.Contains(onboarding.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(WorkspaceStaffOnboarding.ScopeId),
                    nameof(
                        WorkspaceStaffOnboarding.IdentityAnchorSweepOrdinal)
                ]));
        Assert.Contains(onboarding.GetCheckConstraints(), constraint =>
            constraint.Name ==
                "CK_staff_onboarding_identity_anchor_sweep_ordinal" &&
            constraint.Sql == "\"IdentityAnchorSweepOrdinal\" > 0");
    }

    [Fact]
    public void Checkpoint_model_names_completed_state_as_a_bounded_cycle()
    {
        using WorkspacesDbContext context = CreateContext();
        IEntityType checkpoint = context.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(
                typeof(WorkspaceStaffIdentityAnchorSweepCheckpoint))!;

        Assert.Contains(
            checkpoint.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_ws_anchor_sweep_last_cycle");
        Assert.DoesNotContain(
            checkpoint.GetCheckConstraints(),
            constraint => constraint.Name?.Contains(
                "snapshot",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void PostgreSql_translates_the_ordinal_keyset_and_ordering()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=sweep_translation;" +
                    "Username=test;Password=test")
                .Options;
        using WorkspacesDbContext context = new(
            options,
            new TestScopeContext());

        string sql = context.StaffOnboardingApplications
            .AsNoTracking()
            .Where(application =>
                application.IdentityAnchorSweepOrdinal > 10 &&
                application.IdentityAnchorSweepOrdinal <= 42)
            .OrderBy(application =>
                application.IdentityAnchorSweepOrdinal)
            .Select(application => new
            {
                application.Id,
                application.IdentityAnchorSweepOrdinal
            })
            .Take(501)
            .ToQueryString();

        Assert.Contains(
            "\"IdentityAnchorSweepOrdinal\" >",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"IdentityAnchorSweepOrdinal\" <=",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains(
            "\"IdentityAnchorSweepOrdinal\"",
            sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_universe_is_cyclic_and_fixed_to_the_cycle_upper_bound()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffOnboarding first = CreateApplication(Id(10), 1);
        WorkspaceStaffOnboarding terminalNullTarget =
            CreateApplication(Id(20), 2);
        Assert.True(terminalNullTarget.Supersede(Now.AddMinutes(1)).IsSuccess);
        WorkspaceStaffOnboarding third = CreateApplication(Id(30), 3);
        await PersistApplicationsAsync(
            context,
            first,
            terminalNullTarget,
            third);
        WorkspaceStaffIdentityAnchorSweepRepository repository = new(context);
        Assert.True(first.IdentityAnchorSweepOrdinal > 0);
        Assert.True(terminalNullTarget.IdentityAnchorSweepOrdinal >
            first.IdentityAnchorSweepOrdinal);
        Assert.True(third.IdentityAnchorSweepOrdinal >
            terminalNullTarget.IdentityAnchorSweepOrdinal);

        WorkspaceStaffIdentityAnchorSweepPage firstPage =
            (await repository.PreparePageAsync(
                TenantId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                batchSize: 2,
                Now.AddMinutes(2),
                CancellationToken.None)).Value;
        await context.SaveChangesAsync();

        Assert.Equal(third.IdentityAnchorSweepOrdinal, firstPage.UpperOrdinal);
        Assert.Equal([Id(10), Id(20)],
            firstPage.Candidates.Select(candidate => candidate.ApplicationId));
        WorkspaceStaffIdentityAnchorSweepCandidate terminalCandidate =
            Assert.Single(firstPage.Candidates,
                candidate => candidate.ApplicationId == Id(20));
        Assert.False(terminalCandidate.HasLocalAnchorState);
        Assert.Null(terminalNullTarget.StaffMemberId);
        Assert.False(firstPage.ReachedEnd);

        Assert.True((await repository.AdvanceAsync(
            Advance(firstPage, noAnchorCount: 2),
            Now.AddMinutes(3),
            CancellationToken.None)).IsSuccess);
        await context.SaveChangesAsync();
        WorkspaceStaffOnboarding insertedBehindGuid =
            CreateApplication(Id(5), 4);
        await PersistApplicationsAsync(context, insertedBehindGuid);
        Assert.True(insertedBehindGuid.IdentityAnchorSweepOrdinal >
            firstPage.UpperOrdinal);

        WorkspaceStaffIdentityAnchorSweepPage secondPage =
            (await repository.PreparePageAsync(
                TenantId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                batchSize: 2,
                Now.AddMinutes(4),
                CancellationToken.None)).Value;
        Assert.Equal([Id(30)],
            secondPage.Candidates.Select(candidate => candidate.ApplicationId));
        Assert.DoesNotContain(
            Id(5),
            secondPage.Candidates.Select(candidate => candidate.ApplicationId));
        Assert.Equal(firstPage.UpperOrdinal, secondPage.UpperOrdinal);
        Assert.True(secondPage.ReachedEnd);

        Assert.True((await repository.AdvanceAsync(
            Advance(secondPage, noAnchorCount: 1),
            Now.AddMinutes(5),
            CancellationToken.None)).IsSuccess);
        await context.SaveChangesAsync();
        WorkspaceStaffIdentityAnchorSweepStatus completedStatus =
            await repository.GetStatusAsync(
                TenantId,
                CancellationToken.None);
        Assert.False(completedStatus.HasActiveCycle);
        Assert.Equal(3, completedStatus.LastCompletedCycle.ScannedCount);
        Assert.True(completedStatus.HasCompletedCycleObservation);
        Assert.True(completedStatus.HasCompletedBoundedCycle);
        Assert.Equal(
            0,
            completedStatus.LastCompletedObservedBacklogCount);

        WorkspaceStaffIdentityAnchorSweepPage nextCycle =
            (await repository.PreparePageAsync(
                TenantId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                batchSize: 10,
                Now.AddMinutes(6),
                CancellationToken.None)).Value;
        Assert.Equal(insertedBehindGuid.IdentityAnchorSweepOrdinal,
            nextCycle.UpperOrdinal);
        Assert.Contains(
            nextCycle.Candidates,
            candidate => candidate.ApplicationId == Id(5));
    }

    [Fact]
    public async Task Fixed_upper_completes_while_new_rows_are_continuously_inserted()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffOnboarding[] initial =
        [
            CreateApplication(Id(900), 900),
            CreateApplication(Id(800), 800),
            CreateApplication(Id(700), 700)
        ];
        await PersistApplicationsAsync(context, initial);
        WorkspaceStaffIdentityAnchorSweepRepository repository = new(context);
        List<Guid> processed = [];
        List<WorkspaceStaffOnboarding> inserted = [];
        long? fixedUpper = null;
        bool completed = false;

        for (int pageNumber = 0; pageNumber < 6; pageNumber++)
        {
            WorkspaceStaffIdentityAnchorSweepPage page =
                (await repository.PreparePageAsync(
                    TenantId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    batchSize: 1,
                    Now.AddMinutes(10 + (pageNumber * 3)),
                    CancellationToken.None)).Value;
            await context.SaveChangesAsync();
            fixedUpper ??= page.UpperOrdinal;
            Assert.Equal(fixedUpper, page.UpperOrdinal);
            processed.Add(Assert.Single(page.Candidates).ApplicationId);

            Assert.True((await repository.AdvanceAsync(
                Advance(page, noAnchorCount: 1),
                Now.AddMinutes(11 + (pageNumber * 3)),
                CancellationToken.None)).IsSuccess);
            await context.SaveChangesAsync();
            if (page.ReachedEnd)
            {
                completed = true;
                break;
            }

            WorkspaceStaffOnboarding newcomer = CreateApplication(
                Id(100 - pageNumber),
                1_100 + pageNumber);
            await PersistApplicationsAsync(context, newcomer);
            Assert.True(newcomer.IdentityAnchorSweepOrdinal > fixedUpper);
            inserted.Add(newcomer);
        }

        Assert.True(completed);
        Assert.Equal(initial.Select(application => application.Id), processed);
        Assert.DoesNotContain(
            processed,
            applicationId => inserted.Any(application =>
                application.Id == applicationId));
        WorkspaceStaffIdentityAnchorSweepStatus status =
            await repository.GetStatusAsync(
                TenantId,
                CancellationToken.None);
        Assert.Equal(3, status.LastCompletedCycle.ScannedCount);
        Assert.Equal(fixedUpper, status.LastCompletedUpperOrdinal);
        Assert.True(status.HasCompletedBoundedCycle);
    }

    [Fact]
    public async Task Concurrent_advance_conflicts_but_exact_committed_replay_succeeds()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        WorkspaceStaffIdentityAnchorSweepPage seededPage;
        await using (WorkspacesDbContext seed = new(
            options,
            new TestScopeContext()))
        {
            await PersistApplicationsAsync(
                seed,
                CreateApplication(Id(501), 501));
            WorkspaceStaffIdentityAnchorSweepRepository repository =
                new(seed);
            seededPage = (await repository.PreparePageAsync(
                TenantId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                batchSize: 10,
                Now.AddMinutes(30),
                CancellationToken.None)).Value;
            await seed.SaveChangesAsync();
        }

        WorkspaceStaffIdentityAnchorSweepAdvance committedAdvance =
            Advance(seededPage, noAnchorCount: 1);
        await using (WorkspacesDbContext first = new(
            options,
            new TestScopeContext()))
        await using (WorkspacesDbContext second = new(
            options,
            new TestScopeContext()))
        {
            WorkspaceStaffIdentityAnchorSweepRepository firstRepository =
                new(first);
            WorkspaceStaffIdentityAnchorSweepRepository secondRepository =
                new(second);
            Assert.True((await firstRepository.AdvanceAsync(
                committedAdvance,
                Now.AddMinutes(31),
                CancellationToken.None)).IsSuccess);
            Assert.True((await secondRepository.AdvanceAsync(
                committedAdvance with { AdvanceId = Guid.NewGuid() },
                Now.AddMinutes(31),
                CancellationToken.None)).IsSuccess);

            await first.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => second.SaveChangesAsync());
        }

        await using WorkspacesDbContext replay = new(
            options,
            new TestScopeContext());
        WorkspaceStaffIdentityAnchorSweepRepository replayRepository =
            new(replay);
        Assert.True((await replayRepository.AdvanceAsync(
            committedAdvance,
            Now.AddMinutes(32),
            CancellationToken.None)).IsSuccess);
        await replay.SaveChangesAsync();
    }

    [Fact]
    public async Task Scope_stream_is_ordered_cancellable_and_includes_terminal_only_tenants()
    {
        await using WorkspacesDbContext context = CreateUnscopedContext();
        WorkspaceStaffOnboarding tenantATarget = CreateApplication(
            "tenant-a",
            Id(101),
            101);
        WorkspaceStaffOnboarding tenantBTerminalOnly = CreateApplication(
            "tenant-b",
            Id(102),
            102);
        Assert.True(tenantBTerminalOnly.Supersede(
            Now.AddMinutes(1)).IsSuccess);
        WorkspaceStaffOnboarding tenantCFenced = CreateApplication(
            "tenant-c",
            Id(103),
            103);
        await PersistApplicationsAsync(
            context,
            tenantCFenced,
            tenantBTerminalOnly,
            tenantATarget);
        WorkspaceTerminationFence fence = WorkspaceTerminationFence.Freeze(
            Guid.NewGuid(),
            "tenant-c",
            Guid.NewGuid(),
            Guid.NewGuid(),
            approvalRevision: 1,
            Guid.NewGuid(),
            new string('a', 64),
            "system:test",
            Now.AddMinutes(2)).Value;
        context.WorkspaceTerminationFences.Add(fence);
        await context.SaveChangesAsync();
        WorkspaceStaffIdentityAnchorSweepRepository repository = new(context);

        string[] scopes = await repository
            .StreamScheduleScopeIdsAsync(CancellationToken.None)
            .ToArrayAsync(CancellationToken.None);

        Assert.Equal(["tenant-a", "tenant-b"], scopes);
        Assert.Null(tenantBTerminalOnly.StaffMemberId);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Superseded,
            tenantBTerminalOnly.Status);

        using CancellationTokenSource canceled = new();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository
                .StreamScheduleScopeIdsAsync(canceled.Token)
                .ToArrayAsync(canceled.Token));
    }

    private static WorkspaceStaffIdentityAnchorSweepAdvance Advance(
        WorkspaceStaffIdentityAnchorSweepPage page,
        long noAnchorCount) =>
        new(
            page.CheckpointId,
            page.CheckpointVersion,
            page.CycleId,
            page.ExpectedAfterOrdinal,
            page.NextAfterOrdinal!.Value,
            page.ReachedEnd,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new WorkspaceStaffIdentityAnchorSweepPageCounts(
                page.Candidates.Count,
                noAnchorCount,
                RemovedCount: 0,
                ObservedCount: 0,
                AlreadyObservedCount: 0,
                DeferredCount: 0,
                ConflictCount: 0,
                PassOneCommittedCount: 0,
                ResolutionRecordConfirmedCount: 0));

    private static WorkspacesDbContext CreateContext() => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options,
        new TestScopeContext());

    private static WorkspacesDbContext CreateUnscopedContext() => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options,
        new UnscopedContext());

    private static async Task PersistApplicationsAsync(
        WorkspacesDbContext context,
        params WorkspaceStaffOnboarding[] applications)
    {
        long[] stored = await context.StaffOnboardingApplications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(application => application.IdentityAnchorSweepOrdinal)
            .ToArrayAsync();
        long nextOrdinal = stored.DefaultIfEmpty().Max();
        foreach (WorkspaceStaffOnboarding application in applications)
        {
            Assert.Equal(0, application.IdentityAnchorSweepOrdinal);
            context.StaffOnboardingApplications.Add(application);
            context.Entry(application)
                .Property(candidate =>
                    candidate.IdentityAnchorSweepOrdinal)
                .CurrentValue = checked(++nextOrdinal);
        }

        await context.SaveChangesAsync();
    }

    private static WorkspaceStaffOnboarding CreateApplication(
        Guid id,
        int seed) => CreateApplication(TenantId, id, seed);

    private static WorkspaceStaffOnboarding CreateApplication(
        string tenantId,
        Guid id,
        int seed) =>
        WorkspaceStaffOnboarding.Create(
            id,
            tenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Id(1_000 + seed),
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

    private static Guid Id(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class UnscopedContext : IScopeContext
    {
        public bool IsEnabled => false;
        public string? ScopeId => null;
    }
}

namespace BunkFy.Host.Migrations.Tests;

using BunkFy.Modules.Inventory.Persistence;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;

public sealed class InventoryConvergenceHistoryTransitionTests
{
    private const string Convergence =
        "20260812082324_AddInventoryManualBlockGroupConvergence";

    [Fact]
    public void Existing_Inventory_history_requires_explicit_transition_admission()
    {
        BunkFyMigrationHistory history = PublishedInventoryHistory();
        Assert.Equal(16, history.TargetMigrations.Count);
        Assert.Equal(15, history.AppliedMigrations.Count);
        Assert.Throws<InvalidOperationException>(() => BunkFyMigrationPlan.Create([history]));

        BunkFyMigrationPlan plan = BunkFyMigrationPlan.Create(
            [history], MigrationsExistingHistoryDisposition.ApplyInventoryConvergenceV1);

        Assert.Equal([Convergence], plan.Modules[0].PendingMigrations);
        Assert.Equal("inventory-convergence-v1", plan.Modules[0].HistoryTransition);
        Assert.Equal(history.TargetMigrations, plan.Modules[0].TargetMigrations);
        Assert.Equal(history.AppliedMigrations, plan.Modules[0].AppliedMigrations);
        Assert.Equal(1, plan.PendingMigrationCount);
        Assert.Equal(15, plan.AppliedMigrationCount);
    }

    [Fact]
    public void Completed_transition_replays_as_an_exact_prefix_with_the_same_target()
    {
        BunkFyMigrationHistory history = PublishedInventoryHistory();
        BunkFyMigrationPlan before = BunkFyMigrationPlan.Create(
            [history], MigrationsExistingHistoryDisposition.ApplyInventoryConvergenceV1);
        BunkFyMigrationHistory completed = history with { AppliedMigrations = history.TargetMigrations };
        BunkFyMigrationPlan after = BunkFyMigrationPlan.Create(
            [completed], MigrationsExistingHistoryDisposition.ApplyInventoryConvergenceV1);
        BunkFyMigrationPlan ordinary = BunkFyMigrationPlan.Create([completed]);

        Assert.Equal(before.TargetCatalogSha256, after.TargetCatalogSha256);
        Assert.Equal(ordinary.CurrentStateSha256, after.CurrentStateSha256);
        Assert.Equal(ordinary.PendingPlanSha256, after.PendingPlanSha256);
        Assert.Null(after.Modules[0].HistoryTransition);
        Assert.Equal(0, after.PendingMigrationCount);
        Assert.NotEqual(before.CurrentStateSha256, after.CurrentStateSha256);
        Assert.NotEqual(before.PendingPlanSha256, after.PendingPlanSha256);
    }

    public static TheoryData<int> PublishedHistoryPrefixes
    {
        get
        {
            TheoryData<int> prefixes = [];
            for (int count = 0; count < 16; count++)
            {
                prefixes.Add(count);
            }

            return prefixes;
        }
    }

    [Theory]
    [MemberData(nameof(PublishedHistoryPrefixes))]
    public void Every_valid_predecessor_prefix_can_continue_without_losing_pending_work(int appliedCount)
    {
        BunkFyMigrationHistory published = PublishedInventoryHistory();
        string[] applied = published.AppliedMigrations.Take(appliedCount).ToArray();
        BunkFyMigrationPlan oldRelease = BunkFyMigrationPlan.Create(
            [new("inventory", published.AppliedMigrations, applied)]);
        Assert.Equal(15 - appliedCount, oldRelease.PendingMigrationCount);
        BunkFyMigrationHistory current = published with { AppliedMigrations = applied };

        BunkFyMigrationPlan upgraded = BunkFyMigrationPlan.Create(
            [current], MigrationsExistingHistoryDisposition.ApplyInventoryConvergenceV1);

        Assert.Equal(16 - appliedCount, upgraded.PendingMigrationCount);
        Assert.Equal(applied, upgraded.Modules[0].AppliedMigrations);
        if (appliedCount == 14)
        {
            Assert.Equal([Convergence, "20260821195120_AddInventoryAllocationAmendmentDecisionTenantIntegrity"],
                upgraded.Modules[0].PendingMigrations);
        }

        if (appliedCount >= 14)
        {
            Assert.Equal("inventory-convergence-v1", upgraded.Modules[0].HistoryTransition);
            Assert.Throws<InvalidOperationException>(() => BunkFyMigrationPlan.Create([current]));
        }
        else
        {
            Assert.Null(upgraded.Modules[0].HistoryTransition);
            Assert.Equal(BunkFyMigrationPlan.Create([current]).PendingPlanSha256, upgraded.PendingPlanSha256);
        }
    }

    [Theory]
    [InlineData("other-module")]
    [InlineData("wrong-module-case")]
    [InlineData("missing-canonical")]
    [InlineData("unknown-applied")]
    [InlineData("reordered-applied")]
    [InlineData("future-target")]
    [InlineData("reordered-target")]
    [InlineData("another-gap")]
    public void Explicit_transition_rejects_any_unreviewed_history_or_catalogue(string shape)
    {
        BunkFyMigrationHistory history = PublishedInventoryHistory();
        history = shape switch
        {
            "other-module" => history with { ModuleName = "reservations" },
            "wrong-module-case" => history with { ModuleName = "Inventory" },
            "missing-canonical" => history with { AppliedMigrations = history.AppliedMigrations.Skip(1).ToArray() },
            "unknown-applied" => history with { AppliedMigrations = [.. history.AppliedMigrations.SkipLast(1), "999_unknown"] },
            "reordered-applied" => history with { AppliedMigrations = history.AppliedMigrations.Reverse().ToArray() },
            "future-target" => history with { TargetMigrations = [.. history.TargetMigrations, "20261103000000_FutureMigration"] },
            "reordered-target" => history with { TargetMigrations = history.TargetMigrations.Reverse().ToArray() },
            "another-gap" => history with { AppliedMigrations = history.TargetMigrations.Where(id => id != history.TargetMigrations[1]).ToArray() },
            _ => throw new InvalidOperationException("Unknown fixture shape.")
        };

        Assert.Throws<InvalidOperationException>(() => BunkFyMigrationPlan.Create(
            [history], MigrationsExistingHistoryDisposition.ApplyInventoryConvergenceV1));
    }

    [Fact]
    public void Inventory_transition_cannot_admit_a_gap_in_another_module()
    {
        BunkFyMigrationHistory inventory = PublishedInventoryHistory();
        BunkFyMigrationHistory unrelated = new("unrelated", ["001", "002"], ["002"]);

        Assert.Throws<InvalidOperationException>(() => BunkFyMigrationPlan.Create(
            [inventory, unrelated], MigrationsExistingHistoryDisposition.ApplyInventoryConvergenceV1));
    }

    private static BunkFyMigrationHistory PublishedInventoryHistory()
    {
        // Read the real loaded migration assembly without connecting to a DB.
        using InventoryDbContext context = new(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused", provider => provider
                    .MigrationsAssembly(InventoryMigrations.PostgreSqlAssembly))
                .Options,
            new TestScopeContext());
        string[] target = [.. context.Database.GetMigrations()];
        Assert.Contains(Convergence, target);
        return new("inventory", target, target.Where(id => id != Convergence).ToArray());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "fixture";
    }
}

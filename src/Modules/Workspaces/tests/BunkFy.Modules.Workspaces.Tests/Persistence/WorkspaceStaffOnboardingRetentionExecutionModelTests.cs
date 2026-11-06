namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingRetentionExecutionModelTests
{
    [Fact]
    public void Execution_evidence_is_scoped_concurrent_and_fully_constrained()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        using WorkspacesDbContext context =
            new(options, new TestScopeContext());

        IEntityType entity = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(
                typeof(WorkspaceStaffOnboardingRetentionExecution))!;

        Assert.Equal(
            "staff_onboarding_retention_executions",
            entity.GetTableName());
        Assert.NotEmpty(entity.GetDeclaredQueryFilters());
        Assert.True(entity.FindProperty(
            nameof(WorkspaceStaffOnboardingRetentionExecution.Version))!
            .IsConcurrencyToken);
        Assert.False(entity.FindProperty(
            nameof(WorkspaceStaffOnboardingRetentionExecution.ScannedCount))!
            .IsNullable);
        Assert.True(entity.FindProperty(
            nameof(WorkspaceStaffOnboardingRetentionExecution.RemainingCount))!
            .IsNullable);
        Assert.Contains(
            entity.GetIndexes(),
            index => string.Equals(
                index.GetDatabaseName(),
                "IX_staff_onboarding_retention_executions_history",
                StringComparison.Ordinal));
        Assert.Equal(
            [
                "CK_staff_onboarding_retention_execution_coordinate",
                "CK_staff_onboarding_retention_execution_counts",
                "CK_staff_onboarding_retention_execution_failed_remaining",
                "CK_staff_onboarding_retention_execution_lifecycle",
                "CK_staff_onboarding_retention_execution_state",
                "CK_staff_onboarding_retention_execution_timing",
                "CK_staff_onboarding_retention_execution_version"
            ],
            entity.GetCheckConstraints()
                .Select(constraint => constraint.Name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId =>
            "10000000-0000-0000-0000-000000000001";
    }
}

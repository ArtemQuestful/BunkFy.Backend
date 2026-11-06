namespace Integration.Tests;

using System.Data.Common;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Pagination;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingQueryTranslationTests
{
    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public async Task Actionable_page_translates_before_attempting_a_database_connection(
        string provider)
    {
        DbContextOptionsBuilder<WorkspacesDbContext> options = new();
        if (provider == "PostgreSql")
        {
            options.UseNpgsql("Host=127.0.0.1;Database=translation_only;Username=unused");
        }
        else
        {
            options.UseSqlServer("Server=127.0.0.1;Database=translation_only;User Id=unused;Password=unused");
        }

        // EF must translate the complete projection, ordering and pagination
        // before opening a connection. Stop there: no database or socket is used.
        options.AddInterceptors(new StopBeforeConnection());
        await using WorkspacesDbContext context = new(options.Options, new TestScopeContext());
        WorkspaceStaffOnboardingRepository repository = new(context);

        await Assert.ThrowsAsync<QueryTranslatedException>(() => repository.ListActionableAsync(
            PageRequest.Normalize(2, 25), CancellationToken.None));
    }

    private sealed class StopBeforeConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new QueryTranslatedException();
    }

    private sealed class QueryTranslatedException : Exception
    {
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;

        public string? ScopeId => "ac000000-0000-0000-0000-000000000001";
    }
}

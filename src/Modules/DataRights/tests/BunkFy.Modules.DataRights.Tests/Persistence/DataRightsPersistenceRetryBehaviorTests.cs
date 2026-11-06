namespace BunkFy.Modules.DataRights.Tests.Persistence;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Persistence;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsPersistenceRetryBehaviorTests
{
    [Fact]
    public async Task Execution_unique_conflict_reexecutes_once()
    {
        DbContextOptions<DataRightsDbContext> options =
            new DbContextOptionsBuilder<DataRightsDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        await using DataRightsDbContext dbContext = new(
            options,
            new TestScopeContext());
        DataRightsPersistenceRetryBehavior<
            StartDataRightsAnonymisationExecutionCommand,
        DataRightsExecutionDto> behavior = new(dbContext, _ => true);
        StartDataRightsAnonymisationExecutionCommand command = new(
            DataRightsCaseScope.ForProperty(Guid.NewGuid()),
            Guid.NewGuid(),
            Guid.NewGuid(),
            6,
            "user:executor");
        int attempts = 0;

        Task<Result<DataRightsExecutionDto>> Next()
        {
            attempts++;
            return attempts == 1
                ? throw new DbUpdateException("simulated unique conflict")
                : Task.FromResult(Result.Failure<DataRightsExecutionDto>(
                    DataRightsApplicationErrors.ExecutionAlreadyStarted));
        }

        Result<DataRightsExecutionDto> result =
            await behavior.HandleAsync(command, Next, CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(DataRightsApplicationErrors.ExecutionAlreadyStarted, result.Error);
    }

    [Fact]
    public async Task Unrelated_commands_are_not_retried()
    {
        DbContextOptions<DataRightsDbContext> options =
            new DbContextOptionsBuilder<DataRightsDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        await using DataRightsDbContext dbContext = new(
            options,
            new TestScopeContext());
        DataRightsPersistenceRetryBehavior<
            CreateDataRightsCaseCommand,
            DataRightsCaseDto> behavior = new(dbContext, _ => true);
        CreateDataRightsCaseCommand command = new(
            Guid.NewGuid(),
            DataRightsCaseScope.ForProperty(Guid.NewGuid()),
            DataRightsOperation.AccessExport,
            DataRightsRestrictionDirective.Unknown,
            DataRightsRequesterRelationship.ControllerInitiated,
            "user:operator");
        int attempts = 0;

        Task<Result<DataRightsCaseDto>> Next()
        {
            attempts++;
            throw new DbUpdateException("simulated unique conflict");
        }

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            behavior.HandleAsync(command, Next, CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}

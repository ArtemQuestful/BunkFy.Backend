namespace Integration.Tests.Support;

using BunkFy.Modules.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

internal static class InventoryMigrationHistoryTestData
{
    public static async Task ApplyUnchangedMigrationsAsync(
        InventoryDbContext context,
        params string[] migrationIds)
    {
        IMigrationsAssembly migrations = context.Database.GetService<IMigrationsAssembly>();
        IMigrationsSqlGenerator sqlGenerator = context.Database.GetService<IMigrationsSqlGenerator>();
        IHistoryRepository history = context.Database.GetService<IHistoryRepository>();
        IReadOnlyList<HistoryRow> applied = await history.GetAppliedMigrationsAsync();
        Assert.NotEmpty(applied);
        string productVersion = applied[^1].ProductVersion;
        foreach (string migrationId in migrationIds)
        {
            Assert.DoesNotContain(applied, row => row.MigrationId == migrationId);
            Migration migration = migrations.CreateMigration(
                migrations.Migrations[migrationId], context.Database.ProviderName!);
            await using var transaction = await context.Database.BeginTransactionAsync();
            foreach (MigrationCommand migrationCommand in sqlGenerator.Generate(
                         migration.UpOperations, migration.TargetModel))
            {
                Assert.False(migrationCommand.TransactionSuppressed);
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = migrationCommand.CommandText;
                await command.ExecuteNonQueryAsync();
            }

            await using (var command = context.Database.GetDbConnection().CreateCommand())
            {
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = history.GetInsertScript(new HistoryRow(migrationId, productVersion));
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
    }
}

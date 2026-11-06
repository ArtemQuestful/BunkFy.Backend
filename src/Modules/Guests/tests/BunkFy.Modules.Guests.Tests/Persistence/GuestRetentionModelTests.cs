namespace BunkFy.Modules.Guests.Tests.Persistence;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.TimeZones;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestRetentionModelTests
{
    [Fact]
    public void Model_enforces_scoped_execution_cursor_and_proof_shape()
    {
        using GuestsDbContext dbContext = CreateDbContext();
        IModel designModel =
            dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType execution = dbContext.Model.FindEntityType(
            typeof(GuestRetentionExecution))!;
        IEntityType checkpoint = dbContext.Model.FindEntityType(
            typeof(GuestRetentionSweepCheckpoint))!;
        IEntityType receipt = dbContext.Model.FindEntityType(
            typeof(GuestRetentionAnonymisationReceipt))!;

        Assert.Equal(2, execution.GetKeys().Count());
        Assert.True(execution.FindProperty(
            nameof(GuestRetentionExecution.Version))!.IsConcurrencyToken);
        Assert.Contains(execution.GetIndexes(), index =>
            index.GetDatabaseName() ==
                "IX_guest_retention_executions_history" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestRetentionExecution.ScopeId),
                nameof(GuestRetentionExecution.DataClassKey),
                nameof(GuestRetentionExecution.CompletedAtUtc),
                nameof(GuestRetentionExecution.Id)
            ]));
        AssertConstraints(
            designModel.FindEntityType(typeof(GuestRetentionExecution))!,
            "CK_guest_retention_executions_coordinates",
            "CK_guest_retention_executions_policy",
            "CK_guest_retention_executions_cursor",
            "CK_guest_retention_executions_key",
            "CK_guest_retention_executions_version",
            "CK_guest_retention_executions_timestamp",
            "CK_guest_retention_executions_counts",
            "CK_guest_retention_executions_state");

        Assert.Single(checkpoint.GetKeys());
        Assert.Empty(checkpoint.GetForeignKeys());
        Assert.True(checkpoint.FindProperty(
            nameof(GuestRetentionSweepCheckpoint.Version))!.IsConcurrencyToken);
        Assert.Contains(checkpoint.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() ==
                "UX_guest_retention_checkpoints_data_class" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestRetentionSweepCheckpoint.ScopeId),
                nameof(GuestRetentionSweepCheckpoint.DataClassKey)
            ]));
        Assert.Contains(checkpoint.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() ==
                "UX_guest_retention_checkpoints_last_execution" &&
            index.GetFilter() == "\"LastExecutionId\" IS NOT NULL" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestRetentionSweepCheckpoint.ScopeId),
                nameof(GuestRetentionSweepCheckpoint.LastExecutionId)
            ]));
        AssertConstraints(
            designModel.FindEntityType(typeof(GuestRetentionSweepCheckpoint))!,
            "CK_guest_retention_sweep_checkpoints_coordinates",
            "CK_guest_retention_sweep_checkpoints_cursor",
            "CK_guest_retention_sweep_checkpoints_key",
            "CK_guest_retention_sweep_checkpoints_version",
            "CK_guest_retention_sweep_checkpoints_lifecycle",
            "CK_guest_retention_sweep_checkpoints_timestamp");
        Assert.Contains(
            "\"Version\" >= 3",
            designModel
                .FindEntityType(typeof(GuestRetentionSweepCheckpoint))!
                .GetCheckConstraints()
                .Single(constraint => constraint.Name ==
                    "CK_guest_retention_sweep_checkpoints_lifecycle")
                .Sql);

        Assert.Single(receipt.GetKeys());
        Assert.Contains(receipt.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() == "UX_guest_retention_receipts_guest" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestRetentionAnonymisationReceipt.ScopeId),
                nameof(GuestRetentionAnonymisationReceipt.GuestId)
            ]));
        Assert.Contains(receipt.GetIndexes(), index => !index.IsUnique &&
            index.GetDatabaseName() ==
                "IX_guest_retention_receipts_execution" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestRetentionAnonymisationReceipt.ScopeId),
                nameof(GuestRetentionAnonymisationReceipt.ExecutionId)
            ]));
        Assert.Contains(receipt.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() == "UX_guest_retention_receipts_event" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestRetentionAnonymisationReceipt.ScopeId),
                nameof(GuestRetentionAnonymisationReceipt.EventId)
            ]));
        Assert.Equal(3, receipt.GetForeignKeys().Count());
        AssertForeignKey(
            receipt,
            typeof(GuestRetentionExecution),
            "FK_guest_retention_receipts_execution");
        AssertForeignKey(
            receipt,
            typeof(GuestProfile),
            "FK_guest_retention_receipts_guest_profile");
        AssertForeignKey(
            receipt,
            typeof(GuestAnonymisationTombstone),
            "FK_guest_retention_receipts_tombstone");
        AssertConstraints(
            designModel.FindEntityType(
                typeof(GuestRetentionAnonymisationReceipt))!,
            "CK_guest_retention_receipts_coordinates",
            "CK_guest_retention_receipts_contract",
            "CK_guest_retention_receipts_actor",
            "CK_guest_retention_receipts_versions",
            "CK_guest_retention_receipts_properties",
            "CK_guest_retention_receipts_digests",
            "CK_guest_retention_receipts_timestamps");
    }

    [Fact]
    public async Task Retention_receipt_is_append_only()
    {
        await using GuestsDbContext dbContext = CreateDbContext();
        GuestProfile profile = GuestProfile.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "Guest",
            legalName: null,
            email: "guest@example.test",
            phone: null,
            dateOfBirth: null,
            nationalityCountryCode: null,
            preferredLanguageTag: null,
            notes: null,
            "user:creator",
            Guid.NewGuid(),
            new DateTimeOffset(
                2024,
                1,
                1,
                0,
                0,
                0,
                TimeSpan.Zero)).Value;
        DateTimeOffset now =
            new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        GuestRetentionExecution execution =
            GuestRetentionExecution.Start(
                Guid.NewGuid(),
                profile.ScopeId,
                "guest-operational",
                executionPolicyVersion:
                    GuestRetentionExecution.MinimumRunningPolicyVersion,
                attempt: 1,
                startingProjectionOrdinal: 0,
                now.AddMinutes(-1),
                now.AddMinutes(10)).Value;
        GuestProfileAnonymisationOutcome outcome =
            profile.AnonymiseForRetention(
                profile.Version,
                "system:retention",
                Guid.NewGuid(),
                now).Value;
        GuestRetentionAnonymisationReceipt receipt =
            GuestRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                profile.ScopeId,
                execution.Id,
                profile.Id,
                outcome.PreviousVersion,
                outcome.CurrentVersion,
                affectedPropertyCount: 1,
                now.AddDays(-1),
                new string('a', 64),
                TimeZoneCatalog.Default.CatalogVersion,
                outcome.EventId,
                "system:retention",
                now).Value;
        dbContext.GuestProfiles.Add(profile);
        dbContext.RetentionExecutions.Add(execution);
        dbContext.RetentionAnonymisationReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        dbContext.Entry(receipt)
            .Property(nameof(
                GuestRetentionAnonymisationReceipt.PolicySetSha256))
            .CurrentValue = new string('b', 64);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());
        Assert.Equal(
            "Guest anonymisation receipts are append-only.",
            failure.Message);
    }

    private static GuestsDbContext CreateDbContext()
    {
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseInMemoryDatabase(
                    $"guest-retention-model-{Guid.NewGuid():N}")
                .Options;
        return new(options, new TestScopeContext());
    }

    private static void AssertForeignKey(
        IEntityType dependent,
        Type principalType,
        string constraintName)
    {
        IForeignKey reference = Assert.Single(
            dependent.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType ==
                principalType);
        Assert.Equal(constraintName, reference.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, reference.DeleteBehavior);
    }

    private static void AssertConstraints(
        IEntityType entity,
        params string[] expectedNames) =>
        Assert.Equal(
            expectedNames.Order(StringComparer.Ordinal),
            entity.GetCheckConstraints()
                .Select(constraint => constraint.Name)
                .Order(StringComparer.Ordinal));

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}

namespace BunkFy.Modules.Staff.Tests;

using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Domain.Retention;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffRetentionModelTests
{
    [Fact]
    public void Model_enforces_complete_retention_control_and_proof_shape()
    {
        using StaffDbContext dbContext = CreateDbContext();
        IModel designModel =
            dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType member = designModel.FindEntityType(
            typeof(StaffMember))!;
        IEntityType execution = designModel.FindEntityType(
            typeof(StaffRetentionExecution))!;
        IEntityType checkpoint = designModel.FindEntityType(
            typeof(StaffRetentionSweepCheckpoint))!;
        IEntityType receipt = designModel.FindEntityType(
            typeof(StaffRetentionAnonymisationReceipt))!;
        IEntityType tombstone = designModel.FindEntityType(
            typeof(StaffAnonymisationTombstone))!;

        Assert.Contains(
            member.GetIndexes(),
            index =>
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(
                    [
                        nameof(StaffMember.ScopeId),
                        nameof(StaffMember.Status),
                        nameof(StaffMember.ProjectionOrdinal),
                        nameof(StaffMember.Id)
                    ]));
        Assert.True(execution.FindProperty(
            nameof(StaffRetentionExecution.Version))!
            .IsConcurrencyToken);
        AssertConstraintNames(
            execution,
            "CK_staff_retention_executions_coordinates",
            "CK_staff_retention_executions_policy",
            "CK_staff_retention_executions_cursor",
            "CK_staff_retention_executions_key",
            "CK_staff_retention_executions_version",
            "CK_staff_retention_executions_timestamp",
            "CK_staff_retention_executions_counts",
            "CK_staff_retention_executions_state");
        Assert.Equal(2, execution.GetKeys().Count());
        Assert.Contains(
            execution.GetIndexes(),
            index => index.GetDatabaseName() ==
                "IX_staff_retention_executions_history");

        Assert.True(checkpoint.FindProperty(
            nameof(StaffRetentionSweepCheckpoint.Version))!
            .IsConcurrencyToken);
        AssertConstraintNames(
            checkpoint,
            "CK_staff_retention_checkpoints_coordinates",
            "CK_staff_retention_checkpoints_cursor",
            "CK_staff_retention_checkpoints_key",
            "CK_staff_retention_checkpoints_policy",
            "CK_staff_retention_checkpoints_version",
            "CK_staff_retention_checkpoints_lifecycle",
            "CK_staff_retention_checkpoints_timestamp");
        Assert.Single(checkpoint.GetKeys());
        Assert.Empty(checkpoint.GetForeignKeys());
        Assert.Contains(
            checkpoint.GetIndexes(),
            index => index.IsUnique &&
                index.GetDatabaseName() ==
                    "UX_staff_retention_checkpoints_data_class_policy");
        Assert.Contains(
            checkpoint.GetIndexes(),
            index => index.IsUnique &&
                index.GetDatabaseName() ==
                    "UX_staff_retention_checkpoints_last_execution" &&
                index.GetFilter() == "\"LastExecutionId\" IS NOT NULL");

        AssertConstraintNames(
            receipt,
            "CK_staff_retention_receipts_coordinates",
            "CK_staff_retention_receipts_contract",
            "CK_staff_retention_receipts_actor",
            "CK_staff_retention_receipts_versions",
            "CK_staff_retention_receipts_digests",
            "CK_staff_retention_receipts_timestamps");
        Assert.Single(receipt.GetKeys());
        Assert.Equal(
            2,
            receipt.GetForeignKeys().Count(foreignKey =>
                foreignKey.PrincipalEntityType.ClrType is not null));
        AssertForeignKey(
            receipt,
            typeof(StaffRetentionExecution),
            "FK_staff_retention_receipts_execution");
        AssertForeignKey(
            receipt,
            typeof(StaffMember),
            "FK_staff_retention_receipts_staff_member");
        AssertIndex(
            receipt,
            "UX_staff_retention_receipts_staff_member",
            isUnique: true,
            nameof(StaffRetentionAnonymisationReceipt.ScopeId),
            nameof(StaffRetentionAnonymisationReceipt.StaffMemberId));
        AssertIndex(
            receipt,
            "IX_staff_retention_receipts_execution",
            isUnique: false,
            nameof(StaffRetentionAnonymisationReceipt.ScopeId),
            nameof(StaffRetentionAnonymisationReceipt.ExecutionId));
        AssertIndex(
            receipt,
            "UX_staff_retention_receipts_event",
            isUnique: true,
            nameof(StaffRetentionAnonymisationReceipt.ScopeId),
            nameof(StaffRetentionAnonymisationReceipt.EventId));
        Assert.False(tombstone.FindProperty(
            nameof(StaffAnonymisationTombstone.Authority))!
            .IsNullable);
    }

    [Fact]
    public async Task Retention_receipt_is_append_only()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        DateTimeOffset now = StaffRetentionTestData.Now;
        StaffMember member = CreateDepartedMember(
            "tenant-a",
            now.AddDays(-400));
        StaffRetentionExecution execution =
            StaffRetentionExecution.Start(
                Guid.NewGuid(),
                member.ScopeId,
                "staff-employment",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 0,
                now.AddMinutes(-1),
                now.AddMinutes(10)).Value;
        StaffMemberAnonymisationOutcome outcome =
            member.Anonymise(
                member.Version,
                "system:retention",
                Guid.NewGuid(),
                now).Value;
        StaffRetentionAnonymisationReceipt receipt =
            StaffRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                member.ScopeId,
                execution.Id,
                member.Id,
                outcome,
                selectedOperationLockRevision: 8,
                resultingOperationLockRevision: 9,
                member.DepartedAtUtc!.Value,
                now.AddDays(-1),
                new string('a', 64)).Value;
        dbContext.StaffMembers.Add(member);
        dbContext.RetentionExecutions.Add(execution);
        dbContext.RetentionAnonymisationReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        dbContext.Entry(receipt)
            .Property(nameof(
                StaffRetentionAnonymisationReceipt
                    .PolicyEvidenceSha256))
            .CurrentValue = new string('b', 64);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());
        Assert.Equal(
            "Staff immutable receipts are append-only.",
            failure.Message);
    }

    [Fact]
    public async Task Checkpoint_lookup_isolated_by_execution_policy_version()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        StaffRetentionSweepCheckpoint versionOne =
            StaffRetentionSweepCheckpoint.Create(
                Guid.NewGuid(),
                "tenant-a",
                "staff-employment",
                executionPolicyVersion: 1,
                StaffRetentionTestData.Now).Value;
        StaffRetentionSweepCheckpoint versionTwo =
            StaffRetentionSweepCheckpoint.Create(
                Guid.NewGuid(),
                "tenant-a",
                "staff-employment",
                executionPolicyVersion: 2,
                StaffRetentionTestData.Now).Value;
        dbContext.RetentionSweepCheckpoints.AddRange(
            versionOne,
            versionTwo);
        await dbContext.SaveChangesAsync();
        StaffRetentionExecutionRepository repository = new(dbContext);

        Assert.Same(
            versionOne,
            await repository.GetCheckpointAsync(
                "staff-employment",
                executionPolicyVersion: 1,
                CancellationToken.None));
        Assert.Same(
            versionTwo,
            await repository.GetCheckpointAsync(
                "staff-employment",
                executionPolicyVersion: 2,
                CancellationToken.None));
    }

    internal static StaffMember CreateDepartedMember(
        string tenantId,
        DateTimeOffset departedAtUtc,
        Guid? staffMemberId = null)
    {
        StaffMember member = StaffMember.Create(
            staffMemberId ?? Guid.NewGuid(),
            tenantId,
            "Original staff member",
            "Original Legal Name",
            "original@example.test",
            "+44 20 1234 5678",
            "EMP-1",
            "Manager",
            "Operations",
            null,
            "user:creator",
            Guid.NewGuid(),
            departedAtUtc.AddDays(-100)).Value;
        Assert.True(member.Depart(
            DateOnly.FromDateTime(departedAtUtc.UtcDateTime),
            member.Version,
            "user:manager",
            "Employment ended",
            Guid.NewGuid(),
            [],
            departedAtUtc).IsSuccess);
        member.ClearDomainEvents();
        return member;
    }

    private static StaffDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<StaffDbContext>()
            .UseInMemoryDatabase(
                $"staff-retention-model-{Guid.NewGuid():N}")
            .Options,
        new TestScopeContext("tenant-a"));

    private static void AssertConstraintNames(
        IEntityType entityType,
        params string[] expectedNames)
    {
        string[] actualNames = entityType.GetCheckConstraints()
            .Select(constraint => constraint.Name!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expectedNames.Order(StringComparer.Ordinal),
            actualNames);
    }

    private static void AssertIndex(
        IEntityType entityType,
        string databaseName,
        bool isUnique,
        params string[] propertyNames) => Assert.Contains(
        entityType.GetIndexes(),
        index => index.GetDatabaseName() == databaseName &&
            index.IsUnique == isUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(propertyNames));

    private static void AssertForeignKey(
        IEntityType dependent,
        Type principalType,
        string constraintName)
    {
        IForeignKey foreignKey = Assert.Single(
            dependent.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType.ClrType ==
                principalType);
        Assert.Equal(constraintName, foreignKey.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    internal sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }
}

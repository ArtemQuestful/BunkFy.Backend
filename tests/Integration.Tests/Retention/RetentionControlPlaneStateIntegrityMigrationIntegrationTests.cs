namespace Integration.Tests;

using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class RetentionControlPlaneStateIntegrityMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260815153626_AddRetentionRunRetryRecovery";
    private const string ScopeId = "tenant-a";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Control_plane_migration_preserves_valid_states_and_rejects_malformed_writes()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_retention_state_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        DateTimeOffset startedAtUtc = new(
            2026,
            8,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);
        Guid completedExecutionId = Guid.Parse(
            "71000000-0000-0000-0000-000000000001");
        Guid blockedExecutionId = Guid.Parse(
            "71000000-0000-0000-0000-000000000002");
        Guid failedExecutionId = Guid.Parse(
            "71000000-0000-0000-0000-000000000003");
        Guid runningExecutionId = Guid.Parse(
            "71000000-0000-0000-0000-000000000004");
        Guid propertyId = Guid.Parse(
            "72000000-0000-0000-0000-000000000001");
        Guid appliedRetryId = Guid.Parse(
            "73000000-0000-0000-0000-000000000001");
        Guid failedRetryId = Guid.Parse(
            "73000000-0000-0000-0000-000000000002");
        Guid pendingRetryId = Guid.Parse(
            "73000000-0000-0000-0000-000000000003");

        await using (RetentionDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                PreviousMigration);

            RetentionExecution completed = Start(
                completedExecutionId,
                "guests",
                "guest-operational",
                RetentionExecutionTargetKind.Tenant,
                propertyId: null,
                startedAtUtc);
            RetentionScheduleState completedSchedule = new(
                completed,
                startedAtUtc.AddHours(1));
            Assert.True(completed.Complete(
                RetentionExecutionState.Completed,
                attempt: 1,
                scannedCount: 4,
                affectedCount: 3,
                remainingCount: 0,
                "guests.retention.completed",
                startedAtUtc.AddMinutes(2),
                holdReviewDueAtUtc: null).IsSuccess);
            completedSchedule.RecordCompleted(completed);

            DateTimeOffset blockedStartedAtUtc = startedAtUtc.AddHours(2);
            RetentionExecution blocked = Start(
                blockedExecutionId,
                "staff",
                "staff-operational",
                RetentionExecutionTargetKind.Property,
                propertyId,
                blockedStartedAtUtc);
            RetentionScheduleState blockedSchedule = new(
                blocked,
                blockedStartedAtUtc.AddHours(1));
            Assert.True(blocked.Complete(
                RetentionExecutionState.Blocked,
                attempt: 1,
                scannedCount: 0,
                affectedCount: 0,
                remainingCount: 1,
                "staff.retention.legal-hold",
                blockedStartedAtUtc.AddMinutes(2),
                blockedStartedAtUtc.AddDays(30)).IsSuccess);
            blockedSchedule.RecordCompleted(blocked);

            DateTimeOffset failedStartedAtUtc = startedAtUtc.AddHours(4);
            RetentionExecution failed = Start(
                failedExecutionId,
                "reservations",
                "reservation-operational",
                RetentionExecutionTargetKind.Tenant,
                propertyId: null,
                failedStartedAtUtc);
            RetentionScheduleState failedSchedule = new(
                failed,
                failedStartedAtUtc.AddHours(1));
            Assert.True(failed.Complete(
                RetentionExecutionState.Failed,
                attempt: 1,
                scannedCount: 0,
                affectedCount: 0,
                remainingCount: 1,
                "retention.owner-timeout",
                failedStartedAtUtc.AddMinutes(6),
                holdReviewDueAtUtc: null).IsSuccess);
            failedSchedule.RecordCompleted(failed);

            DateTimeOffset runningStartedAtUtc = startedAtUtc.AddHours(6);
            RetentionExecution running = Start(
                runningExecutionId,
                "ingestion",
                "raw-source-evidence",
                RetentionExecutionTargetKind.Property,
                propertyId,
                runningStartedAtUtc);
            RetentionScheduleState runningSchedule = new(
                running,
                runningStartedAtUtc.AddHours(1));

            RetentionRunRetryRequest appliedRetry = CreateRetry(
                appliedRetryId,
                completed,
                completedSchedule.Version,
                startedAtUtc.AddMinutes(10));
            Assert.True(appliedRetry.MarkApplied(
                startedAtUtc.AddMinutes(11)).IsSuccess);
            RetentionRunRetryRequest failedRetry = CreateRetry(
                failedRetryId,
                blocked,
                blockedSchedule.Version,
                blockedStartedAtUtc.AddMinutes(10));
            Assert.True(failedRetry.MarkFailed(
                "task-run-unavailable",
                blockedStartedAtUtc.AddMinutes(11)).IsSuccess);
            RetentionRunRetryRequest pendingRetry = CreateRetry(
                pendingRetryId,
                failed,
                failedSchedule.Version,
                failedStartedAtUtc.AddMinutes(10));

            appliedRetry.ClearDomainEvents();
            failedRetry.ClearDomainEvents();
            pendingRetry.ClearDomainEvents();
            previous.AddRange(
                completed,
                completedSchedule,
                blocked,
                blockedSchedule,
                failed,
                failedSchedule,
                running,
                runningSchedule,
                appliedRetry,
                failedRetry,
                pendingRetry);
            await previous.SaveChangesAsync();
        }

        await using RetentionDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        IMigrator migrator = upgraded.Database.GetService<IMigrator>();
        await migrator.MigrateAsync();

        Assert.Equal(
            RetentionExecutionState.Completed,
            (await upgraded.Executions.SingleAsync(
                execution => execution.Id == completedExecutionId)).State);
        Assert.Equal(
            RetentionExecutionState.Blocked,
            (await upgraded.Executions.SingleAsync(
                execution => execution.Id == blockedExecutionId)).State);
        Assert.Equal(
            RetentionExecutionState.Failed,
            (await upgraded.Executions.SingleAsync(
                execution => execution.Id == failedExecutionId)).State);
        Assert.Equal(
            RetentionExecutionState.Running,
            (await upgraded.Executions.SingleAsync(
                execution => execution.Id == runningExecutionId)).State);
        Assert.Equal(
            RetentionRunRetryRequestState.Applied,
            (await upgraded.RunRetryRequests.SingleAsync(
                request => request.Id == appliedRetryId)).State);
        Assert.Equal(
            RetentionRunRetryRequestState.Failed,
            (await upgraded.RunRetryRequests.SingleAsync(
                request => request.Id == failedRetryId)).State);
        Assert.Equal(
            RetentionRunRetryRequestState.Pending,
            (await upgraded.RunRetryRequests.SingleAsync(
                request => request.Id == pendingRetryId)).State);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_executions_coordinate",
            $"""
            UPDATE retention.executions
            SET "Id" = {Guid.Empty}
            WHERE "Id" = {completedExecutionId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_executions_keys",
            $"""
            UPDATE retention.executions
            SET "OutcomeCode" = {"unstructured outcome"}
            WHERE "Id" = {completedExecutionId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_executions_versions",
            $"""
            UPDATE retention.executions
            SET "Version" = {1L}
            WHERE "Id" = {completedExecutionId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_executions_time",
            $"""
            UPDATE retention.executions
            SET "CompletedAtUtc" = "DeadlineUtc" + interval '1 second'
            WHERE "Id" = {completedExecutionId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_executions_state",
            $"""
            UPDATE retention.executions
            SET "AffectedCount" = "ScannedCount" + 1
            WHERE "Id" = {completedExecutionId};
            """);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_schedule_state_coordinates",
            $"""
            UPDATE retention.schedule_state
            SET "LastExecutionId" = {Guid.Empty}
            WHERE "LastExecutionId" = {completedExecutionId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_schedule_state_versions",
            $"""
            UPDATE retention.schedule_state
            SET "Version" = {1L}
            WHERE "LastExecutionId" = {completedExecutionId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_schedule_state_time",
            $"""
            UPDATE retention.schedule_state
            SET "LastCompletedAtUtc" = "LastStartedAtUtc" - interval '1 second'
            WHERE "LastExecutionId" = {completedExecutionId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_schedule_state_target",
            $"""
            UPDATE retention.schedule_state
            SET "TargetKey" = {new string('f', 32)}
            WHERE "LastExecutionId" = {blockedExecutionId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_schedule_state_result",
            $"""
            UPDATE retention.schedule_state
            SET "ConsecutiveFailures" = {1}
            WHERE "LastExecutionId" = {completedExecutionId};
            """);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_run_retry_request_coordinates",
            $"""
            UPDATE retention.run_retry_requests
            SET "RunId" = {Guid.Empty}
            WHERE "Id" = {appliedRetryId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_run_retry_request_coordinates",
            $"""
            UPDATE retention.run_retry_requests
            SET "FailureCode" = {"invalid failure"}
            WHERE "Id" = {failedRetryId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_run_retry_request_target",
            $"""
            UPDATE retention.run_retry_requests
            SET "PropertyId" = {Guid.Empty}
            WHERE "Id" = {failedRetryId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_run_retry_request_versions",
            $"""
            UPDATE retention.run_retry_requests
            SET "Version" = {1L}
            WHERE "Id" = {appliedRetryId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_run_retry_request_state",
            $"""
            UPDATE retention.run_retry_requests
            SET "FailureCode" = {"task-run-unavailable"}
            WHERE "Id" = {pendingRetryId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_run_retry_request_timestamps",
            $"""
            UPDATE retention.run_retry_requests
            SET "ScheduledAtUtc" = "RequestedAtUtc" - interval '1 second'
            WHERE "Id" = {pendingRetryId};
            """);

        await migrator.MigrateAsync(PreviousMigration);
    }

    private static RetentionExecution Start(
        Guid id,
        string ownerKey,
        string dataClassKey,
        RetentionExecutionTargetKind targetKind,
        Guid? propertyId,
        DateTimeOffset startedAtUtc) => RetentionExecution.Start(
        id,
        ScopeId,
        ownerKey,
        dataClassKey,
        targetKind,
        propertyId,
        executionPolicyVersion: 1,
        attempt: 1,
        startedAtUtc,
        startedAtUtc.AddMinutes(5)).Value;

    private static RetentionRunRetryRequest CreateRetry(
        Guid id,
        RetentionExecution execution,
        long evidenceVersion,
        DateTimeOffset requestedAtUtc) => RetentionRunRetryRequest.Create(
        id,
        Guid.NewGuid(),
        ScopeId,
        execution.Id,
        execution.OwnerKey,
        execution.DataClassKey,
        execution.TargetKind,
        execution.PropertyId,
        execution.ExecutionPolicyVersion,
        evidenceVersion,
        requestedAtUtc,
        scheduledAtUtc: null).Value;

    private static async Task AssertConstraintViolationAsync(
        RetentionDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static RetentionDbContext CreateDbContext(string connectionString)
    {
        DbContextOptions<RetentionDbContext> options =
            new DbContextOptionsBuilder<RetentionDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(RetentionMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        RetentionMigrations.HistoryTable,
                        RetentionMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => RetentionControlPlaneStateIntegrityMigrationIntegrationTests.ScopeId;
    }
}

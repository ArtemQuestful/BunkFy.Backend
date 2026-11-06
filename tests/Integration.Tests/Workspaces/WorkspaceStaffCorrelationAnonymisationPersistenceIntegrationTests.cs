namespace Integration.Tests;

using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class
    WorkspaceStaffCorrelationAnonymisationPersistenceIntegrationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string SubjectId = "account-subject-a";
    private static readonly Guid StaffMemberId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OnboardingId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid AccessPlanId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid AccessProfileId =
        Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid AnchorProcessId =
        Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid OwnerReceiptId =
        Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid IdempotencyKey =
        Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid CaseId =
        Guid.Parse("80000000-0000-0000-0000-000000000001");
    private static readonly Guid LedgerEntryId =
        Guid.Parse("90000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Staff_correlation_anonymisation_is_atomic_restorable_scoped_and_append_only()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase(
                    "bunkfy_workspace_correlation_anonymisation_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();

        await SeedOriginalStateAsync(connectionString);

        AppliedProof rolledBack = await ApplyAsync(
            connectionString,
            commit: false);
        await AssertOriginalStateAsync(connectionString);

        AppliedProof committed = await ApplyAsync(
            connectionString,
            commit: true);
        Assert.Equal(
            rolledBack.Selected.StateSha256,
            committed.Selected.StateSha256);
        await AssertAnonymisedStateAsync(
            connectionString,
            committed.Receipt);
        await AssertOwnerReceiptAppendOnlyAsync(
            connectionString,
            committed.Receipt.Id);

        await ResetToOriginalStateAsync(connectionString);
        WorkspaceStaffCorrelationAnonymisationRestoreRequest
            restoreRequest = new(
                TenantA,
                LedgerEntryId,
                TenantSequence: 13,
                new string('d', 64),
                AnchorProcessId,
                committed.Receipt.ContractVersion,
                committed.Receipt.Id,
                committed.Receipt.CanonicalSha256,
                committed.Receipt.ResultingAnchorVersion,
                committed.Receipt.CompletedAtUtc,
                Now.AddHours(1));
        WorkspaceStaffCorrelationAnonymisationRestoreReceipt
            restored = await RestoreAsync(
                connectionString,
                TenantA,
                restoreRequest);
        Assert.True(restored.HasValidCanonicalProof());
        Assert.Equal(13, restored.TenantSequence);
        Assert.Equal(
            restoreRequest.LedgerEntrySha256,
            restored.LedgerEntrySha256);
        await AssertRestoredStateAsync(
            connectionString,
            committed.Receipt,
            restored);

        WorkspaceStaffCorrelationAnonymisationRestoreReceipt replay =
            await RestoreAsync(
                connectionString,
                TenantA,
                restoreRequest);
        Assert.Equal(
            restored.CanonicalSha256,
            replay.CanonicalSha256);

        Result<WorkspaceStaffCorrelationAnonymisationRestoreReceipt>
            crossTenant = await TryRestoreAsync(
                connectionString,
                TenantB,
                restoreRequest with { TenantId = TenantB });
        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationApplicationErrors
                .RestoreProofConflict,
            crossTenant.Error);

        await AssertRestoreReceiptAppendOnlyAsync(
            connectionString,
            restored.Id);
    }

    private static async Task<AppliedProof> ApplyAsync(
        string connectionString,
        bool commit)
    {
        await using ServiceProvider provider =
            CreatePersistenceProvider(connectionString, TenantA);
        await using AsyncServiceScope scope =
            provider.CreateAsyncScope();
        WorkspacesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        IWorkspaceStaffCorrelationAnonymisationRepository repository =
            scope.ServiceProvider.GetRequiredService<
                IWorkspaceStaffCorrelationAnonymisationRepository>();
        IWorkspaceStaffCorrelationOperationLock operationLock =
            scope.ServiceProvider.GetRequiredService<
                IWorkspaceStaffCorrelationOperationLock>();
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync();

        WorkspaceStaffAccessProcess anchor =
            await dbContext.StaffAccessProcesses
                .SingleAsync(process =>
                    process.Id == AnchorProcessId);
        Assert.True(await operationLock.TryAcquireAsync(
            AnchorProcessId,
            CancellationToken.None));
        WorkspaceStaffCorrelationAnonymisationSnapshot selected =
            await repository.ReadAsync(
                TenantA,
                AnchorProcessId,
                anchor.Version,
                CancellationToken.None);
        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                .Eligible,
            selected.Status);

        Result<WorkspaceStaffCorrelationAnonymisationReceipt> applied =
            await repository.ApplyAsync(
                new(
                    OwnerReceiptId,
                    TenantA,
                    IdempotencyKey,
                    CaseId,
                    ApprovalRevision: 4,
                    OperationRevision: 5,
                    selected,
                    new string('a', 64),
                    new string('b', 64),
                    "user:privacy-executor",
                    Now.AddMinutes(10)),
                CancellationToken.None);
        Assert.True(applied.IsSuccess, applied.Error.Code);
        await dbContext.SaveChangesAsync();

        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return new(selected, applied.Value);
    }

    private static async Task<
        WorkspaceStaffCorrelationAnonymisationRestoreReceipt>
        RestoreAsync(
            string connectionString,
            string tenantId,
            WorkspaceStaffCorrelationAnonymisationRestoreRequest
                request)
    {
        Result<WorkspaceStaffCorrelationAnonymisationRestoreReceipt>
            restored = await TryRestoreAsync(
                connectionString,
                tenantId,
                request);
        Assert.True(restored.IsSuccess, restored.Error.Code);
        return restored.Value;
    }

    private static async Task<Result<
        WorkspaceStaffCorrelationAnonymisationRestoreReceipt>>
        TryRestoreAsync(
            string connectionString,
            string tenantId,
            WorkspaceStaffCorrelationAnonymisationRestoreRequest
                request)
    {
        await using ServiceProvider provider =
            CreatePersistenceProvider(connectionString, tenantId);
        await using AsyncServiceScope scope =
            provider.CreateAsyncScope();
        WorkspacesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        IWorkspaceStaffCorrelationAnonymisationRepository repository =
            scope.ServiceProvider.GetRequiredService<
                IWorkspaceStaffCorrelationAnonymisationRepository>();
        IWorkspaceStaffCorrelationOperationLock operationLock =
            scope.ServiceProvider.GetRequiredService<
                IWorkspaceStaffCorrelationOperationLock>();
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync();

        bool locked = await operationLock.TryAcquireAsync(
            request.AnchorProcessId,
            CancellationToken.None);
        if (!locked)
        {
            await transaction.RollbackAsync();
            return Result.Failure<
                WorkspaceStaffCorrelationAnonymisationRestoreReceipt>(
                WorkspaceStaffCorrelationAnonymisationApplicationErrors
                    .RestoreProofConflict);
        }

        Result<WorkspaceStaffCorrelationAnonymisationRestoreReceipt>
            restored = await repository.RestoreAsync(
                request,
                CancellationToken.None);
        if (restored.IsSuccess)
        {
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return restored;
    }

    private static async Task SeedOriginalStateAsync(
        string connectionString)
    {
        await using WorkspacesDbContext dbContext =
            CreateDbContext(connectionString, TenantA);
        await dbContext.Database.MigrateAsync();
        WorkspaceStaffOnboarding onboarding = SeedOriginalState(dbContext);
        await dbContext.SaveChangesAsync();

        Assert.True(onboarding.ObserveInvitationAccepted(
            Now.AddMinutes(1)).IsSuccess);
        await dbContext.SaveChangesAsync();

        Assert.True(onboarding.MarkStaffReady(
            StaffMemberId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        await dbContext.SaveChangesAsync();

        Assert.True(onboarding.Complete(
            Now.AddMinutes(3)).IsSuccess);
        await dbContext.SaveChangesAsync();
    }

    private static async Task ResetToOriginalStateAsync(
        string connectionString)
    {
        await using (WorkspacesDbContext dbContext =
            CreateDbContext(connectionString, TenantA))
        {
            await dbContext.Database.EnsureDeletedAsync();
        }

        await SeedOriginalStateAsync(connectionString);
    }

    private static WorkspaceStaffOnboarding SeedOriginalState(
        WorkspacesDbContext dbContext)
    {
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                OnboardingId,
                TenantA,
                WorkspaceStaffOnboardingSource.Invitation,
                AccessPlanId,
                SubjectId,
                "staff@example.test",
                "Staff member",
                legalName: null,
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                Now).Value;
        WorkspaceStaffAccessProcess anchor =
            WorkspaceStaffAccessProcess.Create(
                AnchorProcessId,
                TenantA,
                StaffMemberId,
                SubjectId,
                WorkspaceStaffAccessTargetState.Departed,
                targetStaffVersion: 7,
                new DateOnly(2026, 7, 30),
                SubjectId,
                [],
                Now.AddMinutes(4)).Value;
        Assert.True(anchor.MarkAwaitingStaffCommit(
            Now.AddMinutes(5)).IsSuccess);
        Assert.True(anchor.ObserveStaffCommit(
            Now.AddMinutes(6)).IsSuccess);

        WorkspaceStaffAccessPlan plan =
            WorkspaceStaffAccessPlan.Create(
                AccessPlanId,
                TenantA,
                WorkspaceStaffOnboardingSource.Invitation,
                AccessProfileId,
                "front-desk",
                [],
                SubjectId,
                Now).Value;
        Assert.True(plan.Activate(
            Now.AddMinutes(1)).IsSuccess);

        dbContext.AddRange(onboarding, anchor, plan);
        return onboarding;
    }

    private static async Task AssertOriginalStateAsync(
        string connectionString)
    {
        await using WorkspacesDbContext dbContext =
            CreateDbContext(connectionString, TenantA);
        Assert.Equal(
            SubjectId,
            (await dbContext.StaffOnboardingApplications
                .SingleAsync(record =>
                    record.Id == OnboardingId)).SubjectId);
        WorkspaceStaffAccessProcess anchor =
            await dbContext.StaffAccessProcesses
                .SingleAsync(process =>
                    process.Id == AnchorProcessId);
        Assert.Equal(SubjectId, anchor.SubjectId);
        Assert.Equal(SubjectId, anchor.RequestedBy);
        Assert.Equal(
            SubjectId,
            (await dbContext.StaffAccessPlans
                .SingleAsync(plan =>
                    plan.Id == AccessPlanId))
                .CreatedBySubjectId);
        Assert.Empty(
            await dbContext
                .StaffCorrelationAnonymisationReceipts
                .ToArrayAsync());
        Assert.Empty(
            await dbContext
                .StaffCorrelationAnonymisationTombstones
                .ToArrayAsync());
    }

    private static async Task AssertAnonymisedStateAsync(
        string connectionString,
        WorkspaceStaffCorrelationAnonymisationReceipt expected)
    {
        await using WorkspacesDbContext dbContext =
            CreateDbContext(connectionString, TenantA);
        string pseudonym = expected.CreateSubjectPseudonym();
        Assert.Equal(
            pseudonym,
            (await dbContext.StaffOnboardingApplications
                .SingleAsync(record =>
                    record.Id == OnboardingId)).SubjectId);
        WorkspaceStaffAccessProcess anchor =
            await dbContext.StaffAccessProcesses
                .SingleAsync(process =>
                    process.Id == AnchorProcessId);
        Assert.Equal(pseudonym, anchor.SubjectId);
        Assert.Equal(pseudonym, anchor.RequestedBy);
        Assert.Equal(
            pseudonym,
            (await dbContext.StaffAccessPlans
                .SingleAsync(plan =>
                    plan.Id == AccessPlanId))
                .CreatedBySubjectId);
        WorkspaceStaffCorrelationAnonymisationReceipt receipt =
            Assert.Single(
                await dbContext
                    .StaffCorrelationAnonymisationReceipts
                    .ToArrayAsync());
        Assert.Equal(expected.CanonicalSha256, receipt.CanonicalSha256);
        Assert.True(receipt.HasValidCanonicalProof());
        WorkspaceStaffCorrelationAnonymisationTombstone tombstone =
            Assert.Single(
                await dbContext
                    .StaffCorrelationAnonymisationTombstones
                    .ToArrayAsync());
        Assert.True(tombstone.Matches(receipt));
        Assert.False(await HasOriginalSubjectAsync(dbContext));
    }

    private static async Task AssertRestoredStateAsync(
        string connectionString,
        WorkspaceStaffCorrelationAnonymisationReceipt ownerReceipt,
        WorkspaceStaffCorrelationAnonymisationRestoreReceipt
            expectedRestore)
    {
        await using WorkspacesDbContext dbContext =
            CreateDbContext(connectionString, TenantA);
        Assert.False(await HasOriginalSubjectAsync(dbContext));
        string pseudonym = ownerReceipt.CreateSubjectPseudonym();
        Assert.Equal(
            pseudonym,
            (await dbContext.StaffAccessProcesses
                .SingleAsync(process =>
                    process.Id == AnchorProcessId)).SubjectId);
        Assert.Empty(
            await dbContext
                .StaffCorrelationAnonymisationReceipts
                .ToArrayAsync());
        WorkspaceStaffCorrelationAnonymisationRestoreReceipt receipt =
            Assert.Single(
                await dbContext
                    .StaffCorrelationAnonymisationRestoreReceipts
                    .ToArrayAsync());
        Assert.Equal(
            expectedRestore.CanonicalSha256,
            receipt.CanonicalSha256);
        WorkspaceStaffCorrelationAnonymisationTombstone tombstone =
            Assert.Single(
                await dbContext
                    .StaffCorrelationAnonymisationTombstones
                    .ToArrayAsync());
        Assert.True(tombstone.MatchesRestore(
            expectedRestore.LedgerEntryId,
            expectedRestore.OwnerReceiptContractVersion,
            expectedRestore.OwnerReceiptId,
            expectedRestore.OwnerReceiptSha256,
            expectedRestore.ResultingAnchorVersion,
            expectedRestore.OriginallyCompletedAtUtc));
    }

    private static async Task<bool> HasOriginalSubjectAsync(
        WorkspacesDbContext dbContext)
    {
        if (await dbContext.StaffOnboardingApplications.AnyAsync(
                record => record.SubjectId == SubjectId))
        {
            return true;
        }

        if (await dbContext.StaffAccessProcesses.AnyAsync(
                process =>
                    process.SubjectId == SubjectId ||
                    process.RequestedBy == SubjectId))
        {
            return true;
        }

        return await dbContext.StaffAccessPlans.AnyAsync(
            plan => plan.CreatedBySubjectId == SubjectId);
    }

    private static async Task AssertOwnerReceiptAppendOnlyAsync(
        string connectionString,
        Guid receiptId)
    {
        await using WorkspacesDbContext update =
            CreateDbContext(connectionString, TenantA);
        PostgresException updateFailure =
            await Assert.ThrowsAsync<PostgresException>(
                () => update.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE
                        workspaces.staff_correlation_anonymisation_receipts
                    SET
                        "ActorId" = {"tampered"}
                    WHERE
                        "Id" = {receiptId}
                    """));
        Assert.Equal(PostgresErrorCodes.RaiseException, updateFailure.SqlState);

        await using WorkspacesDbContext delete =
            CreateDbContext(connectionString, TenantA);
        PostgresException deleteFailure =
            await Assert.ThrowsAsync<PostgresException>(
                () => delete.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM
                        workspaces.staff_correlation_anonymisation_receipts
                    WHERE
                        "Id" = {receiptId}
                    """));
        Assert.Equal(PostgresErrorCodes.RaiseException, deleteFailure.SqlState);
    }

    private static async Task AssertRestoreReceiptAppendOnlyAsync(
        string connectionString,
        Guid receiptId)
    {
        await using WorkspacesDbContext update =
            CreateDbContext(connectionString, TenantA);
        PostgresException updateFailure =
            await Assert.ThrowsAsync<PostgresException>(
                () => update.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE
                        workspaces.staff_correlation_anonymisation_restore_receipts
                    SET
                        "TenantSequence" = "TenantSequence" + 1
                    WHERE
                        "Id" = {receiptId}
                    """));
        Assert.Equal(PostgresErrorCodes.RaiseException, updateFailure.SqlState);

        await using WorkspacesDbContext delete =
            CreateDbContext(connectionString, TenantA);
        PostgresException deleteFailure =
            await Assert.ThrowsAsync<PostgresException>(
                () => delete.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM
                        workspaces.staff_correlation_anonymisation_restore_receipts
                    WHERE
                        "Id" = {receiptId}
                    """));
        Assert.Equal(PostgresErrorCodes.RaiseException, deleteFailure.SqlState);
    }

    private static ServiceProvider CreatePersistenceProvider(
        string connectionString,
        string tenantId)
    {
        HostApplicationBuilder builder =
            Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] =
            "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(
            new TestScopeContext(tenantId));
        builder.AddWorkspacesPersistence();
        return builder.Services.BuildServiceProvider();
    }

    private static WorkspacesDbContext CreateDbContext(
        string connectionString,
        string tenantId)
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseNpgsql(
                    connectionString,
                    postgreSql => postgreSql
                        .MigrationsAssembly(
                            WorkspacesMigrations
                                .PostgreSqlAssembly)
                        .MigrationsHistoryTable(
                            WorkspacesMigrations.HistoryTable,
                            WorkspacesMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(tenantId));
    }

    private sealed class TestScopeContext(string tenantId)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = tenantId;
    }

    private sealed record AppliedProof(
        WorkspaceStaffCorrelationAnonymisationSnapshot Selected,
        WorkspaceStaffCorrelationAnonymisationReceipt Receipt);
}

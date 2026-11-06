namespace Integration.Tests;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class TenantTerminationPersistenceIntegrationTests
{
    private const string PreviousMigration =
        "20260815130159_BindDataRightsRestrictionReleaseTarget";
    private static readonly string Digest = new('a', 64);
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migration_fences_one_active_process_and_exact_owner_coordinates()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_tenant_termination_foundation_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();

        await using DataRightsDbContext tenantA = CreateDbContext(
            connectionString,
            "tenant-a");
        IMigrator migrator = tenantA.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        DataRightsExportAuditEntry existingAudit =
            DataRightsExportAuditEntry.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                propertyId: null,
                DataRightsCaseKind.StaffRights,
                DataRightsExportAuditAction.Download,
                "operator:privacy",
                "succeeded",
                Now).Value;
        tenantA.ExportAuditEntries.Add(existingAudit);
        await tenantA.SaveChangesAsync();

        await tenantA.Database.MigrateAsync();
        Assert.Empty(await tenantA.Database.GetPendingMigrationsAsync());
        Assert.Equal(
            existingAudit.Id,
            (await tenantA.ExportAuditEntries.SingleAsync()).Id);
        DataRightsExportAuditEntry tenantTerminationAudit =
            DataRightsExportAuditEntry.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                propertyId: null,
                DataRightsCaseKind.TenantTermination,
                DataRightsExportAuditAction.DeletionStarted,
                "system:tenant-termination-export-retention",
                "started",
                Now.AddMinutes(1)).Value;
        tenantA.ExportAuditEntries.Add(tenantTerminationAudit);
        await tenantA.SaveChangesAsync();

        DataRightsCase firstCase = CreateCase(
            "tenant-a",
            Now.AddMinutes(-3));
        TenantTerminationProcess firstProcess = PrepareProcess(
            "tenant-a",
            firstCase);
        DataRightsCase deniedCase = CreateDeniedCase(
            "tenant-a",
            Now.AddMinutes(-3));
        tenantA.Cases.AddRange(firstCase, deniedCase);
        tenantA.TenantTerminationProcesses.Add(firstProcess);
        await tenantA.SaveChangesAsync();

        TenantTerminationProcess duplicateActive =
            TenantTerminationProcess.Prepare(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                deniedCase.Id,
                deniedCase.DecisionRevision!.Value,
                Guid.NewGuid(),
                exportRequested: false,
                Digest,
                "owner:approver",
                deniedCase.DecidedAtUtc!.Value,
                "system:tenant-termination",
                Now).Value;
        tenantA.TenantTerminationProcesses.Add(duplicateActive);
        DbUpdateException duplicateFailure =
            await Assert.ThrowsAsync<DbUpdateException>(
                () => tenantA.SaveChangesAsync());
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(
                duplicateFailure.GetBaseException()).SqlState);
        tenantA.ChangeTracker.Clear();

        firstCase = await tenantA.Cases.SingleAsync(candidate =>
            candidate.Id == firstCase.Id);
        firstProcess = await tenantA.TenantTerminationProcesses
            .SingleAsync(candidate => candidate.Id == firstProcess.Id);
        CancelProcess(firstProcess);
        Assert.True(firstCase.CompleteTenantTerminationCancellation(
            firstProcess.ApprovalRevision,
            firstProcess.PolicyEvidenceSha256,
            firstCase.Version,
            "system:tenant-termination",
            Now.AddMinutes(6)).IsSuccess);
        await tenantA.SaveChangesAsync();

        DataRightsCase secondCase = CreateCase(
            "tenant-a",
            Now.AddMinutes(7));
        TenantTerminationProcess secondProcess = PrepareProcess(
            "tenant-a",
            secondCase);
        Assert.True(secondProcess.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            secondProcess.Version,
            "system:tenant-termination",
            Now.AddMinutes(10)).IsSuccess);
        tenantA.Cases.Add(secondCase);
        tenantA.TenantTerminationProcesses.Add(secondProcess);
        await tenantA.SaveChangesAsync();

        TenantTerminationOwnerWorkItem mismatched = PrepareOwnerWork(
            secondProcess,
            firstCase.Id,
            Guid.NewGuid());
        tenantA.TenantTerminationOwnerWorkItems.Add(mismatched);
        DbUpdateException mismatch =
            await Assert.ThrowsAsync<DbUpdateException>(
                () => tenantA.SaveChangesAsync());
        Assert.Equal(
            PostgresErrorCodes.ForeignKeyViolation,
            Assert.IsType<PostgresException>(
                mismatch.GetBaseException()).SqlState);
        tenantA.ChangeTracker.Clear();

        TenantTerminationOwnerWorkItem valid = PrepareOwnerWork(
            secondProcess,
            secondProcess.CaseId,
            Guid.NewGuid());
        tenantA.TenantTerminationOwnerWorkItems.Add(valid);
        await tenantA.SaveChangesAsync();
        Assert.Equal(
            valid.Id,
            (await tenantA.TenantTerminationOwnerWorkItems.SingleAsync()).Id);

        await using DataRightsDbContext tenantB = CreateDbContext(
            connectionString,
            "tenant-b");
        DataRightsCase tenantBCase = CreateCase(
            "tenant-b",
            Now.AddMinutes(-3));
        TenantTerminationProcess tenantBProcess = PrepareProcess(
            "tenant-b",
            tenantBCase);
        tenantB.Cases.Add(tenantBCase);
        tenantB.TenantTerminationProcesses.Add(tenantBProcess);
        await tenantB.SaveChangesAsync();
        Assert.Equal(
            tenantBProcess.Id,
            (await tenantB.TenantTerminationProcesses.SingleAsync()).Id);
    }

    private static void CancelProcess(TenantTerminationProcess process)
    {
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            "system:tenant-termination",
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(process.CompleteFreeze(
            process.OperationRevision,
            workspaceFenceRevision: 1,
            Digest,
            [new("workspaces", 1, 1, Digest)],
            process.Version,
            "system:tenant-termination",
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(process.RequestCancellation(
            process.Version,
            "system:tenant-termination",
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Restore,
            process.Version,
            "system:tenant-termination",
            Now.AddMinutes(4)).IsSuccess);
        Assert.True(process.CompleteCancellation(
            process.OperationRevision,
            process.Version,
            "system:tenant-termination",
            Now.AddMinutes(5)).IsSuccess);
    }

    private static DataRightsCase CreateCase(
        string tenantId,
        DateTimeOffset requestedAtUtc)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.TenantTermination,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.TenantOwner).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            tenantId,
            request,
            "owner:requester",
            requestedAtUtc).Value;
        Assert.True(dataRightsCase.PrepareTenantTerminationReview(
            exportRequested: false,
            dataRightsCase.Version,
            "owner:requester",
            requestedAtUtc).IsSuccess);
        Assert.True(dataRightsCase.RecordTenantTerminationDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            Digest,
            dataRightsCase.Version,
            "owner:approver",
            requestedAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.BeginTenantTerminationExecution(
            dataRightsCase.Version,
            "system:tenant-termination",
            requestedAtUtc.AddMinutes(2)).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsCase CreateDeniedCase(
        string tenantId,
        DateTimeOffset requestedAtUtc)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.TenantTermination,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.TenantOwner).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            tenantId,
            request,
            "owner:requester",
            requestedAtUtc).Value;
        Assert.True(dataRightsCase.PrepareTenantTerminationReview(
            exportRequested: false,
            dataRightsCase.Version,
            "owner:requester",
            requestedAtUtc).IsSuccess);
        Assert.True(dataRightsCase.RecordTenantTerminationDecision(
            DataRightsCaseDecision.Denied,
            DataRightsCaseDecisionReason.LegalObligation,
            policyEvidenceSha256: null,
            dataRightsCase.Version,
            "owner:approver",
            requestedAtUtc.AddMinutes(1)).IsSuccess);
        return dataRightsCase;
    }

    private static TenantTerminationProcess PrepareProcess(
        string tenantId,
        DataRightsCase dataRightsCase) =>
        TenantTerminationProcess.Prepare(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            dataRightsCase.Id,
            dataRightsCase.DecisionRevision!.Value,
            Guid.NewGuid(),
            exportRequested: false,
            dataRightsCase.TenantTerminationPolicyEvidenceSha256!,
            dataRightsCase.DecidedBy!,
            dataRightsCase.DecidedAtUtc!.Value,
            "system:tenant-termination",
            dataRightsCase.ExecutionStartedAtUtc!.Value).Value;

    private static TenantTerminationOwnerWorkItem PrepareOwnerWork(
        TenantTerminationProcess process,
        Guid caseId,
        Guid idempotencyKey) =>
        TenantTerminationOwnerWorkItem.Prepare(
            Guid.NewGuid(),
            process.ScopeId,
            process.Id,
            caseId,
            process.ApprovalRevision,
            process.OperationRevision,
            process.TerminationEpoch,
            idempotencyKey,
            TenantTerminationOwnerPhase.Freeze,
            "workspaces",
            ownerContractVersion: 1,
            catalogVersion: 1,
            Digest,
            process.PolicyEvidenceSha256,
            Now.AddMinutes(10)).Value;

    private static DataRightsDbContext CreateDbContext(
        string connectionString,
        string tenantId)
    {
        DbContextOptions<DataRightsDbContext> options =
            new DbContextOptionsBuilder<DataRightsDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(
                        DataRightsMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        DataRightsMigrations.HistoryTable,
                        DataRightsMigrations.Schema))
                .Options;
        return new(options, new TestScopeContext(tenantId));
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}

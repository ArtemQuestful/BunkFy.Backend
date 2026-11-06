namespace Integration.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.ProjectionRebuild;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using DomainFenceState =
    BunkFy.Modules.Workspaces.Domain.Termination.WorkspaceTerminationFenceState;

public sealed partial class WorkspacesDataRightsExportIntegrationTests
{
    private static readonly Guid DenseCurrentFenceId =
        Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid HistoricalFenceId =
        Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid HistoricalFenceReceiptId =
        Guid.Parse("b2000000-0000-0000-0000-000000000001");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Workspaces_tenant_destruction_is_bounded_resumable_and_tenant_isolated()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_destroy_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        TestClock clock = new(ExportNowUtc);

        using ServiceProvider tenantAProvider = CreatePersistenceProvider(
            connectionString,
            TenantA,
            clock);
        using (IServiceScope migrationScope = tenantAProvider.CreateScope())
        {
            await migrationScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>()
                .Database.MigrateAsync()
                .ConfigureAwait(false);
        }

        await SeedDenseDestroyStateAsync(tenantAProvider, clock)
            .ConfigureAwait(false);
        using (IServiceScope triggerScope = tenantAProvider.CreateScope())
        {
            await AssertHistoricalReceiptIsAppendOnlyAsync(
                    triggerScope.ServiceProvider)
                .ConfigureAwait(false);
        }

        using ServiceProvider tenantBProvider = CreatePersistenceProvider(
            connectionString,
            TenantB);
        Guid tenantBOnboardingId;
        Guid tenantBProjectionId;
        Guid tenantBDeferredClaimId;
        using (IServiceScope tenantBSeedScope = tenantBProvider.CreateScope())
        {
            (tenantBOnboardingId, tenantBProjectionId, tenantBDeferredClaimId) =
                await SeedOtherTenantGraphAsync(
                        tenantBSeedScope.ServiceProvider)
                    .ConfigureAwait(false);
        }

        WorkspaceTerminationFence fence = CreateTerminationFence();
        using IServiceScope inFlightScope = tenantAProvider.CreateScope();
        WorkspacesDbContext inFlight = inFlightScope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        await using IDbContextTransaction inFlightTransaction =
            await inFlight.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        WorkspacePropertyProjection inFlightProjection =
            await inFlight.PropertyProjections
                .OrderBy(projection => projection.Id)
                .FirstAsync()
                .ConfigureAwait(false);
        inFlightProjection.Apply(
            "Dense property updated",
            PropertyStatus.Active,
            inFlightProjection.Version + 1);
        await inFlight.SaveChangesAsync().ConfigureAwait(false);

        using (IServiceScope fenceScope = tenantAProvider.CreateScope())
        {
            WorkspacesDbContext workspaces = fenceScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            workspaces.WorkspaceTerminationFences.Add(fence);
            Task<int> persistFence = workspaces.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(250))
                .ConfigureAwait(false);
            Assert.False(persistFence.IsCompleted);
            await inFlightTransaction.CommitAsync().ConfigureAwait(false);
            await persistFence.ConfigureAwait(false);
        }

        using IServiceScope ownerScope = tenantAProvider.CreateScope();
        IWorkspaceTenantDestructionOwner owner = ownerScope.ServiceProvider
            .GetRequiredService<IWorkspaceTenantDestructionOwner>();
        TenantTerminationContributionRequest request =
            TenantDestroyRequest(fence);

        TenantTerminationContributionResult busy =
            await owner.ExecuteAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            busy.Status);
        Assert.Equal(
            "workspace.termination.destroy-outbox-busy",
            busy.ResultCode);
        Assert.Equal(0, busy.AffectedCount);

        WorkspacesDbContext ownerContext = ownerScope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        Assert.Equal(
            1,
            await ReadDestroyOperationCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            (int)DomainFenceState.DestructionStarted,
            await ReadFenceStateAsync(ownerContext, DenseCurrentFenceId)
                .ConfigureAwait(false));

        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        using (IServiceScope claimScope = tenantAProvider.CreateScope())
        {
            IOutboxStore outbox = claimScope.ServiceProvider
                .GetServices<IOutboxStore>()
                .Single(store => store.ModuleName ==
                    WorkspacesModuleMetadata.Name);
            IReadOnlyList<OutboxMessageRecord> claims =
                await outbox.ClaimPendingAsync(
                    batchSize: 10,
                    "workspaces-claim-test",
                    clock.UtcNow,
                    TimeSpan.FromMinutes(1),
                    CancellationToken.None).ConfigureAwait(false);
            Assert.Empty(claims);
        }

        List<long> progressCounts = [];
        TenantTerminationContributionResult completed = busy;
        int attempts = 0;
        while (completed.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 40)
        {
            completed = await owner.ExecuteAsync(
                request,
                CancellationToken.None).ConfigureAwait(false);
            attempts++;
            if (completed.Status ==
                TenantTerminationContributionStatus.RetryRequired)
            {
                Assert.Equal(
                    "workspace.termination.destroy-in-progress",
                    completed.ResultCode);
                progressCounts.Add(completed.AffectedCount);
            }
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            completed.Status);
        Assert.Equal(
            "workspace.termination.destroyed",
            completed.ResultCode);
        Assert.Equal(1025, completed.AffectedCount);
        Assert.Equal(1, completed.SelectedProofRevision);
        Assert.Equal(3, completed.ResultingProofRevision);
        Assert.Contains(3, progressCounts);
        Assert.Contains(503, progressCounts);
        Assert.Contains(504, progressCounts);
        long previousCount = 0;
        foreach (long progressCount in progressCounts)
        {
            Assert.InRange(progressCount - previousCount, 1, 500);
            previousCount = progressCount;
        }

        TenantTerminationContributionResult replay =
            await owner.ExecuteAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        Assert.Equal(completed, replay);
        TenantTerminationContributionResult conflict =
            await owner.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);
        Assert.Equal(
            "workspace.termination.destroy-conflict",
            conflict.ResultCode);

        Assert.Equal(
            0,
            await ReadDestructibleOwnerRecordCountAsync(
                    ownerContext,
                    TenantA,
                    DenseCurrentFenceId)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await ReadDestroyOperationCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await ReadDestroyReceiptCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            1025,
            await ReadDestroyReceiptRemovedCountAsync(ownerContext, TenantA)
                .ConfigureAwait(false));
        Assert.Equal(
            (int)DomainFenceState.Closed,
            await ReadFenceStateAsync(ownerContext, DenseCurrentFenceId)
                .ConfigureAwait(false));
        Assert.Equal(
            3,
            await ReadFenceVersionAsync(ownerContext, DenseCurrentFenceId)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await ReadCloseFenceReceiptCountAsync(
                    ownerContext,
                    TenantA,
                    DenseCurrentFenceId)
                .ConfigureAwait(false));

        using (IServiceScope closedScope = tenantAProvider.CreateScope())
        {
            WorkspacesDbContext closedContext = closedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            closedContext.PropertyProjections.Add(
                new WorkspacePropertyProjection(
                    Guid.NewGuid().ToString("D"),
                    Guid.NewGuid(),
                    "Rejected",
                    PropertyStatus.Active,
                    version: 1));
            InvalidOperationException closed =
                await Assert.ThrowsAnyAsync<InvalidOperationException>(
                    () => closedContext.SaveChangesAsync())
                    .ConfigureAwait(false);
            Assert.Equal(
                "The workspace is not accepting operational mutations.",
                closed.Message);
        }

        PostgresException receiptMutation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ownerContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE workspaces.tenant_destroy_receipts
                    SET "RemovedRecordCount" = "RemovedRecordCount"
                    WHERE "ScopeId" = {TenantA};
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", receiptMutation.SqlState);
        Assert.Contains(
            "workspace tenant destruction receipts are append-only",
            receiptMutation.MessageText,
            StringComparison.Ordinal);

        PostgresException fenceDeletion =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ownerContext.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM workspaces.workspace_termination_fences
                    WHERE "Id" = {DenseCurrentFenceId};
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", fenceDeletion.SqlState);
        Assert.Contains(
            "workspace termination fences are historical evidence",
            fenceDeletion.MessageText,
            StringComparison.Ordinal);

        using (IServiceScope tenantBVerificationScope =
            tenantBProvider.CreateScope())
        {
            WorkspacesDbContext tenantB = tenantBVerificationScope
                .ServiceProvider.GetRequiredService<WorkspacesDbContext>();
            Assert.Equal(
                8,
                await ReadDestructibleOwnerRecordCountAsync(
                        tenantB,
                        TenantB,
                        Guid.Empty)
                    .ConfigureAwait(false));
            Assert.Equal(
                tenantBOnboardingId,
                (await tenantB.StaffOnboardingApplications.SingleAsync()
                    .ConfigureAwait(false)).Id);
            Assert.Equal(
                tenantBProjectionId,
                (await tenantB.PropertyProjections.SingleAsync()
                    .ConfigureAwait(false)).Id);
            Assert.Equal(
                tenantBDeferredClaimId,
                (await tenantB.StaffDeferredClaimWithdrawals.SingleAsync()
                    .ConfigureAwait(false)).Id);
        }
    }

    private static async Task SeedDenseDestroyStateAsync(
        IServiceProvider services,
        TestClock clock)
    {
        using IServiceScope scope = services.CreateScope();
        WorkspacesDbContext context = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        _ = await SeedGraphAsync(context, TenantA, SubjectId);
        WorkspaceStaffOnboarding onboarding =
            context.StaffOnboardingApplications.Local.Single();

        WorkspaceStaffOnboardingCorrectionReceipt correction =
            WorkspaceStaffOnboardingCorrectionReceipt.Create(
                Guid.Parse("c1000000-0000-0000-0000-000000000001"),
                TenantA,
                Guid.Parse("c2000000-0000-0000-0000-000000000001"),
                Guid.Parse("c3000000-0000-0000-0000-000000000001"),
                approvalRevision: 1,
                onboarding.Id,
                onboarding.Version,
                onboarding.Version + 1,
                [WorkspaceStaffOnboardingApplicantField.DisplayName],
                Digest,
                Guid.Parse("c4000000-0000-0000-0000-000000000001"),
                Guid.Parse("c5000000-0000-0000-0000-000000000001"),
                clock.UtcNow).Value;
        WorkspaceStaffOnboardingProcessingRestriction restriction =
            WorkspaceStaffOnboardingProcessingRestriction.Create(
                Guid.Parse("c6000000-0000-0000-0000-000000000001"),
                TenantA,
                onboarding.Id,
                Guid.Parse("c7000000-0000-0000-0000-000000000001"),
                applyApprovalRevision: 1,
                onboarding.Version,
                "privacy-operator",
                clock.UtcNow).Value;
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
                TenantA,
                onboarding.Id,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                clock.UtcNow).Value;
        Assert.True(projection.Apply(
            expectedRevision: 0,
            WorkspaceStaffOnboardingProcessingRestrictionContract
                .CurrentVersion,
            clock.UtcNow).IsSuccess);
        WorkspaceStaffOnboardingProcessingRestrictionReceipt
            restrictionReceipt =
                WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                    Guid.Parse(
                        "c8000000-0000-0000-0000-000000000001"),
                    TenantA,
                    Guid.Parse(
                        "c9000000-0000-0000-0000-000000000001"),
                    restriction.Id,
                    WorkspaceStaffOnboardingProcessingRestrictionAction.Apply,
                    onboarding.Id,
                    restriction.ApplyCaseId,
                    restriction.ApplyApprovalRevision,
                    onboarding.Version,
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                    restriction.Version,
                    projection.Revision,
                    effectiveRestricted: true,
                    "privacy-operator",
                    Guid.Parse(
                        "ca000000-0000-0000-0000-000000000001"),
                    clock.UtcNow).Value;

        Guid anchorProcessId =
            Guid.Parse("cb000000-0000-0000-0000-000000000001");
        WorkspaceStaffCorrelationAnonymisationReceipt anonymisationReceipt =
            WorkspaceStaffCorrelationAnonymisationReceipt.Create(
                Guid.Parse("cc000000-0000-0000-0000-000000000001"),
                TenantA,
                Guid.Parse("cd000000-0000-0000-0000-000000000001"),
                Guid.Parse("ce000000-0000-0000-0000-000000000001"),
                approvalRevision: 2,
                operationRevision: 3,
                anchorProcessId,
                StaffMemberId,
                selectedStaffVersion: 2,
                selectedAnchorVersion: 4,
                resultingAnchorVersion: 5,
                onboardingRecordsScrubbed: 1,
                accessProcessRecordsScrubbed: 1,
                accessPlanRecordsScrubbed: 1,
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                "privacy-operator",
                clock.UtcNow).Value;
        WorkspaceStaffCorrelationAnonymisationTombstone tombstone =
            WorkspaceStaffCorrelationAnonymisationTombstone.Create(
                anonymisationReceipt).Value;
        Guid ledgerEntryId =
            Guid.Parse("cf000000-0000-0000-0000-000000000001");
        DateTimeOffset replayedAtUtc = clock.UtcNow.AddMinutes(1);
        Assert.True(tombstone.AttachRestoreProof(
            ledgerEntryId,
            anonymisationReceipt.ContractVersion,
            anonymisationReceipt.Id,
            anonymisationReceipt.CanonicalSha256,
            anonymisationReceipt.ResultingAnchorVersion,
            anonymisationReceipt.CompletedAtUtc,
            replayedAtUtc).IsSuccess);
        WorkspaceStaffCorrelationAnonymisationRestoreReceipt restoreReceipt =
            WorkspaceStaffCorrelationAnonymisationRestoreReceipt.Create(
                TenantA,
                ledgerEntryId,
                tenantSequence: 1,
                new string('d', 64),
                anchorProcessId,
                StaffMemberId,
                anonymisationReceipt.ContractVersion,
                anonymisationReceipt.Id,
                anonymisationReceipt.CanonicalSha256,
                anonymisationReceipt.ResultingAnchorVersion,
                anonymisationReceipt.ResultingStateSha256,
                anonymisationReceipt.OnboardingRecordsScrubbed,
                anonymisationReceipt.AccessProcessRecordsScrubbed,
                anonymisationReceipt.AccessPlanRecordsScrubbed,
                anonymisationReceipt.CompletedAtUtc,
                tombstone.Revision,
                replayedAtUtc).Value;

        context.AddRange(
            correction,
            restriction,
            projection,
            restrictionReceipt,
            anonymisationReceipt,
            tombstone,
            restoreReceipt);
        for (int index = 0; index < 501; index++)
        {
            context.PropertyProjections.Add(
                new WorkspacePropertyProjection(
                    TenantA,
                    DenseGuid(index, 0xd1),
                    $"Dense property {index + 1}",
                    PropertyStatus.Active,
                    version: 1));
            context.StaffDeferredClaimWithdrawals.Add(
                WorkspaceStaffDeferredClaimWithdrawal.Create(
                    TenantA,
                    Guid.Parse(TenantA),
                    Guid.Parse("d1100000-0000-0000-0000-000000000001"),
                    DenseGuid(index, 0xd2),
                    claimVersion: index + 1,
                    DenseGuid(index, 0xd3),
                    clock.UtcNow.AddTicks(index)).Value);
        }

        OutboxMessage outbox = new(
            Guid.Parse("d2000000-0000-0000-0000-000000000001"),
            "bunkfy.workspaces.termination-test.v1",
            "termination-test",
            version: 1,
            TenantA,
            clock.UtcNow,
            "{}",
            clock.UtcNow);
        outbox.MarkClaimed(
            "workspaces-test-worker",
            clock.UtcNow,
            TimeSpan.FromMinutes(1));
        context.OutboxMessages.Add(outbox);
        context.InboxMessages.Add(InboxMessage.Create(
            Guid.Parse("d3000000-0000-0000-0000-000000000001"),
            "workspaces-termination-test-handler",
            "bunkfy.workspaces.termination-test.v1",
            "workspaces-termination-test",
            version: 1,
            TenantA,
            clock.UtcNow,
            clock.UtcNow));
        context.ProjectionRebuildCheckpoints.Add(
            WorkspaceProjectionRebuildCheckpoint.Create(
                new ProjectionRebuildCheckpointKey(
                    WorkspacesModuleMetadata.Name,
                    Guid.Parse("d4000000-0000-0000-0000-000000000001"),
                    "workspace-properties",
                    TenantA),
                ProjectionRebuildCheckpoint.Start(
                    projectionVersion: 1,
                    clock.UtcNow)));
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();

        WorkspaceTerminationFence historical =
            WorkspaceTerminationFence.Freeze(
                HistoricalFenceId,
                TenantA,
                Guid.Parse("d5000000-0000-0000-0000-000000000001"),
                Guid.Parse("d6000000-0000-0000-0000-000000000001"),
                approvalRevision: 1,
                Guid.Parse("d7000000-0000-0000-0000-000000000001"),
                Digest,
                "termination-operator",
                clock.UtcNow.AddHours(-2)).Value;
        Assert.True(historical.Release(
            historical.Version,
            "termination-operator",
            clock.UtcNow.AddHours(-1)).IsSuccess);
        WorkspaceTerminationFenceReceipt historicalReceipt =
            WorkspaceTerminationFenceReceipt.Create(
                HistoricalFenceReceiptId,
                TenantA,
                historical.Id,
                historical.ProcessId,
                historical.CaseId,
                historical.ApprovalRevision,
                operationRevision: 1,
                Guid.Parse("d8000000-0000-0000-0000-000000000001"),
                Guid.Parse("d9000000-0000-0000-0000-000000000001"),
                historical.TerminationEpoch,
                WorkspaceTerminationFenceAction.Release,
                selectedFenceVersion: 1,
                resultingFenceVersion: 2,
                DomainFenceState.Released,
                historical.PolicyEvidenceSha256,
                "termination-operator",
                clock.UtcNow.AddHours(-1)).Value;
        context.AddRange(historical, historicalReceipt);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<(
        Guid OnboardingId,
        Guid ProjectionId,
        Guid DeferredClaimId)>
        SeedOtherTenantGraphAsync(IServiceProvider services)
    {
        WorkspacesDbContext context = services
            .GetRequiredService<WorkspacesDbContext>();
        Guid onboardingId =
            Guid.Parse("e2000000-0000-0000-0000-000000000001");
        Guid sourceId =
            Guid.Parse("e3000000-0000-0000-0000-000000000001");
        Guid staffMemberId =
            Guid.Parse("e4000000-0000-0000-0000-000000000001");
        Guid propertyId =
            Guid.Parse("e5000000-0000-0000-0000-000000000001");
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                onboardingId,
                TenantB,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                "tenant-b-subject",
                "tenant-b@example.test",
                "Tenant B",
                legalName: null,
                "tenant-b@example.test",
                workPhone: null,
                employeeNumber: null,
                jobTitle: "Manager",
                department: null,
                Now).Value;
        WorkspaceStaffAccessProcess process =
            WorkspaceStaffAccessProcess.Create(
                Guid.Parse("e6000000-0000-0000-0000-000000000001"),
                TenantB,
                staffMemberId,
                "tenant-b-subject",
                WorkspaceStaffAccessTargetState.Active,
                targetStaffVersion: 2,
                new DateOnly(2026, 7, 30),
                "tenant-b-subject",
                [
                    new WorkspaceStaffAccessProfileTarget(
                        Guid.Parse(
                            "e7000000-0000-0000-0000-000000000001"),
                        $"property:{propertyId:N}")
                ],
                Now).Value;
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            sourceId,
            TenantB,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.Parse("e8000000-0000-0000-0000-000000000001"),
            "workspace-manager",
            [propertyId],
            "tenant-b-subject",
            Now).Value;
        WorkspaceStaffRetentionCorrelationReceipt retention =
            WorkspaceStaffRetentionCorrelationReceipt.Create(
                Guid.Parse("e9000000-0000-0000-0000-000000000001"),
                TenantB,
                Guid.Parse("ea000000-0000-0000-0000-000000000001"),
                staffMemberId,
                selectedStaffVersion: 1,
                onboardingRecordsScrubbed: 0,
                accessProcessRecordsScrubbed: 0,
                accessPlanRecordsScrubbed: 0,
                Now).Value;
        Guid projectionId =
            Guid.Parse("eb000000-0000-0000-0000-000000000001");
        Guid deferredClaimId =
            Guid.Parse("ec000000-0000-0000-0000-000000000001");
        WorkspaceStaffDeferredClaimWithdrawal deferred =
            WorkspaceStaffDeferredClaimWithdrawal.Create(
                TenantB,
                Guid.Parse(TenantB),
                Guid.Parse("ed000000-0000-0000-0000-000000000001"),
                deferredClaimId,
                claimVersion: 2,
                Guid.Parse("ee000000-0000-0000-0000-000000000001"),
                Now).Value;
        context.AddRange(
            onboarding,
            process,
            plan,
            retention,
            deferred,
            new WorkspacePropertyProjection(
                TenantB,
                projectionId,
                "Tenant B property",
                PropertyStatus.Active,
                version: 1));
        await context.SaveChangesAsync().ConfigureAwait(false);
        return (onboardingId, projectionId, deferredClaimId);
    }

    private static async Task AssertHistoricalReceiptIsAppendOnlyAsync(
        IServiceProvider services)
    {
        WorkspacesDbContext context = services
            .GetRequiredService<WorkspacesDbContext>();
        PostgresException mutation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE workspaces.workspace_termination_fence_receipts
                    SET "ActorId" = "ActorId"
                    WHERE "Id" = {HistoricalFenceReceiptId};
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", mutation.SqlState);
        Assert.Contains(
            "workspace receipts are append-only",
            mutation.MessageText,
            StringComparison.Ordinal);
    }

    private static TenantTerminationContributionRequest TenantDestroyRequest(
        WorkspaceTerminationFence fence) =>
        new(
            TenantTerminationContract.CurrentVersion,
            TenantA,
            fence.ProcessId,
            fence.CaseId,
            fence.ApprovalRevision,
            OperationRevision: 2,
            fence.TerminationEpoch,
            TenantTerminationContributionPhase.Destroy,
            Guid.Parse("ec000000-0000-0000-0000-000000000001"),
            Guid.Parse("ed000000-0000-0000-0000-000000000001"),
            fence.PolicyEvidenceSha256,
            "termination-executor",
            ExportNowUtc.AddMinutes(30));

    private static Guid DenseGuid(int index, byte discriminator)
    {
        byte[] bytes = new byte[16];
        BitConverter.GetBytes(index + 1).CopyTo(bytes, 0);
        bytes[15] = discriminator;
        return new Guid(bytes);
    }

    private static Task<long> ReadDestroyOperationCountAsync(
        WorkspacesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM workspaces.tenant_destroy_operations
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadDestroyReceiptCountAsync(
        WorkspacesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM workspaces.tenant_destroy_receipts
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<long> ReadDestroyReceiptRemovedCountAsync(
        WorkspacesDbContext context,
        string tenantId) =>
        context.Database.SqlQuery<long>($"""
            SELECT "RemovedRecordCount" AS "Value"
            FROM workspaces.tenant_destroy_receipts
            WHERE "ScopeId" = {tenantId}
            """).SingleAsync();

    private static Task<int> ReadFenceStateAsync(
        WorkspacesDbContext context,
        Guid fenceId) =>
        context.Database.SqlQuery<int>($"""
            SELECT "State" AS "Value"
            FROM workspaces.workspace_termination_fences
            WHERE "Id" = {fenceId}
            """).SingleAsync();

    private static Task<long> ReadFenceVersionAsync(
        WorkspacesDbContext context,
        Guid fenceId) =>
        context.Database.SqlQuery<long>($"""
            SELECT "Version" AS "Value"
            FROM workspaces.workspace_termination_fences
            WHERE "Id" = {fenceId}
            """).SingleAsync();

    private static Task<long> ReadCloseFenceReceiptCountAsync(
        WorkspacesDbContext context,
        string tenantId,
        Guid fenceId) =>
        context.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM workspaces.workspace_termination_fence_receipts
            WHERE "ScopeId" = {tenantId}
              AND "FenceId" = {fenceId}
              AND "Action" = 3
            """).SingleAsync();

    private static Task<long> ReadDestructibleOwnerRecordCountAsync(
        WorkspacesDbContext context,
        string tenantId,
        Guid retainedFenceId) =>
        context.Database.SqlQuery<long>($"""
            SELECT (
                (SELECT COUNT(*) FROM workspaces.outbox_messages WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.inbox_messages WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.projection_rebuild_checkpoints WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.property_projection WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_onboarding_correction_receipts WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_onboarding_processing_restriction_receipts WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_onboarding_processing_restriction_state WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_onboarding_processing_restrictions WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_access_plan_properties WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_access_plans WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*)
                    FROM workspaces.staff_access_profile_snapshots snapshot
                    INNER JOIN workspaces.staff_access_processes process
                        ON process."Id" = snapshot."ProcessId"
                    WHERE process."ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_access_processes WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_onboarding_applications WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_deferred_claim_withdrawals WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_retention_correlation_receipts WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_correlation_anonymisation_restore_receipts WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_correlation_anonymisation_tombstones WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.staff_correlation_anonymisation_receipts WHERE "ScopeId" = {tenantId}) +
                (SELECT COUNT(*) FROM workspaces.workspace_termination_fence_receipts WHERE "ScopeId" = {tenantId} AND "Action" <> 3) +
                (SELECT COUNT(*) FROM workspaces.workspace_termination_fences WHERE "ScopeId" = {tenantId} AND "Id" <> {retainedFenceId})
            ) AS "Value"
            """).SingleAsync();
}

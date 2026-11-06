namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspacesModelTests
{
    [Fact]
    public void Staff_onboarding_is_tenant_filtered_and_concurrency_protected()
    {
        DbContextOptions<WorkspacesDbContext> options = new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        using WorkspacesDbContext context = new(options, new TestScopeContext());
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity = context.Model
            .FindEntityType(typeof(WorkspaceStaffOnboarding))!;

        Assert.NotNull(entity.FindProperty(nameof(WorkspaceStaffOnboarding.Version))?.IsConcurrencyToken);
        Assert.True(entity.FindProperty(nameof(WorkspaceStaffOnboarding.Version))!.IsConcurrencyToken);
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffOnboarding.ScopeId),
                nameof(WorkspaceStaffOnboarding.SourceKind),
                nameof(WorkspaceStaffOnboarding.SourceId),
                nameof(WorkspaceStaffOnboarding.SubjectId)]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffOnboarding.ScopeId),
                nameof(WorkspaceStaffOnboarding.SubjectId),
                nameof(WorkspaceStaffOnboarding.Status),
                nameof(WorkspaceStaffOnboarding.Id)]));
        Microsoft.EntityFrameworkCore.Metadata.IIndex staffLookupIndex =
            Assert.Single(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffOnboarding.ScopeId),
                nameof(WorkspaceStaffOnboarding.StaffMemberId),
                nameof(WorkspaceStaffOnboarding.Id)]));
        Assert.Equal(
            "\"StaffMemberId\" IS NOT NULL",
            staffLookupIndex.GetFilter());
        Assert.NotEmpty(entity.GetDeclaredQueryFilters());
    }

    [Fact]
    public void Staff_access_process_is_tenant_filtered_versioned_and_staff_version_unique()
    {
        DbContextOptions<WorkspacesDbContext> options = new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        using WorkspacesDbContext context = new(options, new TestScopeContext());
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity = context.Model
            .FindEntityType(typeof(WorkspaceStaffAccessProcess))!;

        Assert.True(entity.FindProperty(nameof(WorkspaceStaffAccessProcess.Version))!.IsConcurrencyToken);
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffAccessProcess.ScopeId),
                nameof(WorkspaceStaffAccessProcess.StaffMemberId),
                nameof(WorkspaceStaffAccessProcess.TargetStaffVersion)]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffAccessProcess.ScopeId),
                nameof(WorkspaceStaffAccessProcess.SubjectId),
                nameof(WorkspaceStaffAccessProcess.State),
                nameof(WorkspaceStaffAccessProcess.Id)]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffAccessProcess.ScopeId),
                nameof(WorkspaceStaffAccessProcess.RequestedBy),
                nameof(WorkspaceStaffAccessProcess.Id)]));
        Assert.NotEmpty(entity.GetDeclaredQueryFilters());

        Microsoft.EntityFrameworkCore.Metadata.IEntityType snapshot = context.Model
            .FindEntityType(typeof(WorkspaceStaffAccessProfileSnapshot))!;
        Assert.Equal(
            [
                "ProcessId",
                nameof(WorkspaceStaffAccessProfileSnapshot.ProfileId),
                nameof(WorkspaceStaffAccessProfileSnapshot.AssignmentScope)
            ],
            snapshot.FindPrimaryKey()!.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(
            WorkspaceStaffAccessProfileSnapshot.AssignmentScopeMaxLength,
            snapshot.FindProperty(nameof(WorkspaceStaffAccessProfileSnapshot.AssignmentScope))!
                .GetMaxLength());
    }

    [Fact]
    public void Staff_access_plan_persists_source_expiry_and_is_concurrency_protected()
    {
        DbContextOptions<WorkspacesDbContext> options = new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        using WorkspacesDbContext context = new(options, new TestScopeContext());
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity = context.Model
            .FindEntityType(typeof(WorkspaceStaffAccessPlan))!;

        Assert.True(entity.FindProperty(nameof(WorkspaceStaffAccessPlan.Version))!.IsConcurrencyToken);
        Assert.True(entity.FindProperty(nameof(WorkspaceStaffAccessPlan.SourceExpiredAtUtc))!.IsNullable);
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffAccessPlan.ScopeId),
                nameof(WorkspaceStaffAccessPlan.SourceKind),
                nameof(WorkspaceStaffAccessPlan.SourceExpiredAtUtc),
                nameof(WorkspaceStaffAccessPlan.Id)]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffAccessPlan.ScopeId),
                nameof(WorkspaceStaffAccessPlan.CreatedBySubjectId),
                nameof(WorkspaceStaffAccessPlan.Id)]));
        Assert.NotEmpty(entity.GetDeclaredQueryFilters());
    }

    [Fact]
    public void Staff_retention_correlation_receipt_is_tenant_filtered_and_unique_per_staff_version()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        using WorkspacesDbContext context =
            new(options, new TestScopeContext());
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity =
            context.Model.FindEntityType(
                typeof(
                    WorkspaceStaffRetentionCorrelationReceipt))!;

        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(
                        WorkspaceStaffRetentionCorrelationReceipt
                            .ScopeId),
                    nameof(
                        WorkspaceStaffRetentionCorrelationReceipt
                            .StaffMemberId),
                    nameof(
                        WorkspaceStaffRetentionCorrelationReceipt
                            .SelectedStaffVersion)]));
        Assert.NotEmpty(entity.GetDeclaredQueryFilters());
    }

    [Fact]
    public async Task Staff_onboarding_correction_receipt_is_unique_and_append_only()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        await using WorkspacesDbContext context =
            new(options, new TestScopeContext());
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity =
            context.Model.FindEntityType(
                typeof(WorkspaceStaffOnboardingCorrectionReceipt))!;

        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(
                        WorkspaceStaffOnboardingCorrectionReceipt
                            .ScopeId),
                    nameof(
                        WorkspaceStaffOnboardingCorrectionReceipt
                            .ExecutionId)
                ]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(
                        WorkspaceStaffOnboardingCorrectionReceipt
                            .ScopeId),
                    nameof(
                        WorkspaceStaffOnboardingCorrectionReceipt
                            .ApplicationId),
                    nameof(
                        WorkspaceStaffOnboardingCorrectionReceipt
                            .CompletedAtUtc),
                    nameof(
                        WorkspaceStaffOnboardingCorrectionReceipt
                            .Id)
                ]));
        Assert.NotEmpty(entity.GetDeclaredQueryFilters());

        WorkspaceStaffOnboardingCorrectionReceipt receipt =
            WorkspaceStaffOnboardingCorrectionReceipt.Create(
                Guid.NewGuid(),
                WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 1,
                Guid.NewGuid(),
                selectedRecordVersion: 1,
                currentRecordVersion: 2,
                [WorkspaceStaffOnboardingApplicantField.DisplayName],
                new string('a', 64),
                Guid.NewGuid(),
                Guid.NewGuid(),
                WorkspaceStaffOnboardingTests.Now).Value;
        context.StaffOnboardingCorrectionReceipts.Add(receipt);
        await context.SaveChangesAsync();
        context.Entry(receipt).State = EntityState.Modified;

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            "Workspace immutable receipts are append-only.",
            error.Message);
    }

    [Fact]
    public async Task Staff_onboarding_restriction_state_is_versioned_scoped_and_append_only()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        await using WorkspacesDbContext context =
            new(options, new TestScopeContext());
        Microsoft.EntityFrameworkCore.Metadata.IEntityType restriction =
            context.Model.FindEntityType(
                typeof(
                    WorkspaceStaffOnboardingProcessingRestriction))!;
        Microsoft.EntityFrameworkCore.Metadata.IEntityType projection =
            context.Model.FindEntityType(
                typeof(
                    WorkspaceStaffOnboardingProcessingRestrictionProjection))!;
        Microsoft.EntityFrameworkCore.Metadata.IEntityType receipt =
            context.Model.FindEntityType(
                typeof(
                    WorkspaceStaffOnboardingProcessingRestrictionReceipt))!;

        Assert.True(restriction.FindProperty(
            nameof(WorkspaceStaffOnboardingProcessingRestriction.Version))!
            .IsConcurrencyToken);
        Assert.Contains(restriction.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(
                        WorkspaceStaffOnboardingProcessingRestriction.ScopeId),
                    nameof(
                        WorkspaceStaffOnboardingProcessingRestriction
                            .ApplicationId),
                    nameof(
                        WorkspaceStaffOnboardingProcessingRestriction
                            .ApplyCaseId),
                    nameof(
                        WorkspaceStaffOnboardingProcessingRestriction
                            .ApplyApprovalRevision)
                ]));
        Assert.NotEmpty(restriction.GetDeclaredQueryFilters());

        Assert.True(projection.FindProperty(
            nameof(
                WorkspaceStaffOnboardingProcessingRestrictionProjection
                    .Revision))!.IsConcurrencyToken);
        Assert.Contains(projection.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Single().Name ==
                nameof(
                    WorkspaceStaffOnboardingProcessingRestrictionProjection
                        .ProjectionOrdinal));
        Assert.Contains(projection.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(
                        WorkspaceStaffOnboardingProcessingRestrictionProjection
                            .ScopeId),
                    nameof(
                        WorkspaceStaffOnboardingProcessingRestrictionProjection
                            .IsRestricted),
                    nameof(
                        WorkspaceStaffOnboardingProcessingRestrictionProjection
                            .ApplicationId)
                ]));
        Assert.NotEmpty(projection.GetDeclaredQueryFilters());

        Assert.Contains(receipt.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(
                        WorkspaceStaffOnboardingProcessingRestrictionReceipt
                            .ScopeId),
                    nameof(
                        WorkspaceStaffOnboardingProcessingRestrictionReceipt
                            .IdempotencyKey)
                ]));
        Assert.NotEmpty(receipt.GetDeclaredQueryFilters());

        WorkspaceStaffOnboardingProcessingRestrictionReceipt persisted =
            WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                WorkspaceStaffOnboardingProcessingRestrictionAction.Apply,
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 1,
                selectedOnboardingVersion: 1,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                resultingRestrictionVersion: 1,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                "user:privacy",
                Guid.NewGuid(),
                WorkspaceStaffOnboardingTests.Now).Value;
        context.StaffOnboardingProcessingRestrictionReceipts.Add(persisted);
        await context.SaveChangesAsync();
        context.Entry(persisted).State = EntityState.Modified;

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            "Workspace immutable receipts are append-only.",
            error.Message);
    }

    [Fact]
    public async Task Staff_correlation_anonymisation_proof_is_scoped_and_receipts_are_append_only()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        await using WorkspacesDbContext context =
            new(options, new TestScopeContext());
        Microsoft.EntityFrameworkCore.Metadata.IEntityType tombstoneModel =
            context.Model.FindEntityType(
                typeof(
                    WorkspaceStaffCorrelationAnonymisationTombstone))!;
        Microsoft.EntityFrameworkCore.Metadata.IEntityType restoreModel =
            context.Model.FindEntityType(
                typeof(
                    WorkspaceStaffCorrelationAnonymisationRestoreReceipt))!;

        Assert.True(tombstoneModel.FindProperty(
            nameof(
                WorkspaceStaffCorrelationAnonymisationTombstone
                    .Revision))!.IsConcurrencyToken);
        Assert.NotEmpty(tombstoneModel.GetDeclaredQueryFilters());
        Assert.NotEmpty(restoreModel.GetDeclaredQueryFilters());
        Assert.Contains(
            restoreModel.GetForeignKeys(),
            foreignKey =>
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(
                            WorkspaceStaffCorrelationAnonymisationRestoreReceipt
                                .ScopeId),
                        nameof(
                            WorkspaceStaffCorrelationAnonymisationRestoreReceipt
                                .AnchorProcessId)
                    ]) &&
                foreignKey.PrincipalEntityType.ClrType ==
                    typeof(
                        WorkspaceStaffCorrelationAnonymisationTombstone));

        Guid anchorProcessId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Guid ledgerEntryId = Guid.NewGuid();
        DateTimeOffset completedAtUtc =
            WorkspaceStaffOnboardingTests.Now.AddDays(-1);
        WorkspaceStaffCorrelationAnonymisationReceipt receipt =
            WorkspaceStaffCorrelationAnonymisationReceipt.Create(
                Guid.NewGuid(),
                WorkspaceStaffOnboardingTests.OrganizationId
                    .ToString("D"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 2,
                operationRevision: 3,
                anchorProcessId,
                staffMemberId,
                selectedStaffVersion: 7,
                selectedAnchorVersion: 4,
                resultingAnchorVersion: 5,
                onboardingRecordsScrubbed: 1,
                accessProcessRecordsScrubbed: 1,
                accessPlanRecordsScrubbed: 1,
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                "user:privacy",
                completedAtUtc).Value;
        WorkspaceStaffCorrelationAnonymisationTombstone tombstone =
            WorkspaceStaffCorrelationAnonymisationTombstone.Create(
                receipt).Value;
        context.AddRange(receipt, tombstone);
        await context.SaveChangesAsync();

        Assert.True(tombstone.AttachRestoreProof(
            ledgerEntryId,
            receipt.ContractVersion,
            receipt.Id,
            receipt.CanonicalSha256,
            receipt.ResultingAnchorVersion,
            receipt.CompletedAtUtc,
            WorkspaceStaffOnboardingTests.Now).IsSuccess);
        WorkspaceStaffCorrelationAnonymisationRestoreReceipt
            restoreReceipt =
            WorkspaceStaffCorrelationAnonymisationRestoreReceipt.Create(
                WorkspaceStaffOnboardingTests.OrganizationId
                    .ToString("D"),
                ledgerEntryId,
                3,
                new string('d', 64),
                anchorProcessId,
                staffMemberId,
                receipt.ContractVersion,
                receipt.Id,
                receipt.CanonicalSha256,
                receipt.ResultingAnchorVersion,
                receipt.ResultingStateSha256,
                receipt.OnboardingRecordsScrubbed,
                receipt.AccessProcessRecordsScrubbed,
                receipt.AccessPlanRecordsScrubbed,
                receipt.CompletedAtUtc,
                tombstone.Revision,
                replayedAtUtc:
                    WorkspaceStaffOnboardingTests.Now).Value;
        context.StaffCorrelationAnonymisationRestoreReceipts.Add(
            restoreReceipt);
        await context.SaveChangesAsync();
        Assert.Equal(2, tombstone.Revision);

        context.Entry(receipt).State = EntityState.Modified;
        InvalidOperationException ownerMutation =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            "Workspace immutable receipts are append-only.",
            ownerMutation.Message);
        context.Entry(receipt).State = EntityState.Unchanged;

        context.Entry(restoreReceipt).State = EntityState.Modified;
        InvalidOperationException restoreMutation =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            "Workspace immutable receipts are append-only.",
            restoreMutation.Message);
    }

    [Fact]
    public async Task Operational_onboarding_reads_require_current_unrestricted_projection()
    {
        Guid tenantA = WorkspaceStaffOnboardingTests.OrganizationId;
        Guid tenantB =
            Guid.Parse("10000000-0000-0000-0000-000000000099");
        WorkspaceStaffOnboarding unrestricted = CreateFailedOnboarding(
            tenantA,
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            "10000000-0000-0000-0000-000000000001",
            WorkspaceStaffOnboardingTests.Now);
        WorkspaceStaffOnboarding restricted = CreateFailedOnboarding(
            tenantA,
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Guid.Parse("30000000-0000-0000-0000-000000000002"),
            "10000000-0000-0000-0000-000000000002",
            WorkspaceStaffOnboardingTests.Now.AddMinutes(1));
        WorkspaceStaffOnboarding missing = CreateFailedOnboarding(
            tenantA,
            Guid.Parse("20000000-0000-0000-0000-000000000003"),
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            "10000000-0000-0000-0000-000000000003",
            WorkspaceStaffOnboardingTests.Now.AddMinutes(2));
        WorkspaceStaffOnboarding unsupported = CreateFailedOnboarding(
            tenantA,
            Guid.Parse("20000000-0000-0000-0000-000000000004"),
            Guid.Parse("30000000-0000-0000-0000-000000000004"),
            "10000000-0000-0000-0000-000000000004",
            WorkspaceStaffOnboardingTests.Now.AddMinutes(3));
        WorkspaceStaffOnboarding otherTenant = CreateFailedOnboarding(
            tenantB,
            Guid.Parse("20000000-0000-0000-0000-000000000005"),
            Guid.Parse("30000000-0000-0000-0000-000000000005"),
            "10000000-0000-0000-0000-000000000005",
            WorkspaceStaffOnboardingTests.Now.AddMinutes(4));
        WorkspaceStaffOnboarding unrestrictedSecond = CreateFailedOnboarding(
            tenantA,
            Guid.Parse("20000000-0000-0000-0000-000000000006"),
            Guid.Parse("30000000-0000-0000-0000-000000000006"),
            "10000000-0000-0000-0000-000000000006",
            WorkspaceStaffOnboardingTests.Now.AddMinutes(6));

        WorkspaceStaffOnboardingProcessingRestrictionProjection
            unrestrictedProjection = CreateProjection(
                unrestricted,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion);
        WorkspaceStaffOnboardingProcessingRestrictionProjection
            restrictedProjection = CreateProjection(
                restricted,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion);
        Assert.True(restrictedProjection.Apply(
            expectedRevision: 0,
            WorkspaceStaffOnboardingProcessingRestrictionContract
                .CurrentVersion,
            WorkspaceStaffOnboardingTests.Now.AddMinutes(5)).IsSuccess);
        WorkspaceStaffOnboardingProcessingRestrictionProjection
            unsupportedProjection = CreateProjection(
                unsupported,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion + 1);
        WorkspaceStaffOnboardingProcessingRestrictionProjection
            otherProjection = CreateProjection(
                otherTenant,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion);
        WorkspaceStaffOnboardingProcessingRestrictionProjection
            unrestrictedSecondProjection = CreateProjection(
                unrestrictedSecond,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion);

        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        await using (WorkspacesDbContext seed = new(
            options,
            new TestScopeContext(enabled: false, scopeId: null)))
        {
            seed.AddRange(
                unrestricted,
                restricted,
                missing,
                unsupported,
                otherTenant,
                unrestrictedSecond,
                unrestrictedProjection,
                restrictedProjection,
                unsupportedProjection,
                otherProjection,
                unrestrictedSecondProjection);
            await seed.SaveChangesAsync();
        }

        await using WorkspacesDbContext context = new(
            options,
            new TestScopeContext(true, tenantA.ToString("D")));
        WorkspaceStaffOnboardingRepository repository = new(context);

        Assert.NotNull(await repository.GetAsync(
            restricted.Id,
            CancellationToken.None));
        Assert.NotNull(await repository.GetOperationalAsync(
            unrestricted.Id,
            CancellationToken.None));
        Assert.Null(await repository.GetOperationalAsync(
            restricted.Id,
            CancellationToken.None));
        Assert.Null(await repository.GetOperationalAsync(
            missing.Id,
            CancellationToken.None));
        Assert.Null(await repository.GetOperationalAsync(
            unsupported.Id,
            CancellationToken.None));
        Assert.Null(await repository.GetOperationalAsync(
            otherTenant.Id,
            CancellationToken.None));
        Assert.Null(await repository.GetOperationalBySourceAndSubjectAsync(
            restricted.SourceKind,
            restricted.SourceId,
            restricted.SubjectId,
            CancellationToken.None));
        Assert.Equal(
            restricted.Id,
            await repository.FindIdBySourceAndSubjectAsync(
                restricted.SourceKind,
                restricted.SourceId,
                restricted.SubjectId,
                CancellationToken.None));

        WorkspaceStaffOnboardingSummaryListResponse firstPage =
            await repository.ListActionableAsync(
                new Gma.Framework.Pagination.PageRequest(1, 1),
                CancellationToken.None);
        WorkspaceStaffOnboardingActionableSummaryDto first =
            Assert.Single(firstPage.Items);
        Assert.Equal(unrestricted.Id, first.ApplicationId);
        Assert.True(firstPage.HasMore);

        WorkspaceStaffOnboardingSummaryListResponse secondPage =
            await repository.ListActionableAsync(
                new Gma.Framework.Pagination.PageRequest(2, 1),
                CancellationToken.None);
        WorkspaceStaffOnboardingActionableSummaryDto second =
            Assert.Single(secondPage.Items);
        Assert.Equal(unrestrictedSecond.Id, second.ApplicationId);
        Assert.False(secondPage.HasMore);
    }

    [Fact]
    public async Task Open_staff_access_processes_are_projected_ordered_and_report_continuation()
    {
        Guid tenantId = WorkspaceStaffOnboardingTests.OrganizationId;
        DateTimeOffset nowUtc = WorkspaceStaffOnboardingTests.Now;
        WorkspaceStaffAccessProcess first = CreateAccessProcess(
            tenantId,
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            targetStaffVersion: 2,
            nowUtc: nowUtc,
            profileCount: 2);
        WorkspaceStaffAccessProcess second = CreateAccessProcess(
            tenantId,
            Guid.Parse("50000000-0000-0000-0000-000000000002"),
            Guid.Parse("60000000-0000-0000-0000-000000000002"),
            targetStaffVersion: 3,
            nowUtc: nowUtc.AddMinutes(1),
            profileCount: 1);
        WorkspaceStaffAccessProcess completed = CreateAccessProcess(
            tenantId,
            Guid.Parse("50000000-0000-0000-0000-000000000003"),
            Guid.Parse("60000000-0000-0000-0000-000000000003"),
            targetStaffVersion: 4,
            nowUtc: nowUtc.AddMinutes(2),
            profileCount: 1);
        Assert.True(completed.MarkAwaitingStaffCommit(nowUtc.AddMinutes(3)).IsSuccess);
        Assert.True(completed.ObserveStaffCommit(nowUtc.AddMinutes(4)).IsSuccess);

        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        await using (WorkspacesDbContext seed = new(
            options,
            new TestScopeContext(enabled: false, scopeId: null)))
        {
            seed.AddRange(first, second, completed);
            await seed.SaveChangesAsync();
        }

        await using WorkspacesDbContext context = new(
            options,
            new TestScopeContext());
        WorkspaceStaffAccessProcessRepository repository = new(context);

        WorkspaceStaffAccessProcessListResponse firstPage =
            await repository.ListOpenAsync(
                new Gma.Framework.Pagination.PageRequest(1, 1),
                CancellationToken.None);
        WorkspaceStaffAccessProcessDto firstRow = Assert.Single(firstPage.Items);
        Assert.Equal(first.Id, firstRow.ProcessId);
        Assert.Equal(2, firstRow.ProfileCount);
        Assert.True(firstPage.HasMore);

        WorkspaceStaffAccessProcessListResponse secondPage =
            await repository.ListOpenAsync(
                new Gma.Framework.Pagination.PageRequest(2, 1),
                CancellationToken.None);
        Assert.Equal(second.Id, Assert.Single(secondPage.Items).ProcessId);
        Assert.False(secondPage.HasMore);
    }

    [Fact]
    public async Task Staff_onboarding_retention_candidates_are_tenant_isolated_ordered_and_bounded()
    {
        string databaseName = Guid.NewGuid().ToString("N");
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
        Guid tenantA = WorkspaceStaffOnboardingTests.OrganizationId;
        Guid tenantB = Guid.Parse("10000000-0000-0000-0000-000000000099");
        DateTimeOffset nowUtc = WorkspaceStaffOnboardingTests.Now.AddDays(7);
        (WorkspaceStaffOnboarding First, WorkspaceStaffAccessPlan FirstPlan) =
            CreateRetentionPair(
                tenantA,
                Guid.Parse("30000000-0000-0000-0000-000000000001"),
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                nowUtc.AddHours(-5));
        (WorkspaceStaffOnboarding Second, WorkspaceStaffAccessPlan SecondPlan) =
            CreateRetentionPair(
                tenantA,
                Guid.Parse("30000000-0000-0000-0000-000000000002"),
                Guid.Parse("40000000-0000-0000-0000-000000000002"),
                nowUtc.AddHours(-4));
        (WorkspaceStaffOnboarding Fresh, WorkspaceStaffAccessPlan FreshPlan) =
            CreateRetentionPair(
                tenantA,
                Guid.Parse("30000000-0000-0000-0000-000000000003"),
                Guid.Parse("40000000-0000-0000-0000-000000000003"),
                nowUtc.AddHours(-1));
        (WorkspaceStaffOnboarding Other, WorkspaceStaffAccessPlan OtherPlan) =
            CreateRetentionPair(
                tenantB,
                Guid.Parse("30000000-0000-0000-0000-000000000004"),
                Guid.Parse("40000000-0000-0000-0000-000000000004"),
                nowUtc.AddHours(-8));

        await using (WorkspacesDbContext seed = new(
            options,
            new TestScopeContext(enabled: false, scopeId: null)))
        {
            seed.AddRange(
                First, FirstPlan,
                Second, SecondPlan,
                Fresh, FreshPlan,
                Other, OtherPlan);
            await seed.SaveChangesAsync();
        }

        await using WorkspacesDbContext context = new(
            options,
            new TestScopeContext(enabled: true, tenantA.ToString("D")));
        WorkspaceStaffOnboardingRetentionRepository repository = new(context);

        IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate> candidates =
            await repository.ListEligibleAsync(
                tenantA.ToString("D"),
                nowUtc.AddHours(-2),
                2,
                CancellationToken.None);
        IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate> otherTenant =
            await repository.ListEligibleAsync(
                tenantB.ToString("D"),
                nowUtc.AddHours(-2),
                2,
                CancellationToken.None);

        Assert.Collection(
            candidates,
            candidate => Assert.Equal(First.Id, candidate.ApplicationId),
            candidate => Assert.Equal(Second.Id, candidate.ApplicationId));
        Assert.Empty(otherTenant);
    }

    private static (
        WorkspaceStaffOnboarding Application,
        WorkspaceStaffAccessPlan Plan) CreateRetentionPair(
            Guid tenantId,
            Guid applicationId,
            Guid sourceId,
            DateTimeOffset sourceExpiredAtUtc)
    {
        string scopeId = tenantId.ToString("D");
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboarding.Create(
            applicationId,
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            sourceId,
            WorkspaceStaffOnboardingTests.SubjectId,
            "verified@example.test",
            "Ada Operator",
            null,
            null,
            null,
            null,
            null,
            null,
            WorkspaceStaffOnboardingTests.Now).Value;
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            sourceId,
            scopeId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "front-desk",
            [],
            WorkspaceStaffOnboardingTests.SubjectId,
            WorkspaceStaffOnboardingTests.Now).Value;
        Assert.True(plan.Activate(
            WorkspaceStaffOnboardingTests.Now.AddSeconds(1)).IsSuccess);
        Assert.True(plan.ObserveSourceExpired(
            sourceExpiredAtUtc,
            sourceExpiredAtUtc).IsSuccess);
        return (application, plan);
    }

    private static WorkspaceStaffAccessProcess CreateAccessProcess(
        Guid tenantId,
        Guid processId,
        Guid staffMemberId,
        long targetStaffVersion,
        DateTimeOffset nowUtc,
        int profileCount)
    {
        WorkspaceStaffAccessProfileTarget[] profiles = Enumerable.Range(0, profileCount)
            .Select(_ => new WorkspaceStaffAccessProfileTarget(
                Guid.NewGuid(),
                $"tenant:{tenantId:D}"))
            .ToArray();
        return WorkspaceStaffAccessProcess.Create(
            processId,
            tenantId.ToString("D"),
            staffMemberId,
            processId.ToString("D"),
            WorkspaceStaffAccessTargetState.Suspended,
            targetStaffVersion,
            DateOnly.FromDateTime(nowUtc.UtcDateTime),
            "operator",
            profiles,
            nowUtc).Value;
    }

    private static WorkspaceStaffOnboarding CreateFailedOnboarding(
        Guid tenantId,
        Guid applicationId,
        Guid sourceId,
        string subjectId,
        DateTimeOffset createdAtUtc)
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboarding.Create(
                applicationId,
                tenantId.ToString("D"),
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                sourceId,
                subjectId,
                $"{applicationId:N}@example.test",
                "Applicant",
                legalName: null,
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                createdAtUtc).Value;
        Assert.True(application.Fail(
            "Workspaces.TestFailure",
            createdAtUtc.AddSeconds(1)).IsSuccess);
        return application;
    }

    private static
        WorkspaceStaffOnboardingProcessingRestrictionProjection
        CreateProjection(
            WorkspaceStaffOnboarding application,
            int contractVersion) =>
        WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
            application.ScopeId,
            application.Id,
            contractVersion,
            application.CreatedAtUtc).Value;

    private sealed class TestScopeContext(
        bool enabled = true,
        string? scopeId = null)
        : IScopeContext
    {
        public bool IsEnabled => enabled;
        public string? ScopeId => scopeId ??
            (enabled
                ? WorkspaceStaffOnboardingTests.OrganizationId.ToString("D")
                : null);
    }
}

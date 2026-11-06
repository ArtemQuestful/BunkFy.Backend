namespace BunkFy.Modules.Staff.Tests.Persistence;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Domain.Retention;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using BunkFy.Modules.Staff.Persistence.TenantTermination;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffTenantTerminationContributorTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string ForeignTenantId =
        "10000000-0000-0000-0000-000000000002";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid ProcessId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid CaseId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid TerminationEpoch =
        Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyId =
        Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FrozenAtUtc =
        Now.AddMinutes(-1);

    [Fact]
    public async Task Export_streams_complete_owner_state_in_stable_order()
    {
        MutableFenceReader fences = new();
        await using StaffDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        StaffTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink first = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("staff.termination.exported", result.ResultCode);
        Assert.Equal(first.Records.Count, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(1, result.ResultingProofRevision);
        Assert.Equal(
            StaffTenantTerminationMetadata.RecordTypes,
            first.Records
                .Select(record => record.RecordType)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        Assert.Contains(
            first.Records,
            record => record.Fields.Any(field =>
                field.FieldId == "staff.property-id" &&
                field.Value.GetGuid() == PropertyId));
        Assert.Contains(
            first.Records.Where(record =>
                record.RecordType ==
                    StaffTenantTerminationMetadata.StaffMemberRecordType),
            record => Field(record, "staff.profile-state")
                .GetProperty("authSubjectId")
                .GetString() == "account-maya");
        Assert.Contains(
            first.Records,
            record =>
                record.RecordType ==
                    StaffTenantTerminationMetadata
                        .MemberMutationOperationRecordType &&
                Field(record, "staff.member-mutation-operation")
                    .GetProperty("requestFingerprint")
                    .GetString() == Digest);
        DataRightsExportRecord[] anchorRecords = first.Records
            .Where(record => record.RecordType ==
                StaffTenantTerminationMetadata
                    .IdentityProvisioningAnchorRecordType)
            .ToArray();
        Assert.Equal(2, anchorRecords.Length);
        DataRightsExportRecord anchorRecord = Assert.Single(
            anchorRecords,
            record => Field(
                    record,
                    "staff.identity-provisioning-anchor")
                .GetProperty("sourceId")
                .GetGuid() ==
                Guid.Parse("80000000-0000-0000-0000-000000000002"));
        Assert.Equal(
            [
                "staff.identity-provisioning-anchor",
                "staff.scope-id",
                "staff.staff-member-id"
            ],
            anchorRecord.Fields
                .Select(field => field.FieldId)
                .OrderBy(field => field, StringComparer.Ordinal)
                .ToArray());
        JsonElement anchorState = Field(
            anchorRecord,
            "staff.identity-provisioning-anchor");
        Assert.Equal(4, anchorState.EnumerateObject().Count());
        Assert.Equal(
            "workspace-onboarding",
            anchorState.GetProperty("sourceKind").GetString());
        Assert.Equal(
            Guid.Parse("80000000-0000-0000-0000-000000000002"),
            anchorState.GetProperty("sourceId").GetGuid());
        Assert.Equal(
            Guid.Parse("81000000-0000-0000-0000-000000000002"),
            anchorState.GetProperty("resolutionEventId").GetGuid());
        Assert.Equal(
            Now.AddMinutes(3),
            anchorState.GetProperty("anchoredAtUtc")
                .GetDateTimeOffset());
        Assert.Equal(
            StaffTenantTerminationMetadata.ExportSchemaId,
            contributor.ExportDescriptor.ExportSchemaId);
        Assert.Equal(
            StaffTenantTerminationMetadata.ExportSchemaVersion,
            contributor.ExportDescriptor.ExportSchemaVersion);
        Assert.All(
            first.Records,
            record => Assert.InRange(
                record.Fields.Count,
                1,
                DataRightsExportLimits.MaxFieldsPerRecord));
        Assert.Equal(
            StaffTenantTerminationMetadata.ExportFieldIds
                .OrderBy(field => field, StringComparer.Ordinal),
            contributor.ExportDescriptor.FieldIds);
        Assert.Equal(
            [
                TenantTerminationContributionPhase.Export,
                TenantTerminationContributionPhase.Destroy
            ],
            contributor.Descriptor.PhasePlans.Select(plan => plan.Phase));
        Assert.All(
            contributor.Descriptor.PhasePlans,
            plan => Assert.Equal(
                StaffTenantTerminationMetadata.DependencyOwnerKey,
                Assert.Single(plan.DependsOnOwnerKeys)));

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                Request(),
                replay,
                CancellationToken.None);

        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            first.Records.Select(Identity).ToArray(),
            replay.Records.Select(Identity).ToArray());
    }

    [Fact]
    public async Task Export_retries_without_records_for_a_different_fence()
    {
        MutableFenceReader fences = new()
        {
            Current = FrozenFence() with { Version = 2 }
        };
        await using StaffDbContext context = CreateContext(fences);
        StaffTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(),
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "staff.termination.export-fence-unavailable",
            result.ResultCode);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Operational_save_rejects_a_frozen_workspace_without_advancing_revision()
    {
        MutableFenceReader fences = new();
        await using StaffDbContext context = CreateContext(fences);
        context.StaffMembers.Add(CreateMember("Maya Chen"));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        context.StaffMembers.Add(CreateMember("Blocked Staff"));

        StaffOperationalAdmissionException failure =
            await Assert.ThrowsAsync<StaffOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            StaffOperationalAdmissionFailure.Restricted,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
        Assert.Single(await context.StaffMembers.ToListAsync());
    }

    [Fact]
    public async Task Operational_saves_advance_the_tenant_revision_once_each()
    {
        MutableFenceReader fences = new();
        await using StaffDbContext context = CreateContext(fences);
        context.StaffMembers.Add(CreateMember("Maya Chen"));
        await context.SaveChangesAsync();

        context.StaffMembers.Add(CreateMember("Second Staff"));
        await context.SaveChangesAsync();

        Assert.Equal(
            2,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
    }

    [Fact]
    public async Task Operational_save_fails_closed_when_fence_read_fails()
    {
        await using StaffDbContext context = CreateContext(
            new ThrowingFenceReader());
        context.StaffMembers.Add(CreateMember("Maya Chen"));

        StaffOperationalAdmissionException failure =
            await Assert.ThrowsAsync<StaffOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            StaffOperationalAdmissionFailure.Unavailable,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.StaffMembers.ToListAsync());
        Assert.Empty(await context.TenantRevisions.ToListAsync());
    }

    [Fact]
    public async Task Destroy_blocks_an_active_hold_before_opening_local_progress()
    {
        MutableFenceReader fences = new();
        await using StaffDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        StaffTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(Now.AddHours(1)),
            fences);

        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(
                DestroyRequest(),
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Blocked,
            result.Status);
        Assert.Equal(
            "staff.termination.destroy-active-hold",
            result.ResultCode);
        Assert.Equal(1, result.RemainingActiveCount);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        Assert.Empty(await context.TenantDestroyReceipts.ToListAsync());
        StaffTenantRevision state =
            await context.TenantRevisions.SingleAsync();
        Assert.True(state.IsOpen);
        Assert.Null(state.DestroyOperationId);
    }

    [Fact]
    public async Task Destroy_resumes_to_completion_and_exactly_replays()
    {
        MutableFenceReader fences = new();
        await using StaffDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        StaffDataHold hold = await context.DataHolds.SingleAsync();
        Assert.True(hold.Release(
            hold.Version,
            "user:privacy-controller",
            Now.AddMinutes(8)).IsSuccess);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        StaffTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(Now.AddHours(1)),
            fences);
        TenantTerminationContributionRequest request = DestroyRequest();

        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);
        int attempts = 1;
        while (result.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 100)
        {
            result = await contributor.ExecuteAsync(
                request,
                CancellationToken.None);
            attempts++;
        }

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("staff.termination.destroyed", result.ResultCode);
        Assert.True(result.AffectedCount > 0);
        Assert.Equal(2, result.SelectedProofRevision);
        Assert.Equal(3, result.ResultingProofRevision);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        StaffTenantDestroyReceipt receipt =
            await context.TenantDestroyReceipts.SingleAsync();
        Assert.Equal(result.AffectedCount, receipt.RemovedRecordCount);
        Assert.Equal(
            StaffTenantLifecycleStatus.Closed,
            (await context.TenantRevisions.SingleAsync()).LifecycleStatus);
        Assert.False(await HasOwnerRecordsAsync(context));

        TenantTerminationContributionResult replay =
            await contributor.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(result, replay);

        TenantTerminationContributionResult conflict =
            await contributor.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);
        Assert.Equal(
            "staff.termination.destroy-conflict",
            conflict.ResultCode);

        context.TenantDestroyReceipts.Remove(receipt);
        InvalidOperationException receiptMutation =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Contains("append-only", receiptMutation.Message);
        context.ChangeTracker.Clear();
        fences.Current = null;
        context.PropertyProjections.Add(new StaffPropertyProjection(
            TenantId,
            PropertyId,
            "Closed property",
            PropertyStatus.Active,
            version: 1));
        StaffOperationalAdmissionException closedFailure =
            await Assert.ThrowsAsync<StaffOperationalAdmissionException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            StaffOperationalAdmissionFailure.Restricted,
            closedFailure.Failure);

        context.ChangeTracker.Clear();
        context.OutboxMessages.Add(new OutboxMessage(
            Guid.NewGuid(),
            "bunkfy.staff.lifecycle-test.v1",
            "lifecycle-test",
            version: 1,
            TenantId,
            Now,
            "{}",
            Now));
        StaffOperationalAdmissionException messageFailure =
            await Assert.ThrowsAsync<StaffOperationalAdmissionException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            StaffOperationalAdmissionFailure.Restricted,
            messageFailure.Failure);
    }

    [Fact]
    public async Task Destroy_preserves_foreign_property_projection_with_same_id()
    {
        InMemoryDatabaseRoot root = new();
        string databaseName = $"staff-destroy-scope-{Guid.NewGuid():N}";
        MutableFenceReader fences = new();

        await using (StaffDbContext tenantA = CreateContext(
            fences,
            root,
            databaseName,
            TenantId))
        {
            SeedGraph(tenantA);
            await tenantA.SaveChangesAsync();
            StaffDataHold hold = await tenantA.DataHolds.SingleAsync();
            Assert.True(hold.Release(
                hold.Version,
                "user:privacy-controller",
                Now.AddMinutes(8)).IsSuccess);
            await tenantA.SaveChangesAsync();
            tenantA.ChangeTracker.Clear();

            await using (StaffDbContext tenantB = CreateContext(
                new MutableFenceReader(),
                root,
                databaseName,
                ForeignTenantId))
            {
                tenantB.PropertyProjections.Add(new StaffPropertyProjection(
                    ForeignTenantId,
                    PropertyId,
                    "Foreign property",
                    PropertyStatus.Active,
                    version: 1));
                await tenantB.SaveChangesAsync();
            }

            fences.Current = FrozenFence();
            StaffTenantTerminationContributor contributor = new(
                tenantA,
                new TestScopeContext(),
                new TestClock(Now.AddHours(1)),
                fences);
            TenantTerminationContributionResult result =
                await contributor.ExecuteAsync(
                    DestroyRequest(),
                    CancellationToken.None);
            int attempts = 1;
            while (result.Status ==
                    TenantTerminationContributionStatus.RetryRequired &&
                attempts < 100)
            {
                result = await contributor.ExecuteAsync(
                    DestroyRequest(),
                    CancellationToken.None);
                attempts++;
            }

            Assert.Equal(
                TenantTerminationContributionStatus.Completed,
                result.Status);
            Assert.Empty(await tenantA.PropertyProjections.ToArrayAsync());
        }

        await using StaffDbContext verifyTenantB = CreateContext(
            new MutableFenceReader(),
            root,
            databaseName,
            ForeignTenantId);
        StaffPropertyProjection foreignProjection =
            await verifyTenantB.PropertyProjections.SingleAsync();
        Assert.Equal(PropertyId, foreignProjection.Id);
        Assert.Equal(ForeignTenantId, foreignProjection.ScopeId);
        Assert.Equal("Foreign property", foreignProjection.Name);
    }

    [Fact]
    public void Destroy_progress_rejects_a_batch_above_the_persisted_bound()
    {
        StaffTenantDestroyOperation operation = Assert.IsType<
            StaffTenantDestroyOperation>(
            StaffTenantDestroyOperation.TryCreate(
                Guid.NewGuid(),
                TenantId,
                Digest,
                selectedRevision: 4,
                StaffTenantDestroyOperation.MaximumBatchSize,
                Now));

        Assert.False(operation.RecordBatch(
            StaffTenantDestroyStage.OutboxMessages,
            StaffTenantDestroyOperation.MaximumBatchSize + 1,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(0, operation.RemovedRecordCount);
        Assert.Equal(0, operation.CompletedBatchCount);

        Assert.True(operation.RecordBatch(
            StaffTenantDestroyStage.OutboxMessages,
            StaffTenantDestroyOperation.MaximumBatchSize,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(
            StaffTenantDestroyOperation.MaximumBatchSize,
            operation.RemovedRecordCount);
        Assert.Equal(1, operation.CompletedBatchCount);
    }

    [Theory]
    [InlineData("correction")]
    [InlineData("restriction")]
    [InlineData("governance")]
    [InlineData("hold")]
    [InlineData("anonymisation")]
    [InlineData("restore")]
    [InlineData("retention")]
    public async Task Governance_receipts_are_append_only(string receiptKind)
    {
        MutableFenceReader fences = new();
        await using StaffDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        object receipt = receiptKind switch
        {
            "correction" =>
                await context.DataRightsCorrectionReceipts.SingleAsync(),
            "restriction" =>
                await context.ProcessingRestrictionReceipts.SingleAsync(),
            "governance" =>
                await context.EmploymentGovernanceChangeReceipts
                    .SingleAsync(),
            "hold" => await context.DataHoldReceipts.SingleAsync(),
            "anonymisation" =>
                await context.AnonymisationReceipts.SingleAsync(),
            "restore" =>
                await context.AnonymisationRestoreReceipts.SingleAsync(),
            "retention" =>
                await context.RetentionAnonymisationReceipts.SingleAsync(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(receiptKind),
                receiptKind,
                "Unknown receipt kind.")
        };
        context.Remove(receipt);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());

        Assert.Contains("append-only", failure.Message);
    }

    [Fact]
    public async Task Anonymisation_tombstones_cannot_be_deleted()
    {
        MutableFenceReader fences = new();
        await using StaffDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        StaffAnonymisationTombstone tombstone =
            await context.AnonymisationTombstones.SingleAsync(candidate =>
                candidate.Authority ==
                    StaffAnonymisationAuthority.Retention);
        context.AnonymisationTombstones.Remove(tombstone);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());

        Assert.Contains("append-only", failure.Message);
    }

    private static void SeedGraph(StaffDbContext context)
    {
        StaffMember member = CreateMember("Maya Chen");
        _ = member.AssignProperty(
            Guid.NewGuid(),
            PropertyId,
            "Front desk",
            isPrimary: true,
            new DateOnly(2026, 1, 1),
            member.Version,
            "user:owner",
            Guid.NewGuid(),
            Now.AddMinutes(1)).Value;
        Assert.True(member.UnassignProperty(
            PropertyId,
            new DateOnly(2026, 7, 30),
            member.Version,
            "user:owner",
            "Moved to another property.",
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        context.StaffMembers.Add(member);
        context.MemberMutationOperations.Add(
            new StaffMemberMutationOperation(
                new StaffMemberMutationOperationRecord(
                    Guid.NewGuid(),
                    member.ScopeId,
                    member.Id,
                    StaffMemberMutationKind.ProfileUpdate,
                    member.Version,
                    Digest,
                    StaffStatus.Active,
                    member.Version,
                    Now.AddMinutes(3))));
        context.IdentityProvisioningAnchors.AddRange(
            new StaffIdentityProvisioningAnchor(
                new StaffIdentityProvisioningAnchorRecord(
                    member.ScopeId,
                    StaffIdentityProvisioningSourceKind
                        .WorkspaceOnboarding,
                    Guid.Parse(
                        "80000000-0000-0000-0000-000000000001"),
                    member.Id,
                    Now.AddMinutes(3),
                    Guid.Parse(
                        "81000000-0000-0000-0000-000000000001"))),
            new StaffIdentityProvisioningAnchor(
                new StaffIdentityProvisioningAnchorRecord(
                    member.ScopeId,
                    StaffIdentityProvisioningSourceKind
                        .WorkspaceOnboarding,
                    Guid.Parse(
                        "80000000-0000-0000-0000-000000000002"),
                    member.Id,
                    Now.AddMinutes(3),
                    Guid.Parse(
                        "81000000-0000-0000-0000-000000000002"))));
        context.IdentityProvisioningAnchorResolutions.Add(
            new StaffIdentityProvisioningAnchorResolution(
                new StaffIdentityProvisioningAnchorResolutionRecord(
                    member.ScopeId,
                    StaffIdentityProvisioningSourceKind
                        .WorkspaceOnboarding,
                    Guid.Parse(
                        "80000000-0000-0000-0000-000000000001"),
                    member.Id,
                    WorkspaceApplicationVersion: 4,
                    StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                        .CompletedRedacted,
                    Guid.Parse(
                        "81000000-0000-0000-0000-000000000001"),
                    Now.AddMinutes(4))));

        context.DataRightsCorrectionReceipts.Add(
            StaffDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                CaseId,
                approvalRevision: 1,
                member.Id,
                selectedRecordVersion: 2,
                currentRecordVersion: 3,
                [StaffProfileField.DisplayName],
                Digest,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now.AddMinutes(3)).Value);

        StaffProcessingRestriction restriction =
            StaffProcessingRestriction.Create(
                Guid.NewGuid(),
                TenantId,
                member.Id,
                CaseId,
                applyApprovalRevision: 1,
                applySelectedStaffVersion: member.Version,
                "user:owner",
                Now.AddMinutes(4)).Value;
        context.ProcessingRestrictions.Add(restriction);
        context.ProcessingRestrictionReceipts.Add(
            StaffProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                restriction.Id,
                StaffProcessingRestrictionAction.Apply,
                member.Id,
                CaseId,
                approvalRevision: 1,
                selectedStaffVersion: member.Version,
                StaffProcessingRestrictionContract.CurrentVersion,
                resultingRestrictionVersion: restriction.Version,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                "user:owner",
                Guid.NewGuid(),
                Now.AddMinutes(4)).Value);

        StaffEmploymentGovernance governance =
            CreateGovernance(member, Now.AddMinutes(5));
        context.EmploymentGovernance.Add(governance);
        context.EmploymentGovernanceChangeReceipts.Add(
            StaffEmploymentGovernanceChangeReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                governance,
                previousGovernanceVersion: 0,
                Digest,
                "user:privacy",
                Now.AddMinutes(5)).Value);

        StaffDataHold hold = StaffDataHold.Place(
            Guid.NewGuid(),
            TenantId,
            member.Id,
            "regulatory-request",
            "user:owner",
            Now.AddMinutes(6)).Value;
        context.DataHolds.Add(hold);
        context.DataHoldReceipts.Add(StaffDataHoldReceipt.Create(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            hold,
            StaffDataHoldAction.Place,
            member.Version,
            "user:owner",
            Now.AddMinutes(6)).Value);

        SeedAnonymisationProof(context);
        SeedRetentionProof(context);
    }

    private static void SeedAnonymisationProof(StaffDbContext context)
    {
        StaffMember member = CreateDepartedMember(
            "Anonymised Staff Source",
            Now.AddDays(-2));
        StaffMemberAnonymisationOutcome outcome = member.Anonymise(
            member.Version,
            "user:privacy-executor",
            Guid.NewGuid(),
            Now.AddMinutes(7)).Value;
        StaffAnonymisationReceipt receipt =
            StaffAnonymisationReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                CaseId,
                approvalRevision: 1,
                operationRevision: 2,
                member.Id,
                outcome.PreviousVersion,
                outcome.CurrentVersion,
                selectedOperationLockRevision: 1,
                resultingOperationLockRevision: 2,
                Digest,
                Digest,
                outcome.EventId,
                "user:privacy-executor",
                outcome.OccurredAtUtc).Value;
        StaffAnonymisationTombstone tombstone =
            StaffAnonymisationTombstone.Create(
                TenantId,
                member.Id,
                receipt.CompletedAtUtc,
                receipt.CanonicalSha256).Value;
        Guid ledgerEntryId = Guid.NewGuid();
        DateTimeOffset replayedAtUtc = Now.AddMinutes(8);
        Assert.True(tombstone.AttachRestoreProof(
            ledgerEntryId,
            receipt.CompletedAtUtc,
            receipt.CanonicalSha256,
            replayedAtUtc).IsSuccess);
        StaffAnonymisationRestoreReceipt restoreReceipt =
            StaffAnonymisationRestoreReceipt.Create(
                TenantId,
                ledgerEntryId,
                member.Id,
                receipt.ContractVersion,
                receipt.Id,
                receipt.CanonicalSha256,
                receipt.ResultingStaffVersion,
                tombstone.Revision,
                replayedAtUtc).Value;

        context.StaffMembers.Add(member);
        context.AnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(tombstone);
        context.AnonymisationRestoreReceipts.Add(restoreReceipt);
    }

    private static void SeedRetentionProof(StaffDbContext context)
    {
        StaffMember member = CreateDepartedMember(
            "Retention Staff",
            Now.AddDays(-5));
        StaffRetentionExecution execution =
            StaffRetentionExecution.Start(
                Guid.NewGuid(),
                TenantId,
                "staff-operational",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 0,
                Now.AddMinutes(-2),
                Now.AddMinutes(10)).Value;
        StaffMemberAnonymisationOutcome outcome = member.Anonymise(
            member.Version,
            "system:retention",
            Guid.NewGuid(),
            Now).Value;
        StaffRetentionAnonymisationReceipt receipt =
            StaffRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                execution.Id,
                member.Id,
                outcome,
                selectedOperationLockRevision: 1,
                resultingOperationLockRevision: 2,
                member.DepartedAtUtc!.Value,
                Now.AddDays(-1),
                Digest).Value;
        StaffAnonymisationTombstone tombstone =
            StaffAnonymisationTombstone.CreateForRetention(receipt).Value;

        context.StaffMembers.Add(member);
        context.RetentionExecutions.Add(execution);
        context.RetentionAnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(tombstone);
    }

    private static StaffMember CreateMember(string displayName) =>
        StaffMember.Create(
            Guid.NewGuid(),
            TenantId,
            displayName,
            $"{displayName} Legal",
            $"{displayName.Replace(' ', '.').ToLowerInvariant()}@example.test",
            "+44 20 1234 5678",
            $"EMP-{Guid.NewGuid():N}"[..12],
            "Manager",
            "Operations",
            displayName == "Maya Chen" ? "account-maya" : null,
            "user:owner",
            Guid.NewGuid(),
            Now.AddDays(-10)).Value;

    private static StaffMember CreateDepartedMember(
        string displayName,
        DateTimeOffset departedAtUtc)
    {
        StaffMember member = CreateMember(displayName);
        Assert.True(member.Depart(
            DateOnly.FromDateTime(departedAtUtc.UtcDateTime),
            member.Version,
            "user:owner",
            "Employment ended.",
            Guid.NewGuid(),
            [],
            departedAtUtc).IsSuccess);
        return member;
    }

    private static StaffEmploymentGovernance CreateGovernance(
        StaffMember member,
        DateTimeOffset configuredAtUtc) =>
        StaffEmploymentGovernance.Configure(
            TenantId,
            member.Id,
            member.Version,
            StaffEmploymentGovernanceBinding.Create(
                "GB",
                "staff-test",
                1,
                "eu-west-2",
                "uk-no-transfer",
                "staff-employment",
                1,
                Digest,
                new DateTimeOffset(
                    2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(
                    2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
                configuredAtUtc.AddMinutes(-1)).Value,
            [
                StaffEmploymentGovernanceAcknowledgement.Create(
                    "operator-notice",
                    1).Value
            ],
            "user:privacy",
            configuredAtUtc).Value;

    private static WorkspaceTerminationFenceSnapshot FrozenFence() =>
        new(
            ProcessId,
            TerminationEpoch,
            WorkspaceTerminationFenceState.Frozen,
            Version: 3);

    private static TenantTerminationExportRequest Request() =>
        new(
            new TenantTerminationContributionRequest(
                TenantTerminationContract.CurrentVersion,
                TenantId,
                ProcessId,
                CaseId,
                ApprovalRevision: 1,
                OperationRevision: 2,
                TerminationEpoch,
                TenantTerminationContributionPhase.Export,
                Guid.Parse("60000000-0000-0000-0000-000000000001"),
                Guid.Parse("70000000-0000-0000-0000-000000000001"),
                Digest,
                "termination-exporter",
                Now.AddMinutes(5)),
            FreezeOperationRevision: 1,
            WorkspaceFenceRevision: 3,
            Digest,
            FrozenAtUtc);

    private static TenantTerminationContributionRequest DestroyRequest() =>
        new(
            TenantTerminationContract.CurrentVersion,
            TenantId,
            ProcessId,
            CaseId,
            ApprovalRevision: 1,
            OperationRevision: 2,
            TerminationEpoch,
            TenantTerminationContributionPhase.Destroy,
            Guid.Parse("60000000-0000-0000-0000-000000000002"),
            Guid.Parse("70000000-0000-0000-0000-000000000002"),
            Digest,
            "termination-executor",
            Now.AddHours(2));

    private static async Task<bool> HasOwnerRecordsAsync(
        StaffDbContext context) =>
        await context.StaffMembers.AnyAsync() ||
        await context.IdentityProvisioningAnchors.AnyAsync() ||
        await context.IdentityProvisioningAnchorResolutions.AnyAsync() ||
        await context.MemberMutationOperations.AnyAsync() ||
        await context.DataRightsCorrectionReceipts.AnyAsync() ||
        await context.ProcessingRestrictions.AnyAsync() ||
        await context.ProcessingRestrictionProjections.AnyAsync() ||
        await context.ProcessingRestrictionReceipts.AnyAsync() ||
        await context.EmploymentGovernance.AnyAsync() ||
        await context.EmploymentGovernanceChangeReceipts.AnyAsync() ||
        await context.DataHolds.AnyAsync() ||
        await context.DataHoldReceipts.AnyAsync() ||
        await context.AnonymisationReceipts.AnyAsync() ||
        await context.AnonymisationTombstones.AnyAsync() ||
        await context.AnonymisationRestoreReceipts.AnyAsync() ||
        await context.RetentionExecutions.AnyAsync() ||
        await context.RetentionAnonymisationReceipts.AnyAsync() ||
        await context.PropertyAssignments.AnyAsync() ||
        await context.PropertyProjections.AnyAsync() ||
        await context.ProjectionRebuildCheckpoints.AnyAsync() ||
        await context.RetentionSweepCheckpoints.AnyAsync() ||
        await context.OperationLocks.AnyAsync() ||
        await context.OutboxMessages.AnyAsync() ||
        await context.InboxMessages.AnyAsync();

    private static JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(
            record.Fields,
            field => field.FieldId == fieldId).Value;

    private static string Identity(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|{record.RecordVersion}";

    private static StaffDbContext CreateContext(
        IWorkspaceTerminationFenceReader fences)
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new StaffDbContext(
            options,
            new TestScopeContext(),
            fences);
    }

    private static StaffDbContext CreateContext(
        IWorkspaceTerminationFenceReader fences,
        InMemoryDatabaseRoot root,
        string databaseName,
        string scopeId)
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
                .UseInMemoryDatabase(databaseName, root)
                .Options;
        return new StaffDbContext(
            options,
            new TestScopeContext(scopeId),
            fences);
    }

    private sealed class CollectingSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public WorkspaceTerminationFenceSnapshot? Current { get; set; }

        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.Current);
        }
    }

    private sealed class ThrowingFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fence store unavailable.");
    }

    private sealed class TestScopeContext(string scopeId = TenantId)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class TestClock(DateTimeOffset? utcNow = null) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow ?? Now;
    }
}

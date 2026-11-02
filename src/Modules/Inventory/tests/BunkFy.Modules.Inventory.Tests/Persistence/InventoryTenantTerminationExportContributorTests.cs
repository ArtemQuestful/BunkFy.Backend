namespace BunkFy.Modules.Inventory.Tests;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.DataRights;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed partial class InventoryTenantTerminationExportContributorTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
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
    private static readonly Guid RoomId =
        Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid BedId =
        Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid AllocationId =
        Guid.Parse("80000000-0000-0000-0000-000000000001");
    private static readonly Guid ManagementOperationId =
        Guid.Parse("80500000-0000-0000-0000-000000000001");
    private static readonly Guid BlockId =
        Guid.Parse("81000000-0000-0000-0000-000000000001");
    private static readonly Guid BlockGroupId =
        Guid.Parse("82000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset FrozenAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now =
        FrozenAtUtc.AddMinutes(1);

    [Fact]
    public async Task Export_streams_complete_owner_state_in_stable_order()
    {
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        InventoryTenantTerminationContributor contributor = new(
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
        Assert.Equal("inventory.termination.exported", result.ResultCode);
        Assert.Equal(13, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(1, result.ResultingProofRevision);
        Assert.Equal(
            InventoryTenantTerminationMetadata.RecordTypes,
            first.Records.Select(record => record.RecordType).ToArray());
        DataRightsExportRecord groupRecord = Assert.Single(
            first.Records,
            record => record.RecordType ==
                InventoryTenantTerminationMetadata.ManualBlockGroupRecordType);
        JsonElement groupActors = Field(
            groupRecord,
            "inventory.staff-actor-reference");
        Assert.Equal(
            "user:owner",
            groupActors.GetProperty("createdByActorId").GetString());
        Assert.Equal(
            "user:owner",
            groupActors.GetProperty("lastModifiedByActorId").GetString());
        Assert.Equal(
            Digest,
            Field(groupRecord, "inventory.staff-change-operations")
                .GetProperty("membershipDigest")
                .GetString());
        Assert.Equal(
            "maintenance",
            Field(first.Records[3], "inventory.operational-reason")
                .GetString());
        DataRightsExportRecord bedRetirementRecord = Assert.Single(
            first.Records,
            record => record.RecordType ==
                InventoryTenantTerminationMetadata.BedRetirementRecordType);
        Assert.Equal(
            "keep bed in service",
            Field(bedRetirementRecord, "inventory.cancellation-reason")
                .GetString());
        Assert.Equal(
            "user:manager",
            Field(
                    bedRetirementRecord,
                    "inventory.cancellation-actor-reference")
                .GetString());
        Assert.Equal(
            FrozenAtUtc,
            Field(bedRetirementRecord, "inventory.canceled-at")
                .GetDateTimeOffset());
        Assert.Equal(
            ManagementOperationId,
            first.Records[2].Fields.Single(field =>
                field.FieldId == "inventory.operation-id").Value.GetGuid());
        JsonElement managementState = Field(
            first.Records[2],
            "inventory.management-operation");
        Assert.Equal(
            "manual-block-create",
            managementState.GetProperty("kind").GetString());
        Assert.Equal(
            BlockId,
            managementState.GetProperty("resultBlockId").GetGuid());
        Assert.Equal(
            BlockGroupId,
            managementState.GetProperty("resultBlockGroupId").GetGuid());
        Assert.Equal(
            "active",
            managementState.GetProperty("resultBlockStatus").GetString());
        Assert.Equal(
            1,
            managementState.GetProperty("resultAffectedBlockCount").GetInt32());
        Assert.False(managementState.TryGetProperty("reason", out _));
        Assert.Equal(
            InventoryTenantTerminationMetadata.ExportSchemaId,
            contributor.ExportDescriptor.ExportSchemaId);
        Assert.Equal(
            [
                TenantTerminationContributionPhase.Export,
                TenantTerminationContributionPhase.Destroy
            ],
            contributor.Descriptor.PhasePlans.Select(plan => plan.Phase));
        Assert.Equal(
            InventoryTenantTerminationMetadata.ExportDependencyOwnerKey,
            Assert.Single(contributor.Descriptor.PhasePlans.Single(
                plan => plan.Phase ==
                    TenantTerminationContributionPhase.Export)
                .DependsOnOwnerKeys));
        Assert.Equal(
            InventoryTenantTerminationMetadata.DestroyDependencyOwnerKey,
            Assert.Single(contributor.Descriptor.PhasePlans.Single(
                plan => plan.Phase ==
                    TenantTerminationContributionPhase.Destroy)
                .DependsOnOwnerKeys));
        Assert.Equal(
            InventoryTenantTerminationMetadata.ExportFieldIds
                .OrderBy(field => field, StringComparer.Ordinal),
            contributor.ExportDescriptor.FieldIds);

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
    public async Task Management_operation_export_versions_immutable_records_and_keeps_retirement_pointer()
    {
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(fences);
        Guid groupOperationId = Guid.NewGuid();
        Guid retirementOperationId = Guid.NewGuid();
        Guid topologyChangeId = Guid.NewGuid();
        context.ManagementOperations.AddRange(
            new InventoryManagementOperation(
                InventoryManagementOperationRecord.ForBlockGroup(
                    groupOperationId,
                    TenantId,
                    InventoryManagementResourceKind.Property,
                    PropertyId,
                    InventoryManagementMutationKind.ManualBlockGroupCreate,
                    Digest,
                    new(BlockGroupId, PropertyId, AffectedBlockCount: 3),
                    Now)),
            new InventoryManagementOperation(
                InventoryManagementOperationRecord.ForRetirement(
                    retirementOperationId,
                    TenantId,
                    PropertyId,
                    InventoryManagementResourceKind.BedRetirement,
                    topologyChangeId,
                    InventoryManagementMutationKind.BedRetirementRetry,
                    expectedVersion: 3,
                    Digest,
                    topologyChangeId,
                    resultVersion: 4,
                    Now)));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        InventoryTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink sink = new();

        TenantTerminationContributionResult result = await contributor
            .ExportAsync(Request(), sink, CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(2, result.AffectedCount);
        Assert.All(sink.Records, record => Assert.Equal(1, record.RecordVersion));
        DataRightsExportRecord retirement = Assert.Single(
            sink.Records,
            record => Field(record, "inventory.operation-id").GetGuid() ==
                retirementOperationId);
        Assert.Equal(
            topologyChangeId,
            Field(retirement, "inventory.management-operation")
                .GetProperty("resultTopologyChangeId")
                .GetGuid());
    }

    [Fact]
    public async Task Export_retries_without_records_for_a_different_fence()
    {
        MutableFenceReader fences = new()
        {
            Current = FrozenFence() with { Version = 2 }
        };
        await using InventoryDbContext context = CreateContext(fences);
        InventoryTenantTerminationContributor contributor = new(
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
            "inventory.termination.export-fence-unavailable",
            result.ResultCode);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Operational_save_rejects_a_frozen_workspace_without_advancing_revision()
    {
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(fences);
        context.InventoryUnits.Add(CreateUnit(BedId));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        context.InventoryUnits.Add(CreateUnit(Guid.NewGuid()));

        InventoryOperationalAdmissionException failure =
            await Assert.ThrowsAsync<
                InventoryOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            InventoryOperationalAdmissionFailure.Restricted,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
        Assert.Single(await context.InventoryUnits.ToListAsync());
    }

    [Fact]
    public async Task Operational_saves_advance_the_tenant_revision_once_each()
    {
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(fences);
        InventoryUnit unit = CreateUnit(BedId);
        context.InventoryUnits.Add(unit);
        await context.SaveChangesAsync();

        unit.TouchAvailability();
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
        await using InventoryDbContext context = CreateContext(
            new ThrowingFenceReader());
        context.InventoryUnits.Add(CreateUnit(BedId));

        InventoryOperationalAdmissionException failure =
            await Assert.ThrowsAsync<
                InventoryOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            InventoryOperationalAdmissionFailure.Unavailable,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.InventoryUnits.ToListAsync());
        Assert.Empty(await context.TenantRevisions.ToListAsync());
    }

    [Theory]
    [InlineData(false, EntityState.Modified)]
    [InlineData(false, EntityState.Deleted)]
    [InlineData(true, EntityState.Modified)]
    [InlineData(true, EntityState.Deleted)]
    public async Task Anonymisation_receipts_are_append_only(
        bool restoreReceipt,
        EntityState attemptedState)
    {
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        object receipt = restoreReceipt
            ? await context.AllocationAnonymisationRestoreReceipts
                .SingleAsync()
            : await context.AllocationAnonymisationReceipts.SingleAsync();
        context.Entry(receipt).State = attemptedState;

        InvalidOperationException failure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Equal(
            "Inventory anonymisation receipts are append-only.",
            failure.Message);
    }

    [Fact]
    public async Task Anonymisation_tombstones_cannot_be_deleted()
    {
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        InventoryAllocationAnonymisationTombstone tombstone =
            await context.AllocationAnonymisationTombstones.SingleAsync();
        context.Entry(tombstone).State = EntityState.Deleted;

        InvalidOperationException failure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Equal(
            "Inventory anonymisation tombstones cannot be deleted.",
            failure.Message);
    }

    private static void SeedGraph(InventoryDbContext context)
    {
        InventoryUnit unit = CreateUnit(BedId);
        RoomInventoryConfiguration configuration =
            RoomInventoryConfiguration.Create(
                RoomId,
                TenantId,
                PropertyId,
                FrozenAtUtc.AddDays(-5)).Value;
        Assert.True(configuration.Configure(
            RoomSalesMode.BedLevel,
            configuration.Version,
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-4),
            "user:owner").IsSuccess);
        ManualInventoryBlock block = ManualInventoryBlock.Create(
            BlockId,
            BlockGroupId,
            TenantId,
            PropertyId,
            BedId,
            DateOnly.FromDateTime(FrozenAtUtc.UtcDateTime),
            DateOnly.FromDateTime(FrozenAtUtc.AddDays(1).UtcDateTime),
            "maintenance",
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-1),
            "user:owner").Value;
        ManualInventoryBlockGroup group = ManualInventoryBlockGroup.Create(
            BlockGroupId,
            TenantId,
            PropertyId,
            ManualInventoryBlockGroupTargetKind.Unit,
            buildingLabel: null,
            floorLabel: null,
            roomId: null,
            inventoryUnitId: BedId,
            block.Arrival,
            block.Departure,
            "maintenance",
            Digest,
            Digest,
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            initialBlockCount: 1,
            replacesGroupId: null,
            FrozenAtUtc.AddDays(-1),
            "user:owner").Value;
        Guid reservationId =
            Guid.Parse("83000000-0000-0000-0000-000000000001");
        InventoryAllocation allocation =
            InventoryAllocation.CreateRejected(
                AllocationId,
                TenantId,
                reservationId,
                Guid.Parse("84000000-0000-0000-0000-000000000001"),
                PropertyId,
                DateOnly.FromDateTime(FrozenAtUtc.UtcDateTime),
                DateOnly.FromDateTime(FrozenAtUtc.AddDays(1).UtcDateTime),
                [BedId],
                InventoryAllocationRejection.AllocationConflict,
                FrozenAtUtc.AddDays(-2)).Value;
        InventoryAllocationAnonymisationOutcome outcome =
            allocation.Anonymise(
                allocation.Version,
                Guid.Parse("85000000-0000-0000-0000-000000000001"),
                FrozenAtUtc.AddHours(-1)).Value;
        InventoryAllocationAnonymisationReceipt receipt =
            InventoryAllocationAnonymisationReceipt.Create(
                Guid.Parse("86000000-0000-0000-0000-000000000001"),
                TenantId,
                Guid.Parse("87000000-0000-0000-0000-000000000001"),
                Guid.Parse("88000000-0000-0000-0000-000000000001"),
                PropertyId,
                CaseId,
                approvalRevision: 1,
                operationRevision: 2,
                AllocationId,
                outcome,
                removedAmendmentDecisionCount: 1,
                Digest,
                "user:owner").Value;
        InventoryAllocationAnonymisationTombstone tombstone =
            InventoryAllocationAnonymisationTombstone.Create(receipt).Value;
        Guid ledgerEntryId =
            Guid.Parse("89000000-0000-0000-0000-000000000001");
        DateTimeOffset replayedAtUtc = FrozenAtUtc.AddMinutes(-30);
        Assert.True(tombstone.AttachRestoreProof(
            PropertyId,
            receipt.ContractVersion,
            receipt.Id,
            receipt.CanonicalSha256,
            receipt.ResultingAllocationVersion,
            receipt.ResultingReservationPseudonym,
            allocationPresent: true,
            receipt.CompletedAtUtc,
            ledgerEntryId,
            replayedAtUtc).IsSuccess);
        InventoryAllocationAnonymisationRestoreReceipt restoreReceipt =
            InventoryAllocationAnonymisationRestoreReceipt.Create(
                TenantId,
                ledgerEntryId,
                tenantSequence: 1,
                Digest,
                PropertyId,
                AllocationId,
                receipt.ContractVersion,
                receipt.Id,
                receipt.CanonicalSha256,
                receipt.ResultingAllocationVersion,
                receipt.ResultingReservationPseudonym,
                allocationPresent: true,
                receipt.CompletedAtUtc,
                tombstone.Revision,
                replayedAtUtc).Value;
        InventoryAllocationAmendmentDecision decision = new(new(
            Guid.Parse("8a000000-0000-0000-0000-000000000001"),
            TenantId,
            AllocationId,
            reservationId,
            PropertyId,
            Digest,
            Confirmed: false,
            InventoryAllocationRejectionReason.AllocationConflict,
            AllocationVersion: null,
            FrozenAtUtc.AddDays(-1)));
        BedRetirementProcess bedRetirement = BedRetirementProcess.Create(
            Guid.Parse("8b000000-0000-0000-0000-000000000001"),
            TenantId,
            PropertyId,
            RoomId,
            BedId,
            "replace frame",
            "user:owner",
            FrozenAtUtc.AddDays(-1)).Value;
        RoomRetirementProcess roomRetirement =
            RoomRetirementProcess.Create(
                Guid.Parse("8c000000-0000-0000-0000-000000000001"),
                TenantId,
                PropertyId,
                RoomId,
                "renovation",
                "user:owner",
                FrozenAtUtc.AddDays(-1)).Value;
        Assert.True(bedRetirement.Cancel(
            bedRetirement.Version,
            "keep bed in service",
            "user:manager",
            FrozenAtUtc).IsSuccess);
        Assert.True(roomRetirement.Cancel(
            roomRetirement.Version,
            "keep room in service",
            "user:manager",
            FrozenAtUtc).IsSuccess);
        InventoryManagementOperation managementOperation = new(
            new InventoryManagementOperationRecord(
                ManagementOperationId,
                TenantId,
                PropertyId,
                InventoryManagementResourceKind.Property,
                PropertyId,
                InventoryManagementMutationKind.ManualBlockCreate,
                0,
                Digest,
                null,
                BlockId,
                BlockGroupId,
                ManualInventoryBlockStatus.Active,
                1,
                1,
                FrozenAtUtc.AddDays(-4)));

        context.InventoryUnits.Add(unit);
        context.RoomConfigurations.Add(configuration);
        context.ManagementOperations.Add(managementOperation);
        context.ManualBlockGroups.Add(group);
        context.ManualBlocks.Add(block);
        context.Allocations.Add(allocation);
        context.AllocationAmendmentDecisions.Add(decision);
        context.AllocationAnonymisationReceipts.Add(receipt);
        context.AllocationAnonymisationTombstones.Add(tombstone);
        context.AllocationAnonymisationRestoreReceipts.Add(restoreReceipt);
        context.BedRetirements.Add(bedRetirement);
        context.RoomRetirements.Add(roomRetirement);
    }

    private static InventoryUnit CreateUnit(Guid bedId)
    {
        InventoryUnit unit = InventoryUnit.CreateBed(
            bedId,
            TenantId,
            PropertyId,
            RoomId);
        unit.Apply(
            PropertyId,
            RoomId,
            bedId,
            InventoryUnitKind.Bed,
            "1",
            isTopologyActive: true,
            sourceVersion: 2);
        return unit;
    }

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
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                Guid.Parse("a0000000-0000-0000-0000-000000000001"),
                Digest,
                "termination-exporter",
                Now.AddMinutes(5)),
            FreezeOperationRevision: 1,
            WorkspaceFenceRevision: 3,
            Digest,
            FrozenAtUtc);

    private static JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(
            record.Fields,
            field => field.FieldId == fieldId).Value;

    private static string Identity(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|{record.RecordVersion}";

    private static InventoryDbContext CreateContext(
        IWorkspaceTerminationFenceReader fences)
    {
        return CreateContext(
            fences,
            new TestScopeContext(),
            Guid.NewGuid().ToString("N"),
            new InMemoryDatabaseRoot());
    }

    private static InventoryDbContext CreateContext(
        IWorkspaceTerminationFenceReader fences,
        IScopeContext scopeContext,
        string databaseName,
        InMemoryDatabaseRoot root)
    {
        DbContextOptions<InventoryDbContext> options =
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(databaseName, root)
                .Options;
        return new(options, scopeContext, fences);
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

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}

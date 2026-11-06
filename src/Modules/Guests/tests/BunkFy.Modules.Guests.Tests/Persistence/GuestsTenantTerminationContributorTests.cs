namespace BunkFy.Modules.Guests.Tests.Persistence;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Guests.Persistence.Repositories;
using BunkFy.Modules.Guests.Persistence.TenantTermination;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainDataHoldAction =
    BunkFy.Modules.Guests.Domain.Models.GuestDataHoldAction;

[Trait("Category", "Unit")]
public sealed class GuestsTenantTerminationContributorTests
{
    private const string TenantId =
        GuestsTenantTerminationTestData.TenantId;
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
    private static readonly Guid OtherPropertyId =
        Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid CreationConfirmationId =
        Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid ManagementOperationId =
        Guid.Parse("60000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now =
        GuestsTenantTerminationTestData.Now;
    private static readonly DateTimeOffset FrozenAtUtc =
        Now.AddMinutes(-1);

    [Fact]
    public async Task Export_streams_complete_owner_state_in_stable_order()
    {
        MutableFenceReader fences = new();
        await using GuestsDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        GuestsTenantTerminationContributor contributor = new(
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
        Assert.Equal("guests.termination.exported", result.ResultCode);
        Assert.Equal(first.Records.Count, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(1, result.ResultingProofRevision);
        Assert.Equal(
            GuestsTenantTerminationMetadata.RecordTypes,
            first.Records
                .Select(record => record.RecordType)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        Assert.Contains(
            first.Records,
            record => record.Fields.Any(field =>
                field.FieldId == "guests.property-id" &&
                field.Value.GetGuid() == OtherPropertyId));
        Assert.Contains(
            first.Records.Where(record =>
                record.RecordType ==
                    GuestsTenantTerminationMetadata.GuestProfileRecordType),
            record => Field(record, "guests.staff-attribution")
                .GetProperty("createdBy")
                .GetString() == "user:owner");
        Assert.Contains(
            first.Records.Where(record =>
                record.RecordType ==
                    GuestsTenantTerminationMetadata.GuestProfileRecordType),
            record =>
            {
                JsonElement confirmation = Field(
                        record,
                        "guests.profile-state")
                    .GetProperty("creationConfirmationId");
                return confirmation.ValueKind == JsonValueKind.String &&
                    confirmation.GetGuid() == CreationConfirmationId;
            });
        DataRightsExportRecord[] managementOperations = first.Records
            .Where(record => record.RecordType ==
                GuestsTenantTerminationMetadata.ManagementOperationRecordType)
            .ToArray();
        DataRightsExportRecord multilingualProfile = Assert.Single(first.Records,
            record => record.RecordType == GuestsTenantTerminationMetadata.GuestProfileRecordType &&
                Field(record, "guests.profile-state").GetProperty("creationConfirmationId").GetString() ==
                    CreationConfirmationId.ToString());
        Assert.Equal(["en-GB", "fr", "unknown-١"],
            Field(multilingualProfile, "guests.profile-state").GetProperty("languageTags")
                .EnumerateArray().Select(value => Assert.IsType<string>(value.GetString())).ToArray());
        Assert.Equal(2, managementOperations.Length);
        Assert.Equal(
            managementOperations.Length,
            managementOperations.Select(record => record.RecordId).Distinct().Count());
        Assert.All(
            managementOperations,
            managementOperation =>
            {
                Guid guestId = Field(
                    managementOperation,
                    "guests.guest-id").GetGuid();
                Assert.Equal(
                    DataRightsExportRecordIds.CreateDeterministicChild(
                        guestId,
                        ManagementOperationId.ToString("N")),
                    managementOperation.RecordId);
                Assert.Equal(
                    Digest,
                    Field(
                        managementOperation,
                        "guests.management-operation")
                        .GetProperty("requestFingerprint")
                        .GetString());
            });
        Assert.Equal(
            GuestsTenantTerminationMetadata.ExportSchemaId,
            contributor.ExportDescriptor.ExportSchemaId);
        Assert.Equal(
            GuestsTenantTerminationMetadata.ExportSchemaVersion,
            contributor.ExportDescriptor.ExportSchemaVersion);
        Assert.Equal(6, contributor.Descriptor.CatalogVersion);
        Assert.Equal(
            GuestsTenantTerminationMetadata.CatalogSha256,
            contributor.Descriptor.CatalogSha256);
        Assert.Equal(5, contributor.ExportDescriptor.ExportSchemaVersion);
        Assert.Contains(
            "catalog=6",
            GuestsTenantTerminationMetadata.CatalogManifest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catalog=5",
            GuestsTenantTerminationMetadata.CatalogManifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "export-schema=guests.tenant-termination-export:5",
            GuestsTenantTerminationMetadata.CatalogManifest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "export-schema=guests.tenant-termination-export:4",
            GuestsTenantTerminationMetadata.CatalogManifest,
            StringComparison.Ordinal);
        Assert.Equal(
            GuestsTenantTerminationMetadata.ExportFieldIds
                .OrderBy(field => field, StringComparer.Ordinal),
            contributor.ExportDescriptor.FieldIds);
        DataRightsExportRecord retentionReceipt = Assert.Single(
            first.Records,
            record => record.RecordType ==
                GuestsTenantTerminationMetadata
                    .RetentionAnonymisationReceiptRecordType);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            Field(retentionReceipt, "guests.retention-proof")
                .GetProperty("timeZoneCatalogVersion")
                .GetString());
        Assert.Equal(
            [
                TenantTerminationContributionPhase.Export,
                TenantTerminationContributionPhase.Destroy
            ],
            contributor.Descriptor.PhasePlans.Select(plan => plan.Phase));
        Assert.All(
            contributor.Descriptor.PhasePlans,
            plan => Assert.Equal(
                GuestsTenantTerminationMetadata.DependencyOwnerKey,
                Assert.Single(plan.DependsOnOwnerKeys)));

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                Request(),
                replay,
                CancellationToken.None);

        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(6, result.CatalogVersion);
        Assert.Equal(
            GuestsTenantTerminationMetadata.CatalogSha256,
            result.CatalogSha256);
        Assert.Equal(result.CatalogVersion, replayResult.CatalogVersion);
        Assert.Equal(result.CatalogSha256, replayResult.CatalogSha256);
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
        await using GuestsDbContext context = CreateContext(fences);
        GuestsTenantTerminationContributor contributor = new(
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
            "guests.termination.export-fence-unavailable",
            result.ResultCode);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Operational_save_rejects_a_frozen_workspace_without_advancing_revision()
    {
        MutableFenceReader fences = new();
        await using GuestsDbContext context = CreateContext(fences);
        context.GuestProfiles.Add(
            GuestsTenantTerminationTestData.CreateProfile(PropertyId));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        context.GuestProfiles.Add(
            GuestsTenantTerminationTestData.CreateProfile(
                PropertyId,
                "Blocked Guest"));

        GuestsOperationalAdmissionException failure =
            await Assert.ThrowsAsync<GuestsOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            GuestsOperationalAdmissionFailure.Restricted,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
        Assert.Single(await context.GuestProfiles.ToListAsync());
    }

    [Fact]
    public async Task Operational_saves_advance_the_tenant_revision_once_each()
    {
        MutableFenceReader fences = new();
        await using GuestsDbContext context = CreateContext(fences);
        context.GuestProfiles.Add(
            GuestsTenantTerminationTestData.CreateProfile(PropertyId));
        await context.SaveChangesAsync();

        context.GuestProfiles.Add(
            GuestsTenantTerminationTestData.CreateProfile(
                PropertyId,
                "Second Guest"));
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
        await using GuestsDbContext context = CreateContext(
            new ThrowingFenceReader());
        context.GuestProfiles.Add(
            GuestsTenantTerminationTestData.CreateProfile(PropertyId));

        GuestsOperationalAdmissionException failure =
            await Assert.ThrowsAsync<GuestsOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            GuestsOperationalAdmissionFailure.Unavailable,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.GuestProfiles.ToListAsync());
        Assert.Empty(await context.TenantRevisions.ToListAsync());
    }

    [Fact]
    public async Task Destroy_blocks_an_active_hold_before_opening_local_progress()
    {
        MutableFenceReader fences = new();
        await using GuestsDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        GuestsTenantTerminationContributor contributor = new(
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
            "guests.termination.destroy-active-hold",
            result.ResultCode);
        Assert.Equal(1, result.RemainingActiveCount);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        Assert.Empty(await context.TenantDestroyReceipts.ToListAsync());
        GuestsTenantRevision state =
            await context.TenantRevisions.SingleAsync();
        Assert.True(state.IsOpen);
        Assert.Null(state.DestroyOperationId);
    }

    [Fact]
    public async Task Destroy_resumes_to_completion_and_exactly_replays()
    {
        MutableFenceReader fences = new();
        await using GuestsDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        GuestDataHold hold = await context.DataHolds.SingleAsync();
        Assert.True(hold.Release(
            hold.Version,
            "user:privacy-controller",
            Now.AddMinutes(8)).IsSuccess);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        GuestsTenantTerminationContributor contributor = new(
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
        Assert.Equal("guests.termination.destroyed", result.ResultCode);
        Assert.True(result.AffectedCount > 0);
        Assert.Equal(2, result.SelectedProofRevision);
        Assert.Equal(3, result.ResultingProofRevision);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        GuestsTenantDestroyReceipt receipt =
            await context.TenantDestroyReceipts.SingleAsync();
        Assert.Equal(result.AffectedCount, receipt.RemovedRecordCount);
        Assert.Equal(
            GuestsTenantLifecycleStatus.Closed,
            (await context.TenantRevisions.SingleAsync()).LifecycleStatus);
        Assert.False(await HasOwnerRecordsAsync(context));

        TenantTerminationContributionResult replay =
            await contributor.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(6, result.CatalogVersion);
        Assert.Equal(
            GuestsTenantTerminationMetadata.CatalogSha256,
            result.CatalogSha256);
        Assert.Equal(result, replay);

        TenantTerminationContributionResult conflict =
            await contributor.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);
        Assert.Equal(
            "guests.termination.destroy-conflict",
            conflict.ResultCode);

        context.TenantDestroyReceipts.Remove(receipt);
        InvalidOperationException receiptMutation =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Contains("append-only", receiptMutation.Message);
        context.ChangeTracker.Clear();
        fences.Current = null;
        context.PropertyProjections.Add(new GuestPropertyProjection(
            TenantId,
            PropertyId,
            "Closed property",
            BunkFy.Modules.Properties.Contracts.PropertyStatus.Active,
            version: 1));
        GuestsOperationalAdmissionException closedFailure =
            await Assert.ThrowsAsync<GuestsOperationalAdmissionException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            GuestsOperationalAdmissionFailure.Restricted,
            closedFailure.Failure);

        context.ChangeTracker.Clear();
        context.OutboxMessages.Add(new OutboxMessage(
            Guid.NewGuid(),
            "bunkfy.guests.lifecycle-test.v1",
            "lifecycle-test",
            version: 1,
            TenantId,
            Now,
            "{}",
            Now));
        GuestsOperationalAdmissionException messageFailure =
            await Assert.ThrowsAsync<GuestsOperationalAdmissionException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            GuestsOperationalAdmissionFailure.Restricted,
            messageFailure.Failure);
    }

    [Fact]
    public void Destroy_progress_rejects_a_batch_above_the_persisted_bound()
    {
        GuestsTenantDestroyOperation operation = Assert.IsType<
            GuestsTenantDestroyOperation>(
            GuestsTenantDestroyOperation.TryCreate(
                Guid.NewGuid(),
                TenantId,
                Digest,
                selectedRevision: 4,
                GuestsTenantDestroyOperation.MaximumBatchSize,
                Now));

        Assert.False(operation.RecordBatch(
            GuestsTenantDestroyStage.OutboxMessages,
            GuestsTenantDestroyOperation.MaximumBatchSize + 1,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(0, operation.RemovedRecordCount);
        Assert.Equal(0, operation.CompletedBatchCount);

        Assert.True(operation.RecordBatch(
            GuestsTenantDestroyStage.OutboxMessages,
            GuestsTenantDestroyOperation.MaximumBatchSize,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(
            GuestsTenantDestroyOperation.MaximumBatchSize,
            operation.RemovedRecordCount);
        Assert.Equal(1, operation.CompletedBatchCount);
    }

    [Fact]
    public void Destroy_progress_preserves_the_terminal_stage_and_visits_management_operations()
    {
        Assert.Equal(20, (int)GuestsTenantDestroyStage.Completed);
        Assert.Equal(21, (int)GuestsTenantDestroyStage.ManagementOperations);
        GuestsTenantDestroyOperation operation = Assert.IsType<
            GuestsTenantDestroyOperation>(
            GuestsTenantDestroyOperation.TryCreate(
                Guid.NewGuid(),
                TenantId,
                Digest,
                selectedRevision: 4,
                batchSize: 10,
                Now));

        while (operation.Stage != GuestsTenantDestroyStage.StayHistory)
        {
            Assert.True(operation.AdvanceEmptyStage(Now.AddMinutes(1)));
        }

        Assert.True(operation.AdvanceEmptyStage(Now.AddMinutes(1)));
        Assert.Equal(GuestsTenantDestroyStage.ManagementOperations, operation.Stage);
        Assert.True(operation.AdvanceEmptyStage(Now.AddMinutes(1)));
        Assert.Equal(GuestsTenantDestroyStage.GuestProfiles, operation.Stage);
    }

    [Theory]
    [InlineData("correction")]
    [InlineData("restriction")]
    [InlineData("hold")]
    [InlineData("anonymisation")]
    [InlineData("restore")]
    [InlineData("retention")]
    public async Task Governance_receipts_are_append_only(
        string receiptKind)
    {
        MutableFenceReader fences = new();
        await using GuestsDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        object receipt = receiptKind switch
        {
            "correction" =>
                await context.DataRightsCorrectionReceipts.SingleAsync(),
            "restriction" =>
                await context.ProcessingRestrictionReceipts.SingleAsync(),
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
        await using GuestsDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        GuestAnonymisationTombstone tombstone =
            await context.AnonymisationTombstones.SingleAsync(candidate =>
                candidate.Authority ==
                    GuestAnonymisationAuthority.Retention);
        context.AnonymisationTombstones.Remove(tombstone);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            "Guest anonymisation tombstones cannot be deleted.",
            failure.Message);
    }

    private static void SeedGraph(GuestsDbContext context)
    {
        GuestProfile profile =
            GuestsTenantTerminationTestData.CreateProfile(
                PropertyId,
                creationConfirmationId: CreationConfirmationId);
        long managementExpectedVersion = profile.Version;
        Assert.True(profile.Update(
            profile.DisplayName,
            profile.LegalName,
            profile.Email,
            profile.Phone,
            profile.DateOfBirth,
            profile.NationalityCountryCode,
            profile.PreferredLanguageTag,
            profile.Notes,
            managementExpectedVersion,
            "user:owner",
            Guid.NewGuid(),
            Now,
            [profile.PreferredLanguageTag!, "fr", "unknown-١"]).IsSuccess);
        context.GuestProfiles.Add(profile);
        context.ManagementOperations.Add(new GuestManagementOperation(
            new GuestManagementOperationRecord(
                ManagementOperationId,
                TenantId,
                PropertyId,
                profile.Id,
                GuestManagementOperationKind.Update,
                managementExpectedVersion,
                Digest,
                BunkFy.Modules.Guests.Contracts.GuestStatus.Active,
                profile.Version,
                Now)));
        GuestProfile secondProfile =
            GuestsTenantTerminationTestData.CreateProfile(
                PropertyId,
                "Second Management Guest");
        long secondExpectedVersion = secondProfile.Version;
        Assert.True(secondProfile.Update(
            secondProfile.DisplayName,
            secondProfile.LegalName,
            secondProfile.Email,
            secondProfile.Phone,
            secondProfile.DateOfBirth,
            secondProfile.NationalityCountryCode,
            secondProfile.PreferredLanguageTag,
            secondProfile.Notes,
            secondExpectedVersion,
            "user:owner",
            Guid.NewGuid(),
            Now).IsSuccess);
        context.GuestProfiles.Add(secondProfile);
        context.ManagementOperations.Add(new GuestManagementOperation(
            new GuestManagementOperationRecord(
                ManagementOperationId,
                TenantId,
                PropertyId,
                secondProfile.Id,
                GuestManagementOperationKind.Update,
                secondExpectedVersion,
                Digest,
                BunkFy.Modules.Guests.Contracts.GuestStatus.Active,
                secondProfile.Version,
                Now)));
        context.DataRightsCorrectionReceipts.Add(
            GuestDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                PropertyId,
                CaseId,
                approvalRevision: 1,
                profile.Id,
                selectedRecordVersion: 1,
                currentRecordVersion: 2,
                [GuestProfileField.DisplayName],
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now.AddMinutes(1)).Value);

        GuestProcessingRestriction restriction =
            GuestProcessingRestriction.Create(
                Guid.NewGuid(),
                TenantId,
                PropertyId,
                profile.Id,
                CaseId,
                applyApprovalRevision: 1,
                applySelectedGuestVersion: profile.Version,
                "user:owner",
                Now.AddMinutes(2)).Value;
        context.ProcessingRestrictions.Add(restriction);
        context.ProcessingRestrictionReceipts.Add(
            GuestProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                restriction.Id,
                GuestProcessingRestrictionAction.Apply,
                PropertyId,
                profile.Id,
                CaseId,
                approvalRevision: 1,
                selectedGuestVersion: profile.Version,
                GuestProcessingRestrictionContract.CurrentVersion,
                resultingRestrictionVersion: restriction.Version,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                "user:owner",
                Guid.NewGuid(),
                Now.AddMinutes(2)).Value);

        GuestDataHold hold = GuestDataHold.Place(
            Guid.NewGuid(),
            TenantId,
            PropertyId,
            profile.Id,
            "regulatory-request",
            "user:owner",
            Now.AddMinutes(3)).Value;
        context.DataHolds.Add(hold);
        context.DataHoldReceipts.Add(GuestDataHoldReceipt.Create(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            hold,
            DomainDataHoldAction.Place,
            profile.Version,
            "user:owner",
            Now.AddMinutes(3)).Value);

        SeedAnonymisationProof(context);
        SeedRetentionProof(context);
    }

    private static void SeedAnonymisationProof(GuestsDbContext context)
    {
        GuestProfile profile =
            GuestsTenantTerminationTestData.CreateProfile(
                OtherPropertyId,
                "Anonymised Guest Source");
        GuestProfileAnonymisationOutcome outcome = profile.Anonymise(
            profile.Version,
            "user:privacy-executor",
            Guid.NewGuid(),
            Now.AddMinutes(4)).Value;
        GuestAnonymisationReceipt receipt =
            GuestAnonymisationReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                OtherPropertyId,
                CaseId,
                approvalRevision: 1,
                operationRevision: 2,
                profile.Id,
                outcome.PreviousVersion,
                outcome.CurrentVersion,
                affectedPropertyCount: 1,
                Digest,
                Digest,
                outcome.EventId,
                "user:privacy-executor",
                Now.AddMinutes(4)).Value;
        GuestAnonymisationTombstone tombstone =
            GuestAnonymisationTombstone.Create(
                TenantId,
                profile.Id,
                receipt.CompletedAtUtc,
                receipt.CanonicalSha256).Value;
        Guid ledgerEntryId = Guid.NewGuid();
        DateTimeOffset replayedAtUtc = Now.AddMinutes(5);
        Assert.True(tombstone.AttachRestoreProof(
            ledgerEntryId,
            receipt.CompletedAtUtc,
            receipt.CanonicalSha256,
            replayedAtUtc).IsSuccess);
        GuestAnonymisationRestoreReceipt restoreReceipt =
            GuestAnonymisationRestoreReceipt.Create(
                TenantId,
                ledgerEntryId,
                profile.Id,
                receipt.ContractVersion,
                receipt.Id,
                receipt.CanonicalSha256,
                receipt.ResultingGuestVersion,
                tombstone.Revision,
                replayedAtUtc).Value;

        context.GuestProfiles.Add(profile);
        context.AnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(tombstone);
        context.AnonymisationRestoreReceipts.Add(restoreReceipt);
    }

    private static void SeedRetentionProof(GuestsDbContext context)
    {
        GuestProfile profile =
            GuestsTenantTerminationTestData.CreateProfile(
                PropertyId,
                "Retention Guest");
        GuestRetentionExecution execution =
            GuestRetentionExecution.Start(
                Guid.NewGuid(),
                TenantId,
                "guest-operational",
                executionPolicyVersion:
                    GuestRetentionExecution.MinimumRunningPolicyVersion,
                attempt: 1,
                startingProjectionOrdinal: 0,
                Now.AddMinutes(-2),
                Now.AddMinutes(10)).Value;
        GuestProfileAnonymisationOutcome outcome =
            profile.AnonymiseForRetention(
                profile.Version,
                "system:retention",
                Guid.NewGuid(),
                Now).Value;
        GuestRetentionAnonymisationReceipt receipt =
            GuestRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                execution.Id,
                profile.Id,
                outcome.PreviousVersion,
                outcome.CurrentVersion,
                affectedPropertyCount: 1,
                Now.AddDays(-1),
                Digest,
                TimeZoneCatalog.Default.CatalogVersion,
                outcome.EventId,
                "system:retention",
                Now).Value;
        GuestAnonymisationTombstone tombstone =
            GuestAnonymisationTombstone.CreateForRetention(
                TenantId,
                receipt).Value;

        context.GuestProfiles.Add(profile);
        context.RetentionExecutions.Add(execution);
        context.RetentionAnonymisationReceipts.Add(receipt);
        context.AnonymisationTombstones.Add(tombstone);
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
        GuestsDbContext context) =>
        await context.GuestProfiles.AnyAsync() ||
        await context.ManagementOperations.AnyAsync() ||
        await context.DataRightsCorrectionReceipts.AnyAsync() ||
        await context.ProcessingRestrictions.AnyAsync() ||
        await context.ProcessingRestrictionReceipts.AnyAsync() ||
        await context.ProcessingRestrictionProjections.AnyAsync() ||
        await context.DataHolds.AnyAsync() ||
        await context.DataHoldReceipts.AnyAsync() ||
        await context.AnonymisationReceipts.AnyAsync() ||
        await context.AnonymisationTombstones.AnyAsync() ||
        await context.AnonymisationRestoreReceipts.AnyAsync() ||
        await context.RetentionExecutions.AnyAsync() ||
        await context.RetentionAnonymisationReceipts.AnyAsync() ||
        await context.PropertyProjections.AnyAsync() ||
        await context.StayHistory.AnyAsync() ||
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

    private static GuestsDbContext CreateContext(
        IWorkspaceTerminationFenceReader fences)
    {
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new GuestsDbContext(
            options,
            new TestScopeContext(),
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

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock(DateTimeOffset? utcNow = null) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow ?? Now;
    }
}

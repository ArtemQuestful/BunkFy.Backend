namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed partial class WorkspacesTenantTerminationExportContributorTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string SubjectId = "account-subject-a";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid ProcessId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid CaseId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid TerminationEpoch =
        Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset FrozenAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now =
        FrozenAtUtc.AddMinutes(1);

    [Fact]
    public async Task Export_streams_the_complete_portable_workspace_graph_in_stable_order()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspaceTerminationFence fence = SeedFence(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspacesTenantTerminationExportContributor contributor =
            CreateContributor(context);
        CollectingSink first = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(fence.Version),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("workspace.termination.exported", result.ResultCode);
        Assert.Equal(fence.Version, result.SelectedProofRevision);
        Assert.Equal(fence.Version, result.ResultingProofRevision);
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.CatalogVersion,
            result.CatalogVersion);
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.CatalogSha256,
            result.CatalogSha256);
        Assert.Equal(
            [
                WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingRecoveryReceiptRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffDeferredClaimWithdrawalRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingCorrectionReceiptRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingProcessingRestrictionRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingProcessingRestrictionReceiptRecordType,
                WorkspacesDataRightsCoordinates.StaffAccessProcessRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffAccessProfileSnapshotRecordType,
                WorkspacesDataRightsCoordinates.StaffAccessPlanRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffAccessPlanPropertyRecordType,
                WorkspacesDataRightsCoordinates
                    .StaffRetentionCorrelationReceiptRecordType,
                WorkspacesTenantTerminationMetadata
                    .StaffOnboardingRetentionExecutionRecordType
            ],
            first.Records.Select(record => record.RecordType).ToArray());
        Assert.DoesNotContain(
            first.Records,
            record => record.RecordType ==
                WorkspacesTenantTerminationExportContributor
                    .StaffIdentityAnchorSweepCheckpointRecordType);
        Assert.Equal(12, result.AffectedCount);
        DataRightsExportRecord deferredRecord = Assert.Single(first.Records, record =>
            record.RecordType == WorkspacesDataRightsExportContributor.StaffDeferredClaimWithdrawalRecordType);
        Assert.Equal(
            Guid.Parse("61000000-0000-0000-0000-000000000001"),
            deferredRecord.RecordId);
        Assert.Equal(2, deferredRecord.RecordVersion);
        Assert.Equal(
            [
                "workspaces.enrollment-claim-id",
                "workspaces.enrollment-claim-version",
                "workspaces.integration-event-id",
                "workspaces.integration-event-occurred-at",
                "workspaces.join-source-id",
                "workspaces.workspace-scope-id"
            ],
            deferredRecord.Fields
                .Select(field => field.FieldId)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.ExportSchemaId,
            contributor.ExportDescriptor.ExportSchemaId);
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.CatalogVersion,
            contributor.ExportDescriptor.CatalogVersion);
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.ExportSchemaVersion,
            contributor.ExportDescriptor.ExportSchemaVersion);
        Assert.Contains(
            "workspaces.identity-anchor-sweep.structured-control",
            contributor.ExportDescriptor.FieldIds);
        Assert.DoesNotContain(
            "workspaces.identity-anchor-sweep.structured-control",
            WorkspacesDataRightsExportSchema.Descriptor.FieldIds);
        DataRightsExportRecord accessProcessRecord = Assert.Single(
            first.Records,
            record => record.RecordType ==
                WorkspacesDataRightsCoordinates.StaffAccessProcessRecordType);
        Assert.Equal(
            "restore-snapshot",
            Assert.Single(
                accessProcessRecord.Fields,
                field => field.FieldId ==
                    "workspaces.access-process-restoration-disposition")
            .Value.GetString());

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                Request(fence.Version),
                replay,
                CancellationToken.None);

        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            first.Records.Select(Identity).ToArray(),
            replay.Records.Select(Identity).ToArray());
    }

    [Fact]
    public async Task Export_includes_one_privacy_minimal_active_sweep_checkpoint_in_stable_order()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeedGraph(context);
        Guid checkpointId =
            Guid.Parse("13000000-0000-0000-0000-000000000001");
        Guid cycleId =
            Guid.Parse("14000000-0000-0000-0000-000000000001");
        Guid runId =
            Guid.Parse("15000000-0000-0000-0000-000000000001");
        WorkspaceStaffIdentityAnchorSweepCheckpoint checkpoint =
            WorkspaceStaffIdentityAnchorSweepCheckpoint.Create(
                checkpointId,
                TenantId,
                FrozenAtUtc.AddHours(-4)).Value;
        Assert.True(checkpoint.BeginCycle(
            cycleId,
            upperOrdinal: 10,
            runId,
            FrozenAtUtc.AddHours(-3)).IsSuccess);
        Assert.True(checkpoint.Advance(
            expectedVersion: 2,
            cycleId,
            expectedAfterOrdinal: null,
            nextAfterOrdinal: 4,
            reachedEnd: false,
            Guid.Parse("16000000-0000-0000-0000-000000000001"),
            runId,
            new WorkspaceStaffIdentityAnchorSweepPageCounts(
                ScannedCount: 4,
                NoAnchorCount: 1,
                RemovedCount: 0,
                ObservedCount: 1,
                AlreadyObservedCount: 0,
                DeferredCount: 1,
                ConflictCount: 1,
                PassOneCommittedCount: 2,
                ResolutionRecordConfirmedCount: 1),
            FrozenAtUtc.AddHours(-2)).IsSuccess);
        context.StaffIdentityAnchorSweepCheckpoints.Add(checkpoint);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspaceTerminationFence fence = SeedFence(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspacesTenantTerminationExportContributor contributor =
            CreateContributor(context);
        CollectingSink first = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(fence.Version),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(13, result.AffectedCount);
        Assert.Equal(
            [
                WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingRecoveryReceiptRecordType,
                WorkspacesTenantTerminationExportContributor
                    .StaffIdentityAnchorSweepCheckpointRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffDeferredClaimWithdrawalRecordType
            ],
            first.Records.Take(4)
                .Select(record => record.RecordType)
                .ToArray());
        DataRightsExportRecord exported = Assert.Single(
            first.Records,
            record => record.RecordType ==
                WorkspacesTenantTerminationExportContributor
                    .StaffIdentityAnchorSweepCheckpointRecordType);
        Assert.Equal(checkpointId, exported.RecordId);
        Assert.Equal(checkpoint.Version, exported.RecordVersion);
        Assert.Single(exported.Fields);
        System.Text.Json.JsonElement state = Field(
            exported,
            "workspaces.identity-anchor-sweep.structured-control");
        Assert.Equal(1, state.GetProperty("protocolVersion").GetInt32());
        Assert.True(state.GetProperty("hasActiveCycle").GetBoolean());
        Assert.Equal(10, state.GetProperty("cycleUpperOrdinal").GetInt64());
        Assert.Equal(4, state.GetProperty("afterOrdinal").GetInt64());
        Assert.Equal(4, state.GetProperty("cycleScannedCount").GetInt64());
        Assert.Equal(2, state.GetProperty("cycleBacklogCount").GetInt64());
        Assert.Equal(
            0,
            state.GetProperty("lastCompletedScannedCount").GetInt64());
        Assert.Equal(
            0,
            state.GetProperty("lastCompletedBacklogCount").GetInt64());
        string serialized = state.GetRawText();
        Assert.DoesNotContain("subject", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scope", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cycleId", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("runId", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256", serialized, StringComparison.OrdinalIgnoreCase);

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                Request(fence.Version),
                replay,
                CancellationToken.None);
        DataRightsExportRecord replayed = Assert.Single(
            replay.Records,
            record => record.RecordType == exported.RecordType);
        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(Identity(exported), Identity(replayed));
        Assert.Equal(
            state.GetRawText(),
            Field(
                replayed,
                "workspaces.identity-anchor-sweep.structured-control")
            .GetRawText());
    }

    [Fact]
    public async Task Export_includes_privacy_minimal_historical_receipts_only_in_the_tenant_schema()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffHistoricalNoProvisionReceipt second =
            CreateHistoricalReceipt(2, TenantId);
        WorkspaceStaffHistoricalNoProvisionReceipt first =
            CreateHistoricalReceipt(1, TenantId);
        context.StaffHistoricalNoProvisionReceipts.AddRange(second, first);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspaceTerminationFence fence = SeedFence(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspacesTenantTerminationExportContributor contributor =
            CreateContributor(context);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(fence.Version),
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal(
            [first.Id, second.Id],
            sink.Records.Select(record => record.RecordId).ToArray());
        Assert.All(sink.Records, record =>
        {
            Assert.Equal(
                WorkspacesTenantTerminationExportContributor
                    .StaffHistoricalNoProvisionReceiptRecordType,
                record.RecordType);
            Assert.Equal(
                WorkspaceStaffHistoricalNoProvisionReceipt
                    .CurrentContractVersion,
                record.RecordVersion);
        });

        string[] receiptFieldIds =
        [
            "workspaces.actor-subject-id",
            "workspaces.identity-anchor-historical-no-provision.application-id",
            "workspaces.identity-anchor-historical-no-provision.canonical-digest",
            "workspaces.identity-anchor-historical-no-provision.contract-version",
            "workspaces.identity-anchor-historical-no-provision.expected-application-status",
            "workspaces.identity-anchor-historical-no-provision.expected-application-version",
            "workspaces.identity-anchor-historical-no-provision.external-evidence-digest",
            "workspaces.identity-anchor-historical-no-provision.external-evidence-manifest-id",
            "workspaces.identity-anchor-historical-no-provision.operation-id",
            "workspaces.identity-anchor-historical-no-provision.organizations-scope-revision",
            "workspaces.identity-anchor-historical-no-provision.organizations-source-status",
            "workspaces.identity-anchor-historical-no-provision.organizations-source-version",
            "workspaces.identity-anchor-historical-no-provision.receipt-id",
            "workspaces.identity-anchor-historical-no-provision.result-application-status",
            "workspaces.identity-anchor-historical-no-provision.result-application-version",
            "workspaces.identity-anchor-historical-no-provision.reviewed-at",
            "workspaces.identity-anchor-historical-no-provision.scope-id",
            "workspaces.identity-anchor-historical-no-provision.source-id",
            "workspaces.identity-anchor-historical-no-provision.source-kind",
            "workspaces.identity-anchor-historical-no-provision.staff-evidence-digest"
        ];
        DataRightsExportRecord exported = sink.Records[0];
        Assert.Equal(
            receiptFieldIds,
            exported.Fields.Select(field => field.FieldId).ToArray());
        Assert.Equal(
            first.CanonicalSha256,
            Field(
                exported,
                "workspaces.identity-anchor-historical-no-provision.canonical-digest")
            .GetString());
        Assert.Equal(
            first.ReviewerId,
            Field(
                exported,
                "workspaces.actor-subject-id")
            .GetString());
        Assert.All(receiptFieldIds, fieldId => Assert.Contains(
            fieldId,
            contributor.ExportDescriptor.FieldIds));
        Assert.All(
            receiptFieldIds.Where(fieldId => !string.Equals(
                fieldId,
                "workspaces.actor-subject-id",
                StringComparison.Ordinal)),
            fieldId => Assert.DoesNotContain(
            fieldId,
            WorkspacesDataRightsExportSchema.Descriptor.FieldIds));
        string serialized = string.Join(
            '|',
            exported.Fields.Select(field => field.Value.GetRawText()));
        Assert.DoesNotContain(
            "email",
            serialized,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "profile",
            serialized,
            StringComparison.OrdinalIgnoreCase);

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                Request(fence.Version),
                replay,
                CancellationToken.None);
        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            sink.Records.Select(Identity).ToArray(),
            replay.Records.Select(Identity).ToArray());
    }

    [Fact]
    public async Task Export_retries_without_writing_when_the_frozen_fence_does_not_match()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspaceTerminationFence fence = SeedFence(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspacesTenantTerminationExportContributor contributor =
            CreateContributor(context);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(fence.Version + 1),
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "workspace.termination.export-fence-unavailable",
            result.ResultCode);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Export_preflights_every_onboarding_page_before_the_first_sink_write()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeedGraph(context);
        for (int index = 1; index <= 501; index++)
        {
            WorkspaceStaffOnboarding application =
                WorkspaceStaffOnboarding.Create(
                    Coordinate(index),
                    TenantId,
                    WorkspaceStaffOnboardingSource.Invitation,
                    Coordinate(index + 10_000),
                    $"subject:{index}",
                    $"staff-{index}@example.test",
                    $"Staff {index}",
                    legalName: null,
                    workEmail: null,
                    workPhone: null,
                    employeeNumber: null,
                    jobTitle: null,
                    department: null,
                    FrozenAtUtc.AddDays(-2)).Value;
            context.StaffOnboardingApplications.Add(application);
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspaceTerminationFence fence = SeedFence(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        SecondPageUnavailableOutcomeReader outcomes = new();
        WorkspacesTenantTerminationExportContributor contributor =
            CreateContributor(context, outcomes);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(fence.Version),
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "workspace.termination.export-identity-anchor-unavailable",
            result.ResultCode);
        Assert.Equal([500, 2], outcomes.BatchSizes);
        Assert.Empty(sink.Records);
    }

    [Theory]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Corrupt)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved)]
    public async Task Export_preflight_retries_without_writing_for_non_authoritative_anchor_state(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus status)
    {
        await using WorkspacesDbContext context = CreateContext();
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspaceTerminationFence fence = SeedFence(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        Guid target =
            Guid.Parse("76000000-0000-0000-0000-000000000001");
        Guid resolutionEventId =
            Guid.Parse("77000000-0000-0000-0000-000000000001");
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes = new(
            request => new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                request.ApplicationId,
                status,
                target,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                status ==
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved
                        ? StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                            .Mismatch
                        : StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                            .Exact,
                status ==
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved
                        ? 1
                        : null,
                status ==
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved
                        ? StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                            .CompletedRedacted
                        : null,
                resolutionEventId));
        WorkspacesTenantTerminationExportContributor contributor =
            CreateContributor(context, outcomes);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(fence.Version),
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "workspace.termination.export-identity-anchor-unavailable",
            result.ResultCode);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Export_preflight_accepts_exact_observed_resolution_and_exports_its_receipt()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeedGraph(context);
        WorkspaceStaffOnboarding onboarding = Assert.Single(
            context.ChangeTracker.Entries<WorkspaceStaffOnboarding>())
            .Entity;
        Guid staffMemberId =
            Guid.Parse("73000000-0000-0000-0000-000000000001");
        Guid resolutionEventId =
            Guid.Parse("74000000-0000-0000-0000-000000000001");
        Guid continuationEventId =
            Guid.Parse("75000000-0000-0000-0000-000000000001");
        Assert.Null(onboarding.StaffMemberId);
        Assert.True(
            onboarding.ObserveInvitationAccepted(
                FrozenAtUtc.AddHours(-23)).IsSuccess);
        Assert.True(onboarding.BeginProvisioning(
            FrozenAtUtc.AddHours(-23).AddMinutes(30)).IsSuccess);
        Assert.True(
            onboarding.MarkStaffReady(
                staffMemberId,
                resolutionEventId,
                continuationEventId,
                FrozenAtUtc.AddHours(-22)).IsSuccess);
        Assert.True(
            onboarding.Complete(
                FrozenAtUtc.AddHours(-21)).IsSuccess);
        Assert.True(
            onboarding.ObserveResolution(
                resolutionEventId,
                staffMemberId,
                onboarding.IdentityAnchorResolutionApplicationVersion!.Value,
                onboarding.IdentityAnchorResolutionDisposition!.Value,
                FrozenAtUtc.AddHours(-20)).IsSuccess);
        string pseudonym = "workspaces-erased:tenant-export-subject";
        context.Entry(onboarding)
            .Property(application => application.SubjectId)
            .CurrentValue = pseudonym;
        context.Entry(onboarding)
            .Property(application => application.Version)
            .CurrentValue = onboarding.Version + 1;
        context.Entry(onboarding)
            .Property(application => application.LastChangedAtUtc)
            .CurrentValue = FrozenAtUtc.AddHours(-19);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspaceTerminationFence fence = SeedFence(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader reader = new(
            request => request.ApplicationId == onboarding.Id
                ? new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                    onboarding.Id,
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved,
                    staffMemberId,
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Mismatch,
                    onboarding.IdentityAnchorResolutionApplicationVersion,
                    StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                        .CompletedRedacted,
                    resolutionEventId)
                : StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
                    .Absent(request));
        WorkspacesTenantTerminationExportContributor contributor =
            CreateContributor(context, reader);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(fence.Version),
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        DataRightsExportRecord exported = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                WorkspacesDataRightsCoordinates.StaffOnboardingRecordType);
        Assert.Equal(
            resolutionEventId,
            Field(
                exported,
                "workspaces.identity-anchor.expected-resolution-event-id")
            .GetGuid());
        Assert.Equal(
            pseudonym,
            Field(exported, "workspaces.auth-subject-id").GetString());
        Assert.Equal(
            continuationEventId,
            Field(
                exported,
                "workspaces.identity-anchor.continuation-event-id")
            .GetGuid());
        Assert.Equal(
            resolutionEventId,
            Field(
                exported,
                "workspaces.identity-anchor.resolution-event-id")
            .GetGuid());
        Assert.Equal(
            staffMemberId,
            Field(
                exported,
                "workspaces.identity-anchor.resolution-staff-member-id")
            .GetGuid());
        Assert.Equal(
            onboarding.IdentityAnchorResolutionApplicationVersion,
            Field(
                exported,
                "workspaces.identity-anchor.resolution-application-version")
            .GetInt64());
        Assert.Equal(
            "completed-redacted",
            Field(
                exported,
                "workspaces.identity-anchor.resolution-disposition")
            .GetString());
        Assert.Equal(
            onboarding.IdentityAnchorResolutionIntentAtUtc,
            Field(
                exported,
                "workspaces.identity-anchor.resolution-intent-at-utc")
            .GetDateTimeOffset());
        Assert.Equal(
            onboarding.IdentityAnchorResolutionObservedAtUtc,
            Field(
                exported,
                "workspaces.identity-anchor.resolution-observed-at-utc")
            .GetDateTimeOffset());
        Assert.Equal(
            onboarding.IdentityAnchorSweepOrdinal,
            Field(
                exported,
                "workspaces.identity-anchor.sweep-ordinal")
            .GetInt64());
    }

    [Fact]
    public async Task Export_rejects_non_export_and_cross_tenant_requests()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspacesTenantTerminationExportContributor contributor =
            CreateContributor(context);
        CollectingSink sink = new();
        TenantTerminationExportRequest request = Request(1);

        TenantTerminationContributionResult wrongPhase =
            await contributor.ExportAsync(
                request with
                {
                    Contribution = request.Contribution with
                    {
                        Phase = TenantTerminationContributionPhase.Freeze
                    }
                },
                sink,
                CancellationToken.None);
        TenantTerminationContributionResult wrongTenant =
            await contributor.ExportAsync(
                request with
                {
                    Contribution = request.Contribution with
                    {
                        TenantId = Guid.NewGuid().ToString("D")
                    }
                },
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            wrongPhase.Status);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            wrongTenant.Status);
        Assert.Empty(sink.Records);
    }

    private static void SeedGraph(WorkspacesDbContext context)
    {
        Guid sourceId =
            Guid.Parse("50000000-0000-0000-0000-000000000001");
        Guid onboardingId =
            Guid.Parse("60000000-0000-0000-0000-000000000001");
        Guid staffMemberId =
            Guid.Parse("70000000-0000-0000-0000-000000000001");
        Guid propertyId =
            Guid.Parse("80000000-0000-0000-0000-000000000001");
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                onboardingId,
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                SubjectId,
                "staff@example.test",
                "Ada Operator",
                legalName: null,
                "staff@example.test",
                "+44 20 5555 0100",
                employeeNumber: null,
                jobTitle: "Manager",
                department: "Operations",
                FrozenAtUtc.AddDays(-1)).Value;
        Assert.True(onboarding.Fail(
            "Workspaces.TestInducedFailure",
            FrozenAtUtc.AddHours(-23)).IsSuccess);
        WorkspaceStaffOnboardingCorrectionReceipt correction =
            WorkspaceStaffOnboardingCorrectionReceipt.Create(
                Guid.Parse(
                    "90000000-0000-0000-0000-000000000001"),
                TenantId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 1,
                onboarding.Id,
                selectedRecordVersion: 1,
                currentRecordVersion: 2,
                [WorkspaceStaffOnboardingApplicantField.DisplayName],
                Digest,
                Guid.NewGuid(),
                Guid.NewGuid(),
                FrozenAtUtc.AddHours(-12)).Value;
        WorkspaceStaffOnboardingRecoveryReceipt recovery =
            WorkspaceStaffOnboardingRecoveryReceipt.Create(new(
                Guid.Parse(
                    "91000000-0000-0000-0000-000000000001"),
                TenantId,
                Guid.Parse(
                    "92000000-0000-0000-0000-000000000001"),
                onboarding.Id,
                sourceId,
                staffMemberId,
                WorkspaceStaffOnboardingRecoveryTriggerKind.ManualOperator,
                Guid.Parse(
                    "93000000-0000-0000-0000-000000000001"),
                SubjectId,
                SubjectId,
                "recovery-operator",
                "system:workspace-staff-onboarding",
                Digest,
                onboarding.Version,
                onboarding.Version,
                onboarding.Status,
                onboarding.Version,
                onboarding.Status,
                Guid.Parse(
                    "94000000-0000-0000-0000-000000000001"),
                CurrentProfileVersion: 1,
                PropertyCount: 1,
                Digest,
                OrganizationRevision: null,
                MembershipId: null,
                MembershipVersion: null,
                MembershipStatus: null,
                StaffTenantRevision: null,
                StaffMemberVersion: null,
                StaffStateProofSha256: null,
                StaffPropertySetSha256: null,
                AccessManagementRevision: null,
                AccessFactsSha256: null,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                WorkspaceStaffOnboardingRecoveryReason
                    .LegacyMembershipBindingMissing,
                AccessMutationPerformed: false,
                OrganizationObservedAtUtc: null,
                StaffObservedAtUtc: null,
                AccessObservedAtUtc: null,
                FrozenAtUtc.AddHours(-11))).Value;
        WorkspaceStaffOnboardingProcessingRestriction restriction =
            WorkspaceStaffOnboardingProcessingRestriction.Create(
                Guid.Parse(
                    "a0000000-0000-0000-0000-000000000001"),
                TenantId,
                onboarding.Id,
                Guid.NewGuid(),
                applyApprovalRevision: 1,
                onboarding.Version,
                "privacy-operator",
                FrozenAtUtc.AddHours(-10)).Value;
        WorkspaceStaffOnboardingProcessingRestrictionReceipt
            restrictionReceipt =
                WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                    Guid.Parse(
                        "b0000000-0000-0000-0000-000000000001"),
                    TenantId,
                    Guid.NewGuid(),
                    restriction.Id,
                    WorkspaceStaffOnboardingProcessingRestrictionAction.Apply,
                    onboarding.Id,
                    restriction.ApplyCaseId,
                    restriction.ApplyApprovalRevision,
                    onboarding.Version,
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                    restriction.Version,
                    resultingProjectionRevision: 1,
                    effectiveRestricted: true,
                    "privacy-operator",
                    Guid.NewGuid(),
                    FrozenAtUtc.AddHours(-10)).Value;
        WorkspaceStaffAccessProcess process =
            WorkspaceStaffAccessProcess.Create(
                Guid.Parse(
                    "c0000000-0000-0000-0000-000000000001"),
                TenantId,
                staffMemberId,
                SubjectId,
                WorkspaceStaffAccessTargetState.Active,
                targetStaffVersion: 2,
                DateOnly.FromDateTime(FrozenAtUtc.UtcDateTime),
                SubjectId,
                [
                    new WorkspaceStaffAccessProfileTarget(
                        Guid.Parse(
                            "d0000000-0000-0000-0000-000000000001"),
                        $"property:{propertyId:N}")
                ],
                FrozenAtUtc.AddHours(-8)).Value;
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            sourceId,
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.Parse("d0000000-0000-0000-0000-000000000002"),
            "workspace-manager",
            [propertyId],
            SubjectId,
            FrozenAtUtc.AddHours(-7)).Value;
        WorkspaceStaffRetentionCorrelationReceipt retention =
            WorkspaceStaffRetentionCorrelationReceipt.Create(
                Guid.Parse(
                    "e0000000-0000-0000-0000-000000000001"),
                TenantId,
                Guid.NewGuid(),
                staffMemberId,
                selectedStaffVersion: 1,
                onboardingRecordsScrubbed: 0,
                accessProcessRecordsScrubbed: 0,
                accessPlanRecordsScrubbed: 0,
                FrozenAtUtc.AddHours(-6)).Value;
        WorkspaceStaffOnboardingRetentionExecution retentionExecution =
            WorkspaceStaffOnboardingRetentionExecution.Start(
                Guid.Parse(
                    "e0000000-0000-0000-0000-000000000002"),
                TenantId,
                WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
                WorkspaceStaffOnboardingRetentionCoordinates
                    .ExecutionPolicyVersion,
                attempt: 1,
                FrozenAtUtc.AddHours(-5),
                FrozenAtUtc.AddHours(-4)).Value;
        Assert.True(retentionExecution.RecordCandidate(
            attempt: 1,
            affected: true).IsSuccess);
        Assert.True(retentionExecution.Complete(
            WorkspaceStaffOnboardingRetentionExecutionState.Completed,
            attempt: 1,
            scannedCount: 1,
            remainingCount: 0,
            WorkspaceStaffOnboardingRetentionCoordinates.CompletedOutcome,
            FrozenAtUtc.AddHours(-4.5)).IsSuccess);

        WorkspaceStaffDeferredClaimWithdrawal deferred =
            WorkspaceStaffDeferredClaimWithdrawal.Create(
                TenantId,
                Guid.Parse(TenantId),
                Guid.Parse("51000000-0000-0000-0000-000000000001"),
                Guid.Parse("61000000-0000-0000-0000-000000000001"),
                claimVersion: 2,
                Guid.Parse("71000000-0000-0000-0000-000000000001"),
                FrozenAtUtc.AddHours(-5)).Value;

        context.AddRange(
            onboarding,
            recovery,
            deferred,
            correction,
            restriction,
            restrictionReceipt,
            process,
            plan,
            retention,
            retentionExecution);
    }

    private static WorkspaceTerminationFence SeedFence(
        WorkspacesDbContext context)
    {
        WorkspaceTerminationFence fence = WorkspaceTerminationFence.Freeze(
            Guid.Parse("f0000000-0000-0000-0000-000000000001"),
            TenantId,
            ProcessId,
            CaseId,
            approvalRevision: 1,
            TerminationEpoch,
            Digest,
            "termination-operator",
            FrozenAtUtc).Value;
        context.WorkspaceTerminationFences.Add(fence);
        return fence;
    }

    private static TenantTerminationExportRequest Request(
        long workspaceFenceRevision) =>
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
                Guid.Parse(
                    "11000000-0000-0000-0000-000000000001"),
                Guid.Parse(
                    "12000000-0000-0000-0000-000000000001"),
                Digest,
                "termination-exporter",
                Now.AddMinutes(5)),
            FreezeOperationRevision: 1,
            workspaceFenceRevision,
            Digest,
            FrozenAtUtc);

    private static string Identity(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|{record.RecordVersion}";

    private static System.Text.Json.JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(
            record.Fields,
            field => field.FieldId == fieldId).Value;

    private static Guid Coordinate(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        bytes[15] = 1;
        return new Guid(bytes);
    }

    private static WorkspaceStaffHistoricalNoProvisionReceipt
        CreateHistoricalReceipt(
            int coordinate,
            string tenantId) =>
        WorkspaceStaffHistoricalNoProvisionReceipt.Create(
            Coordinate(100_000 + coordinate),
            tenantId,
            Coordinate(200_000 + coordinate),
            Coordinate(300_000 + coordinate),
            WorkspaceStaffOnboardingSource.Invitation,
            Coordinate(400_000 + coordinate),
            expectedApplicationVersion: 3,
            WorkspaceStaffOnboardingState.Superseded,
            resultApplicationVersion: 3,
            WorkspaceStaffOnboardingState.Superseded,
            organizationsScopeRevision: coordinate,
            organizationsSourceVersion: coordinate,
            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                .InvitationRevoked,
            new string('a', 64),
            Coordinate(500_000 + coordinate),
            new string('b', 64),
            "operator:historical-review",
            FrozenAtUtc.AddSeconds(coordinate)).Value;

    private static WorkspacesDbContext CreateContext()
    {
        InMemoryDatabaseRoot databaseRoot = new();
        return CreateContext(
            Guid.NewGuid().ToString("N"),
            TenantId,
            databaseRoot);
    }

    private static WorkspacesDbContext CreateContext(
        string databaseName,
        string tenantId,
        InMemoryDatabaseRoot databaseRoot)
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(databaseName, databaseRoot)
                .Options;
        return new(options, new TestScopeContext(tenantId));
    }

    private static WorkspacesTenantTerminationExportContributor
        CreateContributor(
            WorkspacesDbContext context,
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader? reader =
                null) => new(
            context,
            new TestScopeContext(),
            new TestClock(),
            reader ?? new StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(),
            NullLogger<WorkspacesTenantTerminationExportContributor>.Instance);

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

    private sealed class SecondPageUnavailableOutcomeReader
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
    {
        public List<int> BatchSizes { get; } = [];

        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
                IReadOnlyList<
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                    requests,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.BatchSizes.Add(requests.Count);
            int page = this.BatchSizes.Count;
            StaffWorkspaceOnboardingIdentityAnchorOutcome[] results = requests
                .Select((request, index) => page == 2 && index == 0
                    ? new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                        request.ApplicationId,
                        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                            .Unresolved,
                        Guid.Parse(
                            "71000000-0000-0000-0000-000000000001"),
                        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                            .Active,
                        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                            .Exact,
                        WorkspaceApplicationVersion: null,
                        ResolutionDisposition: null,
                        Guid.Parse(
                            "72000000-0000-0000-0000-000000000001"))
                    : StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
                        .Absent(request))
                .ToArray();
            return Task.FromResult<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>(results);
        }
    }

    private sealed class TestScopeContext(string scopeId = TenantId)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}

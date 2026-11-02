namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
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
        WorkspacesTenantTerminationExportContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock());
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
    public async Task Export_retries_without_writing_when_the_frozen_fence_does_not_match()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspaceTerminationFence fence = SeedFence(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspacesTenantTerminationExportContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock());
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
    public async Task Export_rejects_non_export_and_cross_tenant_requests()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspacesTenantTerminationExportContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock());
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

    private static WorkspacesDbContext CreateContext()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new(options, new TestScopeContext());
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

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}

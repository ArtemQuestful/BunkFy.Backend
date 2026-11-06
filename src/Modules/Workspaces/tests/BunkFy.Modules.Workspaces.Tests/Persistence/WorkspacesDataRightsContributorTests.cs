namespace BunkFy.Modules.Workspaces.Tests;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DomainResolutionDisposition =
    Workspaces.Domain.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition;

[Trait("Category", "Unit")]
public sealed class WorkspacesDataRightsContributorTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string SubjectId = "account-subject-a";
    private static readonly Guid StaffMemberId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyA =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyB =
        Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Account_subject_discovery_returns_owned_records_and_rejects_weak_or_retained_lookup()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeedGraph(context);
        WorkspaceStaffOnboarding active = WorkspaceStaffOnboarding.Create(
            Guid.Parse(
                "50000000-0000-0000-0000-000000000002"),
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.Parse(
                "40000000-0000-0000-0000-000000000002"),
            "active-subject",
            "active@example.test",
            "Active Staff",
            legalName: null,
            "active@example.test",
            "+44 20 5555 0456",
            employeeNumber: null,
            jobTitle: null,
            department: null,
            Now).Value;
        context.StaffOnboardingApplications.Add(active);
        await context.SaveChangesAsync();
        WorkspacesDataRightsDiscoveryContributor contributor =
            new(context, new TestScopeContext());

        DataRightsSubjectDiscoveryResult discovered =
            await contributor.DiscoverAsync(
                DiscoveryRequest(AccountSubjectId: SubjectId),
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectDiscoveryStatus.Succeeded,
            discovered.Status);
        Assert.Equal(
            [
                WorkspacesDataRightsCoordinates.StaffAccessPlanRecordType,
                WorkspacesDataRightsCoordinates.StaffAccessProcessRecordType,
                WorkspacesDataRightsCoordinates.StaffOnboardingRecordType
            ],
            discovered.Candidates
                .Select(candidate => candidate.Coordinate.RecordType)
                .ToArray());
        DataRightsSubjectCandidate onboarding = Assert.Single(
            discovered.Candidates,
            candidate =>
                candidate.Coordinate.RecordType ==
                WorkspacesDataRightsCoordinates
                    .StaffOnboardingRecordType);
        Assert.Null(onboarding.EmailHint);
        Assert.Null(onboarding.PhoneHint);

        DataRightsSubjectDiscoveryResult activeDiscovered =
            await contributor.DiscoverAsync(
                DiscoveryRequest(AccountSubjectId: "active-subject"),
                CancellationToken.None);
        DataRightsSubjectCandidate activeCandidate =
            Assert.Single(activeDiscovered.Candidates);
        Assert.Equal(
            "Workspace onboarding: Submitted",
            activeCandidate.DisplayName);
        Assert.Null(activeCandidate.EmailHint);
        Assert.Null(activeCandidate.PhoneHint);
        Assert.DoesNotContain(
            "Active Staff",
            activeCandidate.DisplayName,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "active@example.test",
            activeCandidate.DisplayName,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "active-subject",
            activeCandidate.DisplayName,
            StringComparison.Ordinal);

        DataRightsSubjectDiscoveryResult weak =
            await contributor.DiscoverAsync(
                DiscoveryRequest(Email: "artem@example.test"),
                CancellationToken.None);
        DataRightsSubjectDiscoveryResult retained =
            await contributor.DiscoverAsync(
                DiscoveryRequest(
                    AccountSubjectId:
                        $"{WorkspaceStaffRetentionCorrelationReceipt.PseudonymPrefix}" +
                        $"{Guid.NewGuid():N}"),
                CancellationToken.None);
        DataRightsSubjectDiscoveryResult propertyScoped =
            await contributor.DiscoverAsync(
                DiscoveryRequest(
                    AccountSubjectId: SubjectId,
                    PropertyId: PropertyA),
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectDiscoveryStatus.ScopeUnavailable,
            weak.Status);
        Assert.Equal(
            DataRightsSubjectDiscoveryStatus.ScopeUnavailable,
            retained.Status);
        Assert.Equal(
            DataRightsSubjectDiscoveryStatus.ScopeUnavailable,
            propertyScoped.Status);
    }

    [Fact]
    public async Task Staff_record_lookup_finds_linked_history_and_validates_exact_versions()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeededGraph graph = SeedGraph(context);
        await context.SaveChangesAsync();
        WorkspacesDataRightsDiscoveryContributor contributor =
            new(context, new TestScopeContext());

        DataRightsSubjectDiscoveryResult discovered =
            await contributor.DiscoverAsync(
                DiscoveryRequest(RecordId: StaffMemberId),
                CancellationToken.None);

        Assert.Equal(
            [
                WorkspacesDataRightsCoordinates.StaffAccessProcessRecordType,
                WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
                WorkspacesDataRightsCoordinates
                    .StaffRetentionCorrelationReceiptRecordType
            ],
            discovered.Candidates
                .Select(candidate => candidate.Coordinate.RecordType)
                .ToArray());
        DataRightsSubjectCandidate onboarding = Assert.Single(
            discovered.Candidates,
            candidate =>
                candidate.Coordinate.RecordId == graph.Onboarding.Id);
        DataRightsSubjectSelectionValidation valid =
            await contributor.ValidateSelectionAsync(
                SelectionRequest(onboarding.Coordinate),
                CancellationToken.None);
        DataRightsSubjectSelectionValidation stale =
            await contributor.ValidateSelectionAsync(
                SelectionRequest(
                    onboarding.Coordinate with
                    {
                        RecordVersion =
                            onboarding.Coordinate.RecordVersion + 1
                    }),
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectSelectionValidationStatus.Valid,
            valid.Status);
        Assert.Equal(
            DataRightsSubjectSelectionValidationStatus.Stale,
            stale.Status);

        DataRightsSubjectCandidate receipt = Assert.Single(
            discovered.Candidates,
            candidate =>
                candidate.Coordinate.RecordType ==
                WorkspacesDataRightsCoordinates
                    .StaffRetentionCorrelationReceiptRecordType);
        DataRightsSubjectSelectionValidation receiptValid =
            await contributor.ValidateSelectionAsync(
                SelectionRequest(receipt.Coordinate),
                CancellationToken.None);
        Assert.Equal(
            DataRightsSubjectSelectionValidationStatus.Valid,
            receiptValid.Status);
        Assert.Equal(
            WorkspaceStaffRetentionCorrelationReceipt
                .CurrentContractVersion,
            receiptValid.Coordinate!.RecordVersion);
    }

    [Fact]
    public async Task Recovery_receipt_is_discoverable_for_every_personal_actor_and_exports_shaped_proof()
    {
        await using WorkspacesDbContext context = CreateContext();
        Guid applicationId = Guid.Parse(
            "51000000-0000-0000-0000-000000000001");
        Guid sourceId = Guid.Parse(
            "41000000-0000-0000-0000-000000000001");
        Guid receiptId = Guid.Parse(
            "61000000-0000-0000-0000-000000000001");
        const string applicant = "recovery-applicant";
        const string planCreator = "recovery-plan-creator";
        const string recoveryActor = "recovery-operator";
        string digest = new('b', 64);
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                applicationId,
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                applicant,
                "recovery@example.test",
                "Recovery Applicant",
                legalName: null,
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: "Manager",
                department: null,
                Now).Value;
        Assert.True(onboarding.Fail(
            "Workspaces.TestInducedFailure",
            Now.AddSeconds(30)).IsSuccess);
        WorkspaceStaffOnboardingRecoveryReceipt receipt =
            WorkspaceStaffOnboardingRecoveryReceipt.Create(new(
                receiptId,
                TenantId,
                Guid.Parse(
                    "71000000-0000-0000-0000-000000000001"),
                applicationId,
                sourceId,
                StaffMemberId,
                WorkspaceStaffOnboardingRecoveryTriggerKind.ManualOperator,
                Guid.Parse(
                    "81000000-0000-0000-0000-000000000099"),
                applicant,
                planCreator,
                recoveryActor,
                "system:workspace-staff-onboarding",
                digest,
                onboarding.Version,
                onboarding.Version,
                onboarding.Status,
                onboarding.Version,
                onboarding.Status,
                Guid.Parse(
                    "91000000-0000-0000-0000-000000000099"),
                CurrentProfileVersion: 4,
                PropertyCount: 2,
                TargetSetSha256: digest,
                OrganizationRevision: 9,
                MembershipId: Guid.Parse(
                    "a1000000-0000-0000-0000-000000000099"),
                MembershipVersion: 5,
                MembershipStatus: 1,
                StaffTenantRevision: 12,
                StaffMemberVersion: 4,
                StaffStateProofSha256: digest,
                StaffPropertySetSha256: digest,
                AccessManagementRevision: null,
                AccessFactsSha256: null,
                WorkspaceRecoveryVerification.Equivalent,
                WorkspaceRecoveryVerification.ReviewRequired,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                WorkspaceStaffOnboardingRecoveryReason.StaffConflict,
                AccessMutationPerformed: false,
                OrganizationObservedAtUtc: Now.AddMinutes(1),
                StaffObservedAtUtc: Now.AddMinutes(1),
                AccessObservedAtUtc: null,
                RecordedAtUtc: Now.AddMinutes(1))).Value;
        context.AddRange(onboarding, receipt);
        await context.SaveChangesAsync();

        WorkspacesDataRightsDiscoveryContributor discovery =
            new(context, new TestScopeContext());
        foreach (string actor in new[]
                 {
                     applicant,
                     planCreator,
                     recoveryActor
                 })
        {
            DataRightsSubjectDiscoveryResult result =
                await discovery.DiscoverAsync(
                    DiscoveryRequest(AccountSubjectId: actor),
                    CancellationToken.None);
            Assert.Contains(
                result.Candidates,
                candidate => candidate.Coordinate.RecordType ==
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecoveryReceiptRecordType &&
                    candidate.Coordinate.RecordId == receiptId);
        }

        WorkspacesDataRightsExportContributor exporter =
            CreateExportContributor(context);
        CollectingSink sink = new();
        DataRightsSubjectExportResult exported = await exporter.ExportAsync(
            ExportRequest(
                WorkspacesDataRightsCoordinates
                    .StaffOnboardingRecoveryReceiptRecordType,
                receiptId,
                receipt.ContractVersion),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, exported.Status);
        DataRightsExportRecord record = Assert.Single(sink.Records);
        Assert.Equal(
            JsonValueKind.Null,
            Field(
                record,
                "workspaces.onboarding-recovery.applicant-subject-id")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            Field(
                record,
                "workspaces.onboarding-recovery.plan-creator-actor-id")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            Field(
                record,
                "workspaces.onboarding-recovery.recovery-actor-id")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            Field(
                record,
                "workspaces.onboarding-recovery.effective-system-actor-id")
                .ValueKind);
        Assert.Equal(
            digest,
            Field(
                record,
                "workspaces.onboarding-recovery.target-set-sha256")
                .GetString());
        Assert.DoesNotContain(
            record.Fields,
            field => field.FieldId.Contains(
                "property-id",
                StringComparison.Ordinal) ||
                field.FieldId.Contains(
                "permission",
                StringComparison.Ordinal));

        CollectingSink applicantSink = new();
        DataRightsSubjectExportResult applicantExport =
            await exporter.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    onboarding.Id,
                    onboarding.Version),
                applicantSink,
                CancellationToken.None);
        Assert.Equal(
            DataRightsSubjectExportStatus.Succeeded,
            applicantExport.Status);
        Assert.Equal(
            [
                WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
                WorkspacesDataRightsCoordinates
                    .StaffOnboardingRecoveryReceiptRecordType
            ],
            applicantSink.Records
                .Select(candidate => candidate.RecordType)
                .ToArray());
        DataRightsExportRecord applicantReceipt =
            applicantSink.Records[1];
        Assert.Equal(
            applicant,
            Field(
                applicantReceipt,
                "workspaces.onboarding-recovery.applicant-subject-id")
                .GetString());
        Assert.Equal(
            JsonValueKind.Null,
            Field(
                applicantReceipt,
                "workspaces.onboarding-recovery.plan-creator-actor-id")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            Field(
                applicantReceipt,
                "workspaces.onboarding-recovery.recovery-actor-id")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            Field(
                applicantReceipt,
                "workspaces.onboarding-recovery.effective-system-actor-id")
                .ValueKind);
    }

    [Fact]
    public async Task Export_writes_each_selected_owner_shape_in_deterministic_order()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeededGraph graph = SeedGraph(context);
        await context.SaveChangesAsync();
        WorkspacesDataRightsExportContributor contributor =
            CreateExportContributor(
                context,
                OutcomeReaderFor(graph.Onboarding));

        CollectingSink onboardingSink = new();
        DataRightsSubjectExportResult onboarding =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    graph.Onboarding.Id,
                    graph.Onboarding.Version),
                onboardingSink,
                CancellationToken.None);
        Assert.Equal(
            DataRightsSubjectExportStatus.Succeeded,
            onboarding.Status);
        Assert.Equal(5, onboarding.RecordCount);
        Assert.Equal(
            [
                WorkspacesDataRightsCoordinates
                    .StaffOnboardingRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingCorrectionReceiptRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingProcessingRestrictionRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingProcessingRestrictionReceiptRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingProcessingRestrictionReceiptRecordType
            ],
            onboardingSink.Records
                .Select(record => record.RecordType)
                .ToArray());
        Assert.Equal(
            JsonValueKind.Null,
            Field(
                onboardingSink.Records[0],
                "workspaces.proposed-display-name").ValueKind);
        Assert.Equal(
            SubjectId,
            Field(
                onboardingSink.Records[0],
                "workspaces.auth-subject-id").GetString());
        Assert.Equal(
            graph.Onboarding.IdentityAnchorExpectedResolutionEventId,
            Field(
                onboardingSink.Records[0],
                "workspaces.identity-anchor.expected-resolution-event-id")
            .GetGuid());
        Assert.Equal(
            graph.Onboarding.IdentityAnchorContinuationEventId,
            Field(
                onboardingSink.Records[0],
                "workspaces.identity-anchor.continuation-event-id")
            .GetGuid());
        Assert.Equal(
            graph.Onboarding.IdentityAnchorResolutionEventId,
            Field(
                onboardingSink.Records[0],
                "workspaces.identity-anchor.resolution-event-id")
            .GetGuid());
        Assert.Equal(
            graph.Onboarding.IdentityAnchorResolutionStaffMemberId,
            Field(
                onboardingSink.Records[0],
                "workspaces.identity-anchor.resolution-staff-member-id")
            .GetGuid());
        Assert.Equal(
            graph.Onboarding.IdentityAnchorResolutionApplicationVersion,
            Field(
                onboardingSink.Records[0],
                "workspaces.identity-anchor.resolution-application-version")
            .GetInt64());
        Assert.Equal(
            "completed-redacted",
            Field(
                onboardingSink.Records[0],
                "workspaces.identity-anchor.resolution-disposition")
            .GetString());
        Assert.Equal(
            graph.Onboarding.IdentityAnchorResolutionIntentAtUtc,
            Field(
                onboardingSink.Records[0],
                "workspaces.identity-anchor.resolution-intent-at-utc")
            .GetDateTimeOffset());
        Assert.Equal(
            graph.Onboarding.IdentityAnchorResolutionObservedAtUtc,
            Field(
                onboardingSink.Records[0],
                "workspaces.identity-anchor.resolution-observed-at-utc")
            .GetDateTimeOffset());
        Assert.Equal(
            graph.Onboarding.IdentityAnchorSweepOrdinal,
            Field(
                onboardingSink.Records[0],
                "workspaces.identity-anchor.sweep-ordinal")
            .GetInt64());
        Assert.Equal(
            graph.CorrectionReceipt.ExecutionId,
            Field(
                onboardingSink.Records[1],
                "workspaces.data-rights.execution-id").GetGuid());
        Assert.DoesNotContain(
            onboardingSink.Records[1].Fields,
            field =>
                field.FieldId ==
                "workspaces.data-rights.request-fingerprint");
        Assert.Equal(
            graph.Restriction.Id,
            Field(
                onboardingSink.Records[2],
                "workspaces.onboarding-restriction.id").GetGuid());
        Assert.Equal(
            [
                "apply",
                "release"
            ],
            onboardingSink.Records
                .Skip(3)
                .Select(record =>
                    Field(
                        record,
                        "workspaces.onboarding-restriction.action")
                    .GetString()!)
                .ToArray());
        Assert.DoesNotContain(
            onboardingSink.Records.Skip(2).SelectMany(record => record.Fields),
            field =>
                field.Value.GetRawText().Contains(
                    "user:privacy",
                    StringComparison.Ordinal) ||
                field.FieldId.Contains(
                    "fingerprint",
                    StringComparison.OrdinalIgnoreCase) ||
                field.FieldId.Contains(
                    "digest",
                    StringComparison.OrdinalIgnoreCase));

        CollectingSink processSink = new();
        DataRightsSubjectExportResult process =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffAccessProcessRecordType,
                    graph.Process.Id,
                    graph.Process.Version),
                processSink,
                CancellationToken.None);
        Assert.Equal(3, process.RecordCount);
        Assert.Equal(
            "not-applicable",
            Field(
                processSink.Records[0],
                "workspaces.access-process-restoration-disposition")
            .GetString());
        Assert.Equal(
            [
                WorkspacesDataRightsCoordinates
                    .StaffAccessProcessRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffAccessProfileSnapshotRecordType,
                WorkspacesDataRightsExportContributor
                    .StaffAccessProfileSnapshotRecordType
            ],
            processSink.Records
                .Select(record => record.RecordType)
                .ToArray());
        CollectingSink processReplaySink = new();
        DataRightsSubjectExportResult processReplay =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffAccessProcessRecordType,
                    graph.Process.Id,
                    graph.Process.Version),
                processReplaySink,
                CancellationToken.None);
        Assert.Equal(process.RecordCount, processReplay.RecordCount);
        Assert.Equal(
            processSink.Records
                .Skip(1)
                .Select(record => record.RecordId)
                .ToArray(),
            processReplaySink.Records
                .Skip(1)
                .Select(record => record.RecordId)
                .ToArray());
        Assert.All(
            processSink.Records.Skip(1),
            record => Assert.Equal(
                graph.Process.Version,
                record.RecordVersion));

        CollectingSink planSink = new();
        DataRightsSubjectExportResult plan =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffAccessPlanRecordType,
                    graph.Plan.Id,
                    graph.Plan.Version),
                planSink,
                CancellationToken.None);
        Assert.Equal(3, plan.RecordCount);
        Assert.Equal(
            [PropertyA, PropertyB],
            planSink.Records
                .Skip(1)
                .Select(record =>
                    Field(
                        record,
                        "workspaces.property-assignment-id")
                    .GetGuid())
                .ToArray());

        CollectingSink receiptSink = new();
        DataRightsSubjectExportResult receipt =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffRetentionCorrelationReceiptRecordType,
                    graph.Receipt.Id,
                    graph.Receipt.ContractVersion),
                receiptSink,
                CancellationToken.None);
        Assert.Equal(1, receipt.RecordCount);
        Assert.Equal(
            graph.Receipt.CanonicalSha256,
            Field(
                receiptSink.Records[0],
                "workspaces.retention-correlation-proof")
            .GetProperty("canonicalSha256")
            .GetString());

        Assert.Equal(
            "workspaces.personal-data",
            contributor.Descriptor.CatalogId);
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.CatalogVersion,
            contributor.Descriptor.CatalogVersion);
        Assert.Equal(
            WorkspacesDataRightsExportSchema.ExportSchemaId,
            contributor.Descriptor.ExportSchemaId);
    }

    [Fact]
    public async Task Export_rejects_stale_missing_and_cross_scope_coordinates_without_writing()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeededGraph graph = SeedGraph(context);
        await context.SaveChangesAsync();
        WorkspacesDataRightsExportContributor contributor =
            CreateExportContributor(
                context,
                OutcomeReaderFor(graph.Onboarding));
        CollectingSink sink = new();

        DataRightsSubjectExportResult stale =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    graph.Onboarding.Id,
                    graph.Onboarding.Version + 1),
                sink,
                CancellationToken.None);
        DataRightsSubjectExportResult missing =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    Guid.NewGuid(),
                    1),
                sink,
                CancellationToken.None);
        DataRightsSubjectExportResult propertyScoped =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    graph.Onboarding.Id,
                    graph.Onboarding.Version,
                    PropertyA),
                sink,
                CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Stale, stale.Status);
        Assert.Equal(
            DataRightsSubjectExportStatus.NotFound,
            missing.Status);
        Assert.Equal(
            DataRightsSubjectExportStatus.ScopeUnavailable,
            propertyScoped.Status);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Onboarding_export_discloses_staged_profile_only_for_exact_absence()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                Guid.NewGuid(),
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                Guid.NewGuid(),
                SubjectId,
                "verified@example.test",
                "Ada Operator",
                "Ada Example",
                "work@example.test",
                "+44 20 5555 0100",
                "E-42",
                "Manager",
                "Operations",
                Now).Value;
        context.StaffOnboardingApplications.Add(onboarding);
        await context.SaveChangesAsync();
        WorkspacesDataRightsExportContributor contributor =
            CreateExportContributor(context);
        CollectingSink sink = new();

        DataRightsSubjectExportResult result =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    onboarding.Id,
                    onboarding.Version),
                sink,
                CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        DataRightsExportRecord record = Assert.Single(sink.Records);
        Assert.Equal(
            "Ada Operator",
            Field(record, "workspaces.proposed-display-name").GetString());
        Assert.Equal(
            "verified@example.test",
            Field(record, "workspaces.verified-account-email").GetString());
    }

    [Fact]
    public async Task Onboarding_export_accepts_observed_resolution_after_local_subject_pseudonymisation()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeededGraph graph = SeedGraph(context);
        await context.SaveChangesAsync();
        string pseudonym = "workspaces-erased:subject-pseudonym";
        context.Entry(graph.Onboarding)
            .Property(application => application.SubjectId)
            .CurrentValue = pseudonym;
        context.Entry(graph.Onboarding)
            .Property(application => application.Version)
            .CurrentValue = graph.Onboarding.Version + 1;
        context.Entry(graph.Onboarding)
            .Property(application => application.LastChangedAtUtc)
            .CurrentValue = Now.AddMinutes(5);
        await context.SaveChangesAsync();
        WorkspacesDataRightsExportContributor contributor =
            CreateExportContributor(
                context,
                OutcomeReaderFor(
                    graph.Onboarding,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                        .Mismatch));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    graph.Onboarding.Id,
                    graph.Onboarding.Version),
                sink,
                CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        DataRightsExportRecord record = Assert.Single(
            sink.Records,
            candidate => candidate.RecordType ==
                WorkspacesDataRightsCoordinates.StaffOnboardingRecordType);
        Assert.Equal(
            pseudonym,
            Field(record, "workspaces.auth-subject-id").GetString());
        Assert.Equal(
            graph.Onboarding.IdentityAnchorResolutionEventId,
            Field(
                record,
                "workspaces.identity-anchor.resolution-event-id").GetGuid());
    }

    [Theory]
    [InlineData(StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved)]
    [InlineData(StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Corrupt)]
    [InlineData(StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved)]
    public async Task Onboarding_export_fails_closed_before_writing_for_non_authoritative_anchor_state(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus status)
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                Guid.NewGuid(),
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                Guid.NewGuid(),
                SubjectId,
                "verified@example.test",
                "Ada Operator",
                legalName: null,
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                Now).Value;
        context.StaffOnboardingApplications.Add(onboarding);
        await context.SaveChangesAsync();
        Guid anchorTarget = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader reader = new(
            request => new(
                request.ApplicationId,
                status,
                anchorTarget,
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
        WorkspacesDataRightsExportContributor contributor =
            CreateExportContributor(context, reader);
        CollectingSink sink = new();

        DataRightsSubjectExportResult result =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    onboarding.Id,
                    onboarding.Version),
                sink,
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectExportStatus.ScopeUnavailable,
            result.Status);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Onboarding_export_rejects_excess_receipts_before_writing()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                Guid.NewGuid(),
                TenantId,
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                Guid.NewGuid(),
                SubjectId,
                "verified@example.test",
                "Ada Operator",
                legalName: null,
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                Now).Value;
        context.StaffOnboardingApplications.Add(onboarding);
        for (int index = 0;
             index <=
             WorkspacesDataRightsExportContributor.MaximumChildRecords;
             index++)
        {
            context.StaffOnboardingCorrectionReceipts.Add(
                WorkspaceStaffOnboardingCorrectionReceipt.Create(
                    Guid.NewGuid(),
                    TenantId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    approvalRevision: 1,
                    onboarding.Id,
                    selectedRecordVersion: 1,
                    currentRecordVersion: 2,
                    [
                        WorkspaceStaffOnboardingApplicantField
                            .DisplayName
                    ],
                    new string('a', 64),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Now.AddTicks(index)).Value);
        }

        await context.SaveChangesAsync();
        WorkspacesDataRightsExportContributor contributor =
            CreateExportContributor(context);
        CollectingSink sink = new();

        DataRightsSubjectExportResult result =
            await contributor.ExportAsync(
                ExportRequest(
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    onboarding.Id,
                    onboarding.Version),
                sink,
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectExportStatus.ScopeUnavailable,
            result.Status);
        Assert.Empty(sink.Records);
    }

    private static SeededGraph SeedGraph(
        WorkspacesDbContext context)
    {
        Guid sourceId =
            Guid.Parse("40000000-0000-0000-0000-000000000001");
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                Guid.Parse(
                    "50000000-0000-0000-0000-000000000001"),
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                SubjectId,
                "artem@example.test",
                "Artem",
                "Artem Prokudanov",
                "artem@example.test",
                "+44 20 5555 0123",
                "E-42",
                "Manager",
                "Operations",
                Now).Value;
        Assert.True(
            onboarding.ObserveInvitationAccepted(
                Now.AddMinutes(1)).IsSuccess);
        Assert.True(
            onboarding.MarkStaffReady(
                StaffMemberId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now.AddMinutes(2)).IsSuccess);
        Assert.True(
            onboarding.Complete(
                Now.AddMinutes(3)).IsSuccess);
        Assert.True(
            onboarding.ObserveResolution(
                onboarding.IdentityAnchorResolutionEventId!.Value,
                StaffMemberId,
                onboarding.IdentityAnchorResolutionApplicationVersion!.Value,
                onboarding.IdentityAnchorResolutionDisposition!.Value,
                Now.AddMinutes(4)).IsSuccess);

        WorkspaceStaffAccessProcess process =
            WorkspaceStaffAccessProcess.Create(
                Guid.Parse(
                    "60000000-0000-0000-0000-000000000001"),
                TenantId,
                StaffMemberId,
                SubjectId,
                WorkspaceStaffAccessTargetState.Departed,
                targetStaffVersion: 2,
                new DateOnly(2026, 7, 30),
                SubjectId,
                [
                    new WorkspaceStaffAccessProfileTarget(
                        Guid.Parse(
                            "70000000-0000-0000-0000-000000000002"),
                        $"property:{PropertyB:N}"),
                    new WorkspaceStaffAccessProfileTarget(
                        Guid.Parse(
                            "70000000-0000-0000-0000-000000000001"),
                        $"property:{PropertyA:N}")
                ],
                Now).Value;
        Assert.True(
            process.MarkAwaitingStaffCommit(
                Now.AddMinutes(1)).IsSuccess);
        Assert.True(
            process.ObserveStaffCommit(
                Now.AddMinutes(2)).IsSuccess);

        WorkspaceStaffAccessPlan plan =
            WorkspaceStaffAccessPlan.Create(
                sourceId,
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                Guid.Parse(
                    "70000000-0000-0000-0000-000000000003"),
                "workspace-manager",
                [PropertyB, PropertyA],
                SubjectId,
                Now).Value;
        Assert.True(plan.Activate(Now.AddMinutes(1)).IsSuccess);

        WorkspaceStaffRetentionCorrelationReceipt receipt =
            WorkspaceStaffRetentionCorrelationReceipt.Create(
                Guid.Parse(
                    "80000000-0000-0000-0000-000000000001"),
                TenantId,
                Guid.Parse(
                    "90000000-0000-0000-0000-000000000001"),
                StaffMemberId,
                selectedStaffVersion: 2,
                onboardingRecordsScrubbed: 1,
                accessProcessRecordsScrubbed: 1,
                accessPlanRecordsScrubbed: 1,
                Now.AddDays(1)).Value;
        WorkspaceStaffOnboardingCorrectionReceipt correctionReceipt =
            WorkspaceStaffOnboardingCorrectionReceipt.Create(
                Guid.Parse(
                    "81000000-0000-0000-0000-000000000001"),
                TenantId,
                Guid.Parse(
                    "91000000-0000-0000-0000-000000000001"),
                Guid.Parse(
                    "92000000-0000-0000-0000-000000000001"),
                approvalRevision: 2,
                onboarding.Id,
                selectedRecordVersion: 1,
                currentRecordVersion: 2,
                [
                    WorkspaceStaffOnboardingApplicantField
                        .DisplayName
                ],
                new string('a', 64),
                Guid.Parse(
                    "93000000-0000-0000-0000-000000000001"),
                Guid.Parse(
                    "94000000-0000-0000-0000-000000000001"),
                Now.AddSeconds(30)).Value;
        Guid applyCaseId =
            Guid.Parse("95000000-0000-0000-0000-000000000001");
        Guid releaseCaseId =
            Guid.Parse("95000000-0000-0000-0000-000000000002");
        WorkspaceStaffOnboardingProcessingRestriction restriction =
            WorkspaceStaffOnboardingProcessingRestriction.Create(
                Guid.Parse(
                    "82000000-0000-0000-0000-000000000001"),
                TenantId,
                onboarding.Id,
                applyCaseId,
                applyApprovalRevision: 3,
                onboarding.Version,
                "user:privacy",
                Now.AddMinutes(4)).Value;
        Assert.True(restriction.Release(
            releaseCaseId,
            releaseApprovalRevision: 4,
            onboarding.Version,
            expectedVersion: 1,
            "user:privacy",
            Now.AddMinutes(5)).IsSuccess);
        WorkspaceStaffOnboardingProcessingRestrictionReceipt applyReceipt =
            WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                Guid.Parse(
                    "83000000-0000-0000-0000-000000000001"),
                TenantId,
                Guid.Parse(
                    "96000000-0000-0000-0000-000000000001"),
                restriction.Id,
                WorkspaceStaffOnboardingProcessingRestrictionAction.Apply,
                onboarding.Id,
                applyCaseId,
                approvalRevision: 3,
                onboarding.Version,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                resultingRestrictionVersion: 1,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                "user:privacy",
                Guid.Parse(
                    "97000000-0000-0000-0000-000000000001"),
                Now.AddMinutes(4)).Value;
        WorkspaceStaffOnboardingProcessingRestrictionReceipt releaseReceipt =
            WorkspaceStaffOnboardingProcessingRestrictionReceipt.Create(
                Guid.Parse(
                    "83000000-0000-0000-0000-000000000002"),
                TenantId,
                Guid.Parse(
                    "96000000-0000-0000-0000-000000000002"),
                restriction.Id,
                WorkspaceStaffOnboardingProcessingRestrictionAction.Release,
                onboarding.Id,
                releaseCaseId,
                approvalRevision: 4,
                onboarding.Version,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                resultingRestrictionVersion: 2,
                resultingProjectionRevision: 2,
                effectiveRestricted: false,
                "user:privacy",
                Guid.Parse(
                    "97000000-0000-0000-0000-000000000002"),
                Now.AddMinutes(5)).Value;

        context.StaffOnboardingApplications.Add(onboarding);
        context.StaffAccessProcesses.Add(process);
        context.StaffAccessPlans.Add(plan);
        context.StaffRetentionCorrelationReceipts.Add(receipt);
        context.StaffOnboardingCorrectionReceipts.Add(
            correctionReceipt);
        context.StaffOnboardingProcessingRestrictions.Add(
            restriction);
        context.StaffOnboardingProcessingRestrictionReceipts.AddRange(
            applyReceipt,
            releaseReceipt);
        return new SeededGraph(
            onboarding,
            process,
            plan,
            receipt,
            correctionReceipt,
            restriction);
    }

    private static DataRightsSubjectDiscoveryRequest DiscoveryRequest(
        Guid? RecordId = null,
        string? Email = null,
        string? AccountSubjectId = null,
        Guid? PropertyId = null) =>
        new(
            TenantId,
            DataRightsCaseType.StaffRights,
            PropertyId,
            new DataRightsSubjectLookup(
                RecordId,
                Email,
                Phone: null,
                Name: null,
                DateOfBirth: null,
                AccountSubjectId),
            DataRightsSubjectDiscoveryLimits.MaxCandidates);

    private static DataRightsSubjectSelectionRequest SelectionRequest(
        DataRightsSubjectCoordinate coordinate) =>
        new(
            TenantId,
            DataRightsCaseType.StaffRights,
            PropertyId: null,
            coordinate);

    private static DataRightsSubjectExportRequest ExportRequest(
        string recordType,
        Guid recordId,
        long version,
        Guid? propertyId = null) =>
        new(
            TenantId,
            DataRightsCaseType.StaffRights,
            propertyId,
            new DataRightsSubjectCoordinate(
                WorkspacesDataRightsCoordinates.Owner,
                recordType,
                recordId,
                version));

    private static JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(
            record.Fields,
            field => field.FieldId == fieldId).Value;

    private static WorkspacesDbContext CreateContext()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new(options, new TestScopeContext());
    }

    private static WorkspacesDataRightsExportContributor
        CreateExportContributor(
            WorkspacesDbContext context,
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader? reader =
                null) => new(
            context,
            new TestScopeContext(),
            new WorkspaceStaffOnboardingSerializedReadBoundary(context),
            new WorkspaceStaffOnboardingOperationLock(context),
            new WorkspaceStaffOnboardingRepository(context),
            reader ?? new StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(),
            NullLogger<WorkspacesDataRightsExportContributor>.Instance);

    private static StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
        OutcomeReaderFor(
            WorkspaceStaffOnboarding application,
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch subjectMatch =
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact) =>
        new(request =>
            request.ApplicationId == application.Id &&
            application.IdentityAnchorResolutionObservedAtUtc.HasValue
                ? new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                    application.Id,
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved,
                    application.StaffMemberId,
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                    subjectMatch,
                    application.IdentityAnchorResolutionApplicationVersion,
                    ToStaffDisposition(
                        application.IdentityAnchorResolutionDisposition!.Value),
                    application.IdentityAnchorResolutionEventId)
                : StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
                    .Absent(request));

    private static StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
        ToStaffDisposition(DomainResolutionDisposition disposition) =>
        disposition switch
        {
            DomainResolutionDisposition.CompletedRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .CompletedRedacted,
            DomainResolutionDisposition.RejectedRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .RejectedRedacted,
            DomainResolutionDisposition.SupersededRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .SupersededRedacted,
            DomainResolutionDisposition.ExpiredRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .ExpiredRedacted,
            DomainResolutionDisposition.WithdrawnRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .WithdrawnRedacted,
            _ => throw new InvalidOperationException(
                "The test resolution disposition is invalid.")
        };

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

    private sealed record SeededGraph(
        WorkspaceStaffOnboarding Onboarding,
        WorkspaceStaffAccessProcess Process,
        WorkspaceStaffAccessPlan Plan,
        WorkspaceStaffRetentionCorrelationReceipt Receipt,
        WorkspaceStaffOnboardingCorrectionReceipt CorrectionReceipt,
        WorkspaceStaffOnboardingProcessingRestriction Restriction);
}

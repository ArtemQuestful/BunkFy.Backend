namespace BunkFy.Extensions.Operations.Notifications.Tests;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed class OperationsNotificationsStaffDataRightsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly string ScopeId = TenantId.ToString("D");
    private static readonly Guid StaffMemberId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid PropertyId =
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Theory]
    [InlineData(
        NotificationHistoryReferenceStatus.Missing,
        "59d846f9695b706747d28866592a8a51bac74751cdf3105e00cb4d762c94ae34")]
    [InlineData(
        NotificationHistoryReferenceStatus.Open,
        "bb172b8a7e3021604ebb79558dfc962c2d8858e58f6a69a213677ddfd3498320")]
    [InlineData(
        NotificationHistoryReferenceStatus.Closed,
        "93cc5fc92a0a7235aaf8b8f13cf7fdb29ef94acf69de1bb05f5d70a66eea9beb")]
    public void Snapshot_hash_preserves_all_v1_status_codes(
        NotificationHistoryReferenceStatus status,
        string expectedSha256)
    {
        NotificationHistoryReference reference = new(
            "staff",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        NotificationHistoryReferenceSnapshot snapshot = new(
            status,
            3,
            2,
            9);

        Assert.Equal(
            expectedSha256,
            OperationsNotificationsStaffHistoryPolicyEvidence
                .ComputeSnapshotSha256(reference, snapshot));
    }

    [Fact]
    public void Snapshot_hash_rejects_statuses_without_a_v1_evidence_code()
    {
        NotificationHistoryReference reference = new(
            "staff",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        NotificationHistoryReferenceSnapshot snapshot = new(
            NotificationHistoryReferenceStatus.Unknown,
            3,
            2,
            9);

        Assert.Throws<InvalidOperationException>(() =>
            OperationsNotificationsStaffHistoryPolicyEvidence
                .ComputeSnapshotSha256(reference, snapshot));
    }

    [Fact]
    public async Task Policy_binds_open_history_to_exact_departed_staff_authority()
    {
        NotificationHistoryReference reference = StaffReference();
        var snapshot = new NotificationHistoryReferenceSnapshot(
            NotificationHistoryReferenceStatus.Open,
            3,
            2,
            9);
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, actual, _) =>
            {
                Assert.Equal(reference, actual);
                return Task.FromResult(snapshot);
            }
        };
        var authority = new TestStaffAuthorityReader(
            new StaffDataRightsAuthorityState(
                StaffMemberId,
                7,
                StaffDataRightsAuthorityRecordState.Departed));
        var contributor =
            new OperationsNotificationsStaffAnonymisationPolicyContributor(
                lifecycle,
                authority);

        DataRightsAnonymisationPolicyContributionResult result =
            await contributor.EvaluateAsync(
                new DataRightsAnonymisationPolicyContributionRequest(
                    DataRightsAnonymisationPolicyContract.CurrentVersion,
                    ScopeId,
                    DataRightsCaseType.StaffRights,
                    PropertyId: null,
                    Guid.NewGuid(),
                    StaffCoordinate(snapshot.Version)),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationPolicyContributionStatus.Approved,
            result.Status);
        Assert.Equal(
            DataRightsAnonymisationPolicyContributionRole.Companion,
            result.Role);
        Assert.Equal(
            new DataRightsSubjectCoordinate(
                StaffDataRightsCoordinates.Owner,
                StaffDataRightsCoordinates.StaffMemberRecordType,
                StaffMemberId,
                7),
            result.AuthorityCoordinate);
        DataRightsApprovalEvidenceBinding binding =
            Assert.Single(result.StateBindings);
        Assert.Equal(
            OperationsNotificationsDataRightsCoordinates
                .StaffHistoryStateBindingKey,
            binding.Key);
        Assert.Equal(snapshot.Version, binding.Version);
        Assert.Equal(
            OperationsNotificationsStaffHistoryPolicyEvidence
                .ComputeSnapshotSha256(reference, snapshot),
            binding.Sha256);
    }

    [Fact]
    public async Task Policy_denies_history_when_staff_is_not_departed()
    {
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _, _) => Task.FromResult(OpenSnapshot())
        };
        var contributor =
            new OperationsNotificationsStaffAnonymisationPolicyContributor(
                lifecycle,
                new TestStaffAuthorityReader(
                    new StaffDataRightsAuthorityState(
                        StaffMemberId,
                        7,
                        StaffDataRightsAuthorityRecordState.Active)));

        DataRightsAnonymisationPolicyContributionResult result =
            await contributor.EvaluateAsync(
                new DataRightsAnonymisationPolicyContributionRequest(
                    DataRightsAnonymisationPolicyContract.CurrentVersion,
                    ScopeId,
                    DataRightsCaseType.StaffRights,
                    PropertyId: null,
                    Guid.NewGuid(),
                    StaffCoordinate(3)),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationPolicyContributionStatus.Denied,
            result.Status);
        Assert.Equal(0, lifecycle.SnapshotCalls);
    }

    [Fact]
    public async Task Execution_prerequisite_rejects_history_changed_after_approval()
    {
        NotificationHistoryReference reference = StaffReference();
        NotificationHistoryReferenceSnapshot approved = OpenSnapshot();
        var changed = approved with
        {
            RecordCount = approved.RecordCount + 1,
            LatestStreamSequence =
                approved.LatestStreamSequence + 1
        };
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _, _) => Task.FromResult(changed)
        };
        var prerequisite =
            new OperationsNotificationsStaffAnonymisationPrerequisite(
                lifecycle,
                new TestScopeContext(),
                NullLogger<
                    OperationsNotificationsStaffAnonymisationPrerequisite>
                    .Instance);

        DataRightsAnonymisationExecutionPrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                AnonymisationRequest(
                    Guid.NewGuid(),
                    approved.Version,
                    ApprovalEvidence(reference, approved)),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationExecutionPrerequisiteStatus.Blocked,
            result.Status);
    }

    [Fact]
    public async Task Execution_prerequisite_accepts_exact_closed_replay_state()
    {
        NotificationHistoryReferenceSnapshot approved = OpenSnapshot();
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _, _) => Task.FromResult(
                new NotificationHistoryReferenceSnapshot(
                    NotificationHistoryReferenceStatus.Closed,
                    approved.Version + 1,
                    0,
                    0))
        };
        var prerequisite =
            new OperationsNotificationsStaffAnonymisationPrerequisite(
                lifecycle,
                new TestScopeContext(),
                NullLogger<
                    OperationsNotificationsStaffAnonymisationPrerequisite>
                    .Instance);

        DataRightsAnonymisationExecutionPrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                AnonymisationRequest(
                    Guid.NewGuid(),
                    approved.Version,
                    ApprovalEvidence(StaffReference(), approved)),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationExecutionPrerequisiteStatus.Completed,
            result.Status);
    }

    [Theory]
    [InlineData(NotificationHistoryReferenceStatus.Open, 3)]
    [InlineData(NotificationHistoryReferenceStatus.Closed, 4)]
    public async Task Restore_prerequisite_accepts_exact_pre_or_post_close_state(
        NotificationHistoryReferenceStatus status,
        long version)
    {
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _, _) => Task.FromResult(
                new NotificationHistoryReferenceSnapshot(
                    status,
                    version,
                    0,
                    0))
        };
        var prerequisite =
            new OperationsNotificationsStaffAnonymisationPrerequisite(
                lifecycle,
                new TestScopeContext(),
                NullLogger<
                    OperationsNotificationsStaffAnonymisationPrerequisite>
                    .Instance);

        DataRightsAnonymisationRestorePrerequisiteResult result =
            await prerequisite.ExecuteAsync(
                RestoreRequest(
                    Guid.NewGuid(),
                    new string('d', 64),
                    resultingVersion: 4),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationRestorePrerequisiteStatus.Completed,
            result.Status);
    }

    [Fact]
    public async Task Anonymisation_closes_exact_history_with_generic_ceiling()
    {
        Guid workItemId = Guid.NewGuid();
        NotificationHistoryReference reference = StaffReference();
        NotificationHistoryReferenceSnapshot approved = OpenSnapshot();
        NotificationHistoryReferenceCloseReceipt receipt =
            CreateCloseReceipt(
                workItemId,
                reference,
                approved.Version + 1);
        var lifecycle = new TestLifecycle
        {
            Close = (request, _) =>
            {
                Assert.Equal(workItemId, request.OperationId);
                Assert.Equal(reference, request.Reference);
                Assert.Equal(
                    NotificationHistoryLifecycleLimits
                        .MaximumCloseRecords,
                    request.MaximumRecords);
                return Task.FromResult(
                    new NotificationHistoryReferenceCloseResult(
                        NotificationHistoryReferenceCloseStatus.Completed,
                        receipt));
            }
        };
        var contributor =
            new OperationsNotificationsStaffAnonymisationContributor(
                lifecycle,
                new TestScopeContext(),
                new FixedClock());

        DataRightsAnonymisationContributionResult result =
            await contributor.ExecuteAsync(
                AnonymisationRequest(
                    workItemId,
                    approved.Version,
                    ApprovalEvidence(reference, approved)),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationContributionStatus.Completed,
            result.Status);
        Assert.Equal(
            OperationsNotificationsDataRightsReceipt.StaffReasonCode,
            result.OwnerProof!.ReasonCode);
        Assert.Equal(
            OperationsNotificationsDataRightsReceipt.ComputeSha256(
                receipt),
            result.OwnerProof.ReceiptSha256);
    }

    [Fact]
    public async Task Anonymisation_blocks_when_staff_history_exceeds_the_generic_ceiling()
    {
        NotificationHistoryReferenceSnapshot approved = OpenSnapshot();
        var lifecycle = new TestLifecycle
        {
            Close = (_, _) => Task.FromResult(
                new NotificationHistoryReferenceCloseResult(
                    NotificationHistoryReferenceCloseStatus.Overflow,
                    null))
        };
        var contributor =
            new OperationsNotificationsStaffAnonymisationContributor(
                lifecycle,
                new TestScopeContext(),
                new FixedClock());

        DataRightsAnonymisationContributionResult result =
            await contributor.ExecuteAsync(
                AnonymisationRequest(
                    Guid.NewGuid(),
                    approved.Version,
                    ApprovalEvidence(StaffReference(), approved)),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationContributionStatus.Blocked,
            result.Status);
        Assert.Null(result.OwnerProof);
    }

    [Fact]
    public async Task Anonymisation_retries_when_delivery_holds_the_staff_history_lease()
    {
        NotificationHistoryReferenceSnapshot approved = OpenSnapshot();
        var lifecycle = new TestLifecycle
        {
            Close = (_, _) => Task.FromResult(
                new NotificationHistoryReferenceCloseResult(
                    NotificationHistoryReferenceCloseStatus.Busy,
                    null))
        };
        var contributor =
            new OperationsNotificationsStaffAnonymisationContributor(
                lifecycle,
                new TestScopeContext(),
                new FixedClock());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            contributor.ExecuteAsync(
                AnonymisationRequest(
                    Guid.NewGuid(),
                    approved.Version,
                    ApprovalEvidence(StaffReference(), approved)),
                CancellationToken.None));
    }

    [Fact]
    public async Task Restore_replays_the_exact_owner_receipt()
    {
        Guid operationId = Guid.NewGuid();
        NotificationHistoryReference reference = StaffReference();
        NotificationHistoryReferenceCloseReceipt receipt =
            CreateCloseReceipt(operationId, reference, 4);
        string receiptSha256 =
            OperationsNotificationsDataRightsReceipt.ComputeSha256(
                receipt);
        var lifecycle = new TestLifecycle
        {
            Close = (request, _) =>
            {
                Assert.Equal(operationId, request.OperationId);
                Assert.Equal(3, request.ExpectedVersion);
                Assert.Equal(
                    NotificationHistoryLifecycleLimits
                        .MaximumCloseRecords,
                    request.MaximumRecords);
                return Task.FromResult(
                    new NotificationHistoryReferenceCloseResult(
                        NotificationHistoryReferenceCloseStatus.Replayed,
                        receipt));
            }
        };
        var contributor =
            new OperationsNotificationsStaffAnonymisationRestoreContributor(
                lifecycle,
                new TestScopeContext(),
                new FixedClock());

        DataRightsAnonymisationRestoreResult result =
            await contributor.RestoreAsync(
                RestoreRequest(
                    operationId,
                    receiptSha256,
                    resultingVersion: 4),
                CancellationToken.None);

        Assert.Equal(
            DataRightsAnonymisationRestoreStatus.Completed,
            result.Status);
        Assert.Equal(receiptSha256, result.Proof!.OwnerReceiptSha256);
        Assert.Equal(4, result.Proof.ResultingRecordVersion);
    }

    [Fact]
    public async Task Staff_export_is_stable_and_excludes_recipient_identity()
    {
        NotificationHistoryReferenceRecord record =
            CreateNotificationRecord(sequence: 7);
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _, _) => Task.FromResult(
                new NotificationHistoryReferenceSnapshot(
                    NotificationHistoryReferenceStatus.Open,
                    3,
                    1,
                    7)),
            Page = (_, _, _, pageSize, _) =>
            {
                Assert.Equal(
                    NotificationHistoryLifecycleLimits.MaximumPageSize,
                    pageSize);
                return Task.FromResult(
                    new NotificationHistoryReferencePage(
                        NotificationHistoryReferenceStatus.Open,
                        3,
                        [record],
                        7,
                        false));
            }
        };
        var sink = new CapturingSink();
        var contributor =
            new OperationsNotificationsStaffDataRightsExportContributor(
                lifecycle,
                new TestScopeContext());

        DataRightsSubjectExportResult result =
            await contributor.ExportAsync(
                new DataRightsSubjectExportRequest(
                    ScopeId,
                    DataRightsCaseType.StaffRights,
                    PropertyId: null,
                    StaffCoordinate(3)),
                sink,
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectExportStatus.Succeeded,
            result.Status);
        DataRightsExportRecord exported = Assert.Single(sink.Records);
        Assert.Equal(13, exported.Fields.Count);
        Assert.All(
            exported.Fields,
            field => Assert.StartsWith(
                "operations-notifications.staff-export-",
                field.FieldId,
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            "auth-subject-must-not-export",
            JsonSerializer.Serialize(exported),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        DataRightsResponseDeadlineNotificationHandler
            .DueSoonNotificationName)]
    [InlineData(
        DataRightsResponseDeadlineNotificationHandler
            .OverdueNotificationName)]
    public async Task Staff_export_accepts_minimal_data_rights_deadline_payload(
        string notificationName)
    {
        Guid caseId = Guid.NewGuid();
        NotificationHistoryReferenceRecord record =
            CreateNotificationRecord(sequence: 7) with
            {
                SourceModule = DataRightsModuleMetadata.Name,
                NotificationName = notificationName,
                Payload = JsonSerializer.SerializeToElement(
                    new DataRightsDeadlineNotificationPayload(
                        PropertyId,
                        caseId)),
                DeliveryPolicy = NotificationDeliveryPolicy.Mandatory
            };
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _, _) => Task.FromResult(
                new NotificationHistoryReferenceSnapshot(
                    NotificationHistoryReferenceStatus.Open,
                    3,
                    1,
                    7)),
            Page = (_, _, _, _, _) => Task.FromResult(
                new NotificationHistoryReferencePage(
                    NotificationHistoryReferenceStatus.Open,
                    3,
                    [record],
                    7,
                    false))
        };
        var sink = new CapturingSink();
        var contributor =
            new OperationsNotificationsStaffDataRightsExportContributor(
                lifecycle,
                new TestScopeContext());

        DataRightsSubjectExportResult result =
            await contributor.ExportAsync(
                new DataRightsSubjectExportRequest(
                    ScopeId,
                    DataRightsCaseType.StaffRights,
                    PropertyId: null,
                    StaffCoordinate(3)),
                sink,
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectExportStatus.Succeeded,
            result.Status);
        DataRightsExportRecord exported = Assert.Single(sink.Records);
        DataRightsExportField notificationNameField = Assert.Single(
            exported.Fields,
            field => field.FieldId ==
                "operations-notifications.staff-export-notification-name");
        Assert.Equal(
            notificationName,
            notificationNameField.Value.GetString());
        Assert.Contains(
            caseId.ToString("D"),
            JsonSerializer.Serialize(exported),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Staff_export_rejects_unknown_payload_without_partial_output()
    {
        NotificationHistoryReferenceRecord valid =
            CreateNotificationRecord(sequence: 7);
        NotificationHistoryReferenceRecord unknown =
            CreateNotificationRecord(sequence: 8) with
            {
                Payload = JsonSerializer.SerializeToElement(
                    new
                    {
                        PropertyId,
                        Unexpected = "not-catalogued"
                    })
            };
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _, _) => Task.FromResult(
                new NotificationHistoryReferenceSnapshot(
                    NotificationHistoryReferenceStatus.Open,
                    3,
                    2,
                    8)),
            Page = (_, _, _, _, _) => Task.FromResult(
                new NotificationHistoryReferencePage(
                    NotificationHistoryReferenceStatus.Open,
                    3,
                    [valid, unknown],
                    8,
                    false))
        };
        var sink = new CapturingSink();
        var contributor =
            new OperationsNotificationsStaffDataRightsExportContributor(
                lifecycle,
                new TestScopeContext());

        DataRightsSubjectExportResult result =
            await contributor.ExportAsync(
                new DataRightsSubjectExportRequest(
                    ScopeId,
                    DataRightsCaseType.StaffRights,
                    PropertyId: null,
                    StaffCoordinate(3)),
                sink,
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectExportStatus.ScopeUnavailable,
            result.Status);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Staff_export_rejects_unknown_notification_contract_without_partial_output()
    {
        NotificationHistoryReferenceRecord valid =
            CreateNotificationRecord(sequence: 7);
        NotificationHistoryReferenceRecord unknown =
            CreateNotificationRecord(sequence: 8) with
            {
                NotificationName = "future-property-notification"
            };
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _, _) => Task.FromResult(
                new NotificationHistoryReferenceSnapshot(
                    NotificationHistoryReferenceStatus.Open,
                    3,
                    2,
                    8)),
            Page = (_, _, _, _, _) => Task.FromResult(
                new NotificationHistoryReferencePage(
                    NotificationHistoryReferenceStatus.Open,
                    3,
                    [valid, unknown],
                    8,
                    false))
        };
        var sink = new CapturingSink();
        var contributor =
            new OperationsNotificationsStaffDataRightsExportContributor(
                lifecycle,
                new TestScopeContext());

        DataRightsSubjectExportResult result =
            await contributor.ExportAsync(
                new DataRightsSubjectExportRequest(
                    ScopeId,
                    DataRightsCaseType.StaffRights,
                    PropertyId: null,
                    StaffCoordinate(3)),
                sink,
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectExportStatus.ScopeUnavailable,
            result.Status);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Staff_selection_validation_is_tenant_scoped_and_exact()
    {
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, reference, _) => Task.FromResult(
                reference == StaffReference()
                    ? OpenSnapshot()
                    : new NotificationHistoryReferenceSnapshot(
                        NotificationHistoryReferenceStatus.Missing,
                        0,
                        0,
                        0))
        };
        var contributor =
            new OperationsNotificationsDataRightsDiscoveryContributor(
                lifecycle,
                new TestScopeContext(),
                NullLogger<
                    OperationsNotificationsDataRightsDiscoveryContributor>
                    .Instance);

        DataRightsSubjectSelectionValidation valid =
            await contributor.ValidateSelectionAsync(
                new DataRightsSubjectSelectionRequest(
                    ScopeId,
                    DataRightsCaseType.StaffRights,
                    PropertyId: null,
                    StaffCoordinate(3)),
                CancellationToken.None);
        DataRightsSubjectSelectionValidation propertyScoped =
            await contributor.ValidateSelectionAsync(
                new DataRightsSubjectSelectionRequest(
                    ScopeId,
                    DataRightsCaseType.StaffRights,
                    PropertyId,
                    StaffCoordinate(3)),
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectSelectionValidationStatus.Valid,
            valid.Status);
        Assert.Equal(
            DataRightsSubjectSelectionValidationStatus.NotFound,
            propertyScoped.Status);
    }

    [Fact]
    public void Dependency_injection_registers_each_staff_owner_contract_once()
    {
        ServiceCollection services = [];

        services.AddBunkFyOperationsNotifications();

        Assert.Single(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IDataRightsAnonymisationPolicyContributor));
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IDataRightsAnonymisationExecutionPrerequisiteV2));
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IDataRightsAnonymisationRestorePrerequisiteV3));
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IDataRightsAnonymisationContributorV2));
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IDataRightsAnonymisationRestoreContributorV3));
        Assert.Equal(
            2,
            services.Count(descriptor => descriptor.ServiceType ==
                typeof(IDataRightsSubjectExportContributor)));
    }

    private static NotificationHistoryReference StaffReference() =>
        OperationsNotificationsDataRightsCoordinates.ForStaff(
            ScopeId,
            StaffMemberId);

    private static DataRightsSubjectCoordinate StaffCoordinate(
        long version) =>
        new(
            OperationsNotificationsDataRightsCoordinates.Owner,
            OperationsNotificationsDataRightsCoordinates
                .StaffInboxHistoryRecordType,
            StaffMemberId,
            version);

    private static NotificationHistoryReferenceSnapshot OpenSnapshot() =>
        new(
            NotificationHistoryReferenceStatus.Open,
            3,
            2,
            9);

    private static DataRightsApprovalEvidence ApprovalEvidence(
        NotificationHistoryReference reference,
        NotificationHistoryReferenceSnapshot snapshot) =>
        new(
            2,
            PropertyId: null,
            PropertyVersion: 0,
            "US",
            "staff-rights-policy",
            1,
            "staff-retention-policy",
            1,
            new string('a', 64),
            "data-rights-anonymisation",
            "erasure",
            "authorized-workspace-operator",
            Now,
            true,
            CaseType: DataRightsCaseType.StaffRights,
            ScopeKind: DataRightsExecutionScopeKind.Tenant,
            RetentionDataClass: "staff-operational-history",
            RetentionTrigger: "employment-departed",
            RetentionTriggeredAtUtc: Now.AddDays(-1),
            RetentionDeadlineUtc: Now.AddMinutes(-1),
            StateBindings:
            [
                new(
                    "staff.governance",
                    1,
                    new string('c', 64)),
                new(
                    "staff.holds",
                    0,
                    new string('d', 64)),
                new(
                    "staff.record",
                    7,
                    new string('e', 64)),
                new(
                    "staff.restriction",
                    1,
                    new string('f', 64)),
                OperationsNotificationsStaffHistoryPolicyEvidence
                    .CreateBinding(reference, snapshot)
            ],
            StateBindingsSha256: new string('b', 64));

    private static DataRightsAnonymisationContributionRequestV2
        AnonymisationRequest(
            Guid workItemId,
            long version,
            DataRightsApprovalEvidence approvalEvidence) =>
        new(
            DataRightsAnonymisationContractV2.CurrentVersion,
            ScopeId,
            DataRightsCaseType.StaffRights,
            DataRightsExecutionScopeKind.Tenant,
            PropertyId: null,
            workItemId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            1,
            StaffCoordinate(version),
            approvalEvidence,
            "user:executor",
            Now.AddMinutes(5));

    private static DataRightsAnonymisationRestoreRequestV3
        RestoreRequest(
            Guid operationId,
            string ownerReceiptSha256,
            long resultingVersion) =>
        new(
            DataRightsAnonymisationRestoreContractV3.CurrentVersion,
            ScopeId,
            Guid.NewGuid(),
            1,
            new string('c', 64),
            DataRightsCaseType.StaffRights,
            DataRightsExecutionScopeKind.Tenant,
            RoutingPropertyId: null,
            OperationsNotificationsDataRightsCoordinates.Owner,
            OperationsNotificationsDataRightsCoordinates
                .StaffInboxHistoryRecordType,
            StaffMemberId,
            OperationsNotificationsDataRightsReceipt.ContractVersion,
            operationId,
            ownerReceiptSha256,
            resultingVersion,
            Now);

    private static NotificationHistoryReferenceCloseReceipt
        CreateCloseReceipt(
            Guid operationId,
            NotificationHistoryReference reference,
            long resultingVersion) =>
        new(
            operationId,
            reference,
            resultingVersion,
            2,
            new string('d', 64),
            Now);

    private static NotificationHistoryReferenceRecord
        CreateNotificationRecord(long sequence) =>
        new(
            Guid.NewGuid(),
            "auth-subject-must-not-export",
            PropertiesModuleMetadata.Name,
            "property-retired",
            1,
            "Property retired",
            "A property was retired.",
            NotificationSeverity.Warning,
            sequence,
            Now.AddMinutes(-2),
            Now.AddMinutes(-1),
            JsonSerializer.SerializeToElement(
                new PropertyNotificationPayload(PropertyId)),
            ["domain:properties", "web"],
            NotificationDeliveryPolicy.RespectPreferences);

    private sealed class TestStaffAuthorityReader(
        StaffDataRightsAuthorityState? state)
        : IStaffDataRightsAuthorityReader
    {
        public Task<StaffDataRightsAuthorityState?> ReadAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ScopeId, tenantId);
            Assert.Equal(StaffMemberId, staffMemberId);
            return Task.FromResult(state);
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId =>
            OperationsNotificationsStaffDataRightsTests.ScopeId;
    }

    private sealed class FixedClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class CapturingSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestLifecycle : INotificationHistoryLifecycle
    {
        public Func<
            string,
            NotificationHistoryReference,
            CancellationToken,
            Task<NotificationHistoryReferenceSnapshot>> Snapshot
        { get; init; } =
            (_, _, _) => Task.FromResult(
                new NotificationHistoryReferenceSnapshot(
                    NotificationHistoryReferenceStatus.Missing,
                    0,
                    0,
                    0));

        public Func<
            string,
            NotificationHistoryReference,
            long,
            int,
            CancellationToken,
            Task<NotificationHistoryReferencePage>> Page
        { get; init; } =
            (_, _, cursor, _, _) => Task.FromResult(
                new NotificationHistoryReferencePage(
                    NotificationHistoryReferenceStatus.Missing,
                    0,
                    [],
                    cursor,
                    false));

        public Func<
            NotificationHistoryReferenceCloseRequest,
            CancellationToken,
            Task<NotificationHistoryReferenceCloseResult>> Close
        { get; init; } =
            (_, _) => Task.FromResult(
                new NotificationHistoryReferenceCloseResult(
                    NotificationHistoryReferenceCloseStatus.Invalid,
                    null));

        public int SnapshotCalls { get; private set; }

        public Task<NotificationHistoryReferenceSnapshot> EnsureOpenAsync(
            string scopeId,
            NotificationHistoryReference reference,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new NotificationHistoryReferenceSnapshot(
                    NotificationHistoryReferenceStatus.Missing,
                    0,
                    0,
                    0));

        public Task<NotificationHistoryReferenceSnapshot> GetSnapshotAsync(
            string scopeId,
            NotificationHistoryReference reference,
            CancellationToken cancellationToken)
        {
            this.SnapshotCalls++;
            return this.Snapshot(
                scopeId,
                reference,
                cancellationToken);
        }

        public Task<NotificationHistoryReferencePage> ListAsync(
            string scopeId,
            NotificationHistoryReference reference,
            long afterStreamSequence,
            int pageSize,
            CancellationToken cancellationToken) =>
            this.Page(
                scopeId,
                reference,
                afterStreamSequence,
                pageSize,
                cancellationToken);

        public Task<NotificationHistoryReferenceCloseResult> CloseAsync(
            NotificationHistoryReferenceCloseRequest request,
            CancellationToken cancellationToken) =>
            this.Close(request, cancellationToken);

        public Task<NotificationHistoryReferenceCloseBatchResult>
            CloseBatchAsync(
                NotificationHistoryReferenceCloseBatchRequest request,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                new NotificationHistoryReferenceCloseBatchResult(
                    NotificationHistoryReferenceCloseBatchStatus.Invalid,
                    null,
                    null));
    }
}

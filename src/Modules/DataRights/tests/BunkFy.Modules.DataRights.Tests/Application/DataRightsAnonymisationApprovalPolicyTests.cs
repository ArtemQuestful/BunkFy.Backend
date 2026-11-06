namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Policies;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;
using SelectedSubject =
    BunkFy.Modules.DataRights.Domain.Entities.DataRightsSubjectCoordinate;

[Trait("Category", "Unit")]
public sealed class DataRightsAnonymisationApprovalPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Active_enabled_property_mints_frozen_policy_evidence()
    {
        Guid propertyId = Guid.NewGuid();
        (CountryPolicyRegistry registry, PropertyGovernancePolicyBinding binding) =
            CreatePolicy();
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(CreateSnapshot(binding)),
            [],
            registry,
            new TestClock());

        Result<DataRightsApprovalPolicyEvidence> result = await policy.EvaluateAsync(
            "tenant-a",
            DataRightsCaseScope.ForProperty(propertyId),
            Guid.NewGuid(),
            [],
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(propertyId, result.Value.PropertyId);
        Assert.Equal(7, result.Value.PropertyVersion);
        Assert.Equal(binding.PolicyId, result.Value.PolicyId);
        Assert.Equal(binding.ContentSha256, result.Value.ContentSha256);
        Assert.Equal(
            DataRightsAnonymisationApprovalPolicy.PurposeCode,
            result.Value.PurposeCode);
        Assert.True(result.Value.RequiresDistinctExecutor);

        static DataRightsPropertyPolicySnapshot CreateSnapshot(
            PropertyGovernancePolicyBinding governancePolicy) => new(
            true,
            PropertyStatus.Active,
            "Europe/London",
            PropertyProcessingStatus.Enabled,
            governancePolicy,
            7,
            7);
    }

    [Theory]
    [InlineData(false, PropertyStatus.Active, PropertyProcessingStatus.Enabled)]
    [InlineData(true, PropertyStatus.Retired, PropertyProcessingStatus.Enabled)]
    [InlineData(true, PropertyStatus.Active, PropertyProcessingStatus.Suspended)]
    public async Task Missing_inactive_or_suspended_property_fails_closed(
        bool isKnown,
        PropertyStatus status,
        PropertyProcessingStatus processingStatus)
    {
        (CountryPolicyRegistry registry, PropertyGovernancePolicyBinding binding) =
            CreatePolicy();
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(new(
                isKnown,
                status,
                "Europe/London",
                processingStatus,
                binding,
                7,
                7)),
            [],
            registry,
            new TestClock());

        Result<DataRightsApprovalPolicyEvidence> result = await policy.EvaluateAsync(
            "tenant-a",
            DataRightsCaseScope.ForProperty(Guid.NewGuid()),
            Guid.NewGuid(),
            [],
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors.AnonymisationApprovalPolicyDenied.Code,
            result.Error.Code);
    }

    [Fact]
    public async Task Policy_registry_mismatch_fails_closed()
    {
        (_, PropertyGovernancePolicyBinding binding) = CreatePolicy();
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(new(
                true,
                PropertyStatus.Active,
                "Europe/London",
                PropertyProcessingStatus.Enabled,
                binding,
                7,
                7)),
            [],
            CountryPolicyRegistry.Create([], [], CountryPolicyRuntimeMode.Production),
            new TestClock());

        Result<DataRightsApprovalPolicyEvidence> result = await policy.EvaluateAsync(
            "tenant-a",
            DataRightsCaseScope.ForProperty(Guid.NewGuid()),
            Guid.NewGuid(),
            [],
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors.AnonymisationApprovalPolicyDenied.Code,
            result.Error.Code);
    }

    [Fact]
    public async Task Staff_owner_policy_mints_tenant_scoped_bound_evidence()
    {
        Guid staffMemberId = Guid.NewGuid();
        SelectedSubject subject = SelectedSubject.Create(
            "staff",
            "staff-member",
            staffMemberId,
            12,
            "user:selector",
            Now.AddMinutes(-2)).Value;
        StubPolicyContributor contributor = new(
            ApprovedStaffContribution());
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(property: null),
            [contributor],
            CountryPolicyRegistry.Create(
                [],
                [],
                CountryPolicyRuntimeMode.Engineering),
            new TestClock());

        Result<DataRightsApprovalPolicyEvidence> result =
            await policy.EvaluateAsync(
                "tenant-a",
                DataRightsCaseScope.Staff,
                Guid.NewGuid(),
                [subject],
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            DataRightsApprovalPolicyEvidence.CurrentSchemaVersion,
            result.Value.SchemaVersion);
        Assert.Equal(DataRightsCaseKind.StaffRights, result.Value.CaseKind);
        Assert.Equal(
            DataRightsCaseScopeKind.Tenant,
            result.Value.ScopeKind);
        Assert.Null(result.Value.PropertyId);
        Assert.Equal(0, result.Value.PropertyVersion);
        Assert.Equal(
            ["staff.governance", "staff.record"],
            result.Value.StateBindings.Select(binding => binding.Key));
        Assert.True(result.Value.HasValidShape());
        Assert.Equal(1, contributor.EvaluationCount);
    }

    [Fact]
    public async Task Staff_authority_and_companion_freeze_combined_owner_bindings()
    {
        SelectedSubject staff = SelectedSubject.Create(
            "staff",
            "staff-member",
            Guid.NewGuid(),
            12,
            "user:selector",
            Now.AddMinutes(-2)).Value;
        SelectedSubject workspace = SelectedSubject.Create(
            "workspaces",
            "staff-access-process",
            Guid.NewGuid(),
            4,
            "user:selector",
            Now.AddMinutes(-1)).Value;
        StubPolicyContributor authority = new(
            ApprovedStaffContribution());
        StubPolicyContributor companion = new(
            DataRightsAnonymisationPolicyContributionResult
                .ApprovedCompanion(
                    new(
                        staff.OwnerKey,
                        staff.RecordType,
                        staff.RecordId,
                        staff.RecordVersion),
                    [
                        new(
                            "workspaces.staff-correlation",
                            workspace.RecordVersion,
                            new string('d', 64))
                    ]),
            workspace.OwnerKey,
            workspace.RecordType);
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(property: null),
            [companion, authority],
            CountryPolicyRegistry.Create(
                [],
                [],
                CountryPolicyRuntimeMode.Engineering),
            new TestClock());

        Result<DataRightsApprovalPolicyEvidence> result =
            await policy.EvaluateAsync(
                "tenant-a",
                DataRightsCaseScope.Staff,
                Guid.NewGuid(),
                [workspace, staff],
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                "staff.governance",
                "staff.record",
                "workspaces.staff-correlation"
            ],
            result.Value.StateBindings.Select(binding => binding.Key));
        Assert.True(result.Value.HasValidShape());
        Assert.Equal(1, authority.EvaluationCount);
        Assert.Equal(1, companion.EvaluationCount);
    }

    [Fact]
    public async Task Normal_policy_owner_return_after_cancellation_stops_later_owners()
    {
        SelectedSubject staff = SelectedSubject.Create(
            "staff",
            "staff-member",
            Guid.NewGuid(),
            12,
            "user:selector",
            Now.AddMinutes(-2)).Value;
        SelectedSubject workspace = SelectedSubject.Create(
            "workspaces",
            "staff-access-process",
            Guid.NewGuid(),
            4,
            "user:selector",
            Now.AddMinutes(-1)).Value;
        using CancellationTokenSource source = new();
        StubPolicyContributor authority = new(
            ApprovedStaffContribution(),
            afterEvaluate: _ => source.Cancel());
        StubPolicyContributor companion = new(
            DataRightsAnonymisationPolicyContributionResult.ApprovedCompanion(
                new(
                    staff.OwnerKey,
                    staff.RecordType,
                    staff.RecordId,
                    staff.RecordVersion),
                [
                    new(
                        "workspaces.staff-correlation",
                        workspace.RecordVersion,
                        new string('d', 64))
                ]),
            workspace.OwnerKey,
            workspace.RecordType);
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(property: null),
            [companion, authority],
            CountryPolicyRegistry.Create(
                [],
                [],
                CountryPolicyRuntimeMode.Engineering),
            new TestClock());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.EvaluateAsync(
                "tenant-a",
                DataRightsCaseScope.Staff,
                Guid.NewGuid(),
                [workspace, staff],
                source.Token));

        Assert.Equal(1, authority.EvaluationCount);
        Assert.Equal(0, companion.EvaluationCount);
    }

    [Fact]
    public async Task Companion_for_unselected_authority_fails_closed()
    {
        SelectedSubject staff = SelectedSubject.Create(
            "staff",
            "staff-member",
            Guid.NewGuid(),
            12,
            "user:selector",
            Now.AddMinutes(-2)).Value;
        SelectedSubject workspace = SelectedSubject.Create(
            "workspaces",
            "staff-access-process",
            Guid.NewGuid(),
            4,
            "user:selector",
            Now.AddMinutes(-1)).Value;
        StubPolicyContributor authority = new(
            ApprovedStaffContribution());
        StubPolicyContributor companion = new(
            DataRightsAnonymisationPolicyContributionResult
                .ApprovedCompanion(
                    new(
                        staff.OwnerKey,
                        staff.RecordType,
                        Guid.NewGuid(),
                        staff.RecordVersion),
                    [
                        new(
                            "workspaces.staff-correlation",
                            workspace.RecordVersion,
                            new string('d', 64))
                    ]),
            workspace.OwnerKey,
            workspace.RecordType);
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(property: null),
            [authority, companion],
            CountryPolicyRegistry.Create(
                [],
                [],
                CountryPolicyRuntimeMode.Engineering),
            new TestClock());

        Result<DataRightsApprovalPolicyEvidence> result =
            await policy.EvaluateAsync(
                "tenant-a",
                DataRightsCaseScope.Staff,
                Guid.NewGuid(),
                [staff, workspace],
                CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors
                .AnonymisationApprovalPolicyDenied,
            result.Error);
    }

    [Fact]
    public async Task Duplicate_staff_policy_contributors_fail_closed()
    {
        SelectedSubject subject = SelectedSubject.Create(
            "staff",
            "staff-member",
            Guid.NewGuid(),
            12,
            "user:selector",
            Now.AddMinutes(-2)).Value;
        StubPolicyContributor first = new(ApprovedStaffContribution());
        StubPolicyContributor second = new(ApprovedStaffContribution());
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(property: null),
            [first, second],
            CountryPolicyRegistry.Create(
                [],
                [],
                CountryPolicyRuntimeMode.Engineering),
            new TestClock());

        Result<DataRightsApprovalPolicyEvidence> result =
            await policy.EvaluateAsync(
                "tenant-a",
                DataRightsCaseScope.Staff,
                Guid.NewGuid(),
                [subject],
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors.AnonymisationApprovalPolicyDenied.Code,
            result.Error.Code);
        Assert.Equal(0, first.EvaluationCount);
        Assert.Equal(0, second.EvaluationCount);
    }

    [Fact]
    public async Task Malformed_staff_policy_contribution_fails_closed()
    {
        DataRightsAnonymisationPolicyContributionResult approved =
            ApprovedStaffContribution();
        StubPolicyContributor contributor = new(
            approved with
            {
                Evidence = approved.Evidence! with
                {
                    StateBindings = null!
                }
            });
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(property: null),
            [contributor],
            CountryPolicyRegistry.Create(
                [],
                [],
                CountryPolicyRuntimeMode.Engineering),
            new TestClock());
        SelectedSubject subject = SelectedSubject.Create(
            "staff",
            "staff-member",
            Guid.NewGuid(),
            12,
            "user:selector",
            Now.AddMinutes(-2)).Value;

        Result<DataRightsApprovalPolicyEvidence> result =
            await policy.EvaluateAsync(
                "tenant-a",
                DataRightsCaseScope.Staff,
                Guid.NewGuid(),
                [subject],
                CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.AnonymisationApprovalPolicyDenied.Code,
            result.Error.Code);
    }

    [Fact]
    public async Task Null_owner_binding_fails_closed_without_throwing()
    {
        DataRightsAnonymisationPolicyContributionResult approved =
            ApprovedStaffContribution();
        StubPolicyContributor contributor = new(
            approved with
            {
                Evidence = approved.Evidence! with
                {
                    StateBindings = [null!]
                }
            });
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(property: null),
            [contributor],
            CountryPolicyRegistry.Create(
                [],
                [],
                CountryPolicyRuntimeMode.Engineering),
            new TestClock());
        SelectedSubject subject = SelectedSubject.Create(
            "staff",
            "staff-member",
            Guid.NewGuid(),
            12,
            "user:selector",
            Now.AddMinutes(-2)).Value;

        Result<DataRightsApprovalPolicyEvidence> result =
            await policy.EvaluateAsync(
                "tenant-a",
                DataRightsCaseScope.Staff,
                Guid.NewGuid(),
                [subject],
                CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors
                .AnonymisationApprovalPolicyDenied,
            result.Error);
    }

    [Fact]
    public async Task Contributor_exception_fails_closed()
    {
        StubPolicyContributor contributor = new(
            ApprovedStaffContribution(),
            exception: new InvalidOperationException("unavailable"));
        DataRightsAnonymisationApprovalPolicy policy = new(
            new StubPropertyRepository(property: null),
            [contributor],
            CountryPolicyRegistry.Create(
                [],
                [],
                CountryPolicyRuntimeMode.Engineering),
            new TestClock());
        SelectedSubject subject = SelectedSubject.Create(
            "staff",
            "staff-member",
            Guid.NewGuid(),
            12,
            "user:selector",
            Now.AddMinutes(-2)).Value;

        Result<DataRightsApprovalPolicyEvidence> result =
            await policy.EvaluateAsync(
                "tenant-a",
                DataRightsCaseScope.Staff,
                Guid.NewGuid(),
                [subject],
                CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors
                .AnonymisationApprovalPolicyDenied,
            result.Error);
    }

    private static DataRightsAnonymisationPolicyContributionResult
        ApprovedStaffContribution() =>
        DataRightsAnonymisationPolicyContributionResult.Approved(
            new(
                "GB",
                "development-example-hostel",
                1,
                "development-staff-employment",
                1,
                new string('a', 64),
                "staff-data-rights-anonymisation",
                "erasure",
                "authorized-workspace-operator",
                "staff-employment",
                "employment-ended",
                Now.AddDays(-8),
                Now.AddDays(-1),
                Now,
                [
                    new("staff.record", 12, new string('b', 64)),
                    new("staff.governance", 3, new string('c', 64))
                ],
                RequiresDistinctExecutor: true));

    private static (
        CountryPolicyRegistry Registry,
        PropertyGovernancePolicyBinding Binding) CreatePolicy()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "CountryPolicies",
            "example-hostel-policy.v1.json"));
        CountryPolicyPackArtifact artifact = CountryPolicyPackJson.Parse(bytes);
        CountryPolicyPackDocument document = artifact.Document;
        const string propertyRetentionPolicyId =
            "development-guest-operational";
        (string Id, int Version) retentionPolicy = document.RetentionRules
            .Where(rule =>
                string.Equals(
                    rule.RetentionPolicyId,
                    propertyRetentionPolicyId,
                    StringComparison.Ordinal))
            .Select(rule => (
                Id: rule.RetentionPolicyId,
                Version: rule.RetentionPolicyVersion))
            .Distinct()
            .Single();
        CountryPolicyRegistry registry = CountryPolicyRegistry.Create(
            [artifact],
            [new(
                document.OperatingCountryCode,
                document.PolicyId,
                document.PolicyVersion,
                artifact.ContentSha256,
                CountryLaunchStatus.Engineering)],
            CountryPolicyRuntimeMode.Engineering);
        CountryPolicyDecision activation = registry.EvaluateActivation(new(
            document.OperatingCountryCode,
            document.PolicyId,
            document.PolicyVersion,
            document.PermittedDataRegions.Single(),
            document.PermittedTransferProfiles.Single(),
            retentionPolicy.Id,
            retentionPolicy.Version,
            document.RequiredAcknowledgements.Select(acknowledgement =>
                new CountryPolicyAcknowledgement(
                    acknowledgement.AcknowledgementId,
                    acknowledgement.AcknowledgementVersion)).ToArray(),
            DataRightsAnonymisationApprovalPolicy.AccommodationType,
            "property-activation",
            DataRightsAnonymisationApprovalPolicy.SourceProvenance,
            Now));
        CountryPolicyEvidence evidence = Assert.IsType<CountryPolicyEvidence>(
            activation.Evidence);
        return (
            registry,
            new(
                evidence.OperatingCountryCode,
                evidence.PolicyId,
                evidence.PolicyVersion,
                evidence.DataRegionId,
                evidence.TransferProfileId,
                evidence.RetentionPolicyId,
                evidence.RetentionPolicyVersion,
                evidence.ContentSha256,
                evidence.EffectiveAtUtc,
                evidence.ExpiresAtUtc,
                evidence.EvaluatedAtUtc,
                evidence.AcceptedAcknowledgements.Select(acknowledgement =>
                    new PropertyGovernanceAcknowledgement(
                        acknowledgement.AcknowledgementId,
                        acknowledgement.AcknowledgementVersion)).ToArray()));
    }

    private sealed class StubPropertyRepository(DataRightsPropertyPolicySnapshot? property)
        : IDataRightsPropertyProjectionRepository
    {
        public Task ApplyTopologyAsync(
            DataRightsPropertyTopologyWriteModel propertyWriteModel,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ApplyPolicyAsync(
            DataRightsPropertyPolicyWriteModel propertyWriteModel,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DataRightsPropertyPolicySnapshot?> GetPolicyAsync(
            Guid propertyId,
            CancellationToken cancellationToken) => Task.FromResult(property);
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubPolicyContributor(
        DataRightsAnonymisationPolicyContributionResult result,
        string ownerKey = "staff",
        string recordType = "staff-member",
        Exception? exception = null,
        Action<CancellationToken>? afterEvaluate = null)
        : IDataRightsAnonymisationPolicyContributor
    {
        public int ContractVersion =>
            DataRightsAnonymisationPolicyContract.CurrentVersion;

        public DataRightsCaseType CaseType =>
            DataRightsCaseType.StaffRights;

        public string OwnerKey => ownerKey;

        public string RecordType => recordType;

        public int EvaluationCount { get; private set; }

        public Task<DataRightsAnonymisationPolicyContributionResult>
            EvaluateAsync(
                DataRightsAnonymisationPolicyContributionRequest request,
                CancellationToken cancellationToken)
        {
            this.EvaluationCount++;
            if (exception is not null)
            {
                throw exception;
            }

            afterEvaluate?.Invoke(cancellationToken);
            return Task.FromResult(result);
        }
    }
}

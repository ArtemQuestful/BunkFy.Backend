namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Policies;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestDataRightsResponseDeadlinePolicyTests
{
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 1, 31, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Real_v2_policy_freezes_the_earliest_requested_right_and_projection_versions()
    {
        (CountryPolicyRegistry registry, PropertyGovernancePolicyBinding binding) =
            LoadPolicy("example-hostel-policy.v2.json");
        GuestDataRightsResponseDeadlinePolicy policy = new(
            new StubPropertyRepository(new(
                true,
                PropertyStatus.Active,
                "Europe/London",
                PropertyProcessingStatus.Enabled,
                binding,
                17,
                19)),
            registry);

        Result<DataRightsResponseDeadlinePolicyEvidence> result =
            await policy.ResolveGuestAsync(
                Guid.Parse("c1000000-0000-0000-0000-000000000001"),
                DataRightsCaseOperation.AccessExport |
                    DataRightsCaseOperation.Correction,
                ReceivedAt,
                ReceivedAt.AddMinutes(2),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DataRightsResponseRight.Export, result.Value.ControllingRight);
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.Zero), result.Value.DueAtUtc);
        Assert.Equal(17, result.Value.PropertyTopologySourceVersion);
        Assert.Equal(19, result.Value.PropertyPolicySourceVersion);
        Assert.Equal(2, result.Value.PolicyVersion);
        Assert.Equal("Europe/London", result.Value.TimeZoneId);
        Assert.True(result.Value.HasValidShape());
    }

    [Fact]
    public async Task Legacy_v1_policy_cannot_authorize_a_response_deadline()
    {
        (CountryPolicyRegistry registry, PropertyGovernancePolicyBinding binding) =
            LoadPolicy("example-hostel-policy.v1.json");
        GuestDataRightsResponseDeadlinePolicy policy = new(
            new StubPropertyRepository(new(
                true,
                PropertyStatus.Active,
                "Europe/London",
                PropertyProcessingStatus.Enabled,
                binding,
                17,
                19)),
            registry);

        Result<DataRightsResponseDeadlinePolicyEvidence> result =
            await policy.ResolveGuestAsync(
                Guid.NewGuid(),
                DataRightsCaseOperation.AccessExport,
                ReceivedAt,
                ReceivedAt,
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors.ResponseDeadlinePolicyUnavailable,
            result.Error);
    }

    [Theory]
    [InlineData(PropertyStatus.Unknown, PropertyProcessingStatus.Enabled, "Europe/London")]
    [InlineData(PropertyStatus.Active, PropertyProcessingStatus.Unconfigured, "Europe/London")]
    [InlineData(PropertyStatus.Active, PropertyProcessingStatus.Enabled, null)]
    public async Task Incomplete_property_governance_fails_closed(
        PropertyStatus status,
        PropertyProcessingStatus processingStatus,
        string? timeZoneId)
    {
        (CountryPolicyRegistry registry, PropertyGovernancePolicyBinding binding) =
            LoadPolicy("example-hostel-policy.v2.json");
        GuestDataRightsResponseDeadlinePolicy policy = new(
            new StubPropertyRepository(new(
                true,
                status,
                timeZoneId,
                processingStatus,
                processingStatus == PropertyProcessingStatus.Unconfigured
                    ? null
                    : binding,
                17,
                19)),
            registry);

        Result<DataRightsResponseDeadlinePolicyEvidence> result =
            await policy.ResolveGuestAsync(
                Guid.NewGuid(),
                DataRightsCaseOperation.AccessExport,
                ReceivedAt,
                ReceivedAt,
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            DataRightsApplicationErrors.ResponseDeadlinePolicyUnavailable,
            result.Error);
    }

    [Fact]
    public async Task Retired_suspended_property_remains_eligible_for_subject_rights_handling()
    {
        (CountryPolicyRegistry registry, PropertyGovernancePolicyBinding binding) =
            LoadPolicy("example-hostel-policy.v2.json");
        GuestDataRightsResponseDeadlinePolicy policy = new(
            new StubPropertyRepository(new(
                true,
                PropertyStatus.Retired,
                "Europe/London",
                PropertyProcessingStatus.Suspended,
                binding,
                17,
                19)),
            registry);

        Result<DataRightsResponseDeadlinePolicyEvidence> result =
            await policy.ResolveGuestAsync(
                Guid.NewGuid(),
                DataRightsCaseOperation.Erasure,
                ReceivedAt,
                ReceivedAt,
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DataRightsResponseRight.Erasure, result.Value.ControllingRight);
    }

    [Fact]
    public async Task Normal_projection_return_after_cancellation_is_not_accepted()
    {
        (CountryPolicyRegistry registry, PropertyGovernancePolicyBinding binding) =
            LoadPolicy("example-hostel-policy.v2.json");
        using CancellationTokenSource source = new();
        GuestDataRightsResponseDeadlinePolicy policy = new(
            new StubPropertyRepository(
                new(
                    true,
                    PropertyStatus.Active,
                    "Europe/London",
                    PropertyProcessingStatus.Enabled,
                    binding,
                    17,
                    19),
                _ => source.Cancel()),
            registry);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.ResolveGuestAsync(
                Guid.NewGuid(),
                DataRightsCaseOperation.AccessExport,
                ReceivedAt,
                ReceivedAt,
                source.Token));
    }

    private static (
        CountryPolicyRegistry Registry,
        PropertyGovernancePolicyBinding Binding) LoadPolicy(string fileName)
    {
        CountryPolicyPackArtifact artifact = CountryPolicyPackJson.Parse(
            File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory,
                "CountryPolicies",
                fileName)));
        CountryPolicyPackDocument document = artifact.Document;
        CountryPolicyRetentionRule retention = document.RetentionRules[0];
        CountryPolicyRegistry registry = CountryPolicyRegistry.Create(
            [artifact],
            [new(
                document.OperatingCountryCode,
                document.PolicyId,
                document.PolicyVersion,
                artifact.ContentSha256,
                CountryLaunchStatus.Engineering)],
            CountryPolicyRuntimeMode.Engineering,
            PinnedTzdbCountryPolicyTimeZoneRules.Instance);
        PropertyGovernancePolicyBinding binding = new(
            document.OperatingCountryCode,
            document.PolicyId,
            document.PolicyVersion,
            document.PermittedDataRegions[0],
            document.PermittedTransferProfiles[0],
            retention.RetentionPolicyId,
            retention.RetentionPolicyVersion,
            artifact.ContentSha256,
            document.EffectiveAtUtc,
            document.ExpiresAtUtc,
            ReceivedAt.AddDays(-1),
            document.RequiredAcknowledgements.Select(acknowledgement =>
                new PropertyGovernanceAcknowledgement(
                    acknowledgement.AcknowledgementId,
                    acknowledgement.AcknowledgementVersion)).ToArray());
        return (registry, binding);
    }

    private sealed class StubPropertyRepository(
        DataRightsPropertyPolicySnapshot? property,
        Action<CancellationToken>? afterGet = null)
        : IDataRightsPropertyProjectionRepository
    {
        public Task ApplyTopologyAsync(
            DataRightsPropertyTopologyWriteModel propertyWriteModel,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ApplyPolicyAsync(
            DataRightsPropertyPolicyWriteModel propertyWriteModel,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsPropertyPolicySnapshot?> GetPolicyAsync(
            Guid propertyId,
            CancellationToken cancellationToken)
        {
            afterGet?.Invoke(cancellationToken);
            return Task.FromResult(property);
        }
    }
}

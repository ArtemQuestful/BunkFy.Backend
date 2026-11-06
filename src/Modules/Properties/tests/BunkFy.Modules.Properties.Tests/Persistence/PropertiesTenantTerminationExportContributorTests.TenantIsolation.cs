namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Properties.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;
using DomainPropertyGovernanceAcknowledgement =
    BunkFy.Modules.Properties.Domain.ValueObjects.PropertyGovernanceAcknowledgement;

public sealed partial class PropertiesTenantTerminationExportContributorTests
{
    private const string ForeignTenantId =
        "10000000-0000-0000-0000-000000000002";
    private static readonly Guid ForeignPropertyId =
        Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid LocalGovernanceRevisionId =
        Guid.Parse("80000000-0000-0000-0000-000000000011");
    private static readonly Guid ForeignGovernanceRevisionId =
        Guid.Parse("80000000-0000-0000-0000-000000000012");

    [Fact]
    public async Task Export_preserves_and_excludes_foreign_governance_revisions()
    {
        string databaseName = Guid.NewGuid().ToString("N");
        InMemoryDatabaseRoot root = new();
        MutableFenceReader fences = new();
        await SeedTenantGovernanceGraphAsync(
            fences,
            databaseName,
            root,
            TenantId,
            PropertyId,
            LocalGovernanceRevisionId,
            "LOCAL");
        await SeedTenantGovernanceGraphAsync(
            fences,
            databaseName,
            root,
            ForeignTenantId,
            ForeignPropertyId,
            ForeignGovernanceRevisionId,
            "OTHER");
        fences.Current = FrozenFence();

        await using PropertiesDbContext context = CreateContext(
            fences,
            TenantId,
            databaseName,
            root);
        PropertiesTenantTerminationContributor contributor = new(
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
        DataRightsExportRecord revision = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                PropertiesTenantTerminationMetadata.GovernanceRevisionRecordType);
        Assert.Equal(LocalGovernanceRevisionId, revision.RecordId);
        Assert.DoesNotContain(
            sink.Records,
            record => record.RecordId == ForeignGovernanceRevisionId);
        Assert.True(await context.GovernanceRevisions
            .IgnoreQueryFilters()
            .AnyAsync(item => item.Id == ForeignGovernanceRevisionId));
    }

    [Fact]
    public async Task Destroy_removes_local_and_preserves_foreign_governance_revisions()
    {
        string databaseName = Guid.NewGuid().ToString("N");
        InMemoryDatabaseRoot root = new();
        MutableFenceReader fences = new();
        await SeedTenantGovernanceGraphAsync(
            fences,
            databaseName,
            root,
            TenantId,
            PropertyId,
            LocalGovernanceRevisionId,
            "LOCAL");
        await SeedTenantGovernanceGraphAsync(
            fences,
            databaseName,
            root,
            ForeignTenantId,
            ForeignPropertyId,
            ForeignGovernanceRevisionId,
            "OTHER");
        fences.Current = FrozenFence();

        await using PropertiesDbContext context = CreateContext(
            fences,
            TenantId,
            databaseName,
            root);
        PropertiesTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);

        TenantTerminationContributionResult result = await CompleteDestroyAsync(
            contributor,
            DestroyRequest());

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        PropertyGovernanceRevision remaining = await context
            .GovernanceRevisions
            .IgnoreQueryFilters()
            .SingleAsync();
        Assert.Equal(ForeignTenantId, remaining.ScopeId);
        Assert.Equal(ForeignGovernanceRevisionId, remaining.Id);
        Assert.True(await context.Properties
            .IgnoreQueryFilters()
            .AnyAsync(property => property.Id == ForeignPropertyId));
        Assert.False(await context.GovernanceRevisions
            .IgnoreQueryFilters()
            .AnyAsync(item => item.Id == LocalGovernanceRevisionId));
    }

    private static async Task SeedTenantGovernanceGraphAsync(
        MutableFenceReader fences,
        string databaseName,
        InMemoryDatabaseRoot root,
        string tenantId,
        Guid propertyId,
        Guid revisionId,
        string code)
    {
        await using PropertiesDbContext context = CreateContext(
            fences,
            tenantId,
            databaseName,
            root);
        Property property = Property.Create(
            propertyId,
            tenantId,
            $"Hostel {code}",
            code,
            "Europe/London",
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-5)).Value;
        PropertyGovernanceBinding binding = PropertyGovernanceBinding.Create(
            "GB",
            "uk-hostel-policy",
            policyVersion: 3,
            "eu-west",
            "standard-transfer",
            "hostel-retention",
            retentionPolicyVersion: 2,
            Digest,
            FrozenAtUtc.AddDays(-10),
            FrozenAtUtc.AddDays(10),
            FrozenAtUtc.AddDays(-2)).Value;
        DomainPropertyGovernanceAcknowledgement acknowledgement =
            DomainPropertyGovernanceAcknowledgement.Create(
                "controller-terms",
                acknowledgementVersion: 2).Value;
        Assert.True(property.ActivateProcessing(
            binding,
            [acknowledgement],
            property.Version,
            Guid.NewGuid(),
            FrozenAtUtc.AddDays(-2),
            "user:owner").IsSuccess);
        PropertyGovernanceRevisionCoordinates current = new(
            binding.OperatingCountryCode,
            binding.PolicyId,
            binding.PolicyVersion,
            binding.DataRegionId,
            binding.TransferProfileId,
            binding.RetentionPolicyId,
            binding.RetentionPolicyVersion,
            binding.ContentSha256,
            Digest);

        context.Properties.Add(property);
        context.GovernanceRevisions.Add(new(new(
            revisionId,
            tenantId,
            propertyId,
            property.Version,
            PropertyGovernanceRevisionAction.Activated,
            "Allowed",
            Previous: null,
            Current: current,
            "user:owner",
            FrozenAtUtc.AddDays(-2))));
        await context.SaveChangesAsync();
    }
}

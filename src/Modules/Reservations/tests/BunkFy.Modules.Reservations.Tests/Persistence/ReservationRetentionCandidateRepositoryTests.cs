namespace BunkFy.Modules.Reservations.Tests;

using System.Reflection;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Reservations.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationRetentionCandidateRepositoryTests
{
    [Fact]
    public async Task Scan_is_bounded_aggregated_and_tenant_scoped()
    {
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseInMemoryDatabase(
                    $"reservation-retention-candidates-" +
                    $"{Guid.NewGuid():N}")
                .Options;
        ReservationRetentionPolicyFixture policy =
            ReservationRetentionTestData.CreatePolicy();
        Guid tenantAReservationId = Guid.NewGuid();
        await SeedAsync(
            options,
            "tenant-a",
            tenantAReservationId,
            policy.Binding);
        await SeedAsync(
            options,
            "tenant-b",
            Guid.NewGuid(),
            policy.Binding);

        await using ReservationsDbContext dbContext = new(
            options,
            new ReservationRetentionModelTests.TestScopeContext(
                "tenant-a"));
        ReservationRetentionCandidateRepository repository =
            new(dbContext);

        ReservationRetentionScanPage page =
            await repository.ScanAsync(
                afterProjectionOrdinal: 0,
                limit: 1,
                CancellationToken.None);

        ReservationRetentionCandidateSnapshot candidate =
            Assert.Single(page.Candidates);
        Assert.Equal(tenantAReservationId, candidate.ReservationId);
        Assert.True(page.ReachedEnd);
        Assert.Equal(0, candidate.ActiveHoldCount);
        Assert.Equal(
            ReservationProcessingRestrictionContract.CurrentVersion,
            candidate.ProcessingRestrictionContractVersion);
        Assert.NotNull(candidate.Property);
        Assert.True(candidate.Property.IsKnown);
        Assert.NotNull(candidate.Property.GovernancePolicy);
    }

    [Fact]
    public async Task Scan_rejects_limit_above_configured_repository_bound()
    {
        DbContextOptions<ReservationsDbContext> options =
            CreateOptions();
        await using ReservationsDbContext dbContext = new(
            options,
            new ReservationRetentionModelTests.TestScopeContext(
                "tenant-a"));
        ReservationRetentionCandidateRepository repository =
            new(dbContext);

        ArgumentOutOfRangeException failure =
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.ScanAsync(
                    afterProjectionOrdinal: 0,
                    ReservationRetentionOptions.MaximumScanSize + 1,
                    CancellationToken.None));

        Assert.Equal("limit", failure.ParamName);
    }

    [Fact]
    public async Task Scan_uses_limit_plus_one_without_materializing_extra_candidate()
    {
        DbContextOptions<ReservationsDbContext> options =
            CreateOptions();
        ReservationRetentionPolicyFixture policy =
            ReservationRetentionTestData.CreatePolicy();
        await SeedAsync(
            options,
            "tenant-a",
            Guid.NewGuid(),
            policy.Binding,
            projectionOrdinal: 1);
        await SeedAsync(
            options,
            "tenant-a",
            Guid.NewGuid(),
            policy.Binding,
            projectionOrdinal: 2);

        await using ReservationsDbContext dbContext = new(
            options,
            new ReservationRetentionModelTests.TestScopeContext(
                "tenant-a"));
        ReservationRetentionScanPage page =
            await new ReservationRetentionCandidateRepository(dbContext)
                .ScanAsync(
                    afterProjectionOrdinal: 0,
                    limit: 1,
                    CancellationToken.None);

        Assert.Single(page.Candidates);
        Assert.Equal(1, page.Candidates[0].ProjectionOrdinal);
        Assert.False(page.ReachedEnd);
    }

    [Fact]
    public async Task Foreign_scope_rows_with_same_coordinates_do_not_enrich_candidate()
    {
        DbContextOptions<ReservationsDbContext> options =
            CreateOptions();
        ReservationRetentionPolicyFixture policy =
            ReservationRetentionTestData.CreatePolicy();
        Guid propertyId = Guid.NewGuid();
        Guid reservationId = Guid.NewGuid();
        await using (ReservationsDbContext local = new(
            options,
            new ReservationRetentionModelTests.TestScopeContext(
                "tenant-a")))
        {
            Reservation reservation =
                ReservationRetentionModelTests.CreateTerminalReservation(
                    "tenant-a",
                    propertyId,
                    reservationId);
            local.Entry(reservation)
                .Property(item => item.ProjectionOrdinal)
                .CurrentValue = 1;
            local.Reservations.Add(reservation);
            local.ProcessingRestrictionProjections.Add(
                ReservationProcessingRestrictionProjection.Create(
                    "tenant-a",
                    propertyId,
                    reservationId,
                    ReservationProcessingRestrictionContract.CurrentVersion,
                    ReservationRetentionTestData.Now.AddDays(-401))
                .Value);
            await local.SaveChangesAsync();
        }

        await using (ReservationsDbContext foreign = new(
            options,
            new ReservationRetentionModelTests.TestScopeContext(
                "tenant-b")))
        {
            foreign.PropertyProjections.Add(CreateProperty(
                propertyId,
                "tenant-b",
                policy.Binding));
            foreign.DataHolds.Add(ReservationDataHold.Place(
                Guid.NewGuid(),
                "tenant-b",
                propertyId,
                reservationId,
                "regulatory-request",
                "user:owner",
                ReservationRetentionTestData.Now.AddDays(-10)).Value);
            foreign.ProcessingRestrictionProjections.Add(
                ReservationProcessingRestrictionProjection.Create(
                    "tenant-b",
                    propertyId,
                    reservationId,
                    ReservationProcessingRestrictionContract.CurrentVersion +
                        1,
                    ReservationRetentionTestData.Now.AddDays(-401))
                .Value);
            await foreign.SaveChangesAsync();
        }

        await using ReservationsDbContext dbContext = new(
            options,
            new ReservationRetentionModelTests.TestScopeContext(
                "tenant-a"));
        ReservationRetentionCandidateSnapshot? candidate =
            await new ReservationRetentionCandidateRepository(dbContext)
                .LoadAsync(
                    propertyId,
                    reservationId,
                    CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Equal(0, candidate.ActiveHoldCount);
        Assert.Null(candidate.EarliestHoldPlacedAtUtc);
        Assert.Equal(
            ReservationProcessingRestrictionContract.CurrentVersion,
            candidate.ProcessingRestrictionContractVersion);
        Assert.Null(candidate.Property);
    }

    [Fact]
    public async Task Maximum_policy_acknowledgements_are_loaded()
    {
        ReservationRetentionCandidateSnapshot candidate =
            await LoadCandidateWithAcknowledgementsAsync(
                PropertiesContractLimits.MaximumPolicyAcknowledgements,
                overflow: false);

        Assert.NotNull(candidate.Property);
        Assert.NotNull(candidate.Property.GovernancePolicy);
        Assert.Equal(
            PropertiesContractLimits.MaximumPolicyAcknowledgements,
            candidate.Property.GovernancePolicy.Acknowledgements.Count);
    }

    [Fact]
    public async Task Policy_acknowledgement_overflow_fails_closed()
    {
        ReservationRetentionCandidateSnapshot candidate =
            await LoadCandidateWithAcknowledgementsAsync(
                PropertiesContractLimits.MaximumPolicyAcknowledgements,
                overflow: true);

        Assert.NotNull(candidate.Property);
        Assert.True(candidate.Property.IsKnown);
        Assert.Null(candidate.Property.GovernancePolicy);
    }

    [Fact]
    public async Task Unconfigured_property_is_preserved_without_policy()
    {
        ReservationPropertyProjection property =
            ReservationPropertyProjection.Create(
                Guid.NewGuid(),
                "tenant-a");
        property.ApplyTopology(
            "UTC",
            isActive: true,
            sourceVersion: 1);
        property.ApplyPolicy(
            PropertyProcessingStatus.Unconfigured,
            governancePolicy: null,
            sourceVersion: 1);

        ReservationRetentionCandidateSnapshot candidate =
            await LoadCandidateWithPropertyAsync(property);

        Assert.NotNull(candidate.Property);
        Assert.True(candidate.Property.IsKnown);
        Assert.Equal(
            PropertyProcessingStatus.Unconfigured,
            candidate.Property.ProcessingStatus);
        Assert.Null(candidate.Property.GovernancePolicy);
    }

    [Fact]
    public async Task Configured_zero_acknowledgement_policy_is_preserved()
    {
        ReservationRetentionCandidateSnapshot candidate =
            await LoadCandidateWithAcknowledgementsAsync(
                acknowledgementCount: 0,
                overflow: false);

        Assert.NotNull(candidate.Property);
        Assert.NotNull(candidate.Property.GovernancePolicy);
        Assert.Empty(
            candidate.Property.GovernancePolicy.Acknowledgements);
    }

    private static async Task SeedAsync(
        DbContextOptions<ReservationsDbContext> options,
        string tenantId,
        Guid reservationId,
        PropertyGovernancePolicyBinding policy,
        long? projectionOrdinal = null)
    {
        await using ReservationsDbContext dbContext = new(
            options,
            new ReservationRetentionModelTests.TestScopeContext(
                tenantId));
        Guid propertyId = Guid.NewGuid();
        Reservation reservation =
            ReservationRetentionModelTests.CreateTerminalReservation(
                tenantId,
                propertyId,
                reservationId);
        dbContext.Entry(reservation)
            .Property(item => item.ProjectionOrdinal)
            .CurrentValue = projectionOrdinal ??
                (string.Equals(
                    tenantId,
                    "tenant-a",
                    StringComparison.Ordinal)
                        ? 1
                        : 2);
        ReservationPropertyProjection property = CreateProperty(
            propertyId,
            tenantId,
            policy);
        ReservationProcessingRestrictionProjection restriction =
            ReservationProcessingRestrictionProjection.Create(
                tenantId,
                propertyId,
                reservationId,
                ReservationProcessingRestrictionContract
                    .CurrentVersion,
                ReservationRetentionTestData.Now.AddDays(-401))
            .Value;

        dbContext.Reservations.Add(reservation);
        dbContext.PropertyProjections.Add(property);
        dbContext.ProcessingRestrictionProjections.Add(restriction);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<ReservationRetentionCandidateSnapshot>
        LoadCandidateWithAcknowledgementsAsync(
            int acknowledgementCount,
            bool overflow)
    {
        PropertyGovernancePolicyBinding baseline =
            ReservationRetentionTestData.CreatePolicy().Binding;
        PropertyGovernancePolicyBinding policy = new(
            baseline.OperatingCountryCode,
            baseline.PolicyId,
            baseline.PolicyVersion,
            baseline.DataRegionId,
            baseline.TransferProfileId,
            baseline.RetentionPolicyId,
            baseline.RetentionPolicyVersion,
            baseline.ContentSha256,
            baseline.PolicyEffectiveAtUtc,
            baseline.PolicyExpiresAtUtc,
            baseline.ActivatedAtUtc,
            Enumerable.Range(0, acknowledgementCount)
                .Select(index => new PropertyGovernanceAcknowledgement(
                    $"acknowledgement-{index:D2}",
                    acknowledgementVersion: 1))
                .ToArray());
        ReservationPropertyProjection property = CreateProperty(
            Guid.NewGuid(),
            "tenant-a",
            policy);
        if (overflow)
        {
            FieldInfo acknowledgementsField =
                typeof(ReservationPropertyPolicyBinding).GetField(
                    "acknowledgements",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
            List<ReservationPropertyPolicyAcknowledgement>
                acknowledgements =
                    Assert.IsType<List<
                        ReservationPropertyPolicyAcknowledgement>>(
                        acknowledgementsField.GetValue(
                            property.GovernancePolicy));
            acknowledgements.Add(new(
                "acknowledgement-overflow",
                acknowledgementVersion: 1));
        }

        return await LoadCandidateWithPropertyAsync(property);
    }

    private static async Task<ReservationRetentionCandidateSnapshot>
        LoadCandidateWithPropertyAsync(
            ReservationPropertyProjection property)
    {
        DbContextOptions<ReservationsDbContext> options =
            CreateOptions();
        Guid reservationId = Guid.NewGuid();
        await using (ReservationsDbContext seed = new(
            options,
            new ReservationRetentionModelTests.TestScopeContext(
                "tenant-a")))
        {
            Reservation reservation =
                ReservationRetentionModelTests.CreateTerminalReservation(
                    "tenant-a",
                    property.Id,
                    reservationId);
            seed.Entry(reservation)
                .Property(item => item.ProjectionOrdinal)
                .CurrentValue = 1;
            seed.Reservations.Add(reservation);
            seed.PropertyProjections.Add(property);
            seed.ProcessingRestrictionProjections.Add(
                ReservationProcessingRestrictionProjection.Create(
                    "tenant-a",
                    property.Id,
                    reservationId,
                    ReservationProcessingRestrictionContract.CurrentVersion,
                    ReservationRetentionTestData.Now.AddDays(-401))
                .Value);
            await seed.SaveChangesAsync();
        }

        await using ReservationsDbContext dbContext = new(
            options,
            new ReservationRetentionModelTests.TestScopeContext(
                "tenant-a"));
        return Assert.IsType<ReservationRetentionCandidateSnapshot>(
            await new ReservationRetentionCandidateRepository(dbContext)
                .LoadAsync(
                    property.Id,
                    reservationId,
                    CancellationToken.None));
    }

    private static ReservationPropertyProjection CreateProperty(
        Guid propertyId,
        string tenantId,
        PropertyGovernancePolicyBinding policy)
    {
        ReservationPropertyProjection property =
            ReservationPropertyProjection.Create(
                propertyId,
                tenantId);
        property.ApplyTopology(
            "UTC",
            isActive: true,
            sourceVersion: 1);
        property.ApplyPolicy(
            PropertyProcessingStatus.Enabled,
            policy,
            sourceVersion: 1);
        return property;
    }

    private static DbContextOptions<ReservationsDbContext>
        CreateOptions() =>
        new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseInMemoryDatabase(
                $"reservation-retention-candidates-{Guid.NewGuid():N}")
            .Options;
}

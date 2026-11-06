namespace BunkFy.Modules.Guests.Tests.Persistence;

using System.Reflection;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Guests.Persistence.Repositories;
using BunkFy.Modules.Guests.Tests.Application;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestRetentionCandidateRepositoryTests
{
    [Fact]
    public async Task Scan_rejects_an_unbounded_head_limit()
    {
        await using GuestsDbContext dbContext = new(
            CreateOptions(),
            new TestScopeContext("tenant-a"));

        ArgumentOutOfRangeException failure =
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                new GuestRetentionCandidateRepository(dbContext).ScanAsync(
                    afterProjectionOrdinal: 0,
                    limit: 1001,
                    CancellationToken.None));

        Assert.Equal("limit", failure.ParamName);
    }

    [Fact]
    public async Task Scan_is_bounded_aggregated_and_tenant_scoped()
    {
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseInMemoryDatabase(
                    $"guest-retention-candidates-{Guid.NewGuid():N}")
                .Options;
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();
        Guid tenantAGuestId = Guid.NewGuid();
        await SeedAsync(
            options,
            "tenant-a",
            tenantAGuestId,
            policy.Binding);
        await SeedAsync(
            options,
            "tenant-b",
            Guid.NewGuid(),
            policy.Binding);

        await using GuestsDbContext dbContext = new(
            options,
            new TestScopeContext("tenant-a"));
        GuestRetentionCandidateRepository repository = new(dbContext);

        GuestRetentionScanPage page = await repository.ScanAsync(
            afterProjectionOrdinal: 0,
            limit: 1,
            CancellationToken.None);

        GuestRetentionCandidateSnapshot candidate =
            Assert.Single(page.Candidates);
        GuestRetentionStaySnapshot stay = Assert.Single(candidate.Stays);
        GuestRetentionPropertySnapshot property =
            Assert.Single(candidate.Properties);
        Assert.Equal(tenantAGuestId, candidate.GuestId);
        Assert.True(page.ReachedEnd);
        Assert.True(stay.ProjectionSupported);
        Assert.False(stay.HasOperationalStay);
        Assert.Equal(new DateOnly(2025, 1, 4),
            stay.LatestTerminalBusinessDate);
        Assert.Equal("UTC", property.TimeZoneId);
        Assert.NotNull(property.GovernancePolicy);
    }

    [Fact]
    public async Task Load_uses_only_the_local_projection_when_property_ids_match()
    {
        DbContextOptions<GuestsDbContext> options = CreateOptions();
        Guid propertyId = Guid.NewGuid();
        Guid guestId = Guid.NewGuid();
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();

        await using (GuestsDbContext tenantA = new(
            options,
            new TestScopeContext("tenant-a")))
        {
            GuestProfile profile = CreateProfile(
                "tenant-a",
                propertyId,
                guestId);
            GuestPropertyProjection localProperty = new(
                "tenant-a",
                propertyId,
                "Local property",
                "UTC",
                PropertyStatus.Active,
                version: 1);
            localProperty.ApplyPolicy(
                PropertyProcessingStatus.Enabled,
                policy.Binding,
                sourceVersion: 1);
            tenantA.GuestProfiles.Add(profile);
            tenantA.Entry(profile)
                .Property(item => item.ProjectionOrdinal)
                .CurrentValue = 1;
            tenantA.PropertyProjections.Add(localProperty);
            await tenantA.SaveChangesAsync();
        }

        await using (GuestsDbContext tenantB = new(
            options,
            new TestScopeContext("tenant-b")))
        {
            GuestPropertyProjection foreignProperty = new(
                "tenant-b",
                propertyId,
                "Foreign property",
                "Asia/Tokyo",
                PropertyStatus.Active,
                version: 1);
            foreignProperty.ApplyPolicy(
                PropertyProcessingStatus.Enabled,
                policy.Binding,
                sourceVersion: 1);
            tenantB.PropertyProjections.Add(foreignProperty);
            await tenantB.SaveChangesAsync();
        }

        await using GuestsDbContext dbContext = new(
            options,
            new TestScopeContext("tenant-a"));
        GuestRetentionCandidateSnapshot? candidate =
            await new GuestRetentionCandidateRepository(dbContext)
                .LoadAsync(guestId, CancellationToken.None);

        Assert.NotNull(candidate);
        GuestRetentionPropertySnapshot property = Assert.Single(
            candidate.Properties);
        Assert.Equal(propertyId, property.PropertyId);
        Assert.Equal("UTC", property.TimeZoneId);
        Assert.NotNull(property.GovernancePolicy);
    }

    [Fact]
    public async Task Load_does_not_consume_foreign_projection_or_associations()
    {
        DbContextOptions<GuestsDbContext> options = CreateOptions();
        Guid originPropertyId = Guid.NewGuid();
        Guid foreignAssociatedPropertyId = Guid.NewGuid();
        Guid guestId = Guid.NewGuid();
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();

        await using (GuestsDbContext tenantA = new(
            options,
            new TestScopeContext("tenant-a")))
        {
            GuestProfile profile = CreateProfile(
                "tenant-a",
                originPropertyId,
                guestId);
            tenantA.GuestProfiles.Add(profile);
            tenantA.Entry(profile)
                .Property(item => item.ProjectionOrdinal)
                .CurrentValue = 1;
            await tenantA.SaveChangesAsync();
        }

        await using (GuestsDbContext tenantB = new(
            options,
            new TestScopeContext("tenant-b")))
        {
            GuestPropertyProjection foreignOrigin = new(
                "tenant-b",
                originPropertyId,
                "Foreign origin",
                "UTC",
                PropertyStatus.Active,
                version: 1);
            foreignOrigin.ApplyPolicy(
                PropertyProcessingStatus.Enabled,
                policy.Binding,
                sourceVersion: 1);
            tenantB.PropertyProjections.Add(foreignOrigin);
            tenantB.StayHistory.Add(new(
                "tenant-b",
                guestId,
                Guid.NewGuid(),
                foreignAssociatedPropertyId,
                GuestStayRole.Primary,
                new DateOnly(2025, 1, 1),
                new DateOnly(2025, 1, 2),
                GuestStayStatus.CheckedOut,
                checkedInBusinessDate: null,
                noShowBusinessDate: null,
                new DateOnly(2025, 1, 2),
                isCurrentParticipant: false,
                reservationVersion: 1,
                GuestsModuleMetadata.StayHistoryProjectionVersion));
            tenantB.DataHolds.Add(GuestDataHold.Place(
                Guid.NewGuid(),
                "tenant-b",
                foreignAssociatedPropertyId,
                guestId,
                "foreign-hold",
                "user:owner",
                GuestRetentionTestData.Now).Value);
            await tenantB.SaveChangesAsync();
        }

        await using GuestsDbContext dbContext = new(
            options,
            new TestScopeContext("tenant-a"));
        GuestRetentionCandidateSnapshot? candidate =
            await new GuestRetentionCandidateRepository(dbContext)
                .LoadAsync(guestId, CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Empty(candidate.Stays);
        Assert.Empty(candidate.ActiveHolds);
        Assert.Empty(candidate.Properties);
        Assert.False(candidate.AssociationOverflowed);
    }

    [Fact]
    public async Task Scan_uses_an_ordered_association_budget()
    {
        DbContextOptions<GuestsDbContext> options = CreateOptions();
        const string tenantId = "tenant-page";
        await using (GuestsDbContext seed = new(
            options,
            new TestScopeContext(tenantId)))
        {
            for (int index = 0; index <=
                    GuestAnonymisationEligibilityContract
                        .MaximumAffectedProperties;
                 index++)
            {
                Guid propertyId = Guid.NewGuid();
                GuestProfile profile = CreateProfile(
                    tenantId,
                    propertyId,
                    Guid.NewGuid());
                seed.GuestProfiles.Add(profile);
                seed.Entry(profile)
                    .Property(item => item.ProjectionOrdinal)
                    .CurrentValue = index + 1;
                seed.PropertyProjections.Add(new(
                    tenantId,
                    propertyId,
                    $"Property {index}",
                    "UTC",
                    PropertyStatus.Active,
                    version: 1));
            }

            await seed.SaveChangesAsync();
        }

        await using GuestsDbContext dbContext = new(
            options,
            new TestScopeContext(tenantId));

        GuestRetentionScanPage page =
            await new GuestRetentionCandidateRepository(dbContext)
                .ScanAsync(
                    afterProjectionOrdinal: 0,
                    limit: 1000,
                    CancellationToken.None);

        Assert.Equal(
            GuestAnonymisationEligibilityContract
                .MaximumAffectedProperties,
            page.Candidates.Count);
        Assert.False(page.ReachedEnd);
    }

    [Fact]
    public async Task Association_overflow_fails_closed_before_materialization()
    {
        DbContextOptions<GuestsDbContext> options = CreateOptions();
        const string tenantId = "tenant-overflow";
        Guid originPropertyId = Guid.NewGuid();
        GuestProfile profile = CreateProfile(
            tenantId,
            originPropertyId,
            Guid.NewGuid());
        await using (GuestsDbContext seed = new(
            options,
            new TestScopeContext(tenantId)))
        {
            seed.GuestProfiles.Add(profile);
            seed.Entry(profile)
                .Property(item => item.ProjectionOrdinal)
                .CurrentValue = 1;
            for (int index = 0; index <
                    GuestAnonymisationEligibilityContract
                        .MaximumAffectedProperties;
                 index++)
            {
                Guid propertyId = Guid.NewGuid();
                seed.StayHistory.Add(new(
                    tenantId,
                    profile.Id,
                    Guid.NewGuid(),
                    propertyId,
                    GuestStayRole.Primary,
                    new DateOnly(2025, 1, 1),
                    new DateOnly(2025, 1, 2),
                    GuestStayStatus.CheckedOut,
                    new DateOnly(2025, 1, 1),
                    noShowBusinessDate: null,
                    new DateOnly(2025, 1, 2),
                    isCurrentParticipant: false,
                    reservationVersion: 1,
                    GuestsModuleMetadata.StayHistoryProjectionVersion));
            }

            await seed.SaveChangesAsync();
        }

        await using GuestsDbContext dbContext = new(
            options,
            new TestScopeContext(tenantId));
        GuestRetentionCandidateSnapshot? candidate =
            await new GuestRetentionCandidateRepository(dbContext)
                .LoadAsync(profile.Id, CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.True(candidate.AssociationOverflowed);
        Assert.Empty(candidate.Stays);
        Assert.Empty(candidate.ActiveHolds);
        Assert.Empty(candidate.Properties);
    }

    [Fact]
    public async Task Active_holds_are_aggregated_per_guest_and_property()
    {
        DbContextOptions<GuestsDbContext> options = CreateOptions();
        const string tenantId = "tenant-holds";
        Guid propertyId = Guid.NewGuid();
        GuestProfile profile = CreateProfile(
            tenantId,
            propertyId,
            Guid.NewGuid());
        DateTimeOffset earliest = GuestRetentionTestData.Now
            .AddMinutes(-99);
        await using (GuestsDbContext seed = new(
            options,
            new TestScopeContext(tenantId)))
        {
            seed.GuestProfiles.Add(profile);
            seed.Entry(profile)
                .Property(item => item.ProjectionOrdinal)
                .CurrentValue = 1;
            seed.PropertyProjections.Add(new(
                tenantId,
                propertyId,
                "Property",
                "UTC",
                PropertyStatus.Active,
                version: 1));
            seed.DataHolds.AddRange(Enumerable.Range(0, 100).Select(index =>
                GuestDataHold.Place(
                    Guid.NewGuid(),
                    tenantId,
                    propertyId,
                    profile.Id,
                    "regulatory-request",
                    "user:owner",
                    GuestRetentionTestData.Now.AddMinutes(-index)).Value));
            await seed.SaveChangesAsync();
        }

        await using GuestsDbContext dbContext = new(
            options,
            new TestScopeContext(tenantId));
        GuestRetentionCandidateSnapshot? candidate =
            await new GuestRetentionCandidateRepository(dbContext)
                .LoadAsync(profile.Id, CancellationToken.None);

        Assert.NotNull(candidate);
        GuestRetentionHoldSnapshot hold = Assert.Single(
            candidate.ActiveHolds);
        Assert.Equal(propertyId, hold.PropertyId);
        Assert.Equal(earliest, hold.PlacedAtUtc);
        Assert.False(candidate.AssociationOverflowed);
    }

    [Fact]
    public async Task Policy_acknowledgement_overflow_marks_projection_unavailable()
    {
        DbContextOptions<GuestsDbContext> options = CreateOptions();
        const string tenantId = "tenant-acknowledgements";
        Guid propertyId = Guid.NewGuid();
        GuestProfile profile = CreateProfile(
            tenantId,
            propertyId,
            Guid.NewGuid());
        PropertyGovernancePolicyBinding baseline =
            GuestRetentionTestData.CreatePolicy().Binding;
        PropertyGovernancePolicyBinding maximumPolicy = new(
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
            Enumerable.Range(
                    0,
                    PropertiesContractLimits
                        .MaximumPolicyAcknowledgements)
                .Select(index => new PropertyGovernanceAcknowledgement(
                    $"acknowledgement-{index:D2}",
                    acknowledgementVersion: 1))
                .ToArray());
        GuestPropertyProjection property = new(
            tenantId,
            propertyId,
            "Property",
            "UTC",
            PropertyStatus.Active,
            version: 1);
        property.ApplyPolicy(
            PropertyProcessingStatus.Enabled,
            maximumPolicy,
            sourceVersion: 1);
        FieldInfo acknowledgementsField = typeof(GuestPropertyPolicyBinding)
            .GetField(
                "acknowledgements",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
        List<GuestPropertyPolicyAcknowledgement> acknowledgements =
            Assert.IsType<List<GuestPropertyPolicyAcknowledgement>>(
                acknowledgementsField.GetValue(
                    property.GovernancePolicy));
        acknowledgements.Add(new(
            "acknowledgement-overflow",
            acknowledgementVersion: 1));

        await using (GuestsDbContext seed = new(
            options,
            new TestScopeContext(tenantId)))
        {
            seed.GuestProfiles.Add(profile);
            seed.Entry(profile)
                .Property(item => item.ProjectionOrdinal)
                .CurrentValue = 1;
            seed.PropertyProjections.Add(property);
            await seed.SaveChangesAsync();
        }

        await using GuestsDbContext dbContext = new(
            options,
            new TestScopeContext(tenantId));
        GuestRetentionCandidateSnapshot? candidate =
            await new GuestRetentionCandidateRepository(dbContext)
                .LoadAsync(profile.Id, CancellationToken.None);

        Assert.NotNull(candidate);
        GuestRetentionPropertySnapshot unavailable = Assert.Single(
            candidate.Properties);
        Assert.False(unavailable.IsKnown);
        Assert.Null(unavailable.GovernancePolicy);
    }

    private static async Task SeedAsync(
        DbContextOptions<GuestsDbContext> options,
        string tenantId,
        Guid guestId,
        PropertyGovernancePolicyBinding policy)
    {
        await using GuestsDbContext dbContext = new(
            options,
            new TestScopeContext(tenantId));
        Guid propertyId = Guid.NewGuid();
        GuestProfile profile = GuestProfile.Create(
            guestId,
            tenantId,
            propertyId,
            "Guest",
            legalName: null,
            email: null,
            phone: null,
            dateOfBirth: null,
            nationalityCountryCode: null,
            preferredLanguageTag: null,
            notes: null,
            "user:creator",
            Guid.NewGuid(),
            GuestRetentionTestData.Now.AddYears(-2)).Value;
        GuestPropertyProjection property = new(
            tenantId,
            propertyId,
            "Property",
            "UTC",
            PropertyStatus.Active,
            version: 1);
        property.ApplyPolicy(
            PropertyProcessingStatus.Enabled,
            policy,
            sourceVersion: 1);
        dbContext.GuestProfiles.Add(profile);
        dbContext.Entry(profile)
            .Property(item => item.ProjectionOrdinal)
            .CurrentValue = string.Equals(
                tenantId,
                "tenant-a",
                StringComparison.Ordinal)
                    ? 1
                    : 2;
        dbContext.PropertyProjections.Add(property);
        dbContext.StayHistory.Add(new(
            tenantId,
            profile.Id,
            Guid.NewGuid(),
            propertyId,
            GuestStayRole.Primary,
            new DateOnly(2025, 1, 2),
            new DateOnly(2025, 1, 4),
            GuestStayStatus.CheckedOut,
            new DateOnly(2025, 1, 2),
            noShowBusinessDate: null,
            new DateOnly(2025, 1, 4),
            isCurrentParticipant: false,
            reservationVersion: 1,
            GuestsModuleMetadata.StayHistoryProjectionVersion));
        await dbContext.SaveChangesAsync();
    }

    private static DbContextOptions<GuestsDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<GuestsDbContext>()
            .UseInMemoryDatabase(
                $"guest-retention-candidates-{Guid.NewGuid():N}")
            .Options;

    private static GuestProfile CreateProfile(
        string tenantId,
        Guid propertyId,
        Guid guestId) =>
        GuestProfile.Create(
            guestId,
            tenantId,
            propertyId,
            "Guest",
            legalName: null,
            email: null,
            phone: null,
            dateOfBirth: null,
            nationalityCountryCode: null,
            preferredLanguageTag: null,
            notes: null,
            "user:creator",
            Guid.NewGuid(),
            GuestRetentionTestData.Now.AddYears(-2)).Value;

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }
}

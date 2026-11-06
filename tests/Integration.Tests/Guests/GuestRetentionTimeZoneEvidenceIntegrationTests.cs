namespace Integration.Tests;

using System.Data.Common;
using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Guests.Persistence.Repositories;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Messaging;
using Gma.Framework.ProjectionRebuild;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tenancy;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class RetentionControlPlaneIntegrationTests
{
    private const string TimeZoneUnavailableOutcome =
        "guests.guest-operational.time-zone-unavailable";
    private const string GuestProjectionUnavailableOutcome =
        "guests.guest-operational.projection-unavailable";
    private const string GuestPolicyUnavailableOutcome =
        "guests.guest-operational.policy-unavailable";
    private const string TimeZoneTenantA =
        "7d000000-0000-0000-0000-000000000001";
    private const string TimeZoneTenantB =
        "7d000000-0000-0000-0000-000000000002";
    private const string MissingProjectionTenant =
        "7d000000-0000-0000-0000-000000000005";
    private const string ForeignProjectionTenant =
        "7d000000-0000-0000-0000-000000000006";
    private const string ConvergenceTenant =
        "7d000000-0000-0000-0000-000000000003";
    private static readonly Guid SharedTimeZonePropertyId = Guid.Parse(
        "7d100000-0000-0000-0000-000000000001");
    private static readonly Guid TimeZoneGuestA = Guid.Parse(
        "7d200000-0000-0000-0000-000000000001");
    private static readonly Guid TimeZoneGuestB = Guid.Parse(
        "7d200000-0000-0000-0000-000000000002");
    private static readonly Guid SharedMissingProjectionPropertyId = Guid.Parse(
        "7d100000-0000-0000-0000-000000000005");
    private static readonly Guid MissingProjectionGuest = Guid.Parse(
        "7d200000-0000-0000-0000-000000000005");
    private static readonly Guid ForeignProjectionGuest = Guid.Parse(
        "7d200000-0000-0000-0000-000000000006");

    [Fact]
    public void Embedded_calendar_covers_worldwide_midnight_edges_and_fails_closed()
    {
        AssertNextLocalDay(
            "Etc/UTC",
            new DateOnly(2026, 1, 1),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        AssertNextLocalDay(
            "Asia/Kathmandu",
            new DateOnly(2026, 1, 1),
            new DateTimeOffset(2026, 1, 1, 18, 15, 0, TimeSpan.Zero));
        AssertNextLocalDay(
            "America/New_York",
            new DateOnly(2026, 3, 8),
            new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero));
        AssertNextLocalDay(
            "America/Havana",
            new DateOnly(2020, 10, 31),
            new DateTimeOffset(2020, 11, 1, 5, 0, 0, TimeSpan.Zero));
        AssertNextLocalDay(
            "America/Sao_Paulo",
            new DateOnly(2018, 11, 3),
            new DateTimeOffset(2018, 11, 4, 3, 0, 0, TimeSpan.Zero));

        Assert.False(TimeZoneCalendarMath.TryGetStartOfNextLocalDay(
            new DateOnly(2011, 12, 29),
            "Pacific/Apia",
            out _));
        Assert.False(TimeZoneCalendarMath.TryGetStartOfNextLocalDay(
            new DateOnly(2026, 1, 1),
            "UTC",
            out _));
        Assert.False(TimeZoneCalendarMath.TryGetStartOfNextLocalDay(
            new DateOnly(2026, 1, 1),
            "Pacific Standard Time",
            out _));
        Assert.False(TimeZoneCalendarMath.TryGetStartOfNextLocalDay(
            new DateOnly(2026, 1, 1),
            "Missing/Zone",
            out _));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Projection_delivery_and_rebuild_converge_without_evidence_downgrade()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_guest_retention_tz_convergence")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        using IHost worker = CreateWorker(postgreSql.GetConnectionString());
        await MigrateAsync(worker).ConfigureAwait(false);
        Guid propertyId = Guid.Parse(
            "7d100000-0000-0000-0000-000000000003");
        DateTimeOffset occurredAtUtc = DateTimeOffset.UtcNow;

        await ApplyGuestPropertyEventAsync(
            worker,
            ConvergenceTenant,
            new PropertyCreatedIntegrationEvent(
                Guid.NewGuid(),
                ConvergenceTenant,
                occurredAtUtc,
                propertyId,
                "Convergence Hostel",
                "convergence-hostel",
                "Etc/UTC",
                PropertyStatus.Active,
                propertyVersion: 1)).ConfigureAwait(false);
        await AssertProjectedTimeZoneAsync(
            worker,
            ConvergenceTenant,
            propertyId,
            "Etc/UTC",
            GuestPropertyTimeZoneEvidenceSource.Generic,
            sourceVersion: 1,
            topologyVersion: 1).ConfigureAwait(false);

        var genericSecond = new PropertyUpdatedIntegrationEvent(
            Guid.NewGuid(),
            ConvergenceTenant,
            occurredAtUtc.AddMinutes(1),
            propertyId,
            "Convergence Hostel",
            "convergence-hostel",
            "Europe/London",
            PropertyStatus.Active,
            propertyVersion: 2);
        await ApplyGuestPropertyEventAsync(
            worker,
            ConvergenceTenant,
            genericSecond).ConfigureAwait(false);
        await ApplyGuestPropertyEventAsync(
            worker,
            ConvergenceTenant,
            new PropertyTimeZoneChangedIntegrationEvent(
                Guid.NewGuid(),
                ConvergenceTenant,
                occurredAtUtc.AddMinutes(1),
                propertyId,
                "Etc/UTC",
                "Etc/UTC",
                "Europe/London",
                PropertyTimeZoneChangeKind.Changed,
                TimeZoneCatalog.Default.CatalogVersion,
                propertyVersion: 2)).ConfigureAwait(false);
        await AssertProjectedTimeZoneAsync(
            worker,
            ConvergenceTenant,
            propertyId,
            "Europe/London",
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            sourceVersion: 2,
            topologyVersion: 2).ConfigureAwait(false);

        var dedicatedThird = new PropertyTimeZoneChangedIntegrationEvent(
            Guid.NewGuid(),
            ConvergenceTenant,
            occurredAtUtc.AddMinutes(2),
            propertyId,
            "Europe/London",
            "Europe/London",
            "Asia/Kathmandu",
            PropertyTimeZoneChangeKind.Changed,
            TimeZoneCatalog.Default.CatalogVersion,
            propertyVersion: 3);
        await ApplyGuestPropertyEventAsync(
            worker,
            ConvergenceTenant,
            dedicatedThird).ConfigureAwait(false);
        await AssertProjectedTimeZoneAsync(
            worker,
            ConvergenceTenant,
            propertyId,
            "Asia/Kathmandu",
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            sourceVersion: 3,
            topologyVersion: 2).ConfigureAwait(false);

        var genericThird = new PropertyUpdatedIntegrationEvent(
            Guid.NewGuid(),
            ConvergenceTenant,
            occurredAtUtc.AddMinutes(2),
            propertyId,
            "Convergence Hostel",
            "convergence-hostel",
            "Asia/Kathmandu",
            PropertyStatus.Active,
            propertyVersion: 3);
        await ApplyGuestPropertyEventAsync(
            worker,
            ConvergenceTenant,
            genericThird).ConfigureAwait(false);
        await ApplyGuestPropertyEventAsync(
            worker,
            ConvergenceTenant,
            genericThird with { }).ConfigureAwait(false);
        await ApplyGuestPropertyEventAsync(
            worker,
            ConvergenceTenant,
            new PropertyTimeZoneChangedIntegrationEvent(
                Guid.NewGuid(),
                ConvergenceTenant,
                occurredAtUtc.AddMinutes(1),
                propertyId,
                "Etc/UTC",
                "Etc/UTC",
                "Europe/London",
                PropertyTimeZoneChangeKind.Changed,
                TimeZoneCatalog.Default.CatalogVersion,
                propertyVersion: 2)).ConfigureAwait(false);
        await AssertProjectedTimeZoneAsync(
            worker,
            ConvergenceTenant,
            propertyId,
            "Asia/Kathmandu",
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            sourceVersion: 3,
            topologyVersion: 3).ConfigureAwait(false);

        Guid rebuildPropertyId = Guid.Parse(
            "7d100000-0000-0000-0000-000000000004");
        await ApplyGuestPropertyEventAsync(
            worker,
            ConvergenceTenant,
            new PropertyCreatedIntegrationEvent(
                Guid.NewGuid(),
                ConvergenceTenant,
                occurredAtUtc,
                rebuildPropertyId,
                "Rebuild Hostel",
                "rebuild-hostel",
                "UTC",
                PropertyStatus.Active,
                propertyVersion: 1)).ConfigureAwait(false);
        await AssertProjectedClassificationAsync(
            worker,
            ConvergenceTenant,
            rebuildPropertyId,
            "UTC",
            PropertyTimeZoneStatus.Alias,
            "Etc/UTC",
            TimeZoneCatalog.Default.CatalogVersion,
            GuestPropertyTimeZoneEvidenceSource.Generic,
            sourceVersion: 1,
            topologyVersion: 1,
            propertyStatus: PropertyStatus.Active).ConfigureAwait(false);

        await ApplyGuestPropertyEventAsync(
            worker,
            ConvergenceTenant,
            new PropertyRetiredIntegrationEvent(
                Guid.NewGuid(),
                ConvergenceTenant,
                occurredAtUtc.AddMinutes(1),
                rebuildPropertyId,
                propertyVersion: 2)).ConfigureAwait(false);

        await ApplyGuestPropertiesRebuildAsync(
            worker,
            ConvergenceTenant,
            new PropertyTopologyProjectionExport(
                ConvergenceTenant,
                rebuildPropertyId,
                "Rebuild Hostel",
                "rebuild-hostel",
                "UTC",
                PropertyStatus.Retired,
                version: 2)).ConfigureAwait(false);
        await AssertProjectedClassificationAsync(
            worker,
            ConvergenceTenant,
            rebuildPropertyId,
            "UTC",
            PropertyTimeZoneStatus.Alias,
            "Etc/UTC",
            TimeZoneCatalog.Default.CatalogVersion,
            GuestPropertyTimeZoneEvidenceSource.Rebuild,
            sourceVersion: 2,
            topologyVersion: 2,
            propertyStatus: PropertyStatus.Retired).ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Provider_runtime_is_retry_exact_and_tenant_isolated()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_guest_retention_tz_runtime")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        var clock = new MutableSubMicrosecondClock(DateTimeOffset.UtcNow);
        using IHost worker = CreateWorker(
            postgreSql.GetConnectionString(),
            clock);
        await MigrateAsync(worker).ConfigureAwait(false);
        DateOnly terminalDate = new(2025, 8, 13);
        await SeedGuestCandidateWithoutPropertyProjectionAsync(
            worker,
            MissingProjectionTenant,
            SharedMissingProjectionPropertyId,
            MissingProjectionGuest,
            terminalDate).ConfigureAwait(false);
        await SeedGuestTimeZoneCandidateAsync(
            worker,
            ForeignProjectionTenant,
            SharedMissingProjectionPropertyId,
            ForeignProjectionGuest,
            "Etc/UTC",
            terminalDate).ConfigureAwait(false);

        RetentionContributionRequest missingProjectionRequest =
            CreateGuestRetentionRequest(
                worker,
                MissingProjectionTenant,
                Guid.NewGuid());
        clock.UtcNow = WithSubMicrosecondTicks(
            missingProjectionRequest.StartedAtUtc.AddSeconds(1));
        RetentionContributionResult missingLocalProjection =
            await ExecuteGuestRetentionAsync(
                worker,
                MissingProjectionTenant,
                missingProjectionRequest).ConfigureAwait(false);
        Assert.Equal(
            RetentionContributionStatus.Failed,
            missingLocalProjection.Status);
        Assert.Equal(
            GuestProjectionUnavailableOutcome,
            missingLocalProjection.OutcomeCode);
        await AssertTenantHasNoRetentionMutationAsync(
            worker,
            MissingProjectionTenant,
            MissingProjectionGuest).ConfigureAwait(false);
        await AssertTenantHasNoRetentionMutationAsync(
            worker,
            ForeignProjectionTenant,
            ForeignProjectionGuest).ConfigureAwait(false);

        await SeedGuestTimeZoneCandidateAsync(
            worker,
            TimeZoneTenantA,
            SharedTimeZonePropertyId,
            TimeZoneGuestA,
            "Asia/Kathmandu",
            terminalDate).ConfigureAwait(false);
        await SeedGuestTimeZoneCandidateAsync(
            worker,
            TimeZoneTenantB,
            SharedTimeZonePropertyId,
            TimeZoneGuestB,
            "Etc/UTC",
            terminalDate).ConfigureAwait(false);
        await SeedForeignScopeStayAsync(
            worker,
            TimeZoneTenantB,
            TimeZoneGuestA,
            SharedTimeZonePropertyId,
            new DateOnly(2025, 12, 31)).ConfigureAwait(false);

        RetentionContributionRequest requestA = CreateGuestRetentionRequest(
            worker,
            TimeZoneTenantA,
            Guid.NewGuid());
        DateTimeOffset rawCompletedAtUtc = WithSubMicrosecondTicks(
            requestA.StartedAtUtc.AddSeconds(1));
        clock.UtcNow = rawCompletedAtUtc;
        RetentionContributionResult first = await ExecuteGuestRetentionAsync(
            worker,
            TimeZoneTenantA,
            requestA).ConfigureAwait(false);
        RetentionContributionResult replay = await ExecuteGuestRetentionAsync(
            worker,
            TimeZoneTenantA,
            requestA).ConfigureAwait(false);
        Assert.Equal(first, replay);
        Assert.NotEqual(
            0,
            rawCompletedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(
            rawCompletedAtUtc.AddTicks(
                -(rawCompletedAtUtc.Ticks %
                    TimeSpan.TicksPerMicrosecond)),
            first.CompletedAtUtc);
        Assert.Equal(RetentionContributionStatus.Completed, first.Status);
        Assert.Equal(1, first.ScannedCount);
        Assert.Equal(1, first.AffectedCount);
        Assert.Equal(0, first.RemainingCount);

        await AssertTenantRetentionProofAsync(
            worker,
            TimeZoneTenantA,
            TimeZoneGuestA,
            new DateTimeOffset(
                2026,
                8,
                13,
                18,
                15,
                0,
                TimeSpan.Zero)).ConfigureAwait(false);
        await AssertTenantHasNoRetentionMutationAsync(
            worker,
            TimeZoneTenantB,
            TimeZoneGuestB).ConfigureAwait(false);

        RetentionContributionRequest requestB = CreateGuestRetentionRequest(
            worker,
            TimeZoneTenantB,
            Guid.NewGuid());
        clock.UtcNow = WithSubMicrosecondTicks(
            requestB.StartedAtUtc.AddSeconds(1));
        RetentionContributionResult resultB = await ExecuteGuestRetentionAsync(
            worker,
            TimeZoneTenantB,
            requestB).ConfigureAwait(false);
        Assert.Equal(RetentionContributionStatus.Completed, resultB.Status);
        Assert.Equal(1, resultB.AffectedCount);
        await AssertTenantRetentionProofAsync(
            worker,
            TimeZoneTenantB,
            TimeZoneGuestB,
            new DateTimeOffset(
                2026,
                8,
                14,
                0,
                0,
                0,
                TimeSpan.Zero)).ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Provider_runtime_fails_closed_and_recovers_with_bounded_work()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_guest_retention_tz_refusal")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        using IHost worker = CreateWorker(postgreSql.GetConnectionString());
        await MigrateAsync(worker).ConfigureAwait(false);
        await AssertPostgreSqlCandidateScanBoundsAsync(worker)
            .ConfigureAwait(false);
        await AssertPostPreflightAssociationGrowthFailsClosedAsync(worker)
            .ConfigureAwait(false);
        TimeZoneRefusalCase[] cases =
        [
            new(
                "7d000000-0000-0000-0000-000000000011",
                Guid.Parse("7d100000-0000-0000-0000-000000000011"),
                Guid.Parse("7d200000-0000-0000-0000-000000000011"),
                "UTC",
                new DateOnly(2025, 1, 1)),
            new(
                "7d000000-0000-0000-0000-000000000012",
                Guid.Parse("7d100000-0000-0000-0000-000000000012"),
                Guid.Parse("7d200000-0000-0000-0000-000000000012"),
                "Pacific Standard Time",
                new DateOnly(2025, 1, 1)),
            new(
                "7d000000-0000-0000-0000-000000000013",
                Guid.Parse("7d100000-0000-0000-0000-000000000013"),
                Guid.Parse("7d200000-0000-0000-0000-000000000013"),
                "Missing/Zone",
                new DateOnly(2025, 1, 1)),
            new(
                "7d000000-0000-0000-0000-000000000014",
                Guid.Parse("7d100000-0000-0000-0000-000000000014"),
                Guid.Parse("7d200000-0000-0000-0000-000000000014"),
                "Pacific/Apia",
                new DateOnly(2011, 12, 29))
        ];
        foreach (TimeZoneRefusalCase refusal in cases)
        {
            await SeedGuestTimeZoneCandidateAsync(
                worker,
                refusal.TenantId,
                refusal.PropertyId,
                refusal.GuestId,
                refusal.TimeZoneId,
                refusal.TerminalDate).ConfigureAwait(false);
            RetentionContributionResult result =
                await ExecuteGuestRetentionAsync(
                    worker,
                    refusal.TenantId,
                    CreateGuestRetentionRequest(
                        worker,
                        refusal.TenantId,
                        Guid.NewGuid())).ConfigureAwait(false);
            Assert.Equal(RetentionContributionStatus.Failed, result.Status);
            Assert.Equal(TimeZoneUnavailableOutcome, result.OutcomeCode);
            await AssertTenantHasNoRetentionMutationAsync(
                worker,
                refusal.TenantId,
                refusal.GuestId).ConfigureAwait(false);
        }

        TimeZoneRefusalCase catalogMismatch = new(
            "7d000000-0000-0000-0000-000000000015",
            Guid.Parse("7d100000-0000-0000-0000-000000000015"),
            Guid.Parse("7d200000-0000-0000-0000-000000000015"),
            "Etc/UTC",
            new DateOnly(2025, 1, 1));
        await SeedGuestTimeZoneCandidateAsync(
            worker,
            catalogMismatch.TenantId,
            catalogMismatch.PropertyId,
            catalogMismatch.GuestId,
            catalogMismatch.TimeZoneId,
            catalogMismatch.TerminalDate).ConfigureAwait(false);
        await ApplyGuestPropertyEventAsync(
            worker,
            catalogMismatch.TenantId,
            new PropertyTimeZoneChangedIntegrationEvent(
                Guid.NewGuid(),
                catalogMismatch.TenantId,
                DateTimeOffset.UtcNow,
                catalogMismatch.PropertyId,
                "Etc/UTC",
                "Etc/UTC",
                "Asia/Kathmandu",
                PropertyTimeZoneChangeKind.Changed,
                "TZDB: deliberately-stale-catalog",
                propertyVersion: 2)).ConfigureAwait(false);
        await ApplyGuestPropertyEventAsync(
            worker,
            catalogMismatch.TenantId,
            new PropertyUpdatedIntegrationEvent(
                Guid.NewGuid(),
                catalogMismatch.TenantId,
                DateTimeOffset.UtcNow,
                catalogMismatch.PropertyId,
                "Catalog Mismatch Hostel",
                "catalog-mismatch-hostel",
                "Asia/Kathmandu",
                PropertyStatus.Active,
                propertyVersion: 2)).ConfigureAwait(false);
        RetentionContributionResult mismatchResult =
            await ExecuteGuestRetentionAsync(
                worker,
                catalogMismatch.TenantId,
                CreateGuestRetentionRequest(
                    worker,
                    catalogMismatch.TenantId,
                    Guid.NewGuid())).ConfigureAwait(false);
        Assert.Equal(RetentionContributionStatus.Failed, mismatchResult.Status);
        Assert.Equal(TimeZoneUnavailableOutcome, mismatchResult.OutcomeCode);
        await AssertTenantHasNoRetentionMutationAsync(
            worker,
            catalogMismatch.TenantId,
            catalogMismatch.GuestId).ConfigureAwait(false);

        TimeZoneRefusalCase alias = cases[0];
        await ApplyGuestPropertyEventAsync(
            worker,
            alias.TenantId,
            new PropertyUpdatedIntegrationEvent(
                Guid.NewGuid(),
                alias.TenantId,
                DateTimeOffset.UtcNow,
                alias.PropertyId,
                "Recovered Hostel",
                "recovered-hostel",
                "Etc/UTC",
                PropertyStatus.Active,
                propertyVersion: 2)).ConfigureAwait(false);
        RetentionContributionResult recovered =
            await ExecuteGuestRetentionAsync(
                worker,
                alias.TenantId,
                CreateGuestRetentionRequest(
                    worker,
                    alias.TenantId,
                    Guid.NewGuid())).ConfigureAwait(false);
        Assert.Equal(RetentionContributionStatus.Completed, recovered.Status);
        Assert.Equal(1, recovered.AffectedCount);
        await AssertTenantRetentionProofAsync(
            worker,
            alias.TenantId,
            alias.GuestId,
            new DateTimeOffset(
                2026,
                1,
                2,
                0,
                0,
                0,
                TimeSpan.Zero)).ConfigureAwait(false);

        const string retiredAliasTenant =
            "7d000000-0000-0000-0000-000000000017";
        Guid retiredAliasProperty = Guid.Parse(
            "7d100000-0000-0000-0000-000000000017");
        Guid retiredAliasGuest = Guid.Parse(
            "7d200000-0000-0000-0000-000000000017");
        await SeedGuestTimeZoneCandidateAsync(
            worker,
            retiredAliasTenant,
            retiredAliasProperty,
            retiredAliasGuest,
            "UTC",
            new DateOnly(2025, 1, 1)).ConfigureAwait(false);
        await ApplyGuestPropertyEventAsync(
            worker,
            retiredAliasTenant,
            new PropertyRetiredIntegrationEvent(
                Guid.NewGuid(),
                retiredAliasTenant,
                DateTimeOffset.UtcNow,
                retiredAliasProperty,
                propertyVersion: 3)).ConfigureAwait(false);
        PropertyGovernancePolicyBinding retiredAliasPolicy =
            await GetProjectedPolicyBindingAsync(
                worker,
                retiredAliasTenant,
                retiredAliasProperty).ConfigureAwait(false);
        await ApplyGuestPropertiesRebuildAsync(
            worker,
            retiredAliasTenant,
            new PropertyTopologyProjectionExport(
                retiredAliasTenant,
                retiredAliasProperty,
                "Retired Alias Hostel",
                "retired-alias-hostel",
                "UTC",
                PropertyStatus.Retired,
                version: 3,
                processingStatus: PropertyProcessingStatus.Enabled,
                governancePolicy: retiredAliasPolicy)).ConfigureAwait(false);
        await AssertProjectedClassificationAsync(
            worker,
            retiredAliasTenant,
            retiredAliasProperty,
            "UTC",
            PropertyTimeZoneStatus.Alias,
            "Etc/UTC",
            TimeZoneCatalog.Default.CatalogVersion,
            GuestPropertyTimeZoneEvidenceSource.Rebuild,
            sourceVersion: 3,
            topologyVersion: 3,
            propertyStatus: PropertyStatus.Retired).ConfigureAwait(false);
        RetentionContributionResult retiredAliasRecovered =
            await ExecuteGuestRetentionAsync(
                worker,
                retiredAliasTenant,
                CreateGuestRetentionRequest(
                    worker,
                    retiredAliasTenant,
                    Guid.NewGuid())).ConfigureAwait(false);
        Assert.Equal(
            RetentionContributionStatus.Completed,
            retiredAliasRecovered.Status);
        Assert.Equal(1, retiredAliasRecovered.AffectedCount);
        await AssertTenantRetentionProofAsync(
            worker,
            retiredAliasTenant,
            retiredAliasGuest,
            new DateTimeOffset(
                2026,
                1,
                2,
                0,
                0,
                0,
                TimeSpan.Zero)).ConfigureAwait(false);

        const string overflowTenant =
            "7d000000-0000-0000-0000-000000000016";
        Guid overflowProperty = Guid.Parse(
            "7d100000-0000-0000-0000-000000000016");
        Guid overflowGuest = Guid.Parse(
            "7d200000-0000-0000-0000-000000000016");
        await SeedGuestTimeZoneCandidateAsync(
            worker,
            overflowTenant,
            overflowProperty,
            overflowGuest,
            "Etc/UTC",
            new DateOnly(2025, 1, 1)).ConfigureAwait(false);
        await SeedOverflowAssociationsAsync(
            worker,
            overflowTenant,
            overflowGuest,
            GuestRetentionAnonymisationReceipt.MaximumAffectedProperties)
            .ConfigureAwait(false);
        RetentionContributionResult overflowResult =
            await ExecuteGuestRetentionAsync(
                worker,
                overflowTenant,
                CreateGuestRetentionRequest(
                    worker,
                    overflowTenant,
                    Guid.NewGuid())).ConfigureAwait(false);
        Assert.Equal(RetentionContributionStatus.Failed, overflowResult.Status);
        Assert.Equal(
            GuestProjectionUnavailableOutcome,
            overflowResult.OutcomeCode);
        await AssertTenantHasNoRetentionMutationAsync(
            worker,
            overflowTenant,
            overflowGuest).ConfigureAwait(false);

        const string acknowledgementOverflowTenant =
            "7d000000-0000-0000-0000-000000000018";
        Guid acknowledgementOverflowProperty = Guid.Parse(
            "7d100000-0000-0000-0000-000000000018");
        Guid acknowledgementOverflowGuest = Guid.Parse(
            "7d200000-0000-0000-0000-000000000018");
        await SeedGuestTimeZoneCandidateAsync(
            worker,
            acknowledgementOverflowTenant,
            acknowledgementOverflowProperty,
            acknowledgementOverflowGuest,
            "Etc/UTC",
            new DateOnly(2025, 1, 1)).ConfigureAwait(false);
        await SeedAcknowledgementOverflowAsync(
            worker,
            acknowledgementOverflowTenant,
            acknowledgementOverflowProperty).ConfigureAwait(false);
        RetentionContributionResult acknowledgementOverflowResult =
            await ExecuteGuestRetentionAsync(
                worker,
                acknowledgementOverflowTenant,
                CreateGuestRetentionRequest(
                    worker,
                    acknowledgementOverflowTenant,
                    Guid.NewGuid())).ConfigureAwait(false);
        Assert.Equal(
            RetentionContributionStatus.Failed,
            acknowledgementOverflowResult.Status);
        Assert.Equal(
            GuestPolicyUnavailableOutcome,
            acknowledgementOverflowResult.OutcomeCode);
        await AssertTenantHasNoRetentionMutationAsync(
            worker,
            acknowledgementOverflowTenant,
            acknowledgementOverflowGuest).ConfigureAwait(false);
    }

    private static void AssertNextLocalDay(
        string timeZoneId,
        DateOnly terminalDate,
        DateTimeOffset expectedUtc)
    {
        Assert.True(TimeZoneCalendarMath.TryGetStartOfNextLocalDay(
            terminalDate,
            timeZoneId,
            out DateTimeOffset actualUtc));
        Assert.Equal(expectedUtc, actualUtc);
    }

    private static DateTimeOffset WithSubMicrosecondTicks(
        DateTimeOffset value) =>
        value.AddTicks(
            -(value.Ticks % TimeSpan.TicksPerMicrosecond) + 7);

    private static async Task SeedGuestTimeZoneCandidateAsync(
        IHost worker,
        string tenantId,
        Guid propertyId,
        Guid guestId,
        string timeZoneId,
        DateOnly terminalDate)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        await ApplyGuestPropertyEventAsync(
            scope.ServiceProvider,
            new PropertyCreatedIntegrationEvent(
                Guid.NewGuid(),
                tenantId,
                nowUtc,
                propertyId,
                $"Time-zone Hostel {propertyId:N}",
                $"tz-{propertyId:N}",
                timeZoneId,
                PropertyStatus.Active,
                propertyVersion: 1)).ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            GuestsModuleMetadata.Name,
            tenantId,
            propertyId,
            propertyVersion: 2).ConfigureAwait(false);

        await SeedGuestCandidateRowsAsync(
            scope.ServiceProvider,
            tenantId,
            propertyId,
            guestId,
            terminalDate,
            nowUtc).ConfigureAwait(false);
    }

    private static async Task SeedGuestCandidateWithoutPropertyProjectionAsync(
        IHost worker,
        string tenantId,
        Guid propertyId,
        Guid guestId,
        DateOnly terminalDate)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        await SeedGuestCandidateRowsAsync(
            scope.ServiceProvider,
            tenantId,
            propertyId,
            guestId,
            terminalDate,
            DateTimeOffset.UtcNow).ConfigureAwait(false);
    }

    private static async Task SeedGuestCandidateRowsAsync(
        IServiceProvider services,
        string tenantId,
        Guid propertyId,
        Guid guestId,
        DateOnly terminalDate,
        DateTimeOffset nowUtc)
    {
        GuestProfile profile = GuestProfile.Create(
            guestId,
            tenantId,
            propertyId,
            $"Retention Candidate {guestId:N}",
            "Retention Candidate",
            $"{guestId:N}@example.test",
            "+44 20 7946 0958",
            new DateOnly(1990, 1, 1),
            "GB",
            "en-GB",
            "Sensitive retention integration note",
            "integration:retention-time-zone",
            Guid.NewGuid(),
            nowUtc.AddYears(-2)).Value;
        profile.ClearDomainEvents();
        GuestsDbContext guests = services
            .GetRequiredService<GuestsDbContext>();
        guests.GuestProfiles.Add(profile);
        guests.StayHistory.Add(new GuestStayHistoryEntry(
            tenantId,
            guestId,
            Guid.NewGuid(),
            propertyId,
            GuestStayRole.Primary,
            terminalDate.AddDays(-2),
            terminalDate,
            GuestStayStatus.CheckedOut,
            terminalDate.AddDays(-2),
            noShowBusinessDate: null,
            checkedOutBusinessDate: terminalDate,
            isCurrentParticipant: false,
            reservationVersion: 1,
            GuestsModuleMetadata.StayHistoryProjectionVersion));
        await guests.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SeedForeignScopeStayAsync(
        IHost worker,
        string foreignTenantId,
        Guid localGuestId,
        Guid propertyId,
        DateOnly foreignTerminalDate)
    {
        using IServiceScope scope = CreateTenantScope(
            worker,
            foreignTenantId);
        GuestsDbContext guests = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        await guests.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.stay_history (
                "ScopeId",
                "GuestId",
                "ReservationId",
                "PropertyId",
                "Role",
                "Arrival",
                "Departure",
                "Status",
                "CheckedInBusinessDate",
                "NoShowBusinessDate",
                "CheckedOutBusinessDate",
                "IsCurrentParticipant",
                "ReservationVersion",
                "ProjectionContractVersion")
            VALUES (
                {foreignTenantId},
                {localGuestId},
                {Guid.NewGuid()},
                {propertyId},
                {(int)GuestStayRole.Primary},
                {foreignTerminalDate.AddDays(-2)},
                {foreignTerminalDate},
                {(int)GuestStayStatus.CheckedOut},
                {foreignTerminalDate.AddDays(-2)},
                NULL,
                {foreignTerminalDate},
                FALSE,
                {1L},
                {GuestsModuleMetadata.StayHistoryProjectionVersion});
            """).ConfigureAwait(false);
    }

    private static RetentionContributionRequest CreateGuestRetentionRequest(
        IHost worker,
        string tenantId,
        Guid executionId)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        IRetentionExecutionContributor contributor = scope.ServiceProvider
            .GetServices<IRetentionExecutionContributor>()
            .Single(item =>
                item.Schedule.OwnerKey == GuestRetentionOwner &&
                item.Schedule.DataClassKey == GuestOperationalDataClass);
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        return new(
            RetentionExecutionContract.CurrentVersion,
            executionId,
            tenantId,
            PropertyId: null,
            contributor.Schedule.OwnerKey,
            contributor.Schedule.DataClassKey,
            contributor.Schedule.ExecutionPolicyVersion,
            Attempt: 1,
            startedAtUtc,
            startedAtUtc.AddMinutes(10));
    }

    private static async Task<RetentionContributionResult>
        ExecuteGuestRetentionAsync(
            IHost worker,
            string tenantId,
            RetentionContributionRequest request)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        IRetentionExecutionContributor contributor = scope.ServiceProvider
            .GetServices<IRetentionExecutionContributor>()
            .Single(item =>
                item.Schedule.OwnerKey == GuestRetentionOwner &&
                item.Schedule.DataClassKey == GuestOperationalDataClass);
        return await contributor.ExecuteAsync(
            request,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task AssertTenantRetentionProofAsync(
        IHost worker,
        string tenantId,
        Guid guestId,
        DateTimeOffset expectedDeadlineUtc)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        GuestsDbContext guests = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        GuestProfile profile = await guests.GuestProfiles
            .AsNoTracking()
            .SingleAsync(item => item.Id == guestId)
            .ConfigureAwait(false);
        Assert.Equal(GuestProfileState.Anonymised, profile.Status);
        GuestRetentionAnonymisationReceipt receipt = await guests
            .RetentionAnonymisationReceipts
            .AsNoTracking()
            .SingleAsync(item => item.GuestId == guestId)
            .ConfigureAwait(false);
        Assert.Equal(
            GuestRetentionAnonymisationReceipt.CurrentContractVersion,
            receipt.ContractVersion);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            receipt.TimeZoneCatalogVersion);
        Assert.Equal(expectedDeadlineUtc, receipt.RetentionDeadlineUtc);
        Assert.Single(await guests.AnonymisationTombstones
            .AsNoTracking()
            .Where(item => item.Id == guestId)
            .ToArrayAsync().ConfigureAwait(false));
        Gma.Framework.Messaging.Infrastructure.OutboxMessage outbox =
            Assert.Single(await guests.OutboxMessages
            .AsNoTracking()
            .Where(item =>
                item.ScopeId == tenantId &&
                item.EventType ==
                    typeof(GuestProfileAnonymisedIntegrationEvent).FullName)
            .ToArrayAsync().ConfigureAwait(false));
        Assert.Equal(receipt.EventId, outbox.Id);
        Assert.Equal(tenantId, outbox.ScopeId);
        using JsonDocument payload = JsonDocument.Parse(outbox.Payload);
        Assert.Equal(
            tenantId,
            payload.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal(
            guestId,
            payload.RootElement.GetProperty("guestId").GetGuid());

        DataRightsExportRecord export = CreateRetentionExportRecord(receipt);
        DataRightsExportField proof = Assert.Single(
            export.Fields,
            field => field.FieldId == "guests.retention-proof");
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            proof.Value.GetProperty("timeZoneCatalogVersion").GetString());
    }

    private static async Task AssertTenantHasNoRetentionMutationAsync(
        IHost worker,
        string tenantId,
        Guid guestId)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        GuestsDbContext guests = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        GuestProfile profile = await guests.GuestProfiles
            .AsNoTracking()
            .SingleAsync(item => item.Id == guestId)
            .ConfigureAwait(false);
        Assert.Equal(GuestProfileState.Active, profile.Status);
        Assert.Empty(await guests.RetentionAnonymisationReceipts
            .AsNoTracking()
            .ToArrayAsync().ConfigureAwait(false));
        Assert.Empty(await guests.AnonymisationTombstones
            .AsNoTracking()
            .ToArrayAsync().ConfigureAwait(false));
        Assert.Empty(await guests.OutboxMessages
            .AsNoTracking()
            .Where(item => item.ScopeId == tenantId)
            .ToArrayAsync().ConfigureAwait(false));
    }

    private static async Task ApplyGuestPropertyEventAsync<TEvent>(
        IHost worker,
        string tenantId,
        TEvent integrationEvent)
        where TEvent : IIntegrationEvent
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        await ApplyGuestPropertyEventAsync(
            scope.ServiceProvider,
            integrationEvent).ConfigureAwait(false);
    }

    private static async Task ApplyGuestPropertyEventAsync<TEvent>(
        IServiceProvider services,
        TEvent integrationEvent)
        where TEvent : IIntegrationEvent
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item =>
                item.ConsumerModule == GuestsModuleMetadata.Name &&
                item.EventType == typeof(TEvent));
        var handler = (IIntegrationEventHandler<TEvent>)services
            .GetRequiredService(subscription.HandlerType);
        GuestsDbContext guests = services
            .GetRequiredService<GuestsDbContext>();
        await using var transaction = await guests.Database
            .BeginTransactionAsync().ConfigureAwait(false);
        await handler.HandleAsync(
            integrationEvent,
            CancellationToken.None).ConfigureAwait(false);
        await guests.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        guests.ChangeTracker.Clear();
    }

    private static async Task ApplyGuestPropertiesRebuildAsync(
        IHost worker,
        string tenantId,
        PropertyTopologyProjectionExport snapshot)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        GuestsDbContext guests = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        await using var transaction = await guests.Database
            .BeginTransactionAsync().ConfigureAwait(false);
        TaskHandlerRegistration rebuildTask = scope.ServiceProvider
            .GetServices<TaskHandlerRegistration>()
            .Single(registration =>
                registration.ModuleName == GuestsModuleMetadata.Name &&
                registration.TaskName ==
                    RebuildGuestsPropertiesPayload.TaskName);
        object rebuildHandler = scope.ServiceProvider
            .GetRequiredService(rebuildTask.HandlerType);
        Assert.Equal(
            "RebuildGuestsPropertiesTaskHandler",
            rebuildHandler.GetType().Name);
        IGuestsPropertiesProjectionRebuildWriter writer =
            scope.ServiceProvider.GetRequiredService<
                IGuestsPropertiesProjectionRebuildWriter>();
        Assert.Equal(
            "GuestsPropertiesProjectionRebuildWriter",
            writer.GetType().Name);
        Assert.Equal(tenantId, snapshot.TenantId);
        ProjectionWriteResult result = await writer.WriteAsync(
            new ProjectionRebuildRequest(
                GuestsModuleMetadata.PropertiesProjectionName,
                GuestsModuleMetadata.PropertiesProjectionVersion,
                batchSize: 10),
            [snapshot],
            CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(1, result.WrittenCount);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task AssertProjectedTimeZoneAsync(
        IHost worker,
        string tenantId,
        Guid propertyId,
        string expectedTimeZoneId,
        GuestPropertyTimeZoneEvidenceSource expectedSource,
        long sourceVersion,
        long topologyVersion)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        GuestPropertyProjection projection = await scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>()
            .PropertyProjections
            .AsNoTracking()
            .SingleAsync(item => item.Id == propertyId)
            .ConfigureAwait(false);
        Assert.Equal(expectedTimeZoneId, projection.TimeZoneId);
        Assert.Equal(expectedTimeZoneId, projection.CanonicalTimeZoneId);
        Assert.Equal(PropertyTimeZoneStatus.Canonical, projection.TimeZoneStatus);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            projection.TimeZoneCatalogVersion);
        Assert.Equal(expectedSource, projection.TimeZoneEvidenceSource);
        Assert.Equal(sourceVersion, projection.TimeZoneEvidenceSourceVersion);
        Assert.Equal(topologyVersion, projection.TopologySourceVersion);
    }

    private static async Task AssertProjectedClassificationAsync(
        IHost worker,
        string tenantId,
        Guid propertyId,
        string expectedTimeZoneId,
        PropertyTimeZoneStatus status,
        string? canonicalTimeZoneId,
        string? catalogVersion,
        GuestPropertyTimeZoneEvidenceSource expectedSource,
        long sourceVersion,
        long topologyVersion,
        PropertyStatus propertyStatus)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        GuestPropertyProjection projection = await scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>()
            .PropertyProjections
            .AsNoTracking()
            .SingleAsync(item => item.Id == propertyId)
            .ConfigureAwait(false);
        Assert.Equal(expectedTimeZoneId, projection.TimeZoneId);
        Assert.Equal(status, projection.TimeZoneStatus);
        Assert.Equal(canonicalTimeZoneId, projection.CanonicalTimeZoneId);
        Assert.Equal(catalogVersion, projection.TimeZoneCatalogVersion);
        Assert.Equal(expectedSource, projection.TimeZoneEvidenceSource);
        Assert.Equal(sourceVersion, projection.TimeZoneEvidenceSourceVersion);
        Assert.Equal(topologyVersion, projection.TopologySourceVersion);
        Assert.Equal(propertyStatus, projection.Status);
    }

    private static async Task<PropertyGovernancePolicyBinding>
        GetProjectedPolicyBindingAsync(
            IHost worker,
            string tenantId,
            Guid propertyId)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        GuestPropertyProjection projection = await scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>()
            .PropertyProjections
            .AsNoTracking()
            .SingleAsync(item => item.Id == propertyId)
            .ConfigureAwait(false);
        return Assert.IsType<PropertyGovernancePolicyBinding>(
            projection.GovernancePolicy.ToContract());
    }

    private static async Task SeedOverflowAssociationsAsync(
        IHost worker,
        string tenantId,
        Guid guestId,
        int count)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        GuestsDbContext guests = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        DateOnly terminalDate = new(2025, 1, 1);
        for (int index = 0; index < count; index++)
        {
            guests.StayHistory.Add(new GuestStayHistoryEntry(
                tenantId,
                guestId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                GuestStayRole.Primary,
                terminalDate.AddDays(-2),
                terminalDate,
                GuestStayStatus.CheckedOut,
                terminalDate.AddDays(-2),
                noShowBusinessDate: null,
                checkedOutBusinessDate: terminalDate,
                isCurrentParticipant: false,
                reservationVersion: 1,
                GuestsModuleMetadata.StayHistoryProjectionVersion));
        }

        await guests.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SeedAcknowledgementOverflowAsync(
        IHost worker,
        string tenantId,
        Guid propertyId)
    {
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        GuestsDbContext guests = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        await guests.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.property_policy_acknowledgements (
                "ScopeId",
                "PropertyId",
                "AcknowledgementId",
                "AcknowledgementVersion")
            SELECT
                {tenantId},
                {propertyId},
                'retention-overflow-' || lpad(item::text, 3, '0'),
                1
            FROM generate_series(
                0,
                {PropertiesContractLimits.MaximumPolicyAcknowledgements})
                AS item;
            """).ConfigureAwait(false);
    }

    private static async Task AssertPostgreSqlCandidateScanBoundsAsync(
        IHost worker)
    {
        const string tenantId =
            "7d000000-0000-0000-0000-000000000019";
        const int candidateCount =
            GuestAnonymisationEligibilityContract.MaximumAffectedProperties +
            1;
        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        GuestsDbContext guests = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>();
        DateTimeOffset effectiveAtUtc = new(
            2020,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        DateTimeOffset activatedAtUtc = new(
            2025,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        PropertyGovernancePolicyBinding maximumPolicy = new(
            "GB",
            "retention-prefix-policy",
            policyVersion: 1,
            "retention-prefix-region",
            "retention-prefix-transfer",
            "retention-prefix-schedule",
            retentionPolicyVersion: 1,
            new string('a', 64),
            effectiveAtUtc,
            new DateTimeOffset(
                2100,
                1,
                1,
                0,
                0,
                0,
                TimeSpan.Zero),
            activatedAtUtc,
            Enumerable.Range(
                    0,
                    PropertiesContractLimits.MaximumPolicyAcknowledgements)
                .Select(index => new PropertyGovernanceAcknowledgement(
                    $"retention-prefix-ack-{index:D2}",
                    acknowledgementVersion: 1))
                .ToArray());

        for (int index = 0; index < candidateCount; index++)
        {
            Guid propertyId = Guid.Parse(
                $"7d1{index:X5}-0000-0000-0000-000000000019");
            Guid guestId = Guid.Parse(
                $"7d2{index:X5}-0000-0000-0000-000000000019");
            GuestProfile profile = GuestProfile.Create(
                guestId,
                tenantId,
                propertyId,
                $"Candidate {index:D3}",
                legalName: null,
                email: null,
                phone: null,
                dateOfBirth: null,
                nationalityCountryCode: null,
                preferredLanguageTag: null,
                notes: null,
                "integration:retention-prefix",
                Guid.NewGuid(),
                activatedAtUtc).Value;
            profile.ClearDomainEvents();
            var property = new GuestPropertyProjection(
                tenantId,
                propertyId,
                $"Candidate Property {index:D3}",
                "Etc/UTC",
                PropertyStatus.Active,
                version: 1);
            property.ApplyPolicy(
                PropertyProcessingStatus.Enabled,
                maximumPolicy,
                sourceVersion: 1);
            guests.GuestProfiles.Add(profile);
            guests.PropertyProjections.Add(property);
        }

        await guests.SaveChangesAsync().ConfigureAwait(false);
        guests.ChangeTracker.Clear();

        object repository = Activator.CreateInstance(
            typeof(GuestsDbContext).Assembly.GetType(
                "BunkFy.Modules.Guests.Persistence.Repositories." +
                "GuestRetentionCandidateRepository",
                throwOnError: true)!,
            guests)!;
        CandidateScanProof first = await InvokeCandidateScanAsync(
            repository,
            afterProjectionOrdinal: 0,
            limit: 1000,
            CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(
            GuestAnonymisationEligibilityContract.MaximumAffectedProperties,
            first.Candidates.Length);
        Assert.False(first.ReachedEnd);
        Assert.Equal(
            checked(
                GuestAnonymisationEligibilityContract
                    .MaximumAffectedProperties *
                PropertiesContractLimits.MaximumPolicyAcknowledgements),
            first.Candidates.Sum(GetCandidateAcknowledgementCount));

        long lastOrdinal = GetCandidateProjectionOrdinal(
            first.Candidates[^1]);
        CandidateScanProof second = await InvokeCandidateScanAsync(
            repository,
            lastOrdinal,
            limit: 1000,
            CancellationToken.None).ConfigureAwait(false);
        object finalCandidate = Assert.Single(second.Candidates);
        Assert.True(second.ReachedEnd);
        Assert.True(
            GetCandidateProjectionOrdinal(finalCandidate) > lastOrdinal);
        Assert.Equal(
            PropertiesContractLimits.MaximumPolicyAcknowledgements,
            GetCandidateAcknowledgementCount(finalCandidate));
    }

    private static async Task
        AssertPostPreflightAssociationGrowthFailsClosedAsync(IHost worker)
    {
        const string tenantId =
            "7d000000-0000-0000-0000-000000000020";
        Guid propertyId = Guid.Parse(
            "7d100000-0000-0000-0000-000000000020");
        Guid guestId = Guid.Parse(
            "7d200000-0000-0000-0000-000000000020");
        await SeedGuestTimeZoneCandidateAsync(
            worker,
            tenantId,
            propertyId,
            guestId,
            "Etc/UTC",
            new DateOnly(2025, 1, 1)).ConfigureAwait(false);
        await SeedOverflowAssociationsAsync(
            worker,
            tenantId,
            guestId,
            GuestAnonymisationEligibilityContract.MaximumAffectedProperties -
                1).ConfigureAwait(false);

        using IServiceScope scope = CreateTenantScope(worker, tenantId);
        string connectionString = scope.ServiceProvider
            .GetRequiredService<GuestsDbContext>()
            .Database
            .GetConnectionString()!;
        Guid racedPropertyId = Guid.Parse(
            "7d100000-0000-0000-0000-000000000021");
        var growth = new AssociationGrowthAfterPreflightInterceptor(
            connectionString,
            tenantId,
            guestId,
            racedPropertyId);
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(growth)
                .Options;
        await using var racedContext = new GuestsDbContext(
            options,
            new FixedScopeContext(tenantId));
        object repository = Activator.CreateInstance(
            typeof(GuestsDbContext).Assembly.GetType(
                "BunkFy.Modules.Guests.Persistence.Repositories." +
                "GuestRetentionCandidateRepository",
                throwOnError: true)!,
            racedContext)!;

        CandidateScanProof page = await InvokeCandidateScanAsync(
            repository,
            afterProjectionOrdinal: 0,
            limit: 1,
            CancellationToken.None).ConfigureAwait(false);

        object candidate = Assert.Single(page.Candidates);
        Assert.True(growth.Inserted);
        Assert.True(GetCandidateAssociationOverflowed(candidate));
        Assert.Empty(GetCandidateCollection(candidate, "Stays"));
        Assert.Empty(GetCandidateCollection(candidate, "ActiveHolds"));
        Assert.Empty(GetCandidateCollection(candidate, "Properties"));

        RetentionContributionResult refusal = await ExecuteGuestRetentionAsync(
            worker,
            tenantId,
            CreateGuestRetentionRequest(
                worker,
                tenantId,
                Guid.NewGuid())).ConfigureAwait(false);
        Assert.Equal(RetentionContributionStatus.Failed, refusal.Status);
        Assert.Equal(GuestProjectionUnavailableOutcome, refusal.OutcomeCode);
        await AssertTenantHasNoRetentionMutationAsync(
            worker,
            tenantId,
            guestId).ConfigureAwait(false);
    }

    private static async Task<CandidateScanProof> InvokeCandidateScanAsync(
        object repository,
        long afterProjectionOrdinal,
        int limit,
        CancellationToken cancellationToken)
    {
        object invocation = repository.GetType()
            .GetMethod("ScanAsync")!
            .Invoke(
                repository,
                [afterProjectionOrdinal, limit, cancellationToken])!;
        Task task = Assert.IsType<Task>(invocation, exactMatch: false);
        await task.ConfigureAwait(false);
        object page = task.GetType().GetProperty("Result")!
            .GetValue(task)!;
        object[] candidates = ((System.Collections.IEnumerable)page
                .GetType()
                .GetProperty("Candidates")!
                .GetValue(page)!)
            .Cast<object>()
            .ToArray();
        bool reachedEnd = Assert.IsType<bool>(page
            .GetType()
            .GetProperty("ReachedEnd")!
            .GetValue(page));
        return new(candidates, reachedEnd);
    }

    private static long GetCandidateProjectionOrdinal(object candidate) =>
        Assert.IsType<long>(candidate
            .GetType()
            .GetProperty("ProjectionOrdinal")!
            .GetValue(candidate));

    private static bool GetCandidateAssociationOverflowed(object candidate) =>
        Assert.IsType<bool>(candidate
            .GetType()
            .GetProperty("AssociationOverflowed")!
            .GetValue(candidate));

    private static object[] GetCandidateCollection(
        object candidate,
        string propertyName) =>
        ((System.Collections.IEnumerable)candidate
                .GetType()
                .GetProperty(propertyName)!
                .GetValue(candidate)!)
            .Cast<object>()
            .ToArray();

    private static int GetCandidateAcknowledgementCount(object candidate)
    {
        object[] properties = ((System.Collections.IEnumerable)candidate
                .GetType()
                .GetProperty("Properties")!
                .GetValue(candidate)!)
            .Cast<object>()
            .ToArray();
        object property = Assert.Single(properties);
        PropertyGovernancePolicyBinding policy =
            Assert.IsType<PropertyGovernancePolicyBinding>(property
                .GetType()
                .GetProperty("GovernancePolicy")!
                .GetValue(property));
        return policy.Acknowledgements.Count;
    }

    private static IServiceScope CreateTenantScope(
        IHost worker,
        string tenantId)
    {
        IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(tenantId);
        return scope;
    }

    private static DataRightsExportRecord CreateRetentionExportRecord(
        GuestRetentionAnonymisationReceipt receipt) =>
        GuestsTenantTerminationExportSchema.CreateRecord(
            GuestsTenantTerminationMetadata
                .RetentionAnonymisationReceiptRecordType,
            receipt.Id,
            receipt.ResultingGuestVersion,
            new GuestRetentionAnonymisationReceiptTenantExport(
                receipt.ScopeId,
                receipt.GuestId,
                receipt.Id,
                new GuestRetentionAnonymisationProofTenantExport(
                    receipt.ContractVersion,
                    receipt.ExecutionId,
                    receipt.SelectedGuestVersion,
                    receipt.ResultingGuestVersion,
                    receipt.AffectedPropertyCount,
                    receipt.RetentionDeadlineUtc,
                    receipt.PolicySetSha256,
                    receipt.TimeZoneCatalogVersion,
                    receipt.EventId,
                    receipt.CompletedAtUtc,
                    receipt.CanonicalSha256),
                new GuestActorStaffTenantExport(receipt.ActorId)));

    private sealed record TimeZoneRefusalCase(
        string TenantId,
        Guid PropertyId,
        Guid GuestId,
        string TimeZoneId,
        DateOnly TerminalDate);

    private sealed record CandidateScanProof(
        object[] Candidates,
        bool ReachedEnd);

    private sealed class FixedScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class MutableSubMicrosecondClock(DateTimeOffset utcNow)
        : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class AssociationGrowthAfterPreflightInterceptor(
        string connectionString,
        string tenantId,
        Guid guestId,
        Guid propertyId) : DbCommandInterceptor
    {
        private int inserted;

        public bool Inserted => Volatile.Read(ref this.inserted) == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!IsAssociationCountQuery(command.CommandText) ||
                Interlocked.CompareExchange(ref this.inserted, 1, 0) != 0)
            {
                return result;
            }

            await using var connection = new NpgsqlConnection(
                connectionString);
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO guests.stay_history (
                    "ScopeId",
                    "GuestId",
                    "ReservationId",
                    "PropertyId",
                    "Role",
                    "Arrival",
                    "Departure",
                    "Status",
                    "CheckedInBusinessDate",
                    "NoShowBusinessDate",
                    "CheckedOutBusinessDate",
                    "IsCurrentParticipant",
                    "ReservationVersion",
                    "ProjectionContractVersion")
                VALUES (
                    @scope_id,
                    @guest_id,
                    @reservation_id,
                    @property_id,
                    @role,
                    @arrival,
                    @departure,
                    @status,
                    @checked_in_business_date,
                    NULL,
                    @checked_out_business_date,
                    FALSE,
                    1,
                    @projection_contract_version);
                """;
            DateOnly terminalDate = new(2025, 1, 1);
            insert.Parameters.AddWithValue("scope_id", tenantId);
            insert.Parameters.AddWithValue("guest_id", guestId);
            insert.Parameters.AddWithValue("reservation_id", Guid.NewGuid());
            insert.Parameters.AddWithValue("property_id", propertyId);
            insert.Parameters.AddWithValue("role", (int)GuestStayRole.Primary);
            insert.Parameters.AddWithValue(
                "arrival",
                terminalDate.AddDays(-2));
            insert.Parameters.AddWithValue("departure", terminalDate);
            insert.Parameters.AddWithValue(
                "status",
                (int)GuestStayStatus.CheckedOut);
            insert.Parameters.AddWithValue(
                "checked_in_business_date",
                terminalDate.AddDays(-2));
            insert.Parameters.AddWithValue(
                "checked_out_business_date",
                terminalDate);
            insert.Parameters.AddWithValue(
                "projection_contract_version",
                GuestsModuleMetadata.StayHistoryProjectionVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            return result;
        }

        private static bool IsAssociationCountQuery(string commandText) =>
            commandText.Contains(
                "guest_profiles",
                StringComparison.OrdinalIgnoreCase) &&
            commandText.Contains(
                "stay_history",
                StringComparison.OrdinalIgnoreCase) &&
            commandText.Contains(
                "data_holds",
                StringComparison.OrdinalIgnoreCase) &&
            commandText.Contains(
                "GROUP BY",
                StringComparison.OrdinalIgnoreCase);
    }
}

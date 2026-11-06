namespace BunkFy.Modules.Guests.Tests.Persistence;

using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Guests.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestAnonymisationExecutionBoundaryTests
{
    [Fact]
    public async Task Acquire_rejects_a_tenant_other_than_the_active_scope()
    {
        await using GuestsDbContext dbContext = new(
            CreateOptions(),
            new TestScopeContext("tenant-a"));
        RecordingOperationLock operationLock = new();
        GuestAnonymisationExecutionBoundary boundary = new(
            dbContext,
            operationLock);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                boundary.AcquireAsync(
                    "tenant-b",
                    Guid.NewGuid(),
                    CancellationToken.None));

        Assert.Equal(
            "A Guest anonymisation execution boundary requires the active tenant scope.",
            failure.Message);
        Assert.Empty(operationLock.GuestAcquisitions);
        Assert.Empty(operationLock.PropertyAcquisitions);
    }

    [Fact]
    public async Task Acquire_does_not_lock_foreign_stay_or_hold_properties()
    {
        DbContextOptions<GuestsDbContext> options = CreateOptions();
        Guid guestId = Guid.NewGuid();
        Guid originPropertyId = Guid.NewGuid();
        Guid foreignStayPropertyId = Guid.NewGuid();
        Guid foreignHoldPropertyId = Guid.NewGuid();
        DateTimeOffset now =
            new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        await using (GuestsDbContext tenantA = new(
            options,
            new TestScopeContext("tenant-a")))
        {
            tenantA.GuestProfiles.Add(GuestProfile.Create(
                guestId,
                "tenant-a",
                originPropertyId,
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
                now.AddYears(-2)).Value);
            await tenantA.SaveChangesAsync();
        }

        await using (GuestsDbContext tenantB = new(
            options,
            new TestScopeContext("tenant-b")))
        {
            tenantB.StayHistory.Add(new(
                "tenant-b",
                guestId,
                Guid.NewGuid(),
                foreignStayPropertyId,
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
                foreignHoldPropertyId,
                guestId,
                "foreign-hold",
                "user:owner",
                now).Value);
            await tenantB.SaveChangesAsync();
        }

        await using GuestsDbContext dbContext = new(
            options,
            new TestScopeContext("tenant-a"));
        RecordingOperationLock operationLock = new();

        await new GuestAnonymisationExecutionBoundary(
                dbContext,
                operationLock)
            .AcquireAsync(
                "tenant-a",
                guestId,
                CancellationToken.None);

        Assert.Equal(
            [("tenant-a", guestId)],
            operationLock.GuestAcquisitions);
        (string TenantId, Guid[] PropertyIds) propertyAcquisition =
            Assert.Single(operationLock.PropertyAcquisitions);
        Assert.Equal("tenant-a", propertyAcquisition.TenantId);
        Assert.Equal([originPropertyId], propertyAcquisition.PropertyIds);
    }

    private static DbContextOptions<GuestsDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<GuestsDbContext>()
            .UseInMemoryDatabase(
                $"guest-anonymisation-boundary-{Guid.NewGuid():N}")
            .Options;

    private sealed class RecordingOperationLock : IGuestOperationLock
    {
        public List<(string TenantId, Guid GuestId)> GuestAcquisitions { get; } = [];

        public List<(string TenantId, Guid[] PropertyIds)> PropertyAcquisitions { get; } = [];

        public Task AcquireGuestAsync(
            string tenantId,
            Guid guestId,
            CancellationToken cancellationToken)
        {
            this.GuestAcquisitions.Add((tenantId, guestId));
            return Task.CompletedTask;
        }

        public Task AcquirePropertiesAsync(
            string tenantId,
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken cancellationToken)
        {
            this.PropertyAcquisitions.Add((tenantId, propertyIds.ToArray()));
            return Task.CompletedTask;
        }
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }
}

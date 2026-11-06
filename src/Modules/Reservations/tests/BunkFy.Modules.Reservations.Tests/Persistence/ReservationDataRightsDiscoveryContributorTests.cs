namespace BunkFy.Modules.Reservations.Tests.Persistence;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Reservations.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationDataRightsDiscoveryContributorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Staff_scope_and_account_subject_lookup_are_not_owned_by_reservations()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        ReservationDataRightsDiscoveryContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));

        DataRightsSubjectDiscoveryResult result = await contributor.DiscoverAsync(
            new DataRightsSubjectDiscoveryRequest(
                "tenant-a",
                DataRightsCaseType.StaffRights,
                PropertyId: null,
                new DataRightsSubjectLookup(
                    RecordId: null,
                    Email: null,
                    Phone: null,
                    Name: null,
                    DateOfBirth: null,
                    AccountSubjectId: "account-subject-123"),
                DataRightsSubjectDiscoveryLimits.MaxCandidates),
            CancellationToken.None);

        Assert.Equal([DataRightsCaseType.GuestRights], contributor.SupportedCaseTypes);
        Assert.Equal(DataRightsSubjectDiscoveryStatus.ScopeUnavailable, result.Status);
    }

    [Fact]
    public async Task Exact_current_email_finds_a_reservation_at_a_retired_property()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext, isActive: false);
        Reservation reservation = CreateReservation(
            propertyId,
            "Maya Chen",
            "maya.chen@example.test",
            "+44 20 1234 5678");
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        ReservationDataRightsDiscoveryContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));

        DataRightsSubjectDiscoveryResult result = await contributor.DiscoverAsync(
            new(
                "tenant-a",
                DataRightsCaseType.GuestRights,
                propertyId,
                new(null, " MAYA.CHEN@EXAMPLE.TEST ", null, " maya chen ", null),
                DataRightsSubjectDiscoveryLimits.MaxCandidates),
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectDiscoveryStatus.Succeeded, result.Status);
        DataRightsSubjectCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(reservation.Id, candidate.Coordinate.RecordId);
        Assert.Equal(reservation.Version, candidate.Coordinate.RecordVersion);
        Assert.Equal("Maya Chen", candidate.DisplayName);
        Assert.Equal("m***@example.test", candidate.EmailHint);
        Assert.Equal("***5678", candidate.PhoneHint);
    }

    [Fact]
    public async Task Exact_pending_phone_uses_the_pending_subject_preview()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext, isActive: true);
        Reservation reservation = CreateReservation(
            propertyId,
            "Original Guest",
            "original@example.test",
            "+1 555 0100");
        Assert.True(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(reservation.BeginAllocationAmendment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', Reservation.RequestFingerprintLength),
            reservation.Arrival,
            reservation.Departure.AddDays(1),
            reservation.RequestedUnits.Select(unit => unit.InventoryUnitId).ToArray(),
            "Pending Guest",
            "pending@example.test",
            "+44 20 9999 4321",
            reservation.GuestCount,
            reservation.Notes,
            reservation.DetailsRevision,
            ReservationDetailsChangeOrigin.Adapter,
            "adapter:test",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        ReservationDataRightsDiscoveryContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));

        DataRightsSubjectDiscoveryResult result = await contributor.DiscoverAsync(
            new(
                "tenant-a",
                DataRightsCaseType.GuestRights,
                propertyId,
                new(null, null, " +44 20 9999 4321 ", "pending guest", null),
                DataRightsSubjectDiscoveryLimits.MaxCandidates),
            CancellationToken.None);

        DataRightsSubjectCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal("Pending Guest", candidate.DisplayName);
        Assert.Equal("p***@example.test", candidate.EmailHint);
        Assert.Equal("***4321", candidate.PhoneHint);
    }

    [Fact]
    public async Task Discovery_is_bounded_and_rejects_weak_or_unknown_scope()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext, isActive: true);
        for (int index = 0; index < 25; index++)
        {
            dbContext.Reservations.Add(CreateReservation(
                propertyId,
                $"Guest {index:D2}",
                "shared@example.test",
                null));
        }

        await dbContext.SaveChangesAsync();
        ReservationDataRightsDiscoveryContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));

        DataRightsSubjectDiscoveryResult bounded = await contributor.DiscoverAsync(
            new(
                "tenant-a",
                DataRightsCaseType.GuestRights,
                propertyId,
                new(null, "shared@example.test", null, null, null),
                DataRightsSubjectDiscoveryLimits.MaxCandidates),
            CancellationToken.None);
        DataRightsSubjectDiscoveryResult nameOnly = await contributor.DiscoverAsync(
            new(
                "tenant-a",
                DataRightsCaseType.GuestRights,
                propertyId,
                new(null, null, null, "Guest 01", null),
                DataRightsSubjectDiscoveryLimits.MaxCandidates),
            CancellationToken.None);
        DataRightsSubjectDiscoveryResult multipleCoordinates = await contributor.DiscoverAsync(
            new(
                "tenant-a",
                DataRightsCaseType.GuestRights,
                propertyId,
                new(null, "shared@example.test", "+1 555", null, null),
                DataRightsSubjectDiscoveryLimits.MaxCandidates),
            CancellationToken.None);
        DataRightsSubjectDiscoveryResult wrongTenant = await contributor.DiscoverAsync(
            new(
                "tenant-b",
                DataRightsCaseType.GuestRights,
                propertyId,
                new(null, "shared@example.test", null, null, null),
                DataRightsSubjectDiscoveryLimits.MaxCandidates),
            CancellationToken.None);
        DataRightsSubjectDiscoveryResult unknownProperty = await contributor.DiscoverAsync(
            new(
                "tenant-a",
                DataRightsCaseType.GuestRights,
                Guid.NewGuid(),
                new(null, "shared@example.test", null, null, null),
                DataRightsSubjectDiscoveryLimits.MaxCandidates),
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectDiscoveryLimits.MaxCandidates, bounded.Candidates.Count);
        Assert.Equal(
            bounded.Candidates.OrderBy(candidate => candidate.Coordinate.RecordId)
                .Select(candidate => candidate.Coordinate.RecordId),
            bounded.Candidates.Select(candidate => candidate.Coordinate.RecordId));
        Assert.Equal(DataRightsSubjectDiscoveryStatus.ScopeUnavailable, nameOnly.Status);
        Assert.Equal(DataRightsSubjectDiscoveryStatus.ScopeUnavailable, multipleCoordinates.Status);
        Assert.Equal(DataRightsSubjectDiscoveryStatus.ScopeUnavailable, wrongTenant.Status);
        Assert.Equal(DataRightsSubjectDiscoveryStatus.ScopeUnavailable, unknownProperty.Status);
    }

    [Fact]
    public async Task Selection_revalidation_rejects_stale_and_cross_property_coordinates()
    {
        await using ReservationsDbContext dbContext = CreateDbContext("tenant-a");
        Guid propertyId = AddKnownProperty(dbContext, isActive: true);
        Guid otherPropertyId = AddKnownProperty(dbContext, isActive: true);
        Reservation reservation = CreateReservation(
            propertyId,
            "Guest",
            "guest@example.test",
            null);
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        ReservationDataRightsDiscoveryContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));

        DataRightsSubjectSelectionValidation valid = await contributor.ValidateSelectionAsync(
            new(
                "tenant-a",
                DataRightsCaseType.GuestRights,
                propertyId,
                new(
                    ReservationDataRightsDiscoveryContributor.Owner,
                    ReservationDataRightsDiscoveryContributor.ReservationRecordType,
                    reservation.Id,
                    reservation.Version)),
            CancellationToken.None);
        DataRightsSubjectSelectionValidation stale = await contributor.ValidateSelectionAsync(
            new(
                "tenant-a",
                DataRightsCaseType.GuestRights,
                propertyId,
                new(
                    ReservationDataRightsDiscoveryContributor.Owner,
                    ReservationDataRightsDiscoveryContributor.ReservationRecordType,
                    reservation.Id,
                    reservation.Version + 1)),
            CancellationToken.None);
        DataRightsSubjectSelectionValidation otherProperty =
            await contributor.ValidateSelectionAsync(
                new(
                    "tenant-a",
                    DataRightsCaseType.GuestRights,
                    otherPropertyId,
                    new(
                        ReservationDataRightsDiscoveryContributor.Owner,
                        ReservationDataRightsDiscoveryContributor.ReservationRecordType,
                        reservation.Id,
                        reservation.Version)),
                CancellationToken.None);

        Assert.Equal(DataRightsSubjectSelectionValidationStatus.Valid, valid.Status);
        Assert.Equal(DataRightsSubjectSelectionValidationStatus.Stale, stale.Status);
        Assert.Equal(DataRightsSubjectSelectionValidationStatus.NotFound, otherProperty.Status);
    }

    private static Reservation CreateReservation(
        Guid propertyId,
        string primaryGuestName,
        string? email,
        string? phone) =>
        Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            Guid.NewGuid(),
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [Guid.NewGuid()],
            primaryGuestName,
            email,
            phone,
            guestCount: 1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: "Late arrival",
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: null,
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            Now).Value;

    private static Guid AddKnownProperty(
        ReservationsDbContext dbContext,
        bool isActive)
    {
        Guid propertyId = Guid.NewGuid();
        ReservationPropertyProjection property =
            ReservationPropertyProjection.Create(propertyId, "tenant-a");
        property.ApplyTopology("UTC", isActive, sourceVersion: 1);
        dbContext.PropertyProjections.Add(property);
        return propertyId;
    }

    private static ReservationsDbContext CreateDbContext(string tenantId)
    {
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseInMemoryDatabase($"reservations-data-rights-{Guid.NewGuid():N}")
                .Options;
        return new(options, new TestScopeContext(tenantId));
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}

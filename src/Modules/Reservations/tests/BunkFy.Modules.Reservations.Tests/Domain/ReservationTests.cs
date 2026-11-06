namespace BunkFy.Modules.Reservations.Tests;

using Gma.Framework.Results;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Errors;
using BunkFy.Modules.Reservations.Domain.Events;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationTests
{
    private static readonly Guid PropertyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid UnitId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid RequestId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_pending_and_raises_allocation_request_fact()
    {
        Reservation reservation = CreateReservation().Value;

        Assert.Equal(ReservationState.PendingAllocation, reservation.Status);
        Assert.Null(reservation.TerminalAtUtc);
        Assert.Equal(1, reservation.Version);
        Assert.Equal(1, reservation.DetailsRevision);
        Assert.Equal(ReservationDetailsChangeOrigin.Staff, reservation.LastDetailsChangeOrigin);
        Assert.Equal(UnitId, Assert.Single(reservation.RequestedUnits).InventoryUnitId);
        ReservationCreatedDomainEvent domainEvent =
            Assert.IsType<ReservationCreatedDomainEvent>(reservation.DomainEvents.Single(item => item is ReservationCreatedDomainEvent));
        Assert.Equal(RequestId, domainEvent.AllocationRequestId);
        ReservationDetailsChangedDomainEvent details =
            Assert.IsType<ReservationDetailsChangedDomainEvent>(reservation.DomainEvents.Single(item => item is ReservationDetailsChangedDomainEvent));
        Assert.Equal(0, details.FromRevision);
        Assert.Equal(1, details.ToRevision);
        Assert.Null(details.Before);
    }

    [Fact]
    public void Only_correlated_inventory_confirmation_can_confirm()
    {
        Reservation reservation = CreateReservation().Value;
        Guid allocationId = Guid.NewGuid();

        Assert.Equal(
            ReservationsDomainErrors.AllocationCorrelationMismatch,
            reservation.ConfirmAllocation(Guid.NewGuid(), allocationId, 1, Guid.NewGuid(), Now).Error);
        Assert.True(reservation.ConfirmAllocation(RequestId, allocationId, 1, Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(ReservationState.Confirmed, reservation.Status);
        Assert.Equal(allocationId, reservation.AllocationId);
        Assert.Equal(2, reservation.Version);
    }

    [Fact]
    public void Allocation_rejection_is_terminal_for_the_initial_attempt()
    {
        Reservation reservation = CreateReservation().Value;

        Assert.True(reservation.RejectAllocation(
            RequestId,
            ReservationAllocationRejection.AllocationConflict,
            Guid.NewGuid(),
            Now).IsSuccess);

        Assert.Equal(ReservationState.AllocationRejected, reservation.Status);
        Assert.Equal(Now, reservation.TerminalAtUtc);
        Assert.Equal(ReservationAllocationRejection.AllocationConflict, reservation.AllocationRejection);
        Assert.Equal(
            ReservationsDomainErrors.InvalidTransition,
            reservation.ConfirmAllocation(RequestId, Guid.NewGuid(), 1, Guid.NewGuid(), Now).Error);
    }

    [Fact]
    public void Confirmed_cancellation_waits_for_correlated_release()
    {
        Reservation reservation = CreateReservation().Value;
        reservation.ConfirmAllocation(RequestId, Guid.NewGuid(), 1, Guid.NewGuid(), Now);
        reservation.ClearDomainEvents();
        Guid releaseRequestId = Guid.NewGuid();

        Assert.True(reservation.RequestCancellation(2, releaseRequestId, Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(ReservationState.CancellationPending, reservation.Status);
        Assert.IsType<ReservationCancellationRequestedDomainEvent>(Assert.Single(reservation.DomainEvents));
        Assert.Equal(
            ReservationsDomainErrors.AllocationCorrelationMismatch,
            reservation.CompleteAllocationRelease(Guid.NewGuid(), Guid.NewGuid(), Now).Error);
        Assert.Equal(
            ReservationReleaseCompletion.Cancelled,
            reservation.CompleteAllocationRelease(releaseRequestId, Guid.NewGuid(), Now).Value);
        Assert.Equal(ReservationState.Cancelled, reservation.Status);
        Assert.Equal(Now, reservation.TerminalAtUtc);
        Assert.IsType<ReservationCancelledDomainEvent>(reservation.DomainEvents.Last());
        long completedVersion = reservation.Version;
        Assert.Equal(
            ReservationReleaseCompletion.Cancelled,
            reservation.CompleteAllocationRelease(releaseRequestId, Guid.NewGuid(), Now).Value);
        Assert.Equal(completedVersion, reservation.Version);
        Assert.Equal(
            ReservationsDomainErrors.AllocationCorrelationMismatch,
            reservation.CompleteAllocationRelease(Guid.NewGuid(), Guid.NewGuid(), Now).Error);
    }

    [Fact]
    public void Check_in_and_checkout_preserve_business_date_actor_and_release_correlation()
    {
        Reservation reservation = CreateReservation().Value;
        Guid allocationId = Guid.NewGuid();
        reservation.ConfirmAllocation(RequestId, allocationId, 1, Guid.NewGuid(), Now);
        reservation.ClearDomainEvents();
        DateOnly checkInDate = new(2026, 8, 2);

        Assert.True(reservation.CheckIn(2, checkInDate, " staff:front-desk ", Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(ReservationState.CheckedIn, reservation.Status);
        Assert.Equal(checkInDate, reservation.CheckedInBusinessDate);
        Assert.Equal("staff:front-desk", reservation.CheckedInBy);
        Assert.IsType<ReservationCheckedInDomainEvent>(Assert.Single(reservation.DomainEvents));

        reservation.ClearDomainEvents();
        Guid rejectedRelease = Guid.NewGuid();
        Assert.True(reservation.RequestCheckout(
            3, checkInDate, "staff:front-desk", rejectedRelease, Guid.NewGuid(), Now.AddHours(1)).IsSuccess);
        Assert.Equal(ReservationState.CheckoutPending, reservation.Status);
        Assert.IsType<ReservationCheckoutRequestedDomainEvent>(Assert.Single(reservation.DomainEvents));
        Assert.True(reservation.RestoreAfterReleaseRejection(
            rejectedRelease,
            rejectionCode: 2,
            eventId: Guid.NewGuid(),
            nowUtc: Now.AddHours(2)).IsSuccess);
        Assert.Equal(ReservationState.CheckedIn, reservation.Status);
        Assert.Null(reservation.PendingStayBusinessDate);
        Assert.Equal(2, reservation.LastReleaseRejectionCode);

        reservation.ClearDomainEvents();
        Guid releaseRequestId = Guid.NewGuid();
        DateOnly checkoutDate = new(2026, 8, 3);
        Assert.True(reservation.RequestCheckout(
            5, checkoutDate, "staff:supervisor", releaseRequestId, Guid.NewGuid(), Now.AddHours(3)).IsSuccess);
        Assert.Equal(
            ReservationReleaseCompletion.CheckedOut,
            reservation.CompleteAllocationRelease(
                releaseRequestId, Guid.NewGuid(), Now.AddHours(4)).Value);
        Assert.Equal(ReservationState.CheckedOut, reservation.Status);
        Assert.Equal(Now.AddHours(4), reservation.TerminalAtUtc);
        Assert.Equal(checkoutDate, reservation.CheckedOutBusinessDate);
        Assert.Equal("staff:supervisor", reservation.CheckedOutBy);
        Assert.IsType<ReservationCheckedOutDomainEvent>(reservation.DomainEvents.Last());
    }

    [Fact]
    public void No_show_waits_for_release_and_rejects_pre_arrival_business_date()
    {
        Reservation reservation = CreateReservation().Value;
        reservation.ConfirmAllocation(RequestId, Guid.NewGuid(), 1, Guid.NewGuid(), Now);
        reservation.ClearDomainEvents();

        Assert.Equal(
            ReservationsDomainErrors.StayBusinessDateInvalid,
            reservation.RequestNoShow(
                2,
                new DateOnly(2026, 7, 31),
                "staff:front-desk",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now).Error);

        Guid releaseRequestId = Guid.NewGuid();
        DateOnly businessDate = new(2026, 8, 1);
        Assert.True(reservation.RequestNoShow(
            2, businessDate, "staff:front-desk", releaseRequestId, Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(ReservationState.NoShowPending, reservation.Status);
        Assert.IsType<ReservationNoShowRequestedDomainEvent>(Assert.Single(reservation.DomainEvents));

        reservation.ClearDomainEvents();
        Assert.Equal(
            ReservationReleaseCompletion.NoShow,
            reservation.CompleteAllocationRelease(releaseRequestId, Guid.NewGuid(), Now.AddMinutes(1)).Value);
        Assert.Equal(ReservationState.NoShow, reservation.Status);
        Assert.Equal(Now.AddMinutes(1), reservation.TerminalAtUtc);
        Assert.Equal(businessDate, reservation.NoShowBusinessDate);
        Assert.Equal("staff:front-desk", reservation.NoShowBy);
        Assert.IsType<ReservationNoShowDomainEvent>(Assert.Single(reservation.DomainEvents));
    }

    [Fact]
    public void Stay_transitions_reject_stale_versions_invalid_dates_actors_and_states()
    {
        Reservation reservation = CreateReservation().Value;
        reservation.ConfirmAllocation(RequestId, Guid.NewGuid(), 1, Guid.NewGuid(), Now);

        Assert.Equal(
            ReservationsDomainErrors.VersionConflict,
            reservation.CheckIn(1, new DateOnly(2026, 8, 1), "staff:1", Guid.NewGuid(), Now).Error);
        Assert.Equal(
            ReservationsDomainErrors.StayBusinessDateInvalid,
            reservation.CheckIn(2, new DateOnly(2026, 8, 3), "staff:1", Guid.NewGuid(), Now).Error);
        Assert.Equal(
            ReservationsDomainErrors.StayProvenanceInvalid,
            reservation.CheckIn(2, new DateOnly(2026, 8, 1), " ", Guid.NewGuid(), Now).Error);
        Assert.True(reservation.CheckIn(
            2, new DateOnly(2026, 8, 2), "staff:1", Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(
            ReservationsDomainErrors.StayBusinessDateInvalid,
            reservation.RequestCheckout(
                3,
                new DateOnly(2026, 8, 1),
                "staff:1",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now).Error);
        Assert.Equal(
            ReservationsDomainErrors.InvalidTransition,
            reservation.RequestCancellation(3, Guid.NewGuid(), Guid.NewGuid(), Now).Error);
        Assert.Equal(
            ReservationsDomainErrors.InvalidTransition,
            reservation.RequestNoShow(
                3,
                new DateOnly(2026, 8, 2),
                "staff:1",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now).Error);
    }

    [Fact]
    public void Pending_cancellation_compensates_a_late_confirmation()
    {
        Reservation reservation = CreateReservation().Value;
        reservation.ClearDomainEvents();
        Guid releaseRequestId = Guid.NewGuid();

        Assert.True(reservation.RequestCancellation(1, releaseRequestId, Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(ReservationState.CancellationPending, reservation.Status);
        Assert.Empty(reservation.DomainEvents);

        Assert.True(reservation.ConfirmAllocation(
            RequestId,
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            Now).IsSuccess);

        ReservationCancellationRequestedDomainEvent release =
            Assert.IsType<ReservationCancellationRequestedDomainEvent>(Assert.Single(reservation.DomainEvents));
        Assert.Equal(releaseRequestId, release.ReleaseRequestId);
    }

    [Fact]
    public void Pending_cancellation_finishes_locally_when_allocation_is_rejected()
    {
        Reservation reservation = CreateReservation().Value;
        reservation.ClearDomainEvents();
        reservation.RequestCancellation(1, Guid.NewGuid(), Guid.NewGuid(), Now);

        Assert.True(reservation.RejectAllocation(
            RequestId,
            ReservationAllocationRejection.AllocationConflict,
            Guid.NewGuid(),
            Now).IsSuccess);

        Assert.Equal(ReservationState.Cancelled, reservation.Status);
        Assert.Equal(Now, reservation.TerminalAtUtc);
        Assert.IsType<ReservationCancelledDomainEvent>(Assert.Single(reservation.DomainEvents));
    }

    [Fact]
    public void First_terminal_timestamp_survives_later_terminal_edits()
    {
        Reservation reservation = CreateReservation().Value;
        Assert.True(reservation.RejectAllocation(
            RequestId,
            ReservationAllocationRejection.AllocationConflict,
            Guid.NewGuid(),
            Now).IsSuccess);

        Assert.Equal(
            ReservationDetailsChangeOutcome.Changed,
            reservation.UpdateGuestDetails(
                "Corrected guest",
                null,
                null,
                1,
                null,
                reservation.DetailsRevision,
                ReservationDetailsChangeOrigin.Staff,
                "staff:supervisor",
                null,
                null,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now.AddHours(1)).Value);
        Assert.True(reservation.RequestCancellation(
            reservation.Version,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddHours(2)).IsSuccess);

        Assert.Equal(ReservationState.Cancelled, reservation.Status);
        Assert.Equal(Now, reservation.TerminalAtUtc);
        Assert.Equal(Now.AddHours(2), reservation.UpdatedAtUtc);
    }

    [Fact]
    public void Create_validates_range_units_guest_and_external_source()
    {
        Assert.Equal(ReservationsDomainErrors.StayRangeInvalid, CreateReservation(departure: new DateOnly(2026, 8, 1)).Error);
        Assert.Equal(ReservationsDomainErrors.RequestedUnitsInvalid, CreateReservation(units: [UnitId, UnitId]).Error);
        Assert.Equal(ReservationsDomainErrors.PrimaryGuestNameInvalid, CreateReservation(primaryGuestName: " ").Error);
        Assert.Equal(ReservationsDomainErrors.GuestCountInvalid, CreateReservation(guestCount: 0).Error);
        Assert.Equal(
            ReservationsDomainErrors.SourceInvalid,
            CreateReservation(source: ReservationSource.External, sourceSystem: null, sourceReference: null).Error);
    }

    [Fact]
    public void Expected_local_times_are_minute_precise_and_part_of_revisioned_booking_details()
    {
        TimeOnly arrivalTime = new(15, 30);
        TimeOnly departureTime = new(10, 45);
        Reservation reservation = CreateReservation(
            expectedArrivalTime: arrivalTime,
            expectedDepartureTime: departureTime).Value;
        ReservationDetailsChangedDomainEvent created =
            Assert.IsType<ReservationDetailsChangedDomainEvent>(reservation.DomainEvents.Last());

        Assert.Equal(arrivalTime, reservation.ExpectedArrivalTime);
        Assert.Equal(departureTime, reservation.ExpectedDepartureTime);
        Assert.Equal(arrivalTime, created.After.ExpectedArrivalTime);
        Assert.Equal(departureTime, created.After.ExpectedDepartureTime);

        reservation.ClearDomainEvents();
        TimeOnly updatedArrival = new(17, 0);
        Result<ReservationDetailsChangeOutcome> changed = reservation.UpdateGuestDetails(
            reservation.PrimaryGuestName,
            reservation.Email,
            reservation.Phone,
            reservation.GuestCount,
            reservation.Notes,
            reservation.DetailsRevision,
            ReservationDetailsChangeOrigin.Staff,
            "staff:front-desk",
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(1),
            updatedArrival,
            departureTime);

        Assert.Equal(ReservationDetailsChangeOutcome.Changed, changed.Value);
        Assert.Equal(updatedArrival, reservation.ExpectedArrivalTime);
        ReservationDetailsChangedDomainEvent updated =
            Assert.IsType<ReservationDetailsChangedDomainEvent>(Assert.Single(reservation.DomainEvents));
        Assert.Equal(nameof(Reservation.ExpectedArrivalTime), Assert.Single(updated.ChangedFields));
        Assert.Equal(arrivalTime, updated.Before!.ExpectedArrivalTime);
        Assert.Equal(updatedArrival, updated.After.ExpectedArrivalTime);

        Assert.Equal(
            ReservationsDomainErrors.ExpectedStayTimeInvalid,
            CreateReservation(expectedArrivalTime: new TimeOnly(15, 30, 1)).Error);
    }

    [Fact]
    public void Guest_details_change_uses_independent_revision_and_records_adapter_provenance()
    {
        Reservation reservation = CreateReservation().Value;
        reservation.ClearDomainEvents();
        Guid connectionId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Guid correlationId = Guid.NewGuid();

        Result<ReservationDetailsChangeOutcome> result = reservation.UpdateGuestDetails(
            "Grace Guest",
            "grace@example.test",
            null,
            2,
            "Upper bunk",
            expectedDetailsRevision: 1,
            ReservationDetailsChangeOrigin.Adapter,
            "adapter-worker",
            connectionId,
            operationId,
            correlationId,
            Guid.NewGuid(),
            Now.AddMinutes(1));

        Assert.Equal(ReservationDetailsChangeOutcome.Changed, result.Value);
        Assert.Equal(2, reservation.DetailsRevision);
        Assert.Equal(2, reservation.Version);
        Assert.Equal(ReservationDetailsChangeOrigin.Adapter, reservation.LastDetailsChangeOrigin);
        Assert.Equal(connectionId, reservation.LastDetailsAdapterConnectionId);
        Assert.Equal(operationId, reservation.LastDetailsExternalOperationId);
        Assert.Equal("GRACE GUEST", reservation.PrimaryGuestNameSearch);
        Assert.Equal("GRACE@EXAMPLE.TEST", reservation.EmailSearch);
        Assert.Null(reservation.PhoneSearch);
        ReservationDetailsChangedDomainEvent details =
            Assert.IsType<ReservationDetailsChangedDomainEvent>(Assert.Single(reservation.DomainEvents));
        Assert.Equal(1, details.FromRevision);
        Assert.Equal(2, details.ToRevision);
        Assert.Contains(nameof(Reservation.PrimaryGuestName), details.ChangedFields);
        Assert.Equal("Ada Guest", details.Before!.PrimaryGuestName);
        Assert.Equal("Grace Guest", details.After.PrimaryGuestName);
    }

    [Fact]
    public void Details_revision_conflict_and_unchanged_update_do_not_mutate_reservation()
    {
        Reservation reservation = CreateReservation().Value;
        reservation.ClearDomainEvents();

        Result<ReservationDetailsChangeOutcome> conflict = reservation.UpdateGuestDetails(
            "Grace Guest",
            null,
            null,
            1,
            null,
            expectedDetailsRevision: 2,
            ReservationDetailsChangeOrigin.Staff,
            "user-a",
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now);
        Result<ReservationDetailsChangeOutcome> unchanged = reservation.UpdateGuestDetails(
            reservation.PrimaryGuestName,
            reservation.Email,
            reservation.Phone,
            reservation.GuestCount,
            reservation.Notes,
            expectedDetailsRevision: 1,
            ReservationDetailsChangeOrigin.Staff,
            "user-a",
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now);

        Assert.Equal(ReservationsDomainErrors.DetailsRevisionConflict, conflict.Error);
        Assert.Equal(ReservationDetailsChangeOutcome.Unchanged, unchanged.Value);
        Assert.Equal(1, reservation.DetailsRevision);
        Assert.Equal(1, reservation.Version);
        Assert.Empty(reservation.DomainEvents);
    }

    [Fact]
    public void Allocation_amendment_keeps_current_details_until_confirmation_and_serializes_staff_changes()
    {
        Reservation reservation = CreateReservation().Value;
        Guid allocationId = Guid.NewGuid();
        Assert.True(reservation.ConfirmAllocation(RequestId, allocationId, 1, Guid.NewGuid(), Now).IsSuccess);
        reservation.ClearDomainEvents();
        Guid amendmentId = Guid.NewGuid();
        Guid inventoryRequestId = Guid.NewGuid();
        Guid replacementUnit = Guid.NewGuid();

        Result<ReservationDetailsChangeOutcome> begun = reservation.BeginAllocationAmendment(
            amendmentId,
            inventoryRequestId,
            new string('a', Reservation.RequestFingerprintLength),
            new DateOnly(2026, 8, 2),
            new DateOnly(2026, 8, 5),
            [replacementUnit],
            "Adapter Updated",
            "updated@example.test",
            null,
            2,
            "Changed stay",
            expectedDetailsRevision: 1,
            origin: ReservationDetailsChangeOrigin.Adapter,
            actorId: "adapter:integration",
            adapterConnectionId: Guid.NewGuid(),
            externalOperationId: Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            eventId: Guid.NewGuid(),
            nowUtc: Now.AddMinutes(1));

        Assert.Equal(ReservationDetailsChangeOutcome.Changed, begun.Value);
        Assert.Equal(new DateOnly(2026, 8, 1), reservation.Arrival);
        Assert.Equal(UnitId, Assert.Single(reservation.RequestedUnits).InventoryUnitId);
        Assert.Equal("ADA GUEST", reservation.PrimaryGuestNameSearch);
        Assert.Equal("ADAPTER UPDATED", reservation.PendingPrimaryGuestNameSearch);
        Assert.Equal("UPDATED@EXAMPLE.TEST", reservation.PendingEmailSearch);
        Assert.Equal(
            ReservationsDomainErrors.AllocationAmendmentInProgress,
            reservation.UpdateGuestDetails(
                "Staff Race", null, null, 1, null, 1, ReservationDetailsChangeOrigin.Staff, "staff:1",
                null, null, Guid.NewGuid(), Guid.NewGuid(), Now).Error);
        Assert.Equal(
            ReservationsDomainErrors.AllocationAmendmentInProgress,
            reservation.RequestCancellation(reservation.Version, Guid.NewGuid(), Guid.NewGuid(), Now).Error);
        Assert.IsType<ReservationAllocationAmendmentRequestedDomainEvent>(Assert.Single(reservation.DomainEvents));

        reservation.ClearDomainEvents();
        Assert.True(reservation.CompleteAllocationAmendment(
            inventoryRequestId,
            allocationId,
            new DateOnly(2026, 8, 2),
            new DateOnly(2026, 8, 5),
            [replacementUnit],
            allocationVersion: 2,
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(new DateOnly(2026, 8, 2), reservation.Arrival);
        Assert.Equal(new DateOnly(2026, 8, 5), reservation.Departure);
        Assert.Equal(replacementUnit, Assert.Single(reservation.RequestedUnits).InventoryUnitId);
        Assert.Equal("Adapter Updated", reservation.PrimaryGuestName);
        Assert.Equal("ADAPTER UPDATED", reservation.PrimaryGuestNameSearch);
        Assert.Equal("UPDATED@EXAMPLE.TEST", reservation.EmailSearch);
        Assert.Equal(2, reservation.DetailsRevision);
        Assert.Equal(2, reservation.AllocationVersion);
        Assert.Null(reservation.PendingAllocationAmendmentId);
        Assert.Null(reservation.PendingPrimaryGuestNameSearch);
        Assert.Null(reservation.PendingEmailSearch);
        Assert.Null(reservation.PendingPhoneSearch);
        Assert.IsType<ReservationDetailsChangedDomainEvent>(Assert.Single(reservation.DomainEvents));
    }

    [Fact]
    public void Canonical_guest_link_is_versioned_idempotent_and_requires_explicit_primary_replacement()
    {
        Reservation reservation = CreateReservation().Value;
        reservation.ClearDomainEvents();
        Guid firstGuest = Guid.NewGuid();
        Guid secondGuest = Guid.NewGuid();

        Result<bool> first = reservation.LinkGuest(
            firstGuest,
            ReservationGuestRole.Primary,
            replaceExistingRole: false,
            expectedVersion: 1,
            "staff:front-desk",
            Guid.NewGuid(),
            Now);

        Assert.True(first.Value);
        Assert.Equal(2, reservation.Version);
        Assert.Equal(firstGuest, Assert.Single(reservation.Guests, guest => guest.IsCurrent).GuestId);
        Assert.IsType<ReservationGuestLinkedDomainEvent>(Assert.Single(reservation.DomainEvents));

        reservation.ClearDomainEvents();
        Result<bool> retry = reservation.LinkGuest(
            firstGuest,
            ReservationGuestRole.Primary,
            replaceExistingRole: false,
            expectedVersion: 1,
            "staff:front-desk",
            Guid.NewGuid(),
            Now);
        Assert.False(retry.Value);
        Assert.Empty(reservation.DomainEvents);
        Assert.Equal(
            ReservationsDomainErrors.ReservationGuestRoleOccupied,
            reservation.LinkGuest(
                secondGuest,
                ReservationGuestRole.Primary,
                replaceExistingRole: false,
                expectedVersion: 2,
                "staff:front-desk",
                Guid.NewGuid(),
                Now).Error);

        Assert.True(reservation.LinkGuest(
            secondGuest,
            ReservationGuestRole.Primary,
            replaceExistingRole: true,
            expectedVersion: 2,
            "staff:supervisor",
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(secondGuest, Assert.Single(reservation.Guests, guest => guest.IsCurrent).GuestId);
        Assert.False(reservation.Guests.Single(guest => guest.GuestId == firstGuest).IsCurrent);
        Assert.Equal(3, reservation.Version);

        reservation.ClearDomainEvents();
        Result<bool> replacementRetry = reservation.LinkGuest(
            secondGuest,
            ReservationGuestRole.Primary,
            replaceExistingRole: true,
            expectedVersion: 2,
            "staff:supervisor",
            Guid.NewGuid(),
            Now.AddMinutes(2));
        Assert.False(replacementRetry.Value);
        Assert.Equal(3, reservation.Version);
        Assert.Empty(reservation.DomainEvents);
    }

    private static Result<Reservation> CreateReservation(
        DateOnly? departure = null,
        IReadOnlyCollection<Guid>? units = null,
        string primaryGuestName = "Ada Guest",
        int guestCount = 1,
        ReservationSource source = ReservationSource.Direct,
        string? sourceSystem = null,
        string? sourceReference = null,
        TimeOnly? expectedArrivalTime = null,
        TimeOnly? expectedDepartureTime = null) =>
        Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            PropertyId,
            RequestId,
            new DateOnly(2026, 8, 1),
            departure ?? new DateOnly(2026, 8, 3),
            units ?? [UnitId],
            primaryGuestName,
            "ada@example.test",
            "+100000000",
            guestCount,
            source,
            sourceSystem,
            sourceReference,
            "Late arrival",
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            initialDetailsActorId: null,
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            Now,
            expectedArrivalTime,
            expectedDepartureTime);
}

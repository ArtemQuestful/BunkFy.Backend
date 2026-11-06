namespace BunkFy.Modules.Reservations.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Entities;

internal sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("reservations", table =>
        {
            table.HasCheckConstraint(
                "CK_reservations_pending_stay_complete",
                "(\"Status\" IN (7, 9) AND \"PendingStayBusinessDate\" IS NOT NULL AND \"PendingStayActorId\" IS NOT NULL AND " +
                "length(trim(\"PendingStayActorId\")) > 0 AND \"ReleaseRequestId\" IS NOT NULL) OR " +
                "(\"Status\" NOT IN (7, 9) AND \"PendingStayBusinessDate\" IS NULL AND \"PendingStayActorId\" IS NULL)");
            table.HasCheckConstraint(
                "CK_reservations_checked_in_complete",
                "(\"Status\" IN (6, 9, 10) AND \"CheckedInBusinessDate\" IS NOT NULL AND \"CheckedInAtUtc\" IS NOT NULL AND " +
                "\"CheckedInBy\" IS NOT NULL AND length(trim(\"CheckedInBy\")) > 0) OR " +
                "(\"Status\" NOT IN (6, 9, 10) AND \"CheckedInBusinessDate\" IS NULL AND \"CheckedInAtUtc\" IS NULL AND \"CheckedInBy\" IS NULL)");
            table.HasCheckConstraint(
                "CK_reservations_no_show_complete",
                "(\"Status\" = 8 AND \"NoShowBusinessDate\" IS NOT NULL AND \"NoShowAtUtc\" IS NOT NULL AND " +
                "\"NoShowBy\" IS NOT NULL AND length(trim(\"NoShowBy\")) > 0) OR " +
                "(\"Status\" <> 8 AND \"NoShowBusinessDate\" IS NULL AND \"NoShowAtUtc\" IS NULL AND \"NoShowBy\" IS NULL)");
            table.HasCheckConstraint(
                "CK_reservations_checked_out_complete",
                "(\"Status\" = 10 AND \"CheckedOutBusinessDate\" IS NOT NULL AND \"CheckedOutAtUtc\" IS NOT NULL AND " +
                "\"CheckedOutBy\" IS NOT NULL AND length(trim(\"CheckedOutBy\")) > 0) OR " +
                "(\"Status\" <> 10 AND \"CheckedOutBusinessDate\" IS NULL AND \"CheckedOutAtUtc\" IS NULL AND \"CheckedOutBy\" IS NULL)");
            table.HasCheckConstraint(
                "CK_reservations_anonymisation_state",
                "(\"IsAnonymised\" = TRUE AND \"AnonymisedAtUtc\" IS NOT NULL) OR " +
                "(\"IsAnonymised\" = FALSE AND \"AnonymisedAtUtc\" IS NULL)");
            table.HasCheckConstraint(
                "CK_reservations_terminal_time",
                "(\"Status\" IN (3, 5, 8, 10) AND \"TerminalAtUtc\" IS NOT NULL) OR " +
                "(\"Status\" NOT IN (3, 5, 8, 10) AND \"TerminalAtUtc\" IS NULL)");
            table.HasCheckConstraint(
                "CK_reservations_pending_inventory_request",
                "(\"PendingAllocationAmendmentId\" IS NULL AND " +
                "\"PendingInventoryAmendmentRequestId\" IS NULL) OR " +
                "(\"PendingAllocationAmendmentId\" IS NOT NULL AND " +
                "\"PendingInventoryAmendmentRequestId\" IS NOT NULL)");
        });
        builder.HasKey(reservation => reservation.Id);
        builder.HasAlternateKey(reservation => new { reservation.ScopeId, reservation.Id });
        builder.Property(reservation => reservation.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(reservation => reservation.PrimaryGuestName).HasMaxLength(Reservation.PrimaryGuestNameMaxLength).IsRequired();
        builder.Property(reservation => reservation.PrimaryGuestNameSearch).HasMaxLength(Reservation.PrimaryGuestNameMaxLength).IsRequired();
        builder.Property(reservation => reservation.Email).HasMaxLength(Reservation.EmailMaxLength);
        builder.Property(reservation => reservation.EmailSearch).HasMaxLength(Reservation.EmailMaxLength);
        builder.Property(reservation => reservation.Phone).HasMaxLength(Reservation.PhoneMaxLength);
        builder.Property(reservation => reservation.PhoneSearch).HasMaxLength(Reservation.PhoneMaxLength);
        builder.Property(reservation => reservation.ExpectedArrivalTime).HasColumnType("time(0) without time zone");
        builder.Property(reservation => reservation.ExpectedDepartureTime).HasColumnType("time(0) without time zone");
        builder.Property(reservation => reservation.SourceSystem).HasMaxLength(Reservation.SourceSystemMaxLength);
        builder.Property(reservation => reservation.SourceReference).HasMaxLength(Reservation.SourceReferenceMaxLength);
        builder.Property(reservation => reservation.Notes).HasMaxLength(Reservation.NotesMaxLength);
        builder.Property(reservation => reservation.PendingAllocationAmendmentRequestFingerprint).HasMaxLength(Reservation.RequestFingerprintLength).IsFixedLength();
        builder.HasIndex(reservation => reservation.PendingInventoryAmendmentRequestId)
            .IsUnique();
        builder.Property(reservation => reservation.PendingInventoryUnitIds).HasMaxLength(Reservation.PendingInventoryUnitIdsMaxLength);
        builder.Property(reservation => reservation.PendingPrimaryGuestName).HasMaxLength(Reservation.PrimaryGuestNameMaxLength);
        builder.Property(reservation => reservation.PendingPrimaryGuestNameSearch).HasMaxLength(Reservation.PrimaryGuestNameMaxLength);
        builder.Property(reservation => reservation.PendingEmail).HasMaxLength(Reservation.EmailMaxLength);
        builder.Property(reservation => reservation.PendingEmailSearch).HasMaxLength(Reservation.EmailMaxLength);
        builder.Property(reservation => reservation.PendingPhone).HasMaxLength(Reservation.PhoneMaxLength);
        builder.Property(reservation => reservation.PendingPhoneSearch).HasMaxLength(Reservation.PhoneMaxLength);
        builder.Property(reservation => reservation.PendingExpectedArrivalTime).HasColumnType("time(0) without time zone");
        builder.Property(reservation => reservation.PendingExpectedDepartureTime).HasColumnType("time(0) without time zone");
        builder.Property(reservation => reservation.PendingNotes).HasMaxLength(Reservation.NotesMaxLength);
        builder.Property(reservation => reservation.PendingDetailsChangeOrigin).HasConversion<int>().IsRequired();
        builder.Property(reservation => reservation.PendingDetailsActorId).HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(reservation => reservation.Source).HasConversion<int>().IsRequired();
        builder.Property(reservation => reservation.Status).HasConversion<int>().IsRequired();
        builder.Property(reservation => reservation.DetailsRevision).IsRequired();
        builder.Property(reservation => reservation.LastDetailsChangeOrigin).HasConversion<int>().IsRequired();
        builder.Property(reservation => reservation.LastDetailsActorId).HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(reservation => reservation.PendingCancellationActorId).HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(reservation => reservation.PendingStayActorId).HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(reservation => reservation.CheckedInBy).HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(reservation => reservation.NoShowBy).HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(reservation => reservation.CheckedOutBy).HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(reservation => reservation.AllocationRejection).HasConversion<int?>();
        builder.Property(reservation => reservation.Version).IsConcurrencyToken().IsRequired();
        builder.Property(reservation => reservation.ProjectionOrdinal).ValueGeneratedOnAdd().IsRequired();
        builder.HasIndex(reservation => new { reservation.ScopeId, reservation.AllocationRequestId }).IsUnique();
        builder.HasIndex(reservation => new { reservation.ScopeId, reservation.SourceSystem, reservation.SourceReference }).IsUnique();
        builder.HasIndex(reservation => reservation.ProjectionOrdinal).IsUnique();
        builder.HasIndex(reservation => new
        {
            reservation.ScopeId,
            reservation.PropertyId,
            reservation.EmailSearch,
            reservation.Id
        });
        builder.HasIndex(reservation => new
        {
            reservation.ScopeId,
            reservation.PropertyId,
            reservation.PhoneSearch,
            reservation.Id
        });
        builder.HasIndex(reservation => new
        {
            reservation.ScopeId,
            reservation.PropertyId,
            reservation.PendingEmailSearch,
            reservation.Id
        });
        builder.HasIndex(reservation => new
        {
            reservation.ScopeId,
            reservation.PropertyId,
            reservation.PendingPhoneSearch,
            reservation.Id
        });
        builder.HasIndex(reservation => new
        {
            reservation.ScopeId,
            reservation.PropertyId,
            reservation.Status,
            reservation.Arrival,
            reservation.Departure
        });
        builder.HasIndex(reservation => new
        {
            reservation.ScopeId,
            reservation.ProjectionOrdinal,
            reservation.TerminalAtUtc,
            reservation.IsAnonymised
        });
        builder.HasMany(reservation => reservation.RequestedUnits)
            .WithOne()
            .HasForeignKey(unit => new { unit.ScopeId, unit.ReservationId })
            .HasPrincipalKey(reservation => new { reservation.ScopeId, reservation.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(reservation => reservation.RequestedUnits).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(reservation => reservation.Guests)
            .WithOne()
            .HasForeignKey(guest => new { guest.ScopeId, guest.ReservationId })
            .HasPrincipalKey(reservation => new { reservation.ScopeId, reservation.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(reservation => reservation.Guests).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(reservation => reservation.DomainEvents);
    }
}

internal sealed class ReservationGuestConfiguration : IEntityTypeConfiguration<ReservationGuest>
{
    public void Configure(EntityTypeBuilder<ReservationGuest> builder)
    {
        builder.ToTable("reservation_guests", table =>
        {
            table.HasCheckConstraint("CK_reservation_guests_link_version", "\"LinkVersion\" >= 1");
            table.HasCheckConstraint(
                "CK_reservation_guests_unlink_snapshot",
                "(\"IsCurrent\" = TRUE AND \"UnlinkedBy\" IS NULL AND \"UnlinkedAtUtc\" IS NULL AND " +
                "\"UnlinkedArrival\" IS NULL AND \"UnlinkedDeparture\" IS NULL AND \"UnlinkedReservationStatus\" IS NULL) OR " +
                "(\"IsCurrent\" = FALSE AND \"UnlinkedBy\" IS NOT NULL AND \"UnlinkedAtUtc\" IS NOT NULL AND " +
                "\"UnlinkedArrival\" IS NOT NULL AND \"UnlinkedDeparture\" IS NOT NULL AND \"UnlinkedReservationStatus\" IS NOT NULL)"
            );
        });
        builder.HasKey(guest => new { guest.ScopeId, guest.ReservationId, guest.Id });
        builder.Property(guest => guest.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(guest => guest.Role).HasConversion<int>().IsRequired();
        builder.Property(guest => guest.LinkedBy).HasMaxLength(Reservation.ActorIdMaxLength).IsRequired();
        builder.Property(guest => guest.UnlinkedBy).HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(guest => guest.LinkVersion).IsRequired();
        builder.Property(guest => guest.UnlinkedReservationStatus).HasConversion<int?>();
        builder.Ignore(guest => guest.GuestId);
        builder.HasIndex(guest => new { guest.ScopeId, guest.ReservationId, guest.IsCurrent, guest.Role });
        builder.HasIndex(guest => new { guest.ScopeId, guest.Id, guest.ReservationId });
    }
}

internal sealed class RequestedInventoryUnitConfiguration : IEntityTypeConfiguration<RequestedInventoryUnit>
{
    public void Configure(EntityTypeBuilder<RequestedInventoryUnit> builder)
    {
        builder.ToTable("requested_inventory_units");
        builder.HasKey(unit => new { unit.ScopeId, unit.ReservationId, unit.Id });
        builder.Property(unit => unit.ScopeId).HasMaxLength(128).IsRequired();
        builder.Ignore(unit => unit.InventoryUnitId);
        builder.HasIndex(unit => new { unit.ScopeId, unit.Id, unit.ReservationId });
    }
}

namespace BunkFy.Modules.Guests.Persistence;

using BunkFy.Modules.Guests.Contracts;
using Gma.Framework.Domain;

public sealed class GuestStayHistoryEntry : IScopedEntity
{
    private GuestStayHistoryEntry() { }

    public GuestStayHistoryEntry(
        string scopeId,
        Guid guestId,
        Guid reservationId,
        Guid propertyId,
        GuestStayRole role,
        DateOnly arrival,
        DateOnly departure,
        GuestStayStatus status,
        DateOnly? checkedInBusinessDate,
        DateOnly? noShowBusinessDate,
        DateOnly? checkedOutBusinessDate,
        bool isCurrentParticipant,
        long reservationVersion,
        int projectionContractVersion)
    {
        this.ScopeId = scopeId;
        this.GuestId = guestId;
        this.ReservationId = reservationId;
        this.Apply(
            propertyId,
            role,
            arrival,
            departure,
            status,
            checkedInBusinessDate,
            noShowBusinessDate,
            checkedOutBusinessDate,
            isCurrentParticipant,
            reservationVersion,
            projectionContractVersion);
    }

    public string ScopeId { get; private set; } = string.Empty;
    public Guid GuestId { get; private set; }
    public Guid ReservationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public GuestStayRole Role { get; private set; }
    public DateOnly Arrival { get; private set; }
    public DateOnly Departure { get; private set; }
    public GuestStayStatus Status { get; private set; }
    public DateOnly? CheckedInBusinessDate { get; private set; }
    public DateOnly? NoShowBusinessDate { get; private set; }
    public DateOnly? CheckedOutBusinessDate { get; private set; }
    public bool IsCurrentParticipant { get; private set; }
    public long ReservationVersion { get; private set; }
    public int ProjectionContractVersion { get; private set; }

    public void Apply(
        Guid propertyId,
        GuestStayRole role,
        DateOnly arrival,
        DateOnly departure,
        GuestStayStatus status,
        DateOnly? checkedInBusinessDate,
        DateOnly? noShowBusinessDate,
        DateOnly? checkedOutBusinessDate,
        bool isCurrentParticipant,
        long reservationVersion,
        int projectionContractVersion)
    {
        if (reservationVersion <= this.ReservationVersion)
        {
            return;
        }

        this.PropertyId = propertyId;
        this.Role = role;
        this.Arrival = arrival;
        this.Departure = departure;
        this.Status = status;
        this.CheckedInBusinessDate = checkedInBusinessDate;
        this.NoShowBusinessDate = noShowBusinessDate;
        this.CheckedOutBusinessDate = checkedOutBusinessDate;
        this.IsCurrentParticipant = isCurrentParticipant;
        this.ReservationVersion = reservationVersion;
        this.ProjectionContractVersion = projectionContractVersion;
    }
}

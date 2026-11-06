namespace BunkFy.Modules.Reservations.Persistence;

using BunkFy.Modules.Guests.Contracts;
using Gma.Framework.Domain;

public sealed class ReservationGuestProfileProjection : IScopedEntity
{
    private ReservationGuestProfileProjection() { }

    public ReservationGuestProfileProjection(
        string scopeId,
        Guid id,
        Guid? originPropertyId,
        GuestStatus status,
        long version)
    {
        this.ScopeId = scopeId;
        this.Id = id;
        this.OriginPropertyId = originPropertyId;
        this.Status = status;
        this.Version = version;
    }

    public string ScopeId { get; private set; } = string.Empty;
    public Guid Id { get; private set; }
    public Guid? OriginPropertyId { get; private set; }
    public GuestStatus Status { get; private set; }
    public long Version { get; private set; }

    public void Apply(Guid? originPropertyId, GuestStatus status, long version)
    {
        if (version <= this.Version)
        {
            return;
        }

        if (originPropertyId.HasValue)
        {
            this.OriginPropertyId = originPropertyId;
        }

        this.Status = status;
        this.Version = version;
    }
}

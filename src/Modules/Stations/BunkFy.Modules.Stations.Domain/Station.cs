namespace BunkFy.Modules.Stations.Domain;

using Gma.Framework.Domain;

public sealed class Station : IScopedEntity
{
    private Station() { }
    public Station(Guid id, string scopeId, Guid propertyId, string label)
    {
        StationRules.Coordinates(scopeId, id, propertyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Length > 100)
        {
            throw new ArgumentException("Station label is too long.", nameof(label));
        }

        this.Id = id;
        this.ScopeId = scopeId;
        this.PropertyId = propertyId;
        this.Label = label.Trim();
    }
    public Guid Id { get; private set; }
    public string ScopeId { get; private set; } = "";
    public Guid PropertyId { get; private set; }
    public string Label { get; private set; } = "";
    public long Version { get; private set; } = 1;
    public bool Revoked { get; private set; }
    public void RePair()
    {
        if (this.Revoked)
        { throw new InvalidOperationException("Revoked station cannot be paired."); }
        this.Version = checked(this.Version + 1);
    }
    public void Revoke() { this.Revoked = true; this.Version = checked(this.Version + 1); }
}

public static class StationRules
{
    public static void Coordinates(string scopeId, params Guid[] ids)
    {
        if (!Guid.TryParseExact(scopeId, "D", out Guid scope) || scope == Guid.Empty ||
            scope.ToString("D") != scopeId || ids.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("Invalid station coordinates.");
        }
    }
    public static void Utc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("UTC time is required.");
        }
    }
    public static void Digest(string value)
    {
        if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
        {
            throw new ArgumentException("A SHA-256 digest is required.");
        }
    }
}


public enum StationActorKind { Unknown = 0, LinkedStation = 1, StationOnly = 2 }

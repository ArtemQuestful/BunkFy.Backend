namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.ValueObjects;

internal static class LegacyPropertyTestFactory
{
    public static Property Create(
        Guid propertyId,
        string tenantId,
        string name,
        string code,
        string persistedTimeZoneId,
        DateTimeOffset createdAtUtc)
    {
        Property property = Property.Create(
            propertyId,
            tenantId,
            name,
            code,
            "Etc/UTC",
            Guid.NewGuid(),
            createdAtUtc).Value;
        typeof(Property).GetProperty(nameof(Property.TimeZoneId))!
            .SetValue(
                property,
                PropertyTimeZoneId.RestorePersisted(
                    persistedTimeZoneId));
        property.ClearDomainEvents();
        return property;
    }
}

namespace BunkFy.Modules.Properties.Domain.Aggregates;

using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.Events;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed partial class Property
{
    public static Result<Property> Create(
        Guid id,
        string tenantId,
        string name,
        string code,
        string timeZoneId,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        Result<PropertyDetails> details = PropertyDetails.Create(
            name,
            code,
            timeZoneId);
        return details.IsFailure
            ? Result.Failure<Property>(details.Error)
            : Create(
                id,
                tenantId,
                details.Value,
                eventId,
                nowUtc);
    }

    public static Result<Property> Create(
        Guid id,
        string tenantId,
        PropertyDetails details,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!details.TimeZoneId.IsPrimaryCanonical)
        {
            return Result.Failure<Property>(
                PropertiesDomainErrors.TimeZoneInvalid);
        }

        if (id == Guid.Empty)
        {
            return Result.Failure<Property>(
                PropertiesDomainErrors.PropertyIdRequired);
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure<Property>(
                PropertiesDomainErrors.DomainEventIdRequired);
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Result.Failure<Property>(
                PropertiesDomainErrors.TenantRequired);
        }

        if (!TenantIds.TryNormalize(tenantId, out string? normalizedTenantId))
        {
            return Result.Failure<Property>(
                PropertiesDomainErrors.TenantInvalid);
        }

        Property property = new(id, normalizedTenantId)
        {
            Name = details.Name,
            Code = details.Code,
            TimeZoneId = details.TimeZoneId,
            CreatedAtUtc = nowUtc
        };

        property.RaiseDomainEvent(new PropertyCreatedDomainEvent(
            eventId,
            nowUtc,
            property.Id,
            property.ScopeId,
            property.Name.Value,
            property.Code.Value,
            property.TimeZoneId.Value,
            property.Status,
            property.Version));

        return Result.Success(property);
    }

    public bool MatchesCreation(PropertyDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return this.Name == details.Name &&
            this.Code == details.Code &&
            this.TimeZoneId == details.TimeZoneId;
    }
}

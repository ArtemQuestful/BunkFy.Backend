namespace BunkFy.Modules.Properties.Domain.Aggregates;

using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.Events;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Results;

public sealed partial class Property
{
    public Result<PropertyDetailsUpdateOutcome> SetTimeZone(
        PropertyTimeZoneId timeZoneId,
        long expectedVersion,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        Result<PropertyDetailsUpdateOutcome> evaluation =
            this.EvaluateTimeZoneChange(timeZoneId, expectedVersion);
        if (evaluation.IsFailure ||
            evaluation.Value == PropertyDetailsUpdateOutcome.Unchanged)
        {
            return evaluation;
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure<PropertyDetailsUpdateOutcome>(
                PropertiesDomainErrors.DomainEventIdRequired);
        }

        string previousTimeZoneId = this.TimeZoneId.Value;
        this.TimeZoneId = timeZoneId;
        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        this.RaiseDomainEvent(new PropertyUpdatedDomainEvent(
            eventId,
            nowUtc,
            this.Id,
            this.ScopeId,
            this.Name.Value,
            this.Code.Value,
            this.TimeZoneId.Value,
            this.Status,
            this.Version,
            previousTimeZoneId));

        return Result.Success(PropertyDetailsUpdateOutcome.Changed);
    }

    public Result<PropertyDetailsUpdateOutcome> EvaluateTimeZoneChange(
        PropertyTimeZoneId timeZoneId,
        long expectedVersion)
    {
        if (!timeZoneId.IsPrimaryCanonical)
        {
            return Result.Failure<PropertyDetailsUpdateOutcome>(
                PropertiesDomainErrors.TimeZoneInvalid);
        }

        Result precondition = this.EnsureActive();
        if (precondition.IsFailure)
        {
            return Result.Failure<PropertyDetailsUpdateOutcome>(
                precondition.Error);
        }

        Result version = this.EnsureExpectedVersion(expectedVersion);
        if (version.IsFailure)
        {
            return Result.Failure<PropertyDetailsUpdateOutcome>(
                version.Error);
        }

        return Result.Success(
            this.TimeZoneId == timeZoneId
                ? PropertyDetailsUpdateOutcome.Unchanged
                : PropertyDetailsUpdateOutcome.Changed);
    }
}

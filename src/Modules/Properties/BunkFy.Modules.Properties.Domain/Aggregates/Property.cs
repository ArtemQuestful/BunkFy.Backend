namespace BunkFy.Modules.Properties.Domain.Aggregates;

using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.Events;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Domain.Models;
using Gma.Framework.Results;

public sealed partial class Property : ScopedAggregateRoot<Guid>
{
    public const int ActorIdMaxLength = 200;
    public const int PropertyNameMaxLength = 256;
    public const int PropertyCodeMaxLength = 64;
    public const int TimeZoneIdMaxLength = 128;
    public const int CountryCodeLength = 2;
    public const int PolicyKeyMaxLength = 128;
    public const int ContentSha256Length = 64;

    private Property() { }

    private Property(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public PropertyName Name { get; private set; }
    public PropertyCode Code { get; private set; }
    public PropertyTimeZoneId TimeZoneId { get; private set; }
    public PropertyState Status { get; private set; } = PropertyState.Active;
    public PropertyProcessingState ProcessingState { get; private set; } = PropertyProcessingState.Unconfigured;
    public PropertyGovernanceBinding? GovernanceBinding { get; private set; }
    public long Version { get; private set; } = 1;
    public long ProjectionOrdinal { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public Result Update(
        string name,
        string code,
        string timeZoneId,
        long expectedVersion,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        Result statusResult = this.EnsureActive();
        if (statusResult.IsFailure)
        {
            return statusResult;
        }

        Result versionResult = this.EnsureExpectedVersion(expectedVersion);
        if (versionResult.IsFailure)
        {
            return versionResult;
        }

        Result<PropertyDetails> details = PropertyDetails.Create(
            name,
            code,
            timeZoneId);
        if (details.IsFailure)
        {
            return Result.Failure(details.Error);
        }

        Result<PropertyDetailsUpdateOutcome> outcome =
            this.UpdateDetails(
                details.Value,
                expectedVersion,
                eventId,
                nowUtc);
        return outcome.IsSuccess
            ? Result.Success()
            : Result.Failure(outcome.Error);
    }

    public Result<PropertyDetailsUpdateOutcome> UpdateDetails(
        PropertyDetails details,
        long expectedVersion,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        Result<PropertyDetailsUpdateOutcome> evaluation =
            this.EvaluateDetailsUpdate(details, expectedVersion);
        if (evaluation.IsFailure ||
            evaluation.Value == PropertyDetailsUpdateOutcome.Unchanged)
        {
            return evaluation;
        }

        return this.ApplyDetails(details, eventId, nowUtc);
    }

    public Result<PropertyDetailsUpdateOutcome> EvaluateDetailsUpdate(
        PropertyDetails details,
        long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(details);
        Result statusResult = this.EnsureActive();
        if (statusResult.IsFailure)
        {
            return Result.Failure<PropertyDetailsUpdateOutcome>(
                statusResult.Error);
        }

        Result versionResult = this.EnsureExpectedVersion(expectedVersion);
        if (versionResult.IsFailure)
        {
            return Result.Failure<PropertyDetailsUpdateOutcome>(
                versionResult.Error);
        }

        if (this.TimeZoneId != details.TimeZoneId)
        {
            return Result.Failure<PropertyDetailsUpdateOutcome>(
                PropertiesDomainErrors.TimeZoneDedicatedOperationRequired);
        }

        return Result.Success(this.MatchesCreation(details)
            ? PropertyDetailsUpdateOutcome.Unchanged
            : PropertyDetailsUpdateOutcome.Changed);
    }

    private Result<PropertyDetailsUpdateOutcome> ApplyDetails(
        PropertyDetails details,
        Guid eventId,
        DateTimeOffset nowUtc)
    {
        if (this.MatchesCreation(details))
        {
            return Result.Success(
                PropertyDetailsUpdateOutcome.Unchanged);
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure<PropertyDetailsUpdateOutcome>(
                PropertiesDomainErrors.DomainEventIdRequired);
        }

        this.Name = details.Name;
        this.Code = details.Code;
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
            this.Version));

        return Result.Success(PropertyDetailsUpdateOutcome.Changed);
    }

    public Result RegisterRoom(long expectedVersion)
    {
        Result evaluation = this.EvaluateRoomRegistration(expectedVersion);
        if (evaluation.IsFailure)
        {
            return evaluation;
        }

        this.Version++;
        return Result.Success();
    }

    public Result EvaluateRoomRegistration(long expectedVersion)
    {
        Result statusResult = this.EnsureActive();
        return statusResult.IsSuccess
            ? this.EnsureExpectedVersion(expectedVersion)
            : statusResult;
    }

    public Result EvaluateRetirement(long expectedVersion)
    {
        Result statusResult = this.EnsureCanRetire();
        return statusResult.IsSuccess
            ? this.EnsureExpectedVersion(expectedVersion)
            : statusResult;
    }

    public Result Retire(long expectedVersion, Guid eventId, DateTimeOffset nowUtc, string? actorId = null)
    {
        Result precondition = this.EvaluateRetirement(expectedVersion);
        if (precondition.IsFailure)
        {
            return precondition;
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure(PropertiesDomainErrors.DomainEventIdRequired);
        }

        if (!TryNormalizeOptionalActor(actorId, out string? normalizedActorId))
        {
            return Result.Failure(PropertiesDomainErrors.ActorIdInvalid);
        }

        this.Status = PropertyState.Retired;
        this.RetiredAtUtc = nowUtc;
        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        this.RaiseDomainEvent(new PropertyRetiredDomainEvent(
            eventId,
            nowUtc,
            this.Id,
            this.ScopeId,
            this.Version,
            normalizedActorId));

        return Result.Success();
    }

    private static bool TryNormalizeOptionalActor(string? value, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null ||
               (normalized.Length <= ActorIdMaxLength && !normalized.Any(char.IsControl));
    }

    private Result EnsureActive() =>
        this.Status switch
        {
            PropertyState.Active => Result.Success(),
            PropertyState.Retired => Result.Failure(PropertiesDomainErrors.PropertyRetired),
            _ => Result.Failure(PropertiesDomainErrors.PropertyStatusUnknown)
        };

    private Result EnsureCanRetire() =>
        this.Status switch
        {
            PropertyState.Active => Result.Success(),
            PropertyState.Retired => Result.Failure(PropertiesDomainErrors.PropertyAlreadyRetired),
            _ => Result.Failure(PropertiesDomainErrors.PropertyStatusUnknown)
        };

    private Result EnsureExpectedVersion(long expectedVersion) =>
        expectedVersion == this.Version
            ? Result.Success()
            : Result.Failure(PropertiesDomainErrors.VersionConflict);

}

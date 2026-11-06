namespace BunkFy.Modules.Properties.Domain.ValueObjects;

using BunkFy.Modules.Properties.Domain.Errors;
using Gma.Framework.Results;

public sealed record PropertyDetails
{
    private PropertyDetails(
        PropertyName name,
        PropertyCode code,
        PropertyTimeZoneId timeZoneId)
    {
        this.Name = name;
        this.Code = code;
        this.TimeZoneId = timeZoneId;
    }

    public PropertyName Name { get; }
    public PropertyCode Code { get; }
    public PropertyTimeZoneId TimeZoneId { get; }

    public static Result<PropertyDetails> Create(
        string? name,
        string? code,
        string? timeZoneId)
    {
        Result<PropertyName> nameResult = PropertyName.Create(name);
        if (nameResult.IsFailure)
        {
            return Result.Failure<PropertyDetails>(nameResult.Error);
        }

        Result<PropertyCode> codeResult = PropertyCode.Create(code);
        if (codeResult.IsFailure)
        {
            return Result.Failure<PropertyDetails>(codeResult.Error);
        }

        Result<PropertyTimeZoneId> timeZoneResult =
            PropertyTimeZoneId.Create(timeZoneId);
        if (timeZoneResult.IsFailure)
        {
            return Result.Failure<PropertyDetails>(timeZoneResult.Error);
        }

        return Result.Success(new PropertyDetails(
            nameResult.Value,
            codeResult.Value,
            timeZoneResult.Value));
    }

    public static Result<PropertyDetails> RestorePersistedTimeZone(
        string? name,
        string? code,
        string timeZoneId)
    {
        Result<PropertyName> nameResult = PropertyName.Create(name);
        if (nameResult.IsFailure)
        {
            return Result.Failure<PropertyDetails>(nameResult.Error);
        }

        Result<PropertyCode> codeResult = PropertyCode.Create(code);
        if (codeResult.IsFailure)
        {
            return Result.Failure<PropertyDetails>(codeResult.Error);
        }

        try
        {
            return Result.Success(new PropertyDetails(
                nameResult.Value,
                codeResult.Value,
                PropertyTimeZoneId.RestorePersisted(timeZoneId)));
        }
        catch (ArgumentException)
        {
            return Result.Failure<PropertyDetails>(
                PropertiesDomainErrors.TimeZoneInvalid);
        }
    }
}

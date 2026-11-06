namespace BunkFy.Host.ServiceDefaults.Security.Stations;

using System.Security.Claims;

internal interface IStationTokenValidator
{
    ValueTask<StationTokenValidationResult> ValidateAsync(string token, CancellationToken cancellationToken);
}

internal enum StationTokenValidationError
{
    None = 0,
    ValidatorDisabled,
    Invalid,
    Expired,
    NotYetValid,
    IssuerInvalid,
    AudienceInvalid,
    TypeInvalid,
    AlgorithmInvalid,
    ClaimsInvalid
}

internal sealed record StationTokenValidationResult(
    bool Succeeded,
    ClaimsPrincipal? Principal,
    StationTokenValidationError Error)
{
    public string Category => this.Error switch
    {
        StationTokenValidationError.None when this.Succeeded => "station_token_valid",
        StationTokenValidationError.ValidatorDisabled => "station_validator_disabled",
        StationTokenValidationError.Invalid => "station_token_invalid",
        StationTokenValidationError.Expired => "station_token_expired",
        StationTokenValidationError.NotYetValid => "station_token_not_yet_valid",
        StationTokenValidationError.IssuerInvalid => "station_token_issuer_invalid",
        StationTokenValidationError.AudienceInvalid => "station_token_audience_invalid",
        StationTokenValidationError.TypeInvalid => "station_token_type_invalid",
        StationTokenValidationError.AlgorithmInvalid => "station_token_algorithm_invalid",
        StationTokenValidationError.ClaimsInvalid => "station_token_claims_invalid",
        _ => "station_token_invalid"
    };

    public static StationTokenValidationResult Success(ClaimsPrincipal principal) =>
        new(true, principal, StationTokenValidationError.None);

    public static StationTokenValidationResult Failure(StationTokenValidationError error)
    {
        if (error == StationTokenValidationError.None)
        {
            throw new ArgumentOutOfRangeException(nameof(error), error, "A failure error is required.");
        }

        return new(false, Principal: null, error);
    }
}

internal sealed class DisabledStationTokenValidator : IStationTokenValidator
{
    public ValueTask<StationTokenValidationResult> ValidateAsync(
        string token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            StationTokenValidationResult.Failure(StationTokenValidationError.ValidatorDisabled));
    }
}

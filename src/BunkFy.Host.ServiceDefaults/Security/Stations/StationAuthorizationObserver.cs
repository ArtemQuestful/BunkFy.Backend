namespace BunkFy.Host.ServiceDefaults.Security.Stations;

internal enum StationAuthorizationResultKind
{
    Challenge,
    Forbidden
}

internal sealed record StationAuthorizationObservation(
    StationAuthorizationResultKind Result,
    int AuthenticatedIdentityCount);

internal interface IStationAuthorizationObserver
{
    void Observe(StationAuthorizationObservation observation);
}

internal sealed class NullStationAuthorizationObserver : IStationAuthorizationObserver
{
    public void Observe(StationAuthorizationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
    }
}

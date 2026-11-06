namespace BunkFy.Host.ServiceDefaults.Security.Stations;

internal enum StationAuthenticationResultKind
{
    NoResult,
    Failed,
    Succeeded
}

internal sealed record StationAuthenticationObservation(
    StationAuthenticationResultKind Result,
    string Category,
    int FieldValueCount,
    IReadOnlyList<int> HeaderLengths);

internal interface IStationAuthenticationObserver
{
    void Observe(StationAuthenticationObservation observation);
}

internal sealed class NullStationAuthenticationObserver : IStationAuthenticationObserver
{
    public void Observe(StationAuthenticationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
    }
}

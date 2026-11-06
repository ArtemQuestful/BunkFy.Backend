namespace BunkFy.Host.ServiceDefaults.Security.Stations;

using System.Globalization;
using Microsoft.Extensions.Configuration;

internal sealed record StationFeatureSnapshot(bool Enabled, long Epoch);

internal interface IStationFeatureState
{
    StationFeatureSnapshot Capture();
}

internal sealed class ConfigurationStationFeatureState(IConfiguration configuration) : IStationFeatureState
{
    public StationFeatureSnapshot Capture()
    {
        string? enabledValue = configuration["Station:Enabled"];
        string? epochValue = configuration["Station:Epoch"];

        bool enabledParsed = bool.TryParse(enabledValue, out bool enabled);
        bool epochParsed = long.TryParse(
            epochValue,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long epoch) && epoch >= 0;

        return new(
            Enabled: enabledParsed && epochParsed && enabled && epoch > 0,
            Epoch: epochParsed ? epoch : 0);
    }
}

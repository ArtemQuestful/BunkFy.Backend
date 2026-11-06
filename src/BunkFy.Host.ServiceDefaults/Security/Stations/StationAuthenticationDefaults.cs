namespace BunkFy.Host.ServiceDefaults.Security.Stations;

public static class StationAuthenticationDefaults
{
    public const string SchemeName = "BunkFyStation";
    public const string WireScheme = "Station";
    public const string StationOnlyPolicy = "BunkFyStationAdmission";
    public const string DualPolicy = "BunkFyStationOrBearerAdmission";
    public const string ActorAuthenticationRequiredCode = "Station.ActorAuthenticationRequired";
    public const string OperationForbiddenCode = "Station.OperationForbidden";
    public const string EpochClaim = "bfx_station_epoch";
    public const int MaximumTokenLength = 4096;
}

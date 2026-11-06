namespace BunkFy.Host.Api;

using Gma.Framework.Api.Production;

/// <summary>Reuse the host HTTP limiter; durable PIN attempt budgets remain a separate station rule.</summary>
public static class StationHttpHostAdmission
{
    private static readonly string[] Paths = ["/api/station-management", "/api/station-setup", "/api/station-runtime"];
    private static readonly string[] Writes = ["POST", "PUT", "PATCH", "DELETE"];
    private static readonly string[] Reads = ["GET", "HEAD"];

    public static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        RateLimitingSettings settings = configuration.GetSection("Http:RateLimiting").Get<RateLimitingSettings>() ?? new();
        if (!settings.Enabled || settings.WindowSeconds is < 60 or > 300 ||
            !HasPolicy(settings, "station-write", 60, Writes) || !HasPolicy(settings, "station-read", 240, Reads))
        {
            throw new InvalidOperationException("Stations HTTP requires bounded station-write and station-read rate policies.");
        }
    }

    private static bool HasPolicy(RateLimitingSettings settings, string name, int maximum, string[] methods) =>
        settings.Policies.Count(p => p.Name == name && p.PermitLimit > 0 && p.PermitLimit <= maximum &&
            Paths.All(path => p.PathPrefixes.Contains(path, StringComparer.Ordinal)) &&
            methods.All(method => p.Methods.Contains(method, StringComparer.Ordinal))) == 1;
}

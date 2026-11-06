namespace BunkFy.Modules.Stations.Api;

/// <summary>The host must explicitly enable this HTTPS-only, PostgreSQL-only surface.</summary>
public sealed class StationApiOptions
{
    public const string SectionName = "Stations:Http";
    public const string CookieName = "__Secure-bunkfy.station";
    public const string RuntimePath = "/api/station-runtime";
    public const string CsrfHeaderName = "X-BunkFy-Station-CSRF";
    public const string ActorHeaderName = "X-BunkFy-Station-Actor";
    public const string GenerationHeaderName = "X-BunkFy-Station-Generation";
    public const string PrimaryAuthenticationScheme = "Bearer";

    public bool Enabled { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
    public int CsrfMinutes { get; set; } = 5;

    public bool IsValid() => !this.Enabled || (this.CsrfMinutes is >= 1 and <= 15 &&
        this.AllowedOrigins is { Length: > 0 and <= 10 } &&
        this.AllowedOrigins.All(IsOrigin) &&
        this.AllowedOrigins.Distinct(StringComparer.Ordinal).Count() == this.AllowedOrigins.Length);

    internal static bool IsOrigin(string? value) => value is { Length: > 0 and <= 300 } &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.GetLeftPart(UriPartial.Authority) == value;
}

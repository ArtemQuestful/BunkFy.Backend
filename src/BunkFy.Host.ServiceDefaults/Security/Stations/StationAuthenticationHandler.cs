namespace BunkFy.Host.ServiceDefaults.Security.Stations;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal sealed class StationAuthenticationHandler(
    IOptionsMonitor<StationAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IStationTokenValidator tokenValidator,
    IStationAuthenticationObserver observer,
    IStationAuthorizationResponseWriter responseWriter)
    : AuthenticationHandler<StationAuthenticationOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        StationAuthorizationHeader header = StationAuthorizationHeaderParser.Parse(this.Request.Headers);

        switch (header.Kind)
        {
            case StationAuthorizationHeaderKind.Missing:
            case StationAuthorizationHeaderKind.NonStation:
                observer.Observe(new(
                    StationAuthenticationResultKind.NoResult,
                    header.Category,
                    header.FieldValueCount,
                    header.Lengths));
                return AuthenticateResult.NoResult();

            case StationAuthorizationHeaderKind.Malformed:
                this.Logger.LogInformation(
                    "Station authentication rejected category {StationCategory}.",
                    header.Category);
                observer.Observe(new(
                    StationAuthenticationResultKind.Failed,
                    header.Category,
                    header.FieldValueCount,
                    header.Lengths));
                return AuthenticateResult.Fail(header.Category);

            case StationAuthorizationHeaderKind.Station:
                break;

            default:
                throw new InvalidOperationException("Unknown station authorization-header state.");
        }

        if (!HasApprovedStationPolicy(this.Context))
        {
            const string category = "station_policy_not_approved";
            this.Logger.LogInformation(
                "Station authentication rejected category {StationCategory}.",
                category);
            observer.Observe(new(
                StationAuthenticationResultKind.Failed,
                category,
                header.FieldValueCount,
                header.Lengths));
            return AuthenticateResult.Fail(category);
        }

        StationTokenValidationResult validation = await tokenValidator
            .ValidateAsync(header.Token!, this.Context.RequestAborted)
            .ConfigureAwait(false);

        if (!validation.Succeeded || validation.Principal is null)
        {
            this.Logger.LogInformation(
                "Station authentication rejected category {StationCategory}.",
                validation.Category);
            observer.Observe(new(
                StationAuthenticationResultKind.Failed,
                validation.Category,
                header.FieldValueCount,
                header.Lengths));
            return AuthenticateResult.Fail(validation.Category);
        }

        ClaimsIdentity identity = new(
            validation.Principal.Claims,
            StationAuthenticationDefaults.SchemeName,
            ClaimTypes.Name,
            ClaimTypes.Role);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, StationAuthenticationDefaults.SchemeName);
        observer.Observe(new(
            StationAuthenticationResultKind.Succeeded,
            validation.Category,
            header.FieldValueCount,
            header.Lengths));
        return AuthenticateResult.Success(ticket);
    }

    private static bool HasApprovedStationPolicy(HttpContext context)
    {
        IReadOnlyList<IAuthorizeData> authorizeData = context.GetEndpoint()?
            .Metadata
            .GetOrderedMetadata<IAuthorizeData>() ?? [];
        return authorizeData.Any(static item =>
            string.Equals(
                item.Policy,
                StationAuthenticationDefaults.StationOnlyPolicy,
                StringComparison.Ordinal) ||
            string.Equals(
                item.Policy,
                StationAuthenticationDefaults.DualPolicy,
                StringComparison.Ordinal));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        responseWriter.WriteChallengeAsync(this.Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        responseWriter.WriteForbiddenAsync(this.Context);
}

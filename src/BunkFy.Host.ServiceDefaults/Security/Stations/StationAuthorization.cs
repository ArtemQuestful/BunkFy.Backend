namespace BunkFy.Host.ServiceDefaults.Security.Stations;

using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal sealed class StationAdmissionRequirement : IAuthorizationRequirement
{
    public static StationAdmissionRequirement Instance { get; } = new();

    private StationAdmissionRequirement()
    {
    }
}

internal sealed class StationAdmissionHandler(IStationFeatureState featureState)
    : AuthorizationHandler<StationAdmissionRequirement>
{
    private const string AssuranceMetadataType =
        "Gma.Framework.Security.AspNetCore.AuthenticationAssuranceMetadata";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StationAdmissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.Resource is not HttpContext httpContext)
        {
            return Task.CompletedTask;
        }

        StationAuthorizationHeader header = StationAuthorizationHeaderParser.Parse(httpContext.Request.Headers);
        if (header.Kind == StationAuthorizationHeaderKind.Malformed)
        {
            return Task.CompletedTask;
        }

        bool stationIdentity = context.User.Identities.Any(static identity =>
            identity.IsAuthenticated &&
            string.Equals(
                identity.AuthenticationType,
                StationAuthenticationDefaults.SchemeName,
                StringComparison.Ordinal));

        if (!stationIdentity)
        {
            bool ordinaryIdentity = context.User.Identities.Any(static identity => identity.IsAuthenticated);
            if (ordinaryIdentity &&
                header.Kind == StationAuthorizationHeaderKind.NonStation &&
                HasNamedPolicy(httpContext, StationAuthenticationDefaults.DualPolicy))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        if (header.Kind != StationAuthorizationHeaderKind.Station ||
            !HasNamedPolicy(
                httpContext,
                StationAuthenticationDefaults.StationOnlyPolicy,
                StationAuthenticationDefaults.DualPolicy) ||
            !HasExactlyOneEligibleClass(httpContext) ||
            HasAssuranceRequirement(httpContext) ||
            IsUnattributedMutation(httpContext))
        {
            return Task.CompletedTask;
        }

        StationFeatureSnapshot feature = featureState.Capture();
        if (!feature.Enabled || !HasCurrentEpoch(context, feature.Epoch))
        {
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    private static bool HasNamedPolicy(HttpContext context, params string[] acceptedPolicies)
    {
        IReadOnlyList<IAuthorizeData> authorizeData = context.GetEndpoint()?
            .Metadata
            .GetOrderedMetadata<IAuthorizeData>() ?? [];

        return authorizeData.Any(item =>
            item.Policy is not null &&
            acceptedPolicies.Contains(item.Policy, StringComparer.Ordinal));
    }

    private static bool HasExactlyOneEligibleClass(HttpContext context)
    {
        IReadOnlyList<StationOperationClassMetadata> metadata = context.GetEndpoint()?
            .Metadata
            .GetOrderedMetadata<StationOperationClassMetadata>() ?? [];

        return metadata.Count == 1 && metadata[0].OperationClass == StationOperationClass.Eligible;
    }

    private static bool HasAssuranceRequirement(HttpContext context) =>
        context.GetEndpoint()?.Metadata.Any(item =>
            string.Equals(item.GetType().FullName, AssuranceMetadataType, StringComparison.Ordinal)) == true;

    private static bool IsUnattributedMutation(HttpContext context)
    {
        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return true;
        }

        IReadOnlyList<IHttpMethodMetadata> methodMetadata =
            endpoint.Metadata.GetOrderedMetadata<IHttpMethodMetadata>();
        bool unsafeMethod = methodMetadata.Count == 0 || methodMetadata.Any(static metadata =>
            metadata.HttpMethods.Count == 0 ||
            metadata.HttpMethods.Any(static method =>
                method is null ||
                method.ToUpperInvariant() is not ("GET" or "HEAD" or "OPTIONS" or "TRACE")));
        bool mutation = unsafeMethod || endpoint.Metadata.GetMetadata<StationMutationMetadata>() is not null;
        IReadOnlyList<StationAttributionAdapterMetadata> attribution =
            endpoint.Metadata.GetOrderedMetadata<StationAttributionAdapterMetadata>();
        return mutation && attribution.Count != 1;
    }

    private static bool HasCurrentEpoch(AuthorizationHandlerContext context, long expectedEpoch)
    {
        string[] values = context.User.FindAll(StationAuthenticationDefaults.EpochClaim)
            .Select(static claim => claim.Value)
            .ToArray();

        return values.Length == 1 &&
            long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out long epoch) &&
            epoch == expectedEpoch;
    }
}

internal sealed class StationAuthorizationMiddlewareResultHandler(
    IStationAuthorizationResponseWriter responseWriter,
    IStationAuthorizationObserver observer)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (!policy.Requirements.OfType<StationAdmissionRequirement>().Any() || authorizeResult.Succeeded)
        {
            return this.fallback.HandleAsync(next, context, policy, authorizeResult);
        }

        int authenticatedIdentityCount = context.User.Identities.Count(static identity => identity.IsAuthenticated);
        StationAuthorizationResultKind result = authenticatedIdentityCount > 0
            ? StationAuthorizationResultKind.Forbidden
            : StationAuthorizationResultKind.Challenge;
        observer.Observe(new(result, authenticatedIdentityCount));
        return result == StationAuthorizationResultKind.Forbidden
            ? responseWriter.WriteForbiddenAsync(context)
            : responseWriter.WriteChallengeAsync(context);
    }
}

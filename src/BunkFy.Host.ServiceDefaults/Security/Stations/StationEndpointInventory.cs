namespace BunkFy.Host.ServiceDefaults.Security.Stations;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gma.Framework.Api.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal sealed record StationRouteIdentity(
    string Schema,
    string HostSurface,
    string RawRoute,
    int RouteOrder,
    bool HttpMethodMetadataPresent,
    IReadOnlyList<string> Methods,
    bool AcceptCorsPreflight,
    IReadOnlyList<string> Hosts);

internal sealed record StationAuthorizeDescriptor(
    string Policy,
    string AuthenticationSchemes,
    string Roles);

internal sealed record StationEndpointInventoryEntry(
    StationRouteIdentity Identity,
    string Module,
    string Operation,
    string AuthorizationKind,
    IReadOnlyList<StationAuthorizeDescriptor> Authorization,
    string EffectiveClass,
    string ClassOrigin,
    string AccessKind,
    IReadOnlyList<string> AssuranceMetadata,
    bool StationAwarePolicy,
    string AttributionAdapter);

internal sealed record StationNonRouteExclusion(
    string Name,
    string PathOrPredicate,
    string PipelineOrder,
    string BaselineAccessibility,
    string Reason,
    string SourceLocation,
    string ProbeHash);

internal sealed record StationEndpointInventoryPayload(
    string Schema,
    string HostSurface,
    IReadOnlyList<StationEndpointInventoryEntry> Endpoints,
    IReadOnlyList<StationNonRouteExclusion> NonRouteExclusions);

internal sealed record StationEndpointInventory(
    StationEndpointInventoryPayload Payload,
    string CanonicalJson,
    string CanonicalSha256,
    string Csv,
    int EligibleCount);

internal static class StationEndpointInventoryBuilder
{
    private const string RouteSchema = "bfx-route-v1";
    private const string InventorySchema = "bfx-station-endpoint-inventory-v1";
    private const string AssuranceMetadataType =
        "Gma.Framework.Security.AspNetCore.AuthenticationAssuranceMetadata";

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static StationEndpointInventory Build(
        IEnumerable<EndpointDataSource> dataSources,
        string hostSurface,
        IAuthorizationPolicyProvider? policyProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSources);
        if (hostSurface is not ("public" or "admin"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(hostSurface),
                hostSurface,
                "Station inventory host surface must be public or admin.");
        }

        List<(string Key, StationEndpointInventoryEntry Entry)> entries = [];
        HashSet<string> identities = new(StringComparer.Ordinal);

        foreach (RouteEndpoint endpoint in dataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>())
        {
            StationRouteIdentity identity = CreateIdentity(endpoint, hostSurface);
            string identityKey = JsonSerializer.Serialize(identity, CanonicalJsonOptions);
            if (!identities.Add(identityKey))
            {
                throw new InvalidOperationException(
                    $"Duplicate station endpoint inventory identity for host '{hostSurface}'.");
            }

            StationEndpointInventoryEntry entry = CreateEntry(endpoint, identity);
            ValidateEntry(entry);
            if (policyProvider is not null)
            {
                ValidateResolvedPolicy(endpoint, entry, policyProvider);
            }

            entries.Add((identityKey, entry));
        }

        StationEndpointInventoryEntry[] ordered = entries
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Entry)
            .ToArray();

        StationEndpointInventoryPayload payload = new(
            InventorySchema,
            hostSurface,
            ordered,
            CreateNonRouteExclusions(hostSurface));
        string canonicalJson = JsonSerializer.Serialize(payload, CanonicalJsonOptions);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
        string csv = CreateCsv(ordered);

        return new(
            payload,
            canonicalJson,
            sha256,
            csv,
            ordered.Count(static entry =>
                string.Equals(entry.EffectiveClass, nameof(StationOperationClass.Eligible), StringComparison.Ordinal)));
    }

    private static StationRouteIdentity CreateIdentity(RouteEndpoint endpoint, string hostSurface)
    {
        string rawRoute = endpoint.RoutePattern.RawText ?? string.Empty;
        if (rawRoute.Length == 0)
        {
            throw new InvalidOperationException("A route endpoint has no stable raw route pattern.");
        }

        IReadOnlyList<IHttpMethodMetadata> methodMetadata =
            endpoint.Metadata.GetOrderedMetadata<IHttpMethodMetadata>();
        bool methodMetadataPresent = methodMetadata.Count > 0;
        string[] methods;
        bool acceptCorsPreflight;
        if (!methodMetadataPresent)
        {
            methods = ["*"];
            acceptCorsPreflight = false;
        }
        else
        {
            (methods, acceptCorsPreflight) = NormalizeMethodMetadata(methodMetadata[0]);
            for (int index = 1; index < methodMetadata.Count; index++)
            {
                (string[] comparisonMethods, bool comparisonCors) = NormalizeMethodMetadata(methodMetadata[index]);
                if (comparisonCors != acceptCorsPreflight || !comparisonMethods.SequenceEqual(methods, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("Conflicting HTTP method metadata on a route endpoint.");
                }
            }
        }

        IReadOnlyList<IHostMetadata> hostMetadata = endpoint.Metadata.GetOrderedMetadata<IHostMetadata>();
        string[] hosts;
        if (hostMetadata.Count == 0)
        {
            hosts = ["*"];
        }
        else
        {
            hosts = NormalizeHosts(hostMetadata[0]);
            for (int index = 1; index < hostMetadata.Count; index++)
            {
                string[] comparisonHosts = NormalizeHosts(hostMetadata[index]);
                if (!comparisonHosts.SequenceEqual(hosts, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("Conflicting host metadata on a route endpoint.");
                }
            }
        }

        return new(
            RouteSchema,
            hostSurface,
            rawRoute,
            endpoint.Order,
            methodMetadataPresent,
            methods,
            acceptCorsPreflight,
            hosts);
    }

    private static StationEndpointInventoryEntry CreateEntry(
        RouteEndpoint endpoint,
        StationRouteIdentity identity)
    {
        IReadOnlyList<StationOperationClassMetadata> classes =
            endpoint.Metadata.GetOrderedMetadata<StationOperationClassMetadata>();
        if (classes.Count > 1)
        {
            throw new InvalidOperationException("A route endpoint has multiple station operation classifications.");
        }

        StationOperationClass effectiveClass = classes.Count == 0
            ? StationOperationClass.Forbidden
            : classes[0].OperationClass;
        if (!Enum.IsDefined(effectiveClass))
        {
            throw new InvalidOperationException("A route endpoint has an unknown station operation classification.");
        }

        IReadOnlyList<IAuthorizeData> authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        bool allowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        string authorizationKind = allowAnonymous
            ? "explicit-anonymous"
            : authorizeData.Count > 0
                ? "protected"
                : "implicit-open";
        StationAuthorizeDescriptor[] authorization = authorizeData
            .Select(static item => new StationAuthorizeDescriptor(
                item.Policy ?? string.Empty,
                item.AuthenticationSchemes ?? string.Empty,
                item.Roles ?? string.Empty))
            .OrderBy(static item => item.Policy, StringComparer.Ordinal)
            .ThenBy(static item => item.AuthenticationSchemes, StringComparer.Ordinal)
            .ThenBy(static item => item.Roles, StringComparer.Ordinal)
            .ToArray();

        string[] assurance = endpoint.Metadata
            .Where(static metadata =>
                string.Equals(metadata.GetType().FullName, AssuranceMetadataType, StringComparison.Ordinal))
            .Select(static metadata => metadata.GetType().FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        bool stationAwarePolicy = authorization.Any(static item =>
            string.Equals(item.Policy, StationAuthenticationDefaults.StationOnlyPolicy, StringComparison.Ordinal) ||
            string.Equals(item.Policy, StationAuthenticationDefaults.DualPolicy, StringComparison.Ordinal));
        bool explicitMutation = endpoint.Metadata.GetMetadata<StationMutationMetadata>() is not null;
        string accessKind = explicitMutation || identity.Methods.Any(static method =>
            method is not ("GET" or "HEAD" or "OPTIONS" or "TRACE"))
            ? "mutation"
            : "read";
        IReadOnlyList<StationAttributionAdapterMetadata> attributionMetadata =
            endpoint.Metadata.GetOrderedMetadata<StationAttributionAdapterMetadata>();
        if (attributionMetadata.Count > 1)
        {
            throw new InvalidOperationException(
                "A route endpoint has multiple station attribution-adapter declarations.");
        }

        string attribution = attributionMetadata.Count == 1 ? attributionMetadata[0].AdapterName : string.Empty;
        string module = endpoint.Metadata.GetMetadata<ModuleEndpointMetadata>()?.ModuleName ?? "unowned";
        string operation = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
            ?? endpoint.DisplayName
            ?? "unnamed";

        return new(
            identity,
            module,
            operation,
            authorizationKind,
            authorization,
            effectiveClass.ToString(),
            classes.Count == 0 ? "implicit-host-default" : "explicit-endpoint",
            accessKind,
            assurance,
            stationAwarePolicy,
            attribution);
    }

    private static void ValidateEntry(StationEndpointInventoryEntry entry)
    {
        if (entry.Authorization.Any(static descriptor =>
                ContainsAuthenticationScheme(
                    descriptor.AuthenticationSchemes,
                    StationAuthenticationDefaults.SchemeName)))
        {
            throw new InvalidOperationException(
                "A route endpoint cannot select BunkFyStation directly; it must use a reviewed station-aware policy.");
        }

        bool eligible = string.Equals(
            entry.EffectiveClass,
            nameof(StationOperationClass.Eligible),
            StringComparison.Ordinal);
        if (!eligible)
        {
            if (entry.StationAwarePolicy)
            {
                throw new InvalidOperationException(
                    "A non-eligible route endpoint cannot name a station-aware policy.");
            }

            return;
        }

        if (string.Equals(entry.AuthorizationKind, "explicit-anonymous", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A station-eligible endpoint cannot allow anonymous access.");
        }

        if (!string.Equals(entry.ClassOrigin, "explicit-endpoint", StringComparison.Ordinal) ||
            !entry.StationAwarePolicy)
        {
            throw new InvalidOperationException(
                "A station-eligible endpoint must be explicit and name a station-aware policy.");
        }

        if (entry.AssuranceMetadata.Count > 0)
        {
            throw new InvalidOperationException(
                "A station-eligible endpoint cannot require privileged or destructive assurance.");
        }

        if (string.Equals(entry.AccessKind, "mutation", StringComparison.Ordinal) &&
            string.IsNullOrEmpty(entry.AttributionAdapter))
        {
            throw new InvalidOperationException(
                "A station-eligible mutation must declare an attribution adapter.");
        }
    }

    private static bool ContainsAuthenticationScheme(string authenticationSchemes, string expectedScheme) =>
        authenticationSchemes
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(scheme => string.Equals(scheme, expectedScheme, StringComparison.Ordinal));

    private static void ValidateResolvedPolicy(
        RouteEndpoint endpoint,
        StationEndpointInventoryEntry entry,
        IAuthorizationPolicyProvider policyProvider)
    {
        IReadOnlyList<IAuthorizeData> authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        if (authorizeData.Count == 0)
        {
            return;
        }

        AuthorizationPolicy? resolvedPolicy;
        try
        {
            resolvedPolicy = AuthorizationPolicy
                .CombineAsync(policyProvider, authorizeData)
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Could not resolve authorization for station inventory route '{entry.Identity.RawRoute}'.",
                exception);
        }

        if (resolvedPolicy is null ||
            !resolvedPolicy.AuthenticationSchemes.Contains(
                StationAuthenticationDefaults.SchemeName,
                StringComparer.Ordinal))
        {
            return;
        }

        int admissionRequirements = resolvedPolicy.Requirements
            .OfType<StationAdmissionRequirement>()
            .Count();
        if (!entry.StationAwarePolicy || admissionRequirements != 1)
        {
            throw new InvalidOperationException(
                "A resolved authorization policy selects BunkFyStation without exactly one approved station admission requirement.");
        }
    }

    private static (string[] Methods, bool AcceptCorsPreflight) NormalizeMethodMetadata(
        IHttpMethodMetadata metadata)
    {
        string[] methods = metadata.HttpMethods
            .Select(static method => method?.Trim().ToUpperInvariant() ?? string.Empty)
            .Where(static method => method.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (methods.Length == 0 || methods.Length != metadata.HttpMethods.Count)
        {
            throw new InvalidOperationException("Invalid or duplicate HTTP methods on a route endpoint.");
        }

        return (methods, metadata.AcceptCorsPreflight);
    }

    private static string[] NormalizeHosts(IHostMetadata metadata)
    {
        string[] hosts = metadata.Hosts
            .Select(static host => host?.Trim().ToLowerInvariant() ?? string.Empty)
            .ToArray();
        if (hosts.Length == 0 || hosts.Any(static host => host.Length == 0))
        {
            throw new InvalidOperationException("Invalid host metadata on a route endpoint.");
        }

        string[] distinct = hosts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (distinct.Length != hosts.Length)
        {
            throw new InvalidOperationException("Duplicate host constraints on a route endpoint.");
        }

        return distinct;
    }

    private static StationNonRouteExclusion[] CreateNonRouteExclusions(string hostSurface) =>
        [
            new(
                "development-swagger-middleware",
                "/swagger and generated OpenAPI predicates",
                "before production HTTP and authentication middleware",
                "environment-dependent baseline probe required",
                "UseGmaOpenApi is middleware rather than a RouteEndpoint",
                $"src/BunkFy.Host.{(hostSurface == "public" ? "Api" : "AdminApi")}/Program.cs",
                "pending-runtime-probe")
        ];

    private static string CreateCsv(IReadOnlyList<StationEndpointInventoryEntry> entries)
    {
        StringBuilder csv = new();
        csv.AppendLine(
            "schema,host,route_order,http_method_metadata_present,methods,accept_cors_preflight,hosts,route,module,operation,authorization_kind,allow_anonymous,authorization_descriptors,effective_class,class_origin,access_kind,assurance_metadata,station_policy,attribution_adapter");
        foreach (StationEndpointInventoryEntry entry in entries)
        {
            string[] fields =
            [
                entry.Identity.Schema,
                entry.Identity.HostSurface,
                entry.Identity.RouteOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.Identity.HttpMethodMetadataPresent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join('|', entry.Identity.Methods),
                entry.Identity.AcceptCorsPreflight.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join('|', entry.Identity.Hosts),
                entry.Identity.RawRoute,
                entry.Module,
                entry.Operation,
                entry.AuthorizationKind,
                string.Equals(entry.AuthorizationKind, "explicit-anonymous", StringComparison.Ordinal)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonSerializer.Serialize(entry.Authorization, CanonicalJsonOptions),
                entry.EffectiveClass,
                entry.ClassOrigin,
                entry.AccessKind,
                string.Join('|', entry.AssuranceMetadata),
                entry.StationAwarePolicy.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.AttributionAdapter
            ];
            csv.AppendLine(string.Join(',', fields.Select(EscapeCsv)));
        }

        return csv.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string EscapeCsv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

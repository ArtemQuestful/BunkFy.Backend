namespace Integration.Tests.Support;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record StationK0TokenContractHashes(
    string A1Sha256,
    string A2Sha256,
    string A3Sha256,
    string A4Sha256);

internal sealed record StationK0TokenEvidenceCorpus(
    string Schema,
    string ArtifactId,
    StationK0TokenContractHashes ContractHashes,
    string SourceFingerprint,
    string BodyHashNormalization,
    string HeaderHashPolicy,
    IReadOnlyList<string> FrozenCaseIds,
    IReadOnlyList<string> FrozenObservationIds,
    IReadOnlyList<StationK0CompositionInventoryEvidence> CompositionInventories,
    IReadOnlyList<StationK0TokenEvidenceObservation> Observations);

internal sealed record StationK0CompositionInventoryEvidence(
    string Host,
    string Environment,
    string ConfigurationVariant,
    int EndpointCount,
    int ProtectedEndpointCount,
    int ExplicitAnonymousEndpointCount,
    int ImplicitOpenEndpointCount,
    int EligibleEndpointCount,
    int StationAwarePolicyCount,
    string CanonicalSha256,
    IReadOnlyList<string> NonRouteExclusionNames);

internal sealed record StationK0BaselineEvidence(
    string ComparisonMode,
    int StatusCode,
    string BodyEvidenceMode,
    int BodyEvidenceLength,
    string BodyEvidenceSha256,
    string SelectedHeadersSha256,
    string? ContentType,
    string OrdinaryAuthenticationResult,
    int StationSchemeEventCount,
    IReadOnlyList<string> FinalAuthenticationTypes,
    int SanitizedLogExceptionCount,
    bool StatusMatched,
    bool BodyMatched,
    bool SelectedHeadersMatched,
    bool ContentTypeMatched,
    bool ComparisonPassed);

internal sealed record StationK0LeakCanaryEvidence(
    string CanaryId,
    int BodyHits,
    int HeaderHits,
    int LogHits);

internal sealed record StationK0TokenEvidenceObservation(
    string ObservationId,
    string CaseId,
    string Subcase,
    string Host,
    string Transport,
    string Environment,
    string Method,
    string Target,
    string EndpointClass,
    string CredentialShape,
    int ClientAuthorizationFieldValueCount,
    IReadOnlyList<int> ClientAuthorizationFieldValueLengths,
    int? ServerAuthorizationFieldValueCount,
    IReadOnlyList<int> ServerAuthorizationFieldValueLengths,
    string OrdinaryAuthenticationResult,
    IReadOnlyList<StationK0SchemeProbeObservation> OrdinarySchemeEvents,
    IReadOnlyList<StationK0StationSchemeEvidence> StationSchemeEvents,
    IReadOnlyList<string> FinalAuthenticationTypes,
    string AuthorizationOutcome,
    int StatusCode,
    string BodyEvidenceMode,
    int BodyEvidenceLength,
    string BodyEvidenceSha256,
    string SelectedHeadersSha256,
    StationK0BaselineEvidence? AnonymousBaseline,
    StationK0SentinelEvidence? Sentinels,
    string SentinelEvidenceScope,
    string ApplicationReach,
    int SanitizedLogCategoryCount,
    int SanitizedLogExceptionCount,
    IReadOnlyList<string> SanitizedLogCategories,
    string SanitizedLogCategoriesSha256,
    IReadOnlyList<StationK0LeakCanaryEvidence> LeakCanaries,
    int TimeoutMilliseconds,
    int AttemptCount,
    string ConfigurationSha256,
    string ExpectedOutcome,
    string ObservedOutcome,
    bool Passed);

internal sealed record StationK0StationSchemeEvidence(
    string Result,
    string Category,
    int AuthorizationFieldValueCount,
    IReadOnlyList<int> AuthorizationFieldValueLengths);

internal sealed record StationK0SentinelEvidence(
    int Binder,
    int Validator,
    int Access,
    int Endpoint,
    int Domain,
    int Mutation);

internal static class StationK0TokenEvidenceWriter
{
    private const string EvidenceDirectoryVariable = "BFX_STATION_K0_TOK_EVIDENCE_DIR";
    private const string BuildFingerprintVariable = "BFX_STATION_K0_BUILD_FINGERPRINT";
    private const string FileName = "BFX-SS-K0-TOK-0002.canonical.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static StationK0TokenEvidenceCorpus CreateCorpus(
        string sourceFingerprint,
        IReadOnlyList<string> frozenObservationIds,
        IReadOnlyList<StationK0CompositionInventoryEvidence> compositionInventories,
        IReadOnlyList<StationK0TokenEvidenceObservation> observations) =>
        new(
            "bfx-station-k0-token-evidence-v2",
            "BFX-SS-K0-TOK-0002",
            new(
                "4489adc1642ef26f33ccdcd301c1e50e3fe4c92125970d80a69b925917eec87d",
                "e8aad4ebcadbcbc164ce8ed5c6dc09075e584596600d571dfb0d232c7ddb3a1b",
                "c153f7feba8cce9a24e5c06cb7e9660c9b7a88ad9d49fdeb27edbcefe807d6a4",
                "563af860669e6ac999fa6c06b6b92dbc0c7ad288edbbba49ba59e7754faf308c"),
            sourceFingerprint,
            "json-sort-remove-traceId-timestamp-timestampUtc-requestId-correlationId-connectionId-connectionToken-v2;prometheus-help-type-descriptors-v1;raw-otherwise",
            "sorted security-cache-auth response headers v1; volatile transport headers excluded",
            Enumerable.Range(1, 22).Select(static value => $"K0TOK-{value:000}").ToArray(),
            frozenObservationIds.Order(StringComparer.Ordinal).ToArray(),
            compositionInventories
                .OrderBy(static item => item.Host, StringComparer.Ordinal)
                .ThenBy(static item => item.ConfigurationVariant, StringComparer.Ordinal)
                .ToArray(),
            observations.OrderBy(static item => item.ObservationId, StringComparer.Ordinal).ToArray());

    public static byte[] Serialize(StationK0TokenEvidenceCorpus corpus)
    {
        byte[] first = JsonSerializer.SerializeToUtf8Bytes(corpus, JsonOptions);
        byte[] second = JsonSerializer.SerializeToUtf8Bytes(corpus, JsonOptions);
        if (!first.AsSpan().SequenceEqual(second))
        {
            throw new InvalidOperationException("Station K0 evidence serialization was not byte deterministic.");
        }

        byte[] withLineFeed = new byte[first.Length + 1];
        first.CopyTo(withLineFeed, 0);
        withLineFeed[^1] = (byte)'\n';
        return withLineFeed;
    }

    public static async Task WriteIfRequestedAsync(
        IReadOnlyList<string> frozenObservationIds,
        IReadOnlyList<StationK0CompositionInventoryEvidence> compositionInventories,
        IReadOnlyList<StationK0TokenEvidenceObservation> observations,
        CancellationToken cancellationToken = default)
    {
        string? outputDirectory = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable);
        string? fingerprint = Environment.GetEnvironmentVariable(BuildFingerprintVariable);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            _ = Serialize(CreateCorpus(
                "unbound-local-test",
                frozenObservationIds,
                compositionInventories,
                observations));
            return;
        }

        if (fingerprint is null ||
            fingerprint.Length != 64 ||
            fingerprint.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                $"{BuildFingerprintVariable} must be a 64-character hexadecimal recursive source fingerprint.");
        }

        Directory.CreateDirectory(outputDirectory);
        byte[] payload = Serialize(CreateCorpus(
            fingerprint.ToLowerInvariant(),
            frozenObservationIds,
            compositionInventories,
            observations));
        string evidencePath = Path.Combine(outputDirectory, FileName);
        await using (FileStream stream = new(
                         evidencePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         useAsync: true))
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        string sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        await using FileStream hashStream = new(
            $"{evidencePath}.sha256",
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4 * 1024,
            useAsync: true);
        byte[] hashPayload = Encoding.UTF8.GetBytes($"{sha256}  {FileName}\n");
        await hashStream.WriteAsync(hashPayload, cancellationToken).ConfigureAwait(false);
    }
}

internal static class StationK0EvidenceHashing
{
    private static readonly HashSet<string> SelectedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache-Control",
        "Content-Length",
        "Content-Security-Policy",
        "Content-Type",
        "Expires",
        "Pragma",
        "Referrer-Policy",
        "Set-Cookie",
        "Strict-Transport-Security",
        "Vary",
        "WWW-Authenticate",
        "X-Content-Type-Options"
    };

    private static readonly HashSet<string> VolatileJsonProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "correlationId",
        "connectionId",
        "connectionToken",
        "requestId",
        "timestamp",
        "timestampUtc",
        "traceId"
    };

    public static string HashBody(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length == 0)
        {
            return Convert.ToHexStringLower(SHA256.HashData(body));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            using MemoryStream canonical = new();
            using (Utf8JsonWriter writer = new(canonical))
            {
                WriteCanonicalJson(writer, document.RootElement);
            }

            return Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray()));
        }
        catch (JsonException)
        {
            return Convert.ToHexStringLower(SHA256.HashData(body));
        }
    }

    public static string HashSelectedHeaders(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string canonical = string.Join(
            "\n",
            response.Headers
                .Concat(response.Content.Headers)
                .Where(header => SelectedResponseHeaders.Contains(header.Key))
                .Select(header => new
                {
                    Name = header.Key.ToLowerInvariant(),
                    Value = string.Join(",", header.Value.Order(StringComparer.Ordinal))
                })
                .OrderBy(static header => header.Name, StringComparer.Ordinal)
                .ThenBy(static header => header.Value, StringComparer.Ordinal)
                .Select(static header => $"{header.Name}:{header.Value}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string HashSanitizedLogCategories(IReadOnlyList<StationK0LogEntry> entries)
    {
        string canonical = string.Join(
            "\n",
            entries
                .Select(static entry => $"{entry.Category}|{entry.Level}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static (int Length, string Sha256) HashPrometheusDescriptorContract(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string canonical = string.Join(
            "\n",
            Encoding.UTF8.GetString(body)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line =>
                    line.StartsWith("# HELP ", StringComparison.Ordinal) ||
                    line.StartsWith("# TYPE ", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        if (canonical.Length == 0)
        {
            throw new InvalidOperationException("Prometheus evidence contained no HELP or TYPE descriptors.");
        }

        byte[] canonicalBytes = Encoding.UTF8.GetBytes(canonical + "\n");
        return (canonicalBytes.Length, Convert.ToHexStringLower(SHA256.HashData(canonicalBytes)));
    }

    public static string HashConfiguration(
        string host,
        string transport,
        string mode,
        bool enabled,
        long epoch,
        int? eligibleCount)
    {
        string canonical = JsonSerializer.Serialize(new
        {
            algorithm = "HS256",
            audience = StationK0TestApplication.StationAudience,
            eligibleCount,
            enabled,
            epoch,
            host,
            issuer = StationK0TestApplication.StationIssuer,
            keyId = "ephemeral-in-process-station-key",
            keyPresent = true,
            mode,
            schemeRegistered = true,
            transport,
            type = StationK0TestApplication.StationTokenType
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject()
                             .Where(property => !VolatileJsonProperties.Contains(property.Name))
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }
}

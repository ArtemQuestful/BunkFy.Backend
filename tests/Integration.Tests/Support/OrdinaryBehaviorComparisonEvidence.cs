namespace Integration.Tests.Support;

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static partial class OrdinaryBehaviorComparisonEvidence
{
    public const int OperationDenominator = 26;
    public const int ObservationDenominator = 52;
    public const int ComparisonDenominator = 26;

    public static readonly string[] OperationIds =
    [
        "CMP-001-register-success",
        "CMP-002-register-conflict",
        "CMP-003-login-success",
        "CMP-004-login-invalid",
        "CMP-005-login-rate-limited",
        "CMP-006-mfa-login-challenge",
        "CMP-007-mfa-complete",
        "CMP-008-refresh-success",
        "CMP-009-refresh-invalid",
        "CMP-010-step-up-success",
        "CMP-011-step-up-failure",
        "CMP-012-sign-out",
        "CMP-013-sessions-list",
        "CMP-014-session-revoke",
        "CMP-015-password-recovery-request",
        "CMP-016-password-recovery-complete",
        "CMP-017-external-exchange-sign-in",
        "CMP-018-external-exchange-link",
        "CMP-019-access-permission-evaluate",
        "CMP-020-tenant-read-permitted",
        "CMP-021-tenant-read-forbidden",
        "CMP-022-admin-read-permitted",
        "CMP-023-admin-read-forbidden",
        "CMP-024-admin-read-unauthenticated",
        "CMP-025-signalr-ordinary-bearer",
        "CMP-026-sse-ordinary-bearer-delivery"
    ];

    public static readonly string[] NormalizationRules =
    [
        "JSON object properties are ordered ordinally; array order is retained.",
        "Only the named JSON field paths in the normalization allowlist are replaced by typed placeholders.",
        "Named exact-value paths are retained byte-for-byte even when GUID-shaped and are compared without normalization.",
        "Unlisted embedded or whole-value GUIDs, JWTs, ISO-8601 timestamps, sensitive query values, and 48-or-more-character opaque material fail evidence generation instead of being hidden.",
        "Set-Cookie names and security attributes are retained and sorted; cookie values are redacted, expiry and Max-Age are typed, and other attribute values including Path retain exact case.",
        "Response Content-Type, Cache-Control, Pragma, security, CORS, Allow, Vary, WWW-Authenticate schemes, Retry-After presence, and normalized Set-Cookie metadata are compared; stable values remain exact.",
        "Raw runtime evidence distinguishes zero registered handlers with framework fallback, one DI handler, and invalid multiple DI handlers; comparable evidence treats the first two as one effective handler and never hides multiples.",
        "Raw runtime request paths replace GUID path segments with {guid}; selected observation runtime is bound to the declared operation path.",
        "Logs retain category, level, event id, original message template, and exception type only; formatted values are never persisted.",
        "Fixture-generated subject ids and database/container addresses are not persisted."
    ];

    public static readonly OrdinaryBehaviorNormalizationPathAllowlist NormalizationPathAllowlist = new(
        TokenPaths:
        [
            "$.accessToken",
            "$.challengeToken",
            "$.connectionToken",
            "$.multiFactorChallenge.challengeToken",
            "$.refreshToken"
        ],
        SecretPaths:
        [
            "$.provisioningUri",
            "$.recoveryCodes[]",
            "$.secret"
        ],
        GeneratedIdPaths:
        [
            "$.connectionId",
            "$.externalIdentityId",
            "$.items[].memberId",
            "$.notification.id",
            "$.sessions[].sessionId",
            "$.traceId"
        ],
        TimestampPaths:
        [
            "$.expiresAtUtc",
            "$.items[].registeredAtUtc",
            "$.multiFactorChallenge.expiresAtUtc",
            "$.notification.occurredAtUtc",
            "$.sessions[].absoluteExpiresAtUtc",
            "$.sessions[].loginDateTimeUtc",
            "$.sessions[].refreshTokenExpiresAtUtc",
            "$.sentAtUtc"
        ],
        ExactValuePaths:
        [
            "$.items[].scopeId",
            "$.notification.scopeId",
            "$.permissions[].scope"
        ]);

    private static readonly string[] StableResponseHeaderNames =
    [
        "Access-Control-Allow-Credentials",
        "Access-Control-Allow-Headers",
        "Access-Control-Allow-Methods",
        "Access-Control-Allow-Origin",
        "Access-Control-Expose-Headers",
        "Access-Control-Max-Age",
        "Allow",
        "Content-Security-Policy",
        "Cross-Origin-Embedder-Policy",
        "Cross-Origin-Opener-Policy",
        "Cross-Origin-Resource-Policy",
        "Permissions-Policy",
        "Referrer-Policy",
        "Strict-Transport-Security",
        "Vary",
        "X-Content-Type-Options",
        "X-Frame-Options"
    ];

    public static string CanonicalizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteCanonical(writer, document.RootElement, path: "$");
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            string normalized = body.Trim();
            RejectUnexpectedRandomMaterial(normalized, path: "$<non-json>");
            return normalized;
        }
    }

    public static string? ExtractErrorCode(string canonicalBody)
    {
        if (string.IsNullOrWhiteSpace(canonicalBody) || canonicalBody[0] != '{')
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(canonicalBody);
        return document.RootElement.TryGetProperty("code", out JsonElement code) &&
            code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;
    }

    public static IReadOnlyList<string> ExtractSchema(string canonicalBody)
    {
        if (string.IsNullOrWhiteSpace(canonicalBody) || canonicalBody[0] is not ('{' or '['))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(canonicalBody);
        SortedSet<string> paths = new(StringComparer.Ordinal);
        VisitSchema(document.RootElement, "$", paths);
        return paths.ToArray();
    }

    public static IReadOnlyDictionary<string, string[]> NormalizeHeaders(HttpResponseMessage response)
    {
        SortedDictionary<string, string[]> headers = new(StringComparer.Ordinal);
        AddIfPresent(headers, "cache-control", response.Headers.CacheControl?.ToString());
        AddIfPresent(headers, "pragma", string.Join(",", response.Headers.Pragma.Select(static value => value.ToString())));
        AddIfPresent(headers, "content-type", response.Content.Headers.ContentType?.ToString());
        foreach (string name in StableResponseHeaderNames)
        {
            if (response.Headers.TryGetValues(name, out IEnumerable<string>? values) ||
                response.Content.Headers.TryGetValues(name, out values))
            {
                headers[name.ToLowerInvariant()] = values
                    .Select(static value => value.Trim())
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }
        }

        string[] challenges = response.Headers.WwwAuthenticate
            .Select(static value => value.Scheme)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (challenges.Length > 0)
        {
            headers["www-authenticate"] = challenges;
        }

        if (response.Headers.RetryAfter is not null)
        {
            headers["retry-after"] = ["<seconds>"];
        }

        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            headers["set-cookie"] = cookies
                .Select(NormalizeCookie)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        return headers;
    }

    public static OrdinaryBehaviorObservation CreateObservation(
        OrdinaryBehaviorOperation operation,
        HttpResponseMessage response,
        string responseBody,
        OrdinaryBehaviorRuntimeSnapshot runtime,
        IReadOnlyList<OrdinaryBehaviorLogRecord> logs,
        IReadOnlyList<OrdinaryBehaviorSubresponse>? subresponses = null)
    {
        string canonicalBody = CanonicalizeBody(responseBody);
        return new OrdinaryBehaviorObservation(
            operation.Id,
            operation.Ordinal,
            operation.Method,
            operation.Path,
            operation.Fixture,
            operation.State,
            operation.AuthenticationInput,
            operation.SubjectCategory,
            operation.ExpectedStatus,
            (int)response.StatusCode,
            (int)response.StatusCode == operation.ExpectedStatus ? "pass" : "fail",
            NormalizeHeaders(response),
            canonicalBody,
            ExtractSchema(canonicalBody),
            ExtractErrorCode(canonicalBody),
            runtime.ToComparable(),
            logs,
            subresponses ?? []);
    }

    public static async Task WriteAsync(
        string outputDirectory,
        string variant,
        string buildFingerprint,
        IReadOnlyList<OrdinaryBehaviorObservation> observations,
        IReadOnlyList<OrdinaryBehaviorRuntimeSnapshot> rawRuntime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildFingerprint);
        Directory.CreateDirectory(outputDirectory);

        OrdinaryBehaviorEvidenceDocument canonical = new(
            "BFX-SS-K0-CMP-0001",
            variant,
            buildFingerprint,
            new(OperationDenominator, ObservationDenominator, ComparisonDenominator),
            NormalizationRules,
            NormalizationPathAllowlist,
            observations.OrderBy(static item => item.Ordinal).ToArray());
        OrdinaryBehaviorRawRuntimeDocument raw = new(
            "BFX-SS-K0-CMP-0001",
            variant,
            buildFingerprint,
            rawRuntime.OrderBy(static item => item.Sequence).ToArray());
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        string prefix = $"BFX-SS-K0-CMP-0001-{variant}";
        string canonicalPath = Path.Combine(outputDirectory, $"{prefix}.canonical.json");
        string runtimePath = Path.Combine(outputDirectory, $"{prefix}.runtime.raw.json");
        if (File.Exists(canonicalPath) || File.Exists(runtimePath))
        {
            throw new IOException(
                $"Evidence output already exists for variant '{variant}' and will not be overwritten.");
        }

        await WriteNewAsync(canonicalPath, JsonSerializer.Serialize(canonical, options)).ConfigureAwait(false);
        await WriteNewAsync(runtimePath, JsonSerializer.Serialize(raw, options)).ConfigureAwait(false);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, $"{path}.{property.Name}");
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item, $"{path}[]");
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(NormalizeString(element.GetString() ?? string.Empty, path));
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
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
                throw new InvalidOperationException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }

    private static string NormalizeString(string value, string path)
    {
        if (NormalizationPathAllowlist.TokenPaths.Contains(path, StringComparer.Ordinal))
        {
            return "<token>";
        }

        if (NormalizationPathAllowlist.SecretPaths.Contains(path, StringComparer.Ordinal))
        {
            return "<secret>";
        }

        if (NormalizationPathAllowlist.GeneratedIdPaths.Contains(path, StringComparer.Ordinal))
        {
            return "<generated-id>";
        }

        if (NormalizationPathAllowlist.TimestampPaths.Contains(path, StringComparer.Ordinal))
        {
            return "<timestamp>";
        }

        if (NormalizationPathAllowlist.ExactValuePaths.Contains(path, StringComparer.Ordinal))
        {
            return value;
        }

        RejectUnexpectedRandomMaterial(value, path);
        return value;
    }

    private static void RejectUnexpectedRandomMaterial(string value, string path)
    {
        if (SensitiveQueryValueRegex().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Unlisted sensitive response substring at '{path}'. Add an exact normalization path or remove the value.");
        }

        if (OpaqueRegex().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Unlisted opaque response value at '{path}'. Add an exact normalization path or preserve a stable value.");
        }

        if (GuidRegex().IsMatch(value) || JwtRegex().IsMatch(value) || TimestampRegex().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Unlisted generated response value at '{path}'. Add an exact normalization path or preserve a stable value.");
        }
    }

    private static string NormalizeCookie(string value)
    {
        string[] segments = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "<invalid-cookie>";
        }

        string name = segments[0].Split('=', 2)[0].Trim().ToLowerInvariant();
        List<string> attributes = [];
        foreach (string segment in segments.Skip(1))
        {
            string[] pair = segment.Split('=', 2);
            string key = pair[0].Trim().ToLowerInvariant();
            string normalized = key switch
            {
                "expires" => "expires=<timestamp>",
                "max-age" => "max-age=<seconds>",
                _ when pair.Length == 2 => $"{key}={pair[1].Trim()}",
                _ => key
            };
            attributes.Add(normalized);
        }

        attributes.Sort(StringComparer.Ordinal);
        return $"{name}=<redacted>;{string.Join(';', attributes)}";
    }

    private static void AddIfPresent(
        SortedDictionary<string, string[]> target,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[name] = [value.Trim()];
        }
    }

    private static async Task WriteNewAsync(string path, string payload)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            useAsync: true);
        await using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteLineAsync(payload).ConfigureAwait(false);
    }

    private static void VisitSchema(JsonElement element, string path, ISet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string child = $"{path}.{property.Name}";
                paths.Add($"{child}:{SchemaKind(property.Value)}");
                VisitSchema(property.Value, child, paths);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            paths.Add($"{path}[]:array");
            foreach (JsonElement item in element.EnumerateArray().Take(1))
            {
                VisitSchema(item, $"{path}[]", paths);
            }
        }
    }

    private static string SchemaKind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown"
    };

    [GeneratedRegex("(?<![0-9a-fA-F])[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?![0-9a-fA-F])", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();

    [GeneratedRegex("(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex("(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/=_-]{48,}(?![A-Za-z0-9+/=_-])", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueRegex();

    [GeneratedRegex("(?<![0-9])(?:[0-9]{4}-[0-9]{2}-[0-9]{2})T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})(?![0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex("(?:access_token|refresh_token|challenge_token|recovery_code|secret)=[^&\\s]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveQueryValueRegex();
}

internal sealed record OrdinaryBehaviorOperation(
    string Id,
    int Ordinal,
    string Method,
    string Path,
    string Fixture,
    string State,
    string AuthenticationInput,
    string SubjectCategory,
    int ExpectedStatus);

internal sealed record OrdinaryBehaviorObservation(
    string Id,
    int Ordinal,
    string Method,
    string Path,
    string Fixture,
    string State,
    string AuthenticationInput,
    string SubjectCategory,
    int ExpectedStatus,
    int ActualStatus,
    string Result,
    IReadOnlyDictionary<string, string[]> Headers,
    string CanonicalBody,
    IReadOnlyList<string> BodySchema,
    string? ErrorCode,
    OrdinaryBehaviorComparableRuntime Runtime,
    IReadOnlyList<OrdinaryBehaviorLogRecord> Logs,
    IReadOnlyList<OrdinaryBehaviorSubresponse> Subresponses);

internal sealed record OrdinaryBehaviorSubresponse(
    string Name,
    int Status,
    IReadOnlyDictionary<string, string[]> Headers,
    string CanonicalBody,
    OrdinaryBehaviorComparableRuntime Runtime);

internal sealed record OrdinaryBehaviorDenominator(
    int Operations,
    int CandidateControlObservations,
    int Comparisons);

internal sealed record OrdinaryBehaviorEvidenceDocument(
    string EvidenceId,
    string Variant,
    string BuildFingerprint,
    OrdinaryBehaviorDenominator Denominator,
    IReadOnlyList<string> NormalizationRules,
    OrdinaryBehaviorNormalizationPathAllowlist NormalizationPathAllowlist,
    IReadOnlyList<OrdinaryBehaviorObservation> Observations);

internal sealed record OrdinaryBehaviorNormalizationPathAllowlist(
    IReadOnlyList<string> TokenPaths,
    IReadOnlyList<string> SecretPaths,
    IReadOnlyList<string> GeneratedIdPaths,
    IReadOnlyList<string> TimestampPaths,
    IReadOnlyList<string> ExactValuePaths);

internal sealed record OrdinaryBehaviorRawRuntimeDocument(
    string EvidenceId,
    string Variant,
    string BuildFingerprint,
    IReadOnlyList<OrdinaryBehaviorRuntimeSnapshot> RuntimeSnapshots);

internal sealed record OrdinaryBehaviorComparableRuntime(
    IReadOnlyList<string> AuthenticatedSchemes,
    bool SubjectPresent,
    string AccessSubjectKind,
    string AccessSubjectIdCategory,
    int EffectiveAuthorizationResultHandlerCount,
    string EffectiveAuthorizationResultHandlerComposition);

internal sealed record OrdinaryBehaviorRuntimeSnapshot(
    long Sequence,
    string Method,
    string Path,
    int Status,
    IReadOnlyList<string> AuthenticatedSchemes,
    bool SubjectPresent,
    string AccessSubjectKind,
    string AccessSubjectIdCategory,
    int RegisteredAuthorizationResultHandlerCount,
    string AuthorizationResultHandlerSelection,
    IReadOnlyList<string> RegisteredAuthorizationResultHandlerImplementationHashes)
{
    public OrdinaryBehaviorComparableRuntime ToComparable() => new(
        this.AuthenticatedSchemes,
        this.SubjectPresent,
        this.AccessSubjectKind,
        this.AccessSubjectIdCategory,
        OrdinaryBehaviorAuthorizationResultHandlerModel.EffectiveCount(
            this.RegisteredAuthorizationResultHandlerCount),
        OrdinaryBehaviorAuthorizationResultHandlerModel.EffectiveComposition(
            this.RegisteredAuthorizationResultHandlerCount));
}

internal static class OrdinaryBehaviorAuthorizationResultHandlerModel
{
    public static string RawSelection(int registeredCount) => registeredCount switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(registeredCount)),
        0 => "framework-default-fallback",
        1 => "di-single-handler",
        _ => "invalid-multiple-di-handlers"
    };

    public static int EffectiveCount(int registeredCount) => registeredCount switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(registeredCount)),
        0 or 1 => 1,
        _ => registeredCount
    };

    public static string EffectiveComposition(int registeredCount) => registeredCount switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(registeredCount)),
        0 or 1 => "single-effective-handler",
        _ => "invalid-multiple-effective-handlers"
    };
}

internal sealed record OrdinaryBehaviorLogRecord(
    string Category,
    string Level,
    int EventId,
    string MessageTemplate,
    string? ExceptionType);

internal sealed class OrdinaryBehaviorRuntimeProbe : IStartupFilter, ILoggerProvider
{
    private readonly ConcurrentQueue<OrdinaryBehaviorRuntimeSnapshot> runtime = new();
    private readonly ConcurrentQueue<OrdinaryBehaviorLogRecord> logs = new();
    private long sequence;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
    {
        application.Use(async (context, following) =>
        {
            await following(context).ConfigureAwait(false);
            IAuthorizationMiddlewareResultHandler[] handlers = context.RequestServices
                .GetServices<IAuthorizationMiddlewareResultHandler>()
                .ToArray();
            string[] schemes = context.User.Identities
                .Where(static identity => identity.IsAuthenticated)
                .Select(static identity => identity.AuthenticationType ?? "<unknown>")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            bool subjectPresent = context.User.Claims.Any(static claim =>
                string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
                string.Equals(claim.Type, "sub", StringComparison.Ordinal) ||
                string.Equals(claim.Type, "nameid", StringComparison.Ordinal));
            AccessSubject? accessSubject = context.RequestServices
                .GetService<IAccessHttpSubjectResolver>()?
                .ResolveSubject(context);
            string[] handlerHashes = handlers
                .Select(static handler => handler.GetType().FullName ?? handler.GetType().Name)
                .Select(static name => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name))))
                .Order(StringComparer.Ordinal)
                .ToArray();
            this.runtime.Enqueue(new(
                Interlocked.Increment(ref this.sequence),
                context.Request.Method,
                NormalizeRuntimePath(context.Request.Path.Value ?? string.Empty),
                context.Response.StatusCode,
                schemes,
                subjectPresent,
                accessSubject is null ? "none" : AccessSubjectKindNames.GetName(accessSubject.Kind),
                GetSubjectIdCategory(accessSubject),
                handlers.Length,
                OrdinaryBehaviorAuthorizationResultHandlerModel.RawSelection(handlers.Length),
                handlerHashes));
        });
        next(application);
    };

    private static string GetSubjectIdCategory(AccessSubject? subject)
    {
        if (subject is null)
        {
            return "none";
        }

        if (subject.Id.StartsWith("cmp-", StringComparison.Ordinal))
        {
            return $"stable-fixture:{subject.Id}";
        }

        return Guid.TryParse(subject.Id, out _)
            ? "guid-redacted"
            : "text-redacted";
    }

    private static string NormalizeRuntimePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string[] segments = path.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            if (Guid.TryParse(segments[index], out _))
            {
                segments[index] = "{guid}";
            }
        }

        return string.Join('/', segments);
    }

    public OrdinaryBehaviorRuntimeSnapshot? LastFor(string method, string path)
    {
        string normalizedPath = NormalizeRuntimePath(path.Split('?', 2)[0]);
        return this.runtime
            .Where(item =>
                string.Equals(item.Method, method, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Path, normalizedPath, StringComparison.Ordinal))
            .OrderByDescending(static item => item.Sequence)
            .FirstOrDefault();
    }

    public IReadOnlyList<OrdinaryBehaviorRuntimeSnapshot> RuntimeSnapshots =>
        this.runtime.OrderBy(static item => item.Sequence).ToArray();

    public IReadOnlyList<OrdinaryBehaviorLogRecord> DrainLogs()
    {
        List<OrdinaryBehaviorLogRecord> drained = [];
        while (this.logs.TryDequeue(out OrdinaryBehaviorLogRecord? record))
        {
            drained.Add(record);
        }

        return drained
            .OrderBy(static item => item.Category, StringComparer.Ordinal)
            .ThenBy(static item => item.Level, StringComparer.Ordinal)
            .ThenBy(static item => item.EventId)
            .ThenBy(static item => item.MessageTemplate, StringComparer.Ordinal)
            .ToArray();
    }

    public ILogger CreateLogger(string categoryName) => new ProbeLogger(categoryName, this.logs);

    public void Dispose()
    {
    }

    private sealed class ProbeLogger(
        string category,
        ConcurrentQueue<OrdinaryBehaviorLogRecord> target) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!this.IsEnabled(logLevel))
            {
                return;
            }

            string template = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.FirstOrDefault(static item =>
                    string.Equals(item.Key, "{OriginalFormat}", StringComparison.Ordinal)).Value?.ToString() ??
                    "<template-unavailable>"
                : "<template-unavailable>";
            target.Enqueue(new(
                category,
                logLevel.ToString(),
                eventId.Id,
                template,
                exception?.GetType().FullName));
        }
    }
}

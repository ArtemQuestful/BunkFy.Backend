namespace Integration.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Globalization;
using BunkFy.Host.ServiceDefaults.Security.Stations;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

[Trait("Category", "Integration")]
public sealed class StationTokenEvidenceIntegrationTests
{
    private const int RequestTimeoutMilliseconds = 15_000;

    private static readonly string[] FrozenObservationIds =
    [
        "K0TOK-001-ordinary-bearer-control-test-server",
        "K0TOK-002-station-against-ordinary-test-server",
        "K0TOK-003-ordinary-against-station-only-test-server",
        "K0TOK-004-A-feature-off-test-server",
        "K0TOK-004-B-validation-test-server",
        "K0TOK-005-station-claims-ordinary-key-test-server",
        "K0TOK-006-A-ordinary-endpoint-test-server",
        "K0TOK-006-B-station-endpoint-bearer-test-server",
        "K0TOK-006-C-station-endpoint-station-scheme-test-server",
        "K0TOK-007-wrong-issuer-test-server",
        "K0TOK-008-wrong-audience-test-server",
        "K0TOK-009-A-wrong-type-test-server",
        "K0TOK-009-B-missing-type-test-server",
        "K0TOK-010-expired-test-server",
        "K0TOK-011-future-not-before-test-server",
        "K0TOK-012-A-missing-critical-claim-test-server",
        "K0TOK-012-B-empty-critical-claim-test-server",
        "K0TOK-013-duplicate-critical-claim-test-server",
        "K0TOK-014-A-unsupported-algorithm-test-server",
        "K0TOK-014-B-unsigned-test-server",
        "K0TOK-015-A-corrupt-three-segment-test-server",
        "K0TOK-015-A-length-0001-test-server",
        "K0TOK-015-A-length-0002-test-server",
        "K0TOK-015-A-length-0003-test-server",
        "K0TOK-015-A-length-0016-test-server",
        "K0TOK-015-A-length-0255-test-server",
        "K0TOK-015-A-length-1024-test-server",
        "K0TOK-015-A-length-4096-test-server",
        "K0TOK-015-A-truncated-two-segment-test-server",
        "K0TOK-015-B-kestrel-4097-kestrel-loopback",
        "K0TOK-015-B-test-server-4097-test-server",
        "K0TOK-015-C-kestrel-65536-kestrel-loopback",
        "K0TOK-016-A-two-station-fields-kestrel-kestrel-loopback",
        "K0TOK-016-A-two-station-fields-test-server-test-server",
        "K0TOK-016-B-bearer-plus-station-fields-kestrel-kestrel-loopback",
        "K0TOK-016-B-bearer-plus-station-fields-test-server-test-server",
        "K0TOK-017-A-comma-folded-station-kestrel-kestrel-loopback",
        "K0TOK-017-A-comma-folded-station-test-server-test-server",
        "K0TOK-017-B-comma-folded-mixed-kestrel-kestrel-loopback",
        "K0TOK-017-B-comma-folded-mixed-test-server-test-server",
        "K0TOK-018-station-cookie-only-test-server",
        "K0TOK-019-ordinary-bearer-plus-station-cookie-test-server",
        "K0TOK-020-A-admin-audit-test-server",
        "K0TOK-020-A-admin-auth-members-test-server",
        "K0TOK-020-A-public-auth-account-methods-test-server",
        "K0TOK-020-A-public-auth-browser-mfa-step-up-test-server",
        "K0TOK-020-A-public-auth-mfa-step-up-test-server",
        "K0TOK-020-A-public-auth-mfa-test-server",
        "K0TOK-020-A-public-auth-oidc-link-test-server",
        "K0TOK-020-A-public-auth-recovery-codes-test-server",
        "K0TOK-020-A-public-auth-sessions-test-server",
        "K0TOK-020-A-public-notification-stream-test-server",
        "K0TOK-020-B-public-alive-test-server",
        "K0TOK-020-B-public-auth-oidc-providers-test-server",
        "K0TOK-020-B-public-auth-password-recovery-test-server",
        "K0TOK-020-B-public-auth-refresh-test-server",
        "K0TOK-020-B-public-auth-self-registration-test-server",
        "K0TOK-020-B-public-capabilities-test-server",
        "K0TOK-020-B-public-health-test-server",
        "K0TOK-020-B-public-metrics-test-server",
        "K0TOK-020-B-public-smoke-test-server",
        "K0TOK-020-C-development-swagger-middleware-test-server",
        "K0TOK-020-D-signalr-ordinary-query-control-test-server",
        "K0TOK-020-D-signalr-station-header-rejected-test-server",
        "K0TOK-020-D-signalr-station-query-rejected-test-server",
        "K0TOK-021-A-ordinary-dual-feature-off-test-server",
        "K0TOK-021-B-station-dual-feature-off-test-server",
        "K0TOK-021-C-station-dual-validation-test-server",
        "K0TOK-022-runtime-deny-and-startup-reject-test-server"
    ];

    [Fact]
    public async Task Complete_station_token_corpus_is_frozen_as_sanitized_canonical_json()
    {
        List<StationK0TokenEvidenceObservation> observations = [];

        await using StationK0TestApplication validation = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Validation)
            .ConfigureAwait(true);
        await using StationK0TestApplication featureOff = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.FeatureOff)
            .ConfigureAwait(true);
        await using StationK0TestApplication negative = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Negative)
            .ConfigureAwait(true);
        await using StationK0TestApplication kestrel = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Validation, useKestrel: true)
            .ConfigureAwait(true);

        using HttpClient validationClient = validation.CreateClient();
        using HttpClient featureOffClient = featureOff.CreateClient();
        using HttpClient negativeClient = negative.CreateClient();
        using HttpClient kestrelClient = kestrel.CreateClient();

        string validationOrdinary = validation.CreateOrdinaryToken();
        string validationStation = validation.CreateStationToken();
        string featureOffOrdinary = featureOff.CreateOrdinaryToken();
        string featureOffStation = featureOff.CreateStationToken();
        string negativeStation = negative.CreateStationToken();
        string kestrelOrdinary = kestrel.CreateOrdinaryToken();
        string kestrelStation = kestrel.CreateStationToken();

        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-001",
            "ordinary-bearer-control",
            "ordinary-protected",
            Plan("/ordinary", "Bearer " + validationOrdinary, "ordinary-bearer"),
            HttpStatusCode.NoContent,
            SentinelExpectation.OrdinaryPositive,
            [validationOrdinary]).ConfigureAwait(true));

        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-002",
            "station-against-ordinary",
            "ordinary-protected",
            Plan("/ordinary", "Station " + validationStation, "station-header"),
            HttpStatusCode.Unauthorized,
            SentinelExpectation.Zero,
            [validationStation],
            expectStationSchemeNotSelected: true).ConfigureAwait(true));

        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-003",
            "ordinary-against-station-only",
            "station-only-eligible",
            Plan("/station-only-eligible", "Bearer " + validationOrdinary, "ordinary-bearer"),
            HttpStatusCode.Unauthorized,
            SentinelExpectation.Zero,
            [validationOrdinary],
            "station_header_other_scheme").ConfigureAwait(true));

        observations.Add(await CaptureSyntheticAsync(
            featureOff,
            featureOffClient,
            "K0TOK-004",
            "A-feature-off",
            "station-only-eligible",
            Plan("/station-only-eligible", "Station " + featureOffStation, "station-header"),
            HttpStatusCode.Forbidden,
            SentinelExpectation.Zero,
            [featureOffStation],
            "station_token_valid").ConfigureAwait(true));

        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-004",
            "B-validation",
            "station-only-eligible",
            Plan("/station-only-eligible", "Station " + validationStation, "station-header"),
            HttpStatusCode.NoContent,
            SentinelExpectation.StationPositive,
            [validationStation],
            "station_token_valid").ConfigureAwait(true));

        string stationClaimsOrdinaryKey = validation.CreateStationToken(new()
        {
            SigningKey = StationK0SigningKey.Ordinary
        });
        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-005",
            "station-claims-ordinary-key",
            "station-only-eligible",
            Plan("/station-only-eligible", "Station " + stationClaimsOrdinaryKey, "station-header"),
            HttpStatusCode.Unauthorized,
            SentinelExpectation.Zero,
            [stationClaimsOrdinaryKey],
            "station_token_invalid").ConfigureAwait(true));

        string ordinaryClaimsStationKey = validation.CreateOrdinaryToken(signWithStationKey: true);
        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-006",
            "A-ordinary-endpoint",
            "ordinary-protected",
            Plan("/ordinary", "Bearer " + ordinaryClaimsStationKey, "ordinary-bearer"),
            HttpStatusCode.Unauthorized,
            SentinelExpectation.Zero,
            [ordinaryClaimsStationKey]).ConfigureAwait(true));
        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-006",
            "B-station-endpoint-bearer",
            "station-only-eligible",
            Plan("/station-only-eligible", "Bearer " + ordinaryClaimsStationKey, "ordinary-bearer"),
            HttpStatusCode.Unauthorized,
            SentinelExpectation.Zero,
            [ordinaryClaimsStationKey],
            "station_header_other_scheme").ConfigureAwait(true));
        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-006",
            "C-station-endpoint-station-scheme",
            "station-only-eligible",
            Plan("/station-only-eligible", "Station " + ordinaryClaimsStationKey, "station-header"),
            HttpStatusCode.Unauthorized,
            SentinelExpectation.Zero,
            [ordinaryClaimsStationKey],
            "station_token_claims_invalid").ConfigureAwait(true));

        DateTime now = DateTime.UtcNow;
        IReadOnlyList<Claim> requiredClaims = StationK0TestApplication.CreateRequiredStationClaims();
        (string CaseId, string Subcase, StationK0TokenSpec Spec, string Category, string[] Canaries)[] invalidCases =
        [
            ("K0TOK-007", "wrong-issuer",
                new() { Issuer = "urn:bunkfy:canary:wrong-issuer" },
                "station_token_issuer_invalid",
                ["urn:bunkfy:canary:wrong-issuer"]),
            ("K0TOK-008", "wrong-audience",
                new() { Audience = "urn:bunkfy:canary:wrong-audience" },
                "station_token_audience_invalid",
                ["urn:bunkfy:canary:wrong-audience"]),
            ("K0TOK-009", "A-wrong-type",
                new() { Type = "bunkfy-canary-wrong-type" },
                "station_token_type_invalid",
                ["bunkfy-canary-wrong-type"]),
            ("K0TOK-009", "B-missing-type",
                new() { OmitType = true },
                "station_token_type_invalid",
                []),
            ("K0TOK-010", "expired",
                new() { NotBeforeUtc = now.AddMinutes(-10), ExpiresUtc = now.AddMinutes(-5) },
                "station_token_expired",
                []),
            ("K0TOK-011", "future-not-before",
                new() { NotBeforeUtc = now.AddMinutes(5), ExpiresUtc = now.AddMinutes(10) },
                "station_token_not_yet_valid",
                []),
            ("K0TOK-012", "A-missing-critical-claim",
                new() { Claims = requiredClaims.Where(static claim => claim.Type != "jti").ToArray() },
                "station_token_claims_invalid",
                []),
            ("K0TOK-012", "B-empty-critical-claim",
                new()
                {
                    Claims = requiredClaims
                        .Where(static claim => claim.Type != StationK0TestApplication.ActorIdClaim)
                        .Append(new Claim(StationK0TestApplication.ActorIdClaim, string.Empty))
                        .ToArray()
                },
                "station_token_claims_invalid",
                []),
            ("K0TOK-013", "duplicate-critical-claim",
                new()
                {
                    Claims =
                    [
                        .. requiredClaims,
                        new Claim("sub", "bunkfy-canary-duplicate-station-actor")
                    ]
                },
                "station_token_claims_invalid",
                ["bunkfy-canary-duplicate-station-actor"]),
            ("K0TOK-014", "A-unsupported-algorithm",
                new() { Algorithm = SecurityAlgorithms.HmacSha256Signature },
                "station_token_algorithm_invalid",
                []),
            ("K0TOK-014", "B-unsigned",
                new() { Unsigned = true },
                "station_token_algorithm_invalid",
                [])
        ];

        foreach ((string caseId, string subcase, StationK0TokenSpec spec, string category, string[] claimCanaries)
                 in invalidCases)
        {
            string token = validation.CreateStationToken(spec);
            observations.Add(await CaptureSyntheticAsync(
                validation,
                validationClient,
                caseId,
                subcase,
                "station-only-eligible",
                Plan("/station-only-eligible", "Station " + token, "station-header"),
                HttpStatusCode.Unauthorized,
                SentinelExpectation.Zero,
                [token, .. claimCanaries],
                category).ConfigureAwait(true));
        }

        (string Subcase, string Token)[] malformedTokens =
        [
            ("length-0001", "A"),
            ("length-0002", "AA"),
            ("length-0003", "AAA"),
            ("truncated-two-segment", "a.b"),
            ("corrupt-three-segment", "a.b.c"),
            ("length-0016", new string('A', 16)),
            ("length-0255", new string('A', 255)),
            ("length-1024", new string('A', 1_024)),
            ("length-4096", new string('A', StationAuthenticationDefaults.MaximumTokenLength))
        ];
        foreach ((string subcase, string token) in malformedTokens)
        {
            observations.Add(await CaptureSyntheticAsync(
                validation,
                validationClient,
                "K0TOK-015",
                "A-" + subcase,
                "station-only-eligible",
                Plan("/station-only-eligible", "Station " + token, "station-header"),
                HttpStatusCode.Unauthorized,
                SentinelExpectation.Zero,
                ["Station " + token]).ConfigureAwait(true));
        }

        string overApplicationLimit = new('A', StationAuthenticationDefaults.MaximumTokenLength + 1);
        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-015",
            "B-test-server-4097",
            "station-only-eligible",
            Plan("/station-only-eligible", "Station " + overApplicationLimit, "station-header"),
            HttpStatusCode.Unauthorized,
            SentinelExpectation.Zero,
            [overApplicationLimit],
            "station_token_length_invalid").ConfigureAwait(true));
        observations.Add(await CaptureSyntheticAsync(
            kestrel,
            kestrelClient,
            "K0TOK-015",
            "B-kestrel-4097",
            "station-only-eligible",
            Plan("/station-only-eligible", "Station " + overApplicationLimit, "station-header"),
            HttpStatusCode.Unauthorized,
            SentinelExpectation.Zero,
            [overApplicationLimit],
            "station_token_length_invalid").ConfigureAwait(true));

        string overTransportLimit = new('A', 65_536);
        observations.Add(await CaptureSyntheticAsync(
            kestrel,
            kestrelClient,
            "K0TOK-015",
            "C-kestrel-65536",
            "station-only-eligible",
            Plan("/station-only-eligible", "Station " + overTransportLimit, "station-header"),
            (HttpStatusCode)431,
            SentinelExpectation.Zero,
            [overTransportLimit],
            expectApplicationReach: false).ConfigureAwait(true));

        (string CaseId, string Subcase, IReadOnlyList<string> TestServerValues, IReadOnlyList<string> KestrelValues)[] mixed =
        [
            ("K0TOK-016", "A-two-station-fields",
                ["Station " + validationStation, "Station " + validationStation],
                ["Station " + kestrelStation, "Station " + kestrelStation]),
            ("K0TOK-016", "B-bearer-plus-station-fields",
                ["Bearer " + validationOrdinary, "Station " + validationStation],
                ["Bearer " + kestrelOrdinary, "Station " + kestrelStation]),
            ("K0TOK-017", "A-comma-folded-station",
                ["Station " + validationStation + ",Station " + validationStation],
                ["Station " + kestrelStation + ",Station " + kestrelStation]),
            ("K0TOK-017", "B-comma-folded-mixed",
                ["Bearer " + validationOrdinary + ",Station " + validationStation],
                ["Bearer " + kestrelOrdinary + ",Station " + kestrelStation])
        ];
        foreach ((string caseId, string subcase, IReadOnlyList<string> testServerValues,
                     IReadOnlyList<string> kestrelValues) in mixed)
        {
            observations.Add(await CaptureSyntheticAsync(
                validation,
                validationClient,
                caseId,
                subcase + "-test-server",
                "dual-eligible",
                Plan("/dual-eligible", testServerValues, "multiple-or-folded-authorization"),
                expectedStatus: null,
                SentinelExpectation.Zero,
                testServerValues.ToArray(),
                identityDerivedStatus: true).ConfigureAwait(true));
            observations.Add(await CaptureSyntheticAsync(
                kestrel,
                kestrelClient,
                caseId,
                subcase + "-kestrel",
                "dual-eligible",
                Plan("/dual-eligible", kestrelValues, "multiple-or-folded-authorization"),
                expectedStatus: null,
                SentinelExpectation.Zero,
                kestrelValues.ToArray(),
                identityDerivedStatus: true).ConfigureAwait(true));
        }

        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-018",
            "station-cookie-only",
            "ordinary-protected",
            new RequestPlan(
                HttpMethod.Get,
                "/ordinary",
                [],
                "bunkfy_station=" + validationStation,
                null,
                "station-cookie"),
            HttpStatusCode.Unauthorized,
            SentinelExpectation.Zero,
            [validationStation, "bunkfy_station"],
            expectStationSchemeNotSelected: true).ConfigureAwait(true));

        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-019",
            "ordinary-bearer-plus-station-cookie",
            "ordinary-protected",
            new RequestPlan(
                HttpMethod.Get,
                "/ordinary",
                ["Bearer " + validationOrdinary],
                "bunkfy_station=" + validationStation,
                null,
                "ordinary-bearer-plus-station-cookie"),
            HttpStatusCode.NoContent,
            SentinelExpectation.OrdinaryPositive,
            [validationOrdinary, validationStation, "bunkfy_station"],
            expectStationSchemeNotSelected: true).ConfigureAwait(true));

        observations.Add(await CaptureSyntheticAsync(
            featureOff,
            featureOffClient,
            "K0TOK-021",
            "A-ordinary-dual-feature-off",
            "dual-eligible",
            Plan("/dual-eligible", "Bearer " + featureOffOrdinary, "ordinary-bearer"),
            HttpStatusCode.NoContent,
            SentinelExpectation.StationPositive,
            [featureOffOrdinary],
            "station_header_other_scheme").ConfigureAwait(true));
        observations.Add(await CaptureSyntheticAsync(
            featureOff,
            featureOffClient,
            "K0TOK-021",
            "B-station-dual-feature-off",
            "dual-eligible",
            Plan("/dual-eligible", "Station " + featureOffStation, "station-header"),
            HttpStatusCode.Forbidden,
            SentinelExpectation.Zero,
            [featureOffStation],
            "station_token_valid").ConfigureAwait(true));
        observations.Add(await CaptureSyntheticAsync(
            validation,
            validationClient,
            "K0TOK-021",
            "C-station-dual-validation",
            "dual-eligible",
            Plan("/dual-eligible", "Station " + validationStation, "station-header"),
            HttpStatusCode.NoContent,
            SentinelExpectation.StationPositive,
            [validationStation],
            "station_token_valid").ConfigureAwait(true));

        InvalidOperationException startupFailure = Assert.Throws<InvalidOperationException>(
            negative.BuildInventoryForEvidence);
        Assert.Contains("non-eligible", startupFailure.Message, StringComparison.OrdinalIgnoreCase);
        StationK0TokenEvidenceObservation forbiddenNegative = await CaptureSyntheticAsync(
            negative,
            negativeClient,
            "K0TOK-022",
            "runtime-deny-and-startup-reject",
            "station-forbidden-negative",
            Plan("/station-forbidden-negative", "Station " + negativeStation, "station-header"),
            HttpStatusCode.Forbidden,
            SentinelExpectation.Zero,
            [negativeStation],
            "station_token_valid").ConfigureAwait(true);
        observations.Add(forbiddenNegative with
        {
            ExpectedOutcome = forbiddenNegative.ExpectedOutcome + "; normal inventory startup rejects endpoint",
            ObservedOutcome = forbiddenNegative.ObservedOutcome + "; startup rejected non-eligible station policy"
        });

        IReadOnlyList<StationK0CompositionInventoryEvidence> compositionInventories =
            await AddFullHost020EvidenceAsync(observations).ConfigureAwait(true);

        AssertCompleteDenominator(observations, compositionInventories);
        Assert.All(observations, static observation => Assert.True(
            observation.Passed,
            "Evidence observation did not pass: " + observation.ObservationId));
        await StationK0TokenEvidenceWriter.WriteIfRequestedAsync(
            FrozenObservationIds,
            compositionInventories,
            observations).ConfigureAwait(true);
    }

    private static async Task<IReadOnlyList<StationK0CompositionInventoryEvidence>> AddFullHost020EvidenceAsync(
        List<StationK0TokenEvidenceObservation> observations)
    {
        List<StationK0CompositionInventoryEvidence> compositionInventories = [];
        await using NotificationStreamingTestApplication publicInventoryApplication = new();
        await using NotificationStreamingTestApplication publicApplication =
            new(environmentName: "Development");
        using HttpClient publicClient = publicApplication.CreateClient();
        string stationToken = NotificationStreamingTestApplication.CreateStationShapedToken();
        StationEndpointInventory publicInventory = BuildCompositionInventory(
            publicInventoryApplication.Services,
            "public");
        compositionInventories.Add(AssertCompositionInventory(
            publicInventory,
            "public",
            "Integration",
            "standard",
            expectedEndpointCount: 323,
            expectedCanonicalSha256: "5f75b75ebdfe6895a28ae228b11f33515317841417983c6a38ecd03441633c26"));

        (string Subcase, HttpMethod Method, string Path)[] protectedPublic =
        [
            ("public-auth-sessions", HttpMethod.Get, "/api/auth/sessions"),
            ("public-auth-account-methods", HttpMethod.Get, "/api/auth/methods"),
            ("public-auth-mfa", HttpMethod.Get, "/api/auth/mfa"),
            ("public-auth-mfa-step-up", HttpMethod.Post, "/api/auth/step-up/mfa"),
            ("public-auth-browser-mfa-step-up", HttpMethod.Post, "/api/auth/browser/step-up/mfa"),
            ("public-auth-recovery-codes", HttpMethod.Post, "/api/auth/mfa/recovery-codes/regenerate"),
            ("public-auth-oidc-link", HttpMethod.Get, "/api/auth/external/test/link"),
            ("public-notification-stream", HttpMethod.Get, "/api/notifications/stream")
        ];
        foreach ((string subcase, HttpMethod method, string path) in protectedPublic)
        {
            observations.Add(await CaptureFullHostAsync(
                "K0TOK-020",
                "A-" + subcase,
                "public",
                "Development",
                "protected-non-station",
                publicClient,
                publicApplication.ResetStationAuthenticationObservations,
                publicApplication.StationEvidenceProbe,
                publicApplication.StationEvidenceLogs,
                () => publicApplication.StationAuthenticationObservations,
                new RequestPlan(method, path, ["Station " + stationToken], null, null, "station-header"),
                stationToken,
                expectedStatus: HttpStatusCode.Unauthorized,
                BaselineComparison.None).ConfigureAwait(true));
        }

        (string Subcase, HttpMethod Method, string Path, HttpStatusCode? ExpectedStatus)[]
            openPublic =
        [
            ("public-alive", HttpMethod.Get, "/alive", null),
            ("public-health", HttpMethod.Get, "/health", null),
            ("public-capabilities", HttpMethod.Get, "/api/product-capabilities", HttpStatusCode.OK),
            ("public-smoke", HttpMethod.Get, "/api/smoke", HttpStatusCode.OK),
            ("public-auth-self-registration", HttpMethod.Get, "/api/auth/self-registration", null),
            ("public-auth-oidc-providers", HttpMethod.Get, "/api/auth/external/providers", null),
            ("public-auth-refresh", HttpMethod.Post, "/api/auth/refresh", null),
            ("public-auth-password-recovery", HttpMethod.Post, "/api/auth/password-recovery", null)
        ];
        foreach ((string subcase, HttpMethod method, string path, HttpStatusCode? expectedStatus) in openPublic)
        {
            observations.Add(await CaptureFullHostAsync(
                "K0TOK-020",
                "B-" + subcase,
                "public",
                "Development",
                "anonymous-or-open",
                publicClient,
                publicApplication.ResetStationAuthenticationObservations,
                publicApplication.StationEvidenceProbe,
                publicApplication.StationEvidenceLogs,
                () => publicApplication.StationAuthenticationObservations,
                new RequestPlan(method, path, ["Station " + stationToken], null, null, "station-header"),
                stationToken,
                expectedStatus,
                BaselineComparison.Exact).ConfigureAwait(true));
        }

        observations.Add(await CaptureFullHostAsync(
            "K0TOK-020",
            "C-development-swagger-middleware",
            "public",
            "Development",
            "non-route-middleware",
            publicClient,
            publicApplication.ResetStationAuthenticationObservations,
            publicApplication.StationEvidenceProbe,
            publicApplication.StationEvidenceLogs,
            () => publicApplication.StationAuthenticationObservations,
            new RequestPlan(
                HttpMethod.Get,
                "/swagger/v1/swagger.json",
                ["Station " + stationToken],
                null,
                null,
                "station-header"),
            stationToken,
            HttpStatusCode.OK,
            BaselineComparison.Exact).ConfigureAwait(true));

        string ordinarySignalRToken =
            NotificationStreamingTestApplication.CreateAccessToken("tenant-a", "user-a");
        observations.Add(await CaptureSignalRAsync(
            publicApplication,
            publicClient,
            "ordinary-query-control",
            new RequestPlan(
                HttpMethod.Post,
                "/hubs/notifications/negotiate?negotiateVersion=1&access_token=" +
                Uri.EscapeDataString(ordinarySignalRToken),
                [],
                null,
                null,
                "ordinary-bearer-query"),
            ordinarySignalRToken,
            HttpStatusCode.OK,
            "succeeded",
            expectedFinalIdentityCount: 1).ConfigureAwait(true));
        observations.Add(await CaptureSignalRAsync(
            publicApplication,
            publicClient,
            "station-query-rejected",
            new RequestPlan(
                HttpMethod.Post,
                "/hubs/notifications/negotiate?negotiateVersion=1&access_token=" +
                Uri.EscapeDataString(stationToken),
                [],
                null,
                null,
                "station-shaped-query"),
            stationToken,
            HttpStatusCode.Unauthorized,
            "failed",
            expectedFinalIdentityCount: 0).ConfigureAwait(true));
        observations.Add(await CaptureSignalRAsync(
            publicApplication,
            publicClient,
            "station-header-rejected",
            new RequestPlan(
                HttpMethod.Post,
                "/hubs/notifications/negotiate?negotiateVersion=1",
                ["Station " + stationToken],
                null,
                null,
                "station-header"),
            stationToken,
            HttpStatusCode.Unauthorized,
            "no-result",
            expectedFinalIdentityCount: 0).ConfigureAwait(true));

        await using AdminApiTestApplication adminApplication = new(
            "SqlServer",
            "Server=127.0.0.1,1433;Database=unused;User Id=sa;Password=Pass@word1;" +
            "TrustServerCertificate=True;Connect Timeout=1",
            "nats://127.0.0.1:4222",
            disableDatabaseBackgroundServices: true);
        using HttpClient adminClient = adminApplication.CreateClient();
        StationEndpointInventory adminInventory = BuildCompositionInventory(
            adminApplication.Services,
            "admin");
        compositionInventories.Add(AssertCompositionInventory(
            adminInventory,
            "admin",
            "Integration",
            "standard",
            expectedEndpointCount: 181,
            expectedCanonicalSha256: "8515ba6b3e7adce05b33805f40f0eac7e16637a89da4b274fdb546654c3ee54e"));
        foreach ((string subcase, string path) in new[]
                 {
                     ("admin-audit", "/api/admin/audit/"),
                     ("admin-auth-members", "/api/admin/auth/members/")
                 })
        {
            observations.Add(await CaptureFullHostAsync(
                "K0TOK-020",
                "A-" + subcase,
                "admin",
                "Integration",
                "protected-non-station",
                adminClient,
                adminApplication.ResetStationAuthenticationObservations,
                adminApplication.StationEvidenceProbe,
                adminApplication.StationEvidenceLogs,
                () => adminApplication.StationAuthenticationObservations,
                new RequestPlan(
                    HttpMethod.Get,
                    path,
                    ["Station " + stationToken],
                    null,
                    null,
                    "station-header"),
                stationToken,
                HttpStatusCode.Unauthorized,
                BaselineComparison.None).ConfigureAwait(true));
        }

        await using NotificationStreamingTestApplication metricsApplication = new(
            environmentName: "Development",
            prometheusEnabled: true);
        using HttpClient metricsClient = metricsApplication.CreateClient();
        StationEndpointInventory metricsInventory = BuildCompositionInventory(
            metricsApplication.Services,
            "public");
        Assert.Equal(publicInventory.Payload.Endpoints.Count + 1, metricsInventory.Payload.Endpoints.Count);
        StationEndpointInventoryEntry metricsEndpoint = Assert.Single(
            metricsInventory.Payload.Endpoints,
            static endpoint => endpoint.Identity.RawRoute == "/metrics");
        Assert.Equal("implicit-open", metricsEndpoint.AuthorizationKind);
        compositionInventories.Add(AssertCompositionInventory(
            metricsInventory,
            "public",
            "Development",
            "prometheus-enabled",
            expectedEndpointCount: 324,
            expectedCanonicalSha256: null));
        observations.Add(await CaptureFullHostAsync(
            "K0TOK-020",
            "B-public-metrics",
            "public",
            "Development",
            "anonymous-diagnostics",
            metricsClient,
            metricsApplication.ResetStationAuthenticationObservations,
            metricsApplication.StationEvidenceProbe,
            metricsApplication.StationEvidenceLogs,
            () => metricsApplication.StationAuthenticationObservations,
            new RequestPlan(
                HttpMethod.Get,
                "/metrics",
                ["Station " + stationToken],
                null,
                null,
                "station-header"),
            stationToken,
            HttpStatusCode.OK,
            BaselineComparison.BoundedDiagnostics).ConfigureAwait(true));

        return compositionInventories;
    }

    private static async Task<StationK0TokenEvidenceObservation> CaptureSyntheticAsync(
        StationK0TestApplication application,
        HttpClient client,
        string caseId,
        string subcase,
        string endpointClass,
        RequestPlan plan,
        HttpStatusCode? expectedStatus,
        SentinelExpectation sentinelExpectation,
        IReadOnlyList<string> canaries,
        string? expectedStationCategory = null,
        bool identityDerivedStatus = false,
        bool expectApplicationReach = true,
        bool expectStationSchemeNotSelected = false)
    {
        application.ResetObservations();
        using HttpRequestMessage request = CreateRequest(plan);
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(RequestTimeoutMilliseconds));
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(true);
        byte[] body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(true);

        StationK0RequestProbeObservation? serverRequest = application.EvidenceProbe.RequestObservations.SingleOrDefault();
        IReadOnlyList<string> finalAuthenticationTypes = application.EvidenceProbe.FinalIdentityObservations
            .SingleOrDefault()?.AuthenticationTypes ?? [];
        HttpStatusCode resolvedExpectedStatus = expectedStatus ??
            (finalAuthenticationTypes.Count == 0 ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
        if (identityDerivedStatus)
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                "Mixed credentials must always be denied.");
        }

        Assert.Equal(resolvedExpectedStatus, response.StatusCode);
        Assert.Equal(expectApplicationReach, serverRequest is not null);
        AssertSentinels(application.Sentinels, sentinelExpectation);
        if (expectedStationCategory is not null)
        {
            Assert.Contains(
                application.Observer.Observations,
                observation => string.Equals(
                    observation.Category,
                    expectedStationCategory,
                    StringComparison.Ordinal));
        }

        if (expectStationSchemeNotSelected)
        {
            Assert.Empty(application.Observer.Observations);
        }

        int logExceptionCount = application.Logs.Entries.Count(static entry =>
            !string.IsNullOrEmpty(entry.ExceptionText));
        bool requireNoLogExceptions = string.Equals(caseId, "K0TOK-015", StringComparison.Ordinal);
        if (requireNoLogExceptions)
        {
            Assert.Equal(0, logExceptionCount);
        }
        IReadOnlyList<StationK0LeakCanaryEvidence> leakCanaries = InspectCanaries(
            body,
            response,
            application.Logs.Entries,
            canaries);
        int leakHits = CountLeakHits(leakCanaries);
        Assert.Equal(0, leakHits);
        string ordinaryResult = ClassifyOrdinaryAuthentication(
            application.EvidenceProbe.SchemeObservations);
        string authorizationOutcome = application.AuthorizationObserver.Observations.Count == 0
            ? "not-observed"
            : string.Join(
                ",",
                application.AuthorizationObserver.Observations.Select(static observation =>
                    observation.Result.ToString().ToLowerInvariant() + ":" +
                    observation.AuthenticatedIdentityCount));
        if (identityDerivedStatus)
        {
            string expectedAuthorizationOutcome = finalAuthenticationTypes.Count == 0
                ? "challenge:0"
                : "forbidden:" + finalAuthenticationTypes.Count.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(expectedAuthorizationOutcome, authorizationOutcome);
        }

        bool passed = response.StatusCode == resolvedExpectedStatus &&
                      (serverRequest is not null) == expectApplicationReach &&
                      (!expectStationSchemeNotSelected || application.Observer.Observations.Count == 0) &&
                      (!requireNoLogExceptions || logExceptionCount == 0) &&
                      leakHits == 0;
        StationK0BodyEvidence bodyEvidence = CreateBodyEvidence(body, BaselineComparison.None);

        return new(
            caseId + "-" + subcase + "-" + application.Transport,
            caseId,
            subcase,
            "isolated-test-host",
            application.Transport,
            "Integration",
            plan.Method.Method,
            plan.Path.Split('?', 2)[0],
            endpointClass,
            plan.CredentialShape,
            plan.AuthorizationValues.Count,
            plan.AuthorizationValues.Select(static value => value.Length).ToArray(),
            serverRequest?.AuthorizationFieldValueCount,
            serverRequest?.AuthorizationFieldValueLengths ?? [],
            ordinaryResult,
            application.EvidenceProbe.SchemeObservations.ToArray(),
            application.Observer.Observations.Select(static observation => new StationK0StationSchemeEvidence(
                observation.Result.ToString().ToLowerInvariant(),
                observation.Category,
                observation.FieldValueCount,
                observation.HeaderLengths)).ToArray(),
            finalAuthenticationTypes,
            authorizationOutcome,
            (int)response.StatusCode,
            bodyEvidence.Mode,
            bodyEvidence.Length,
            bodyEvidence.Sha256,
            StationK0EvidenceHashing.HashSelectedHeaders(response),
            null,
            Snapshot(application.Sentinels),
            "instrumented-isolated-test-host",
            serverRequest is null ? "transport-only" : "application",
            GetSanitizedLogCategories(application.Logs.Entries).Length,
            logExceptionCount,
            GetSanitizedLogCategories(application.Logs.Entries),
            StationK0EvidenceHashing.HashSanitizedLogCategories(application.Logs.Entries),
            leakCanaries,
            RequestTimeoutMilliseconds,
            1,
            StationK0EvidenceHashing.HashConfiguration(
                "isolated-test-host",
                application.Transport,
                application.Mode.ToString(),
                application.Mode != StationK0TestMode.FeatureOff,
                application.Mode == StationK0TestMode.FeatureOff ? 0 : 1,
                application.Inventory?.EligibleCount),
            identityDerivedStatus
                ? "denied with identity-derived 401 or 403 and zero protected sentinels"
                : "HTTP " + (int)resolvedExpectedStatus + " with expected sentinel boundary",
            "HTTP " + (int)response.StatusCode + "; ordinary=" + ordinaryResult +
            "; final-identities=" + finalAuthenticationTypes.Count,
            passed);
    }

    private static async Task<StationK0TokenEvidenceObservation> CaptureFullHostAsync(
        string caseId,
        string subcase,
        string host,
        string environment,
        string endpointClass,
        HttpClient client,
        Action reset,
        StationK0EvidenceProbe probe,
        StationK0LogCollector logs,
        Func<IReadOnlyList<StationAuthenticationObservation>> stationObservations,
        RequestPlan plan,
        string stationToken,
        HttpStatusCode? expectedStatus,
        BaselineComparison baselineComparison)
    {
        StationK0BaselineCapture? baseline = null;
        if (baselineComparison != BaselineComparison.None)
        {
            reset();
            using RequestPlan baselinePlan = plan.WithoutCredentials();
            using HttpRequestMessage baselineRequest = CreateRequest(baselinePlan);
            using CancellationTokenSource baselineTimeout =
                new(TimeSpan.FromMilliseconds(RequestTimeoutMilliseconds));
            using HttpResponseMessage baselineResponse = await client
                .SendAsync(baselineRequest, baselineTimeout.Token)
                .ConfigureAwait(true);
            byte[] baselineBody = await baselineResponse.Content
                .ReadAsByteArrayAsync(baselineTimeout.Token)
                .ConfigureAwait(true);
            _ = Assert.Single(probe.RequestObservations);
            IReadOnlyList<string> baselineIdentities =
                Assert.Single(probe.FinalIdentityObservations).AuthenticationTypes;
            int baselineStationSchemeEventCount = stationObservations().Count;
            Assert.Equal(0, baselineStationSchemeEventCount);
            Assert.Empty(baselineIdentities);
            int baselineLogExceptionCount = logs.Entries.Count(static entry =>
                !string.IsNullOrEmpty(entry.ExceptionText));
            StationK0BodyEvidence baselineBodyEvidence = CreateBodyEvidence(
                baselineBody,
                baselineComparison);
            baseline = new(
                baselineResponse.StatusCode,
                baselineBodyEvidence.Mode,
                baselineBodyEvidence.Length,
                baselineBodyEvidence.Sha256,
                StationK0EvidenceHashing.HashSelectedHeaders(baselineResponse),
                baselineResponse.Content.Headers.ContentType?.ToString(),
                ClassifyOrdinaryAuthentication(probe.SchemeObservations),
                baselineStationSchemeEventCount,
                baselineIdentities,
                baselineLogExceptionCount);
        }

        reset();
        using HttpRequestMessage request = CreateRequest(plan);
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(RequestTimeoutMilliseconds));
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(true);
        byte[] body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(true);
        StationK0RequestProbeObservation serverRequest = Assert.Single(probe.RequestObservations);
        IReadOnlyList<string> finalAuthenticationTypes =
            Assert.Single(probe.FinalIdentityObservations).AuthenticationTypes;
        Assert.Empty(stationObservations());
        Assert.Empty(finalAuthenticationTypes);
        if (expectedStatus is not null)
        {
            Assert.Equal(expectedStatus.Value, response.StatusCode);
        }

        StationK0BodyEvidence bodyEvidence = CreateBodyEvidence(body, baselineComparison);
        string selectedHeadersSha256 = StationK0EvidenceHashing.HashSelectedHeaders(response);
        string? contentType = response.Content.Headers.ContentType?.ToString();
        StationK0BaselineEvidence? baselineEvidence = null;
        if (baseline is not null)
        {
            bool statusMatched = response.StatusCode == baseline.StatusCode;
            bool bodyMatched = string.Equals(
                bodyEvidence.Sha256,
                baseline.BodyEvidenceSha256,
                StringComparison.Ordinal);
            bool selectedHeadersMatched = string.Equals(
                selectedHeadersSha256,
                baseline.SelectedHeadersSha256,
                StringComparison.Ordinal);
            bool contentTypeMatched = string.Equals(contentType, baseline.ContentType, StringComparison.Ordinal);
            bool comparisonPassed = baselineComparison switch
            {
                BaselineComparison.Exact =>
                    statusMatched && bodyMatched && selectedHeadersMatched && contentTypeMatched,
                BaselineComparison.BoundedDiagnostics => statusMatched && contentTypeMatched,
                _ => throw new InvalidOperationException("Unknown anonymous baseline comparison mode.")
            };
            Assert.True(comparisonPassed, "The Station-header response changed its anonymous baseline contract.");
            baselineEvidence = new(
                baselineComparison == BaselineComparison.Exact ? "exact" : "bounded-diagnostics",
                (int)baseline.StatusCode,
                baseline.BodyEvidenceMode,
                baseline.BodyEvidenceLength,
                baseline.BodyEvidenceSha256,
                baseline.SelectedHeadersSha256,
                baseline.ContentType,
                baseline.OrdinaryAuthenticationResult,
                baseline.StationSchemeEventCount,
                baseline.FinalAuthenticationTypes,
                baseline.SanitizedLogExceptionCount,
                statusMatched,
                bodyMatched,
                selectedHeadersMatched,
                contentTypeMatched,
                comparisonPassed);
        }

        int logExceptionCount = logs.Entries.Count(static entry =>
            !string.IsNullOrEmpty(entry.ExceptionText));
        IReadOnlyList<StationK0LeakCanaryEvidence> leakCanaries = InspectCanaries(
            body,
            response,
            logs.Entries,
            [stationToken, "station-actor", "actor-1", "property-1", "station-1"]);
        int leakHits = CountLeakHits(leakCanaries);
        Assert.Equal(0, leakHits);
        string ordinaryResult = ClassifyOrdinaryAuthentication(probe.SchemeObservations);
        bool passed = stationObservations().Count == 0 &&
                      finalAuthenticationTypes.Count == 0 &&
                      leakHits == 0 &&
                      (expectedStatus is null || response.StatusCode == expectedStatus.Value) &&
                      (baselineEvidence?.ComparisonPassed ?? true);

        return new(
            caseId + "-" + subcase + "-test-server",
            caseId,
            subcase,
            host,
            "test-server",
            environment,
            plan.Method.Method,
            plan.Path.Split('?', 2)[0],
            endpointClass,
            plan.CredentialShape,
            plan.AuthorizationValues.Count,
            plan.AuthorizationValues.Select(static value => value.Length).ToArray(),
            serverRequest.AuthorizationFieldValueCount,
            serverRequest.AuthorizationFieldValueLengths,
            ordinaryResult,
            probe.SchemeObservations.ToArray(),
            [],
            finalAuthenticationTypes,
            response.StatusCode == HttpStatusCode.Unauthorized
                ? "ordinary-bearer-challenge"
                : "anonymous-baseline",
            (int)response.StatusCode,
            bodyEvidence.Mode,
            bodyEvidence.Length,
            bodyEvidence.Sha256,
            selectedHeadersSha256,
            baselineEvidence,
            null,
            "not-instrumented-composed-host; inventory and auth probes only",
            "application",
            GetSanitizedLogCategories(logs.Entries).Length,
            logExceptionCount,
            GetSanitizedLogCategories(logs.Entries),
            StationK0EvidenceHashing.HashSanitizedLogCategories(logs.Entries),
            leakCanaries,
            RequestTimeoutMilliseconds,
            1,
            StationK0EvidenceHashing.HashConfiguration(
                host,
                "test-server",
                "production-composition-feature-off",
                enabled: false,
                epoch: 0,
                eligibleCount: 0),
            baselineEvidence is not null
                ? baselineEvidence.ComparisonMode +
                  " anonymous baseline equivalence; no Station identity or scheme selection"
                : expectedStatus == HttpStatusCode.Unauthorized
                    ? "HTTP 401 ordinary Bearer challenge; no Station identity or scheme selection"
                    : "open composed-host probe; no Station identity or scheme selection",
            "HTTP " + (int)response.StatusCode + "; baseline=" +
            (baseline is null
                ? "not-run"
                : ((int)baseline.StatusCode).ToString(CultureInfo.InvariantCulture)) +
            "; ordinary=" + ordinaryResult,
            passed);
    }

    private static async Task<StationK0TokenEvidenceObservation> CaptureSignalRAsync(
        NotificationStreamingTestApplication application,
        HttpClient client,
        string subcase,
        RequestPlan plan,
        string credentialCanary,
        HttpStatusCode expectedStatus,
        string expectedOrdinaryResult,
        int expectedFinalIdentityCount)
    {
        application.ResetStationAuthenticationObservations();
        using HttpRequestMessage request = CreateRequest(plan);
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(RequestTimeoutMilliseconds));
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(true);
        byte[] body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(true);
        StationK0RequestProbeObservation serverRequest =
            Assert.Single(application.StationEvidenceProbe.RequestObservations);
        IReadOnlyList<string> finalAuthenticationTypes =
            Assert.Single(application.StationEvidenceProbe.FinalIdentityObservations).AuthenticationTypes;
        Assert.Empty(application.StationAuthenticationObservations);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedFinalIdentityCount, finalAuthenticationTypes.Count);
        string ordinaryResult = ClassifyOrdinaryAuthentication(
            application.StationEvidenceProbe.SchemeObservations);
        Assert.Equal(expectedOrdinaryResult, ordinaryResult);

        int logExceptionCount = application.StationEvidenceLogs.Entries.Count(static entry =>
            !string.IsNullOrEmpty(entry.ExceptionText));
        IReadOnlyList<string> canaries = plan.CredentialShape.StartsWith("station", StringComparison.Ordinal)
            ? [credentialCanary, "station-actor", "actor-1", "property-1", "station-1"]
            : [credentialCanary];
        IReadOnlyList<StationK0LeakCanaryEvidence> leakCanaries = InspectCanaries(
            body,
            response,
            application.StationEvidenceLogs.Entries,
            canaries);
        int leakHits = CountLeakHits(leakCanaries);
        Assert.Equal(0, leakHits);
        bool passed = response.StatusCode == expectedStatus &&
                      finalAuthenticationTypes.Count == expectedFinalIdentityCount &&
                      string.Equals(ordinaryResult, expectedOrdinaryResult, StringComparison.Ordinal) &&
                      application.StationAuthenticationObservations.Count == 0 &&
                      leakHits == 0;
        StationK0BodyEvidence bodyEvidence = CreateBodyEvidence(body, BaselineComparison.None);

        return new(
            "K0TOK-020-D-signalr-" + subcase + "-test-server",
            "K0TOK-020",
            "D-signalr-" + subcase,
            "public",
            "test-server",
            "Development",
            plan.Method.Method,
            plan.Path.Split('?', 2)[0],
            "protected-non-station-signalr",
            plan.CredentialShape,
            plan.AuthorizationValues.Count,
            plan.AuthorizationValues.Select(static value => value.Length).ToArray(),
            serverRequest.AuthorizationFieldValueCount,
            serverRequest.AuthorizationFieldValueLengths,
            ordinaryResult,
            application.StationEvidenceProbe.SchemeObservations.ToArray(),
            [],
            finalAuthenticationTypes,
            expectedFinalIdentityCount == 0
                ? "ordinary-bearer-challenge"
                : "ordinary-bearer-authenticated",
            (int)response.StatusCode,
            bodyEvidence.Mode,
            bodyEvidence.Length,
            bodyEvidence.Sha256,
            StationK0EvidenceHashing.HashSelectedHeaders(response),
            null,
            null,
            "not-instrumented-composed-host; SignalR auth probes only",
            "application",
            GetSanitizedLogCategories(application.StationEvidenceLogs.Entries).Length,
            logExceptionCount,
            GetSanitizedLogCategories(application.StationEvidenceLogs.Entries),
            StationK0EvidenceHashing.HashSanitizedLogCategories(application.StationEvidenceLogs.Entries),
            leakCanaries,
            RequestTimeoutMilliseconds,
            1,
            StationK0EvidenceHashing.HashConfiguration(
                "public",
                "test-server",
                "production-composition-feature-off",
                enabled: false,
                epoch: 0,
                eligibleCount: 0),
            "ordinary SignalR ownership preserved; no Station scheme selection or identity",
            "HTTP " + (int)response.StatusCode + "; ordinary=" + ordinaryResult +
            "; final-identities=" + finalAuthenticationTypes.Count,
            passed);
    }

    private static StationEndpointInventory BuildCompositionInventory(
        IServiceProvider services,
        string hostSurface)
    {
        EndpointDataSource[] dataSources = services.GetServices<EndpointDataSource>().ToArray();
        Assert.NotEmpty(dataSources);
        return StationEndpointInventoryBuilder.Build(
            dataSources,
            hostSurface,
            services.GetRequiredService<IAuthorizationPolicyProvider>());
    }

    private static StationK0CompositionInventoryEvidence AssertCompositionInventory(
        StationEndpointInventory inventory,
        string host,
        string environment,
        string configurationVariant,
        int expectedEndpointCount,
        string? expectedCanonicalSha256)
    {
        Assert.Equal(expectedEndpointCount, inventory.Payload.Endpoints.Count);
        if (expectedCanonicalSha256 is not null)
        {
            Assert.Equal(expectedCanonicalSha256, inventory.CanonicalSha256);
        }

        Assert.Equal(0, inventory.EligibleCount);
        Assert.All(
            inventory.Payload.Endpoints,
            static endpoint =>
            {
                Assert.False(endpoint.StationAwarePolicy);
                Assert.Equal(nameof(StationOperationClass.Forbidden), endpoint.EffectiveClass);
            });
        Assert.Contains(
            inventory.Payload.Endpoints,
            static endpoint => endpoint.Identity.RawRoute == "/alive");
        Assert.Contains(
            inventory.Payload.Endpoints,
            static endpoint => endpoint.Identity.RawRoute == "/health");
        Assert.Contains(
            inventory.Payload.NonRouteExclusions,
            static exclusion => exclusion.Name == "development-swagger-middleware");

        return new(
            host,
            environment,
            configurationVariant,
            inventory.Payload.Endpoints.Count,
            inventory.Payload.Endpoints.Count(static endpoint => endpoint.AuthorizationKind == "protected"),
            inventory.Payload.Endpoints.Count(static endpoint =>
                endpoint.AuthorizationKind == "explicit-anonymous"),
            inventory.Payload.Endpoints.Count(static endpoint => endpoint.AuthorizationKind == "implicit-open"),
            inventory.EligibleCount,
            inventory.Payload.Endpoints.Count(static endpoint => endpoint.StationAwarePolicy),
            inventory.CanonicalSha256,
            inventory.Payload.NonRouteExclusions
                .Select(static exclusion => exclusion.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static RequestPlan Plan(string path, string authorization, string credentialShape) =>
        new(HttpMethod.Get, path, [authorization], null, null, credentialShape);

    private static RequestPlan Plan(
        string path,
        IReadOnlyList<string> authorizationValues,
        string credentialShape) =>
        new(HttpMethod.Get, path, authorizationValues, null, null, credentialShape);

    private static HttpRequestMessage CreateRequest(RequestPlan plan)
    {
        HttpRequestMessage request = new(plan.Method, plan.Path);
        if (plan.AuthorizationValues.Count > 0 &&
            !request.Headers.TryAddWithoutValidation("Authorization", plan.AuthorizationValues))
        {
            request.Dispose();
            throw new InvalidOperationException("The evidence request rejected the Authorization shape.");
        }

        if (plan.Cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", plan.Cookie);
        }

        if (plan.WebSocketProtocol is not null)
        {
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Protocol", plan.WebSocketProtocol);
        }

        return request;
    }

    private static string ClassifyOrdinaryAuthentication(
        IReadOnlyList<StationK0SchemeProbeObservation> observations)
    {
        if (observations.Any(static observation =>
                string.Equals(observation.Category, "succeeded", StringComparison.Ordinal)))
        {
            return "succeeded";
        }

        if (observations.Any(static observation =>
                observation.Category.StartsWith("failed:", StringComparison.Ordinal)))
        {
            return "failed";
        }

        return observations.Any(static observation =>
                string.Equals(observation.Category, "invoked", StringComparison.Ordinal))
            ? "no-result"
            : "not-invoked";
    }

    private static List<StationK0LeakCanaryEvidence> InspectCanaries(
        byte[] body,
        HttpResponseMessage response,
        IReadOnlyList<StationK0LogEntry> logs,
        IReadOnlyList<string> canaries)
    {
        string bodyText = Encoding.UTF8.GetString(body);
        string headerText = string.Join(
            "\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(static header => header.Value.Select(value => header.Key + ":" + value)));
        string logText = string.Join(
            "\n",
            logs.Select(static entry =>
                entry.Category + "\n" + entry.Message + "\n" + entry.ExceptionText));
        Assert.NotEmpty(canaries);
        List<StationK0LeakCanaryEvidence> evidence = [];
        for (int index = 0; index < canaries.Count; index++)
        {
            string canary = canaries[index];
            Assert.False(string.IsNullOrEmpty(canary));
            evidence.Add(new(
                "canary-" + (index + 1).ToString("000", CultureInfo.InvariantCulture),
                bodyText.Contains(canary, StringComparison.Ordinal) ? 1 : 0,
                headerText.Contains(canary, StringComparison.Ordinal) ? 1 : 0,
                logText.Contains(canary, StringComparison.Ordinal) ? 1 : 0));
        }

        return evidence;
    }

    private static int CountLeakHits(IReadOnlyList<StationK0LeakCanaryEvidence> canaries) =>
        canaries.Sum(static canary => canary.BodyHits + canary.HeaderHits + canary.LogHits);

    private static string[] GetSanitizedLogCategories(
        IReadOnlyList<StationK0LogEntry> logs) =>
        logs.Select(static entry => entry.Category + "|" + entry.Level)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static StationK0BodyEvidence CreateBodyEvidence(
        byte[] body,
        BaselineComparison baselineComparison)
    {
        if (baselineComparison == BaselineComparison.BoundedDiagnostics)
        {
            (int length, string sha256) =
                StationK0EvidenceHashing.HashPrometheusDescriptorContract(body);
            return new("prometheus-help-type-descriptors-v1", length, sha256);
        }

        return new(
            "normalized-json-or-raw-v2",
            body.Length,
            StationK0EvidenceHashing.HashBody(body));
    }

    private static StationK0SentinelEvidence Snapshot(StationK0TestSentinels sentinels) =>
        new(
            sentinels.Binder,
            sentinels.Validator,
            sentinels.Access,
            sentinels.Endpoint,
            sentinels.Domain,
            sentinels.Mutation);

    private static void AssertSentinels(
        StationK0TestSentinels sentinels,
        SentinelExpectation expectation)
    {
        StationK0SentinelEvidence actual = Snapshot(sentinels);
        StationK0SentinelEvidence expected = expectation switch
        {
            SentinelExpectation.Zero => new(0, 0, 0, 0, 0, 0),
            SentinelExpectation.OrdinaryPositive => new(0, 0, 1, 1, 1, 0),
            SentinelExpectation.StationPositive => new(1, 1, 1, 1, 1, 0),
            _ => throw new InvalidOperationException("Unknown sentinel expectation.")
        };
        Assert.Equal(expected, actual);
    }

    private static void AssertCompleteDenominator(
        List<StationK0TokenEvidenceObservation> observations,
        IReadOnlyList<StationK0CompositionInventoryEvidence> compositionInventories)
    {
        Assert.Equal(
            observations.Count,
            observations.Select(static item => item.ObservationId).Distinct(StringComparer.Ordinal).Count());
        string[] observedCases = observations
            .Select(static item => item.CaseId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedCases = Enumerable.Range(1, 22)
            .Select(static value => "K0TOK-" + value.ToString("000", CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(expectedCases, observedCases);
        string[] actualObservationIds = observations
            .Select(static item => item.ObservationId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(FrozenObservationIds, actualObservationIds);
        Assert.Contains(observations, static item =>
            item.CaseId == "K0TOK-015" &&
            item.Subcase == "C-kestrel-65536" &&
            item.StatusCode == 431 &&
            item.ApplicationReach == "transport-only");
        foreach (string caseId in new[] { "K0TOK-016", "K0TOK-017" })
        {
            Assert.Contains(observations, item => item.CaseId == caseId && item.Transport == "test-server");
            Assert.Contains(observations, item => item.CaseId == caseId && item.Transport == "kestrel-loopback");
        }

        Assert.Equal(3, compositionInventories.Count);
        Assert.Contains(compositionInventories, static item =>
            item.Host == "public" &&
            item.ConfigurationVariant == "standard" &&
            item.EndpointCount == 323 &&
            item.ProtectedEndpointCount == 290);
        Assert.Contains(compositionInventories, static item =>
            item.Host == "admin" &&
            item.ConfigurationVariant == "standard" &&
            item.EndpointCount == 181 &&
            item.ProtectedEndpointCount == 179);
        Assert.Contains(compositionInventories, static item =>
            item.Host == "public" &&
            item.ConfigurationVariant == "prometheus-enabled" &&
            item.EndpointCount == 324);
        Assert.All(compositionInventories, static item =>
        {
            Assert.Equal(0, item.EligibleEndpointCount);
            Assert.Equal(0, item.StationAwarePolicyCount);
        });
    }

    private sealed record StationK0BaselineCapture(
        HttpStatusCode StatusCode,
        string BodyEvidenceMode,
        int BodyEvidenceLength,
        string BodyEvidenceSha256,
        string SelectedHeadersSha256,
        string? ContentType,
        string OrdinaryAuthenticationResult,
        int StationSchemeEventCount,
        IReadOnlyList<string> FinalAuthenticationTypes,
        int SanitizedLogExceptionCount);

    private sealed record StationK0BodyEvidence(
        string Mode,
        int Length,
        string Sha256);

    private sealed record RequestPlan(
        HttpMethod Method,
        string Path,
        IReadOnlyList<string> AuthorizationValues,
        string? Cookie,
        string? WebSocketProtocol,
        string CredentialShape) : IDisposable
    {
        public RequestPlan WithoutCredentials() =>
            this with
            {
                AuthorizationValues = [],
                Cookie = null,
                WebSocketProtocol = null,
                CredentialShape = "no-credential-baseline"
            };

        public void Dispose()
        {
        }
    }

    private enum SentinelExpectation
    {
        Zero,
        OrdinaryPositive,
        StationPositive
    }

    private enum BaselineComparison
    {
        None,
        Exact,
        BoundedDiagnostics
    }
}

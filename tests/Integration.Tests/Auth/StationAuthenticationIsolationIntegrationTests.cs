namespace Integration.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using BunkFy.Host.ServiceDefaults.Security.Stations;
using Integration.Tests.Support;
using Microsoft.IdentityModel.Tokens;
using Xunit;

[Trait("Category", "Integration")]
public sealed class StationAuthenticationIsolationIntegrationTests
{
    [Fact]
    public async Task Ordinary_and_station_schemes_remain_isolated()
    {
        await using StationK0TestApplication application = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Validation)
            .ConfigureAwait(true);
        using HttpClient client = application.CreateClient();
        string ordinaryToken = application.CreateOrdinaryToken();
        string stationToken = application.CreateStationToken();

        using (HttpResponseMessage ordinary = await SendAsync(
                   client,
                   "/ordinary",
                   "Bearer",
                   ordinaryToken).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NoContent, ordinary.StatusCode);
            Assert.Empty(application.Observer.Observations);
            Assert.Equal(1, application.Sentinels.Access);
            Assert.Equal(1, application.Sentinels.Endpoint);
            Assert.Equal(1, application.Sentinels.Domain);
        }

        application.ResetObservations();
        using (HttpResponseMessage stationAgainstOrdinary = await SendAsync(
                   client,
                   "/ordinary",
                   "Station",
                   stationToken).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stationAgainstOrdinary.StatusCode);
            Assert.Empty(application.Observer.Observations);
            AssertAllSentinelsZero(application.Sentinels);
        }

        application.ResetObservations();
        using HttpResponseMessage ordinaryAgainstStation = await SendAsync(
            client,
            "/station-only-eligible",
            "Bearer",
            ordinaryToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, ordinaryAgainstStation.StatusCode);
        StationAuthenticationObservation observation = Assert.Single(application.Observer.Observations);
        Assert.Equal(StationAuthenticationResultKind.NoResult, observation.Result);
        Assert.Equal("station_header_other_scheme", observation.Category);
        AssertAllSentinelsZero(application.Sentinels);

        application.ResetObservations();
        string ordinaryClaimsWithStationSignature = application.CreateOrdinaryToken(signWithStationKey: true);
        using (HttpResponseMessage wrongOrdinarySignature = await SendAsync(
                   client,
                   "/ordinary",
                   "Bearer",
                   ordinaryClaimsWithStationSignature).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongOrdinarySignature.StatusCode);
            Assert.Empty(application.Observer.Observations);
            AssertAllSentinelsZero(application.Sentinels);
        }

        application.ResetObservations();
        using HttpResponseMessage wrongOrdinarySignatureAtStation = await SendAsync(
            client,
            "/station-only-eligible",
            "Bearer",
            ordinaryClaimsWithStationSignature).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongOrdinarySignatureAtStation.StatusCode);
        AssertAllSentinelsZero(application.Sentinels);

        application.ResetObservations();
        using HttpResponseMessage ordinaryClaimsAsStation = await SendAsync(
            client,
            "/station-only-eligible",
            "Station",
            ordinaryClaimsWithStationSignature).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, ordinaryClaimsAsStation.StatusCode);
        Assert.Equal(
            StationAuthenticationResultKind.Failed,
            Assert.Single(application.Observer.Observations).Result);
        AssertAllSentinelsZero(application.Sentinels);
    }

    [Fact]
    public async Task Feature_gate_and_dual_policy_resolve_before_binding()
    {
        await using StationK0TestApplication featureOff = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.FeatureOff)
            .ConfigureAwait(true);
        using HttpClient featureOffClient = featureOff.CreateClient();
        string stationToken = featureOff.CreateStationToken();

        using (HttpResponseMessage stationOnly = await SendAsync(
                   featureOffClient,
                   "/station-only-eligible",
                   "Station",
                   stationToken).ConfigureAwait(true))
        {
            await AssertStationProblemAsync(
                stationOnly,
                HttpStatusCode.Forbidden,
                StationAuthenticationDefaults.OperationForbiddenCode).ConfigureAwait(true);
            Assert.Equal(
                StationAuthenticationResultKind.Succeeded,
                Assert.Single(featureOff.Observer.Observations).Result);
            StationAuthorizationObservation authorization = Assert.Single(
                featureOff.AuthorizationObserver.Observations);
            Assert.Equal(StationAuthorizationResultKind.Forbidden, authorization.Result);
            Assert.Equal(1, authorization.AuthenticatedIdentityCount);
            AssertAllSentinelsZero(featureOff.Sentinels);
        }

        featureOff.ResetObservations();
        using (HttpResponseMessage ordinaryDual = await SendAsync(
                   featureOffClient,
                   "/dual-eligible",
                   "Bearer",
                   featureOff.CreateOrdinaryToken()).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NoContent, ordinaryDual.StatusCode);
            Assert.Equal(
                StationAuthenticationResultKind.NoResult,
                Assert.Single(featureOff.Observer.Observations).Result);
            AssertPositiveStationSentinels(featureOff.Sentinels);
        }

        featureOff.ResetObservations();
        using (HttpResponseMessage stationDual = await SendAsync(
                   featureOffClient,
                   "/dual-eligible",
                   "Station",
                   stationToken).ConfigureAwait(true))
        {
            await AssertStationProblemAsync(
                stationDual,
                HttpStatusCode.Forbidden,
                StationAuthenticationDefaults.OperationForbiddenCode).ConfigureAwait(true);
            AssertAllSentinelsZero(featureOff.Sentinels);
        }

        await using StationK0TestApplication validation = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Validation)
            .ConfigureAwait(true);
        using HttpClient validationClient = validation.CreateClient();
        using HttpResponseMessage stationAllowed = await SendAsync(
            validationClient,
            "/station-only-eligible",
            "Station",
            validation.CreateStationToken()).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NoContent, stationAllowed.StatusCode);
        Assert.Equal(
            StationAuthenticationResultKind.Succeeded,
            Assert.Single(validation.Observer.Observations).Result);
        AssertPositiveStationSentinels(validation.Sentinels);
    }

    [Fact]
    public async Task Feature_toggle_off_during_admission_is_denied_before_endpoint_work()
    {
        await using StationK0TestApplication application = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Validation, useControllableFeatureState: true)
            .ConfigureAwait(true);
        using HttpClient client = application.CreateClient();
        string stationToken = application.CreateStationToken();
        StationK0ControllableFeatureState featureState = application.ControllableFeatureState;

        featureState.ArmNextCapture();
        Task<HttpResponseMessage> request = Task.Run(
            () => SendAsync(client, "/station-only-eligible", "Station", stationToken));

        try
        {
            Assert.True(
                featureState.WaitUntilAdmissionReached(TimeSpan.FromSeconds(10)),
                "The request did not reach the Station feature-state admission barrier.");
            Assert.False(request.IsCompleted);
            featureState.Disable();
        }
        finally
        {
            featureState.ReleaseAdmission();
        }

        using HttpResponseMessage response = await request
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(true);
        await AssertStationProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            StationAuthenticationDefaults.OperationForbiddenCode).ConfigureAwait(true);
        StationAuthenticationObservation authentication = Assert.Single(application.Observer.Observations);
        Assert.Equal(StationAuthenticationResultKind.Succeeded, authentication.Result);
        Assert.Equal("station_token_valid", authentication.Category);
        StationAuthorizationObservation authorization = Assert.Single(
            application.AuthorizationObserver.Observations);
        Assert.Equal(StationAuthorizationResultKind.Forbidden, authorization.Result);
        Assert.Equal(1, authorization.AuthenticatedIdentityCount);
        AssertAllSentinelsZero(application.Sentinels);
        AssertLogsDoNotContain(application.Logs, stationToken);
    }

    [Fact]
    public async Task Invalid_station_tokens_are_bounded_challenges_without_endpoint_work()
    {
        await using StationK0TestApplication application = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Validation)
            .ConfigureAwait(true);
        using HttpClient client = application.CreateClient();
        DateTime now = DateTime.UtcNow;
        IReadOnlyList<Claim> required = StationK0TestApplication.CreateRequiredStationClaims();
        (string Id, StationK0TokenSpec Spec, string Category)[] cases =
        [
            ("wrong-key", new() { SigningKey = StationK0SigningKey.Wrong }, "station_token_invalid"),
            ("ordinary-key", new() { SigningKey = StationK0SigningKey.Ordinary }, "station_token_invalid"),
            ("wrong-issuer", new() { Issuer = "urn:bunkfy:test:wrong-issuer" }, "station_token_issuer_invalid"),
            ("wrong-audience", new() { Audience = "urn:bunkfy:test:wrong-audience" },
                "station_token_audience_invalid"),
            ("wrong-type", new() { Type = "not-a-station-token" }, "station_token_type_invalid"),
            ("missing-type", new() { OmitType = true }, "station_token_type_invalid"),
            ("expired", new() { NotBeforeUtc = now.AddMinutes(-10), ExpiresUtc = now.AddMinutes(-5) },
                "station_token_expired"),
            ("not-yet-valid", new() { NotBeforeUtc = now.AddMinutes(5), ExpiresUtc = now.AddMinutes(10) },
                "station_token_not_yet_valid"),
            ("missing-claim", new()
            {
                Claims = required.Where(static claim => claim.Type != "jti").ToArray()
            }, "station_token_claims_invalid"),
            ("empty-claim", new()
            {
                Claims = required
                    .Where(static claim => claim.Type != StationK0TestApplication.ActorIdClaim)
                    .Append(new Claim(StationK0TestApplication.ActorIdClaim, string.Empty))
                    .ToArray()
            }, "station_token_claims_invalid"),
            ("duplicate-claim", new()
            {
                Claims = [.. required, new Claim("sub", "duplicate-station-actor")]
            }, "station_token_claims_invalid"),
            ("wrong-algorithm", new()
            {
                Algorithm = SecurityAlgorithms.HmacSha256Signature
            }, "station_token_algorithm_invalid"),
            ("unsigned", new() { Unsigned = true }, "station_token_algorithm_invalid")
        ];

        foreach ((string id, StationK0TokenSpec specification, string expectedCategory) in cases)
        {
            application.ResetObservations();
            string token = application.CreateStationToken(specification);
            using HttpResponseMessage response = await SendAsync(
                client,
                "/station-only-eligible",
                "Station",
                token).ConfigureAwait(true);

            await AssertStationProblemAsync(
                response,
                HttpStatusCode.Unauthorized,
                StationAuthenticationDefaults.ActorAuthenticationRequiredCode).ConfigureAwait(true);
            StationAuthenticationObservation observation = Assert.Single(application.Observer.Observations);
            Assert.True(
                observation.Result == StationAuthenticationResultKind.Failed,
                $"Case '{id}' unexpectedly produced {observation.Result} ({observation.Category}).");
            Assert.Equal(expectedCategory, observation.Category);
            StationAuthorizationObservation authorization = Assert.Single(
                application.AuthorizationObserver.Observations);
            Assert.Equal(StationAuthorizationResultKind.Challenge, authorization.Result);
            Assert.Equal(0, authorization.AuthenticatedIdentityCount);
            AssertAllSentinelsZero(application.Sentinels);
            AssertLogsDoNotContain(application.Logs, token);
        }
    }

    [Fact]
    public async Task Malformed_mixed_and_non_header_credentials_never_gain_station_authority()
    {
        await using StationK0TestApplication application = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Validation)
            .ConfigureAwait(true);
        using HttpClient client = application.CreateClient();
        string stationToken = application.CreateStationToken();
        string ordinaryToken = application.CreateOrdinaryToken();

        string[] rejectedAuthorizationValues =
        [
            "Station A",
            "Station truncated",
            $"Station {new string('A', StationAuthenticationDefaults.MaximumTokenLength)}",
            $"Station {new string('A', StationAuthenticationDefaults.MaximumTokenLength + 1)}",
            $"Station {stationToken},Station {stationToken}",
            $"Bearer {ordinaryToken},Station {stationToken}"
        ];

        foreach (string authorization in rejectedAuthorizationValues)
        {
            application.ResetObservations();
            using HttpResponseMessage response = await SendRawAuthorizationAsync(
                client,
                "/dual-eligible",
                [authorization]).ConfigureAwait(true);

            AssertIdentityDerivedDeniedStatus(application, response);
            AssertAllSentinelsZero(application.Sentinels);
            AssertLogsDoNotContain(application.Logs, authorization);
        }

        application.ResetObservations();
        using (HttpResponseMessage duplicate = await SendRawAuthorizationAsync(
                   client,
                   "/station-only-eligible",
                   [$"Station {stationToken}", $"Station {stationToken}"]).ConfigureAwait(true))
        {
            AssertIdentityDerivedDeniedStatus(application, duplicate);
            AssertAllSentinelsZero(application.Sentinels);
        }

        application.ResetObservations();
        using (HttpResponseMessage mixedFieldValues = await SendRawAuthorizationAsync(
                   client,
                   "/dual-eligible",
                   [$"Bearer {ordinaryToken}", $"Station {stationToken}"]).ConfigureAwait(true))
        {
            AssertIdentityDerivedDeniedStatus(application, mixedFieldValues);
            AssertAllSentinelsZero(application.Sentinels);
            AssertLogsDoNotContain(application.Logs, ordinaryToken, stationToken);
        }

        application.ResetObservations();
        using (HttpResponseMessage queryOnly = await client
                   .GetAsync($"/station-only-eligible?access_token={Uri.EscapeDataString(stationToken)}")
                   .ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, queryOnly.StatusCode);
            Assert.Equal(
                StationAuthenticationResultKind.NoResult,
                Assert.Single(application.Observer.Observations).Result);
            AssertAllSentinelsZero(application.Sentinels);
        }

        application.ResetObservations();
        using (HttpResponseMessage cookieOnly = await SendWithCookieAsync(
                   client,
                   "/station-only-eligible",
                   stationToken,
                   authorization: null).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
            AssertAllSentinelsZero(application.Sentinels);
        }

        application.ResetObservations();
        using (HttpResponseMessage ordinaryCookieOnly = await SendWithCookieAsync(
            client,
            "/ordinary",
            stationToken,
            authorization: null).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ordinaryCookieOnly.StatusCode);
            Assert.Empty(application.Observer.Observations);
            AssertAllSentinelsZero(application.Sentinels);
            AssertLogsDoNotContain(application.Logs, stationToken);
        }

        application.ResetObservations();
        using HttpResponseMessage ordinaryWithStationCookie = await SendWithCookieAsync(
            client,
            "/ordinary",
            stationToken,
            new AuthenticationHeaderValue("Bearer", ordinaryToken)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, ordinaryWithStationCookie.StatusCode);
        Assert.Empty(application.Observer.Observations);
        Assert.Equal(1, application.Sentinels.Access);
        Assert.Equal(1, application.Sentinels.Endpoint);
        Assert.Equal(1, application.Sentinels.Domain);
    }

    [Fact]
    public async Task Real_kestrel_enforces_transport_limit_and_mixed_header_denial()
    {
        await using StationK0TestApplication application = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Validation, useKestrel: true)
            .ConfigureAwait(true);
        using HttpClient client = application.CreateClient();
        string oversizedCredential = new('A', 65_536);

        using (HttpResponseMessage oversized = await SendRawAuthorizationAsync(
                   client,
                   "/station-only-eligible",
                   [$"Station {oversizedCredential}"]).ConfigureAwait(true))
        {
            Assert.Equal((HttpStatusCode)431, oversized.StatusCode);
            Assert.Empty(application.Observer.Observations);
            AssertAllSentinelsZero(application.Sentinels);
            AssertLogsDoNotContain(application.Logs, oversizedCredential);
        }

        string stationToken = application.CreateStationToken();
        string ordinaryToken = application.CreateOrdinaryToken();
        IReadOnlyList<IReadOnlyList<string>> rejected =
        [
            [$"Station {stationToken}", $"Station {stationToken}"],
            [$"Bearer {ordinaryToken}", $"Station {stationToken}"],
            [$"Station {stationToken},Station {stationToken}"],
            [$"Bearer {ordinaryToken},Station {stationToken}"]
        ];

        foreach (IReadOnlyList<string> values in rejected)
        {
            application.ResetObservations();
            using HttpResponseMessage response = await SendRawAuthorizationAsync(
                client,
                "/dual-eligible",
                values).ConfigureAwait(true);
            AssertIdentityDerivedDeniedStatus(application, response);
            AssertAllSentinelsZero(application.Sentinels);
            AssertLogsDoNotContain(application.Logs, values.ToArray());
        }
    }

    [Fact]
    public async Task Epoch_drift_and_forbidden_class_are_denied_before_endpoint_work()
    {
        await using StationK0TestApplication validation = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Validation)
            .ConfigureAwait(true);
        using HttpClient validationClient = validation.CreateClient();
        using HttpResponseMessage staleEpoch = await SendAsync(
            validationClient,
            "/station-only-eligible",
            "Station",
            validation.CreateStationToken(new()
            {
                Claims = StationK0TestApplication.CreateRequiredStationClaims(epoch: 2)
            })).ConfigureAwait(true);

        await AssertStationProblemAsync(
            staleEpoch,
            HttpStatusCode.Forbidden,
            StationAuthenticationDefaults.OperationForbiddenCode).ConfigureAwait(true);
        AssertAllSentinelsZero(validation.Sentinels);

        await using StationK0TestApplication negative = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Negative)
            .ConfigureAwait(true);
        using HttpClient negativeClient = negative.CreateClient();
        using HttpResponseMessage forbiddenClass = await SendAsync(
            negativeClient,
            "/station-forbidden-negative",
            "Station",
            negative.CreateStationToken()).ConfigureAwait(true);

        await AssertStationProblemAsync(
            forbiddenClass,
            HttpStatusCode.Forbidden,
            StationAuthenticationDefaults.OperationForbiddenCode).ConfigureAwait(true);
        AssertAllSentinelsZero(negative.Sentinels);

        await using StationK0TestApplication alias = await StationK0TestApplication
            .CreateAsync(StationK0TestMode.Alias)
            .ConfigureAwait(true);
        using HttpClient aliasClient = alias.CreateClient();
        using HttpResponseMessage aliasAttempt = await SendAsync(
            aliasClient,
            "/station-alias-negative",
            "Station",
            alias.CreateStationToken()).ConfigureAwait(true);

        await AssertStationProblemAsync(
            aliasAttempt,
            HttpStatusCode.Unauthorized,
            StationAuthenticationDefaults.ActorAuthenticationRequiredCode).ConfigureAwait(true);
        StationAuthenticationObservation aliasObservation = Assert.Single(alias.Observer.Observations);
        Assert.Equal(StationAuthenticationResultKind.Failed, aliasObservation.Result);
        Assert.Equal("station_policy_not_approved", aliasObservation.Category);
        AssertAllSentinelsZero(alias.Sentinels);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string path,
        string scheme,
        string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> SendRawAuthorizationAsync(
        HttpClient client,
        string path,
        IReadOnlyList<string> values)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        Assert.True(request.Headers.TryAddWithoutValidation("Authorization", values));
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> SendWithCookieAsync(
        HttpClient client,
        string path,
        string stationToken,
        AuthenticationHeaderValue? authorization)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = authorization;
        request.Headers.Add("Cookie", $"bunkfy_station={stationToken}");
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task AssertStationProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains(expectedCode, body, StringComparison.Ordinal);
        Assert.InRange(body.Length, 1, 512);

        if (expectedStatus == HttpStatusCode.Unauthorized)
        {
            Assert.Contains(
                response.Headers.WwwAuthenticate,
                static value => string.Equals(value.Scheme, "Station", StringComparison.Ordinal));
        }
    }

    private static void AssertAllSentinelsZero(StationK0TestSentinels sentinels)
    {
        Assert.Equal(0, sentinels.Binder);
        Assert.Equal(0, sentinels.Validator);
        Assert.Equal(0, sentinels.Access);
        Assert.Equal(0, sentinels.Endpoint);
        Assert.Equal(0, sentinels.Domain);
        Assert.Equal(0, sentinels.Mutation);
    }

    private static void AssertPositiveStationSentinels(StationK0TestSentinels sentinels)
    {
        Assert.Equal(1, sentinels.Binder);
        Assert.Equal(1, sentinels.Validator);
        Assert.Equal(1, sentinels.Access);
        Assert.Equal(1, sentinels.Endpoint);
        Assert.Equal(1, sentinels.Domain);
        Assert.Equal(0, sentinels.Mutation);
    }

    private static void AssertIdentityDerivedDeniedStatus(
        StationK0TestApplication application,
        HttpResponseMessage response)
    {
        StationK0FinalIdentityObservation final = Assert.Single(application.Observer.FinalIdentities);
        HttpStatusCode expected = final.AuthenticationTypes.Count == 0
            ? HttpStatusCode.Unauthorized
            : HttpStatusCode.Forbidden;
        Assert.Equal(expected, response.StatusCode);
        StationAuthorizationObservation authorization = Assert.Single(
            application.AuthorizationObserver.Observations);
        Assert.Equal(final.AuthenticationTypes.Count, authorization.AuthenticatedIdentityCount);
        Assert.Equal(
            expected == HttpStatusCode.Unauthorized
                ? StationAuthorizationResultKind.Challenge
                : StationAuthorizationResultKind.Forbidden,
            authorization.Result);
    }

    private static void AssertLogsDoNotContain(
        StationK0LogCollector logs,
        params string[] prohibitedValues)
    {
        foreach (StationK0LogEntry entry in logs.Entries)
        {
            string payload = $"{entry.Category}\n{entry.Message}\n{entry.ExceptionText}";
            foreach (string prohibitedValue in prohibitedValues)
            {
                Assert.False(
                    payload.Contains(prohibitedValue, StringComparison.Ordinal),
                    "A captured log entry contained prohibited credential material.");
            }
        }
    }
}

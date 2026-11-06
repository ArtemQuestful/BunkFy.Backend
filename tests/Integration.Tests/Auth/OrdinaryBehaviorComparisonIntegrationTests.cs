namespace Integration.Tests;

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Host.Api;
using BunkFy.Modules.Properties.Contracts;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Security;
using Gma.Framework.Notifications;
using Gma.Modules.Auth.Application.ExternalAuthentication;
using Gma.Modules.Auth.Application.Ports;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Auth.Domain.Services;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

[Trait("Category", "Docker")]
[Trait("Category", "Integration")]
public sealed class OrdinaryBehaviorComparisonIntegrationTests
{
    private const string TenantId = "b1000000-0000-0000-0000-000000000001";
    private const string ValidTotpCode = "123456";
    private const string RecoveryCode = "cmp-recovery-code";
    private const string NewRecoveryPassword = "Passw0rd!cmp-recovered";
    private static readonly System.Text.Json.JsonSerializerOptions WebJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    [DockerFact]
    public async Task Full_hosts_emit_the_frozen_twenty_six_operation_ordinary_behavior_denominator()
    {
        Assert.Equal(
            OrdinaryBehaviorComparisonEvidence.OperationDenominator,
            OrdinaryBehaviorComparisonEvidence.OperationIds.Length);
        Assert.Equal(
            OrdinaryBehaviorComparisonEvidence.OperationIds.Length,
            OrdinaryBehaviorComparisonEvidence.OperationIds.Distinct(StringComparer.Ordinal).Count());

        string variant = RequireVariant();
        string outputDirectory = RequireOutputDirectory();
        string buildFingerprint = RequireBuildFingerprint();
        Dictionary<string, OrdinaryBehaviorObservation> observations = new(StringComparer.Ordinal);

        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync().ConfigureAwait(false);
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase($"bunkfy_cmp_{variant.Replace('-', '_')}")
            .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        string connectionString = postgreSql.GetConnectionString();
        string natsConnectionString = AuthTestContainers.GetNatsConnectionString(nats);
        DeterministicOneTimeTokenService oneTimeTokens = new();
        DeterministicPasswordRecoveryTokenService recoveryTokens = new();
        DeterministicExternalAuthenticationExchangeStore exchangeStore = new();
        DeterministicTotpProvider totp = new();
        DeterministicSecretProtector secretProtector = new();
        OrdinaryBehaviorRuntimeProbe publicProbe = new();

        await using AuthTestApplication application = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            configureServices: services => ConfigurePublicEvidenceServices(
                services,
                publicProbe,
                oneTimeTokens,
                recoveryTokens,
                exchangeStore,
                totp,
                secretProtector));
        await application.MigratePropertiesAuthorizationDatabaseAsync().ConfigureAwait(false);
        using HttpClient client = application.CreateClient();

        Guid ownerId = Guid.Parse("b1000000-0000-0000-0000-0000000000aa");
        await using AdminCliTestApplication adminCli = new(
            "PostgreSql",
            connectionString,
            includeProperties: true);
        await adminCli.MigrateAsync().ConfigureAwait(false);
        await AssertCliSuccessAsync(adminCli.ExecuteAsync(
            "admin", "bootstrap",
            "--actor", ownerId.ToString("D"),
            "--yes")).ConfigureAwait(false);

        await ExecutePrimaryAuthenticationOperationsAsync(
            application,
            client,
            publicProbe,
            observations).ConfigureAwait(false);
        await ExecuteMultiFactorOperationsAsync(
            client,
            publicProbe,
            observations).ConfigureAwait(false);
        await ExecuteRecoveryOperationsAsync(
            application,
            client,
            publicProbe,
            observations).ConfigureAwait(false);
        await ExecuteExternalAuthenticationOperationsAsync(
            client,
            publicProbe,
            exchangeStore,
            oneTimeTokens,
            observations).ConfigureAwait(false);
        await ExecuteAccessOperationsAsync(
            application,
            adminCli,
            ownerId,
            client,
            publicProbe,
            observations).ConfigureAwait(false);
        IReadOnlyList<OrdinaryBehaviorRuntimeSnapshot> rateLimitRuntime = await ExecuteRateLimitOperationAsync(
            connectionString,
            natsConnectionString,
            observations).ConfigureAwait(false);

        OrdinaryBehaviorRuntimeProbe adminProbe = new();
        await using AdminApiTestApplication adminApplication = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            disableDatabaseBackgroundServices: true,
            configureServices: services => ConfigureProbe(services, adminProbe));
        await adminApplication.MigrateAsync().ConfigureAwait(false);
        await ExecuteAdminOperationsAsync(
            adminApplication,
            ownerId,
            adminProbe,
            observations).ConfigureAwait(false);

        OrdinaryBehaviorRuntimeProbe signalRProbe = new();
        await using NotificationStreamingTestApplication signalRApplication = new(
            configureServices: services => ConfigureProbe(services, signalRProbe));
        await ExecuteSignalROperationAsync(
            signalRApplication,
            signalRProbe,
            observations).ConfigureAwait(false);
        await ExecuteSseOperationAsync(
            signalRApplication,
            signalRProbe,
            observations).ConfigureAwait(false);

        OrdinaryBehaviorObservation[] ordered = observations.Values
            .OrderBy(static item => item.Ordinal)
            .ToArray();
        Assert.Equal(OrdinaryBehaviorComparisonEvidence.OperationDenominator, ordered.Length);
        Assert.Equal(OrdinaryBehaviorComparisonEvidence.OperationIds, ordered.Select(static item => item.Id));

        List<OrdinaryBehaviorRuntimeSnapshot> rawRuntime = [];
        rawRuntime.AddRange(publicProbe.RuntimeSnapshots);
        rawRuntime.AddRange(adminProbe.RuntimeSnapshots);
        rawRuntime.AddRange(signalRProbe.RuntimeSnapshots);
        rawRuntime.AddRange(rateLimitRuntime);
        Assert.Equal(OrdinaryBehaviorComparisonEvidence.ObservationDenominator / 2, ordered.Length);
        Assert.NotEmpty(rawRuntime);
        Assert.All(ordered, static observation =>
            Assert.True(
                string.Equals(observation.Result, "pass", StringComparison.Ordinal),
                $"{observation.Id} expected {observation.ExpectedStatus} but observed {observation.ActualStatus}."));
        Assert.All(ordered, static observation => Assert.NotNull(observation.Runtime));
        await OrdinaryBehaviorComparisonEvidence.WriteAsync(
            outputDirectory,
            variant,
            buildFingerprint,
            ordered,
            rawRuntime).ConfigureAwait(false);
    }

    private static async Task ExecutePrimaryAuthenticationOperationsAsync(
        AuthTestApplication application,
        HttpClient client,
        OrdinaryBehaviorRuntimeProbe probe,
        IDictionary<string, OrdinaryBehaviorObservation> observations)
    {
        const string username = "primary@cmp.test";
        using CapturedOperation register = await CaptureAsync(
            Operation(1, "POST", "/api/auth/register", "primary-account", "initial", "anonymous", "new-user", 200),
            client,
            probe,
            JsonRequest(HttpMethod.Post, "/api/auth/register", new RegisterMemberRequest(
                username,
                UsernameType.Email,
                AuthApiClient.Password))).ConfigureAwait(false);
        Add(observations, register.Observation);
        AuthTokensResponse registered = Read<AuthTokensResponse>(register.Body);

        using CapturedOperation conflict = await CaptureAsync(
            Operation(2, "POST", "/api/auth/register", "primary-account", "duplicate", "anonymous", "existing-user", 409),
            client,
            probe,
            JsonRequest(HttpMethod.Post, "/api/auth/register", new RegisterMemberRequest(
                username,
                UsernameType.Email,
                AuthApiClient.Password))).ConfigureAwait(false);
        Add(observations, conflict.Observation);

        using CapturedOperation login = await CaptureAsync(
            Operation(3, "POST", "/api/auth/login", "primary-account", "valid-credentials", "anonymous", "existing-user", 200),
            client,
            probe,
            JsonRequest(HttpMethod.Post, "/api/auth/login", new LoginMemberRequest(
                username,
                AuthApiClient.Password))).ConfigureAwait(false);
        Add(observations, login.Observation);
        AuthTokensResponse loggedIn = Read<AuthTokensResponse>(login.Body);

        using CapturedOperation invalidLogin = await CaptureAsync(
            Operation(4, "POST", "/api/auth/login", "primary-account", "invalid-password", "anonymous", "existing-user", 401),
            client,
            probe,
            JsonRequest(HttpMethod.Post, "/api/auth/login", new LoginMemberRequest(
                username,
                "wrong-password"))).ConfigureAwait(false);
        Add(observations, invalidLogin.Observation);

        using CapturedOperation sessions = await CaptureAsync(
            Operation(13, "GET", "/api/auth/sessions", "primary-account", "two-active-sessions", "ordinary-bearer", "current-user", 200),
            client,
            probe,
            Request(HttpMethod.Get, "/api/auth/sessions", loggedIn.AccessToken)).ConfigureAwait(false);
        Add(observations, sessions.Observation);
        AuthenticationSessionsResponse active = Read<AuthenticationSessionsResponse>(sessions.Body);
        AuthenticationSessionResponse other = Assert.Single(active.Sessions, static session => !session.IsCurrent);

        using CapturedOperation revoke = await CaptureAsync(
            Operation(14, "POST", "/api/auth/sessions/{sessionId}/sign-out", "primary-account", "other-active-session", "ordinary-bearer", "current-user", 204),
            client,
            probe,
            Request(HttpMethod.Post, $"/api/auth/sessions/{other.SessionId:D}/sign-out", loggedIn.AccessToken)).ConfigureAwait(false);
        Add(observations, revoke.Observation);
        using (HttpResponseMessage revokedSession = await SendAsync(
                   client,
                   Request(HttpMethod.Get, "/api/auth/sessions", registered.AccessToken)).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, revokedSession.StatusCode);
        }

        using CapturedOperation refresh = await CaptureAsync(
            Operation(8, "POST", "/api/auth/refresh", "primary-account", "valid-refresh", "anonymous-with-token-body", "existing-user", 200),
            client,
            probe,
            JsonRequest(HttpMethod.Post, "/api/auth/refresh", new RefreshTokenRequest(
                loggedIn.AccessToken,
                loggedIn.RefreshToken))).ConfigureAwait(false);
        Add(observations, refresh.Observation);
        AuthTokensResponse refreshed = Read<AuthTokensResponse>(refresh.Body);

        using CapturedOperation invalidRefresh = await CaptureAsync(
            Operation(9, "POST", "/api/auth/refresh", "primary-account", "invalid-refresh", "anonymous-with-token-body", "existing-user", 401),
            client,
            probe,
            JsonRequest(HttpMethod.Post, "/api/auth/refresh", new RefreshTokenRequest(
                "invalid-access-token",
                "invalid-refresh-token"))).ConfigureAwait(false);
        Add(observations, invalidRefresh.Observation);

        using CapturedOperation stepUp = await CaptureAsync(
            Operation(10, "POST", "/api/auth/step-up/password", "primary-account", "valid-password-proof", "ordinary-bearer", "current-user", 200),
            client,
            probe,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/step-up/password",
                new PasswordStepUpRequest(AuthApiClient.Password, refreshed.RefreshToken),
                refreshed.AccessToken)).ConfigureAwait(false);
        Add(observations, stepUp.Observation);
        AuthTokensResponse steppedUp = Read<AuthTokensResponse>(stepUp.Body);

        using CapturedOperation failedStepUp = await CaptureAsync(
            Operation(11, "POST", "/api/auth/step-up/password", "primary-account", "invalid-password-proof", "ordinary-bearer", "current-user", 401),
            client,
            probe,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/step-up/password",
                new PasswordStepUpRequest("wrong-password", steppedUp.RefreshToken),
                steppedUp.AccessToken)).ConfigureAwait(false);
        Add(observations, failedStepUp.Observation);

        using CapturedOperation signOut = await CaptureAsync(
            Operation(12, "POST", "/api/auth/sign-out", "primary-account", "active-session", "ordinary-bearer", "current-user", 204),
            client,
            probe,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/sign-out",
                new SignOutRequest(steppedUp.RefreshToken),
                steppedUp.AccessToken)).ConfigureAwait(false);
        Add(observations, signOut.Observation);
        using (HttpResponseMessage signedOutRefresh = await SendAsync(
                   client,
                   JsonRequest(
                       HttpMethod.Post,
                       "/api/auth/refresh",
                       new RefreshTokenRequest(steppedUp.AccessToken, steppedUp.RefreshToken))).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, signedOutRefresh.StatusCode);
        }

        Assert.False(string.IsNullOrWhiteSpace(registered.AccessToken));
        _ = application;
    }

    private static async Task ExecuteMultiFactorOperationsAsync(
        HttpClient client,
        OrdinaryBehaviorRuntimeProbe probe,
        IDictionary<string, OrdinaryBehaviorObservation> observations)
    {
        const string username = "mfa@cmp.test";
        AuthTokensResponse registered = await AuthApiClient.RegisterAsync(client, TenantId, username).ConfigureAwait(false);
        using HttpResponseMessage enrollmentResponse = await SendAsync(
            client,
            Request(HttpMethod.Post, "/api/auth/mfa/totp/enrollment", registered.AccessToken)).ConfigureAwait(false);
        enrollmentResponse.EnsureSuccessStatusCode();

        using HttpResponseMessage activationResponse = await SendAsync(
            client,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/mfa/totp/activate",
                new ActivateTotpRequest(ValidTotpCode, registered.RefreshToken),
                registered.AccessToken)).ConfigureAwait(false);
        activationResponse.EnsureSuccessStatusCode();

        using CapturedOperation challenge = await CaptureAsync(
            Operation(6, "POST", "/api/auth/login", "mfa-account", "active-totp", "anonymous", "existing-user", 202),
            client,
            probe,
            JsonRequest(HttpMethod.Post, "/api/auth/login", new LoginMemberRequest(
                username,
                AuthApiClient.Password))).ConfigureAwait(false);
        Add(observations, challenge.Observation);
        MultiFactorChallengeResponse challengeBody = Read<MultiFactorChallengeResponse>(challenge.Body);

        using CapturedOperation complete = await CaptureAsync(
            Operation(7, "POST", "/api/auth/mfa/challenges/complete", "mfa-account", "valid-totp", "anonymous-with-challenge", "existing-user", 200),
            client,
            probe,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/mfa/challenges/complete",
                new CompleteMultiFactorChallengeRequest(
                    challengeBody.ChallengeToken,
                    MultiFactorCodeType.Totp,
                    ValidTotpCode))).ConfigureAwait(false);
        Add(observations, complete.Observation);
    }

    private static async Task ExecuteRecoveryOperationsAsync(
        AuthTestApplication application,
        HttpClient client,
        OrdinaryBehaviorRuntimeProbe probe,
        IDictionary<string, OrdinaryBehaviorObservation> observations)
    {
        const string username = "recovery@cmp.test";
        AuthTokensResponse registered = await AuthApiClient.RegisterAsync(client, TenantId, username).ConfigureAwait(false);
        Guid memberId = GetClaimGuid(registered.AccessToken, ClaimTypes.NameIdentifier, "nameid", "sub");
        using HttpResponseMessage methodsResponse = await SendAsync(
            client,
            Request(HttpMethod.Get, "/api/auth/methods", registered.AccessToken)).ConfigureAwait(false);
        methodsResponse.EnsureSuccessStatusCode();
        AuthenticationMethodsResponse methods = await methodsResponse.Content
            .ReadFromJsonAsync<AuthenticationMethodsResponse>().ConfigureAwait(false) ??
            throw new InvalidOperationException("Authentication methods response was empty.");
        AuthenticationEmailResponse email = Assert.Single(methods.Emails);
        using HttpResponseMessage verificationRequest = await SendAsync(
            client,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/email-verification",
                new RequestEmailVerificationRequest(email.Id),
                registered.AccessToken)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Accepted, verificationRequest.StatusCode);
        string verificationCode = await application
            .WaitForEmailVerificationCodeAsync(memberId, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        using HttpResponseMessage verificationConfirmation = await SendAsync(
            client,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/email-verification/confirm",
                new ConfirmEmailVerificationRequest(verificationCode))).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.NoContent, verificationConfirmation.StatusCode);

        using CapturedOperation request = await CaptureAsync(
            Operation(15, "POST", "/api/auth/password-recovery", "verified-recovery-account", "eligible-email", "anonymous", "existing-user", 202),
            client,
            probe,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/password-recovery",
                new PasswordRecoveryRequest(username))).ConfigureAwait(false);
        Add(observations, request.Observation);

        using CapturedOperation complete = await CaptureAsync(
            Operation(16, "POST", "/api/auth/password-recovery/confirm", "verified-recovery-account", "valid-one-time-code", "anonymous-with-recovery-code", "existing-user", 204),
            client,
            probe,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/password-recovery/confirm",
                new ConfirmPasswordRecoveryRequest(RecoveryCode, NewRecoveryPassword))).ConfigureAwait(false);
        Add(observations, complete.Observation);
        using HttpResponseMessage recoveredLogin = await SendAsync(
            client,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/login",
                new LoginMemberRequest(username, NewRecoveryPassword))).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, recoveredLogin.StatusCode);
        AuthTokensResponse recoveredTokens = Read<AuthTokensResponse>(
            await recoveredLogin.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.False(string.IsNullOrWhiteSpace(recoveredTokens.AccessToken));
    }

    private static async Task ExecuteExternalAuthenticationOperationsAsync(
        HttpClient client,
        OrdinaryBehaviorRuntimeProbe probe,
        DeterministicExternalAuthenticationExchangeStore exchangeStore,
        DeterministicOneTimeTokenService tokenService,
        IDictionary<string, OrdinaryBehaviorObservation> observations)
    {
        AuthTokensResponse linkTarget = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "external-link@cmp.test").ConfigureAwait(false);
        string scopeId = GetClaimString(linkTarget.AccessToken, ApplicationClaimNames.ScopeId);
        Guid memberId = GetClaimGuid(linkTarget.AccessToken, ClaimTypes.NameIdentifier, "nameid", "sub");
        Guid sessionId = GetClaimGuid(linkTarget.AccessToken, "sid", "session_id", "gma_session_id");

        const string signInCode = "cmp-external-sign-in";
        exchangeStore.Add(CreateExchange(
            tokenService,
            signInCode,
            scopeId,
            ExternalAuthenticationIntent.SignIn,
            "external-sign-in-subject",
            "external-sign-in@cmp.test"));
        using CapturedOperation signIn = await CaptureAsync(
            Operation(17, "POST", "/api/auth/external/exchange", "external-sign-in", "validated-provider-handoff", "anonymous-with-exchange-code", "new-external-user", 200),
            client,
            probe,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/external/exchange",
                new ExternalAuthenticationExchangeRequest(signInCode))).ConfigureAwait(false);
        Add(observations, signIn.Observation);
        Assert.True(
            signIn.Response.StatusCode == HttpStatusCode.OK,
            $"External sign-in exchange failed with {(int)signIn.Response.StatusCode}; " +
            $"storeMatched={exchangeStore.LastConsumeMatched}, exchangeScope={exchangeStore.LastConsumedScopeId}, " +
            $"tokenScope={scopeId}: {signIn.Body}");

        const string linkCode = "cmp-external-link";
        exchangeStore.Add(CreateExchange(
            tokenService,
            linkCode,
            scopeId,
            ExternalAuthenticationIntent.Link,
            "external-link-subject",
            "external-link-provider@cmp.test",
            memberId,
            sessionId));
        using CapturedOperation link = await CaptureAsync(
            Operation(18, "POST", "/api/auth/external/exchange", "external-link", "authorized-provider-handoff", "ordinary-bearer-with-exchange-code", "current-user", 200),
            client,
            probe,
            JsonRequest(
                HttpMethod.Post,
                "/api/auth/external/exchange",
                new ExternalAuthenticationExchangeRequest(linkCode),
                linkTarget.AccessToken)).ConfigureAwait(false);
        Add(observations, link.Observation);
        Assert.True(
            link.Response.StatusCode == HttpStatusCode.OK,
            $"External link exchange failed with {(int)link.Response.StatusCode}; " +
            $"storeMatched={exchangeStore.LastConsumeMatched}, exchangeScope={exchangeStore.LastConsumedScopeId}, " +
            $"tokenScope={scopeId}: {link.Body}");
        ExternalAuthenticationResponse linkResult = Read<ExternalAuthenticationResponse>(link.Body);
        string methodsToken = linkResult.AccessToken ?? linkTarget.AccessToken;
        using HttpResponseMessage linkedMethods = await SendAsync(
            client,
            Request(HttpMethod.Get, "/api/auth/methods", methodsToken)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, linkedMethods.StatusCode);
        AuthenticationMethodsResponse persistedMethods = Read<AuthenticationMethodsResponse>(
            await linkedMethods.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.Contains(
            persistedMethods.ExternalIdentities,
            static identity => string.Equals(
                identity.ProviderCode,
                "cmp-provider",
                StringComparison.Ordinal));
    }

    private static async Task ExecuteAccessOperationsAsync(
        AuthTestApplication application,
        AdminCliTestApplication adminCli,
        Guid ownerId,
        HttpClient client,
        OrdinaryBehaviorRuntimeProbe probe,
        IDictionary<string, OrdinaryBehaviorObservation> observations)
    {
        AuthTokensResponse permitted = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "permitted@cmp.test").ConfigureAwait(false);
        AuthTokensResponse forbidden = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "forbidden@cmp.test").ConfigureAwait(false);
        Guid permittedId = GetClaimGuid(permitted.AccessToken, ClaimTypes.NameIdentifier, "nameid", "sub");
        Guid forbiddenId = GetClaimGuid(forbidden.AccessToken, ClaimTypes.NameIdentifier, "nameid", "sub");
        await application.SeedOrganizationMembershipAsync(TenantId, permittedId).ConfigureAwait(false);
        await application.SeedOrganizationMembershipAsync(TenantId, forbiddenId).ConfigureAwait(false);

        const string role = "cmp-property-reader";
        await AssertCliSuccessAsync(adminCli.ExecuteAsync(
            "admin", "roles", "create",
            "--actor", ownerId.ToString("D"),
            "--name", role)).ConfigureAwait(false);
        await AssertCliSuccessAsync(adminCli.ExecuteAsync(
            "admin", "roles", "grant",
            "--actor", ownerId.ToString("D"),
            "--role", role,
            "--permission", PropertiesAdminPermissionCodes.Read)).ConfigureAwait(false);
        await AssertCliSuccessAsync(adminCli.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", ownerId.ToString("D"),
            "--target-kind", "user",
            "--target-id", permittedId.ToString("D"),
            "--role", role,
            "--scope", $"tenant:{TenantId}")).ConfigureAwait(false);

        using CapturedOperation evaluate = await CaptureAsync(
            Operation(19, "POST", "/api/access/permissions/evaluate", "permitted-operator", "persisted-role-grant", "ordinary-bearer", "tenant-member", 200),
            client,
            probe,
            JsonRequest(
                HttpMethod.Post,
                "/api/access/permissions/evaluate",
                new AccessPermissionEvaluationRequest([
                    new AccessPermissionCheck(PropertiesAdminPermissionCodes.Read, $"tenant:{TenantId}")
                ]),
                permitted.AccessToken)).ConfigureAwait(false);
        Add(observations, evaluate.Observation);

        using CapturedOperation permittedRead = await CaptureAsync(
            Operation(20, "GET", "/api/properties", "permitted-operator", "empty-tenant-inventory", "ordinary-bearer", "tenant-member", 200),
            client,
            probe,
            Request(HttpMethod.Get, "/api/properties", permitted.AccessToken)).ConfigureAwait(false);
        Add(observations, permittedRead.Observation);

        using CapturedOperation forbiddenRead = await CaptureAsync(
            Operation(21, "GET", "/api/properties", "permission-restricted-operator", "no-role-grant", "ordinary-bearer", "tenant-member", 403),
            client,
            probe,
            Request(HttpMethod.Get, "/api/properties", forbidden.AccessToken)).ConfigureAwait(false);
        Add(observations, forbiddenRead.Observation);
    }

    private static async Task<IReadOnlyList<OrdinaryBehaviorRuntimeSnapshot>> ExecuteRateLimitOperationAsync(
        string connectionString,
        string natsConnectionString,
        IDictionary<string, OrdinaryBehaviorObservation> observations)
    {
        OrdinaryBehaviorRuntimeProbe probe = new();
        IReadOnlyDictionary<string, string?> settings = new Dictionary<string, string?>
        {
            ["Http:RateLimiting:Enabled"] = "true",
            ["Http:RateLimiting:Mode"] = "InProcess",
            ["Http:RateLimiting:GlobalPermitLimit"] = "100",
            ["Http:RateLimiting:SensitivePermitLimit"] = "1",
            ["Http:RateLimiting:WindowSeconds"] = "60"
        };
        await using AuthTestApplication rateApplication = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            configurationOverrides: settings,
            configureServices: services => ConfigureProbe(services, probe));
        using HttpClient rateClient = rateApplication.CreateClient();
        using HttpResponseMessage first = await SendAsync(
            rateClient,
            JsonRequest(HttpMethod.Post, "/api/auth/login", new LoginMemberRequest(
                "rate-limit@cmp.test",
                "wrong-password"))).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        using CapturedOperation limited = await CaptureAsync(
            Operation(5, "POST", "/api/auth/login", "rate-limit-account", "sensitive-window-exhausted", "anonymous", "unknown-user", 429),
            rateClient,
            probe,
            JsonRequest(HttpMethod.Post, "/api/auth/login", new LoginMemberRequest(
                "rate-limit@cmp.test",
                "wrong-password"))).ConfigureAwait(false);
        Add(observations, limited.Observation);
        return probe.RuntimeSnapshots;
    }

    private static async Task ExecuteAdminOperationsAsync(
        AdminApiTestApplication application,
        Guid ownerId,
        OrdinaryBehaviorRuntimeProbe probe,
        IDictionary<string, OrdinaryBehaviorObservation> observations)
    {
        using HttpClient client = application.CreateClient();
        string ownerToken = application.CreateAccessToken(ownerId, TenantId);
        string strangerToken = application.CreateAccessToken(Guid.NewGuid(), TenantId);

        using CapturedOperation permitted = await CaptureAsync(
            Operation(22, "GET", "/api/admin/auth/members", "admin-owner", "persisted-owner-grant", "ordinary-bearer", "admin-owner", 200),
            client,
            probe,
            Request(HttpMethod.Get, "/api/admin/auth/members", ownerToken)).ConfigureAwait(false);
        Add(observations, permitted.Observation);

        using CapturedOperation forbidden = await CaptureAsync(
            Operation(23, "GET", "/api/admin/auth/members", "admin-stranger", "no-admin-grant", "ordinary-bearer", "admin-stranger", 403),
            client,
            probe,
            Request(HttpMethod.Get, "/api/admin/auth/members", strangerToken)).ConfigureAwait(false);
        Add(observations, forbidden.Observation);

        using CapturedOperation unauthenticated = await CaptureAsync(
            Operation(24, "GET", "/api/admin/auth/members", "admin-anonymous", "missing-credentials", "anonymous", "anonymous", 401),
            client,
            probe,
            Request(HttpMethod.Get, "/api/admin/auth/members")).ConfigureAwait(false);
        Add(observations, unauthenticated.Observation);
    }

    private static async Task ExecuteSignalROperationAsync(
        NotificationStreamingTestApplication application,
        OrdinaryBehaviorRuntimeProbe probe,
        IDictionary<string, OrdinaryBehaviorObservation> observations)
    {
        using HttpClient client = application.CreateClient();
        string token = NotificationStreamingTestApplication.CreateAccessToken(TenantId, "cmp-signalr-user");
        probe.DrainLogs();
        using HttpResponseMessage queryResponse = await client.PostAsync(
            $"/hubs/notifications/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}",
            content: null).ConfigureAwait(false);
        string queryBody = await queryResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        OrdinaryBehaviorRuntimeSnapshot queryRuntime = probe
            .LastFor("POST", "/hubs/notifications/negotiate") ??
            throw new InvalidOperationException("SignalR query-token runtime evidence was not captured.");

        using HttpRequestMessage headerRequest = Request(
            HttpMethod.Post,
            "/hubs/notifications/negotiate?negotiateVersion=1",
            token,
            includeTenant: false);
        using HttpResponseMessage headerResponse = await client.SendAsync(headerRequest).ConfigureAwait(false);
        string headerBody = await headerResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        OrdinaryBehaviorRuntimeSnapshot headerRuntime = probe
            .LastFor("POST", "/hubs/notifications/negotiate") ??
            throw new InvalidOperationException("SignalR header-token runtime evidence was not captured.");
        bool bothSucceeded = queryResponse.StatusCode == HttpStatusCode.OK && headerResponse.StatusCode == HttpStatusCode.OK;
        using HttpResponseMessage aggregate = new(bothSucceeded ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
        OrdinaryBehaviorOperation operation = Operation(
            25,
            "POST",
            "/hubs/notifications/negotiate",
            "ordinary-signalr-user",
            "query-and-header-bearer",
            "ordinary-bearer",
            "tenant-user",
            200);
        IReadOnlyList<OrdinaryBehaviorSubresponse> subresponses =
        [
            new(
                "query-access-token",
                (int)queryResponse.StatusCode,
                OrdinaryBehaviorComparisonEvidence.NormalizeHeaders(queryResponse),
                OrdinaryBehaviorComparisonEvidence.CanonicalizeBody(queryBody),
                queryRuntime.ToComparable()),
            new(
                "authorization-header",
                (int)headerResponse.StatusCode,
                OrdinaryBehaviorComparisonEvidence.NormalizeHeaders(headerResponse),
                OrdinaryBehaviorComparisonEvidence.CanonicalizeBody(headerBody),
                headerRuntime.ToComparable())
        ];
        OrdinaryBehaviorObservation observation = OrdinaryBehaviorComparisonEvidence.CreateObservation(
            operation,
            aggregate,
            string.Empty,
            headerRuntime with { Path = operation.Path },
            probe.DrainLogs(),
            subresponses);
        Add(observations, observation);
    }

    private static async Task ExecuteSseOperationAsync(
        NotificationStreamingTestApplication application,
        OrdinaryBehaviorRuntimeProbe probe,
        IDictionary<string, OrdinaryBehaviorObservation> observations)
    {
        using HttpClient client = application.CreateClient();
        string token = NotificationStreamingTestApplication.CreateAccessToken(
            TenantId,
            "cmp-sse-user");
        using HttpResponseMessage evidenceResponse = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        };
        string heartbeatEvent;
        string heartbeatPayload;
        string notificationEvent;
        string deliveredPayload;

        _ = probe.DrainLogs();
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10)))
        {
            using HttpRequestMessage request = Request(
                HttpMethod.Get,
                "/api/notifications/stream",
                token);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            evidenceResponse.StatusCode = response.StatusCode;
            CopyResponseHeaders(response, evidenceResponse);

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using StreamReader reader = new(stream);
            (heartbeatEvent, heartbeatPayload) = await ReadSseFrameContainingAsync(
                reader,
                "\"kind\":\"heartbeat\"",
                timeout.Token).ConfigureAwait(false);
            Assert.Equal("heartbeat", heartbeatEvent);

            const string title = "CMP SSE delivered message";
            await application.PublishAsync(
                TenantId,
                "cmp-sse-user",
                title,
                NotificationSeverity.Warning,
                timeout.Token).ConfigureAwait(false);
            (notificationEvent, deliveredPayload) = await ReadSseFrameContainingAsync(
                reader,
                title,
                timeout.Token).ConfigureAwait(false);
            Assert.Equal("notification", notificationEvent);
            Assert.Contains("\"kind\":\"notification\"", deliveredPayload, StringComparison.Ordinal);
            Assert.Contains("\"name\":\"streaming.test\"", deliveredPayload, StringComparison.Ordinal);
            Assert.Contains("\"severity\":\"warning\"", deliveredPayload, StringComparison.Ordinal);
        }

        OrdinaryBehaviorRuntimeSnapshot runtime = await WaitForRuntimeSnapshotAsync(
            probe,
            "GET",
            "/api/notifications/stream").ConfigureAwait(false);
        OrdinaryBehaviorOperation operation = Operation(
            26,
            "GET",
            "/api/notifications/stream",
            "ordinary-sse-user",
            "authenticated-stream-and-delivery",
            "ordinary-bearer",
            "tenant-user",
            200);
        IReadOnlyDictionary<string, string[]> headers =
            OrdinaryBehaviorComparisonEvidence.NormalizeHeaders(evidenceResponse);
        OrdinaryBehaviorSubresponse[] frameEvidence =
        [
            new(
                $"{heartbeatEvent}-event",
                (int)evidenceResponse.StatusCode,
                headers,
                OrdinaryBehaviorComparisonEvidence.CanonicalizeBody(heartbeatPayload),
                runtime.ToComparable()),
            new(
                $"{notificationEvent}-event",
                (int)evidenceResponse.StatusCode,
                headers,
                OrdinaryBehaviorComparisonEvidence.CanonicalizeBody(deliveredPayload),
                runtime.ToComparable())
        ];
        OrdinaryBehaviorObservation observation = OrdinaryBehaviorComparisonEvidence.CreateObservation(
            operation,
            evidenceResponse,
            deliveredPayload,
            runtime with { Path = operation.Path },
            probe.DrainLogs(),
            frameEvidence);
        Add(observations, observation);
    }

    private static void CopyResponseHeaders(
        HttpResponseMessage source,
        HttpResponseMessage target)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in source.Headers)
        {
            target.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in source.Content.Headers)
        {
            target.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static async Task<(string EventType, string Data)> ReadSseFrameContainingAsync(
        StreamReader reader,
        string expectedText,
        CancellationToken cancellationToken)
    {
        string? eventType = null;
        List<string> dataLines = [];
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                string data = string.Join('\n', dataLines);
                if (!string.IsNullOrWhiteSpace(eventType) &&
                    data.Contains(expectedText, StringComparison.Ordinal))
                {
                    return (eventType, data);
                }

                eventType = null;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line["event:".Length..].TrimStart();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
            }
        }

        throw new InvalidOperationException(
            $"No complete SSE frame containing '{expectedText}' was received.");
    }

    private static async Task<OrdinaryBehaviorRuntimeSnapshot> WaitForRuntimeSnapshotAsync(
        OrdinaryBehaviorRuntimeProbe probe,
        string method,
        string path)
    {
        for (int attempt = 0; attempt < 250; attempt++)
        {
            OrdinaryBehaviorRuntimeSnapshot? runtime = probe.LastFor(method, path);
            if (runtime is not null)
            {
                return runtime;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Runtime evidence was not captured for {method} {path} after stream disposal.");
    }

    private static void ConfigurePublicEvidenceServices(
        IServiceCollection services,
        OrdinaryBehaviorRuntimeProbe probe,
        DeterministicOneTimeTokenService oneTimeTokens,
        DeterministicPasswordRecoveryTokenService recoveryTokens,
        DeterministicExternalAuthenticationExchangeStore exchangeStore,
        DeterministicTotpProvider totp,
        DeterministicSecretProtector protector)
    {
        ConfigureProbe(services, probe);
        services.RemoveAll<IAuthOneTimeTokenService>();
        services.RemoveAll<IPasswordRecoveryTokenService>();
        services.RemoveAll<IExternalAuthenticationExchangeStore>();
        services.RemoveAll<ITimeBasedOneTimePasswordProvider>();
        services.RemoveAll<IAuthenticatorSecretProtector>();
        services.AddSingleton<IAuthOneTimeTokenService>(oneTimeTokens);
        services.AddSingleton<IPasswordRecoveryTokenService>(recoveryTokens);
        services.AddSingleton<IExternalAuthenticationExchangeStore>(exchangeStore);
        services.AddSingleton<ITimeBasedOneTimePasswordProvider>(totp);
        services.AddSingleton<IAuthenticatorSecretProtector>(protector);
    }

    private static void ConfigureProbe(IServiceCollection services, OrdinaryBehaviorRuntimeProbe probe)
    {
        services.AddSingleton(probe);
        services.AddSingleton<IStartupFilter>(probe);
        services.AddSingleton<ILoggerProvider>(probe);
    }

    private static async Task<CapturedOperation> CaptureAsync(
        OrdinaryBehaviorOperation operation,
        HttpClient client,
        OrdinaryBehaviorRuntimeProbe probe,
        HttpRequestMessage request)
    {
        _ = probe.DrainLogs();
        Uri? requestUri = request.RequestUri;
        string requestPath = requestUri is null
            ? operation.Path
            : requestUri.IsAbsoluteUri
                ? requestUri.PathAndQuery
                : requestUri.OriginalString;
        HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
        request.Dispose();
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        OrdinaryBehaviorRuntimeSnapshot runtime = probe.LastFor(operation.Method, requestPath) ??
            throw new InvalidOperationException($"Runtime evidence was not captured for {operation.Id}.");
        OrdinaryBehaviorObservation observation = OrdinaryBehaviorComparisonEvidence.CreateObservation(
            operation,
            response,
            body,
            runtime with { Path = operation.Path },
            probe.DrainLogs());
        return new(response, body, observation);
    }

    private static OrdinaryBehaviorOperation Operation(
        int ordinal,
        string method,
        string path,
        string fixture,
        string state,
        string authenticationInput,
        string subjectCategory,
        int expectedStatus)
    {
        string id = OrdinaryBehaviorComparisonEvidence.OperationIds[ordinal - 1];
        return new(
            id,
            ordinal,
            method,
            path,
            fixture,
            state,
            authenticationInput,
            subjectCategory,
            expectedStatus);
    }

    private static HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string? accessToken = null,
        bool includeTenant = true)
    {
        HttpRequestMessage request = new(method, path);
        if (includeTenant)
        {
            request.Headers.Add("X-Tenant-Id", TenantId);
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    private static HttpRequestMessage JsonRequest<TValue>(
        HttpMethod method,
        string path,
        TValue value,
        string? accessToken = null,
        bool includeTenant = true)
    {
        HttpRequestMessage request = Request(method, path, accessToken, includeTenant);
        request.Content = JsonContent.Create(value);
        return request;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request) =>
        client.SendAsync(request);

    private static TValue Read<TValue>(string body) where TValue : class =>
        System.Text.Json.JsonSerializer.Deserialize<TValue>(
            body,
            WebJsonOptions) ??
        throw new InvalidOperationException($"Response body could not be read as {typeof(TValue).Name}.");

    private static Guid GetClaimGuid(string accessToken, params string[] acceptedClaimTypes)
    {
        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        string? value = token.Claims.FirstOrDefault(claim =>
            acceptedClaimTypes.Contains(claim.Type, StringComparer.Ordinal))?.Value;
        if (!Guid.TryParse(value, out Guid parsed))
        {
            throw new InvalidOperationException(
                $"Token did not contain a GUID claim in [{string.Join(", ", acceptedClaimTypes)}].");
        }

        return parsed;
    }

    private static string GetClaimString(string accessToken, params string[] acceptedClaimTypes)
    {
        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        string? value = token.Claims.FirstOrDefault(claim =>
            acceptedClaimTypes.Contains(claim.Type, StringComparer.Ordinal))?.Value;
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Token did not contain a claim in [{string.Join(", ", acceptedClaimTypes)}].");
    }

    private static ExternalAuthenticationExchange CreateExchange(
        IAuthOneTimeTokenService tokenService,
        string code,
        string scopeId,
        ExternalAuthenticationIntent intent,
        string subject,
        string email,
        Guid? targetMemberId = null,
        Guid? targetSessionId = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(
            Guid.NewGuid(),
            scopeId,
            tokenService.HashToken(AuthOneTimeTokenPurpose.ExternalAuthenticationExchange, code),
            intent,
            "cmp-provider",
            "https://identity.cmp.test",
            subject,
            email,
            EmailVerified: true,
            targetMemberId,
            targetSessionId,
            "/auth/complete",
            now,
            now.AddMinutes(10));
    }

    private static async Task AssertCliSuccessAsync(Task<AdminCliResult> operation)
    {
        AdminCliResult result = await operation.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == 0,
            $"CLI failed with {result.ExitCode}. Output: {result.Output} Error: {result.Error}");
    }

    private static void Add(
        IDictionary<string, OrdinaryBehaviorObservation> observations,
        OrdinaryBehaviorObservation observation) =>
        observations.Add(observation.Id, observation);

    private static string RequireVariant()
    {
        string? variant = Environment.GetEnvironmentVariable("BFX_CMP_VARIANT");
        Assert.True(
            string.Equals(variant, "candidate", StringComparison.Ordinal) ||
            string.Equals(variant, "control", StringComparison.Ordinal),
            "BFX_CMP_VARIANT must be exactly 'candidate' or 'control'.");
        return variant!;
    }

    private static string RequireOutputDirectory()
    {
        string? directory = Environment.GetEnvironmentVariable("BFX_CMP_EVIDENCE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory), "BFX_CMP_EVIDENCE_DIR is required.");
        return Path.GetFullPath(directory);
    }

    private static string RequireBuildFingerprint()
    {
        string? fingerprint = Environment.GetEnvironmentVariable("BFX_CMP_BUILD_FINGERPRINT");
        Assert.False(string.IsNullOrWhiteSpace(fingerprint), "BFX_CMP_BUILD_FINGERPRINT is required.");
        return fingerprint;
    }

    private sealed class CapturedOperation(
        HttpResponseMessage response,
        string body,
        OrdinaryBehaviorObservation observation) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        public string Body { get; } = body;

        public OrdinaryBehaviorObservation Observation { get; } = observation;

        public void Dispose() => this.Response.Dispose();
    }

    private sealed class DeterministicOneTimeTokenService : IAuthOneTimeTokenService
    {
        private int sequence;

        public string GenerateToken() => $"cmp-one-time-token-{Interlocked.Increment(ref this.sequence):D4}";

        public string HashToken(AuthOneTimeTokenPurpose purpose, string token) => Hash($"{purpose}:{token.Trim()}");

        public IReadOnlyList<string> GetCandidateHashes(AuthOneTimeTokenPurpose purpose, string token) =>
            [this.HashToken(purpose, token)];
    }

    private sealed class DeterministicPasswordRecoveryTokenService : IPasswordRecoveryTokenService
    {
        public string GenerateCode() => RecoveryCode;

        public string HashCode(string code) => Hash($"password-recovery:{code.Trim()}");

        public IReadOnlyList<string> GetCandidateHashes(string code) => [this.HashCode(code)];
    }

    private sealed class DeterministicExternalAuthenticationExchangeStore : IExternalAuthenticationExchangeStore
    {
        private readonly ConcurrentDictionary<string, ExternalAuthenticationExchange> exchanges =
            new(StringComparer.Ordinal);

        public bool LastConsumeMatched { get; private set; }

        public string? LastConsumedScopeId { get; private set; }

        public void Add(ExternalAuthenticationExchange exchange)
        {
            if (!this.exchanges.TryAdd(exchange.CodeHash, exchange))
            {
                throw new InvalidOperationException("Duplicate deterministic external exchange hash.");
            }
        }

        public Task AddAsync(ExternalAuthenticationExchange exchange, CancellationToken cancellationToken)
        {
            this.Add(exchange);
            return Task.CompletedTask;
        }

        public Task<ExternalAuthenticationExchange?> ConsumeAsync(
            string codeHash,
            DateTimeOffset consumedAtUtc,
            CancellationToken cancellationToken)
        {
            if (!this.exchanges.TryRemove(codeHash, out ExternalAuthenticationExchange? exchange) ||
                exchange.ExpiresAtUtc <= consumedAtUtc)
            {
                this.LastConsumeMatched = false;
                this.LastConsumedScopeId = null;
                return Task.FromResult<ExternalAuthenticationExchange?>(null);
            }

            this.LastConsumeMatched = true;
            this.LastConsumedScopeId = exchange.ScopeId;
            return Task.FromResult<ExternalAuthenticationExchange?>(exchange with { ConsumedAtUtc = consumedAtUtc });
        }

        public Task<int> DeleteExpiredAsync(
            DateTimeOffset cutoffUtc,
            int batchSize,
            CancellationToken cancellationToken)
        {
            string[] expired = this.exchanges
                .Where(item => item.Value.ExpiresAtUtc <= cutoffUtc)
                .Take(batchSize)
                .Select(static item => item.Key)
                .ToArray();
            int removed = expired.Count(key => this.exchanges.TryRemove(key, out _));
            return Task.FromResult(removed);
        }
    }

    private sealed class DeterministicTotpProvider : ITimeBasedOneTimePasswordProvider
    {
        private long matchedTimeStep = 40;

        public bool IsAvailable => true;

        public GeneratedTotpSecret GenerateSecret() => new([1, 2, 3, 4], "AEBAGBA");

        public string CreateProvisioningUri(string accountName, string encodedSecret) =>
            $"otpauth://totp/BunkFy:{Uri.EscapeDataString(accountName)}?secret={encodedSecret}";

        public TotpVerificationResult Verify(byte[] secret, string code, DateTimeOffset nowUtc) =>
            string.Equals(code, ValidTotpCode, StringComparison.Ordinal)
                ? new(true, Interlocked.Increment(ref this.matchedTimeStep))
                : TotpVerificationResult.Invalid;
    }

    private sealed class DeterministicSecretProtector : IAuthenticatorSecretProtector
    {
        public bool IsAvailable => true;

        public string Protect(byte[] secret) => Convert.ToBase64String(secret);

        public byte[] Unprotect(string protectedSecret) => Convert.FromBase64String(protectedSecret);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

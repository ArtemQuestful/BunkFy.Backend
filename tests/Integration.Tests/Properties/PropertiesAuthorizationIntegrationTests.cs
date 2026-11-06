namespace Integration.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using BunkFy.Host.Api;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Contracts;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class PropertiesAuthorizationIntegrationTests
{
    private const string TenantA = "a1000000-0000-0000-0000-000000000001";
    private const string TenantB = "a1000000-0000-0000-0000-000000000002";
    private const string TenantHeader = "X-Tenant-Id";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Real_tokens_and_persisted_grants_enforce_the_properties_scope_matrix()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();

        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_properties_authorization_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            AuthTestContainers.GetNatsConnectionString(nats));
        await api.MigratePropertiesAuthorizationDatabaseAsync().ConfigureAwait(false);

        await using AdminCliTestApplication admin = new("PostgreSql", connectionString);
        await admin.MigrateAsync().ConfigureAwait(false);

        using HttpClient client = api.CreateClient();

        using (HttpResponseMessage unauthenticated = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   "/api/properties").ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Unauthorized, unauthenticated).ConfigureAwait(false);
        }

        AuthTokensResponse tenantManagerA = await AuthApiClient.RegisterAsync(
            client,
            TenantA,
            "manager-a@properties.test").ConfigureAwait(false);
        AuthTokensResponse localOperator = await AuthApiClient.RegisterAsync(
            client,
            TenantA,
            "operator@properties.test").ConfigureAwait(false);
        AuthTokensResponse tenantManagerB = await AuthApiClient.RegisterAsync(
            client,
            TenantB,
            "manager-b@properties.test").ConfigureAwait(false);

        Guid tenantManagerAId = GetSubjectId(tenantManagerA.AccessToken);
        Guid localOperatorId = GetSubjectId(localOperator.AccessToken);
        Guid tenantManagerBId = GetSubjectId(tenantManagerB.AccessToken);
        await api.SeedOrganizationMembershipAsync(TenantA, tenantManagerAId).ConfigureAwait(false);
        await api.SeedOrganizationMembershipAsync(TenantA, localOperatorId).ConfigureAwait(false);
        await api.SeedOrganizationMembershipAsync(TenantB, tenantManagerBId).ConfigureAwait(false);

        await AssertAdminSuccessAsync(admin.ExecuteAsync("admin", "bootstrap", "--actor", "owner", "--yes"));
        await CreatePropertiesRoleAsync(admin, "property-manager").ConfigureAwait(false);
        await CreatePropertiesRoleAsync(admin, "property-operator").ConfigureAwait(false);
        await AssignRoleAsync(admin, tenantManagerAId, "property-manager", $"tenant:{TenantA}").ConfigureAwait(false);
        await AssignRoleAsync(admin, tenantManagerBId, "property-manager", $"tenant:{TenantB}").ConfigureAwait(false);

        PropertyMutationReceiptDto propertyA1 = await CreatePropertyAsync(
            client,
            TenantA,
            tenantManagerA.AccessToken,
            "Alpha House",
            "ALPHA").ConfigureAwait(false);
        PropertyMutationReceiptDto propertyA2 = await CreatePropertyAsync(
            client,
            TenantA,
            tenantManagerA.AccessToken,
            "Beta House",
            "BETA").ConfigureAwait(false);
        PropertyMutationReceiptDto propertyB = await CreatePropertyAsync(
            client,
            TenantB,
            tenantManagerB.AccessToken,
            "Gamma House",
            "GAMMA").ConfigureAwait(false);

        using (HttpResponseMessage permissionEvaluation = await SendAsync(
                   client,
                   HttpMethod.Post,
                   TenantA,
                   "/api/access/permissions/evaluate",
                   tenantManagerA.AccessToken,
                   new AccessPermissionEvaluationRequest([
                       new AccessPermissionCheck(
                           PropertiesAdminPermissionCodes.PropertiesManage,
                           $"tenant:{TenantA}"),
                       new AccessPermissionCheck(
                           PropertiesAdminPermissionCodes.RoomsManage,
                           $"tenant:{TenantA}/property:{propertyA1.PropertyId:D}"),
                       new AccessPermissionCheck(
                           "inventory.configure",
                           $"tenant:{TenantA}/property:{propertyA1.PropertyId:D}")
                   ])).ConfigureAwait(false))
        {
            AccessPermissionEvaluationResponse evaluation =
                await ReadSuccessAsync<AccessPermissionEvaluationResponse>(permissionEvaluation).ConfigureAwait(false);
            Assert.Collection(
                evaluation.Permissions,
                decision => Assert.True(decision.Allowed),
                decision => Assert.True(decision.Allowed),
                decision => Assert.False(decision.Allowed));
        }

        using (HttpResponseMessage foreignTenantScope = await SendAsync(
                   client,
                   HttpMethod.Post,
                   TenantA,
                   "/api/access/permissions/evaluate",
                   tenantManagerA.AccessToken,
                   new AccessPermissionEvaluationRequest([
                       new AccessPermissionCheck(
                           PropertiesAdminPermissionCodes.Read,
                           $"tenant:{TenantB}/property:{propertyB.PropertyId:D}")
                   ])).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.BadRequest, foreignTenantScope).ConfigureAwait(false);
        }

        using (HttpResponseMessage tenantList = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   "/api/properties",
                   tenantManagerA.AccessToken).ConfigureAwait(false))
        {
            PropertyListResponse properties = await ReadSuccessAsync<PropertyListResponse>(tenantList).ConfigureAwait(false);
            Assert.Equal([propertyA1.PropertyId, propertyA2.PropertyId], properties.Properties.Select(property => property.PropertyId));
        }

        using (HttpResponseMessage ungranted = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   "/api/properties",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, ungranted).ConfigureAwait(false);
        }

        await AssignRoleAsync(
            admin,
            localOperatorId,
            "property-operator",
            $"tenant:{TenantA}/property:{propertyA1.PropertyId:D}").ConfigureAwait(false);

        using (HttpResponseMessage propertyList = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   "/api/properties",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            PropertyListResponse properties = await ReadSuccessAsync<PropertyListResponse>(propertyList).ConfigureAwait(false);
            PropertyListItemDto visible = Assert.Single(properties.Properties);
            Assert.Equal(propertyA1.PropertyId, visible.PropertyId);
        }

        using (HttpResponseMessage exactProperty = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.OK, exactProperty).ConfigureAwait(false);
        }

        CountryPolicyDescriptorDto countryPolicy;
        using (HttpResponseMessage countryPolicies = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}/country-policies",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            CountryPolicyListResponse response =
                await ReadSuccessAsync<CountryPolicyListResponse>(countryPolicies).ConfigureAwait(false);
            countryPolicy = Assert.Single(response.Items);
            Assert.Equal("GB", countryPolicy.OperatingCountryCode);
        }

        using (HttpResponseMessage processing = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}/processing",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            PropertyProcessingStateDto state =
                await ReadSuccessAsync<PropertyProcessingStateDto>(processing).ConfigureAwait(false);
            Assert.Equal(PropertyProcessingEffectiveStatus.Unconfigured, state.EffectiveStatus);
            Assert.Null(state.GovernancePolicy);
        }

        using (HttpResponseMessage otherProperty = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   $"/api/properties/{propertyA2.PropertyId:D}",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, otherProperty).ConfigureAwait(false);
        }

        using (HttpResponseMessage otherPropertyPolicies = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   $"/api/properties/{propertyA2.PropertyId:D}/country-policies",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, otherPropertyPolicies).ConfigureAwait(false);
        }

        using (HttpResponseMessage rootCreateDenied = await SendAsync(
                   client,
                   HttpMethod.Post,
                   TenantA,
                   "/api/properties",
                   localOperator.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       name = "Denied House",
                       code = "DENIED",
                       timeZoneId = "UTC"
                   }).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, rootCreateDenied).ConfigureAwait(false);
        }

        PropertyMutationReceiptDto updatedProperty;
        using (HttpResponseMessage updateProperty = await SendAsync(
                   client,
                   HttpMethod.Put,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}",
                   localOperator.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       name = "Alpha House Updated",
                       code = "ALPHA",
                       expectedVersion = 1
                   }).ConfigureAwait(false))
        {
            updatedProperty = await ReadSuccessAsync<PropertyMutationReceiptDto>(updateProperty).ConfigureAwait(false);
            Assert.Equal(2, updatedProperty.Version);
        }

        CountryPolicyRetentionDescriptorDto retentionPolicy = Assert.Single(
            countryPolicy.RetentionPolicies,
            policy => policy.RetentionPolicyId == "integration-guest-operational");
        object activationRequest = new
        {
            operationId = Guid.NewGuid(),
            operatingCountryCode = countryPolicy.OperatingCountryCode,
            policyId = countryPolicy.PolicyId,
            policyVersion = countryPolicy.PolicyVersion,
            dataRegionId = Assert.Single(countryPolicy.PermittedDataRegions),
            transferProfileId = Assert.Single(countryPolicy.PermittedTransferProfiles),
            retentionPolicyId = retentionPolicy.RetentionPolicyId,
            retentionPolicyVersion = retentionPolicy.RetentionPolicyVersion,
            acceptedAcknowledgements = countryPolicy.RequiredAcknowledgements.Select(acknowledgement => new
            {
                acknowledgementId = acknowledgement.AcknowledgementId,
                acknowledgementVersion = acknowledgement.AcknowledgementVersion
            }).ToArray(),
            confirmed = false,
            expectedVersion = updatedProperty.Version
        };
        using (HttpResponseMessage unconfirmedActivation = await SendAsync(
                   client,
                   HttpMethod.Post,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}/processing/activate",
                   localOperator.AccessToken,
                   activationRequest).ConfigureAwait(false))
        {
            string body = await unconfirmedActivation.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, unconfirmedActivation.StatusCode);
            Assert.Contains(PropertiesApplicationErrors.ConfirmationRequired.Code, body, StringComparison.Ordinal);
        }

        PropertyMutationReceiptDto processingEnabledProperty;
        using (HttpResponseMessage activateProcessing = await SendAsync(
                   client,
                   HttpMethod.Post,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}/processing/activate",
                   localOperator.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       operatingCountryCode = countryPolicy.OperatingCountryCode,
                       policyId = countryPolicy.PolicyId,
                       policyVersion = countryPolicy.PolicyVersion,
                       dataRegionId = Assert.Single(countryPolicy.PermittedDataRegions),
                       transferProfileId = Assert.Single(countryPolicy.PermittedTransferProfiles),
                       retentionPolicyId = retentionPolicy.RetentionPolicyId,
                       retentionPolicyVersion = retentionPolicy.RetentionPolicyVersion,
                       acceptedAcknowledgements = countryPolicy.RequiredAcknowledgements.Select(acknowledgement => new
                       {
                           acknowledgementId = acknowledgement.AcknowledgementId,
                           acknowledgementVersion = acknowledgement.AcknowledgementVersion
                       }).ToArray(),
                       confirmed = true,
                       expectedVersion = updatedProperty.Version
                   }).ConfigureAwait(false))
        {
            processingEnabledProperty =
                await ReadSuccessAsync<PropertyMutationReceiptDto>(activateProcessing).ConfigureAwait(false);
            Assert.Equal(PropertyProcessingStatus.Enabled, processingEnabledProperty.ProcessingStatus);
        }

        using (HttpResponseMessage processing = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}/processing",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            PropertyProcessingStateDto state =
                await ReadSuccessAsync<PropertyProcessingStateDto>(processing).ConfigureAwait(false);
            Assert.Equal(PropertyProcessingEffectiveStatus.Enabled, state.EffectiveStatus);
            Assert.Equal(processingEnabledProperty.Version, state.PropertyVersion);
        }

        RoomMutationReceiptDto room;
        using (HttpResponseMessage createRoom = await SendAsync(
                   client,
                   HttpMethod.Post,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}/rooms",
                   localOperator.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       name = "Room 101",
                       expectedPropertyVersion = processingEnabledProperty.Version,
                       buildingLabel = "Main",
                       floorLabel = "1"
                   }).ConfigureAwait(false))
        {
            room = await ReadSuccessAsync<RoomMutationReceiptDto>(createRoom).ConfigureAwait(false);
        }

        BedMutationReceiptDto bed;
        using (HttpResponseMessage addBed = await SendAsync(
                   client,
                   HttpMethod.Post,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}/rooms/{room.RoomId:D}/beds",
                   localOperator.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       label = "A",
                       expectedRoomVersion = room.Version
                   }).ConfigureAwait(false))
        {
            bed = await ReadSuccessAsync<BedMutationReceiptDto>(addBed).ConfigureAwait(false);
            Assert.Equal(room.RoomId, bed.RoomId);
        }

        using (HttpResponseMessage retireBed = await SendAsync(
                   client,
                   HttpMethod.Post,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}/rooms/{room.RoomId:D}/beds/{bed.BedId:D}/retire",
                   localOperator.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       confirmed = true,
                       expectedRoomVersion = bed.RoomVersion
                   }).ConfigureAwait(false))
        {
            string body = await retireBed.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Conflict, retireBed.StatusCode);
            Assert.Contains(PropertiesApplicationErrors.BedRetirementRequiresInventory.Code, body, StringComparison.Ordinal);
        }

        using (HttpResponseMessage retireRoom = await SendAsync(
                   client,
                   HttpMethod.Post,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}/rooms/{room.RoomId:D}/retire",
                   localOperator.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       confirmed = true,
                       expectedVersion = bed.RoomVersion,
                       cascadeBeds = true
                   }).ConfigureAwait(false))
        {
            string body = await retireRoom.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Conflict, retireRoom.StatusCode);
            Assert.Contains(PropertiesApplicationErrors.RoomRetirementRequiresInventory.Code, body, StringComparison.Ordinal);
        }

        using (HttpResponseMessage tenantMismatch = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantB,
                   "/api/properties",
                   tenantManagerA.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, tenantMismatch).ConfigureAwait(false);
        }

        using (HttpResponseMessage crossTenantIdentifier = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   $"/api/properties/{propertyB.PropertyId:D}",
                   tenantManagerA.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.NotFound, crossTenantIdentifier).ConfigureAwait(false);
        }

        await api.SetOrganizationMembershipSuspendedAsync(TenantA, localOperatorId, suspended: true)
            .ConfigureAwait(false);
        using (HttpResponseMessage suspendedMembership = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, suspendedMembership).ConfigureAwait(false);
        }

        await api.SetOrganizationMembershipSuspendedAsync(TenantA, localOperatorId, suspended: false)
            .ConfigureAwait(false);
        using (HttpResponseMessage resumedMembership = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   $"/api/properties/{propertyA1.PropertyId:D}",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.OK, resumedMembership).ConfigureAwait(false);
        }

        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "unassign",
            "--actor", "owner",
            "--target-kind", "user",
            "--target-id", localOperatorId.ToString("D"),
            "--role", "property-operator",
            "--scope", $"tenant:{TenantA}/property:{propertyA1.PropertyId:D}"));

        using (HttpResponseMessage revoked = await SendAsync(
                   client,
                   HttpMethod.Get,
                   TenantA,
                   "/api/properties",
                   localOperator.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, revoked).ConfigureAwait(false);
        }
    }

    private static async Task CreatePropertiesRoleAsync(AdminCliTestApplication admin, string roleName)
    {
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create",
            "--actor", "owner",
            "--name", roleName));

        foreach (string permission in new[]
                 {
                     PropertiesAdminPermissionCodes.Read,
                     PropertiesAdminPermissionCodes.PropertiesManage,
                     PropertiesAdminPermissionCodes.RoomsManage,
                     PropertiesAdminPermissionCodes.BedsManage
                 })
        {
            await AssertAdminSuccessAsync(admin.ExecuteAsync(
                "admin", "roles", "grant",
                "--actor", "owner",
                "--role", roleName,
                "--permission", permission));
        }
    }

    private static Task<AdminCliResult> AssignRoleAsync(
        AdminCliTestApplication admin,
        Guid subjectId,
        string roleName,
        string scope) =>
        AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", "owner",
            "--target-kind", "user",
            "--target-id", subjectId.ToString("D"),
            "--role", roleName,
            "--scope", scope));

    private static async Task<PropertyMutationReceiptDto> CreatePropertyAsync(
        HttpClient client,
        string tenantId,
        string accessToken,
        string name,
        string code)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Post,
            tenantId,
            "/api/properties",
            accessToken,
            new
            {
                operationId = Guid.NewGuid(),
                name,
                code,
                timeZoneId = "UTC"
            }).ConfigureAwait(false);

        return await ReadSuccessAsync<PropertyMutationReceiptDto>(response).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string tenantId,
        string path,
        string? accessToken = null,
        object? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add(TenantHeader, tenantId);

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<T> ReadSuccessAsync<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected a successful response but received {(int)response.StatusCode}. Body: {body}");

        T? value = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        Assert.NotNull(value);
        return value;
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.StatusCode == expected,
            $"Expected {(int)expected} but received {(int)response.StatusCode}. Body: {body}");
    }

    private static async Task<AdminCliResult> AssertAdminSuccessAsync(Task<AdminCliResult> resultTask)
    {
        AdminCliResult result = await resultTask.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == AdminExitCodes.Success,
            $"ExitCode={result.ExitCode}{Environment.NewLine}Output:{Environment.NewLine}{result.Output}{Environment.NewLine}Error:{Environment.NewLine}{result.Error}");
        return result;
    }

    private static Guid GetSubjectId(string accessToken)
    {
        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        string? subjectId = token.Claims.FirstOrDefault(claim =>
            string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
            string.Equals(claim.Type, "nameid", StringComparison.Ordinal) ||
            string.Equals(claim.Type, "sub", StringComparison.Ordinal))?.Value;

        Assert.True(Guid.TryParse(subjectId, out Guid parsedSubjectId));
        return parsedSubjectId;
    }
}

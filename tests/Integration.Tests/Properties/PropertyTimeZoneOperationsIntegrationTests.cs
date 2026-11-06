namespace Integration.Tests;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BunkFy.Modules.Properties.Admin.Contracts;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Persistence;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Security;
using Gma.Framework.Tenancy;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Npgsql;
using Swashbuckle.AspNetCore.Swagger;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class PropertyTimeZoneOperationsIntegrationTests
{
    private const string TenantId = "a8100000-0000-0000-0000-000000000001";
    private const string TenantHeader = "X-Tenant-Id";
    private const string PreviousPropertiesMigration =
        "20260807212415_AddBedMutationOperationReceipts";
    private static readonly Guid LegacyAliasPropertyId =
        Guid.Parse("a8300000-0000-0000-0000-000000000001");
    private static readonly Guid AdminActorId =
        Guid.Parse("a8200000-0000-0000-0000-000000000001");
    private static readonly Guid CatalogAdminActorId =
        Guid.Parse("a8200000-0000-0000-0000-000000000002");
    private static readonly Guid PropertyScopedAdminActorId =
        Guid.Parse("a8200000-0000-0000-0000-000000000003");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Public_admin_api_and_cli_operate_bounded_recoverable_time_zone_corrections()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_property_time_zone_operations_tests")
                .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        string natsConnectionString =
            AuthTestContainers.GetNatsConnectionString(nats);
        var systemClock = new MutableSystemClock(
            DateTimeOffset.FromUnixTimeMilliseconds(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using AuthTestApplication publicApi = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            systemClock: systemClock);
        await publicApi.MigratePropertiesAuthorizationDatabaseAsync(
                PreviousPropertiesMigration)
            .ConfigureAwait(false);
        await SeedLegacyAliasBeforeConvergenceAsync(publicApi)
            .ConfigureAwait(false);
        await publicApi.MigratePropertiesAuthorizationDatabaseAsync()
            .ConfigureAwait(false);
        await AssertPostConvergenceRawAliasUpdateRejectedAsync(publicApi)
            .ConfigureAwait(false);
        await using AdminCliTestApplication adminCli = new(
            "PostgreSql",
            connectionString,
            includeProperties: true,
            systemClock: systemClock);
        await adminCli.MigrateAsync().ConfigureAwait(false);
        string adminActor = AdminActorId.ToString("D");
        await AssertCliSuccessAsync(adminCli.ExecuteAsync(
            "admin", "bootstrap",
            "--actor", adminActor,
            "--yes"));

        using HttpClient publicClient = publicApi.CreateClient();
        await AssertUpdateTimeZoneIsOptionalInOpenApiAsync(
                publicApi.Services,
                "/api/properties/{propertyId}")
            .ConfigureAwait(false);
        AuthTokensResponse manager = await AuthApiClient.RegisterAsync(
            publicClient,
            TenantId,
            "time-zone-manager@properties.test").ConfigureAwait(false);
        AuthTokensResponse ungranted = await AuthApiClient.RegisterAsync(
            publicClient,
            TenantId,
            "time-zone-reader@properties.test").ConfigureAwait(false);
        AuthTokensResponse propertyScoped = await AuthApiClient.RegisterAsync(
            publicClient,
            TenantId,
            "time-zone-property-operator@properties.test").ConfigureAwait(false);
        AuthTokensResponse catalogReader = await AuthApiClient.RegisterAsync(
            publicClient,
            TenantId,
            "time-zone-catalog-reader@properties.test").ConfigureAwait(false);
        Guid managerId = SubjectId(manager.AccessToken);
        Guid ungrantedId = SubjectId(ungranted.AccessToken);
        Guid propertyScopedId = SubjectId(propertyScoped.AccessToken);
        Guid catalogReaderId = SubjectId(catalogReader.AccessToken);
        await publicApi.SeedOrganizationMembershipAsync(TenantId, managerId)
            .ConfigureAwait(false);
        await publicApi.SeedOrganizationMembershipAsync(TenantId, ungrantedId)
            .ConfigureAwait(false);
        await publicApi.SeedOrganizationMembershipAsync(
                TenantId,
                propertyScopedId)
            .ConfigureAwait(false);
        await publicApi.SeedOrganizationMembershipAsync(
                TenantId,
                catalogReaderId)
            .ConfigureAwait(false);
        await GrantManagerAsync(adminCli, managerId, adminActor)
            .ConfigureAwait(false);
        await GrantCatalogReadersAsync(
                adminCli,
                catalogReaderId,
                adminActor)
            .ConfigureAwait(false);

        using (HttpResponseMessage anonymous = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/catalog").ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            AssertNoStore(anonymous);
        }

        using (HttpResponseMessage denied = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/compliance",
                   ungranted.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            AssertNoStore(denied);
        }

        using (HttpResponseMessage malformed = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/catalog?countryCode=N1",
                   manager.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            AssertNoStore(malformed);
        }

        using (HttpResponseMessage readableCatalog = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/catalog?search=Etc%2FUTC",
                   catalogReader.AccessToken).ConfigureAwait(false))
        {
            PropertyTimeZoneCatalogPageDto page =
                await ReadSuccessAsync<PropertyTimeZoneCatalogPageDto>(readableCatalog)
                    .ConfigureAwait(false);
            Assert.Contains(page.TimeZones, item => item.TimeZoneId == "Etc/UTC");
            AssertNoStore(readableCatalog);
        }

        using (HttpResponseMessage sensitiveCompliance = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/compliance",
                   catalogReader.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, sensitiveCompliance.StatusCode);
            AssertNoStore(sensitiveCompliance);
        }

        using (HttpResponseMessage oversizedPage = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/catalog?pageSize=101",
                   manager.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, oversizedPage.StatusCode);
            AssertNoStore(oversizedPage);
        }

        PropertyTimeZoneCatalogPageDto runtimeCatalog;
        using (HttpResponseMessage catalog = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/catalog?search=Etc%2FUTC&pageSize=10",
                   manager.AccessToken).ConfigureAwait(false))
        {
            runtimeCatalog = await ReadSuccessAsync<PropertyTimeZoneCatalogPageDto>(catalog)
                .ConfigureAwait(false);
            PropertyTimeZoneCatalogItemDto utc = Assert.Single(
                runtimeCatalog.TimeZones,
                item => item.TimeZoneId == "Etc/UTC");
            Assert.True(utc.RuntimeAvailable);
            Assert.Equal(0, utc.UtcOffsetMinutes);
            AssertNoStore(catalog);
        }

        PropertyTimeZoneCatalogPageDto firstCatalogPage;
        using (HttpResponseMessage catalog = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/catalog?search=america&countryCode=us&pageSize=1",
                   manager.AccessToken).ConfigureAwait(false))
        {
            firstCatalogPage = await ReadSuccessAsync<PropertyTimeZoneCatalogPageDto>(catalog)
                .ConfigureAwait(false);
            Assert.True(firstCatalogPage.HasMore);
            Assert.NotNull(firstCatalogPage.NextCursor);
            Assert.Single(firstCatalogPage.TimeZones);
            Assert.NotEqual(default, firstCatalogPage.ObservedAtUtc);
            AssertNoStore(catalog);
        }

        using (HttpResponseMessage next = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/catalog?search=AMERICA&countryCode=US&pageSize=1&cursor=" +
                   Uri.EscapeDataString(firstCatalogPage.NextCursor!),
                   manager.AccessToken).ConfigureAwait(false))
        {
            PropertyTimeZoneCatalogPageDto page =
                await ReadSuccessAsync<PropertyTimeZoneCatalogPageDto>(next)
                    .ConfigureAwait(false);
            Assert.DoesNotContain(
                page.TimeZones,
                item => item.TimeZoneId ==
                    firstCatalogPage.TimeZones.Single().TimeZoneId);
            Assert.NotEqual(default, page.ObservedAtUtc);
            Assert.True(page.ObservedAtUtc >= firstCatalogPage.ObservedAtUtc);
            AssertNoStore(next);
        }

        using (HttpResponseMessage cursorMismatch = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/catalog?search=EUROPE&countryCode=US&pageSize=1&cursor=" +
                   Uri.EscapeDataString(firstCatalogPage.NextCursor!),
                   manager.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, cursorMismatch.StatusCode);
            AssertNoStore(cursorMismatch);
        }

        Guid rejectedCreateOperationId = Guid.NewGuid();
        using (HttpResponseMessage rejectedCreate = await SendAsync(
                   publicClient,
                   HttpMethod.Post,
                   "/api/properties",
                   manager.AccessToken,
                   new
                   {
                       operationId = rejectedCreateOperationId,
                       name = "Rejected Windows zone",
                       code = "REJECTED",
                       timeZoneId = "Pacific Standard Time"
                   }).ConfigureAwait(false))
        {
            string body = await rejectedCreate.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedCreate.StatusCode);
            Assert.Contains(PropertiesDomainErrors.TimeZoneInvalid.Code, body, StringComparison.Ordinal);
            AssertNoStore(rejectedCreate);
        }
        Assert.Equal(
            (0, 0),
            await CountPropertyAndTimeZoneOperationAsync(
                    publicApi,
                    rejectedCreateOperationId,
                    rejectedCreateOperationId)
                .ConfigureAwait(false));

        foreach (string invalidTimeZoneId in new[]
                 {
                     "   ",
                     new string('a', PropertiesContractLimits.TimeZoneIdMaxLength + 1)
                 })
        {
            Guid invalidOperationId = Guid.NewGuid();
            using HttpResponseMessage invalid = await SendAsync(
                    publicClient,
                    HttpMethod.Post,
                    "/api/properties",
                    manager.AccessToken,
                    new
                    {
                        operationId = invalidOperationId,
                        name = "Invalid time zone",
                        code = $"INVALID-{invalidOperationId:N}",
                        timeZoneId = invalidTimeZoneId
                    })
                .ConfigureAwait(false);
            string body = await invalid.Content.ReadAsStringAsync()
                .ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains(
                RequestValidationErrors.FailedCode,
                body,
                StringComparison.Ordinal);
            AssertNoStore(invalid);
            Assert.Equal(
                (0, 0),
                await CountPropertyAndTimeZoneOperationAsync(
                        publicApi,
                        invalidOperationId,
                        invalidOperationId)
                    .ConfigureAwait(false));
        }

        (PropertyMutationReceiptDto firstProperty, Guid firstCreateOperationId) = await CreatePropertyAsync(
            publicClient,
            manager.AccessToken,
            "Alpha House",
            "ALPHA").ConfigureAwait(false);
        (PropertyMutationReceiptDto secondProperty, Guid secondCreateOperationId) = await CreatePropertyAsync(
            publicClient,
            manager.AccessToken,
            "Beta House",
            "BETA").ConfigureAwait(false);

        using (HttpResponseMessage replayedCreate = await SendAsync(
                   publicClient,
                   HttpMethod.Post,
                   "/api/properties",
                   manager.AccessToken,
                   new
                   {
                       operationId = firstCreateOperationId,
                       name = "Alpha House",
                       code = "ALPHA",
                       timeZoneId = "UTC"
                   }).ConfigureAwait(false))
        {
            Assert.Equal(
                firstProperty,
                await ReadSuccessAsync<PropertyMutationReceiptDto>(replayedCreate)
                    .ConfigureAwait(false));
            AssertNoStore(replayedCreate);
        }
        Assert.Equal(
            (1, 1),
            await CountPropertyAndTimeZoneOperationAsync(
                    publicApi,
                    firstProperty.PropertyId,
                    firstCreateOperationId)
                .ConfigureAwait(false));

        await GrantPropertyTimeZoneOperatorAsync(
                adminCli,
                propertyScopedId,
                adminActor,
                firstProperty.PropertyId)
            .ConfigureAwait(false);

        using (HttpResponseMessage tenantCatalog = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/catalog",
                   propertyScoped.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, tenantCatalog.StatusCode);
            AssertNoStore(tenantCatalog);
        }

        using (HttpResponseMessage scopedCatalog = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zones/catalog?search=Etc%2FUTC",
                   propertyScoped.AccessToken).ConfigureAwait(false))
        {
            PropertyTimeZoneCatalogPageDto page =
                await ReadSuccessAsync<PropertyTimeZoneCatalogPageDto>(scopedCatalog)
                    .ConfigureAwait(false);
            Assert.Contains(
                page.TimeZones,
                item => item.TimeZoneId == "Etc/UTC");
            AssertNoStore(scopedCatalog);
        }

        using (HttpResponseMessage crossPropertyCatalog = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   $"/api/properties/{secondProperty.PropertyId:D}/time-zones/catalog?search=Etc%2FUTC",
                   propertyScoped.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossPropertyCatalog.StatusCode);
            AssertNoStore(crossPropertyCatalog);
        }

        using (HttpResponseMessage scopedRecovery = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone/operations/{firstCreateOperationId:D}",
                   propertyScoped.AccessToken).ConfigureAwait(false))
        {
            PropertyTimeZoneRecoveryDto recovered =
                await ReadSuccessAsync<PropertyTimeZoneRecoveryDto>(scopedRecovery)
                    .ConfigureAwait(false);
            Assert.Equal(firstCreateOperationId, recovered.Receipt.OperationId);
            AssertNoStore(scopedRecovery);
        }

        using (HttpResponseMessage crossPropertyRecovery = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   $"/api/properties/{secondProperty.PropertyId:D}/time-zone/operations/{secondCreateOperationId:D}",
                   propertyScoped.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossPropertyRecovery.StatusCode);
            AssertNoStore(crossPropertyRecovery);
        }

        Guid crossPropertySetOperationId = Guid.NewGuid();
        using (HttpResponseMessage crossPropertySet = await SendAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{secondProperty.PropertyId:D}/time-zone",
                   propertyScoped.AccessToken,
                   new
                   {
                       operationId = crossPropertySetOperationId,
                       timeZoneId = "Etc/UTC",
                       confirmed = false,
                       expectedVersion = secondProperty.Version
                   }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossPropertySet.StatusCode);
            AssertNoStore(crossPropertySet);
        }
        Assert.Equal(
            (1, 0),
            await CountPropertyAndTimeZoneOperationAsync(
                    publicApi,
                    secondProperty.PropertyId,
                    crossPropertySetOperationId)
                .ConfigureAwait(false));

        using (HttpResponseMessage routingFailure = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/not-a-guid/time-zone/operations/not-a-guid",
                   manager.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.NotFound, routingFailure.StatusCode);
            AssertNoStore(routingFailure);
        }

        using (HttpResponseMessage bindingFailure = await SendMalformedJsonAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone",
                   manager.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bindingFailure.StatusCode);
            AssertNoStore(bindingFailure);
        }

        using (HttpResponseMessage createdRecovery = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone/operations/{firstCreateOperationId:D}",
                   manager.AccessToken).ConfigureAwait(false))
        {
            PropertyTimeZoneRecoveryDto recovered =
                await ReadSuccessAsync<PropertyTimeZoneRecoveryDto>(createdRecovery)
                    .ConfigureAwait(false);
            Assert.Equal(PropertyTimeZoneChangeKind.Created, recovered.Receipt.ChangeKind);
            Assert.Equal(firstCreateOperationId, recovered.Receipt.OperationId);
            Assert.Equal("UTC", recovered.Receipt.RequestedTimeZoneId);
            Assert.Null(recovered.Receipt.PreviousTimeZoneId);
            Assert.Equal("Etc/UTC", recovered.Receipt.TimeZoneId);
            Assert.Equal(0, recovered.Receipt.ExpectedVersion);
            Assert.Equal(1, recovered.Receipt.Version);
            Assert.Equal($"user:{managerId:D}", recovered.Receipt.ActorId);
            Assert.Equal("Etc/UTC", recovered.CurrentTimeZoneId);
            Assert.Equal(PropertyTimeZoneStatus.Canonical, recovered.CurrentTimeZoneStatus);
            Assert.Equal("Etc/UTC", recovered.CurrentCanonicalTimeZoneId);
            Assert.NotEqual(default, recovered.CurrentTimeZoneObservedAtUtc);
            Assert.Equal(firstProperty.Version, recovered.CurrentVersion);
            AssertNoStore(createdRecovery);
        }

        PropertyTimeZoneCompliancePageDto firstCompliancePage;
        using (HttpResponseMessage compliance = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/compliance?pageSize=1",
                   manager.AccessToken).ConfigureAwait(false))
        {
            firstCompliancePage =
                await ReadSuccessAsync<PropertyTimeZoneCompliancePageDto>(compliance)
                    .ConfigureAwait(false);
            Assert.Single(firstCompliancePage.Properties);
            Assert.True(firstCompliancePage.HasMore);
            Assert.NotNull(firstCompliancePage.NextCursor);
            Assert.NotEqual(default, firstCompliancePage.ObservedAtUtc);
            AssertNoStore(compliance);
        }

        using (HttpResponseMessage compliance = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   "/api/properties/time-zones/compliance?pageSize=1&cursor=" +
                   Uri.EscapeDataString(firstCompliancePage.NextCursor!),
                   manager.AccessToken).ConfigureAwait(false))
        {
            PropertyTimeZoneCompliancePageDto nextPage =
                await ReadSuccessAsync<PropertyTimeZoneCompliancePageDto>(compliance)
                    .ConfigureAwait(false);
            Assert.Single(nextPage.Properties);
            Assert.True(nextPage.ObservedAtUtc >= firstCompliancePage.ObservedAtUtc);
            Assert.NotEqual(
                firstCompliancePage.Properties.Single().PropertyId,
                nextPage.Properties.Single().PropertyId);
            AssertNoStore(compliance);
        }

        Guid publicOperationId = Guid.NewGuid();
        Guid rejectedSetOperationId = Guid.NewGuid();
        using (HttpResponseMessage rejectedSet = await SendAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone",
                   manager.AccessToken,
                   new
                   {
                       operationId = rejectedSetOperationId,
                       timeZoneId = "Missing/Zone",
                       confirmed = true,
                       expectedVersion = firstProperty.Version
                   }).ConfigureAwait(false))
        {
            string body = await rejectedSet.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedSet.StatusCode);
            Assert.Contains(PropertiesDomainErrors.TimeZoneInvalid.Code, body, StringComparison.Ordinal);
            AssertNoStore(rejectedSet);
        }
        Assert.Equal(
            (1, 0),
            await CountPropertyAndTimeZoneOperationAsync(
                    publicApi,
                    firstProperty.PropertyId,
                    rejectedSetOperationId)
                .ConfigureAwait(false));

        using (HttpResponseMessage rejectedRecovery = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone/operations/{rejectedSetOperationId:D}",
                   manager.AccessToken).ConfigureAwait(false))
        {
            string body = await rejectedRecovery.Content.ReadAsStringAsync()
                .ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.NotFound, rejectedRecovery.StatusCode);
            Assert.Contains(
                PropertiesApplicationErrors.TimeZoneOperationNotFound.Code,
                body,
                StringComparison.Ordinal);
            AssertNoStore(rejectedRecovery);
        }

        foreach (string invalidTimeZoneId in new[]
                 {
                     "   ",
                     new string('a', PropertiesContractLimits.TimeZoneIdMaxLength + 1)
                 })
        {
            Guid invalidOperationId = Guid.NewGuid();
            using HttpResponseMessage invalid = await SendAsync(
                    publicClient,
                    HttpMethod.Put,
                    $"/api/properties/{firstProperty.PropertyId:D}/time-zone",
                    manager.AccessToken,
                    new
                    {
                        operationId = invalidOperationId,
                        timeZoneId = invalidTimeZoneId,
                        confirmed = true,
                        expectedVersion = firstProperty.Version
                    })
                .ConfigureAwait(false);
            string body = await invalid.Content.ReadAsStringAsync()
                .ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains(
                RequestValidationErrors.FailedCode,
                body,
                StringComparison.Ordinal);
            AssertNoStore(invalid);
            Assert.Equal(
                (1, 0),
                await CountPropertyAndTimeZoneOperationAsync(
                        publicApi,
                        firstProperty.PropertyId,
                        invalidOperationId)
                    .ConfigureAwait(false));
        }

        string staleToken = WithAuthenticationTime(
            manager.AccessToken,
            DateTimeOffset.UtcNow.AddMinutes(-11));
        using (HttpResponseMessage stale = await SendAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone",
                   staleToken,
                   new
                   {
                       operationId = publicOperationId,
                       timeZoneId = "Europe/Amsterdam",
                       confirmed = true,
                       expectedVersion = firstProperty.Version
                   }).ConfigureAwait(false))
        {
            string body = await stale.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
            Assert.Contains("Security.InsufficientAuthentication", body, StringComparison.Ordinal);
            AssertNoStore(stale);
        }

        using (HttpResponseMessage unconfirmed = await SendAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone",
                   manager.AccessToken,
                   new
                   {
                       operationId = publicOperationId,
                       timeZoneId = "Europe/Amsterdam",
                       confirmed = false,
                       expectedVersion = firstProperty.Version
                   }).ConfigureAwait(false))
        {
            string body = await unconfirmed.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
            Assert.Contains(
                PropertiesApplicationErrors.ConfirmationRequired.Code,
                body,
                StringComparison.Ordinal);
            AssertNoStore(unconfirmed);
        }

        SetPropertyTimeZoneReceiptDto publicReceipt;
        using (HttpResponseMessage changed = await SendAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone",
                   manager.AccessToken,
                   new
                   {
                       operationId = publicOperationId,
                       timeZoneId = "Europe/Amsterdam",
                       confirmed = true,
                       expectedVersion = firstProperty.Version
                   }).ConfigureAwait(false))
        {
            publicReceipt = await ReadSuccessAsync<SetPropertyTimeZoneReceiptDto>(changed)
                .ConfigureAwait(false);
            Assert.Equal(PropertyTimeZoneChangeKind.Changed, publicReceipt.ChangeKind);
            Assert.Equal($"user:{managerId:D}", publicReceipt.ActorId);
            Assert.Equal("Europe/Amsterdam", publicReceipt.RequestedTimeZoneId);
            Assert.Equal("Europe/Brussels", publicReceipt.TimeZoneId);
            Assert.Equal(firstProperty.Version + 1, publicReceipt.Version);
            AssertNoStore(changed);
        }
        await AssertChangedOperationCommitAsync(
                publicApi,
                publicReceipt)
            .ConfigureAwait(false);

        using (HttpResponseMessage replay = await SendAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone",
                   manager.AccessToken,
                   new
                   {
                       operationId = publicOperationId,
                       timeZoneId = "Europe/Amsterdam",
                       confirmed = false,
                       expectedVersion = firstProperty.Version
                   }).ConfigureAwait(false))
        {
            SetPropertyTimeZoneReceiptDto replayed =
                await ReadSuccessAsync<SetPropertyTimeZoneReceiptDto>(replay)
                    .ConfigureAwait(false);
            Assert.Equal(publicReceipt, replayed);
            AssertNoStore(replay);
        }

        using (HttpResponseMessage recovery = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   $"/api/properties/{firstProperty.PropertyId:D}/time-zone/operations/{publicOperationId:D}",
                   manager.AccessToken).ConfigureAwait(false))
        {
            PropertyTimeZoneRecoveryDto recovered =
                await ReadSuccessAsync<PropertyTimeZoneRecoveryDto>(recovery)
                    .ConfigureAwait(false);
            Assert.Equal(publicReceipt, recovered.Receipt);
            Assert.Equal(publicReceipt.Version, recovered.CurrentVersion);
            Assert.NotEqual(default, recovered.CurrentTimeZoneObservedAtUtc);
            AssertNoStore(recovery);
        }

        Guid genericUpdateOperationId = Guid.NewGuid();
        using (HttpResponseMessage differentRawTimeZone = await SendAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{firstProperty.PropertyId:D}",
                   manager.AccessToken,
                   new
                   {
                       operationId = genericUpdateOperationId,
                       name = "Alpha House Updated",
                       code = "ALPHA",
                       timeZoneId = "Europe/Amsterdam",
                       expectedVersion = publicReceipt.Version
                   }).ConfigureAwait(false))
        {
            string body = await differentRawTimeZone.Content.ReadAsStringAsync()
                .ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Conflict, differentRawTimeZone.StatusCode);
            Assert.Contains(
                PropertiesApplicationErrors.TimeZoneDedicatedOperationRequired.Code,
                body,
                StringComparison.Ordinal);
            AssertNoStore(differentRawTimeZone);
        }

        PropertyMutationReceiptDto omittedTimeZoneUpdate;
        int dedicatedOutboxBeforeGenericUpdate =
            await CountDedicatedTimeZoneOutboxMessagesAsync(
                    publicApi,
                    firstProperty.PropertyId)
                .ConfigureAwait(false);
        using (HttpResponseMessage omitted = await SendAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{firstProperty.PropertyId:D}",
                   manager.AccessToken,
                   new
                   {
                       operationId = genericUpdateOperationId,
                       name = "Alpha House Updated",
                       code = "ALPHA",
                       expectedVersion = publicReceipt.Version
                   }).ConfigureAwait(false))
        {
            omittedTimeZoneUpdate =
                await ReadSuccessAsync<PropertyMutationReceiptDto>(omitted)
                    .ConfigureAwait(false);
            Assert.Equal(publicReceipt.Version + 1, omittedTimeZoneUpdate.Version);
            AssertNoStore(omitted);
        }

        using (HttpResponseMessage replay = await SendAsync(
                   publicClient,
                   HttpMethod.Put,
                   $"/api/properties/{firstProperty.PropertyId:D}",
                   manager.AccessToken,
                   new
                   {
                       operationId = genericUpdateOperationId,
                       name = "Alpha House Updated",
                       code = "ALPHA",
                       expectedVersion = publicReceipt.Version
                   }).ConfigureAwait(false))
        {
            Assert.Equal(
                omittedTimeZoneUpdate,
                await ReadSuccessAsync<PropertyMutationReceiptDto>(replay)
                    .ConfigureAwait(false));
            AssertNoStore(replay);
        }

        using (HttpResponseMessage updated = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   $"/api/properties/{firstProperty.PropertyId:D}",
                   manager.AccessToken).ConfigureAwait(false))
        {
            PropertyDto property = await ReadSuccessAsync<PropertyDto>(updated)
                .ConfigureAwait(false);
            Assert.Equal("Alpha House Updated", property.Name);
            Assert.Equal("Europe/Brussels", property.TimeZoneId);
            Assert.Equal(omittedTimeZoneUpdate.Version, property.Version);
            AssertNoStore(updated);
        }
        Assert.Equal(
            dedicatedOutboxBeforeGenericUpdate,
            await CountDedicatedTimeZoneOutboxMessagesAsync(
                    publicApi,
                    firstProperty.PropertyId)
                .ConfigureAwait(false));

        using (HttpResponseMessage untouched = await SendAsync(
                   publicClient,
                   HttpMethod.Get,
                   $"/api/properties/{secondProperty.PropertyId:D}",
                   manager.AccessToken).ConfigureAwait(false))
        {
            PropertyDto property = await ReadSuccessAsync<PropertyDto>(untouched)
                .ConfigureAwait(false);
            Assert.Equal(secondProperty.Version, property.Version);
            Assert.Equal("Etc/UTC", property.TimeZoneId);
            Assert.Equal(PropertyTimeZoneStatus.Canonical, property.TimeZoneStatus);
            Assert.False(property.TimeZoneCorrectionAllowed);
            Assert.NotEqual(default, property.TimeZoneObservedAtUtc);
            AssertNoStore(untouched);
        }

        Guid aliasOperationId = Guid.NewGuid();
        AdminCliResult aliasCorrection = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "time-zones", "set",
                "--actor", adminActor,
                "--tenant", TenantId,
                "--property-id", LegacyAliasPropertyId.ToString("D"),
                "--operation-id", aliasOperationId.ToString("D"),
                "--time-zone", "Etc/UTC",
                "--expected-version", "1",
                "--output", "json"));
        SetPropertyTimeZoneReceiptDto aliasReceipt =
            DeserializeCli<SetPropertyTimeZoneReceiptDto>(aliasCorrection);
        Assert.Equal(PropertyTimeZoneChangeKind.Canonicalized, aliasReceipt.ChangeKind);
        Assert.Equal($"admin-cli:{adminActor}", aliasReceipt.ActorId);
        await AssertChangedOperationCommitAsync(publicApi, aliasReceipt)
            .ConfigureAwait(false);

        Guid cliChangedOperationId = Guid.NewGuid();
        AdminCliResult cliUnconfirmed = await adminCli.ExecuteAsync(
            "properties", "time-zones", "set",
            "--actor", adminActor,
            "--tenant", TenantId,
            "--property-id", LegacyAliasPropertyId.ToString("D"),
            "--operation-id", cliChangedOperationId.ToString("D"),
            "--time-zone", "Europe/London",
            "--expected-version", aliasReceipt.Version.ToString(CultureInfo.InvariantCulture),
            "--output", "json").ConfigureAwait(false);
        Assert.Equal(AdminExitCodes.Failed, cliUnconfirmed.ExitCode);
        Assert.Contains("Confirmation is required", cliUnconfirmed.Error, StringComparison.Ordinal);
        Assert.True(
            await adminCli.CountAuditEntriesContainingAsync(
                PropertiesApplicationErrors.ConfirmationRequired.Code)
                .ConfigureAwait(false) > 0);

        AdminCliResult missingCliOperation = await adminCli.ExecuteAsync(
            "properties", "time-zones", "operation-get",
            "--actor", adminActor,
            "--tenant", TenantId,
            "--property-id", LegacyAliasPropertyId.ToString("D"),
            "--operation-id", cliChangedOperationId.ToString("D"),
            "--output", "json").ConfigureAwait(false);
        Assert.Equal(AdminExitCodes.Failed, missingCliOperation.ExitCode);

        AdminCliResult cliChanged = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "time-zones", "set",
                "--actor", adminActor,
                "--tenant", TenantId,
                "--property-id", LegacyAliasPropertyId.ToString("D"),
                "--operation-id", cliChangedOperationId.ToString("D"),
                "--time-zone", "Europe/London",
                "--expected-version", aliasReceipt.Version.ToString(CultureInfo.InvariantCulture),
                "--yes",
                "--output", "json"));
        SetPropertyTimeZoneReceiptDto cliReceipt =
            DeserializeCli<SetPropertyTimeZoneReceiptDto>(cliChanged);
        Assert.Equal(PropertyTimeZoneChangeKind.Changed, cliReceipt.ChangeKind);
        Assert.Equal($"admin-cli:{adminActor}", cliReceipt.ActorId);

        AdminCliResult cliRecovery = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "time-zones", "operation-get",
                "--actor", adminActor,
                "--tenant", TenantId,
                "--property-id", LegacyAliasPropertyId.ToString("D"),
                "--operation-id", cliChangedOperationId.ToString("D"),
                "--output", "json"));
        PropertyTimeZoneRecoveryDto cliRecovered =
            DeserializeCli<PropertyTimeZoneRecoveryDto>(cliRecovery);
        Assert.Equal(cliReceipt, cliRecovered.Receipt);
        Assert.NotEqual(default, cliRecovered.CurrentTimeZoneObservedAtUtc);

        AdminCliResult cliCompliance = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "time-zones", "compliance",
                "--actor", adminActor,
                "--tenant", TenantId,
                "--page-size", "1",
                "--output", "json"));
        PropertyTimeZoneCompliancePageDto cliCompliancePage =
            DeserializeCli<PropertyTimeZoneCompliancePageDto>(cliCompliance);
        Assert.NotEqual(default, cliCompliancePage.ObservedAtUtc);

        AdminCliResult cliProperty = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "get",
                "--actor", adminActor,
                "--tenant", TenantId,
                "--property-id", secondProperty.PropertyId.ToString("D"),
                "--output", "json"));
        PropertyDto cliPropertyDto = Assert.Single(
            DeserializeCli<PropertyDto[]>(cliProperty));
        Assert.NotEqual(default, cliPropertyDto.TimeZoneObservedAtUtc);

        Guid cliUpdateOperationId = Guid.NewGuid();
        AdminCliResult cliUpdate = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "update",
                "--actor", adminActor,
                "--tenant", TenantId,
                "--property-id", secondProperty.PropertyId.ToString("D"),
                "--operation-id", cliUpdateOperationId.ToString("D"),
                "--name", "Beta House Updated",
                "--code", "BETA",
                "--expected-version", secondProperty.Version.ToString(
                    CultureInfo.InvariantCulture),
                "--output", "json"));
        PropertyMutationReceiptDto cliUpdateReceipt =
            DeserializeCli<PropertyMutationReceiptDto>(cliUpdate);
        Assert.Equal(secondProperty.Version + 1, cliUpdateReceipt.Version);

        AdminCliResult cliUpdatedProperty = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "get",
                "--actor", adminActor,
                "--tenant", TenantId,
                "--property-id", secondProperty.PropertyId.ToString("D"),
                "--output", "json"));
        PropertyDto cliUpdatedPropertyDto = Assert.Single(
            DeserializeCli<PropertyDto[]>(cliUpdatedProperty));
        Assert.Equal("Beta House Updated", cliUpdatedPropertyDto.Name);
        Assert.Equal("Etc/UTC", cliUpdatedPropertyDto.TimeZoneId);
        Assert.Equal(cliUpdateReceipt.Version, cliUpdatedPropertyDto.Version);

        AdminCliResult cliCatalog = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "time-zones", "catalog",
                "--actor", adminActor,
                "--tenant", TenantId,
                "--search", "brussels",
                "--country-code", "nl",
                "--page-size", "1",
                "--output", "json"));
        PropertyTimeZoneCatalogPageDto cliCatalogPage =
            DeserializeCli<PropertyTimeZoneCatalogPageDto>(cliCatalog);
        Assert.Contains(
            cliCatalogPage.TimeZones,
            item => item.TimeZoneId == "Europe/Brussels" && item.RuntimeAvailable);

        string catalogAdminActor = CatalogAdminActorId.ToString("D");
        AdminCliResult readOnlyCliCatalog = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "time-zones", "catalog",
                "--actor", catalogAdminActor,
                "--tenant", TenantId,
                "--search", "Etc/UTC",
                "--output", "json"));
        Assert.Contains(
            DeserializeCli<PropertyTimeZoneCatalogPageDto>(readOnlyCliCatalog)
                .TimeZones,
            item => item.TimeZoneId == "Etc/UTC");
        AdminCliResult readOnlyCliCompliance = await adminCli.ExecuteAsync(
                "properties", "time-zones", "compliance",
                "--actor", catalogAdminActor,
                "--tenant", TenantId,
                "--output", "json")
            .ConfigureAwait(false);
        Assert.Equal(AdminExitCodes.Unauthorized, readOnlyCliCompliance.ExitCode);

        string propertyScopedAdminActor = PropertyScopedAdminActorId.ToString("D");
        AdminCliResult propertyScopedUnscopedCatalog = await adminCli.ExecuteAsync(
                "properties", "time-zones", "catalog",
                "--actor", propertyScopedAdminActor,
                "--tenant", TenantId,
                "--search", "Etc/UTC",
                "--output", "json")
            .ConfigureAwait(false);
        Assert.Equal(
            AdminExitCodes.Unauthorized,
            propertyScopedUnscopedCatalog.ExitCode);
        AdminCliResult propertyScopedCliCatalog = await AssertCliSuccessAsync(
            adminCli.ExecuteAsync(
                "properties", "time-zones", "catalog",
                "--actor", propertyScopedAdminActor,
                "--tenant", TenantId,
                "--property-id", firstProperty.PropertyId.ToString("D"),
                "--search", "Etc/UTC",
                "--output", "json"));
        Assert.Contains(
            DeserializeCli<PropertyTimeZoneCatalogPageDto>(propertyScopedCliCatalog)
                .TimeZones,
            item => item.TimeZoneId == "Etc/UTC");
        AdminCliResult crossPropertyCliCatalog = await adminCli.ExecuteAsync(
                "properties", "time-zones", "catalog",
                "--actor", propertyScopedAdminActor,
                "--tenant", TenantId,
                "--property-id", secondProperty.PropertyId.ToString("D"),
                "--search", "Etc/UTC",
                "--output", "json")
            .ConfigureAwait(false);
        Assert.Equal(AdminExitCodes.Unauthorized, crossPropertyCliCatalog.ExitCode);

        DateTimeOffset supportedObservation = systemClock.UtcNow;
        systemClock.UtcNow = new DateTimeOffset(
            1899,
            12,
            31,
            23,
            59,
            59,
            TimeSpan.Zero);
        foreach (string path in new[]
                 {
                     "/api/properties",
                     $"/api/properties/{firstProperty.PropertyId:D}",
                     $"/api/properties/{firstProperty.PropertyId:D}/time-zone/operations/{firstCreateOperationId:D}"
                 })
        {
            using HttpResponseMessage unavailable = await SendAsync(
                    publicClient,
                    HttpMethod.Get,
                    path,
                    manager.AccessToken)
                .ConfigureAwait(false);
            string body = await unavailable.Content.ReadAsStringAsync()
                .ConfigureAwait(false);
            Assert.Equal(
                HttpStatusCode.ServiceUnavailable,
                unavailable.StatusCode);
            Assert.Contains(
                PropertiesApplicationErrors.TimeSourceUnavailable.Code,
                body,
                StringComparison.Ordinal);
            AssertNoStore(unavailable);
        }

        AdminCliResult unavailableCli = await adminCli.ExecuteAsync(
                "properties", "get",
                "--actor", adminActor,
                "--tenant", TenantId,
                "--property-id", secondProperty.PropertyId.ToString("D"),
                "--output", "json")
            .ConfigureAwait(false);
        Assert.Equal(AdminExitCodes.Failed, unavailableCli.ExitCode);
        Assert.Contains(
            PropertiesApplicationErrors.TimeSourceUnavailable.Message,
            unavailableCli.Error,
            StringComparison.Ordinal);
        Assert.True(
            await adminCli.CountAuditEntriesContainingAsync(
                    PropertiesApplicationErrors.TimeSourceUnavailable.Code)
                .ConfigureAwait(false) > 0);
        systemClock.UtcNow = supportedObservation;

        await using AdminApiTestApplication adminApi = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            systemClock: systemClock);
        await adminApi.MigrateAsync().ConfigureAwait(false);
        await adminApi.MigratePropertiesAsync().ConfigureAwait(false);
        using HttpClient adminClient = adminApi.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            adminApi.CreateAccessToken(AdminActorId, TenantId));
        adminClient.DefaultRequestHeaders.Add(TenantHeader, TenantId);
        await AssertUpdateTimeZoneIsOptionalInOpenApiAsync(
                adminApi.Services,
                "/api/admin/properties/{propertyId}")
            .ConfigureAwait(false);

        using (HttpResponseMessage routingFailure = await adminClient.GetAsync(
                   "/api/admin/properties/not-a-guid/time-zone/operations/not-a-guid")
                   .ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.NotFound, routingFailure.StatusCode);
            AssertNoStore(routingFailure);
        }

        using (HttpResponseMessage bindingFailure = await SendMalformedJsonAsync(
                   adminClient,
                   HttpMethod.Put,
                   $"/api/admin/properties/{firstProperty.PropertyId:D}/time-zone")
                   .ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bindingFailure.StatusCode);
            AssertNoStore(bindingFailure);
        }

        using (HttpClient propertyScopedAdminClient = adminApi.CreateClient())
        {
            propertyScopedAdminClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    adminApi.CreateAccessToken(
                        PropertyScopedAdminActorId,
                        TenantId));
            propertyScopedAdminClient.DefaultRequestHeaders.Add(
                TenantHeader,
                TenantId);

            using (HttpResponseMessage tenantCatalog =
                   await propertyScopedAdminClient
                       .GetAsync("/api/admin/properties/time-zones/catalog")
                       .ConfigureAwait(false))
            {
                Assert.Equal(HttpStatusCode.Forbidden, tenantCatalog.StatusCode);
                AssertNoStore(tenantCatalog);
            }

            using (HttpResponseMessage scopedCatalog =
                   await propertyScopedAdminClient.GetAsync(
                           $"/api/admin/properties/{firstProperty.PropertyId:D}/time-zones/catalog?search=Etc%2FUTC")
                       .ConfigureAwait(false))
            {
                PropertyTimeZoneCatalogPageDto page =
                    await ReadSuccessAsync<PropertyTimeZoneCatalogPageDto>(scopedCatalog)
                        .ConfigureAwait(false);
                Assert.Contains(
                    page.TimeZones,
                    item => item.TimeZoneId == "Etc/UTC");
                AssertNoStore(scopedCatalog);
            }

            using (HttpResponseMessage crossPropertyCatalog =
                   await propertyScopedAdminClient.GetAsync(
                           $"/api/admin/properties/{secondProperty.PropertyId:D}/time-zones/catalog?search=Etc%2FUTC")
                       .ConfigureAwait(false))
            {
                Assert.Equal(
                    HttpStatusCode.Forbidden,
                    crossPropertyCatalog.StatusCode);
                AssertNoStore(crossPropertyCatalog);
            }

            using (HttpResponseMessage scopedRecovery =
                   await propertyScopedAdminClient.GetAsync(
                           $"/api/admin/properties/{firstProperty.PropertyId:D}/time-zone/operations/{firstCreateOperationId:D}")
                       .ConfigureAwait(false))
            {
                PropertyTimeZoneRecoveryDto recovered =
                    await ReadSuccessAsync<PropertyTimeZoneRecoveryDto>(scopedRecovery)
                        .ConfigureAwait(false);
                Assert.Equal(firstCreateOperationId, recovered.Receipt.OperationId);
                AssertNoStore(scopedRecovery);
            }

            using (HttpResponseMessage crossPropertyRecovery =
                   await propertyScopedAdminClient.GetAsync(
                           $"/api/admin/properties/{secondProperty.PropertyId:D}/time-zone/operations/{secondCreateOperationId:D}")
                       .ConfigureAwait(false))
            {
                Assert.Equal(
                    HttpStatusCode.Forbidden,
                    crossPropertyRecovery.StatusCode);
                AssertNoStore(crossPropertyRecovery);
            }
        }

        using (HttpClient catalogAdminClient = adminApi.CreateClient())
        {
            catalogAdminClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    adminApi.CreateAccessToken(CatalogAdminActorId, TenantId));
            catalogAdminClient.DefaultRequestHeaders.Add(TenantHeader, TenantId);

            using (HttpResponseMessage catalog = await catalogAdminClient.GetAsync(
                       "/api/admin/properties/time-zones/catalog?search=Etc%2FUTC")
                   .ConfigureAwait(false))
            {
                PropertyTimeZoneCatalogPageDto page =
                    await ReadSuccessAsync<PropertyTimeZoneCatalogPageDto>(catalog)
                        .ConfigureAwait(false);
                Assert.Contains(
                    page.TimeZones,
                    item => item.TimeZoneId == "Etc/UTC");
                AssertNoStore(catalog);
            }

            using (HttpResponseMessage compliance =
                   await catalogAdminClient.GetAsync(
                           "/api/admin/properties/time-zones/compliance")
                       .ConfigureAwait(false))
            {
                Assert.Equal(HttpStatusCode.Forbidden, compliance.StatusCode);
                AssertNoStore(compliance);
            }
        }

        using (HttpClient anonymousAdminClient = adminApi.CreateClient())
        using (HttpRequestMessage request = new(
                   HttpMethod.Get,
                   "/api/admin/properties/time-zones/catalog"))
        {
            request.Headers.Add(TenantHeader, TenantId);
            using HttpResponseMessage response = await anonymousAdminClient
                .SendAsync(request).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            AssertNoStore(response);
        }

        using (HttpClient strangerAdminClient = adminApi.CreateClient())
        {
            strangerAdminClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    adminApi.CreateAccessToken(Guid.NewGuid(), TenantId));
            strangerAdminClient.DefaultRequestHeaders.Add(TenantHeader, TenantId);
            using HttpResponseMessage response = await strangerAdminClient
                .GetAsync("/api/admin/properties/time-zones/compliance")
                .ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            AssertNoStore(response);
        }

        PropertyMutationReceiptDto adminProperty;
        using (HttpResponseMessage created = await adminClient.PostAsJsonAsync(
                   "/api/admin/properties",
                   new
                   {
                       operationId = Guid.NewGuid(),
                       name = "Admin House",
                       code = "ADMIN",
                       timeZoneId = "Etc/UTC"
                   }).ConfigureAwait(false))
        {
            adminProperty = await ReadSuccessAsync<PropertyMutationReceiptDto>(created)
                .ConfigureAwait(false);
            AssertNoStore(created);
        }

        Guid adminOperationId = Guid.NewGuid();
        Guid rejectedAdminOperationId = Guid.NewGuid();
        using (HttpResponseMessage rejectedSet = await adminClient.PutAsJsonAsync(
                   $"/api/admin/properties/{adminProperty.PropertyId:D}/time-zone",
                   new
                   {
                       operationId = rejectedAdminOperationId,
                       timeZoneId = "Pacific Standard Time",
                       confirmed = true,
                       expectedVersion = adminProperty.Version
                   }).ConfigureAwait(false))
        {
            string body = await rejectedSet.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedSet.StatusCode);
            Assert.Contains(PropertiesDomainErrors.TimeZoneInvalid.Code, body, StringComparison.Ordinal);
            AssertNoStore(rejectedSet);
        }
        Assert.Equal(
            (1, 0),
            await CountPropertyAndTimeZoneOperationAsync(
                    publicApi,
                    adminProperty.PropertyId,
                    rejectedAdminOperationId)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await CountMaterialTimeZoneOutboxMessagesAsync(
                    publicApi,
                    adminProperty.PropertyId)
                .ConfigureAwait(false));

        SetPropertyTimeZoneReceiptDto adminReceipt;
        using (HttpResponseMessage set = await adminClient.PutAsJsonAsync(
                   $"/api/admin/properties/{adminProperty.PropertyId:D}/time-zone",
                   new
                   {
                       operationId = adminOperationId,
                       timeZoneId = "Etc/UTC",
                       confirmed = false,
                       expectedVersion = adminProperty.Version
                   }).ConfigureAwait(false))
        {
            adminReceipt = await ReadSuccessAsync<SetPropertyTimeZoneReceiptDto>(set)
                .ConfigureAwait(false);
            Assert.Equal(PropertyTimeZoneChangeKind.Unchanged, adminReceipt.ChangeKind);
            Assert.Equal($"admin-api:{adminActor}", adminReceipt.ActorId);
            AssertNoStore(set);
        }

        using (HttpResponseMessage recovery = await adminClient.GetAsync(
                   $"/api/admin/properties/{adminProperty.PropertyId:D}/time-zone/operations/{adminOperationId:D}")
                   .ConfigureAwait(false))
        {
            PropertyTimeZoneRecoveryDto recovered =
                await ReadSuccessAsync<PropertyTimeZoneRecoveryDto>(recovery)
                    .ConfigureAwait(false);
            Assert.Equal(adminReceipt, recovered.Receipt);
            Assert.NotEqual(default, recovered.CurrentTimeZoneObservedAtUtc);
            AssertNoStore(recovery);
        }
        Assert.Equal(
            0,
            await CountMaterialTimeZoneOutboxMessagesAsync(
                    publicApi,
                    adminProperty.PropertyId)
                .ConfigureAwait(false));

        Guid adminUpdateOperationId = Guid.NewGuid();
        using (HttpResponseMessage suppliedAlias = await adminClient.PutAsJsonAsync(
                   $"/api/admin/properties/{adminProperty.PropertyId:D}",
                   new
                   {
                       operationId = adminUpdateOperationId,
                       name = "Admin House Updated",
                       code = "ADMIN",
                       timeZoneId = "UTC",
                       expectedVersion = adminProperty.Version
                   }).ConfigureAwait(false))
        {
            string body = await suppliedAlias.Content.ReadAsStringAsync()
                .ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Conflict, suppliedAlias.StatusCode);
            Assert.Contains(
                PropertiesApplicationErrors.TimeZoneDedicatedOperationRequired.Code,
                body,
                StringComparison.Ordinal);
            AssertNoStore(suppliedAlias);
        }

        PropertyMutationReceiptDto adminUpdateReceipt;
        using (HttpResponseMessage omitted = await adminClient.PutAsJsonAsync(
                   $"/api/admin/properties/{adminProperty.PropertyId:D}",
                   new
                   {
                       operationId = adminUpdateOperationId,
                       name = "Admin House Updated",
                       code = "ADMIN",
                       expectedVersion = adminProperty.Version
                   }).ConfigureAwait(false))
        {
            adminUpdateReceipt =
                await ReadSuccessAsync<PropertyMutationReceiptDto>(omitted)
                    .ConfigureAwait(false);
            Assert.Equal(adminProperty.Version + 1, adminUpdateReceipt.Version);
            AssertNoStore(omitted);
        }

        using (HttpResponseMessage updated = await adminClient.GetAsync(
                   $"/api/admin/properties/{adminProperty.PropertyId:D}")
                   .ConfigureAwait(false))
        {
            PropertyDto property = await ReadSuccessAsync<PropertyDto>(updated)
                .ConfigureAwait(false);
            Assert.Equal("Admin House Updated", property.Name);
            Assert.Equal("Etc/UTC", property.TimeZoneId);
            Assert.Equal(adminUpdateReceipt.Version, property.Version);
            AssertNoStore(updated);
        }
        Assert.Equal(
            0,
            await CountDedicatedTimeZoneOutboxMessagesAsync(
                    publicApi,
                    adminProperty.PropertyId)
                .ConfigureAwait(false));

        DateTimeOffset supportedAdminObservation = systemClock.UtcNow;
        systemClock.UtcNow = new DateTimeOffset(
            1899,
            12,
            31,
            23,
            59,
            59,
            TimeSpan.Zero);
        foreach (string path in new[]
                 {
                     "/api/admin/properties",
                     $"/api/admin/properties/{adminProperty.PropertyId:D}",
                     $"/api/admin/properties/{adminProperty.PropertyId:D}/time-zone/operations/{adminOperationId:D}"
                 })
        {
            using HttpResponseMessage unavailable = await adminClient.GetAsync(path)
                .ConfigureAwait(false);
            string body = await unavailable.Content.ReadAsStringAsync()
                .ConfigureAwait(false);
            Assert.Equal(
                HttpStatusCode.ServiceUnavailable,
                unavailable.StatusCode);
            Assert.Contains(
                PropertiesApplicationErrors.TimeSourceUnavailable.Code,
                body,
                StringComparison.Ordinal);
            AssertNoStore(unavailable);
        }
        systemClock.UtcNow = supportedAdminObservation;

        Assert.True(
            await adminApi.CountAuditEntriesAsync(
                PropertiesAdminOperationNames.PropertyTimeZoneSet)
                .ConfigureAwait(false) > 0);
        Assert.True(
            await adminApi.CountAuditEntriesAsync(
                PropertiesAdminOperationNames.PropertyTimeZoneOperationGet)
                .ConfigureAwait(false) > 0);
    }

    private static async Task GrantManagerAsync(
        AdminCliTestApplication admin,
        Guid managerId,
        string adminActor)
    {
        await AssertCliSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create",
            "--actor", adminActor,
            "--name", "time-zone-manager"));
        foreach (string permission in new[]
                 {
                     PropertiesAdminPermissionCodes.Read,
                     PropertiesAdminPermissionCodes.PropertiesManage,
                     PropertiesAdminPermissionCodes.TimeZonesManage
                 })
        {
            await AssertCliSuccessAsync(admin.ExecuteAsync(
                "admin", "roles", "grant",
                "--actor", adminActor,
                "--role", "time-zone-manager",
                "--permission", permission));
        }

        await AssertCliSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", adminActor,
            "--target-kind", "user",
            "--target-id", managerId.ToString("D"),
            "--role", "time-zone-manager",
            "--scope", $"tenant:{TenantId}"));
    }

    private static async Task GrantPropertyTimeZoneOperatorAsync(
        AdminCliTestApplication admin,
        Guid operatorId,
        string adminActor,
        Guid propertyId)
    {
        await AssertCliSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create",
            "--actor", adminActor,
            "--name", "property-time-zone-operator"));
        foreach (string permission in new[]
                 {
                     PropertiesAdminPermissionCodes.Read,
                     PropertiesAdminPermissionCodes.TimeZonesManage
                 })
        {
            await AssertCliSuccessAsync(admin.ExecuteAsync(
                "admin", "roles", "grant",
                "--actor", adminActor,
                "--role", "property-time-zone-operator",
                "--permission", permission));
        }

        await AssertCliSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", adminActor,
            "--target-kind", "user",
            "--target-id", operatorId.ToString("D"),
            "--role", "property-time-zone-operator",
            "--scope", $"tenant:{TenantId}/property:{propertyId:D}"));
        await AssertCliSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", adminActor,
            "--target-kind", "admin-actor",
            "--target-id", PropertyScopedAdminActorId.ToString("D"),
            "--role", "property-time-zone-operator",
            "--scope", $"tenant:{TenantId}/property:{propertyId:D}"));
    }

    private static async Task GrantCatalogReadersAsync(
        AdminCliTestApplication admin,
        Guid publicReaderId,
        string adminActor)
    {
        await AssertCliSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create",
            "--actor", adminActor,
            "--name", "property-catalog-reader"));
        await AssertCliSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "grant",
            "--actor", adminActor,
            "--role", "property-catalog-reader",
            "--permission", PropertiesAdminPermissionCodes.Read));
        await AssertCliSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", adminActor,
            "--target-kind", "user",
            "--target-id", publicReaderId.ToString("D"),
            "--role", "property-catalog-reader",
            "--scope", $"tenant:{TenantId}"));
        await AssertCliSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", adminActor,
            "--target-kind", "admin-actor",
            "--target-id", CatalogAdminActorId.ToString("D"),
            "--role", "property-catalog-reader",
            "--scope", $"tenant:{TenantId}"));
    }

    private static async Task<(PropertyMutationReceiptDto Receipt, Guid OperationId)> CreatePropertyAsync(
        HttpClient client,
        string accessToken,
        string name,
        string code)
    {
        Guid operationId = Guid.NewGuid();
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Post,
            "/api/properties",
            accessToken,
            new
            {
                operationId,
                name,
                code,
                timeZoneId = "UTC"
            }).ConfigureAwait(false);
        AssertNoStore(response);
        PropertyMutationReceiptDto receipt =
            await ReadSuccessAsync<PropertyMutationReceiptDto>(response)
                .ConfigureAwait(false);
        return (receipt, operationId);
    }

    private static async Task SeedLegacyAliasBeforeConvergenceAsync(
        AuthTestApplication application)
    {
        using IServiceScope scope = application.Services.CreateScope();
        ITenantContextAccessor tenant =
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        tenant.SetTenant(TenantId);
        PropertiesDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<PropertiesDbContext>();
        int inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO properties.properties
                    ("Id", "Name", "Code", "TimeZoneId", "Status",
                     "CreatedAtUtc", "ScopeId")
                VALUES
                    ({LegacyAliasPropertyId}, {"Pre-upgrade alias"},
                     {"legacy-alias"}, {"UTC"}, 1,
                     {DateTimeOffset.UtcNow}, {TenantId});

                INSERT INTO properties.property_operation_locks
                    ("Id", "PropertyId", "Revision", "ScopeId")
                VALUES
                    ({LegacyAliasPropertyId}, {LegacyAliasPropertyId}, 1,
                     {TenantId});
                """)
            .ConfigureAwait(false);
        Assert.Equal(2, inserted);
    }

    private static async Task AssertPostConvergenceRawAliasUpdateRejectedAsync(
        AuthTestApplication application)
    {
        using IServiceScope scope = application.Services.CreateScope();
        ITenantContextAccessor tenant =
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        tenant.SetTenant(TenantId);
        PropertiesDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<PropertiesDbContext>();
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE properties.properties
                SET "TimeZoneId" = {"Etc/UTC"}
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {LegacyAliasPropertyId};
                """));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, failure.SqlState);

        string stored = await dbContext.Database.SqlQuery<string>($"""
                SELECT "TimeZoneId" AS "Value"
                FROM properties.properties
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {LegacyAliasPropertyId}
                """).SingleAsync().ConfigureAwait(false);
        Assert.Equal("UTC", stored);
    }

    private static async Task<(int Properties, int Operations)>
        CountPropertyAndTimeZoneOperationAsync(
            AuthTestApplication application,
            Guid propertyId,
            Guid operationId)
    {
        using IServiceScope scope = application.Services.CreateScope();
        ITenantContextAccessor tenant =
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        tenant.SetTenant(TenantId);
        PropertiesDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<PropertiesDbContext>();
        int properties = await dbContext.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value"
                FROM properties.properties
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {propertyId}
                """).SingleAsync().ConfigureAwait(false);
        int operations = await dbContext.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value"
                FROM properties.property_time_zone_operations
                WHERE "ScopeId" = {TenantId}
                  AND "PropertyId" = {propertyId}
                  AND "OperationId" = {operationId}
                """).SingleAsync().ConfigureAwait(false);
        return (properties, operations);
    }

    private static async Task AssertChangedOperationCommitAsync(
        AuthTestApplication application,
        SetPropertyTimeZoneReceiptDto receipt)
    {
        using IServiceScope scope = application.Services.CreateScope();
        ITenantContextAccessor tenant =
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        tenant.SetTenant(TenantId);
        PropertiesDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<PropertiesDbContext>();

        long storedVersion = await dbContext.Properties
            .Where(property => property.Id == receipt.PropertyId)
            .Select(property => property.Version)
            .SingleAsync()
            .ConfigureAwait(false);
        Assert.Equal(receipt.Version, storedVersion);

        int operationCount = await dbContext.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value"
                FROM properties.property_time_zone_operations
                WHERE "ScopeId" = {TenantId}
                  AND "PropertyId" = {receipt.PropertyId}
                  AND "OperationId" = {receipt.OperationId}
                  AND "ResultVersion" = {receipt.Version}
                """).SingleAsync().ConfigureAwait(false);
        Assert.Equal(1, operationCount);

        OutboxMessage[] messages = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(message =>
                message.ScopeId == TenantId &&
                (EF.Functions.Like(
                     message.EventType,
                     $"%{nameof(PropertyUpdatedIntegrationEvent)}%") ||
                 EF.Functions.Like(
                     message.EventType,
                     $"%{nameof(PropertyTimeZoneChangedIntegrationEvent)}%")))
            .ToArrayAsync()
            .ConfigureAwait(false);

        messages = messages
            .Where(message => message.Payload.Contains(
                receipt.PropertyId.ToString("D"),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        OutboxMessage updated = Assert.Single(messages, message =>
            message.EventType.Contains(
                nameof(PropertyUpdatedIntegrationEvent),
                StringComparison.Ordinal));
        Assert.Equal(PropertyUpdatedIntegrationEvent.EventVersion, updated.Version);
        PropertyUpdatedIntegrationEvent? updatedEvent =
            JsonSerializer.Deserialize<PropertyUpdatedIntegrationEvent>(
                updated.Payload,
                JsonOptions);
        Assert.NotNull(updatedEvent);
        Assert.Equal(receipt.PropertyId, updatedEvent.PropertyId);
        Assert.Equal(receipt.TimeZoneId, updatedEvent.TimeZoneId);
        Assert.Equal(receipt.Version, updatedEvent.PropertyVersion);

        OutboxMessage dedicated = Assert.Single(messages, message =>
            message.EventType.Contains(
                nameof(PropertyTimeZoneChangedIntegrationEvent),
                StringComparison.Ordinal));
        Assert.Equal(
            PropertyTimeZoneChangedIntegrationEvent.EventVersion,
            dedicated.Version);
        PropertyTimeZoneChangedIntegrationEvent? dedicatedEvent =
            JsonSerializer.Deserialize<PropertyTimeZoneChangedIntegrationEvent>(
                dedicated.Payload,
                JsonOptions);
        Assert.NotNull(dedicatedEvent);
        Assert.Equal(receipt.PropertyId, dedicatedEvent.PropertyId);
        Assert.Equal(receipt.PreviousTimeZoneId, dedicatedEvent.PreviousTimeZoneId);
        Assert.Equal(receipt.TimeZoneId, dedicatedEvent.TimeZoneId);
        Assert.Equal(receipt.ChangeKind, dedicatedEvent.ChangeKind);
        Assert.Equal(receipt.CatalogVersion, dedicatedEvent.CatalogVersion);
        Assert.Equal(receipt.Version, dedicatedEvent.PropertyVersion);
    }

    private static async Task<int> CountMaterialTimeZoneOutboxMessagesAsync(
        AuthTestApplication application,
        Guid propertyId)
    {
        using IServiceScope scope = application.Services.CreateScope();
        ITenantContextAccessor tenant =
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        tenant.SetTenant(TenantId);
        PropertiesDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<PropertiesDbContext>();

        OutboxMessage[] messages = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(message =>
                message.ScopeId == TenantId &&
                (EF.Functions.Like(
                     message.EventType,
                     $"%{nameof(PropertyUpdatedIntegrationEvent)}%") ||
                 EF.Functions.Like(
                     message.EventType,
                     $"%{nameof(PropertyTimeZoneChangedIntegrationEvent)}%")))
            .ToArrayAsync()
            .ConfigureAwait(false);

        return messages.Count(message =>
            message.Payload.Contains(
                propertyId.ToString("D"),
                StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<int> CountDedicatedTimeZoneOutboxMessagesAsync(
        AuthTestApplication application,
        Guid propertyId)
    {
        using IServiceScope scope = application.Services.CreateScope();
        ITenantContextAccessor tenant =
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        tenant.SetTenant(TenantId);
        PropertiesDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<PropertiesDbContext>();

        OutboxMessage[] messages = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(message =>
                message.ScopeId == TenantId &&
                EF.Functions.Like(
                    message.EventType,
                    $"%{nameof(PropertyTimeZoneChangedIntegrationEvent)}%"))
            .ToArrayAsync()
            .ConfigureAwait(false);

        return messages.Count(message =>
            message.Payload.Contains(
                propertyId.ToString("D"),
                StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? accessToken = null,
        object? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add(TenantHeader, TenantId);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task AssertUpdateTimeZoneIsOptionalInOpenApiAsync(
        IServiceProvider services,
        string operationPath)
    {
        ISwaggerProvider swagger = services.GetRequiredService<ISwaggerProvider>();
        string body = await swagger.GetSwagger("v1")
            .SerializeAsync(OpenApiSpecVersion.OpenApi3_0, "json")
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement schema = document.RootElement
            .GetProperty("paths")
            .GetProperty(operationPath)
            .GetProperty("put")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            const string prefix = "#/components/schemas/";
            string value = reference.GetString()!;
            Assert.StartsWith(prefix, value, StringComparison.Ordinal);
            schema = document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty(value[prefix.Length..]);
        }

        Assert.True(schema.GetProperty("properties").TryGetProperty(
            "timeZoneId",
            out _));
        if (schema.TryGetProperty("required", out JsonElement required))
        {
            Assert.DoesNotContain(
                required.EnumerateArray().Select(item => item.GetString()),
                item => string.Equals(
                    item,
                    "timeZoneId",
                    StringComparison.Ordinal));
        }
    }

    private static async Task<HttpResponseMessage> SendMalformedJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? accessToken = null)
    {
        using HttpRequestMessage request = new(method, path);
        if (!client.DefaultRequestHeaders.Contains(TenantHeader))
        {
            request.Headers.Add(TenantHeader, TenantId);
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
        }
        request.Content = new StringContent(
            "{\"operationId\":",
            Encoding.UTF8,
            "application/json");
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<T> ReadSuccessAsync<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected success but received {(int)response.StatusCode}. Body: {body}");
        T? value = JsonSerializer.Deserialize<T>(body, JsonOptions);
        Assert.NotNull(value);
        return value;
    }

    private static async Task<AdminCliResult> AssertCliSuccessAsync(
        Task<AdminCliResult> resultTask)
    {
        AdminCliResult result = await resultTask.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == AdminExitCodes.Success,
            $"ExitCode={result.ExitCode}{Environment.NewLine}Output:{Environment.NewLine}{result.Output}{Environment.NewLine}Error:{Environment.NewLine}{result.Error}");
        return result;
    }

    private static T DeserializeCli<T>(AdminCliResult result)
    {
        T? value = JsonSerializer.Deserialize<T>(result.Output, JsonOptions);
        Assert.NotNull(value);
        return value;
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
        Assert.Contains(
            response.Headers.Pragma,
            value => string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase));
        bool hasExpires = response.Headers.TryGetValues("Expires", out IEnumerable<string>? expires)
            || response.Content.Headers.TryGetValues("Expires", out expires);
        Assert.True(hasExpires);
        Assert.NotNull(expires);
        Assert.Equal("0", Assert.Single(expires));
    }

    private static string WithAuthenticationTime(
        string accessToken,
        DateTimeOffset authenticatedAtUtc)
    {
        JwtSecurityToken source =
            new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        string[] generatedClaims =
        [
            JwtRegisteredClaimNames.Iat,
            JwtRegisteredClaimNames.Nbf,
            JwtRegisteredClaimNames.Exp,
            JwtRegisteredClaimNames.Iss,
            JwtRegisteredClaimNames.Aud,
            ApplicationClaimNames.AuthenticationTime
        ];
        List<Claim> claims = source.Claims
            .Where(claim => !generatedClaims.Contains(
                claim.Type,
                StringComparer.Ordinal))
            .ToList();
        claims.Add(new Claim(
            ApplicationClaimNames.AuthenticationTime,
            authenticatedAtUtc.ToUnixTimeSeconds().ToString(
                CultureInfo.InvariantCulture),
            ClaimValueTypes.Integer64));
        SigningCredentials credentials = new(
            new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(AuthTestApplication.JwtSigningKey)),
            SecurityAlgorithms.HmacSha256);
        JwtSecurityToken token = new(
            issuer: "BunkFy",
            audience: "BunkFy",
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: source.ValidTo,
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static Guid SubjectId(string accessToken)
    {
        JwtSecurityToken token =
            new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        string? value = token.Claims.FirstOrDefault(claim =>
            claim.Type is ClaimTypes.NameIdentifier or "nameid" or
                ApplicationClaimNames.Subject)?.Value;
        Assert.True(Guid.TryParse(value, out Guid id));
        return id;
    }

    private sealed class MutableSystemClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}

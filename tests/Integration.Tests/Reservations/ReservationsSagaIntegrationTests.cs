namespace Integration.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BunkFy.Host.Worker;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Admin.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Persistence;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Results;
using Gma.Framework.Tenancy;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class ReservationsSagaIntegrationTests
{
    private const string TenantId = "a3000000-0000-0000-0000-000000000001";
    private const string TenantHeader = "X-Tenant-Id";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid PropertyId = Guid.Parse("71000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherPropertyId = Guid.Parse("71000000-0000-0000-0000-000000000002");
    private static readonly Guid RoomId = Guid.Parse("72000000-0000-0000-0000-000000000001");
    private static readonly Guid ReplacementRoomId =
        Guid.Parse("72000000-0000-0000-0000-000000000002");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Real_worker_coordinates_reservation_and_stay_lifecycles_through_jetstream()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_reservations_saga_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        string natsConnectionString = AuthTestContainers.GetNatsConnectionString(nats);
        await using (AuthTestApplication migrationApi = new(
                         "PostgreSql",
                         connectionString,
                         natsConnectionString,
                         disableOutboxPublisher: true))
        {
            await migrationApi.MigrateGuestDataRightsAuthorizationDatabaseAsync()
                .ConfigureAwait(false);
            await migrationApi.MigrateStaffAuthorizationDatabaseAsync()
                .ConfigureAwait(false);
            await migrationApi.MigrateIngestionDatabaseAsync()
                .ConfigureAwait(false);
            await migrationApi.MigrateRetentionDatabaseAsync()
                .ConfigureAwait(false);
        }

        await using AdminCliTestApplication admin = new(
            "PostgreSql",
            connectionString,
            includeReservations: true);
        await admin.MigrateAsync().ConfigureAwait(false);
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            disableOutboxPublisher: false);
        await AssertInventoryHoldTruthIsRequiredInOpenApiAsync(api.Services)
            .ConfigureAwait(false);
        await using AdminApiTestApplication adminApi = new(
            "PostgreSql",
            connectionString,
            natsConnectionString);
        using HttpClient client = api.CreateClient();
        using HttpClient adminClient = adminApi.CreateClient();

        using IHost initialWorker = CreateWorker(connectionString, natsConnectionString);
        IHost? stayWorker = null;
        await initialWorker.StartAsync().ConfigureAwait(false);
        try
        {
            await SeedInventoryAsync(api).ConfigureAwait(false);
            await SeedGovernedPropertyProjectionsAsync(api).ConfigureAwait(false);
            await WaitForSellableProjectionAsync(api, TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(
                client,
                TenantId,
                "operator@reservations.test").ConfigureAwait(false);
            Guid operatorId = GetSubjectId(tokens.AccessToken);
            Guid adminActorId = Guid.NewGuid();
            await api.SeedOrganizationMembershipAsync(TenantId, operatorId).ConfigureAwait(false);
            await GrantReservationsAccessAsync(admin, operatorId).ConfigureAwait(false);
            AuthTokensResponse readerTokens = await AuthApiClient.RegisterAsync(
                client,
                TenantId,
                "reader@reservations.test").ConfigureAwait(false);
            Guid readerId = GetSubjectId(readerTokens.AccessToken);
            await api.SeedOrganizationMembershipAsync(TenantId, readerId).ConfigureAwait(false);
            await GrantReservationsReadAccessAsync(admin, readerId).ConfigureAwait(false);
            await GrantReservationsAdminAccessAsync(admin, operatorId, adminActorId).ConfigureAwait(false);

            await using AdminApiTestApplication reservationsAdminApi = new(
                "PostgreSql",
                connectionString,
                natsConnectionString);
            using HttpClient reservationsAdminClient = reservationsAdminApi.CreateClient();
            reservationsAdminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                AdminApiTestApplication.CreateAccessTokenWithTenantClaim(operatorId, TenantId));
            reservationsAdminClient.DefaultRequestHeaders.Add(TenantHeader, TenantId);

            GuestMutationReceiptDto canonicalGuest = await CreateGuestAsync(
                client,
                tokens.AccessToken,
                "Canonical Replacement Guest").ConfigureAwait(false);
            await WaitForGuestEligibilityProjectionAsync(
                api,
                canonicalGuest.GuestId,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            ReservationMutationReceiptDto first = await CreateReservationAsync(
                client,
                tokens.AccessToken,
                new DateOnly(2026, 10, 1),
                new DateOnly(2026, 10, 3),
                "First Guest").ConfigureAwait(false);
            Assert.Equal(ReservationStatus.PendingAllocation, first.Status);
            ReservationDto confirmed = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                first.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.NotNull(confirmed.AllocationId);

            ReservationMutationReceiptDto overlapping = await CreateReservationAsync(
                client,
                tokens.AccessToken,
                new DateOnly(2026, 10, 2),
                new DateOnly(2026, 10, 4),
                "Overlapping Guest").ConfigureAwait(false);
            await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                overlapping.ReservationId,
                ReservationStatus.AllocationRejected,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            const string operationsPathSuffix =
                "operations-snapshot?localDate=2026-10-01&upcomingLimit=1";
            using (HttpResponseMessage operations = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/reservations/properties/{PropertyId:D}/{operationsPathSuffix}",
                       tokens.AccessToken).ConfigureAwait(false))
            {
                ReservationOperationsSnapshotDto snapshot =
                    await ReadSuccessAsync<ReservationOperationsSnapshotDto>(operations)
                        .ConfigureAwait(false);
                AssertNoStore(operations);
                Assert.Equal(PropertyId, snapshot.PropertyId);
                Assert.Equal(new DateOnly(2026, 10, 1), snapshot.LocalDate);
                Assert.Equal("UTC", snapshot.TimeZoneId);
                Assert.Equal(ReservationOperationsDateSource.Explicit, snapshot.DateSource);
                Assert.Equal(1, snapshot.UpcomingLimit);
                Assert.Single(snapshot.Upcoming);
                Assert.Equal("First Guest", snapshot.Upcoming.Single().PrimaryGuestName);
                Assert.Equal(1, snapshot.Upcoming.Single().GuestCount);
                Assert.Equal(1, snapshot.Cohorts.ConfirmedArrivalsOnLocalDate.ReservationCount);
                Assert.Equal(1, snapshot.Cohorts.ConfirmedArrivalsOnLocalDate.GuestCount);
                Assert.Equal(1, snapshot.Attention.AllocationRejected.ReservationCount);
                Assert.Equal(1, snapshot.Attention.Total.ReservationCount);
            }

            using (HttpResponseMessage crossPropertyOperations = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/reservations/properties/{OtherPropertyId:D}/{operationsPathSuffix}",
                       tokens.AccessToken).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Forbidden, crossPropertyOperations)
                    .ConfigureAwait(false);
                AssertNoStore(crossPropertyOperations);
            }

            using (HttpResponseMessage anonymousOperations = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/reservations/properties/{PropertyId:D}/{operationsPathSuffix}")
                       .ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Unauthorized, anonymousOperations)
                    .ConfigureAwait(false);
                AssertNoStore(anonymousOperations);
            }

            using (HttpResponseMessage malformedDate = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/reservations/properties/{PropertyId:D}/operations-snapshot?localDate=not-a-date",
                       tokens.AccessToken).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.BadRequest, malformedDate)
                    .ConfigureAwait(false);
                AssertNoStore(malformedDate);
            }

            using (HttpResponseMessage invalidLimit = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/reservations/properties/{PropertyId:D}/operations-snapshot?upcomingLimit=51",
                       tokens.AccessToken).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.BadRequest, invalidLimit)
                    .ConfigureAwait(false);
                AssertNoStore(invalidLimit);
            }

            string adminAccessToken = AdminApiTestApplication
                .CreateAccessTokenWithTenantClaim(adminActorId, TenantId);
            using (HttpResponseMessage adminOperations = await SendAsync(
                       adminClient,
                       HttpMethod.Get,
                       $"/api/admin/reservations/properties/{PropertyId:D}/{operationsPathSuffix}",
                       adminAccessToken).ConfigureAwait(false))
            {
                ReservationOperationsSnapshotDto adminSnapshot =
                    await ReadSuccessAsync<ReservationOperationsSnapshotDto>(adminOperations)
                        .ConfigureAwait(false);
                AssertNoStore(adminOperations);
                Assert.Equal(PropertyId, adminSnapshot.PropertyId);
                Assert.Equal(1, adminSnapshot.Cohorts.ConfirmedArrivalsOnLocalDate.ReservationCount);
                Assert.Equal(1, adminSnapshot.Attention.AllocationRejected.ReservationCount);
            }

            using (HttpResponseMessage adminCrossProperty = await SendAsync(
                       adminClient,
                       HttpMethod.Get,
                       $"/api/admin/reservations/properties/{OtherPropertyId:D}/{operationsPathSuffix}",
                       adminAccessToken).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Forbidden, adminCrossProperty)
                    .ConfigureAwait(false);
                AssertNoStore(adminCrossProperty);
            }
            Assert.Equal(
                2,
                await adminApi.CountAuditEntriesAsync(
                    ReservationsAdminOperationNames.OperationsSnapshot)
                    .ConfigureAwait(false));
            Assert.Equal(
                1,
                await adminApi.CountAuditEntriesAsync(
                    ReservationsAdminOperationNames.OperationsSnapshot,
                    AdminErrors.Unauthorized.Code)
                    .ConfigureAwait(false));

            AdminCliResult operationsTable = await admin.ExecuteAsync(
                    "reservations", "operations-snapshot",
                    "--actor", adminActorId.ToString("D"),
                    "--tenant", TenantId,
                    "--property-id", PropertyId.ToString("D"),
                    "--local-date", "2026-10-01",
                    "--upcoming-limit", "1")
                .ConfigureAwait(false);
            Assert.Equal(AdminExitCodes.Success, operationsTable.ExitCode);
            Assert.Contains("ArrivalsOnLocalDate", operationsTable.Output, StringComparison.Ordinal);
            Assert.Contains("CurrentlyInHouse", operationsTable.Output, StringComparison.Ordinal);
            Assert.Contains("PendingAllocation", operationsTable.Output, StringComparison.Ordinal);
            Assert.Contains("AllocationRejected", operationsTable.Output, StringComparison.Ordinal);
            Assert.Contains("CancellationPending", operationsTable.Output, StringComparison.Ordinal);
            Assert.Contains("NoShowPending", operationsTable.Output, StringComparison.Ordinal);
            Assert.Contains("CheckoutPending", operationsTable.Output, StringComparison.Ordinal);
            Assert.Contains(
                "ArrivalBeforeLocalDateStillConfirmed",
                operationsTable.Output,
                StringComparison.Ordinal);
            Assert.Contains(
                "DepartureBeforeLocalDateStillInHouse",
                operationsTable.Output,
                StringComparison.Ordinal);
            Assert.Contains("Guests", operationsTable.Output, StringComparison.Ordinal);
            Assert.Contains("First Guest", operationsTable.Output, StringComparison.Ordinal);

            AdminCliResult operationsJson = await admin.ExecuteAsync(
                    "reservations", "operations-snapshot",
                    "--actor", adminActorId.ToString("D"),
                    "--tenant", TenantId,
                    "--property-id", PropertyId.ToString("D"),
                    "--local-date", "2026-10-01",
                    "--upcoming-limit", "0",
                    "--output", "json")
                .ConfigureAwait(false);
            Assert.Equal(AdminExitCodes.Success, operationsJson.ExitCode);
            Assert.True(
                operationsJson.Output.TrimStart().StartsWith('{'),
                $"Expected CLI JSON object output but received:{Environment.NewLine}{operationsJson.Output}");
            ReservationOperationsSnapshotDto? cliSnapshot = JsonSerializer.Deserialize<
                ReservationOperationsSnapshotDto>(
                operationsJson.Output,
                WebJsonOptions);
            Assert.NotNull(cliSnapshot);
            Assert.Equal(0, cliSnapshot.UpcomingLimit);
            Assert.Empty(cliSnapshot.Upcoming);
            Assert.True(cliSnapshot.HasMoreUpcoming);
            Assert.Equal(1, cliSnapshot.Attention.AllocationRejected.ReservationCount);

            AdminCliResult wrongProperty = await admin.ExecuteAsync(
                    "reservations", "operations-snapshot",
                    "--actor", adminActorId.ToString("D"),
                    "--tenant", TenantId,
                    "--property-id", OtherPropertyId.ToString("D"))
                .ConfigureAwait(false);
            Assert.Equal(AdminExitCodes.Unauthorized, wrongProperty.ExitCode);

            AdminCliResult invalidCliDate = await admin.ExecuteAsync(
                    "reservations", "operations-snapshot",
                    "--actor", adminActorId.ToString("D"),
                    "--tenant", TenantId,
                    "--property-id", PropertyId.ToString("D"),
                    "--local-date", "not-a-date")
                .ConfigureAwait(false);
            Assert.NotEqual(AdminExitCodes.Success, invalidCliDate.ExitCode);
            Assert.Contains(
                ReservationsApplicationErrors.OperationsSnapshotLocalDateInvalid.Message,
                invalidCliDate.Error,
                StringComparison.Ordinal);

            AdminCliResult invalidCliLimit = await admin.ExecuteAsync(
                    "reservations", "operations-snapshot",
                    "--actor", adminActorId.ToString("D"),
                    "--tenant", TenantId,
                    "--property-id", PropertyId.ToString("D"),
                    "--upcoming-limit", "51")
                .ConfigureAwait(false);
            Assert.NotEqual(AdminExitCodes.Success, invalidCliLimit.ExitCode);
            Assert.Contains(
                ReservationsApplicationErrors.OperationsSnapshotLimitInvalid.Message,
                invalidCliLimit.Error,
                StringComparison.Ordinal);
            Assert.True(
                await admin.CountAuditEntriesContainingAsync(
                    ReservationsAdminOperationNames.OperationsSnapshot)
                    .ConfigureAwait(false) >= 5);

            using (HttpResponseMessage crossScope = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/reservations/properties/{OtherPropertyId:D}/{first.ReservationId:D}",
                       tokens.AccessToken).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Forbidden, crossScope).ConfigureAwait(false);
            }

            ReservationMutationReceiptDto cancellationPending;
            using (HttpResponseMessage cancel = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/reservations/properties/{PropertyId:D}/{first.ReservationId:D}/cancel",
                       tokens.AccessToken,
                       new
                       {
                           operationId = Guid.NewGuid(),
                           expectedVersion = confirmed.Version
                       }).ConfigureAwait(false))
            {
                cancellationPending =
                    await ReadSuccessAsync<ReservationMutationReceiptDto>(cancel)
                        .ConfigureAwait(false);
            }

            Assert.Equal(ReservationStatus.CancellationPending, cancellationPending.Status);
            await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                first.ReservationId,
                ReservationStatus.Cancelled,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            ReservationMutationReceiptDto replacement = await CreateReservationAsync(
                client,
                tokens.AccessToken,
                new DateOnly(2026, 10, 1),
                new DateOnly(2026, 10, 3),
                "Replacement Guest").ConfigureAwait(false);
            ReservationDto replacementConfirmed = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                replacement.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            await initialWorker.StopAsync().ConfigureAwait(false);

            Guid stayOperationId = Guid.NewGuid();
            ReservationStayAmendmentReceiptDto pendingStay;
            using (HttpResponseMessage amendStay = await SendAsync(
                       client,
                       HttpMethod.Put,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/stay-amendments/{stayOperationId:D}",
                       tokens.AccessToken,
                       new
                       {
                           arrival = replacementConfirmed.Arrival,
                           departure = replacementConfirmed.Departure.AddDays(1),
                           expectedArrivalTime = new TimeOnly(16, 0),
                           expectedDepartureTime = new TimeOnly(9, 0),
                           inventoryUnitIds = new[] { ReplacementRoomId },
                           expectedDetailsRevision = replacementConfirmed.DetailsRevision
                       }).ConfigureAwait(false))
            {
                Assert.True(amendStay.Headers.CacheControl?.NoStore);
                pendingStay = await ReadSuccessAsync<ReservationStayAmendmentReceiptDto>(amendStay)
                    .ConfigureAwait(false);
            }
            Assert.Equal(ReservationStayAmendmentOutcome.Pending, pendingStay.Outcome);
            Assert.Equal(replacementConfirmed.Departure.AddDays(1), pendingStay.Target!.Departure);
            ReservationDto pendingAuthoritativeStay = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                replacement.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(replacementConfirmed.Arrival, pendingAuthoritativeStay.Arrival);
            Assert.Equal(replacementConfirmed.Departure, pendingAuthoritativeStay.Departure);
            Assert.Equal(replacementConfirmed.ExpectedArrivalTime, pendingAuthoritativeStay.ExpectedArrivalTime);
            Assert.Equal(replacementConfirmed.ExpectedDepartureTime, pendingAuthoritativeStay.ExpectedDepartureTime);
            Assert.Equal(replacementConfirmed.AllocationId, pendingAuthoritativeStay.AllocationId);
            Assert.Equal(replacementConfirmed.AllocationVersion, pendingAuthoritativeStay.AllocationVersion);
            Assert.Equal(replacementConfirmed.DetailsRevision, pendingAuthoritativeStay.DetailsRevision);
            Assert.Equal([RoomId], pendingAuthoritativeStay.InventoryUnitIds);

            stayWorker = CreateWorker(connectionString, natsConnectionString);
            await stayWorker.StartAsync().ConfigureAwait(false);

            ReservationStayAmendmentReceiptDto appliedStay = await WaitForStayAmendmentAsync(
                client,
                tokens.AccessToken,
                replacement.ReservationId,
                stayOperationId,
                ReservationStayAmendmentOutcome.Applied,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Guid inventoryRequestId = await GetStayInventoryRequestIdAsync(
                api,
                replacement.ReservationId,
                stayOperationId).ConfigureAwait(false);
            Assert.NotEqual(stayOperationId, inventoryRequestId);
            Assert.Equal(1, await CountInventoryAmendmentDecisionsAsync(
                api,
                inventoryRequestId).ConfigureAwait(false));

            using (HttpResponseMessage exactStayReplay = await SendAsync(
                       client,
                       HttpMethod.Put,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/stay-amendments/{stayOperationId:D}",
                       tokens.AccessToken,
                       new
                       {
                           arrival = replacementConfirmed.Arrival,
                           departure = replacementConfirmed.Departure.AddDays(1),
                           expectedArrivalTime = new TimeOnly(16, 0),
                           expectedDepartureTime = new TimeOnly(9, 0),
                           inventoryUnitIds = new[] { ReplacementRoomId },
                           expectedDetailsRevision = replacementConfirmed.DetailsRevision
                       }).ConfigureAwait(false))
            {
                ReservationStayAmendmentReceiptDto replayed =
                    await ReadSuccessAsync<ReservationStayAmendmentReceiptDto>(exactStayReplay)
                        .ConfigureAwait(false);
                Assert.Equal(appliedStay.OperationVersion, replayed.OperationVersion);
                Assert.Equal(appliedStay.Outcome, replayed.Outcome);
            }
            Assert.Equal(1, await CountInventoryAmendmentDecisionsAsync(
                api,
                inventoryRequestId).ConfigureAwait(false));
            replacementConfirmed = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                replacement.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(appliedStay.Target!.Departure, replacementConfirmed.Departure);
            Assert.Equal(appliedStay.Target.ExpectedArrivalTime, replacementConfirmed.ExpectedArrivalTime);
            Assert.Equal(appliedStay.Target.ExpectedDepartureTime, replacementConfirmed.ExpectedDepartureTime);
            Assert.Equal([ReplacementRoomId], replacementConfirmed.InventoryUnitIds);

            string stayStatusPath =
                $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/stay-amendments/{stayOperationId:D}";
            using (HttpResponseMessage anonymousStayStatus = await SendAsync(
                       client,
                       HttpMethod.Get,
                       stayStatusPath).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Unauthorized, anonymousStayStatus)
                    .ConfigureAwait(false);
            }

            using (HttpResponseMessage readerStayStatus = await SendAsync(
                       client,
                       HttpMethod.Get,
                       stayStatusPath,
                       readerTokens.AccessToken).ConfigureAwait(false))
            {
                Assert.True(readerStayStatus.Headers.CacheControl?.NoStore);
                ReservationStayAmendmentReceiptDto readReceipt =
                    await ReadSuccessAsync<ReservationStayAmendmentReceiptDto>(readerStayStatus)
                        .ConfigureAwait(false);
                Assert.Equal(stayOperationId, readReceipt.OperationId);
            }

            object exactStayRequest = new
            {
                arrival = appliedStay.Target!.Arrival,
                departure = appliedStay.Target.Departure,
                expectedArrivalTime = appliedStay.Target.ExpectedArrivalTime,
                expectedDepartureTime = appliedStay.Target.ExpectedDepartureTime,
                inventoryUnitIds = appliedStay.Target.InventoryUnitIds,
                expectedDetailsRevision = appliedStay.ExpectedDetailsRevision
            };
            using (HttpResponseMessage readerAmendDenied = await SendAsync(
                       client,
                       HttpMethod.Put,
                       stayStatusPath,
                       readerTokens.AccessToken,
                       exactStayRequest).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Forbidden, readerAmendDenied)
                    .ConfigureAwait(false);
            }

            using (HttpResponseMessage crossPropertyStayStatus = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/reservations/properties/{OtherPropertyId:D}/{replacement.ReservationId:D}/stay-amendments/{stayOperationId:D}",
                       tokens.AccessToken).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Forbidden, crossPropertyStayStatus)
                    .ConfigureAwait(false);
            }

            int inventoryDecisionCountBeforeAdministrativeNoOps =
                await CountAllInventoryAmendmentDecisionsAsync(api).ConfigureAwait(false);
            Guid cliOperationId = Guid.NewGuid();
            int cliConfirmationAuditBefore = await admin
                .CountAuditEntriesContainingAsync(AdminErrors.ConfirmationRequired.Code)
                .ConfigureAwait(false);
            AdminCliResult unconfirmedCliAmend = await admin.ExecuteAsync(
                "reservations", "stay-amendments", "amend",
                "--actor", operatorId.ToString("D"),
                "--tenant", TenantId,
                "--property-id", PropertyId.ToString("D"),
                "--reservation-id", replacement.ReservationId.ToString("D"),
                "--operation-id", cliOperationId.ToString("D"),
                "--arrival", replacementConfirmed.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "--departure", replacementConfirmed.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "--expected-arrival-time", replacementConfirmed.ExpectedArrivalTime!.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
                "--expected-departure-time", replacementConfirmed.ExpectedDepartureTime!.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
                "--unit-ids", ReplacementRoomId.ToString("D"),
                "--expected-details-revision", replacementConfirmed.DetailsRevision.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            Assert.Equal(AdminExitCodes.Failed, unconfirmedCliAmend.ExitCode);
            Assert.Contains(
                AdminErrors.ConfirmationRequired.Message,
                unconfirmedCliAmend.Error,
                StringComparison.Ordinal);
            Assert.Equal(
                cliConfirmationAuditBefore + 1,
                await admin.CountAuditEntriesContainingAsync(AdminErrors.ConfirmationRequired.Code)
                    .ConfigureAwait(false));

            AdminCliResult confirmedCliAmend = await admin.ExecuteAsync(
                "reservations", "stay-amendments", "amend",
                "--actor", operatorId.ToString("D"),
                "--tenant", TenantId,
                "--property-id", PropertyId.ToString("D"),
                "--reservation-id", replacement.ReservationId.ToString("D"),
                "--operation-id", cliOperationId.ToString("D"),
                "--arrival", replacementConfirmed.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "--departure", replacementConfirmed.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "--expected-arrival-time", replacementConfirmed.ExpectedArrivalTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
                "--expected-departure-time", replacementConfirmed.ExpectedDepartureTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
                "--unit-ids", ReplacementRoomId.ToString("D"),
                "--expected-details-revision", replacementConfirmed.DetailsRevision.ToString(CultureInfo.InvariantCulture),
                "--yes").ConfigureAwait(false);
            Assert.Equal(AdminExitCodes.Success, confirmedCliAmend.ExitCode);
            Assert.Contains(cliOperationId.ToString("D"), confirmedCliAmend.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Applied", confirmedCliAmend.Output, StringComparison.Ordinal);
            Assert.Equal(
                operatorId.ToString("D"),
                await GetStayRequestedByAsync(api, replacement.ReservationId, cliOperationId)
                    .ConfigureAwait(false));

            Guid adminOperationId = Guid.NewGuid();
            string adminStayPath =
                $"/api/admin/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/stay-amendments/{adminOperationId:D}";
            int adminAmendAuditBefore = await reservationsAdminApi
                .CountAuditEntriesAsync(ReservationsAdminOperationNames.AmendStay)
                .ConfigureAwait(false);
            int adminConfirmationAuditBefore = await reservationsAdminApi
                .CountAuditEntriesAsync(
                    ReservationsAdminOperationNames.AmendStay,
                    AdminErrors.ConfirmationRequired.Code)
                .ConfigureAwait(false);
            using (HttpResponseMessage unconfirmedAdminReplay = await reservationsAdminClient.PutAsJsonAsync(
                       adminStayPath,
                       new
                       {
                           replacementConfirmed.Arrival,
                           replacementConfirmed.Departure,
                           replacementConfirmed.ExpectedArrivalTime,
                           replacementConfirmed.ExpectedDepartureTime,
                           replacementConfirmed.InventoryUnitIds,
                           expectedDetailsRevision = replacementConfirmed.DetailsRevision,
                           confirmed = false
                       }).ConfigureAwait(false))
            {
                Assert.True(unconfirmedAdminReplay.Headers.CacheControl?.NoStore);
                await AssertStatusAsync(HttpStatusCode.BadRequest, unconfirmedAdminReplay)
                    .ConfigureAwait(false);
            }
            Assert.Equal(
                adminConfirmationAuditBefore + 1,
                await reservationsAdminApi.CountAuditEntriesAsync(
                    ReservationsAdminOperationNames.AmendStay,
                    AdminErrors.ConfirmationRequired.Code).ConfigureAwait(false));

            ReservationStayAmendmentReceiptDto adminApplied;
            using (HttpResponseMessage confirmedAdminAmend = await reservationsAdminClient.PutAsJsonAsync(
                       adminStayPath,
                       new
                       {
                           replacementConfirmed.Arrival,
                           replacementConfirmed.Departure,
                           replacementConfirmed.ExpectedArrivalTime,
                           replacementConfirmed.ExpectedDepartureTime,
                           replacementConfirmed.InventoryUnitIds,
                           expectedDetailsRevision = replacementConfirmed.DetailsRevision,
                           confirmed = true
                       }).ConfigureAwait(false))
            {
                Assert.True(confirmedAdminAmend.Headers.CacheControl?.NoStore);
                adminApplied =
                    await ReadSuccessAsync<ReservationStayAmendmentReceiptDto>(confirmedAdminAmend)
                        .ConfigureAwait(false);
                Assert.Equal(adminOperationId, adminApplied.OperationId);
                Assert.Equal(ReservationStayAmendmentOutcome.Applied, adminApplied.Outcome);
            }
            Assert.Equal(
                adminAmendAuditBefore + 2,
                await reservationsAdminApi.CountAuditEntriesAsync(
                    ReservationsAdminOperationNames.AmendStay).ConfigureAwait(false));
            Assert.Equal(
                $"admin-api:{operatorId:D}",
                await GetStayRequestedByAsync(api, replacement.ReservationId, adminOperationId)
                    .ConfigureAwait(false));
            Assert.Equal(
                inventoryDecisionCountBeforeAdministrativeNoOps,
                await CountAllInventoryAmendmentDecisionsAsync(api).ConfigureAwait(false));

            using (HttpResponseMessage adminStatus = await reservationsAdminClient
                       .GetAsync(adminStayPath)
                       .ConfigureAwait(false))
            {
                Assert.True(adminStatus.Headers.CacheControl?.NoStore);
                ReservationStayAmendmentReceiptDto adminReceipt =
                    await ReadSuccessAsync<ReservationStayAmendmentReceiptDto>(adminStatus)
                        .ConfigureAwait(false);
                Assert.Equal(adminOperationId, adminReceipt.OperationId);
            }

            int reconcileConfirmationAuditBefore = await reservationsAdminApi
                .CountAuditEntriesAsync(
                    ReservationsAdminOperationNames.ReconcileStayAmendment,
                    AdminErrors.ConfirmationRequired.Code)
                .ConfigureAwait(false);
            using (HttpResponseMessage unconfirmedReconcile = await reservationsAdminClient.PostAsJsonAsync(
                       adminStayPath + "/reconcile",
                       new
                       {
                           expectedOperationVersion = adminApplied.OperationVersion,
                           confirmed = false
                       }).ConfigureAwait(false))
            {
                Assert.True(unconfirmedReconcile.Headers.CacheControl?.NoStore);
                await AssertStatusAsync(HttpStatusCode.BadRequest, unconfirmedReconcile)
                    .ConfigureAwait(false);
            }
            Assert.Equal(
                reconcileConfirmationAuditBefore + 1,
                await reservationsAdminApi.CountAuditEntriesAsync(
                    ReservationsAdminOperationNames.ReconcileStayAmendment,
                    AdminErrors.ConfirmationRequired.Code).ConfigureAwait(false));

            ReservationMutationReceiptDto conflictHolder = await CreateReservationAsync(
                client,
                tokens.AccessToken,
                replacementConfirmed.Arrival,
                replacementConfirmed.Departure,
                "Stay Amendment Conflict Holder").ConfigureAwait(false);
            ReservationDto conflictConfirmed = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                conflictHolder.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Guid rejectedOperationId = Guid.NewGuid();
            using (HttpResponseMessage rejectedRequest = await SendAsync(
                       client,
                       HttpMethod.Put,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/stay-amendments/{rejectedOperationId:D}",
                       tokens.AccessToken,
                       new
                       {
                           arrival = replacementConfirmed.Arrival,
                           departure = replacementConfirmed.Departure,
                           expectedArrivalTime = replacementConfirmed.ExpectedArrivalTime,
                           expectedDepartureTime = replacementConfirmed.ExpectedDepartureTime,
                           inventoryUnitIds = new[] { RoomId },
                           expectedDetailsRevision = replacementConfirmed.DetailsRevision
                       }).ConfigureAwait(false))
            {
                ReservationStayAmendmentReceiptDto pendingRejection =
                    await ReadSuccessAsync<ReservationStayAmendmentReceiptDto>(rejectedRequest)
                        .ConfigureAwait(false);
                Assert.Equal(ReservationStayAmendmentOutcome.Pending, pendingRejection.Outcome);
            }

            ReservationStayAmendmentReceiptDto rejectedStay = await WaitForStayAmendmentAsync(
                client,
                tokens.AccessToken,
                replacement.ReservationId,
                rejectedOperationId,
                ReservationStayAmendmentOutcome.Rejected,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.NotNull(rejectedStay.RejectionReason);
            ReservationDto afterRejectedStay = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                replacement.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(replacementConfirmed.Arrival, afterRejectedStay.Arrival);
            Assert.Equal(replacementConfirmed.Departure, afterRejectedStay.Departure);
            Assert.Equal(replacementConfirmed.ExpectedArrivalTime, afterRejectedStay.ExpectedArrivalTime);
            Assert.Equal(replacementConfirmed.ExpectedDepartureTime, afterRejectedStay.ExpectedDepartureTime);
            Assert.Equal(replacementConfirmed.AllocationId, afterRejectedStay.AllocationId);
            Assert.Equal(replacementConfirmed.AllocationVersion, afterRejectedStay.AllocationVersion);
            Assert.Equal(replacementConfirmed.DetailsRevision, afterRejectedStay.DetailsRevision);
            Assert.Equal([ReplacementRoomId], afterRejectedStay.InventoryUnitIds);
            replacementConfirmed = afterRejectedStay;

            using (HttpResponseMessage cancelConflict = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/reservations/properties/{PropertyId:D}/{conflictConfirmed.ReservationId:D}/cancel",
                       tokens.AccessToken,
                       new
                       {
                           operationId = Guid.NewGuid(),
                           expectedVersion = conflictConfirmed.Version
                       }).ConfigureAwait(false))
            {
                await ReadSuccessAsync<ReservationMutationReceiptDto>(cancelConflict)
                    .ConfigureAwait(false);
            }
            await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                conflictConfirmed.ReservationId,
                ReservationStatus.Cancelled,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            await PublishGuestRestrictionTransitionAsync(
                api,
                canonicalGuest.GuestId,
                expectedRevision: 0,
                isRestricted: true).ConfigureAwait(false);
            await WaitForGuestRestrictionProjectionAsync(
                api,
                canonicalGuest.GuestId,
                expectedRevision: 1,
                isRestricted: true,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            using (HttpResponseMessage restrictedLink = await SendAsync(
                       client,
                       HttpMethod.Put,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/guests",
                       tokens.AccessToken,
                       new
                       {
                           guestId = canonicalGuest.GuestId,
                           role = ReservationGuestRoleKind.Primary,
                           replaceExistingRole = false,
                           expectedVersion = replacementConfirmed.Version
                       }).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Conflict, restrictedLink).ConfigureAwait(false);
            }

            await PublishGuestRestrictionTransitionAsync(
                api,
                canonicalGuest.GuestId,
                expectedRevision: 1,
                isRestricted: false).ConfigureAwait(false);
            await WaitForGuestRestrictionProjectionAsync(
                api,
                canonicalGuest.GuestId,
                expectedRevision: 2,
                isRestricted: false,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            ReservationMutationReceiptDto linkedReceipt;
            using (HttpResponseMessage link = await SendAsync(
                       client,
                       HttpMethod.Put,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/guests",
                       tokens.AccessToken,
                       new
                       {
                           guestId = canonicalGuest.GuestId,
                           role = ReservationGuestRoleKind.Primary,
                           replaceExistingRole = false,
                           expectedVersion = replacementConfirmed.Version
                       }).ConfigureAwait(false))
            {
                linkedReceipt = await ReadSuccessAsync<ReservationMutationReceiptDto>(link)
                    .ConfigureAwait(false);
            }

            ReservationDto linked = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                replacement.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(linkedReceipt.Version, linked.Version);
            Assert.Equal(canonicalGuest.GuestId, Assert.Single(linked.Guests).GuestId);

            using (HttpResponseMessage idempotentRetry = await SendAsync(
                       client,
                       HttpMethod.Put,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/guests",
                       tokens.AccessToken,
                       new
                       {
                           guestId = canonicalGuest.GuestId,
                           role = ReservationGuestRoleKind.Primary,
                           replaceExistingRole = false,
                           expectedVersion = replacementConfirmed.Version
                       }).ConfigureAwait(false))
            {
                ReservationMutationReceiptDto retried =
                    await ReadSuccessAsync<ReservationMutationReceiptDto>(idempotentRetry)
                        .ConfigureAwait(false);
                Assert.Equal(linkedReceipt.Version, retried.Version);
            }

            using (HttpResponseMessage deniedCheckIn = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/reservations/properties/{OtherPropertyId:D}/{replacement.ReservationId:D}/check-in",
                       tokens.AccessToken,
                       new
                       {
                           operationId = Guid.NewGuid(),
                           businessDate = new DateOnly(2026, 10, 1),
                           expectedVersion = linked.Version
                       })
                       .ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Forbidden, deniedCheckIn).ConfigureAwait(false);
            }

            Guid checkInOperationId = Guid.NewGuid();
            ReservationMutationReceiptDto checkedInReceipt;
            using (HttpResponseMessage checkIn = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/check-in",
                       tokens.AccessToken,
                       new
                       {
                           operationId = checkInOperationId,
                           businessDate = new DateOnly(2026, 10, 1),
                           expectedVersion = linked.Version
                       })
                       .ConfigureAwait(false))
            {
                checkedInReceipt =
                    await ReadSuccessAsync<ReservationMutationReceiptDto>(checkIn)
                        .ConfigureAwait(false);
            }

            ReservationDto checkedIn = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                replacement.ReservationId,
                ReservationStatus.CheckedIn,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(checkedInReceipt.Version, checkedIn.Version);
            Assert.Equal(ReservationStatus.CheckedIn, checkedIn.Status);
            Assert.Equal(new DateOnly(2026, 10, 1), checkedIn.CheckedInBusinessDate);
            Assert.StartsWith("user:", checkedIn.CheckedInBy, StringComparison.Ordinal);

            using (HttpResponseMessage checkInReplay = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/check-in",
                       tokens.AccessToken,
                       new
                       {
                           operationId = checkInOperationId,
                           businessDate = new DateOnly(2026, 10, 1),
                           expectedVersion = linked.Version
                       }).ConfigureAwait(false))
            {
                ReservationMutationReceiptDto replayed =
                    await ReadSuccessAsync<ReservationMutationReceiptDto>(checkInReplay)
                        .ConfigureAwait(false);
                Assert.Equal(ReservationStatus.CheckedIn, replayed.Status);
                Assert.Equal(checkedIn.Version, replayed.Version);
                Assert.Equal(checkedIn.DetailsRevision, replayed.DetailsRevision);
            }
            Assert.Equal(
                5,
                await CountManagementOperationsAsync(api, replacement.ReservationId)
                    .ConfigureAwait(false));

            using (HttpResponseMessage staleCheckIn = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/check-in",
                       tokens.AccessToken,
                       new
                       {
                           operationId = Guid.NewGuid(),
                           businessDate = new DateOnly(2026, 10, 1),
                           expectedVersion = replacementConfirmed.Version
                       })
                       .ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Conflict, staleCheckIn).ConfigureAwait(false);
            }

            Guid checkOutOperationId = Guid.NewGuid();
            ReservationMutationReceiptDto checkoutPending;
            using (HttpResponseMessage checkOut = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/check-out",
                       tokens.AccessToken,
                       new
                       {
                           operationId = checkOutOperationId,
                           businessDate = new DateOnly(2026, 10, 3),
                           expectedVersion = checkedIn.Version
                       })
                       .ConfigureAwait(false))
            {
                checkoutPending =
                    await ReadSuccessAsync<ReservationMutationReceiptDto>(checkOut)
                        .ConfigureAwait(false);
            }

            Assert.Equal(ReservationStatus.CheckoutPending, checkoutPending.Status);
            ReservationDto checkedOut = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                replacement.ReservationId,
                ReservationStatus.CheckedOut,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(new DateOnly(2026, 10, 3), checkedOut.CheckedOutBusinessDate);
            Assert.Equal(checkedIn.CheckedInBy, checkedOut.CheckedOutBy);

            using (HttpResponseMessage checkOutReplay = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/reservations/properties/{PropertyId:D}/{replacement.ReservationId:D}/check-out",
                       tokens.AccessToken,
                       new
                       {
                           operationId = checkOutOperationId,
                           businessDate = new DateOnly(2026, 10, 3),
                           expectedVersion = checkedIn.Version
                       }).ConfigureAwait(false))
            {
                ReservationMutationReceiptDto replayed =
                    await ReadSuccessAsync<ReservationMutationReceiptDto>(checkOutReplay)
                        .ConfigureAwait(false);
                Assert.Equal(ReservationStatus.CheckedOut, replayed.Status);
                Assert.Equal(checkedOut.Version, replayed.Version);
                Assert.Equal(checkedOut.DetailsRevision, replayed.DetailsRevision);
            }
            Assert.Equal(
                6,
                await CountManagementOperationsAsync(api, replacement.ReservationId)
                    .ConfigureAwait(false));

            GuestStayHistoryItem stay = await WaitForGuestStayAsync(
                client,
                tokens.AccessToken,
                canonicalGuest.GuestId,
                replacement.ReservationId,
                GuestStayStatus.CheckedOut,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(checkedOut.Version, stay.ReservationVersion);
            Assert.Equal(checkedIn.CheckedInBusinessDate, stay.CheckedInBusinessDate);
            Assert.Equal(checkedOut.CheckedOutBusinessDate, stay.CheckedOutBusinessDate);

            await ApplyStaleGuestStayEventAsync(api, canonicalGuest.GuestId, linked, linked.Version)
                .ConfigureAwait(false);
            GuestStayHistoryItem afterStale = await GetGuestStayAsync(
                client,
                tokens.AccessToken,
                canonicalGuest.GuestId,
                replacement.ReservationId).ConfigureAwait(false);
            Assert.Equal(GuestStayStatus.CheckedOut, afterStale.Status);
            Assert.Equal(checkedOut.Version, afterStale.ReservationVersion);

            using (HttpResponseMessage crossPropertyGuest = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/guests/properties/{OtherPropertyId:D}/{canonicalGuest.GuestId:D}",
                       tokens.AccessToken).ConfigureAwait(false))
            {
                await AssertStatusAsync(HttpStatusCode.Forbidden, crossPropertyGuest).ConfigureAwait(false);
            }

            ReservationMutationReceiptDto noShowCandidate = await CreateReservationAsync(
                client,
                tokens.AccessToken,
                new DateOnly(2026, 10, 1),
                new DateOnly(2026, 10, 3),
                "No Show Guest").ConfigureAwait(false);
            ReservationDto noShowConfirmed = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                noShowCandidate.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            using (HttpResponseMessage noShow = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/reservations/properties/{PropertyId:D}/{noShowCandidate.ReservationId:D}/no-show",
                       tokens.AccessToken,
                       new
                       {
                           operationId = Guid.NewGuid(),
                           businessDate = new DateOnly(2026, 10, 1),
                           expectedVersion = noShowConfirmed.Version
                       })
                       .ConfigureAwait(false))
            {
                ReservationMutationReceiptDto pending =
                    await ReadSuccessAsync<ReservationMutationReceiptDto>(noShow)
                        .ConfigureAwait(false);
                Assert.Equal(ReservationStatus.NoShowPending, pending.Status);
            }

            ReservationDto noShowTerminal = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                noShowCandidate.ReservationId,
                ReservationStatus.NoShow,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(new DateOnly(2026, 10, 1), noShowTerminal.NoShowBusinessDate);
            Assert.StartsWith("user:", noShowTerminal.NoShowBy, StringComparison.Ordinal);

            ReservationMutationReceiptDto finalReplacement = await CreateReservationAsync(
                client,
                tokens.AccessToken,
                new DateOnly(2026, 10, 1),
                new DateOnly(2026, 10, 3),
                "Final Replacement Guest").ConfigureAwait(false);
            await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                finalReplacement.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        finally
        {
            if (stayWorker is not null)
            {
                await stayWorker.StopAsync().ConfigureAwait(false);
                stayWorker.Dispose();
            }

            await initialWorker.StopAsync().ConfigureAwait(false);
        }
    }

    private static IHost CreateWorker(string connectionString, string natsConnectionString)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.Environment.EnvironmentName = "Integration";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationIdentity:DisplayName"] = "BunkFy Reservation Saga Worker",
            ["ApplicationIdentity:Namespace"] = "bunkfy",
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:PostgreSql"] = connectionString,
            ["ConnectionStrings:nats"] = natsConnectionString,
            ["Tenancy:Enabled"] = "true",
            ["Caching:Enabled"] = "false",
            ["NatsJetStream:Enabled"] = "true",
            ["NatsConsumers:Enabled"] = "true",
            ["NatsConsumers:FetchBatchSize"] = "10",
            ["NatsConsumers:PollInterval"] = "00:00:00.100",
            ["NatsConsumers:AckWait"] = "00:00:05",
            ["NatsConsumers:AckProgressInterval"] = "00:00:01",
            ["NatsConsumers:HandlerTimeout"] = "00:00:10",
            ["NatsConsumers:NakDelay"] = "00:00:00.100",
            ["Outbox:PollIntervalMilliseconds"] = "100",
            ["Outbox:LockDurationMilliseconds"] = "5000",
            ["Worker:Modules:Properties"] = "true",
            ["Worker:Modules:Inventory"] = "true",
            ["Worker:Modules:Reservations"] = "true",
            ["Worker:Modules:Guests"] = "true",
            ["Tasks:Worker:Enabled"] = "false"
        });
        builder.AddWorkerHost();
        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
        builder.ValidateModuleComposition();
        return builder.Build();
    }

    private static async Task<int> CountManagementOperationsAsync(
        AuthTestApplication api,
        Guid reservationId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        return await reservations.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM reservations.management_operations
                WHERE "ScopeId" = {TenantId}
                  AND "ReservationId" = {reservationId}
                """)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private static async Task<Guid> GetStayInventoryRequestIdAsync(
        AuthTestApplication api,
        Guid reservationId,
        Guid operationId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        return await reservations.Database.SqlQuery<Guid>($"""
                SELECT "InventoryRequestId" AS "Value"
                FROM reservations.stay_amendment_operations
                WHERE "ScopeId" = {TenantId}
                  AND "ReservationId" = {reservationId}
                  AND "Id" = {operationId}
                """)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private static async Task<string> GetStayRequestedByAsync(
        AuthTestApplication api,
        Guid reservationId,
        Guid operationId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        return await reservations.Database.SqlQuery<string>($"""
                SELECT "RequestedBy" AS "Value"
                FROM reservations.stay_amendment_operations
                WHERE "ScopeId" = {TenantId}
                  AND "ReservationId" = {reservationId}
                  AND "Id" = {operationId}
                """)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private static async Task<int> CountInventoryAmendmentDecisionsAsync(
        AuthTestApplication api,
        Guid inventoryRequestId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        InventoryDbContext inventory = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        return await inventory.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM inventory.allocation_amendment_decisions
                WHERE "Id" = {inventoryRequestId}
                """)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private static async Task<int> CountAllInventoryAmendmentDecisionsAsync(
        AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        InventoryDbContext inventory = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        return await inventory.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM inventory.allocation_amendment_decisions
                WHERE "ScopeId" = {TenantId}
                """)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private static async Task SeedInventoryAsync(AuthTestApplication api)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (IServiceScope scope = api.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            InventoryDbContext inventory = scope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            await using var transaction = await inventory.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            IIntegrationEventHandler<PropertyCreatedIntegrationEvent> propertyHandler =
                ResolveInventoryHandler<PropertyCreatedIntegrationEvent>(scope.ServiceProvider);
            IIntegrationEventHandler<RoomCreatedIntegrationEvent> roomHandler =
                ResolveInventoryHandler<RoomCreatedIntegrationEvent>(scope.ServiceProvider);
            await propertyHandler.HandleAsync(
                new(Guid.NewGuid(), TenantId, now, PropertyId, "Saga House", "saga", "UTC", PropertyStatus.Active, 1),
                CancellationToken.None).ConfigureAwait(false);
            await roomHandler.HandleAsync(
                new(Guid.NewGuid(), TenantId, now, PropertyId, RoomId, "101", null, null, RoomStatus.Active, 1),
                CancellationToken.None).ConfigureAwait(false);
            await roomHandler.HandleAsync(
                new(Guid.NewGuid(), TenantId, now, PropertyId, ReplacementRoomId, "102", null, null, RoomStatus.Active, 1),
                CancellationToken.None).ConfigureAwait(false);
            await inventory
                .SaveChangesAsync()
                .ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        using IServiceScope configurationScope = api.Services.CreateScope();
        configurationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        Result<RoomInventoryMutationReceiptDto> configured = await configurationScope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(
                new ConfigureRoomSalesModeCommand(
                    Guid.NewGuid(),
                    PropertyId,
                    RoomId,
                    InventorySalesMode.RoomLevel,
                    1),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(configured.IsSuccess, configured.Error.Code);
        Result<RoomInventoryMutationReceiptDto> replacementConfigured =
            await configurationScope.ServiceProvider
                .GetRequiredService<IRequestDispatcher>()
                .SendAsync(
                    new ConfigureRoomSalesModeCommand(
                        Guid.NewGuid(),
                        PropertyId,
                        ReplacementRoomId,
                        InventorySalesMode.RoomLevel,
                        1),
                    CancellationToken.None)
                .ConfigureAwait(false);
        Assert.True(replacementConfigured.IsSuccess, replacementConfigured.Error.Code);
    }

    private static IIntegrationEventHandler<TEvent> ResolveInventoryHandler<TEvent>(IServiceProvider services)
        where TEvent : IIntegrationEvent
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item => item.ConsumerModule == InventoryModuleMetadata.Name && item.EventType == typeof(TEvent));
        return (IIntegrationEventHandler<TEvent>)services.GetRequiredService(subscription.HandlerType);
    }

    private static async Task SeedGovernedPropertyProjectionsAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        PropertyCreatedIntegrationEvent propertyCreated = new(
            Guid.NewGuid(),
            TenantId,
            DateTimeOffset.UtcNow,
            PropertyId,
            "Saga House",
            "saga",
            "UTC",
            PropertyStatus.Active,
            1);
        GuestsDbContext guests = scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        await using (var guestsTransaction = await guests.Database.BeginTransactionAsync()
                         .ConfigureAwait(false))
        {
            await ResolveHandler<PropertyCreatedIntegrationEvent>(
                    scope.ServiceProvider,
                    GuestsModuleMetadata.Name)
                .HandleAsync(propertyCreated, CancellationToken.None).ConfigureAwait(false);
            await CountryPolicyIntegrationTestData.ApplyActivationAsync(
                scope.ServiceProvider,
                GuestsModuleMetadata.Name,
                TenantId,
                PropertyId,
                2).ConfigureAwait(false);
            await guests.SaveChangesAsync().ConfigureAwait(false);
            await guestsTransaction.CommitAsync().ConfigureAwait(false);
        }

        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        await using (var reservationsTransaction = await reservations.Database
                         .BeginTransactionAsync()
                         .ConfigureAwait(false))
        {
            await ResolveHandler<PropertyCreatedIntegrationEvent>(
                    scope.ServiceProvider,
                    ReservationsModuleMetadata.Name)
                .HandleAsync(propertyCreated, CancellationToken.None).ConfigureAwait(false);
            await CountryPolicyIntegrationTestData.ApplyActivationAsync(
                scope.ServiceProvider,
                ReservationsModuleMetadata.Name,
                TenantId,
                PropertyId,
                2).ConfigureAwait(false);
            await reservations.SaveChangesAsync().ConfigureAwait(false);
            await reservationsTransaction.CommitAsync().ConfigureAwait(false);
        }
    }

    private static IIntegrationEventHandler<TEvent> ResolveHandler<TEvent>(
        IServiceProvider services,
        string consumerModule)
        where TEvent : IIntegrationEvent
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item => item.ConsumerModule == consumerModule && item.EventType == typeof(TEvent));
        return (IIntegrationEventHandler<TEvent>)services.GetRequiredService(subscription.HandlerType);
    }

    private static async Task<GuestMutationReceiptDto> CreateGuestAsync(
        HttpClient client,
        string accessToken,
        string displayName)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Post,
            $"/api/guests/properties/{PropertyId:D}",
            accessToken,
            new
            {
                operationId = Guid.NewGuid(),
                displayName,
                legalName = (string?)null,
                email = "shared@example.test",
                phone = "+100000000",
                dateOfBirth = (DateOnly?)null,
                nationalityCountryCode = (string?)null,
                preferredLanguageTag = "en",
                notes = (string?)null
            }).ConfigureAwait(false);
        return await ReadSuccessAsync<GuestMutationReceiptDto>(response).ConfigureAwait(false);
    }

    private static async Task WaitForGuestEligibilityProjectionAsync(
        AuthTestApplication api,
        Guid guestId,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            ReservationsDbContext reservations =
                scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            bool profileReady = await reservations.GuestProfileProjections
                .AsNoTracking()
                .AnyAsync(profile => profile.Id == guestId && profile.Status == GuestStatus.Active)
                .ConfigureAwait(false);
            bool restrictionReady = await reservations.GuestProcessingRestrictionProjections
                .AsNoTracking()
                .AnyAsync(projection =>
                    projection.PropertyId == PropertyId &&
                    projection.GuestId == guestId &&
                    projection.ContractVersion == GuestProcessingRestrictionContract.CurrentVersion &&
                    projection.Revision == 0 &&
                    !projection.IsRestricted)
                .ConfigureAwait(false);
            if (profileReady && restrictionReady)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException("Reservations did not receive the Guest eligibility projection.");
    }

    private static async Task PublishGuestRestrictionTransitionAsync(
        AuthTestApplication api,
        Guid guestId,
        long expectedRevision,
        bool isRestricted)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        GuestsDbContext guests = scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        GuestProcessingRestrictionProjection projection = await guests
            .ProcessingRestrictionProjections
            .SingleAsync(item => item.PropertyId == PropertyId && item.GuestId == guestId)
            .ConfigureAwait(false);
        DateTimeOffset occurredAtUtc = DateTimeOffset.UtcNow;
        Result transition = isRestricted
            ? projection.Apply(
                expectedRevision,
                GuestProcessingRestrictionContract.CurrentVersion,
                occurredAtUtc)
            : projection.Release(
                expectedRevision,
                GuestProcessingRestrictionContract.CurrentVersion,
                occurredAtUtc);
        Assert.True(transition.IsSuccess, transition.Error.Code);

        await scope.ServiceProvider.GetRequiredService<IOutboxWriterRegistry>()
            .GetRequired(GuestsModuleMetadata.Name)
            .EnqueueAsync(
                new GuestProcessingRestrictionChangedIntegrationEvent(
                    Guid.NewGuid(),
                    TenantId,
                    occurredAtUtc,
                    PropertyId,
                    guestId,
                    GuestProcessingRestrictionContract.CurrentVersion,
                    projection.Revision,
                    projection.IsRestricted),
                CancellationToken.None)
            .ConfigureAwait(false);
        await guests.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task WaitForGuestRestrictionProjectionAsync(
        AuthTestApplication api,
        Guid guestId,
        long expectedRevision,
        bool isRestricted,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            bool ready = await scope.ServiceProvider.GetRequiredService<ReservationsDbContext>()
                .GuestProcessingRestrictionProjections
                .AsNoTracking()
                .AnyAsync(projection =>
                    projection.PropertyId == PropertyId &&
                    projection.GuestId == guestId &&
                    projection.ContractVersion == GuestProcessingRestrictionContract.CurrentVersion &&
                    projection.Revision == expectedRevision &&
                    projection.IsRestricted == isRestricted)
                .ConfigureAwait(false);
            if (ready)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Reservations did not receive Guest restriction revision '{expectedRevision}'.");
    }

    private static async Task<GuestStayHistoryItem> WaitForGuestStayAsync(
        HttpClient client,
        string accessToken,
        Guid guestId,
        Guid reservationId,
        GuestStayStatus status,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            GuestStayHistoryItem? stay = await TryGetGuestStayAsync(
                client,
                accessToken,
                guestId,
                reservationId).ConfigureAwait(false);
            if (stay?.Status == status)
            {
                return stay;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Guest stay did not reach '{status}'.");
    }

    private static async Task<GuestStayHistoryItem> GetGuestStayAsync(
        HttpClient client,
        string accessToken,
        Guid guestId,
        Guid reservationId) => await TryGetGuestStayAsync(client, accessToken, guestId, reservationId)
        .ConfigureAwait(false) ?? throw new InvalidOperationException("Guest stay was not found.");

    private static async Task<GuestStayHistoryItem?> TryGetGuestStayAsync(
        HttpClient client,
        string accessToken,
        Guid guestId,
        Guid reservationId)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/guests/properties/{PropertyId:D}/{guestId:D}/stays?page=1&pageSize=100",
            accessToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        GuestStayHistoryListResponse? stays = await response.Content
            .ReadFromJsonAsync<GuestStayHistoryListResponse>()
            .ConfigureAwait(false);
        return stays?.Stays.SingleOrDefault(stay => stay.ReservationId == reservationId);
    }

    private static async Task ApplyStaleGuestStayEventAsync(
        AuthTestApplication api,
        Guid guestId,
        ReservationDto reservation,
        long staleVersion)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        IntegrationEventSubscription subscription = scope.ServiceProvider
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item => item.ConsumerModule == GuestsModuleMetadata.Name &&
                            item.EventType == typeof(ReservationGuestStayChangedIntegrationEvent));
        IIntegrationEventHandler<ReservationGuestStayChangedIntegrationEvent> handler =
            (IIntegrationEventHandler<ReservationGuestStayChangedIntegrationEvent>)scope.ServiceProvider
                .GetRequiredService(subscription.HandlerType);
        GuestsDbContext guests = scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        await using var transaction = await guests.Database.BeginTransactionAsync()
            .ConfigureAwait(false);
        await handler.HandleAsync(
            new(
                Guid.NewGuid(),
                TenantId,
                DateTimeOffset.UtcNow,
                reservation.PropertyId,
                reservation.ReservationId,
                guestId,
                GuestStayRole.Primary,
                reservation.Arrival,
                reservation.Departure,
                GuestStayStatus.Confirmed,
                null,
                null,
                null,
                true,
                staleVersion),
            CancellationToken.None).ConfigureAwait(false);
        await guests.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task WaitForSellableProjectionAsync(AuthTestApplication api, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            Guid[] sellableIds = await scope.ServiceProvider.GetRequiredService<ReservationsDbContext>()
                .InventoryUnitProjections
                .AsNoTracking()
                .Where(unit =>
                    (unit.Id == RoomId || unit.Id == ReplacementRoomId) &&
                    unit.PropertyId == PropertyId &&
                    unit.IsSellable)
                .Select(unit => unit.Id)
                .ToArrayAsync()
                .ConfigureAwait(false);
            if (sellableIds.Length == 2)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException("Reservations did not receive the sellable Inventory unit projection.");
    }

    private static async Task GrantReservationsAccessAsync(
        AdminCliTestApplication admin,
        Guid operatorId)
    {
        await AssertAdminSuccessAsync(admin.ExecuteAsync("admin", "bootstrap", "--actor", "owner", "--yes"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create",
            "--actor", "owner",
            "--name", "reservations-operator"));
        foreach (string permission in new[]
                 {
                     ReservationsAdminPermissionCodes.Read,
                     ReservationsAdminPermissionCodes.Create,
                     ReservationsAdminPermissionCodes.Manage,
                     ReservationsAdminPermissionCodes.Cancel,
                     ReservationsAdminPermissionCodes.CheckIn,
                     ReservationsAdminPermissionCodes.NoShow,
                     ReservationsAdminPermissionCodes.CheckOut
                     ,ReservationsAdminPermissionCodes.ManageGuests
                     ,GuestsAdminPermissionCodes.Read
                     ,GuestsAdminPermissionCodes.Create
                     ,GuestsAdminPermissionCodes.Manage
                     ,GuestsAdminPermissionCodes.Archive
                 })
        {
            await AssertAdminSuccessAsync(admin.ExecuteAsync(
                "admin", "roles", "grant",
                "--actor", "owner",
                "--role", "reservations-operator",
                "--permission", permission));
        }

        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", "owner",
            "--target-kind", "user",
            "--target-id", operatorId.ToString("D"),
            "--role", "reservations-operator",
            "--scope", $"tenant:{TenantId}/property:{PropertyId:D}"));
    }

    private static async Task GrantReservationsReadAccessAsync(
        AdminCliTestApplication admin,
        Guid readerId)
    {
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create",
            "--actor", "owner",
            "--name", "reservations-reader"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "grant",
            "--actor", "owner",
            "--role", "reservations-reader",
            "--permission", ReservationsAdminPermissionCodes.Read));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", "owner",
            "--target-kind", "user",
            "--target-id", readerId.ToString("D"),
            "--role", "reservations-reader",
            "--scope", $"tenant:{TenantId}/property:{PropertyId:D}"));
    }

    private static async Task GrantReservationsAdminAccessAsync(
        AdminCliTestApplication admin,
        params Guid[] adminActorIds)
    {
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create",
            "--actor", "owner",
            "--name", "reservations-stay-admin"));
        foreach (string permission in new[]
                 {
                     ReservationsAdminPermissionCodes.Read,
                     ReservationsAdminPermissionCodes.Manage
                 })
        {
            await AssertAdminSuccessAsync(admin.ExecuteAsync(
                "admin", "roles", "grant",
                "--actor", "owner",
                "--role", "reservations-stay-admin",
                "--permission", permission));
        }

        foreach (Guid adminActorId in adminActorIds)
        {
            await AssertAdminSuccessAsync(admin.ExecuteAsync(
                "admin", "roles", "assign",
                "--actor", "owner",
                "--target-kind", "admin-actor",
                "--target-id", adminActorId.ToString("D"),
                "--role", "reservations-stay-admin",
                "--scope", $"tenant:{TenantId}/property:{PropertyId:D}"));
        }
    }

    private static async Task<ReservationMutationReceiptDto> CreateReservationAsync(
        HttpClient client,
        string accessToken,
        DateOnly arrival,
        DateOnly departure,
        string guestName)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Post,
            $"/api/reservations/properties/{PropertyId:D}",
            accessToken,
            new
            {
                operationId = Guid.NewGuid(),
                arrival,
                departure,
                inventoryUnitIds = new[] { RoomId },
                primaryGuestName = guestName,
                email = $"{guestName.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant()}@example.test",
                phone = (string?)null,
                guestCount = 1,
                sourceKind = ReservationSourceKind.Direct,
                sourceSystem = (string?)null,
                sourceReference = (string?)null,
                notes = (string?)null
            }).ConfigureAwait(false);
        return await ReadSuccessAsync<ReservationMutationReceiptDto>(response)
            .ConfigureAwait(false);
    }

    private static async Task<ReservationDto> WaitForStatusAsync(
        HttpClient client,
        string accessToken,
        Guid reservationId,
        ReservationStatus expected,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        ReservationDto? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using HttpResponseMessage response = await SendAsync(
                client,
                HttpMethod.Get,
                $"/api/reservations/properties/{PropertyId:D}/{reservationId:D}",
                accessToken).ConfigureAwait(false);
            last = await ReadSuccessAsync<ReservationDto>(response).ConfigureAwait(false);
            if (last.Status == expected)
            {
                return last;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Reservation '{reservationId}' did not reach '{expected}'. Last status: '{last?.Status}'.");
    }

    private static async Task<ReservationStayAmendmentReceiptDto> WaitForStayAmendmentAsync(
        HttpClient client,
        string accessToken,
        Guid reservationId,
        Guid operationId,
        ReservationStayAmendmentOutcome expected,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        ReservationStayAmendmentReceiptDto? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using HttpResponseMessage response = await SendAsync(
                client,
                HttpMethod.Get,
                $"/api/reservations/properties/{PropertyId:D}/{reservationId:D}/stay-amendments/{operationId:D}",
                accessToken).ConfigureAwait(false);
            last = await ReadSuccessAsync<ReservationStayAmendmentReceiptDto>(response)
                .ConfigureAwait(false);
            if (last.Outcome == expected)
            {
                return last;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Stay amendment '{operationId}' did not reach '{expected}'. Last outcome: '{last?.Outcome}'.");
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
        Assert.True(response.IsSuccessStatusCode, $"Expected success but received {(int)response.StatusCode}. Body: {body}");
        T? value = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        Assert.NotNull(value);
        return value;
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but received {(int)response.StatusCode}. Body: {body}");
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore is true);
        Assert.Contains(response.Headers.Pragma, header =>
            string.Equals(header.Name, "no-cache", StringComparison.OrdinalIgnoreCase));
        Assert.True(response.Content.Headers.TryGetValues("Expires", out IEnumerable<string>? expires));
        Assert.Equal("0", Assert.Single(expires));
    }

    private static async Task AssertInventoryHoldTruthIsRequiredInOpenApiAsync(
        IServiceProvider services)
    {
        ISwaggerProvider swagger = services.GetRequiredService<ISwaggerProvider>();
        string body = await swagger.GetSwagger("v1")
            .SerializeAsync(OpenApiSpecVersion.OpenApi3_0, "json")
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");

        foreach (string schemaName in new[] { "ReservationDto", "ReservationListItemDto" })
        {
            JsonElement schema = schemas.GetProperty(schemaName);
            Assert.True(schema.GetProperty("properties").TryGetProperty(
                "holdsInventory",
                out JsonElement property));
            Assert.Equal("boolean", property.GetProperty("type").GetString());
            Assert.Contains(
                schema.GetProperty("required").EnumerateArray(),
                item => string.Equals(
                    item.GetString(),
                    "holdsInventory",
                    StringComparison.Ordinal));
        }
    }

    private static async Task AssertAdminSuccessAsync(Task<AdminCliResult> resultTask)
    {
        AdminCliResult result = await resultTask.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == AdminExitCodes.Success,
            $"ExitCode={result.ExitCode}{Environment.NewLine}Output:{Environment.NewLine}{result.Output}{Environment.NewLine}Error:{Environment.NewLine}{result.Error}");
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

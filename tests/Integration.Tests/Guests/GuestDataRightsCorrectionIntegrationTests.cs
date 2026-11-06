namespace Integration.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using BunkFy.Host.Worker;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Properties.Contracts;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Administration.Cli;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Tenancy;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestDataRightsCorrectionIntegrationTests
{
    private const string TenantId = "a4000000-0000-0000-0000-000000000001";
    private static readonly Guid PropertyId =
        Guid.Parse("74000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 13, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Approved_correction_commits_profile_event_and_receipt_atomically()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_guest_data_rights_correction_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            AuthTestContainers.GetNatsConnectionString(nats),
            disableOutboxPublisher: false);
        await api.MigrateGuestDataRightsAuthorizationDatabaseAsync().ConfigureAwait(false);
        await using AdminCliTestApplication admin = new("PostgreSql", connectionString);
        await admin.MigrateAsync().ConfigureAwait(false);
        using HttpClient client = api.CreateClient();
        using TemporaryDirectory ledgerDelta = new("bunkfy-guest-correction");
        using IHost worker = CreateCorrectionWorker(
            connectionString,
            AuthTestContainers.GetNatsConnectionString(nats),
            ledgerDelta.Path);
        await worker.StartAsync().ConfigureAwait(false);

        AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "privacy-operator@guests.test").ConfigureAwait(false);
        Guid operatorId = GetSubjectId(tokens.AccessToken);
        await api.SeedOrganizationMembershipAsync(TenantId, operatorId).ConfigureAwait(false);
        await GrantCorrectionAccessAsync(admin, operatorId).ConfigureAwait(false);

        (GuestProfile profile, DataRightsCase dataRightsCase) =
            await SeedApprovedCorrectionAsync(api).ConfigureAwait(false);
        Guid executionId = Guid.NewGuid();
        DataRightsCorrectionExecutionDto started;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/properties/{PropertyId:D}/cases/" +
                   $"{dataRightsCase.Id:D}/correction",
                   new
                   {
                       executionId,
                       expectedVersion = dataRightsCase.Version
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            started = await ReadSuccessAsync<DataRightsCorrectionExecutionDto>(response)
                .ConfigureAwait(false);
        }

        Assert.Equal(DataRightsCaseStatus.Executing, started.Case.Status);
        Assert.Equal(executionId, started.Execution.ExecutionId);
        var request = new
        {
            idempotencyKey = executionId,
            caseId = dataRightsCase.Id,
            approvalRevision = dataRightsCase.DecisionRevision!.Value,
            guestId = profile.Id,
            expectedVersion = profile.Version,
            displayName = "Corrected Guest",
            legalName = "Correct Legal Name",
            email = "corrected@example.test",
            phone = "+44 20 9999 0000",
            dateOfBirth = new DateOnly(1992, 4, 10),
            nationalityCountryCode = "GB",
            preferredLanguageTag = "en-GB",
            languageTags = new[] { "en-GB", "fr", "unknown-١" },
            notes = "Corrected through an approved rights request."
        };

        GuestDataRightsCorrectionReceiptDto first;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/guests/properties/{PropertyId:D}/data-rights-corrections",
                   request,
                   tokens.AccessToken).ConfigureAwait(false))
        {
            first = await ReadSuccessAsync<GuestDataRightsCorrectionReceiptDto>(response)
                .ConfigureAwait(false);
        }

        await WaitForCorrectionCompletionAsync(
            api,
            dataRightsCase.Id,
            executionId,
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);

        using (HttpResponseMessage retry = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/guests/properties/{PropertyId:D}/data-rights-corrections",
                   request,
                   tokens.AccessToken).ConfigureAwait(false))
        {
            GuestDataRightsCorrectionReceiptDto replay =
                await ReadSuccessAsync<GuestDataRightsCorrectionReceiptDto>(retry)
                    .ConfigureAwait(false);
            Assert.Equal(first.ReceiptId, replay.ReceiptId);
            Assert.Equal(first.CaseId, replay.CaseId);
            Assert.Equal(first.ApprovalRevision, replay.ApprovalRevision);
            Assert.Equal(first.GuestId, replay.GuestId);
            Assert.Equal(first.PreviousVersion, replay.PreviousVersion);
            Assert.Equal(first.CurrentVersion, replay.CurrentVersion);
            Assert.Equal(first.ChangedFields, replay.ChangedFields);
            Assert.Equal(first.EventId, replay.EventId);
            Assert.Equal(first.CompletedAtUtc, replay.CompletedAtUtc);
        }

        using (HttpResponseMessage replayClaim = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/properties/{PropertyId:D}/cases/" +
                   $"{dataRightsCase.Id:D}/correction",
                   new
                   {
                       executionId,
                       expectedVersion = dataRightsCase.Version
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            DataRightsCorrectionExecutionDto completed =
                await ReadSuccessAsync<DataRightsCorrectionExecutionDto>(replayClaim)
                    .ConfigureAwait(false);
            Assert.Equal(DataRightsCaseStatus.Completed, completed.Case.Status);
            Assert.Equal(
                DataRightsCorrectionExecutionStatus.Completed,
                completed.Execution.Status);
            Assert.Equal(first.ReceiptId, completed.Execution.ReceiptId);
        }

        using (HttpResponseMessage conflicting = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/guests/properties/{PropertyId:D}/data-rights-corrections",
                   new
                   {
                       request.idempotencyKey,
                       request.caseId,
                       request.approvalRevision,
                       request.guestId,
                       request.expectedVersion,
                       displayName = "Different Reuse",
                       request.legalName,
                       request.email,
                       request.phone,
                       request.dateOfBirth,
                       request.nationalityCountryCode,
                       request.preferredLanguageTag,
                       request.notes
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        }

        using IServiceScope verificationScope = api.Services.CreateScope();
        verificationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        GuestsDbContext guests =
            verificationScope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        GuestProfile persisted = await guests.GuestProfiles
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == profile.Id)
            .ConfigureAwait(false);
        GuestDataRightsCorrectionReceipt receipt = await guests.DataRightsCorrectionReceipts
            .AsNoTracking()
            .SingleAsync()
            .ConfigureAwait(false);

        Assert.Equal("Corrected Guest", persisted.DisplayName);
        Assert.Equal(["en-GB", "fr", "unknown-١"], persisted.LanguageTags);
        Assert.Equal(2, receipt.ContractVersion);
        Assert.Contains("guest.profile.language-tags", first.ChangedFields);
        Assert.Equal(2, persisted.Version);
        Assert.Equal(first.ReceiptId, receipt.Id);
        Assert.Equal(profile.Id, receipt.GuestId);
        Assert.Equal(dataRightsCase.Id, receipt.CaseId);
        Assert.Equal(dataRightsCase.DecisionRevision, receipt.ApprovalRevision);
        Assert.Equal(1, receipt.SelectedRecordVersion);
        Assert.Equal(2, receipt.CurrentRecordVersion);
        Assert.NotEqual(Guid.Empty, receipt.CompletionEventId);
        Assert.Single(guests.DataRightsCorrectionReceipts);
        Assert.Single(
            guests.OutboxMessages,
            message => message.EventType.Contains("GuestProfileUpdated", StringComparison.Ordinal));
        Assert.Single(
            guests.OutboxMessages,
            message =>
                message.EventType ==
                    typeof(DataRightsCorrectionAppliedIntegrationEvent).FullName &&
                message.ProcessedAtUtc != null);
        await worker.StopAsync().ConfigureAwait(false);
    }

    private static async Task WaitForCorrectionCompletionAsync(
        AuthTestApplication api,
        Guid caseId,
        Guid executionId,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            DataRightsDbContext dataRights =
                scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
            DataRightsCase? dataRightsCase = await dataRights.Cases
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == caseId)
                .ConfigureAwait(false);
            DataRightsCorrectionExecution? execution = await dataRights.CorrectionExecutions
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == executionId)
                .ConfigureAwait(false);
            if (dataRightsCase?.Status == DataRightsCaseState.Completed &&
                execution?.State == DataRightsCorrectionExecutionState.Completed)
            {
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        using IServiceScope diagnosticScope = api.Services.CreateScope();
        diagnosticScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        DataRightsDbContext diagnosticDataRights =
            diagnosticScope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        GuestsDbContext diagnosticGuests =
            diagnosticScope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        DataRightsCorrectionExecution? observedExecution =
            await diagnosticDataRights.CorrectionExecutions
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == executionId)
                .ConfigureAwait(false);
        Gma.Framework.Messaging.Infrastructure.InboxMessage? observedInbox =
            await diagnosticDataRights.InboxMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(message =>
                    message.EventType ==
                    DataRightsCorrectionAppliedIntegrationEvent.EventType)
                .ConfigureAwait(false);
        Gma.Framework.Messaging.Infrastructure.OutboxMessage? observedOutbox =
            await diagnosticGuests.OutboxMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(message =>
                    message.EventType ==
                    DataRightsCorrectionAppliedIntegrationEvent.EventType)
                .ConfigureAwait(false);
        string registeredOutboxStores = string.Join(
            ",",
            diagnosticScope.ServiceProvider
                .GetServices<Gma.Framework.Messaging.IOutboxStore>()
                .Select(store => store.ModuleName));
        Microsoft.Extensions.Hosting.BackgroundService? outboxPublisher =
            diagnosticScope.ServiceProvider
                .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<Microsoft.Extensions.Hosting.BackgroundService>()
                .SingleOrDefault(service =>
                    service.GetType().Name == "OutboxPublisherService");
        string publisherTaskStatus =
            outboxPublisher?.ExecuteTask?.Status.ToString() ?? "<missing>";
        string publisherTaskError =
            outboxPublisher?.ExecuteTask?.Exception?.GetBaseException().Message ?? "<none>";
        string hostedServices = string.Join(
            ",",
            diagnosticScope.ServiceProvider
                .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .Select(service => service.GetType().Name)
                .Order(StringComparer.Ordinal));
        throw new TimeoutException(
            "The correction owner proof did not reconcile the Data Rights case. " +
            $"Execution={observedExecution?.State}; " +
            $"OutboxProcessed={observedOutbox?.ProcessedAtUtc is not null}; " +
            $"OutboxAttempts={observedOutbox?.Attempts ?? 0}; " +
            $"OutboxLockedBy={observedOutbox?.LockedBy ?? "<none>"}; " +
            $"OutboxLockedUntil={observedOutbox?.LockedUntilUtc?.ToString("O") ?? "<none>"}; " +
            $"OutboxNextAttempt={observedOutbox?.NextAttemptAtUtc?.ToString("O") ?? "<none>"}; " +
            $"OutboxError={observedOutbox?.Error ?? "<none>"}; " +
            $"InboxStatus={observedInbox?.Status.ToString() ?? "<missing>"}; " +
            $"InboxAttempts={observedInbox?.Attempts ?? 0}; " +
            $"InboxError={observedInbox?.LastError ?? "<none>"}; " +
            $"OutboxStores={registeredOutboxStores}; " +
            $"PublisherTaskStatus={publisherTaskStatus}; " +
            $"PublisherTaskError={publisherTaskError}; " +
            $"HostedServices={hostedServices}.");
    }

    private static async Task<(GuestProfile Profile, DataRightsCase Case)>
        SeedApprovedCorrectionAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        GuestsDbContext guests = scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        GuestPropertyProjection property = new(
            TenantId,
            PropertyId,
            "Correction House",
            PropertyStatus.Active,
            1);
        guests.PropertyProjections.Add(property);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            GuestsModuleMetadata.Name,
            TenantId,
            PropertyId,
            2).ConfigureAwait(false);
        GuestProfile profile = GuestProfile.Create(
            Guid.NewGuid(),
            TenantId,
            PropertyId,
            "Original Guest",
            "Original Legal Name",
            "original@example.test",
            "+44 20 1111 0000",
            new DateOnly(1992, 4, 10),
            "GB",
            "en-GB",
            "Original note.",
            "user:seed",
            Guid.NewGuid(),
            Now).Value;
        guests.GuestProfiles.Add(profile);
        await guests.SaveChangesAsync().ConfigureAwait(false);

        DataRightsCaseRequest caseRequest = DataRightsCaseRequest.Create(
            PropertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Correction,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            caseRequest,
            "user:privacy-reviewer",
            Now.AddMinutes(1)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            GuestsDataRightsCoordinates.Owner,
            GuestsDataRightsCoordinates.GuestProfileRecordType,
            profile.Id,
            profile.Version,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            Now.AddMinutes(4)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(5)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(6)).IsSuccess);
        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        dataRights.Cases.Add(dataRightsCase);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return (profile, dataRightsCase);
    }

    private static IHost CreateCorrectionWorker(
        string connectionString,
        string natsConnectionString,
        string ledgerDeltaPath)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Integration" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationIdentity:DisplayName"] = "BunkFy correction integration worker",
            ["ApplicationIdentity:Namespace"] = "bunkfy",
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:PostgreSql"] = connectionString,
            ["ConnectionStrings:nats"] = natsConnectionString,
            ["DataRights:LedgerDelta:Provider"] = "LocalFile",
            ["DataRights:LedgerDelta:LocalFilePath"] = ledgerDeltaPath,
            ["DataRights:LedgerDelta:ActiveIntegrityKeyVersion"] = "1",
            ["DataRights:LedgerDelta:IntegrityKeys:1"] =
                "aWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWk=",
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
            ["Worker:Modules:DataRights"] = "true"
        });
        builder.Logging.ClearProviders();
        builder.AddWorkerHost();
        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
        ModuleCompositionValidationResult composition = builder.ValidateModuleComposition();
        Assert.True(composition.IsValid, composition.Report);
        return builder.Build();
    }

    private sealed class TemporaryDirectory(string prefix) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(this.Path))
            {
                Directory.Delete(this.Path, recursive: true);
            }
        }
    }

    private static async Task GrantCorrectionAccessAsync(
        AdminCliTestApplication admin,
        Guid operatorId)
    {
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "bootstrap",
            "--actor",
            "owner",
            "--yes"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "roles",
            "create",
            "--actor",
            "owner",
            "--name",
            "privacy-correction-operator"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "roles",
            "grant",
            "--actor",
            "owner",
            "--role",
            "privacy-correction-operator",
            "--permission",
            DataRightsAdminPermissionCodes.Execute));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "roles",
            "assign",
            "--actor",
            "owner",
            "--target-kind",
            "user",
            "--target-id",
            operatorId.ToString("D"),
            "--role",
            "privacy-correction-operator",
            "--scope",
            $"tenant:{TenantId}/property:{PropertyId:D}"));
    }

    private static async Task<T> ReadSuccessAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Fail(
                $"Request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}): {body}");
        }

        T? value = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        return Assert.IsType<T>(value);
    }

    private static Guid GetSubjectId(string accessToken)
    {
        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        return Guid.Parse(token.Claims.Single(claim =>
            claim.Type is "sub" or "nameid").Value);
    }

    private static async Task AssertAdminSuccessAsync(Task<AdminCliResult> operation)
    {
        AdminCliResult result = await operation.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == 0,
            $"Admin CLI failed with exit code {result.ExitCode}:{Environment.NewLine}" +
            $"{result.Output}{Environment.NewLine}{result.Error}");
    }
}

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
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Persistence;
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

public sealed class StaffDataRightsCorrectionIntegrationTests
{
    private const string TenantId = "a7000000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset SeededAtUtc =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Approved_tenant_correction_commits_departed_profile_and_completes_case()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_staff_data_rights_correction_tests")
                .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        string natsConnectionString =
            AuthTestContainers.GetNatsConnectionString(nats);
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            disableOutboxPublisher: false);
        await api.MigrateStaffAuthorizationDatabaseAsync().ConfigureAwait(false);
        await api.MigrateGuestDataRightsAuthorizationDatabaseAsync()
            .ConfigureAwait(false);

        await using AdminCliTestApplication admin =
            new("PostgreSql", connectionString);
        await admin.MigrateAsync().ConfigureAwait(false);
        using HttpClient client = api.CreateClient();
        using TemporaryDirectory ledgerDelta =
            new("bunkfy-staff-correction");
        using IHost worker = CreateCorrectionWorker(
            connectionString,
            natsConnectionString,
            ledgerDelta.Path);
        await worker.StartAsync().ConfigureAwait(false);

        AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "privacy-operator@staff.test").ConfigureAwait(false);
        Guid operatorId = GetSubjectId(tokens.AccessToken);
        await api.SeedOrganizationMembershipAsync(TenantId, operatorId)
            .ConfigureAwait(false);
        await GrantCorrectionAccessAsync(admin, operatorId)
            .ConfigureAwait(false);

        (StaffMember member, DataRightsCase dataRightsCase) =
            await SeedApprovedCorrectionAsync(api).ConfigureAwait(false);
        Guid executionId = Guid.NewGuid();
        DataRightsCorrectionExecutionDto started;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/tenant/cases/{dataRightsCase.Id:D}/correction",
                   new
                   {
                       executionId,
                       expectedVersion = dataRightsCase.Version
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            started =
                await ReadSuccessAsync<DataRightsCorrectionExecutionDto>(response)
                    .ConfigureAwait(false);
        }

        Assert.Equal(DataRightsCaseStatus.Executing, started.Case.Status);
        Assert.Equal(DataRightsCaseType.StaffRights, started.Execution.CaseType);
        Assert.Null(started.Execution.PropertyId);
        Assert.Equal(executionId, started.Execution.ExecutionId);

        var request = new
        {
            executionId,
            caseId = dataRightsCase.Id,
            approvalRevision = dataRightsCase.DecisionRevision!.Value,
            staffMemberId = member.Id,
            expectedVersion = member.Version,
            displayName = "Corrected Staff Member",
            legalName = "Corrected Legal Name",
            workEmail = "corrected.staff@example.test",
            workPhone = "+44 20 9999 0000",
            employeeNumber = "EMP-900",
            jobTitle = "Operations Lead",
            department = "Operations"
        };

        StaffDataRightsCorrectionReceiptDto first;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   "/api/staff/data-rights-corrections",
                   request,
                   tokens.AccessToken).ConfigureAwait(false))
        {
            first =
                await ReadSuccessAsync<StaffDataRightsCorrectionReceiptDto>(response)
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
                   "/api/staff/data-rights-corrections",
                   request,
                   tokens.AccessToken).ConfigureAwait(false))
        {
            StaffDataRightsCorrectionReceiptDto replay =
                await ReadSuccessAsync<StaffDataRightsCorrectionReceiptDto>(retry)
                    .ConfigureAwait(false);
            Assert.Equal(first.ReceiptId, replay.ReceiptId);
            Assert.Equal(first.ExecutionId, replay.ExecutionId);
            Assert.Equal(first.CaseId, replay.CaseId);
            Assert.Equal(first.ApprovalRevision, replay.ApprovalRevision);
            Assert.Equal(first.StaffMemberId, replay.StaffMemberId);
            Assert.Equal(
                first.SelectedRecordVersion,
                replay.SelectedRecordVersion);
            Assert.Equal(first.CurrentRecordVersion, replay.CurrentRecordVersion);
            Assert.Equal(first.ChangedFieldKeys, replay.ChangedFieldKeys);
            Assert.Equal(first.CompletedAtUtc, replay.CompletedAtUtc);
        }

        using (HttpResponseMessage replayClaim =
               await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/tenant/cases/{dataRightsCase.Id:D}/correction",
                   new
                   {
                       executionId,
                       expectedVersion = dataRightsCase.Version
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            DataRightsCorrectionExecutionDto completed =
                await ReadSuccessAsync<DataRightsCorrectionExecutionDto>(
                    replayClaim).ConfigureAwait(false);
            Assert.Equal(DataRightsCaseStatus.Completed, completed.Case.Status);
            Assert.Equal(
                DataRightsCorrectionExecutionStatus.Completed,
                completed.Execution.Status);
            Assert.Equal(first.ReceiptId, completed.Execution.ReceiptId);
        }

        using (HttpResponseMessage conflicting =
               await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   "/api/staff/data-rights-corrections",
                   new
                   {
                       request.executionId,
                       request.caseId,
                       request.approvalRevision,
                       request.staffMemberId,
                       request.expectedVersion,
                       displayName = "Different Reuse",
                       request.legalName,
                       request.workEmail,
                       request.workPhone,
                       request.employeeNumber,
                       request.jobTitle,
                       request.department
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        }

        using IServiceScope verificationScope = api.Services.CreateScope();
        verificationScope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        StaffDbContext staff =
            verificationScope.ServiceProvider.GetRequiredService<StaffDbContext>();
        StaffMember persisted = await staff.StaffMembers
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == member.Id)
            .ConfigureAwait(false);
        StaffDataRightsCorrectionReceipt receipt =
            await staff.DataRightsCorrectionReceipts
                .AsNoTracking()
                .SingleAsync()
                .ConfigureAwait(false);

        Assert.Equal("Corrected Staff Member", persisted.DisplayName);
        Assert.Equal("account-staff", persisted.AuthSubjectId);
        Assert.Equal(StaffMemberState.Departed, persisted.Status);
        Assert.Equal(member.DepartureEffectiveOn, persisted.DepartureEffectiveOn);
        Assert.Equal(member.Version + 1, persisted.Version);
        Assert.Equal(first.ReceiptId, receipt.Id);
        Assert.Equal(member.Id, receipt.StaffMemberId);
        Assert.Equal(dataRightsCase.Id, receipt.CaseId);
        Assert.Equal(dataRightsCase.DecisionRevision, receipt.ApprovalRevision);
        Assert.Equal(member.Version, receipt.SelectedRecordVersion);
        Assert.Equal(member.Version + 1, receipt.CurrentRecordVersion);
        Assert.NotEqual(Guid.Empty, receipt.CompletionEventId);
        Assert.Single(staff.DataRightsCorrectionReceipts);
        Assert.Single(
            staff.OutboxMessages,
            message =>
                message.EventType.Contains(
                    "StaffMemberUpdated",
                    StringComparison.Ordinal));
        Assert.Single(
            staff.OutboxMessages,
            message =>
                message.EventType ==
                    typeof(DataRightsTenantCorrectionAppliedIntegrationEvent)
                        .FullName &&
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
            DataRightsCorrectionExecution? execution =
                await dataRights.CorrectionExecutions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.Id == executionId)
                    .ConfigureAwait(false);
            if (dataRightsCase?.Status == DataRightsCaseState.Completed &&
                execution?.State ==
                    DataRightsCorrectionExecutionState.Completed)
            {
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        using IServiceScope diagnosticScope = api.Services.CreateScope();
        diagnosticScope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        DataRightsDbContext diagnosticDataRights =
            diagnosticScope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        StaffDbContext diagnosticStaff =
            diagnosticScope.ServiceProvider.GetRequiredService<StaffDbContext>();
        DataRightsCorrectionExecution? observedExecution =
            await diagnosticDataRights.CorrectionExecutions
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == executionId)
                .ConfigureAwait(false);
        Gma.Framework.Messaging.Infrastructure.OutboxMessage? observedOutbox =
            await diagnosticStaff.OutboxMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(message =>
                    message.EventType ==
                    typeof(DataRightsTenantCorrectionAppliedIntegrationEvent)
                        .FullName)
                .ConfigureAwait(false);
        throw new TimeoutException(
            "The Staff correction proof did not reconcile the Data Rights case. " +
            $"Execution={observedExecution?.State}; " +
            $"OutboxProcessed={observedOutbox?.ProcessedAtUtc is not null}; " +
            $"OutboxAttempts={observedOutbox?.Attempts ?? 0}; " +
            $"OutboxError={observedOutbox?.Error ?? "<none>"}.");
    }

    private static async Task<(StaffMember Member, DataRightsCase Case)>
        SeedApprovedCorrectionAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        StaffDbContext staff =
            scope.ServiceProvider.GetRequiredService<StaffDbContext>();
        StaffMember member = StaffMember.Create(
            Guid.NewGuid(),
            TenantId,
            "Original Staff Member",
            "Original Legal Name",
            "original.staff@example.test",
            "+44 20 1111 0000",
            "EMP-100",
            "Manager",
            "Front Office",
            "account-staff",
            "user:seed",
            Guid.NewGuid(),
            SeededAtUtc).Value;
        Assert.True(member.Depart(
            new DateOnly(2026, 7, 1),
            member.Version,
            "user:seed",
            "Employment ended.",
            Guid.NewGuid(),
            [],
            SeededAtUtc.AddMinutes(1)).IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IStaffMemberRepository>()
            .AddAsync(member, CancellationToken.None).ConfigureAwait(false);
        await staff.SaveChangesAsync().ConfigureAwait(false);

        DataRightsCaseRequest caseRequest = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.Correction,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            caseRequest,
            "user:privacy-reviewer",
            SeededAtUtc.AddMinutes(2)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            SeededAtUtc.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            StaffDataRightsCoordinates.Owner,
            StaffDataRightsCoordinates.StaffMemberRecordType,
            member.Id,
            member.Version,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            SeededAtUtc.AddMinutes(4)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            SeededAtUtc.AddMinutes(5)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            SeededAtUtc.AddMinutes(6)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            SeededAtUtc.AddMinutes(7)).IsSuccess);
        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        dataRights.Cases.Add(dataRightsCase);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return (member, dataRightsCase);
    }

    private static IHost CreateCorrectionWorker(
        string connectionString,
        string natsConnectionString,
        string ledgerDeltaPath)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                EnvironmentName = "Integration"
            });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ApplicationIdentity:DisplayName"] =
                    "BunkFy Staff correction integration worker",
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
                ["Worker:Modules:Staff"] = "true",
                ["Worker:Modules:DataRights"] = "true"
            });
        builder.Logging.ClearProviders();
        builder.AddWorkerHost();
        ModuleCompositionValidationResult composition =
            builder.ValidateModuleComposition();
        Assert.True(composition.IsValid, composition.Report);
        return builder.Build();
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
            "staff-privacy-correction-operator"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "roles",
            "grant",
            "--actor",
            "owner",
            "--role",
            "staff-privacy-correction-operator",
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
            "staff-privacy-correction-operator",
            "--scope",
            $"tenant:{TenantId}"));
    }

    private static async Task<T> ReadSuccessAsync<T>(
        HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync()
                .ConfigureAwait(false);
            Assert.Fail(
                $"Request failed with HTTP {(int)response.StatusCode} " +
                $"({response.StatusCode}): {body}");
        }

        T? value = await response.Content.ReadFromJsonAsync<T>()
            .ConfigureAwait(false);
        return Assert.IsType<T>(value);
    }

    private static Guid GetSubjectId(string accessToken)
    {
        JwtSecurityToken token =
            new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        return Guid.Parse(token.Claims.Single(claim =>
            claim.Type is "sub" or "nameid").Value);
    }

    private static async Task AssertAdminSuccessAsync(
        Task<AdminCliResult> operation)
    {
        AdminCliResult result = await operation.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == 0,
            $"Admin CLI failed with exit code {result.ExitCode}:" +
            $"{Environment.NewLine}{result.Output}{Environment.NewLine}" +
            result.Error);
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
}

namespace Integration.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BunkFy.Host.Worker;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Persistence;
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

public sealed class
    WorkspaceStaffOnboardingDataRightsCorrectionIntegrationTests
{
    private const string TenantId =
        "a7100000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset SeededAtUtc =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Approved_correction_reconciles_once_and_rejects_authority_handoff()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_correction_tests")
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
        await api.MigrateStaffAuthorizationDatabaseAsync()
            .ConfigureAwait(false);
        await api.MigrateGuestDataRightsAuthorizationDatabaseAsync()
            .ConfigureAwait(false);

        await using AdminCliTestApplication admin =
            new("PostgreSql", connectionString);
        await admin.MigrateAsync().ConfigureAwait(false);
        using HttpClient client = api.CreateClient();
        using TemporaryDirectory ledgerDelta =
            new("bunkfy-workspace-correction");
        using IHost worker = CreateCorrectionWorker(
            connectionString,
            natsConnectionString,
            ledgerDelta.Path);
        await worker.StartAsync().ConfigureAwait(false);

        AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "privacy-operator@workspaces.test").ConfigureAwait(false);
        Guid operatorId = GetSubjectId(tokens.AccessToken);
        await api.SeedOrganizationMembershipAsync(TenantId, operatorId)
            .ConfigureAwait(false);
        await GrantCorrectionAccessAsync(admin, operatorId)
            .ConfigureAwait(false);

        CorrectionSeed seed = await SeedApprovedCorrectionsAsync(api)
            .ConfigureAwait(false);
        Guid executionId = Guid.NewGuid();
        DataRightsCorrectionExecutionDto started =
            await StartCorrectionAsync(
                client,
                tokens.AccessToken,
                seed.ApplicationCase,
                executionId).ConfigureAwait(false);

        Assert.Equal(DataRightsCaseStatus.Executing, started.Case.Status);
        Assert.Equal(DataRightsCaseType.StaffRights, started.Execution.CaseType);
        Assert.Null(started.Execution.PropertyId);
        Assert.Equal(executionId, started.Execution.ExecutionId);

        string targetPath = BuildTargetPath(
            seed.Application.Id,
            executionId,
            seed.ApplicationCase,
            seed.Application.Version);
        using (HttpResponseMessage response = await SendAsync(
                   client,
                   HttpMethod.Get,
                   targetPath,
                   tokens.AccessToken).ConfigureAwait(false))
        {
            Assert.True(response.Headers.CacheControl?.NoStore is true);
            WorkspaceStaffOnboardingDataRightsCorrectionTargetDto target =
                await ReadSuccessAsync<
                    WorkspaceStaffOnboardingDataRightsCorrectionTargetDto>(
                    response).ConfigureAwait(false);
            Assert.Equal(seed.Application.Id, target.ApplicationId);
            Assert.Equal(seed.Application.Version, target.Version);
            Assert.Equal("Original Applicant", target.DisplayName);
            Assert.Equal("Original Legal Name", target.LegalName);
            Assert.Equal("original.applicant@example.test", target.WorkEmail);
            Assert.Equal("+44 20 1111 0000", target.WorkPhone);
            Assert.Equal("EMP-100", target.EmployeeNumber);
            Assert.Equal("Receptionist", target.JobTitle);
            Assert.Equal("Front Office", target.Department);
        }

        var request = new
        {
            executionId,
            caseId = seed.ApplicationCase.Id,
            approvalRevision =
                seed.ApplicationCase.DecisionRevision!.Value,
            applicationId = seed.Application.Id,
            expectedVersion = seed.Application.Version,
            displayName = "Corrected Applicant",
            legalName = "Corrected Legal Name",
            workEmail = "corrected.applicant@example.test",
            workPhone = "+44 20 9999 0000",
            employeeNumber = "EMP-900",
            jobTitle = "Operations Lead",
            department = "Operations"
        };

        WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto first;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   "/api/workspace-staff-enrollment/data-rights-corrections",
                   request,
                   tokens.AccessToken).ConfigureAwait(false))
        {
            Assert.True(response.Headers.CacheControl?.NoStore is true);
            (first, string body) = await ReadSuccessWithBodyAsync<
                WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                response).ConfigureAwait(false);
            Assert.DoesNotContain(
                "requestSha256",
                body,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                request.displayName,
                body,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                request.workEmail,
                body,
                StringComparison.Ordinal);
        }

        await WaitForCorrectionCompletionAsync(
            api,
            seed.ApplicationCase.Id,
            executionId,
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);

        using (HttpResponseMessage retry = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   "/api/workspace-staff-enrollment/data-rights-corrections",
                   request,
                   tokens.AccessToken).ConfigureAwait(false))
        {
            WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto replay =
                await ReadSuccessAsync<
                    WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>(
                    retry).ConfigureAwait(false);
            Assert.Equal(first.ReceiptId, replay.ReceiptId);
            Assert.Equal(first.ExecutionId, replay.ExecutionId);
            Assert.Equal(first.CaseId, replay.CaseId);
            Assert.Equal(first.ApprovalRevision, replay.ApprovalRevision);
            Assert.Equal(first.ApplicationId, replay.ApplicationId);
            Assert.Equal(
                first.SelectedRecordVersion,
                replay.SelectedRecordVersion);
            Assert.Equal(
                first.CurrentRecordVersion,
                replay.CurrentRecordVersion);
            Assert.Equal(
                first.ChangedFieldKeys,
                replay.ChangedFieldKeys);
            Assert.Equal(first.CompletedAtUtc, replay.CompletedAtUtc);
        }

        using (HttpResponseMessage conflicting =
               await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   "/api/workspace-staff-enrollment/data-rights-corrections",
                   new
                   {
                       request.executionId,
                       request.caseId,
                       request.approvalRevision,
                       request.applicationId,
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

        Guid handoffExecutionId = Guid.NewGuid();
        await StartCorrectionAsync(
            client,
            tokens.AccessToken,
            seed.HandoffCase,
            handoffExecutionId).ConfigureAwait(false);
        await MoveApplicationToProvisioningAsync(
            api,
            seed.HandoffApplication.Id).ConfigureAwait(false);

        string handoffTargetPath = BuildTargetPath(
            seed.HandoffApplication.Id,
            handoffExecutionId,
            seed.HandoffCase,
            seed.HandoffApplication.Version);
        using (HttpResponseMessage handoffTarget = await SendAsync(
                   client,
                   HttpMethod.Get,
                   handoffTargetPath,
                   tokens.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Conflict, handoffTarget.StatusCode);
        }

        using (HttpResponseMessage handoffMutation =
               await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   "/api/workspace-staff-enrollment/data-rights-corrections",
                   new
                   {
                       executionId = handoffExecutionId,
                       caseId = seed.HandoffCase.Id,
                       approvalRevision =
                           seed.HandoffCase.DecisionRevision!.Value,
                       applicationId = seed.HandoffApplication.Id,
                       expectedVersion = seed.HandoffApplication.Version,
                       displayName = "Must Not Apply",
                       legalName = seed.HandoffApplication.LegalName,
                       workEmail = seed.HandoffApplication.WorkEmail,
                       workPhone = seed.HandoffApplication.WorkPhone,
                       employeeNumber =
                           seed.HandoffApplication.EmployeeNumber,
                       jobTitle = seed.HandoffApplication.JobTitle,
                       department = seed.HandoffApplication.Department
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Conflict, handoffMutation.StatusCode);
        }

        await VerifyPersistenceAsync(
            api,
            seed,
            first,
            executionId,
            handoffExecutionId).ConfigureAwait(false);

        await worker.StopAsync().ConfigureAwait(false);
    }

    private static async Task VerifyPersistenceAsync(
        AuthTestApplication api,
        CorrectionSeed seed,
        WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto response,
        Guid executionId,
        Guid handoffExecutionId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        WorkspacesDbContext workspaces =
            scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding persisted = await workspaces
            .StaffOnboardingApplications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.Application.Id)
            .ConfigureAwait(false);
        WorkspaceStaffOnboarding handoff = await workspaces
            .StaffOnboardingApplications
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seed.HandoffApplication.Id)
            .ConfigureAwait(false);
        WorkspaceStaffOnboardingCorrectionReceipt receipt =
            await workspaces.StaffOnboardingCorrectionReceipts
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.ExecutionId == executionId)
                .ConfigureAwait(false);

        Assert.Equal("Corrected Applicant", persisted.DisplayName);
        Assert.Equal("Corrected Legal Name", persisted.LegalName);
        Assert.Equal(
            "corrected.applicant@example.test",
            persisted.WorkEmail);
        Assert.Equal("+44 20 9999 0000", persisted.WorkPhone);
        Assert.Equal("EMP-900", persisted.EmployeeNumber);
        Assert.Equal("Operations Lead", persisted.JobTitle);
        Assert.Equal("Operations", persisted.Department);
        Assert.Equal(
            "verified.applicant@example.test",
            persisted.VerifiedAccountEmail);
        Assert.Equal("account-applicant", persisted.SubjectId);
        Assert.Equal(
            WorkspaceStaffOnboardingSource.Invitation,
            persisted.SourceKind);
        Assert.Equal(seed.Application.SourceId, persisted.SourceId);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Submitted,
            persisted.Status);
        Assert.Equal(seed.Application.Version + 1, persisted.Version);

        Assert.Equal(response.ReceiptId, receipt.Id);
        Assert.Equal(seed.ApplicationCase.Id, receipt.CaseId);
        Assert.Equal(
            seed.ApplicationCase.DecisionRevision,
            receipt.ApprovalRevision);
        Assert.Equal(seed.Application.Id, receipt.ApplicationId);
        Assert.Equal(seed.Application.Version, receipt.SelectedRecordVersion);
        Assert.Equal(
            seed.Application.Version + 1,
            receipt.CurrentRecordVersion);
        Assert.Equal(
            Enum.GetValues<WorkspaceStaffOnboardingApplicantField>()
                .Where(field =>
                    field !=
                    WorkspaceStaffOnboardingApplicantField.Unknown)
                .ToArray(),
            receipt.ChangedFields);
        Assert.NotEqual(Guid.Empty, receipt.ApplicantEventId);
        Assert.NotEqual(Guid.Empty, receipt.CompletionEventId);
        Assert.Equal(
            1,
            await workspaces.StaffOnboardingCorrectionReceipts
                .CountAsync()
                .ConfigureAwait(false));
        Assert.False(await workspaces.StaffOnboardingCorrectionReceipts
            .AnyAsync(candidate =>
                candidate.ExecutionId == handoffExecutionId)
            .ConfigureAwait(false));

        Assert.Equal(
            WorkspaceStaffOnboardingState.Provisioning,
            handoff.Status);
        Assert.Equal(seed.HandoffApplication.Version + 1, handoff.Version);
        Assert.Equal(
            seed.HandoffApplication.DisplayName,
            handoff.DisplayName);
        Assert.Equal(
            seed.HandoffApplication.VerifiedAccountEmail,
            handoff.VerifiedAccountEmail);

        Gma.Framework.Messaging.Infrastructure.OutboxMessage outbox =
            await workspaces.OutboxMessages
                .AsNoTracking()
                .SingleAsync(message =>
                    message.EventType ==
                    typeof(
                        DataRightsTenantCorrectionAppliedIntegrationEvent)
                    .FullName)
                .ConfigureAwait(false);
        Assert.NotNull(outbox.ProcessedAtUtc);
        Assert.DoesNotContain(
            "Corrected Applicant",
            outbox.Payload,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "corrected.applicant@example.test",
            outbox.Payload,
            StringComparison.Ordinal);

        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        DataRightsCase completedCase = await dataRights.Cases
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seed.ApplicationCase.Id)
            .ConfigureAwait(false);
        DataRightsCorrectionExecution completedExecution =
            await dataRights.CorrectionExecutions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == executionId)
                .ConfigureAwait(false);
        Assert.Equal(DataRightsCaseState.Completed, completedCase.Status);
        Assert.Equal(
            DataRightsCorrectionExecutionState.Completed,
            completedExecution.State);
        Assert.Equal(response.ReceiptId, completedExecution.ReceiptId);
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
                    .SingleOrDefaultAsync(candidate =>
                        candidate.Id == executionId)
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
            diagnosticScope.ServiceProvider
                .GetRequiredService<DataRightsDbContext>();
        WorkspacesDbContext diagnosticWorkspaces =
            diagnosticScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
        DataRightsCorrectionExecution? observedExecution =
            await diagnosticDataRights.CorrectionExecutions
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == executionId)
                .ConfigureAwait(false);
        Gma.Framework.Messaging.Infrastructure.OutboxMessage?
            observedOutbox =
            await diagnosticWorkspaces.OutboxMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(message =>
                    message.EventType ==
                    typeof(
                        DataRightsTenantCorrectionAppliedIntegrationEvent)
                    .FullName)
                .ConfigureAwait(false);
        throw new TimeoutException(
            "The Workspaces correction proof did not reconcile the case. " +
            $"Execution={observedExecution?.State}; " +
            $"OutboxProcessed={observedOutbox?.ProcessedAtUtc is not null}; " +
            $"OutboxAttempts={observedOutbox?.Attempts ?? 0}; " +
            $"OutboxError={observedOutbox?.Error ?? "<none>"}.");
    }

    private static async Task<CorrectionSeed> SeedApprovedCorrectionsAsync(
        AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        WorkspacesDbContext workspaces =
            scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding application = CreateApplication(
            "account-applicant",
            "verified.applicant@example.test",
            "Original Applicant",
            SeededAtUtc);
        WorkspaceStaffOnboarding handoffApplication = CreateApplication(
            "account-handoff",
            "verified.handoff@example.test",
            "Handoff Applicant",
            SeededAtUtc.AddMinutes(1));
        workspaces.StaffOnboardingApplications.AddRange(
            application,
            handoffApplication);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);

        DataRightsCase applicationCase = CreateApprovedCase(
            application,
            SeededAtUtc.AddMinutes(2));
        DataRightsCase handoffCase = CreateApprovedCase(
            handoffApplication,
            SeededAtUtc.AddMinutes(12));
        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        dataRights.Cases.AddRange(applicationCase, handoffCase);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return new CorrectionSeed(
            application,
            applicationCase,
            handoffApplication,
            handoffCase);
    }

    private static WorkspaceStaffOnboarding CreateApplication(
        string subjectId,
        string verifiedEmail,
        string displayName,
        DateTimeOffset nowUtc) =>
        WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            subjectId,
            verifiedEmail,
            displayName,
            displayName == "Original Applicant"
                ? "Original Legal Name"
                : "Handoff Legal Name",
            displayName == "Original Applicant"
                ? "original.applicant@example.test"
                : "handoff.applicant@example.test",
            "+44 20 1111 0000",
            "EMP-100",
            "Receptionist",
            "Front Office",
            nowUtc).Value;

    private static DataRightsCase CreateApprovedCase(
        WorkspaceStaffOnboarding application,
        DateTimeOffset nowUtc)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.Correction,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:privacy-reviewer",
            nowUtc).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            nowUtc.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            WorkspacesDataRightsCoordinates.Owner,
            WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
            application.Id,
            application.Version,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            nowUtc.AddMinutes(2)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            nowUtc.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            nowUtc.AddMinutes(4)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            nowUtc.AddMinutes(5)).IsSuccess);
        return dataRightsCase;
    }

    private static async Task<DataRightsCorrectionExecutionDto>
        StartCorrectionAsync(
            HttpClient client,
            string accessToken,
            DataRightsCase dataRightsCase,
            Guid executionId)
    {
        using HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
            client,
            TenantId,
            $"/api/data-rights/tenant/cases/{dataRightsCase.Id:D}/correction",
            new
            {
                executionId,
                expectedVersion = dataRightsCase.Version
            },
            accessToken).ConfigureAwait(false);
        return await ReadSuccessAsync<DataRightsCorrectionExecutionDto>(
            response).ConfigureAwait(false);
    }

    private static string BuildTargetPath(
        Guid applicationId,
        Guid executionId,
        DataRightsCase dataRightsCase,
        long expectedVersion) =>
        "/api/workspace-staff-enrollment/data-rights-corrections/" +
        $"{applicationId:D}?executionId={executionId:D}" +
        $"&caseId={dataRightsCase.Id:D}" +
        $"&approvalRevision={dataRightsCase.DecisionRevision!.Value}" +
        $"&expectedVersion={expectedVersion}";

    private static async Task MoveApplicationToProvisioningAsync(
        AuthTestApplication api,
        Guid applicationId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        WorkspacesDbContext workspaces =
            scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding application = await workspaces
            .StaffOnboardingApplications
            .SingleAsync(candidate => candidate.Id == applicationId)
            .ConfigureAwait(false);
        Assert.True(application.ObserveInvitationAccepted(
            SeededAtUtc.AddHours(1)).IsSuccess);
        await workspaces.SaveChangesAsync().ConfigureAwait(false);
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
                    "BunkFy Workspaces correction integration worker",
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
                ["Worker:Modules:AccessControl"] = "true",
                ["Worker:Modules:Auth"] = "true",
                ["Worker:Modules:Organizations"] = "true",
                ["Worker:Modules:Staff"] = "true",
                ["Worker:Modules:DataRights"] = "true",
                ["Tasks:Worker:Enabled"] = "false"
            });
        AuthTestConfiguration.ConfigureTokenHashing(builder.Configuration);
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
            "workspace-privacy-correction-operator"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "roles",
            "grant",
            "--actor",
            "owner",
            "--role",
            "workspace-privacy-correction-operator",
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
            "workspace-privacy-correction-operator",
            "--scope",
            $"tenant:{TenantId}"));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string accessToken)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("X-Tenant-Id", TenantId);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<T> ReadSuccessAsync<T>(
        HttpResponseMessage response)
    {
        (T value, _) =
            await ReadSuccessWithBodyAsync<T>(response).ConfigureAwait(false);
        return value;
    }

    private static async Task<(T Value, string Body)>
        ReadSuccessWithBodyAsync<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync()
            .ConfigureAwait(false);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected success but received {(int)response.StatusCode}. " +
            $"Body: {body}");
        T? value = JsonSerializer.Deserialize<T>(body, JsonOptions);
        return (Assert.IsType<T>(value), body);
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

    private sealed record CorrectionSeed(
        WorkspaceStaffOnboarding Application,
        DataRightsCase ApplicationCase,
        WorkspaceStaffOnboarding HandoffApplication,
        DataRightsCase HandoffCase);

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

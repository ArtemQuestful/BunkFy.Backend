namespace Integration.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Scoping.Infrastructure;
using Gma.Modules.Organizations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class
    WorkspaceStaffHistoricalNoProvisionTransactionIntegrationTests
{
    private const string TenantId =
        "ac000000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        19,
        0,
        0,
        TimeSpan.Zero);
    private static readonly string EvidenceSha256 = new('a', 64);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Dispatcher_commits_atomically_and_rolls_back_after_handler_faults()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_historical_review_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();

        Guid committedApplicationId = Guid.NewGuid();
        Guid committedSourceId = Guid.NewGuid();
        DispatchProbe committedProbe = new();
        await using (ServiceProvider committed = CreateProvider(
            connectionString,
            committedSourceId,
            Guid.NewGuid(),
            DispatchFault.None,
            committedProbe))
        {
            await MigrateActualSchemaAsync(committed).ConfigureAwait(false);
            SeedCoordinate seed = await SeedAsync(
                    committed,
                    committedApplicationId,
                    committedSourceId,
                    "subject:committed")
                .ConfigureAwait(false);
            ReviewWorkspaceStaffHistoricalNoProvisionCommand command =
                CreateCommand(seed, Guid.NewGuid());

            Result<WorkspaceStaffHistoricalNoProvisionDispositionResult>
                first = await SendAsync(committed, command)
                    .ConfigureAwait(false);
            Assert.True(first.IsSuccess, first.Error.Code);
            Assert.False(first.Value.AlreadyReviewed);
            await AssertCommittedAsync(
                    committed,
                    seed,
                    first.Value)
                .ConfigureAwait(false);

            Result<WorkspaceStaffHistoricalNoProvisionDispositionResult>
                replay = await SendAsync(committed, command)
                    .ConfigureAwait(false);
            Assert.True(replay.IsSuccess, replay.Error.Code);
            Assert.True(replay.Value.AlreadyReviewed);
            Assert.Equal(first.Value.ReceiptId, replay.Value.ReceiptId);
            Assert.Equal(first.Value.CanonicalSha256, replay.Value.CanonicalSha256);
            Assert.Equal(1, committedProbe.StaffOutcomeCalls);
            Assert.Equal(1, committedProbe.StaffInspectionCalls);
            Assert.Equal(2, committedProbe.OrganizationSnapshotCalls);
            Assert.Equal(1, committedProbe.OrganizationExportCalls);
        }

        await ProveRollbackAsync(
                connectionString,
                DispatchFault.ResultFailure)
            .ConfigureAwait(false);
        await ProveRollbackAsync(
                connectionString,
                DispatchFault.Exception)
            .ConfigureAwait(false);
    }

    private static async Task ProveRollbackAsync(
        string connectionString,
        DispatchFault fault)
    {
        Guid applicationId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        DispatchProbe probe = new();
        await using ServiceProvider services = CreateProvider(
            connectionString,
            sourceId,
            Guid.NewGuid(),
            fault,
            probe);
        SeedCoordinate seed = await SeedAsync(
                services,
                applicationId,
                sourceId,
                "subject:rollback-" + fault)
            .ConfigureAwait(false);
        ReviewWorkspaceStaffHistoricalNoProvisionCommand command =
            CreateCommand(seed, Guid.NewGuid());

        if (fault == DispatchFault.ResultFailure)
        {
            Result<WorkspaceStaffHistoricalNoProvisionDispositionResult>
                result = await SendAsync(services, command)
                    .ConfigureAwait(false);
            Assert.Equal(InjectedFailureBehavior.Error, result.Error);
        }
        else
        {
            await Assert.ThrowsAsync<InjectedAfterHandlerException>(() =>
                SendAsync(services, command)).ConfigureAwait(false);
        }

        Assert.Equal(1, probe.AfterHandlerCalls);
        Assert.Equal(1, probe.StaffOutcomeCalls);
        Assert.Equal(1, probe.StaffInspectionCalls);
        Assert.Equal(2, probe.OrganizationSnapshotCalls);
        Assert.Equal(1, probe.OrganizationExportCalls);
        await using AsyncServiceScope verification =
            services.CreateAsyncScope();
        verification.ServiceProvider
            .GetRequiredService<IScopeContextAccessor>()
            .SetScope(TenantId);
        WorkspacesDbContext database = verification.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding application = await database
            .StaffOnboardingApplications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == applicationId)
            .ConfigureAwait(false);
        Assert.Equal(seed.Version, application.Version);
        Assert.Equal(seed.Status, application.Status);
        Assert.Equal(seed.SubjectId, application.SubjectId);
        Assert.False(application.HasIdentityAnchorState);
        Assert.False(await database.StaffHistoricalNoProvisionReceipts
            .AsNoTracking()
            .AnyAsync(receipt => receipt.ApplicationId == applicationId)
            .ConfigureAwait(false));
    }

    private static async Task AssertCommittedAsync(
        ServiceProvider services,
        SeedCoordinate seed,
        WorkspaceStaffHistoricalNoProvisionDispositionResult result)
    {
        await using AsyncServiceScope verification =
            services.CreateAsyncScope();
        verification.ServiceProvider
            .GetRequiredService<IScopeContextAccessor>()
            .SetScope(TenantId);
        WorkspacesDbContext database = verification.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding application = await database
            .StaffOnboardingApplications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.ApplicationId)
            .ConfigureAwait(false);
        WorkspaceStaffHistoricalNoProvisionReceipt receipt = await database
            .StaffHistoricalNoProvisionReceipts
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ApplicationId == seed.ApplicationId)
            .ConfigureAwait(false);

        Assert.Equal(seed.Version + 1, application.Version);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Superseded,
            application.Status);
        Assert.Equal(receipt.CreateSubjectPseudonym(), application.SubjectId);
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
        Assert.False(application.HasIdentityAnchorState);
        Assert.Equal(result.ReceiptId, receipt.Id);
        Assert.Equal(result.CanonicalSha256, receipt.CanonicalSha256);
        Assert.True(receipt.HasValidCanonicalProof());
        Assert.Empty(await database.OutboxMessages.AsNoTracking()
            .ToArrayAsync()
            .ConfigureAwait(false));
    }

    private static async Task<SeedCoordinate> SeedAsync(
        ServiceProvider services,
        Guid applicationId,
        Guid sourceId,
        string subjectId)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>()
            .SetScope(TenantId);
        WorkspacesDbContext database = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboarding.Create(
                applicationId,
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                subjectId,
                applicationId.ToString("N") + "@example.test",
                "Historical Review",
                legalName: "Historical Review",
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                Now.AddHours(-1)).Value;
        database.StaffOnboardingApplications.Add(application);
        await database.SaveChangesAsync().ConfigureAwait(false);
        return new(
            application.Id,
            application.SourceId,
            application.SubjectId,
            application.Version,
            application.Status);
    }

    private static ReviewWorkspaceStaffHistoricalNoProvisionCommand
        CreateCommand(
            SeedCoordinate seed,
            Guid operationId) => new(
                operationId,
                seed.ApplicationId,
                seed.Version,
                seed.Status,
                ExpectedOrganizationsScopeRevision: 4,
                ExpectedOrganizationsSourceVersion: 7,
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .InvitationRevoked,
                Guid.NewGuid(),
                EvidenceSha256,
                "operator:historical-review");

    private static async Task<Result<
        WorkspaceStaffHistoricalNoProvisionDispositionResult>> SendAsync(
            ServiceProvider services,
            ReviewWorkspaceStaffHistoricalNoProvisionCommand command)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>()
            .SetScope(TenantId);
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task MigrateActualSchemaAsync(ServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>()
            .SetScope(TenantId);
        await scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>()
            .Database.MigrateAsync()
            .ConfigureAwait(false);
    }

    private static ServiceProvider CreateProvider(
        string connectionString,
        Guid sourceId,
        Guid receiptId,
        DispatchFault fault,
        DispatchProbe probe)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Configuration["Scoping:Enabled"] = "true";
        builder.AddScopingInfrastructure();
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.Services.AddWorkspacesApplication(
            builder.Configuration,
            "global");
        builder.AddWorkspacesPersistence();

        TestStaffEvidence staff = new(probe);
        builder.Services.AddSingleton<
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader>(staff);
        builder.Services.AddSingleton<IStaffIdentityProvisioningAnchorCutover>(
            staff);
        builder.Services.AddSingleton<IOrganizationScopeLifecycle>(
            new TestOrganizations(sourceId, probe));
        builder.Services.Replace(ServiceDescriptor.Singleton<IIdGenerator>(
            new FixedIdGenerator(receiptId)));
        builder.Services.Replace(ServiceDescriptor.Singleton<ISystemClock>(
            new FixedClock()));
        builder.Services.AddSingleton(probe);
        if (fault == DispatchFault.ResultFailure)
        {
            builder.Services.AddScoped<ICommandPipelineBehavior<
                ReviewWorkspaceStaffHistoricalNoProvisionCommand,
                WorkspaceStaffHistoricalNoProvisionDispositionResult>,
                InjectedFailureBehavior>();
        }
        else if (fault == DispatchFault.Exception)
        {
            builder.Services.AddScoped<ICommandPipelineBehavior<
                ReviewWorkspaceStaffHistoricalNoProvisionCommand,
                WorkspaceStaffHistoricalNoProvisionDispositionResult>,
                InjectedExceptionBehavior>();
        }

        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class InjectedFailureBehavior(DispatchProbe probe)
        : ICommandPipelineBehavior<
            ReviewWorkspaceStaffHistoricalNoProvisionCommand,
            WorkspaceStaffHistoricalNoProvisionDispositionResult>
    {
        public static readonly Error Error = new(
            "Workspaces.InjectedHistoricalReviewFailure",
            "The provider proof injects a failure after the handler.");

        public async Task<Result<
            WorkspaceStaffHistoricalNoProvisionDispositionResult>>
            HandleAsync(
                ReviewWorkspaceStaffHistoricalNoProvisionCommand command,
                CommandNext<
                    WorkspaceStaffHistoricalNoProvisionDispositionResult>
                    next,
                CancellationToken cancellationToken)
        {
            Result<WorkspaceStaffHistoricalNoProvisionDispositionResult>
                result = await next().ConfigureAwait(false);
            Assert.True(result.IsSuccess, result.Error.Code);
            probe.AfterHandlerCalls++;
            return Result.Failure<
                WorkspaceStaffHistoricalNoProvisionDispositionResult>(Error);
        }
    }

    private sealed class InjectedExceptionBehavior(DispatchProbe probe)
        : ICommandPipelineBehavior<
            ReviewWorkspaceStaffHistoricalNoProvisionCommand,
            WorkspaceStaffHistoricalNoProvisionDispositionResult>
    {
        public async Task<Result<
            WorkspaceStaffHistoricalNoProvisionDispositionResult>>
            HandleAsync(
                ReviewWorkspaceStaffHistoricalNoProvisionCommand command,
                CommandNext<
                    WorkspaceStaffHistoricalNoProvisionDispositionResult>
                    next,
                CancellationToken cancellationToken)
        {
            Result<WorkspaceStaffHistoricalNoProvisionDispositionResult>
                result = await next().ConfigureAwait(false);
            Assert.True(result.IsSuccess, result.Error.Code);
            probe.AfterHandlerCalls++;
            throw new InjectedAfterHandlerException();
        }
    }

    private sealed class TestStaffEvidence(DispatchProbe probe)
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            IStaffIdentityProvisioningAnchorCutover
    {
        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
                IReadOnlyList<
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                    requests,
                CancellationToken cancellationToken = default)
        {
            probe.StaffOutcomeCalls++;
            return Task.FromResult<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>(
                    requests.Select(request => new
                        StaffWorkspaceOnboardingIdentityAnchorOutcome(
                            request.ApplicationId,
                            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                                .Absent,
                            StaffMemberId: null,
                            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                                .Unknown,
                            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                                .Unknown,
                            WorkspaceApplicationVersion: null,
                            ResolutionDisposition: null,
                            ResolutionEventId: null))
                        .ToArray());
        }

        public Task<StaffIdentityProvisioningAnchorInspection> InspectAsync(
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            probe.StaffInspectionCalls++;
            StaffIdentityProvisioningAnchorCandidate candidate =
                Assert.Single(candidates);
            return Task.FromResult(new StaffIdentityProvisioningAnchorInspection(
                IsSuccess: true,
                [new StaffIdentityProvisioningAnchorCandidateInspection(
                    candidate.SourceKind,
                    candidate.SourceId,
                    StaffIdentityProvisioningAnchorCutoverDisposition
                        .Ambiguous)],
                ErrorCode: null));
        }

        public Task<StaffIdentityProvisioningAnchorApplyResult> ApplyAsync(
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Apply must not be called.");
    }

    private sealed class TestOrganizations(
        Guid sourceId,
        DispatchProbe probe)
        : IOrganizationScopeLifecycle
    {
        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            probe.OrganizationSnapshotCalls++;
            return Task.FromResult(new OrganizationScopeSnapshot(
                OrganizationScopeStatus.Open,
                Revision: 4));
        }

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken)
        {
            probe.OrganizationExportCalls++;
            OrganizationScopeInvitationExportRecord record = new(
                sourceId,
                Guid.Parse(TenantId),
                "subject:inviter",
                RecipientEmail: null,
                TokenVersion: 1,
                Now.AddDays(-1),
                OrganizationInvitationStatus.Revoked,
                AcceptedSubjectId: null,
                AcceptedMembershipId: null,
                AcceptedAtUtc: null,
                Version: 7,
                CreatedBy: "operator",
                CreatedAtUtc: Now.AddYears(-1),
                LastChangedBy: "operator",
                LastChangedAtUtc: Now.AddDays(-1));
            return Task.FromResult(new OrganizationScopeExportPage(
                OrganizationScopeExportStatus.Completed,
                ScopeRevision: 4,
                OrganizationScopeExportStore.Invitations,
                [record],
                "id:" + sourceId.ToString("D"),
                HasMore: false));
        }

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Destroy must not be called.");
    }

    private sealed class FixedIdGenerator(Guid receiptId) : IIdGenerator
    {
        public Guid NewId() => receiptId;
    }

    private sealed class FixedClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class DispatchProbe
    {
        public int AfterHandlerCalls { get; set; }
        public int StaffOutcomeCalls { get; set; }
        public int StaffInspectionCalls { get; set; }
        public int OrganizationSnapshotCalls { get; set; }
        public int OrganizationExportCalls { get; set; }
    }

    private sealed class InjectedAfterHandlerException : Exception;

    private sealed record SeedCoordinate(
        Guid ApplicationId,
        Guid SourceId,
        string SubjectId,
        long Version,
        WorkspaceStaffOnboardingState Status);

    private enum DispatchFault
    {
        None = 0,
        ResultFailure = 1,
        Exception = 2
    }
}

namespace Integration.Tests;

using System.Text.Json;
using BunkFy.Extensions.Workspaces;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.AccessControl;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Cqrs.UnitOfWork;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Scoping.Infrastructure;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using DomainResolutionDisposition =
    BunkFy.Modules.Workspaces.Domain.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition;
using ContractResolutionDisposition =
    BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition;
using DomainRestorationDisposition =
    BunkFy.Modules.Workspaces.Domain.WorkspaceStaffAccessRestorationDisposition;

public sealed class
    WorkspaceStaffOnboardingIdentityAnchorLifecycleIntegrationTests
{
    private const string TenantId =
        "b8000000-0000-0000-0000-000000000001";
    private const string SubjectId = "account-phase-a-anchor";
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        16,
        0,
        0,
        TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Staff_commit_survives_Workspaces_rollback_and_two_pass_delivery_records_exact_resolution()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_phase_a_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        SequentialIdGenerator ids = new();
        PhaseAAccessState access = new();
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            ids,
            access);
        await MigrateActualModuleSchemasAsync(services)
            .ConfigureAwait(false);

        Guid applicationId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        await SeedSubmittedApplicationAsync(
            services,
            applicationId,
            sourceId,
            SubjectId,
            access.ProfileId).ConfigureAwait(false);

        long preStaffApplicationVersion;
        using (IServiceScope preStaffReview = CreateTenantScope(services))
        {
            Result<WorkspaceStaffOnboardingDto> review = await preStaffReview.ServiceProvider
                .GetRequiredService<IRequestDispatcher>()
                .QueryAsync(new BunkFy.Modules.Workspaces.Application.Queries.GetWorkspaceStaffOnboardingReviewQuery(applicationId))
                .ConfigureAwait(false);
            Assert.True(review.IsSuccess, review.Error.Code);
            Assert.Equal("Phase A Applicant", review.Value.DisplayName);
            preStaffApplicationVersion = review.Value.Version;
        }

        Guid rolledBackContinuationId;
        StaffMemberDto provisioned;
        Guid resolutionEventId;
        using (IServiceScope rollbackScope = CreateTenantScope(services))
        {
            ITransactionalUnitOfWork workspaces = GetTransactionalUnitOfWork(
                rollbackScope.ServiceProvider,
                WorkspacesMigrations.Schema);
            await workspaces.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                WorkspacesDbContext dbContext = rollbackScope.ServiceProvider
                    .GetRequiredService<WorkspacesDbContext>();
                WorkspaceStaffOnboarding application = await dbContext
                    .StaffOnboardingApplications.SingleAsync(candidate =>
                        candidate.Id == applicationId).ConfigureAwait(false);
                bool acquired = await rollbackScope.ServiceProvider
                    .GetRequiredService<
                        WorkspaceStaffOnboardingMutationCoordinator>()
                    .AcquireTrackedAsync(
                        application,
                        WorkspaceStaffOnboardingSourceLockMode.Read,
                        CancellationToken.None).ConfigureAwait(false);
                Assert.True(acquired);
                Assert.True(application.ObserveInvitationAccepted(Now)
                    .IsSuccess);

                Result<StaffMemberDto> staffResult = await SendAsync(
                    services,
                    new ProvisionStaffOnboardingCommand(
                        applicationId,
                        SubjectId,
                        "Phase A Applicant",
                        "Phase A Legal",
                        "phase-a@example.test",
                        null,
                        null,
                        "Front desk",
                        "Operations",
                        "integration:workspaces")).ConfigureAwait(false);
                Assert.True(staffResult.IsSuccess, staffResult.Error.Code);
                provisioned = staffResult.Value;
                resolutionEventId = await ReadAnchorResolutionEventIdAsync(
                    services,
                    applicationId).ConfigureAwait(false);

                rolledBackContinuationId = ids.NewId();
                Assert.True(application.MarkStaffReady(
                    provisioned.StaffMemberId,
                    resolutionEventId,
                    rolledBackContinuationId,
                    Now.AddMinutes(1)).IsSuccess);
                await workspaces.SaveChangesAsync().ConfigureAwait(false);
                Assert.Contains(
                    dbContext.OutboxMessages.Local,
                    message => message.Id == rolledBackContinuationId);
            }
            finally
            {
                await workspaces.RollbackTransactionAsync(
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        using (IServiceScope afterRollback = CreateTenantScope(services))
        {
            WorkspacesDbContext dbContext = afterRollback.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            WorkspaceStaffOnboarding application = await dbContext
                .StaffOnboardingApplications.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == applicationId)
                .ConfigureAwait(false);
            Assert.Equal(
                WorkspaceStaffOnboardingState.Submitted,
                application.Status);
            Assert.Null(application.StaffMemberId);
            Assert.Null(application.IdentityAnchorExpectedResolutionEventId);
            Assert.Null(application.IdentityAnchorContinuationEventId);
            Assert.Equal("Phase A Applicant", application.DisplayName);
            Assert.Equal(preStaffApplicationVersion, application.Version);
            Result<WorkspaceStaffOnboardingDto> managerReview = await afterRollback.ServiceProvider
                .GetRequiredService<IRequestDispatcher>()
                .QueryAsync(new BunkFy.Modules.Workspaces.Application.Queries.GetWorkspaceStaffOnboardingReviewQuery(applicationId))
                .ConfigureAwait(false);
            Assert.True(managerReview.IsFailure);
            Assert.Equal(WorkspaceStaffOnboardingApplicationErrors.ProfileReviewUnavailable,
                managerReview.Error);
            // Staff committed in its own real module transaction; Workspaces
            // rolled back every local Staff coordinate and version change.
            // The unchanged staging/version must never authorize disclosure.
            Assert.False(await dbContext.OutboxMessages.AsNoTracking()
                .AnyAsync(message => message.Id == rolledBackContinuationId)
                .ConfigureAwait(false));
        }

        StaffIdentityProvisioningAnchorCreatedIntegrationEvent anchorCreated =
            await ReadOutboxEventAsync<
                StaffIdentityProvisioningAnchorCreatedIntegrationEvent>(
                services,
                StaffModuleMetadata.Name,
                applicationId).ConfigureAwait(false);
        Assert.Equal(resolutionEventId, anchorCreated.ResolutionEventId);

        await DeliverWorkspacesAsync(
            services,
            anchorCreated,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorCreatedHandlerName)
            .ConfigureAwait(false);

        Guid committedContinuationId;
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation;
        using (IServiceScope passOne = CreateTenantScope(services))
        {
            WorkspacesDbContext dbContext = passOne.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            WorkspaceStaffOnboarding application = await dbContext
                .StaffOnboardingApplications.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == applicationId)
                .ConfigureAwait(false);
            Assert.Equal(
                WorkspaceStaffOnboardingState.StaffReady,
                application.Status);
            Assert.Equal(provisioned.StaffMemberId, application.StaffMemberId);
            Assert.Equal(
                resolutionEventId,
                application.IdentityAnchorExpectedResolutionEventId);
            committedContinuationId = Assert.IsType<Guid>(
                application.IdentityAnchorContinuationEventId);
            Assert.NotEqual(rolledBackContinuationId, committedContinuationId);
            Assert.Null(application.DisplayName);
            Assert.Null(application.VerifiedAccountEmail);
            Assert.Null(application.IdentityAnchorResolutionEventId);
            Assert.Equal(
                1,
                await dbContext.OutboxMessages.AsNoTracking().CountAsync(
                    message => message.Id == committedContinuationId)
                    .ConfigureAwait(false));
            continuation = await ReadOutboxEventAsync<
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent>(
                passOne.ServiceProvider,
                WorkspacesModuleMetadata.Name,
                committedContinuationId).ConfigureAwait(false);
        }

        await DeliverWorkspacesAsync(
            services,
            continuation,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorContinuationHandlerName)
            .ConfigureAwait(false);

        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent resolved;
        long completedVersion;
        using (IServiceScope passTwo = CreateTenantScope(services))
        {
            WorkspacesDbContext dbContext = passTwo.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            WorkspaceStaffOnboarding application = await dbContext
                .StaffOnboardingApplications.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == applicationId)
                .ConfigureAwait(false);
            Assert.Equal(
                WorkspaceStaffOnboardingState.Completed,
                application.Status);
            Assert.Equal(
                committedContinuationId,
                application.IdentityAnchorContinuationEventId);
            Assert.Equal(
                resolutionEventId,
                application.IdentityAnchorResolutionEventId);
            Assert.Equal(
                DomainResolutionDisposition
                    .CompletedRedacted,
                application.IdentityAnchorResolutionDisposition);
            Assert.Null(application.IdentityAnchorResolutionObservedAtUtc);
            completedVersion = Assert.IsType<long>(
                application.IdentityAnchorResolutionApplicationVersion);
            Assert.Equal(application.Version, completedVersion);
            Assert.Equal(1, access.ProvisionCountFor(SubjectId));
            Assert.Equal(
                1,
                await dbContext.OutboxMessages.AsNoTracking().CountAsync(
                    message => message.Id == resolutionEventId)
                    .ConfigureAwait(false));
            resolved = await ReadOutboxEventAsync<
                WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent>(
                passTwo.ServiceProvider,
                WorkspacesModuleMetadata.Name,
                resolutionEventId).ConfigureAwait(false);
        }

        await DeliverWorkspacesAsync(
            services,
            continuation,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorContinuationHandlerName)
            .ConfigureAwait(false);
        using (IServiceScope replay = CreateTenantScope(services))
        {
            WorkspacesDbContext dbContext = replay.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            Assert.Equal(
                1,
                await dbContext.OutboxMessages.AsNoTracking().CountAsync(
                    message => message.Id == committedContinuationId)
                    .ConfigureAwait(false));
            Assert.Equal(
                1,
                await dbContext.OutboxMessages.AsNoTracking().CountAsync(
                    message => message.Id == resolutionEventId)
                    .ConfigureAwait(false));
        }

        await DeliverStaffExtensionAsync(services, resolved)
            .ConfigureAwait(false);
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome =
            await ReadStaffOutcomeAsync(
                services,
                applicationId,
                SubjectId).ConfigureAwait(false);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved,
            outcome.Status);
        Assert.Equal(completedVersion, outcome.WorkspaceApplicationVersion);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            outcome.ResolutionDisposition);
        Assert.Equal(resolutionEventId, outcome.ResolutionEventId);

        using IServiceScope finalScope = CreateTenantScope(services);
        WorkspaceStaffOnboarding finalApplication = await finalScope
            .ServiceProvider.GetRequiredService<WorkspacesDbContext>()
            .StaffOnboardingApplications.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == applicationId)
            .ConfigureAwait(false);
        Assert.Null(finalApplication.IdentityAnchorResolutionObservedAtUtc);

        long activeVersion = await ReadStaffVersionAsync(
            services,
            provisioned.StaffMemberId).ConfigureAwait(false);
        Result<StaffMemberMutationReceiptDto> suspended = await SendAsync(
            services,
            new SuspendStaffMemberCommand(
                Guid.NewGuid(),
                provisioned.StaffMemberId,
                "Phase A positive restoration proof",
                activeVersion,
                "integration:test")).ConfigureAwait(false);
        Assert.True(suspended.IsSuccess, suspended.Error.Code);
        Assert.Empty(access.TargetsFor(SubjectId));
        WorkspaceStaffAccessProcess suspension = await ReadAccessProcessAsync(
            services,
            provisioned.StaffMemberId,
            suspended.Value.Version).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffAccessTargetState.Suspended,
            suspension.TargetState);
        Assert.Equal(
            WorkspaceStaffAccessProcessState.AwaitingStaffCommit,
            suspension.State);
        Assert.Single(suspension.ProfileSnapshots);
        await DeliverStaffLifecycleAsync(
            services,
            provisioned.StaffMemberId,
            suspended.Value.Version).ConfigureAwait(false);

        Result<StaffMemberMutationReceiptDto> resumed = await SendAsync(
            services,
            new ResumeStaffMemberCommand(
                Guid.NewGuid(),
                provisioned.StaffMemberId,
                "Phase A positive restoration proof",
                suspended.Value.Version,
                "integration:test")).ConfigureAwait(false);
        Assert.True(resumed.IsSuccess, resumed.Error.Code);
        WorkspaceStaffAccessProcess restoration = await ReadAccessProcessAsync(
            services,
            provisioned.StaffMemberId,
            resumed.Value.Version).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffAccessTargetState.Active,
            restoration.TargetState);
        Assert.Equal(
            DomainRestorationDisposition.RestoreSnapshot,
            restoration.RestorationDisposition);
        Assert.Single(restoration.ProfileSnapshots);
        await DeliverStaffLifecycleAsync(
            services,
            provisioned.StaffMemberId,
            resumed.Value.Version).ConfigureAwait(false);
        WorkspaceStaffAccessProcess completedRestoration =
            await ReadAccessProcessAsync(
                services,
                provisioned.StaffMemberId,
                resumed.Value.Version).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffAccessProcessState.Completed,
            completedRestoration.State);
        Assert.Single(access.TargetsFor(SubjectId));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Unresolved_resume_is_blocked_then_negative_resolution_completes_suppressed_without_restore()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_resume_guard_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        SequentialIdGenerator ids = new();
        PhaseAAccessState access = new();
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            ids,
            access);
        await MigrateActualModuleSchemasAsync(services)
            .ConfigureAwait(false);

        AnchoredPassOne seeded = await SeedAnchoredPassOneAsync(
            services,
            access,
            SubjectId,
            SubjectId).ConfigureAwait(false);
        access.SeedTargets(
            SubjectId,
            new AccessProfileAssignmentTarget(
                access.ProfileId,
                WorkspaceAccessScopes.Create(TenantId)));

        long activeVersion = await ReadStaffVersionAsync(
            services,
            seeded.StaffMemberId).ConfigureAwait(false);
        Result<StaffMemberMutationReceiptDto> suspended = await SendAsync(
            services,
            new SuspendStaffMemberCommand(
                Guid.NewGuid(),
                seeded.StaffMemberId,
                "Unresolved anchor resume guard",
                activeVersion,
                "integration:test")).ConfigureAwait(false);
        Assert.True(suspended.IsSuccess, suspended.Error.Code);
        Assert.Empty(access.TargetsFor(SubjectId));

        Result<StaffMemberMutationReceiptDto> blockedResume = await SendAsync(
            services,
            new ResumeStaffMemberCommand(
                Guid.NewGuid(),
                seeded.StaffMemberId,
                "Must remain suspended until resolution",
                suspended.Value.Version,
                "integration:test")).ConfigureAwait(false);
        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorResolutionRequired,
            blockedResume.Error);
        Assert.Equal(
            StaffStatus.Suspended,
            await ReadStaffStatusAsync(
                services,
                seeded.StaffMemberId).ConfigureAwait(false));
        Assert.False(await HasAccessProcessAsync(
            services,
            seeded.StaffMemberId,
            suspended.Value.Version + 1).ConfigureAwait(false));
        await AssertRawResumeRejectedAsync(
            postgreSql.GetConnectionString(),
            seeded.StaffMemberId).ConfigureAwait(false);

        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            suspended.Value.Version).ConfigureAwait(false);
        await DeliverWorkspacesAsync(
            services,
            seeded.Continuation,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorContinuationHandlerName)
            .ConfigureAwait(false);

        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent
            resolution = await ReadOutboxEventAsync<
                WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent>(
                services,
                WorkspacesModuleMetadata.Name,
                seeded.ResolutionEventId).ConfigureAwait(false);
        Assert.Equal(
            ContractResolutionDisposition
                .SupersededRedacted,
            resolution.Disposition);
        await DeliverStaffExtensionAsync(services, resolution)
            .ConfigureAwait(false);

        int provisionsBeforeResume = access.ProvisionCountFor(SubjectId);
        Result<StaffMemberMutationReceiptDto> resumed = await SendAsync(
            services,
            new ResumeStaffMemberCommand(
                Guid.NewGuid(),
                seeded.StaffMemberId,
                "Resolved negative onboarding remains suppressed",
                suspended.Value.Version,
                "integration:test")).ConfigureAwait(false);
        Assert.True(resumed.IsSuccess, resumed.Error.Code);
        WorkspaceStaffAccessProcess suppressed = await ReadAccessProcessAsync(
            services,
            seeded.StaffMemberId,
            resumed.Value.Version).ConfigureAwait(false);
        Assert.Equal(
            DomainRestorationDisposition.Suppressed,
            suppressed.RestorationDisposition);
        Assert.Empty(suppressed.ProfileSnapshots);
        Assert.Equal(
            WorkspaceStaffAccessProcessState.AwaitingStaffCommit,
            suppressed.State);
        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            resumed.Value.Version).ConfigureAwait(false);
        WorkspaceStaffAccessProcess completed = await ReadAccessProcessAsync(
            services,
            seeded.StaffMemberId,
            resumed.Value.Version).ConfigureAwait(false);
        Assert.Equal(WorkspaceStaffAccessProcessState.Completed, completed.State);
        Assert.Empty(access.TargetsFor(SubjectId));
        Assert.Equal(
            provisionsBeforeResume,
            access.ProvisionCountFor(SubjectId));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Suspension_commit_prevents_precommit_grants_and_converges_without_deadlock()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_suspend_commit_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        SequentialIdGenerator ids = new();
        PhaseAAccessState access = new();
        BlockingLifecyclePolicy blocker = new(
            StaffLifecyclePolicyDecision.Allowed);
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            ids,
            access,
            blocker);
        await MigrateActualModuleSchemasAsync(services)
            .ConfigureAwait(false);

        ProvisionedAnchor seeded = await SeedProvisionedAnchorAsync(
            services,
            access,
            SubjectId,
            SubjectId).ConfigureAwait(false);
        access.SeedTargets(
            SubjectId,
            new AccessProfileAssignmentTarget(
                access.ProfileId,
                WorkspaceAccessScopes.Create(TenantId)));
        SuspendStaffMemberCommand suspend = new(
            Guid.NewGuid(),
            seeded.StaffMemberId,
            "Serialize onboarding continuation against suspension",
            seeded.StaffVersion,
            "integration:test");
        Task<Result<StaffMemberMutationReceiptDto>> suspensionTask =
            SendAsync(services, suspend);
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        Assert.Equal(
            StaffStatus.Active,
            await ReadStaffStatusAsync(
                services,
                seeded.StaffMemberId).ConfigureAwait(false));
        WorkspaceStaffAccessProcess open = await ReadAccessProcessAsync(
            services,
            seeded.StaffMemberId,
            seeded.StaffVersion + 1).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffAccessProcessState.AwaitingStaffCommit,
            open.State);
        Assert.Empty(access.TargetsFor(SubjectId));

        await DeliverWorkspacesAsync(
            services,
            seeded.AnchorCreated,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorCreatedHandlerName)
            .ConfigureAwait(false);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = await ReadContinuationAsync(
                services,
                seeded.ApplicationId).ConfigureAwait(false);
        WorkspaceStaffOnboarding passOne = await ReadApplicationAsync(
            services,
            seeded.ApplicationId).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffOnboardingState.StaffReady,
            passOne.Status);
        Assert.Null(passOne.DisplayName);
        Assert.Null(passOne.IdentityAnchorResolutionEventId);
        Assert.Equal(0, access.ProvisionCountFor(SubjectId));

        Task continuationTask = DeliverWorkspacesAsync(
            services,
            continuation,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorContinuationHandlerName);
        await AssertNoGrantBeforeStaffCommitAsync(
            services, access, passOne.Id, seeded.StaffMemberId,
            SubjectId, continuationTask).ConfigureAwait(false);

        blocker.Release();
        Result<StaffMemberMutationReceiptDto> suspended =
            await suspensionTask.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        Assert.True(suspended.IsSuccess, suspended.Error.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await continuationTask.ConfigureAwait(false));
        WorkspaceStaffOnboarding afterPending = await ReadApplicationAsync(
            services,
            seeded.ApplicationId).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffOnboardingState.StaffReady,
            afterPending.Status);
        Assert.Null(afterPending.IdentityAnchorResolutionEventId);
        Assert.Equal(0, access.ProvisionCountFor(SubjectId));

        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            suspended.Value.Version).ConfigureAwait(false);
        await DeliverWorkspacesAsync(
            services,
            continuation,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorContinuationHandlerName)
            .ConfigureAwait(false);
        WorkspaceStaffOnboarding terminal = await ReadApplicationAsync(
            services,
            seeded.ApplicationId).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Superseded,
            terminal.Status);
        Assert.Equal(
            DomainResolutionDisposition.SupersededRedacted,
            terminal.IdentityAnchorResolutionDisposition);
        Assert.Equal(0, access.ProvisionCountFor(SubjectId));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Aborted_Staff_transition_preserves_durable_denial_and_exact_retry_commits_the_saga()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_suspend_retry_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        SequentialIdGenerator ids = new();
        PhaseAAccessState access = new();
        BlockingLifecyclePolicy blocker = new(
            StaffLifecyclePolicyDecision.RetryRequired);
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            ids,
            access,
            blocker);
        await MigrateActualModuleSchemasAsync(services)
            .ConfigureAwait(false);

        ProvisionedAnchor seeded = await SeedProvisionedAnchorAsync(
            services,
            access,
            SubjectId,
            SubjectId).ConfigureAwait(false);
        access.SeedTargets(
            SubjectId,
            new AccessProfileAssignmentTarget(
                access.ProfileId,
                WorkspaceAccessScopes.Create(TenantId)));
        SuspendStaffMemberCommand suspend = new(
            Guid.NewGuid(),
            seeded.StaffMemberId,
            "Retry exact suspended-access saga",
            seeded.StaffVersion,
            "integration:test");
        Task<Result<StaffMemberMutationReceiptDto>> firstAttempt =
            SendAsync(services, suspend);
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await DeliverWorkspacesAsync(
            services,
            seeded.AnchorCreated,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorCreatedHandlerName)
            .ConfigureAwait(false);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = await ReadContinuationAsync(
                services,
                seeded.ApplicationId).ConfigureAwait(false);
        Task continuationTask = DeliverWorkspacesAsync(
            services,
            continuation,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorContinuationHandlerName);
        await AssertNoGrantBeforeStaffCommitAsync(
            services, access, seeded.ApplicationId, seeded.StaffMemberId,
            SubjectId, continuationTask).ConfigureAwait(false);

        blocker.Release();
        Result<StaffMemberMutationReceiptDto> aborted =
            await firstAttempt.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        Assert.Equal(
            StaffApplicationErrors.LifecycleCoordinationPending,
            aborted.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await continuationTask.ConfigureAwait(false));
        Assert.Equal(
            StaffStatus.Active,
            await ReadStaffStatusAsync(
                services,
                seeded.StaffMemberId).ConfigureAwait(false));
        WorkspaceStaffAccessProcess durableDenial =
            await ReadAccessProcessAsync(
                services,
                seeded.StaffMemberId,
                seeded.StaffVersion + 1).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffAccessProcessState.AwaitingStaffCommit,
            durableDenial.State);
        Assert.Empty(access.TargetsFor(SubjectId));
        Assert.Equal(
            1,
            access.MembershipTransitionCount(
                SubjectId,
                OrganizationMembershipStatus.Suspended));

        Result<StaffMemberMutationReceiptDto> retried = await SendAsync(
            services,
            suspend).ConfigureAwait(false);
        Assert.True(retried.IsSuccess, retried.Error.Code);
        Assert.Equal(
            1,
            access.MembershipTransitionCount(
                SubjectId,
                OrganizationMembershipStatus.Suspended));
        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            retried.Value.Version).ConfigureAwait(false);
        await DeliverWorkspacesAsync(
            services,
            continuation,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorContinuationHandlerName)
            .ConfigureAwait(false);
        WorkspaceStaffOnboarding terminal = await ReadApplicationAsync(
            services,
            seeded.ApplicationId).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Superseded,
            terminal.Status);
        Assert.NotNull(terminal.IdentityAnchorResolutionEventId);
        Assert.Equal(0, access.ProvisionCountFor(SubjectId));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Historical_subject_mismatch_denies_old_only_and_blocks_later_subject_change()
    {
        const string oldSubjectId = "account-phase-a-historical-old";
        const string currentSubjectId = "account-phase-a-current";
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_mismatch_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        SequentialIdGenerator ids = new();
        PhaseAAccessState access = new();
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            ids,
            access);
        await MigrateActualModuleSchemasAsync(services)
            .ConfigureAwait(false);

        ProvisionedAnchor seeded = await SeedProvisionedAnchorAsync(
            services,
            access,
            oldSubjectId,
            currentSubjectId).ConfigureAwait(false);
        AccessProfileAssignmentTarget target = new(
            access.ProfileId,
            WorkspaceAccessScopes.Create(TenantId));
        access.SeedTargets(oldSubjectId, target);
        access.SeedTargets(currentSubjectId, target);

        await DeliverWorkspacesAsync(
            services,
            seeded.AnchorCreated,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorCreatedHandlerName)
            .ConfigureAwait(false);
        WorkspaceStaffOnboarding mismatch = await ReadApplicationAsync(
            services,
            seeded.ApplicationId).ConfigureAwait(false);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Superseded,
            mismatch.Status);
        Assert.Equal(
            DomainResolutionDisposition.SupersededRedacted,
            mismatch.IdentityAnchorResolutionDisposition);
        Assert.Empty(access.TargetsFor(oldSubjectId));
        Assert.Single(access.TargetsFor(currentSubjectId));

        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent
            mismatchResolution = await ReadOutboxEventAsync<
                WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent>(
                services,
                WorkspacesModuleMetadata.Name,
                seeded.ResolutionEventId).ConfigureAwait(false);
        await DeliverStaffExtensionAsync(services, mismatchResolution)
            .ConfigureAwait(false);

        Result<StaffMemberMutationReceiptDto> currentSuspension =
            await SendAsync(
                services,
                new SuspendStaffMemberCommand(
                    Guid.NewGuid(),
                    seeded.StaffMemberId,
                    "Capture the legitimate current-subject snapshot",
                    await ReadStaffVersionAsync(
                        services,
                        seeded.StaffMemberId).ConfigureAwait(false),
                    "integration:test")).ConfigureAwait(false);
        Assert.True(
            currentSuspension.IsSuccess,
            currentSuspension.Error.Code);
        Assert.Empty(access.TargetsFor(currentSubjectId));
        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            currentSuspension.Value.Version).ConfigureAwait(false);

        Result<StaffMemberMutationReceiptDto> currentResume = await SendAsync(
            services,
            new ResumeStaffMemberCommand(
                Guid.NewGuid(),
                seeded.StaffMemberId,
                "Restore the legitimate current-subject snapshot",
                currentSuspension.Value.Version,
                "integration:test")).ConfigureAwait(false);
        Assert.True(currentResume.IsSuccess, currentResume.Error.Code);
        WorkspaceStaffAccessProcess currentRestoration =
            await ReadAccessProcessAsync(
                services,
                seeded.StaffMemberId,
                currentResume.Value.Version).ConfigureAwait(false);
        Assert.Equal(
            DomainRestorationDisposition.RestoreSnapshot,
            currentRestoration.RestorationDisposition);
        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            currentResume.Value.Version).ConfigureAwait(false);
        Assert.Single(access.TargetsFor(currentSubjectId));
        Assert.Empty(access.TargetsFor(oldSubjectId));

        Result<StaffMemberMutationReceiptDto> relinkSuspension =
            await SendAsync(
                services,
                new SuspendStaffMemberCommand(
                    Guid.NewGuid(),
                    seeded.StaffMemberId,
                    "Suspend before the governed identity relink",
                    currentResume.Value.Version,
                    "integration:test")).ConfigureAwait(false);
        Assert.True(
            relinkSuspension.IsSuccess,
            relinkSuspension.Error.Code);
        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            relinkSuspension.Value.Version).ConfigureAwait(false);
        Assert.Empty(access.TargetsFor(currentSubjectId));

        Guid unlinkOperationId = Guid.NewGuid();
        int outboxCountBeforeUnlink = await ReadStaffOutboxCountAsync(services)
            .ConfigureAwait(false);
        Result<StaffMemberMutationReceiptDto> unlinked = await SendAsync(
            services,
            new SetStaffAuthSubjectCommand(
                unlinkOperationId,
                seeded.StaffMemberId,
                null,
                relinkSuspension.Value.Version,
                "integration:test")).ConfigureAwait(false);
        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorAccessClosureRequired,
            unlinked.Error);
        Assert.Equal(
            currentSubjectId,
            await ReadStaffAuthSubjectAsync(
                services,
                seeded.StaffMemberId).ConfigureAwait(false));
        await AssertStaffOperationAbsentAsync(
            services,
            unlinkOperationId).ConfigureAwait(false);
        Assert.Equal(
            outboxCountBeforeUnlink,
            await ReadStaffOutboxCountAsync(services).ConfigureAwait(false));

        Result<StaffMemberMutationReceiptDto> currentResumeAgain =
            await SendAsync(
                services,
                new ResumeStaffMemberCommand(
                    Guid.NewGuid(),
                    seeded.StaffMemberId,
                    "Restore the still-linked current subject",
                    relinkSuspension.Value.Version,
                    "integration:test")).ConfigureAwait(false);
        Assert.True(
            currentResumeAgain.IsSuccess,
            currentResumeAgain.Error.Code);
        WorkspaceStaffAccessProcess currentRestorationAgain =
            await ReadAccessProcessAsync(
                services,
                seeded.StaffMemberId,
                currentResumeAgain.Value.Version).ConfigureAwait(false);
        Assert.Equal(
            DomainRestorationDisposition.RestoreSnapshot,
            currentRestorationAgain.RestorationDisposition);
        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            currentResumeAgain.Value.Version).ConfigureAwait(false);
        Assert.Empty(access.TargetsFor(oldSubjectId));
        Assert.Single(access.TargetsFor(currentSubjectId));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Resolved_anchor_blocks_subject_relink_and_preserves_existing_access()
    {
        const string oldSubjectId = "account-phase-a-resolved-old";
        const string newSubjectId = "account-phase-a-resolved-new";
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_resolved_relink_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        SequentialIdGenerator ids = new();
        PhaseAAccessState access = new();
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            ids,
            access);
        await MigrateActualModuleSchemasAsync(services)
            .ConfigureAwait(false);

        AnchoredPassOne seeded = await SeedAnchoredPassOneAsync(
            services,
            access,
            oldSubjectId,
            oldSubjectId).ConfigureAwait(false);
        await DeliverWorkspacesAsync(
            services,
            seeded.Continuation,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorContinuationHandlerName)
            .ConfigureAwait(false);
        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent
            resolution = await ReadOutboxEventAsync<
                WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent>(
                services,
                WorkspacesModuleMetadata.Name,
                seeded.ResolutionEventId).ConfigureAwait(false);
        await DeliverStaffExtensionAsync(services, resolution)
            .ConfigureAwait(false);
        await DeliverWorkspacesAsync(
            services,
            seeded.Continuation,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorContinuationHandlerName)
            .ConfigureAwait(false);

        Assert.Single(access.TargetsFor(oldSubjectId));
        AccessProfileAssignmentTarget newSubjectTarget = new(
            Guid.NewGuid(),
            WorkspaceAccessScopes.Create(TenantId));
        access.SeedTargets(newSubjectId, newSubjectTarget);
        await AssertRawAuthSubjectChangeRejectedAsync(
            postgreSql.GetConnectionString(),
            seeded.StaffMemberId,
            null).ConfigureAwait(false);
        Assert.Equal(
            oldSubjectId,
            await ReadStaffAuthSubjectAsync(
                services,
                seeded.StaffMemberId).ConfigureAwait(false));
        Assert.Single(access.TargetsFor(oldSubjectId));
        Assert.Equal(
            [newSubjectTarget],
            access.TargetsFor(newSubjectId));

        Result<StaffMemberMutationReceiptDto> suspended = await SendAsync(
            services,
            new SuspendStaffMemberCommand(
                Guid.NewGuid(),
                seeded.StaffMemberId,
                "Prepare a governed account-link change",
                await ReadStaffVersionAsync(
                    services,
                    seeded.StaffMemberId).ConfigureAwait(false),
                "integration:test")).ConfigureAwait(false);
        Assert.True(suspended.IsSuccess, suspended.Error.Code);
        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            suspended.Value.Version).ConfigureAwait(false);
        Assert.Empty(access.TargetsFor(oldSubjectId));

        Guid unlinkOperationId = Guid.NewGuid();
        int outboxCountBeforeUnlink = await ReadStaffOutboxCountAsync(services)
            .ConfigureAwait(false);
        Result<StaffMemberMutationReceiptDto> unlinked = await SendAsync(
            services,
            new SetStaffAuthSubjectCommand(
                unlinkOperationId,
                seeded.StaffMemberId,
                null,
                suspended.Value.Version,
                "integration:test")).ConfigureAwait(false);
        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorAccessClosureRequired,
            unlinked.Error);
        Assert.Equal(
            oldSubjectId,
            await ReadStaffAuthSubjectAsync(
                services,
                seeded.StaffMemberId).ConfigureAwait(false));
        await AssertStaffOperationAbsentAsync(
            services,
            unlinkOperationId).ConfigureAwait(false);
        Assert.Equal(
            outboxCountBeforeUnlink,
            await ReadStaffOutboxCountAsync(services).ConfigureAwait(false));

        Result<StaffMemberMutationReceiptDto> resumed = await SendAsync(
            services,
            new ResumeStaffMemberCommand(
                Guid.NewGuid(),
                seeded.StaffMemberId,
                "Keep the existing account link",
                suspended.Value.Version,
                "integration:test")).ConfigureAwait(false);
        Assert.True(resumed.IsSuccess, resumed.Error.Code);
        WorkspaceStaffAccessProcess restoration = await ReadAccessProcessAsync(
            services,
            seeded.StaffMemberId,
            resumed.Value.Version).ConfigureAwait(false);
        Assert.Equal(
            DomainRestorationDisposition.RestoreSnapshot,
            restoration.RestorationDisposition);
        await DeliverStaffLifecycleAsync(
            services,
            seeded.StaffMemberId,
            resumed.Value.Version).ConfigureAwait(false);

        Assert.Single(access.TargetsFor(oldSubjectId));
        Assert.Equal(
            [newSubjectTarget],
            access.TargetsFor(newSubjectId));
    }

    private static async Task<AnchoredPassOne> SeedAnchoredPassOneAsync(
        ServiceProvider services,
        PhaseAAccessState access,
        string applicationSubjectId,
        string staffSubjectId)
    {
        ProvisionedAnchor provisioned = await SeedProvisionedAnchorAsync(
            services,
            access,
            applicationSubjectId,
            staffSubjectId).ConfigureAwait(false);
        await DeliverWorkspacesAsync(
            services,
            provisioned.AnchorCreated,
            WorkspacesModuleMetadata
                .StaffOnboardingIdentityAnchorCreatedHandlerName)
            .ConfigureAwait(false);

        using IServiceScope scope = CreateTenantScope(services);
        WorkspaceStaffOnboarding application = await scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>()
            .StaffOnboardingApplications.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == provisioned.ApplicationId)
            .ConfigureAwait(false);
        Guid continuationEventId = Assert.IsType<Guid>(
            application.IdentityAnchorContinuationEventId);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            continuation = await ReadOutboxEventAsync<
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent>(
                scope.ServiceProvider,
                WorkspacesModuleMetadata.Name,
                continuationEventId).ConfigureAwait(false);
        return new AnchoredPassOne(
            provisioned.ApplicationId,
            provisioned.StaffMemberId,
            provisioned.ResolutionEventId,
            continuation);
    }

    private static async Task<ProvisionedAnchor> SeedProvisionedAnchorAsync(
        ServiceProvider services,
        PhaseAAccessState access,
        string applicationSubjectId,
        string staffSubjectId)
    {
        access.SeedMembership(staffSubjectId);
        Guid applicationId = Guid.NewGuid();
        await SeedSubmittedApplicationAsync(
            services,
            applicationId,
            Guid.NewGuid(),
            applicationSubjectId,
            access.ProfileId).ConfigureAwait(false);
        Result<StaffMemberDto> provisioned = await SendAsync(
            services,
            new ProvisionStaffOnboardingCommand(
                applicationId,
                staffSubjectId,
                "Phase A Applicant",
                "Phase A Legal",
                "phase-a@example.test",
                null,
                null,
                "Front desk",
                "Operations",
                "integration:workspaces")).ConfigureAwait(false);
        Assert.True(provisioned.IsSuccess, provisioned.Error.Code);
        Guid resolutionEventId = await ReadAnchorResolutionEventIdAsync(
            services,
            applicationId).ConfigureAwait(false);
        StaffIdentityProvisioningAnchorCreatedIntegrationEvent anchorCreated =
            await ReadOutboxEventAsync<
                StaffIdentityProvisioningAnchorCreatedIntegrationEvent>(
                services,
                StaffModuleMetadata.Name,
                applicationId).ConfigureAwait(false);
        return new ProvisionedAnchor(
            applicationId,
            provisioned.Value.StaffMemberId,
            provisioned.Value.Version,
            resolutionEventId,
            anchorCreated);
    }

    private static async Task SeedSubmittedApplicationAsync(
        ServiceProvider services,
        Guid applicationId,
        Guid sourceId,
        string subjectId,
        Guid profileId)
    {
        services.GetRequiredService<PhaseAAccessState>()
            .SeedMembership(subjectId);
        using IServiceScope scope = CreateTenantScope(services);
        WorkspacesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboarding.Create(
            applicationId,
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            sourceId,
            subjectId,
            "phase-a-account@example.test",
            "Phase A Applicant",
            "Phase A Legal",
            "phase-a@example.test",
            null,
            null,
            "Front desk",
            "Operations",
            Now).Value;
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            sourceId,
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            profileId,
            WorkspaceAccessProfileSeeds.FrontDeskKey,
            [],
            "account-owner",
            Now).Value;
        Assert.True(plan.Activate(Now.AddSeconds(1)).IsSuccess);
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
                TenantId,
                applicationId,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                Now).Value;
        dbContext.StaffOnboardingApplications.Add(application);
        dbContext.StaffAccessPlans.Add(plan);
        dbContext.StaffOnboardingProcessingRestrictionProjections.Add(
            projection);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AssertNoGrantBeforeStaffCommitAsync(
        ServiceProvider services,
        PhaseAAccessState access,
        Guid applicationId,
        Guid staffMemberId,
        string subjectId,
        Task continuationTask)
    {
        Task completed = await Task.WhenAny(continuationTask,
            Task.Delay(TimeSpan.FromMilliseconds(300))).ConfigureAwait(false);
        if (ReferenceEquals(completed, continuationTask))
        {
            // Current Organizations membership denial can reject before the
            // lifecycle fence is reached. This exact rejection is safe; a
            // successful continuation or any unrelated fault is not.
            InvalidOperationException denied =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    continuationTask).ConfigureAwait(false);
            Assert.Equal(
                "Identity-anchor continuation failed with 'Workspaces.StaffOnboardingMembershipStateConflict'.",
                denied.Message);
        }

        Assert.Equal(StaffStatus.Active,
            await ReadStaffStatusAsync(services, staffMemberId).ConfigureAwait(false));
        OrganizationMembershipSnapshotDto? membership = await services
            .GetRequiredService<IOrganizationMembershipReader>()
            .FindAsync(Guid.Parse(TenantId), subjectId).ConfigureAwait(false);
        Assert.Equal(OrganizationMembershipStatus.Suspended,
            membership?.Membership?.Status);
        Assert.Equal(0, access.ProvisionCountFor(subjectId));
        Assert.Empty(access.TargetsFor(subjectId));
        WorkspaceStaffOnboarding application = await ReadApplicationAsync(
            services, applicationId).ConfigureAwait(false);
        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Null(application.FailureCode);
        Assert.Null(application.IdentityAnchorResolutionEventId);
    }

    private static async Task<Guid> ReadAnchorResolutionEventIdAsync(
        ServiceProvider services,
        Guid applicationId)
    {
        using IServiceScope scope = CreateTenantScope(services);
        StaffIdentityProvisioningAnchor anchor = await scope.ServiceProvider
            .GetRequiredService<StaffDbContext>()
            .IdentityProvisioningAnchors.AsNoTracking()
            .SingleAsync(candidate =>
                candidate.SourceKind ==
                    StaffIdentityProvisioningSourceKind.WorkspaceOnboarding &&
                candidate.SourceId == applicationId)
            .ConfigureAwait(false);
        return Assert.IsType<Guid>(anchor.ResolutionEventId);
    }

    private static async Task<long> ReadStaffVersionAsync(
        ServiceProvider services,
        Guid staffMemberId)
    {
        using IServiceScope scope = CreateTenantScope(services);
        return await scope.ServiceProvider.GetRequiredService<StaffDbContext>()
            .StaffMembers.AsNoTracking()
            .Where(candidate => candidate.Id == staffMemberId)
            .Select(candidate => candidate.Version)
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<StaffStatus> ReadStaffStatusAsync(
        ServiceProvider services,
        Guid staffMemberId)
    {
        using IServiceScope scope = CreateTenantScope(services);
        return (StaffStatus)await scope.ServiceProvider
            .GetRequiredService<StaffDbContext>()
            .StaffMembers.AsNoTracking()
            .Where(candidate => candidate.Id == staffMemberId)
            .Select(candidate => (int)candidate.Status)
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<string?> ReadStaffAuthSubjectAsync(
        ServiceProvider services,
        Guid staffMemberId)
    {
        using IServiceScope scope = CreateTenantScope(services);
        return await scope.ServiceProvider
            .GetRequiredService<StaffDbContext>()
            .StaffMembers.AsNoTracking()
            .Where(candidate => candidate.Id == staffMemberId)
            .Select(candidate => candidate.AuthSubjectId)
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<int> ReadStaffOutboxCountAsync(
        ServiceProvider services)
    {
        using IServiceScope scope = CreateTenantScope(services);
        return await scope.ServiceProvider
            .GetRequiredService<StaffDbContext>()
            .OutboxMessages.AsNoTracking()
            .CountAsync().ConfigureAwait(false);
    }

    private static async Task AssertStaffOperationAbsentAsync(
        ServiceProvider services,
        Guid operationId)
    {
        using IServiceScope scope = CreateTenantScope(services);
        Assert.False(await scope.ServiceProvider
            .GetRequiredService<StaffDbContext>()
            .MemberMutationOperations.AsNoTracking()
            .AnyAsync(candidate => candidate.Id == operationId)
            .ConfigureAwait(false));
    }

    private static async Task<WorkspaceStaffOnboarding> ReadApplicationAsync(
        ServiceProvider services,
        Guid applicationId)
    {
        using IServiceScope scope = CreateTenantScope(services);
        return await scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>()
            .StaffOnboardingApplications.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == applicationId)
            .ConfigureAwait(false);
    }

    private static async Task<
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent>
        ReadContinuationAsync(
            ServiceProvider services,
            Guid applicationId)
    {
        using IServiceScope scope = CreateTenantScope(services);
        WorkspaceStaffOnboarding application = await scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>()
            .StaffOnboardingApplications.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == applicationId)
            .ConfigureAwait(false);
        Guid continuationEventId = Assert.IsType<Guid>(
            application.IdentityAnchorContinuationEventId);
        return await ReadOutboxEventAsync<
            WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent>(
            scope.ServiceProvider,
            WorkspacesModuleMetadata.Name,
            continuationEventId).ConfigureAwait(false);
    }

    private static async Task<bool> HasAccessProcessAsync(
        ServiceProvider services,
        Guid staffMemberId,
        long targetStaffVersion)
    {
        using IServiceScope scope = CreateTenantScope(services);
        return await scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>()
            .StaffAccessProcesses.AsNoTracking()
            .AnyAsync(candidate =>
                candidate.StaffMemberId == staffMemberId &&
                candidate.TargetStaffVersion == targetStaffVersion)
            .ConfigureAwait(false);
    }

    private static async Task AssertRawResumeRejectedAsync(
        string connectionString,
        Guid staffMemberId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            UPDATE staff.staff_members
            SET "Status" = 1,
                "SuspendedAtUtc" = NULL,
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scope_id
              AND "Id" = @staff_member_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", TenantId);
        command.Parameters.AddWithValue("staff_member_id", staffMemberId);
        PostgresException exception = await Assert.ThrowsAsync<
            PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        await transaction.RollbackAsync().ConfigureAwait(false);
    }

    private static async Task AssertRawAuthSubjectChangeRejectedAsync(
        string connectionString,
        Guid staffMemberId,
        string? authSubjectId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            UPDATE staff.staff_members
            SET "AuthSubjectId" = @auth_subject_id,
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scope_id
              AND "Id" = @staff_member_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", TenantId);
        command.Parameters.AddWithValue("staff_member_id", staffMemberId);
        command.Parameters.AddWithValue(
            "auth_subject_id",
            NpgsqlTypes.NpgsqlDbType.Text,
            (object?)authSubjectId ?? DBNull.Value);
        PostgresException exception = await Assert.ThrowsAsync<
            PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Contains(
            "Workspace onboarding anchor requires access closure before Staff Auth subject change",
            exception.MessageText,
            StringComparison.Ordinal);
        await transaction.RollbackAsync().ConfigureAwait(false);
    }

    private static async Task<WorkspaceStaffAccessProcess>
        ReadAccessProcessAsync(
            ServiceProvider services,
            Guid staffMemberId,
            long targetStaffVersion)
    {
        using IServiceScope scope = CreateTenantScope(services);
        return await scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>()
            .StaffAccessProcesses.AsNoTracking()
            .Include(candidate => candidate.ProfileSnapshots)
            .SingleAsync(candidate =>
                candidate.StaffMemberId == staffMemberId &&
                candidate.TargetStaffVersion == targetStaffVersion)
            .ConfigureAwait(false);
    }

    private static async Task DeliverStaffLifecycleAsync(
        ServiceProvider services,
        Guid staffMemberId,
        long staffVersion)
    {
        StaffMemberLifecycleChangedIntegrationEvent integrationEvent =
            await ReadLifecycleEventAsync(
                services,
                staffMemberId,
                staffVersion).ConfigureAwait(false);
        await DeliverWorkspacesAsync(
            services,
            integrationEvent,
            WorkspacesModuleMetadata.StaffAccessLifecycleHandlerName)
            .ConfigureAwait(false);
    }

    private static async Task<StaffMemberLifecycleChangedIntegrationEvent>
        ReadLifecycleEventAsync(
            ServiceProvider services,
            Guid staffMemberId,
            long staffVersion)
    {
        using IServiceScope scope = CreateTenantScope(services);
        OutboxMessage[] messages = await scope.ServiceProvider
            .GetRequiredService<StaffDbContext>()
            .OutboxMessages.AsNoTracking()
            .Where(message => message.EventType ==
                    typeof(StaffMemberLifecycleChangedIntegrationEvent)
                        .FullName &&
                message.Version ==
                    StaffMemberLifecycleChangedIntegrationEvent.EventVersion)
            .ToArrayAsync().ConfigureAwait(false);
        return Assert.Single(messages
            .Select(message => JsonSerializer.Deserialize<
                StaffMemberLifecycleChangedIntegrationEvent>(
                    message.Payload,
                    JsonOptions))
            .OfType<StaffMemberLifecycleChangedIntegrationEvent>(),
            candidate =>
                candidate.StaffMemberId == staffMemberId &&
                candidate.StaffVersion == staffVersion);
    }

    private static async Task<
        StaffWorkspaceOnboardingIdentityAnchorOutcome> ReadStaffOutcomeAsync(
        ServiceProvider services,
        Guid applicationId,
        string expectedSubjectId)
    {
        using IServiceScope scope = CreateTenantScope(services);
        IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome> outcomes =
            await scope.ServiceProvider.GetRequiredService<
                    IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader>()
                .ReadAsync(
                    [new(applicationId, expectedSubjectId)],
                    CancellationToken.None).ConfigureAwait(false);
        return Assert.Single(outcomes);
    }

    private static async Task DeliverWorkspacesAsync<TEvent>(
        ServiceProvider services,
        TEvent integrationEvent,
        string handlerName)
        where TEvent : IntegrationEvent
    {
        using IServiceScope scope = CreateTenantScope(services);
        ITransactionalUnitOfWork workspaces = GetTransactionalUnitOfWork(
            scope.ServiceProvider,
            WorkspacesMigrations.Schema);
        await workspaces.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            IIntegrationEventHandler<TEvent> handler = GetHandler<TEvent>(
                scope.ServiceProvider,
                WorkspacesModuleMetadata.Name,
                handlerName);
            await handler.HandleAsync(integrationEvent, CancellationToken.None)
                .ConfigureAwait(false);
            await workspaces.SaveChangesAsync().ConfigureAwait(false);
            await workspaces.CommitTransactionAsync().ConfigureAwait(false);
        }
        catch
        {
            await workspaces.RollbackTransactionAsync(CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DeliverStaffExtensionAsync(
        ServiceProvider services,
        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent
            integrationEvent)
    {
        using IServiceScope scope = CreateTenantScope(services);
        IIntegrationEventHandler<
            WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent>
            handler = GetHandler<
                WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent>(
                scope.ServiceProvider,
                StaffModuleMetadata.Name,
                "bunkfy-workspace-staff-identity-anchor-resolution");
        await handler.HandleAsync(integrationEvent, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static IIntegrationEventHandler<TEvent> GetHandler<TEvent>(
        IServiceProvider services,
        string consumerModule,
        string handlerName)
        where TEvent : IntegrationEvent
    {
        IIntegrationEventSubscriptionRegistry subscriptions = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>();
        Type handlerType = subscriptions.Subscriptions.Single(subscription =>
            subscription.ConsumerModule == consumerModule &&
            subscription.HandlerName == handlerName).HandlerType;
        return (IIntegrationEventHandler<TEvent>)services
            .GetRequiredService(handlerType);
    }

    private static async Task<TEvent> ReadOutboxEventAsync<TEvent>(
        ServiceProvider services,
        string moduleName,
        Guid eventId)
        where TEvent : IntegrationEvent
    {
        using IServiceScope scope = CreateTenantScope(services);
        return await ReadOutboxEventAsync<TEvent>(
            scope.ServiceProvider,
            moduleName,
            eventId).ConfigureAwait(false);
    }

    private static async Task<TEvent> ReadOutboxEventAsync<TEvent>(
        IServiceProvider services,
        string moduleName,
        Guid eventId)
        where TEvent : IntegrationEvent
    {
        OutboxMessage message = string.Equals(
            moduleName,
            StaffModuleMetadata.Name,
            StringComparison.Ordinal)
            ? await services.GetRequiredService<StaffDbContext>()
                .OutboxMessages.AsNoTracking().SingleAsync(candidate =>
                    candidate.Id == eventId).ConfigureAwait(false)
            : await services.GetRequiredService<WorkspacesDbContext>()
                .OutboxMessages.AsNoTracking().SingleAsync(candidate =>
                    candidate.Id == eventId).ConfigureAwait(false);
        return Assert.IsType<TEvent>(JsonSerializer.Deserialize<TEvent>(
            message.Payload,
            JsonOptions));
    }

    private static async Task<Result<TResponse>> SendAsync<TResponse>(
        ServiceProvider services,
        ICommand<TResponse> command)
    {
        using IServiceScope scope = CreateTenantScope(services);
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static ITransactionalUnitOfWork GetTransactionalUnitOfWork(
        IServiceProvider services,
        string moduleName) => Assert.IsType<ITransactionalUnitOfWork>(
            services.GetServices<IUnitOfWork>()
            .Single(unitOfWork => string.Equals(
                unitOfWork.ModuleName,
                moduleName,
                StringComparison.Ordinal)),
            exactMatch: false);

    private static IServiceScope CreateTenantScope(
        ServiceProvider services)
    {
        IServiceScope scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>()
            .SetScope(TenantId);
        return scope;
    }

    private static async Task MigrateActualModuleSchemasAsync(
        ServiceProvider services)
    {
        using IServiceScope scope = CreateTenantScope(services);
        await scope.ServiceProvider.GetRequiredService<StaffDbContext>()
            .Database.GetService<IMigrator>().MigrateAsync()
            .ConfigureAwait(false);
        WorkspacesDbContext workspaces = scope.ServiceProvider
            .GetRequiredService<WorkspacesDbContext>();
        await workspaces.Database.GetService<IMigrator>().MigrateAsync()
            .ConfigureAwait(false);
    }

    private static ServiceProvider CreateProvider(
        string connectionString,
        SequentialIdGenerator ids,
        PhaseAAccessState access,
        BlockingLifecyclePolicy? blockingLifecycle = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Configuration["Scoping:Enabled"] = "true";
        builder.AddScopingInfrastructure();
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        builder.Services.AddSingleton<IAccessControlRoleProvisioner>(access);
        builder.Services.AddSingleton<IAccessProfileProvisioner>(access);
        builder.Services.AddSingleton<IScopedAccessProfileProvisioner>(access);
        builder.Services.AddSingleton<IScopedAccessProfileManager>(access);
        builder.Services.AddSingleton<IOrganizationMembershipLifecycle>(access);
        builder.Services.AddSingleton<IOrganizationScopeLifecycle>(access);
        builder.Services.AddSingleton<IOrganizationMembershipReader>(access);
        builder.Services.AddSingleton(access);
        builder.Services.AddSingleton<IAccessAuthorizationService>(
            AllowAllAuthorizationService.Instance);
        builder.Services.AddStaffApplication();
        builder.AddStaffPersistence();
        builder.Services.AddWorkspacesApplication(
            builder.Configuration,
            "global");
        if (blockingLifecycle is not null)
        {
            builder.Services.AddSingleton<IStaffLifecyclePolicy>(
                blockingLifecycle);
        }
        builder.AddWorkspacesPersistence();
        builder.Services.AddBunkFyWorkspaces(options =>
            options.GlobalAuthScopeId = "global");
        builder.Services.Replace(ServiceDescriptor.Singleton<IIdGenerator>(
            ids));
        builder.Services.Replace(ServiceDescriptor.Singleton<ISystemClock>(
            new AdvancingClock()));
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private long next;

        public Guid NewId()
        {
            long value = Interlocked.Increment(ref this.next);
            return Guid.Parse(
                $"70000000-0000-0000-0000-{value:x12}");
        }
    }

    private sealed record AnchoredPassOne(
        Guid ApplicationId,
        Guid StaffMemberId,
        Guid ResolutionEventId,
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            Continuation);

    private sealed record ProvisionedAnchor(
        Guid ApplicationId,
        Guid StaffMemberId,
        long StaffVersion,
        Guid ResolutionEventId,
        StaffIdentityProvisioningAnchorCreatedIntegrationEvent AnchorCreated);

    private sealed class BlockingLifecyclePolicy(
        StaffLifecyclePolicyDecision firstDecision)
        : IStaffLifecyclePolicy
    {
        private readonly TaskCompletionSource entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int remaining = 1;

        public Task Entered => this.entered.Task;

        public void Release() => this.released.TrySetResult();

        public async ValueTask<StaffLifecyclePolicyDecision> PrepareAsync(
            StaffLifecyclePolicyContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.Transition != StaffLifecycleTransition.Suspend ||
                Interlocked.Exchange(ref this.remaining, 0) == 0)
            {
                return StaffLifecyclePolicyDecision.Allowed;
            }

            this.entered.TrySetResult();
            await this.released.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return firstDecision;
        }
    }

    private sealed class AdvancingClock : ISystemClock
    {
        private long ticks;

        public DateTimeOffset UtcNow => Now.AddMilliseconds(
            Interlocked.Increment(ref this.ticks));
    }

    private sealed class AllowAllAuthorizationService
        : IAccessAuthorizationService
    {
        public static AllowAllAuthorizationService Instance { get; } = new();

        public Task<AccessDecision> AuthorizeAsync(
            AccessRequirement requirement,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccessDecision.Allowed());
    }

    private sealed class PhaseAAccessState
        : IAccessControlRoleProvisioner,
          IAccessProfileProvisioner,
          IScopedAccessProfileProvisioner,
          IScopedAccessProfileManager,
          IOrganizationMembershipLifecycle,
          IOrganizationScopeLifecycle,
          IOrganizationMembershipReader
    {
        private readonly Lock membershipSync = new();
        private readonly Dictionary<(Guid OrganizationId, string SubjectId),
            OrganizationMembershipDto> memberships = [];
        private long organizationRevision = 1;
        private readonly Dictionary<string, AccessProfileAssignmentTarget[]>
            assignments = new(StringComparer.Ordinal);
        private readonly HashSet<string> membershipMarkers =
            new(StringComparer.Ordinal);
        private readonly List<string> provisions = [];
        private readonly List<(string SubjectId,
            OrganizationMembershipStatus Status)> membershipTransitions = [];

        public Guid ProfileId { get; } =
            Guid.Parse("75000000-0000-0000-0000-000000000001");

        public void SeedMembership(string subjectId)
        {
            Guid organizationId = Guid.Parse(TenantId);
            lock (this.membershipSync)
            {
                if (this.memberships.TryAdd((organizationId, subjectId), new(
                        Guid.NewGuid(), organizationId, subjectId,
                        OrganizationMembershipRole.Member,
                        OrganizationMembershipStatus.Active, 1, Now, Now)))
                {
                    this.organizationRevision++;
                }
            }
        }

        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.membershipSync)
            {
                return Task.FromResult(new OrganizationScopeSnapshot(
                    organizationId == Guid.Parse(TenantId)
                        ? OrganizationScopeStatus.Open
                        : OrganizationScopeStatus.Missing,
                    this.organizationRevision));
            }
        }

        public Task<OrganizationMembershipSnapshotDto?> FindAsync(
            Guid organizationId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.membershipSync)
            {
                return Task.FromResult<OrganizationMembershipSnapshotDto?>(
                    this.memberships.TryGetValue((organizationId, subjectId),
                        out OrganizationMembershipDto? membership)
                        ? new(organizationId, OrganizationStatus.Active, membership)
                        : null);
            }
        }

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public int ProvisionCountFor(string subjectId) =>
            this.provisions.Count(candidate => string.Equals(
                candidate,
                subjectId,
                StringComparison.Ordinal));

        public AccessProfileAssignmentTarget[] TargetsFor(
            string subjectId) =>
            (this.assignments.GetValueOrDefault(subjectId) ?? []).ToArray();

        public void SeedTargets(
            string subjectId,
            params AccessProfileAssignmentTarget[] targets) =>
            this.assignments[subjectId] = targets.ToArray();

        public int MembershipTransitionCount(
            string subjectId,
            OrganizationMembershipStatus status) =>
            this.membershipTransitions.Count(candidate =>
                string.Equals(
                    candidate.SubjectId,
                    subjectId,
                    StringComparison.Ordinal) &&
                candidate.Status == status);

        public Task EnsureRoleAsync(
            AccessControlRoleDefinition role,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(
                    roleName,
                    WorkspaceAccessRoles.MembershipMarker,
                    StringComparison.Ordinal))
            {
                this.membershipMarkers.Add(subject.Id);
            }

            return Task.CompletedTask;
        }

        public Task<AccessControlAssignmentRemovalOutcome>
            RemoveAssignmentAsync(
                AccessSubject subject,
                string roleName,
                AccessScope scope,
                CancellationToken cancellationToken = default)
        {
            bool removed = string.Equals(
                    roleName,
                    WorkspaceAccessRoles.MembershipMarker,
                    StringComparison.Ordinal) &&
                this.membershipMarkers.Remove(subject.Id);
            return Task.FromResult(removed
                ? AccessControlAssignmentRemovalOutcome.Removed
                : AccessControlAssignmentRemovalOutcome.NotFound);
        }

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(
                roleName,
                WorkspaceAccessRoles.Owner,
                StringComparison.Ordinal));

        public Task<AccessControlPage<AccessControlRoleAssignment>>
            ListAssignmentsAsync(
                string roleName,
                AccessScope scope,
                int page,
                int pageSize,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessControlPage<
                AccessControlRoleAssignment>([], page, pageSize, false));

        public Task<AccessProfileDto> EnsureProfileAsync(
            AccessScope ownerScope,
            AccessProfileDefinition definition,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Profile(ownerScope, definition.Key));

        public Task<AccessProfileDto?> FindProfileByKeyAsync(
            AccessScope ownerScope,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AccessProfileDto?>(this.Profile(ownerScope, key));

        public Task<AccessProfileAssignmentSet> GetSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessProfileAssignmentSet(
                subject,
                ownerScope,
                []));

        public Task<AccessProfileAssignmentReconciliation>
            ReconcileSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<Guid> profileIds,
                AccessSubject actor,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessProfileAssignmentReconciliation(
                subject,
                ownerScope,
                profileIds.ToArray(),
                profileIds.Count,
                0));

        public Task<ScopedAccessProfileAssignmentSet>
            GetSubjectScopedAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                CancellationToken cancellationToken = default)
        {
            AccessProfileAssignmentTarget[] targets =
                this.assignments.GetValueOrDefault(subject.Id) ?? [];
            return Task.FromResult(new ScopedAccessProfileAssignmentSet(
                subject,
                ownerScope,
                targets.Select(target => new ScopedAccessProfileAssignment(
                    this.Profile(ownerScope, WorkspaceAccessProfileSeeds
                        .FrontDeskKey),
                    target.AssignmentScope)).ToArray()));
        }

        public Task<ScopedAccessProfileAssignmentReconciliation>
            ReconcileSubjectScopedAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken = default)
        {
            AccessProfileAssignmentTarget[] desired = targets
                .Distinct()
                .ToArray();
            this.assignments[subject.Id] = desired;
            if (desired.Length > 0)
            {
                this.provisions.Add(subject.Id);
            }

            return Task.FromResult(
                new ScopedAccessProfileAssignmentReconciliation(
                    subject,
                    ownerScope,
                    desired,
                    desired.Length,
                    0));
        }

        async Task<Result<ScopedAccessProfileAssignmentSet>>
            IScopedAccessProfileManager.GetSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                AccessSubject actor,
                CancellationToken cancellationToken) =>
            Result.Success(await this.GetSubjectScopedAssignmentsAsync(
                subject, ownerScope, cancellationToken).ConfigureAwait(false));

        async Task<Result<ScopedAccessProfileAssignmentReconciliation>>
            IScopedAccessProfileManager.ReconcileSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken) =>
            Result.Success(await this.ReconcileSubjectScopedAssignmentsAsync(
                subject, ownerScope, targets, actor, cancellationToken)
                .ConfigureAwait(false));

        public Task<OrganizationMembershipLifecycleResult> EnsureStateAsync(
            Guid organizationId,
            string subjectId,
            OrganizationMembershipStatus desiredStatus,
            string actorId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.membershipSync)
            {
                OrganizationMembershipDto membership =
                    this.memberships[(organizationId, subjectId)];
                if (membership.Status != desiredStatus)
                {
                    membership = membership with
                    {
                        Status = desiredStatus,
                        Version = membership.Version + 1,
                        LastChangedAtUtc = Now
                    };
                    this.memberships[(organizationId, subjectId)] = membership;
                    this.organizationRevision++;
                }

                this.membershipTransitions.Add((subjectId, desiredStatus));
                return Task.FromResult(new OrganizationMembershipLifecycleResult(
                    OrganizationMembershipLifecycleOutcome.Changed,
                    membership));
            }
        }

        private AccessProfileDto Profile(
            AccessScope ownerScope,
            string key) => new(
            this.ProfileId,
            ownerScope.Value,
            key,
            "Front desk",
            "Front desk operations.",
            AccessProfileStatus.Active,
            1,
            WorkspaceAccessProfileSeeds.FrontDesk.Permissions.ToArray(),
            0,
            Now,
            Now);
    }

}

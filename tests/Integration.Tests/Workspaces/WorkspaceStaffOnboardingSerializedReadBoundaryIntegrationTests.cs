namespace Integration.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class
    WorkspaceStaffOnboardingSerializedReadBoundaryIntegrationTests
{
    private const string TenantId =
        "ac000000-0000-0000-0000-000000000001";
    private const string SubjectId = "subject:serialized-profile-read";
    private static readonly Guid ApplicationId =
        Guid.Parse("ac000000-0000-0000-0000-000000000002");
    private static readonly Guid SourceId =
        Guid.Parse("ac000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 15, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Successful_boundary_callback_is_conclusively_rolled_back()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_profile_read_boundary_rollback")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await CreateSchemaAndSeedAsync(connectionString).ConfigureAwait(false);

        await using (WorkspacesDbContext dbContext =
            CreateDbContext(connectionString))
        {
            WorkspaceStaffOnboardingMutationCoordinator mutations = new(
                new WorkspaceStaffOnboardingOperationLock(dbContext),
                new WorkspaceStaffOnboardingRepository(dbContext));
            WorkspaceStaffOnboardingSerializedReadBoundary boundary = new(
                dbContext);

            Result<long> result = await boundary.RunAsync(
                async cancellationToken =>
                {
                    WorkspaceStaffOnboardingMutationLease lease =
                        await mutations.AcquireExistingAsync(
                            ApplicationId,
                            WorkspaceStaffOnboardingSourceLockMode.Read,
                            requireOperational: true,
                            cancellationToken).ConfigureAwait(false);
                    WorkspaceStaffOnboarding application =
                        Assert.IsType<WorkspaceStaffOnboarding>(
                            lease.Application);
                    Assert.True(application.UpdateSubmission(
                        application.VerifiedAccountEmail!,
                        "Must roll back",
                        application.LegalName,
                        application.WorkEmail,
                        application.WorkPhone,
                        application.EmployeeNumber,
                        application.JobTitle,
                        application.Department,
                        Now.AddMinutes(1)).IsSuccess);
                    await dbContext.SaveChangesAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return Result.Success(application.Version);
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.True(result.IsSuccess, result.Error.Code);
            Assert.Equal(2, result.Value);
            Assert.Null(dbContext.Database.CurrentTransaction);
        }

        await using WorkspacesDbContext verification =
            CreateDbContext(connectionString);
        WorkspaceStaffOnboarding persisted = await verification
            .StaffOnboardingApplications.AsNoTracking()
            .SingleAsync(application => application.Id == ApplicationId)
            .ConfigureAwait(false);
        Assert.Equal("Applicant Profile", persisted.DisplayName);
        Assert.Equal(1, persisted.Version);
        Assert.Empty(await verification.OutboxMessages.AsNoTracking()
            .ToArrayAsync().ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Get_waits_for_application_lock_before_reading_staff_outcome()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_profile_read_boundary_lock")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await CreateSchemaAndSeedAsync(connectionString).ConfigureAwait(false);

        await using WorkspacesDbContext holder =
            CreateDbContext(connectionString);
        await using IDbContextTransaction holderTransaction = await holder
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        WorkspaceStaffOnboardingMutationCoordinator holderMutations = new(
            new WorkspaceStaffOnboardingOperationLock(holder),
            new WorkspaceStaffOnboardingRepository(holder));
        WorkspaceStaffOnboardingMutationLease held =
            await holderMutations.AcquireExistingAsync(
                ApplicationId,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: true,
                CancellationToken.None).ConfigureAwait(false);
        Assert.NotNull(held.Application);

        await using WorkspacesDbContext reader =
            CreateDbContext(connectionString);
        MutableOutcomeReader outcomes = new();
        TestScopeContext scope = new();
        GetOwnWorkspaceStaffOnboardingQueryHandler handler = new(
            new WorkspaceStaffOnboardingMutationCoordinator(
                new WorkspaceStaffOnboardingOperationLock(reader),
                new WorkspaceStaffOnboardingRepository(reader)),
            new WorkspaceStaffOnboardingSerializedReadBoundary(reader),
            outcomes,
            new WorkspaceOperationalAdmissionEvaluator(
                new WorkspaceTerminationFenceRepository(reader),
                scope,
                NullLogger<WorkspaceOperationalAdmissionEvaluator>.Instance),
            scope);

        Task<Result<WorkspaceStaffOnboardingDto>> pending =
            handler.HandleAsync(
                new GetOwnWorkspaceStaffOnboardingQuery(
                    WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
                    SourceId,
                    SubjectId),
                CancellationToken.None);
        await using WorkspacesDbContext managerDb = CreateDbContext(connectionString);
        MutableOutcomeReader managerOutcomes = new();
        GetWorkspaceStaffOnboardingReviewQueryHandler manager = new(
            new WorkspaceStaffOnboardingManagerReviewReader(
                new WorkspaceStaffOnboardingMutationCoordinator(
                    new WorkspaceStaffOnboardingOperationLock(managerDb),
                    new WorkspaceStaffOnboardingRepository(managerDb)),
                new WorkspaceStaffOnboardingSerializedReadBoundary(managerDb),
                managerOutcomes,
                new WorkspaceOperationalAdmissionEvaluator(
                    new WorkspaceTerminationFenceRepository(managerDb), scope,
                    NullLogger<WorkspaceOperationalAdmissionEvaluator>.Instance),
                scope));
        Task<Result<WorkspaceStaffOnboardingDto>> pendingManager = manager.HandleAsync(
            new GetWorkspaceStaffOnboardingReviewQuery(ApplicationId),
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
        Assert.False(outcomes.ReadStarted.Task.IsCompleted);
        Assert.False(managerOutcomes.ReadStarted.Task.IsCompleted);

        outcomes.Outcome = new StaffWorkspaceOnboardingIdentityAnchorOutcome(
            ApplicationId,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
            Guid.Parse("ac000000-0000-0000-0000-000000000004"),
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
            WorkspaceApplicationVersion: null,
            ResolutionDisposition: null,
            Guid.Parse("ac000000-0000-0000-0000-000000000005"));
        managerOutcomes.Outcome = outcomes.Outcome;
        await holderTransaction.RollbackAsync(CancellationToken.None)
            .ConfigureAwait(false);

        Result<WorkspaceStaffOnboardingDto> result = await pending.WaitAsync(
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(ApplicationId, result.Value.ApplicationId);
        Assert.Equal(WorkspaceStaffOnboardingStatus.Submitted, result.Value.Status);
        Assert.All<string?>([
            result.Value.VerifiedAccountEmail, result.Value.DisplayName,
            result.Value.LegalName, result.Value.WorkEmail, result.Value.WorkPhone,
            result.Value.EmployeeNumber, result.Value.JobTitle, result.Value.Department
        ], value => Assert.Null(value));
        Result<WorkspaceStaffOnboardingDto> managerResult = await pendingManager.WaitAsync(
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.True(managerResult.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingApplicationErrors.ProfileReviewUnavailable,
            managerResult.Error);
        Assert.True(managerOutcomes.ReadStarted.Task.IsCompleted);
        Assert.Null(managerDb.Database.CurrentTransaction);
        Assert.True(outcomes.ReadStarted.Task.IsCompleted);
        await using WorkspacesDbContext verify = CreateDbContext(connectionString);
        Assert.Equal("Applicant Profile", (await verify.StaffOnboardingApplications
            .AsNoTracking().SingleAsync().ConfigureAwait(false)).DisplayName);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Data_rights_export_waits_for_application_lock_and_writes_nothing_for_unresolved_anchor()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_profile_export_boundary_lock")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await CreateSchemaAndSeedAsync(connectionString).ConfigureAwait(false);

        await using WorkspacesDbContext holder =
            CreateDbContext(connectionString);
        await using IDbContextTransaction holderTransaction = await holder
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        WorkspaceStaffOnboardingMutationCoordinator holderMutations = new(
            new WorkspaceStaffOnboardingOperationLock(holder),
            new WorkspaceStaffOnboardingRepository(holder));
        WorkspaceStaffOnboardingMutationLease held =
            await holderMutations.AcquireExistingAsync(
                ApplicationId,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: true,
                CancellationToken.None).ConfigureAwait(false);
        Assert.NotNull(held.Application);

        await using WorkspacesDbContext reader =
            CreateDbContext(connectionString);
        MutableOutcomeReader outcomes = new()
        {
            Outcome = new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                ApplicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
                Guid.Parse("ac000000-0000-0000-0000-000000000004"),
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null,
                Guid.Parse("ac000000-0000-0000-0000-000000000005"))
        };
        TestScopeContext scope = new();
        WorkspacesDataRightsExportContributor contributor = new(
            reader,
            scope,
            new WorkspaceStaffOnboardingSerializedReadBoundary(reader),
            new WorkspaceStaffOnboardingOperationLock(reader),
            new WorkspaceStaffOnboardingRepository(reader),
            outcomes,
            NullLogger<WorkspacesDataRightsExportContributor>.Instance);
        CollectingSink sink = new();

        Task<DataRightsSubjectExportResult> pending = contributor.ExportAsync(
            new DataRightsSubjectExportRequest(
                TenantId,
                DataRightsCaseType.StaffRights,
                PropertyId: null,
                new DataRightsSubjectCoordinate(
                    WorkspacesDataRightsCoordinates.Owner,
                    WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
                    ApplicationId,
                    RecordVersion: 1)),
            sink,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
        Assert.False(outcomes.ReadStarted.Task.IsCompleted);
        Assert.Empty(sink.Records);

        await holderTransaction.RollbackAsync(CancellationToken.None)
            .ConfigureAwait(false);

        DataRightsSubjectExportResult result = await pending.WaitAsync(
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.Equal(
            DataRightsSubjectExportStatus.ScopeUnavailable,
            result.Status);
        Assert.True(outcomes.ReadStarted.Task.IsCompleted);
        Assert.Empty(sink.Records);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Tenant_export_preflights_more_than_one_staff_page_before_any_sink_write()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_tenant_export_anchor_preflight")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await CreateSchemaAndSeedAsync(connectionString).ConfigureAwait(false);

        await using WorkspacesDbContext dbContext =
            CreateDbContext(connectionString);
        for (int index = 1; index <= 501; index++)
        {
            dbContext.StaffOnboardingApplications.Add(
                WorkspaceStaffOnboarding.Create(
                    Guid.Parse($"ad000000-0000-0000-0000-{index:D12}"),
                    TenantId,
                    WorkspaceStaffOnboardingSource.Invitation,
                    Guid.Parse($"ae000000-0000-0000-0000-{index:D12}"),
                    $"subject:tenant-export:{index}",
                    $"verified-{index}@example.test",
                    $"Applicant {index}",
                    legalName: null,
                    workEmail: null,
                    workPhone: null,
                    employeeNumber: null,
                    jobTitle: null,
                    department: null,
                    Now).Value);
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();

        WorkspaceTerminationFence fence = WorkspaceTerminationFence.Freeze(
            Guid.Parse("af000000-0000-0000-0000-000000000001"),
            TenantId,
            Guid.Parse("af000000-0000-0000-0000-000000000002"),
            Guid.Parse("af000000-0000-0000-0000-000000000003"),
            approvalRevision: 1,
            Guid.Parse("af000000-0000-0000-0000-000000000004"),
            new string('a', 64),
            "tenant-export-operator",
            Now.AddMinutes(1)).Value;
        dbContext.WorkspaceTerminationFences.Add(fence);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();

        PagedOutcomeReader outcomes = new();
        WorkspacesTenantTerminationExportContributor contributor = new(
            dbContext,
            new TestScopeContext(),
            new TestClock(Now.AddMinutes(2)),
            outcomes,
            NullLogger<WorkspacesTenantTerminationExportContributor>.Instance);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                new TenantTerminationExportRequest(
                    new TenantTerminationContributionRequest(
                        TenantTerminationContract.CurrentVersion,
                        TenantId,
                        fence.ProcessId,
                        fence.CaseId,
                        fence.ApprovalRevision,
                        OperationRevision: 2,
                        fence.TerminationEpoch,
                        TenantTerminationContributionPhase.Export,
                        Guid.Parse(
                            "af000000-0000-0000-0000-000000000005"),
                        Guid.Parse(
                            "af000000-0000-0000-0000-000000000006"),
                        fence.PolicyEvidenceSha256,
                        "tenant-exporter",
                        Now.AddMinutes(10)),
                    FreezeOperationRevision: 1,
                    fence.Version,
                    fence.PolicyEvidenceSha256,
                    fence.CreatedAtUtc),
                sink,
                CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "workspace.termination.export-identity-anchor-unavailable",
            result.ResultCode);
        Assert.Equal([500, 2], outcomes.BatchSizes);
        Assert.Empty(sink.Records);
    }

    private static async Task CreateSchemaAndSeedAsync(
        string connectionString)
    {
        await using WorkspacesDbContext dbContext =
            CreateDbContext(connectionString);
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
        WorkspaceStaffOnboarding application = CreateApplication();
        dbContext.StaffOnboardingApplications.Add(application);
        dbContext.StaffOnboardingProcessingRestrictionProjections.Add(
            WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
                TenantId,
                ApplicationId,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                Now).Value);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static WorkspacesDbContext CreateDbContext(
        string connectionString) =>
        new(
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseNpgsql(connectionString, options => options.MigrationsAssembly(
                    WorkspacesMigrations.PostgreSqlAssembly))
                .Options,
            new TestScopeContext());

    private static WorkspaceStaffOnboarding CreateApplication() =>
        WorkspaceStaffOnboarding.Create(
            ApplicationId,
            TenantId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            SourceId,
            SubjectId,
            "verified@example.test",
            "Applicant Profile",
            "Applicant Legal",
            "work@example.test",
            "+1 555 0100",
            "EMP-AC",
            "Manager",
            "Operations",
            Now).Value;

    private sealed class MutableOutcomeReader
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StaffWorkspaceOnboardingIdentityAnchorOutcome? Outcome
        {
            get;
            set;
        }

        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
            IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                requests,
            CancellationToken cancellationToken = default)
        {
            StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest request =
                Assert.Single(requests);
            this.ReadStarted.TrySetResult();
            return Task.FromResult<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>([
                    this.Outcome ?? new(
                        request.ApplicationId,
                        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                            .Absent,
                        StaffMemberId: null,
                        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                            .Unknown,
                        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                            .Unknown,
                        WorkspaceApplicationVersion: null,
                        ResolutionDisposition: null)
                ]);
        }
    }

    private sealed class CollectingSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PagedOutcomeReader
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
    {
        public List<int> BatchSizes { get; } = [];

        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
            IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                requests,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.BatchSizes.Add(requests.Count);
            bool firstPage = this.BatchSizes.Count == 1;
            return Task.FromResult<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>(
                    requests.Select(request => firstPage
                        ? new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                            request.ApplicationId,
                            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                                .Absent,
                            StaffMemberId: null,
                            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                                .Unknown,
                            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                                .Unknown,
                            WorkspaceApplicationVersion: null,
                            ResolutionDisposition: null)
                        : new StaffWorkspaceOnboardingIdentityAnchorOutcome(
                            request.ApplicationId,
                            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                                .Unresolved,
                            Guid.Parse(
                                "af000000-0000-0000-0000-000000000007"),
                            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                                .Active,
                            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                                .Exact,
                            WorkspaceApplicationVersion: null,
                            ResolutionDisposition: null,
                            Guid.Parse(
                                "af000000-0000-0000-0000-000000000008"))
                    ).ToArray());
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}

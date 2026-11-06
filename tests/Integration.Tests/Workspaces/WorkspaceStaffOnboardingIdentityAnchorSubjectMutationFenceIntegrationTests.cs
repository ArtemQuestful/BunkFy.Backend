namespace Integration.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class
    WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFenceIntegrationTests
{
    private const string TenantId =
        "ad000000-0000-0000-0000-000000000001";
    private const string OtherTenantId =
        "ad000000-0000-0000-0000-000000000002";
    private const string SubjectId = "subject:anchor-mutation-fence";
    private const string OtherSubjectId =
        "subject:anchor-mutation-fence-sentinel";
    private const string MutatedSubjectId =
        "subject:anchor-mutation-fence-mutated";
    private static readonly Guid OtherSubjectApplicationId =
        CreateOrderedId(900);
    private static readonly Guid OtherTenantApplicationId =
        CreateOrderedId(901);
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Exact_absent_pages_permit_the_guarded_subject_mutation()
    {
        await using PostgreSqlContainer postgreSql =
            await StartPostgreSqlAsync(
                "bunkfy_anchor_subject_mutation_permit")
                .ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await CreateSchemaAndSeedAsync(connectionString)
            .ConfigureAwait(false);
        RecordingOutcomeReader outcomes = new(SecondPageMode.Absent);

        GuardedMutationResult result = await RunGuardedMutationAsync(
                connectionString,
                TenantId,
                outcomes)
            .ConfigureAwait(false);

        Assert.True(result.Allowed);
        Assert.Equal(501, result.MutatedCount);
        Assert.Equal([500, 1], outcomes.BatchSizes);
        Assert.Equal(501, outcomes.RequestedApplicationIds.Count);
        Assert.Equal(
            501,
            outcomes.RequestedApplicationIds.Distinct().Count());
        Assert.DoesNotContain(
            OtherSubjectApplicationId,
            outcomes.RequestedApplicationIds);
        Assert.DoesNotContain(
            OtherTenantApplicationId,
            outcomes.RequestedApplicationIds);
        Assert.All(
            outcomes.ExpectedSubjects,
            subject => Assert.Equal(SubjectId, subject));
        await AssertPersistedSubjectsAsync(
                connectionString,
                expectedOriginalCount: 0,
                expectedMutatedCount: 501)
            .ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Second_page_unresolved_blocks_before_caller_mutation()
    {
        await AssertSecondPageBlocksAsync(
                "bunkfy_anchor_subject_mutation_unresolved",
                SecondPageMode.Unresolved)
            .ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Second_page_malformed_blocks_before_caller_mutation()
    {
        await AssertSecondPageBlocksAsync(
                "bunkfy_anchor_subject_mutation_malformed",
                SecondPageMode.Malformed)
            .ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Active_scope_mismatch_fails_before_staff_read()
    {
        await using PostgreSqlContainer postgreSql =
            await StartPostgreSqlAsync(
                "bunkfy_anchor_subject_mutation_scope_mismatch")
                .ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await CreateSchemaAndSeedAsync(connectionString)
            .ConfigureAwait(false);
        RecordingOutcomeReader outcomes = new(SecondPageMode.Absent);

        GuardedMutationResult result = await RunGuardedMutationAsync(
                connectionString,
                OtherTenantId,
                outcomes)
            .ConfigureAwait(false);

        Assert.False(result.Allowed);
        Assert.Equal(0, result.MutatedCount);
        Assert.Empty(outcomes.BatchSizes);
        Assert.Empty(outcomes.RequestedApplicationIds);
        await AssertPersistedSubjectsAsync(
                connectionString,
                expectedOriginalCount: 501,
                expectedMutatedCount: 0)
            .ConfigureAwait(false);
    }

    private static async Task AssertSecondPageBlocksAsync(
        string databaseName,
        SecondPageMode mode)
    {
        await using PostgreSqlContainer postgreSql =
            await StartPostgreSqlAsync(databaseName).ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await CreateSchemaAndSeedAsync(connectionString)
            .ConfigureAwait(false);
        RecordingOutcomeReader outcomes = new(mode);

        GuardedMutationResult result = await RunGuardedMutationAsync(
                connectionString,
                TenantId,
                outcomes)
            .ConfigureAwait(false);

        Assert.False(result.Allowed);
        Assert.Equal(0, result.MutatedCount);
        Assert.Equal([500, 1], outcomes.BatchSizes);
        Assert.Equal(501, outcomes.RequestedApplicationIds.Count);
        await AssertPersistedSubjectsAsync(
                connectionString,
                expectedOriginalCount: 501,
                expectedMutatedCount: 0)
            .ConfigureAwait(false);
    }

    private static async Task<GuardedMutationResult> RunGuardedMutationAsync(
        string connectionString,
        string requestedTenantId,
        RecordingOutcomeReader outcomes)
    {
        await using WorkspacesDbContext database =
            CreateDbContext(connectionString, TenantId);
        await using IDbContextTransaction transaction = await database
            .Database.BeginTransactionAsync()
            .ConfigureAwait(false);
        await new WorkspaceCrossGraphMutationLock(database)
            .AcquireAsync(CancellationToken.None)
            .ConfigureAwait(false);
        WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence fence =
            new(
                database,
                outcomes,
                new TestScopeContext(TenantId),
                NullLogger<
                    WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence>
                    .Instance);

        bool allowed = await fence.CanMutateAsync(
                requestedTenantId,
                SubjectId,
                CancellationToken.None)
            .ConfigureAwait(false);
        int mutatedCount = 0;
        if (allowed)
        {
            mutatedCount = await database.StaffOnboardingApplications
                .Where(application =>
                    application.ScopeId == TenantId &&
                    application.SubjectId == SubjectId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    application => application.SubjectId,
                    MutatedSubjectId))
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
        return new(allowed, mutatedCount);
    }

    private static async Task AssertPersistedSubjectsAsync(
        string connectionString,
        int expectedOriginalCount,
        int expectedMutatedCount)
    {
        await using (WorkspacesDbContext tenant =
            CreateDbContext(connectionString, TenantId))
        {
            Assert.Equal(
                expectedOriginalCount,
                await tenant.StaffOnboardingApplications
                    .AsNoTracking()
                    .CountAsync(application =>
                        application.SubjectId == SubjectId)
                    .ConfigureAwait(false));
            Assert.Equal(
                expectedMutatedCount,
                await tenant.StaffOnboardingApplications
                    .AsNoTracking()
                    .CountAsync(application =>
                        application.SubjectId == MutatedSubjectId)
                    .ConfigureAwait(false));
            WorkspaceStaffOnboarding otherSubject = await tenant
                .StaffOnboardingApplications
                .AsNoTracking()
                .SingleAsync(application =>
                    application.Id == OtherSubjectApplicationId)
                .ConfigureAwait(false);
            Assert.Equal(OtherSubjectId, otherSubject.SubjectId);
        }

        await using WorkspacesDbContext otherTenant =
            CreateDbContext(connectionString, OtherTenantId);
        WorkspaceStaffOnboarding otherTenantSentinel = await otherTenant
            .StaffOnboardingApplications
            .AsNoTracking()
            .SingleAsync(application =>
                application.Id == OtherTenantApplicationId)
            .ConfigureAwait(false);
        Assert.Equal(SubjectId, otherTenantSentinel.SubjectId);
    }

    private static async Task CreateSchemaAndSeedAsync(
        string connectionString)
    {
        await using (WorkspacesDbContext tenant =
            CreateDbContext(connectionString, TenantId))
        {
            await tenant.Database.MigrateAsync().ConfigureAwait(false);
            for (int index = 1; index <= 501; index++)
            {
                tenant.StaffOnboardingApplications.Add(CreateApplication(
                    CreateOrderedId(index),
                    TenantId,
                    SubjectId));
            }

            tenant.StaffOnboardingApplications.Add(CreateApplication(
                OtherSubjectApplicationId,
                TenantId,
                OtherSubjectId));
            await tenant.SaveChangesAsync().ConfigureAwait(false);
        }

        await using WorkspacesDbContext otherTenant =
            CreateDbContext(connectionString, OtherTenantId);
        otherTenant.StaffOnboardingApplications.Add(CreateApplication(
            OtherTenantApplicationId,
            OtherTenantId,
            SubjectId));
        await otherTenant.SaveChangesAsync().ConfigureAwait(false);
    }

    private static WorkspaceStaffOnboarding CreateApplication(
        Guid applicationId,
        string tenantId,
        string subjectId) => WorkspaceStaffOnboarding.Create(
            applicationId,
            tenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            subjectId,
            $"{applicationId:N}@example.test",
            "Anchor mutation fence applicant",
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            Now).Value;

    private static WorkspacesDbContext CreateDbContext(
        string connectionString,
        string tenantId) => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseNpgsql(connectionString, options => options.MigrationsAssembly(
                WorkspacesMigrations.PostgreSqlAssembly))
            .Options,
        new TestScopeContext(tenantId));

    private static async Task<PostgreSqlContainer> StartPostgreSqlAsync(
        string databaseName)
    {
        PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase(databaseName)
            .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        return postgreSql;
    }

    private static Guid CreateOrderedId(int value) => Guid.Parse(
        $"00000000-0000-0000-0000-{value:D12}");

    private sealed class RecordingOutcomeReader(SecondPageMode mode)
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
    {
        public List<int> BatchSizes { get; } = [];
        public List<Guid> RequestedApplicationIds { get; } = [];
        public List<string> ExpectedSubjects { get; } = [];

        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
            IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                requests,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.BatchSizes.Add(requests.Count);
            this.RequestedApplicationIds.AddRange(
                requests.Select(request => request.ApplicationId));
            this.ExpectedSubjects.AddRange(
                requests.Select(request => request.ExpectedAuthSubjectId));
            bool secondPage = this.BatchSizes.Count == 2;
            if (secondPage && mode == SecondPageMode.Malformed)
            {
                return Task.FromResult<IReadOnlyList<
                    StaffWorkspaceOnboardingIdentityAnchorOutcome>>([]);
            }

            return Task.FromResult<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>(
                requests.Select(request =>
                        secondPage && mode == SecondPageMode.Unresolved
                            ? CreateUnresolved(request.ApplicationId)
                            : CreateAbsent(request.ApplicationId))
                    .ToArray());
        }

        private static StaffWorkspaceOnboardingIdentityAnchorOutcome
            CreateAbsent(Guid applicationId) => new(
                applicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent,
                StaffMemberId: null,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null,
                ResolutionEventId: null);

        private static StaffWorkspaceOnboardingIdentityAnchorOutcome
            CreateUnresolved(Guid applicationId) => new(
                applicationId,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
                Guid.Parse("ad000000-0000-0000-0000-000000000010"),
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                WorkspaceApplicationVersion: 1,
                ResolutionDisposition: null,
                Guid.Parse("ad000000-0000-0000-0000-000000000011"));
    }

    private sealed class TestScopeContext(string tenantId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = tenantId;
    }

    private sealed record GuardedMutationResult(
        bool Allowed,
        int MutatedCount);

    private enum SecondPageMode
    {
        Absent,
        Unresolved,
        Malformed
    }
}

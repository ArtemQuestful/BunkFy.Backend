namespace BunkFy.Modules.Workspaces.Tests;

using System.Reflection;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Pagination;
using Gma.Modules.Organizations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingMutationCoordinatorTests
{
    [Theory]
    [InlineData(
        1,
        "source-read")]
    [InlineData(
        2,
        "source-write")]
    public async Task Existing_application_locks_source_before_row_and_reload(
        int modeValue,
        string expectedSourceCall)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        List<string> calls = [];
        WorkspaceStaffOnboardingMutationCoordinator coordinator = new(
            new RecordingOperationLock(calls),
            new RecordingRepository(application, calls));

        WorkspaceStaffOnboardingMutationLease lease =
            await coordinator.AcquireExistingAsync(
                application.Id,
                (WorkspaceStaffOnboardingSourceLockMode)modeValue,
                requireOperational: false,
                CancellationToken.None);

        Assert.True(lease.CoordinateExists);
        Assert.Same(application, lease.Application);
        Assert.Equal(
            ["coordinate", expectedSourceCall, "application-lock", "read"],
            calls);
    }

    [Fact]
    public async Task Applicant_absence_is_checked_under_source_and_applicant_locks()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        List<string> calls = [];
        WorkspaceStaffOnboardingMutationCoordinator coordinator = new(
            new RecordingOperationLock(calls),
            new RecordingRepository(application: null, calls));

        WorkspaceStaffOnboardingMutationLease lease =
            await coordinator.AcquireApplicantAsync(
                application.SourceKind,
                application.SourceId,
                application.SubjectId,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: true,
                CancellationToken.None);

        Assert.False(lease.CoordinateExists);
        Assert.Null(lease.Application);
        Assert.Equal(["source-read", "applicant-lock", "find"], calls);
    }

    [Fact]
    public async Task Operational_filter_preserves_existing_coordinate_result()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        List<string> calls = [];
        RecordingRepository applications = new(application, calls)
        {
            OperationallyVisible = false
        };
        WorkspaceStaffOnboardingMutationCoordinator coordinator = new(
            new RecordingOperationLock(calls),
            applications);

        WorkspaceStaffOnboardingMutationLease lease =
            await coordinator.AcquireExistingAsync(
                application.Id,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: true,
                CancellationToken.None);

        Assert.True(lease.CoordinateExists);
        Assert.Null(lease.Application);
        Assert.Equal(
            ["coordinate", "source-read", "application-lock", "operational-read"],
            calls);
    }

    [Fact]
    public async Task Tracked_application_reloads_only_after_source_and_row_locks()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        List<string> calls = [];
        WorkspaceStaffOnboardingMutationCoordinator coordinator = new(
            new RecordingOperationLock(calls),
            new RecordingRepository(application, calls));

        bool acquired = await coordinator.AcquireTrackedAsync(
            application,
            WorkspaceStaffOnboardingSourceLockMode.Read,
            CancellationToken.None);

        Assert.True(acquired);
        Assert.Equal(
            ["source-read", "application-lock", "reload"],
            calls);
    }

    [Fact]
    public async Task Empty_application_coordinate_does_not_enter_the_lock()
    {
        List<string> calls = [];
        WorkspaceStaffOnboardingMutationCoordinator coordinator = new(
            new RecordingOperationLock(calls),
            new RecordingRepository(application: null, calls));

        WorkspaceStaffOnboardingMutationLease lease =
            await coordinator.AcquireExistingAsync(
                Guid.Empty,
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: false,
                CancellationToken.None);

        Assert.False(lease.CoordinateExists);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Unknown_source_kind_fails_before_entering_the_lock()
    {
        List<string> calls = [];
        WorkspaceStaffOnboardingMutationCoordinator coordinator = new(
            new RecordingOperationLock(calls),
            new RecordingRepository(application: null, calls));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            coordinator.AcquireApplicantAsync(
                (WorkspaceStaffOnboardingSource)int.MaxValue,
                Guid.NewGuid(),
                Guid.NewGuid().ToString("D"),
                WorkspaceStaffOnboardingSourceLockMode.Read,
                requireOperational: false,
                CancellationToken.None));

        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(typeof(PrepareWorkspaceStaffAccessPlanCommandHandler))]
    [InlineData(typeof(ActivateWorkspaceStaffAccessPlanCommandHandler))]
    [InlineData(typeof(SubmitWorkspaceStaffOnboardingCommandHandler))]
    [InlineData(typeof(WorkspaceStaffOnboardingProcessor))]
    [InlineData(typeof(ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler))]
    [InlineData(typeof(ApplyWorkspaceStaffOnboardingProcessingRestrictionCommandHandler))]
    [InlineData(typeof(ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommandHandler))]
    [InlineData(typeof(ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler))]
    [InlineData(typeof(OrganizationInvitationStaffOnboardingHandler))]
    [InlineData(typeof(OrganizationEnrollmentClaimStaffOnboardingHandler))]
    [InlineData(typeof(OrganizationEnrollmentLinkStaffOnboardingHandler))]
    [InlineData(typeof(OrganizationInvitationExpiredStaffOnboardingHandler))]
    [InlineData(typeof(OrganizationEnrollmentClaimExpiredStaffOnboardingHandler))]
    [InlineData(typeof(OrganizationEnrollmentClaimWithdrawnStaffOnboardingHandler))]
    [InlineData(typeof(OrganizationEnrollmentLinkExpiredStaffOnboardingHandler))]
    public void Direct_source_graph_writers_require_mutation_coordinator(
        Type writerType)
    {
        AssertDependency(
            writerType,
            typeof(WorkspaceStaffOnboardingMutationCoordinator));
    }

    [Theory]
    [InlineData(typeof(SubmitWorkspaceStaffOnboardingCommandHandler))]
    [InlineData(typeof(ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler))]
    public void Enrollment_profile_mutations_require_organizations_claim_authority(
        Type writerType)
    {
        AssertDependency(
            writerType,
            typeof(IOrganizationEnrollmentClaimInspector));
    }

    [Theory]
    [InlineData(typeof(RetryWorkspaceStaffOnboardingCommandHandler))]
    [InlineData(typeof(WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler))]
    public void Recovery_writers_continue_through_the_locked_processor(
        Type writerType)
    {
        AssertDependency(writerType, typeof(WorkspaceStaffOnboardingProcessor));
    }

    private static void AssertDependency(Type writerType, Type dependencyType)
    {
        bool hasDependency = writerType
            .GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == dependencyType);

        Assert.True(
            hasDependency,
            $"{writerType.Name} must serialize through {dependencyType.Name}.");
    }

    private static WorkspaceStaffOnboarding CreateApplication() =>
        WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            "tenant-a",
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            Guid.NewGuid().ToString("D"),
            "applicant@example.test",
            "Applicant",
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            new DateTimeOffset(2026, 8, 6, 14, 0, 0, TimeSpan.Zero)).Value;

    private sealed class RecordingOperationLock(List<string> calls)
        : IWorkspaceStaffOnboardingOperationLock
    {
        public Task AcquireSourceReadAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            calls.Add("source-read");
            return Task.CompletedTask;
        }

        public Task AcquireSourceWriteAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            calls.Add("source-write");
            return Task.CompletedTask;
        }

        public Task AcquireApplicantAsync(
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken)
        {
            calls.Add("applicant-lock");
            return Task.CompletedTask;
        }

        public Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            calls.Add("application-lock");
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingRepository(
        WorkspaceStaffOnboarding? application,
        List<string> calls)
        : IWorkspaceStaffOnboardingRepository
    {
        public bool OperationallyVisible { get; init; } = true;

        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            calls.Add("coordinate");
            return Task.FromResult(application?.Id == applicationId
                ? new WorkspaceStaffOnboardingCoordinate(
                    application.Id,
                    application.SourceKind,
                    application.SourceId)
                : null);
        }

        public Task<WorkspaceStaffOnboarding?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            calls.Add("read");
            return Task.FromResult(application?.Id == applicationId
                ? application
                : null);
        }

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            calls.Add("operational-read");
            return Task.FromResult(this.OperationallyVisible &&
                application?.Id == applicationId
                ? application
                : null);
        }

        public Task<WorkspaceStaffOnboarding?>
            GetOperationalBySourceAndSubjectAsync(
                WorkspaceStaffOnboardingSource sourceKind,
                Guid sourceId,
                string subjectId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboarding?>
            GetBySourceAndSubjectForLifecycleAsync(
                WorkspaceStaffOnboardingSource sourceKind,
                Guid sourceId,
                string subjectId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindIdBySourceAndSubjectAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken)
        {
            calls.Add("find");
            return Task.FromResult(application is not null &&
                application.SourceKind == sourceKind &&
                application.SourceId == sourceId &&
                string.Equals(
                    application.SubjectId,
                    subjectId,
                    StringComparison.Ordinal)
                ? (Guid?)application.Id
                : null);
        }

        public Task<WorkspaceStaffOnboarding?> GetByClaimAsync(
            Guid claimId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkspaceStaffOnboarding>>
            ListActiveBySourceAsync(
                WorkspaceStaffOnboardingSource sourceKind,
                Guid sourceId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboardingListResponse> ListActionableAsync(
            PageRequest page,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReloadAsync(
            WorkspaceStaffOnboarding reloaded,
            CancellationToken cancellationToken)
        {
            calls.Add("reload");
            return Task.CompletedTask;
        }

        public Task AddAsync(
            WorkspaceStaffOnboarding added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

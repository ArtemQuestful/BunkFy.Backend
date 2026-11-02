namespace BunkFy.Modules.Workspaces.Tests.Application;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Tests;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    WorkspaceStaffOnboardingProcessingRestrictionEnforcementTests
{
    private const string TenantId =
        "4ad4969d-4467-4338-8a90-4ca76106b04d";
    private static readonly Guid OrganizationId = Guid.Parse(TenantId);
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 17, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("missing")]
    [InlineData("unsupported")]
    [InlineData("restricted")]
    public async Task Processor_fails_closed_before_any_external_provisioning(
        string projectionState)
    {
        WorkspaceStaffOnboarding application = CreateAcceptedApplication();
        List<string> calls = [];
        RecordingApplicationRepository applications =
            new(application, calls);
        RecordingOperationLock operationLock = new(calls);
        RecordingProjectionRepository projections = new(
            CreateProjection(application, projectionState),
            calls);
        RecordingStaffProvisioner staff = new();
        RecordingStaffPropertyProvisioner staffProperties = new();
        RecordingPlanRepository plans = new();
        WorkspaceStaffOnboardingProcessor processor = CreateProcessor(
            staff,
            staffProperties,
            applications,
            projections,
            operationLock,
            plans);

        Result result = await processor.ProcessAsync(
            application,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            projectionState == "restricted"
                ? WorkspaceStaffOnboardingApplicationErrors
                    .ProcessingRestricted
                : WorkspaceStaffOnboardingApplicationErrors
                    .RestrictionProjectionUnavailable,
            result.Error);
        Assert.Equal(["source", "lock", "reload", "projection"], calls);
        Assert.Equal(0, staff.CallCount);
        Assert.Equal(0, staffProperties.CallCount);
        Assert.Equal(0, plans.GetCount);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Provisioning,
            application.Status);
    }

    [Fact]
    public async Task Invitation_acceptance_is_observed_after_the_locked_reload()
    {
        WorkspaceStaffOnboarding application =
            CreateSubmittedApplication(
                WorkspaceStaffOnboardingSource.Invitation);
        List<string> calls = [];
        WorkspaceStaffOnboardingProcessor processor = CreateProcessor(
            new RecordingStaffProvisioner(),
            new RecordingStaffPropertyProvisioner(),
            new RecordingApplicationRepository(application, calls),
            new RecordingProjectionRepository(null, calls),
            new RecordingOperationLock(calls),
            new RecordingPlanRepository());

        Result result = await processor.ProcessInvitationAcceptanceAsync(
            application,
            CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .RestrictionProjectionUnavailable,
            result.Error);
        Assert.Equal(["source", "lock", "reload", "projection"], calls);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Provisioning,
            application.Status);
        Assert.Equal(2, application.Version);
    }

    [Fact]
    public async Task Enrollment_acceptance_is_observed_after_the_locked_reload()
    {
        WorkspaceStaffOnboarding application =
            CreateSubmittedApplication(
                WorkspaceStaffOnboardingSource.EnrollmentLink);
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(
            claimId,
            claimVersion: 1,
            Now.AddMinutes(1)).IsSuccess);
        List<string> calls = [];
        WorkspaceStaffOnboardingProcessor processor = CreateProcessor(
            new RecordingStaffProvisioner(),
            new RecordingStaffPropertyProvisioner(),
            new RecordingApplicationRepository(application, calls),
            new RecordingProjectionRepository(null, calls),
            new RecordingOperationLock(calls),
            new RecordingPlanRepository());

        Result result = await processor
            .ProcessEnrollmentClaimAcceptanceAsync(
                application,
                claimId,
                claimVersion: 2,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .RestrictionProjectionUnavailable,
            result.Error);
        Assert.Equal(
            ["source-write", "lock", "reload", "projection"],
            calls);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Provisioning,
            application.Status);
        Assert.Equal(3, application.Version);
        Assert.Equal(2, application.ClaimVersion);
    }

    [Fact]
    public async Task Termination_fence_stops_onboarding_before_projection_or_provisioning()
    {
        WorkspaceStaffOnboarding application = CreateAcceptedApplication();
        List<string> calls = [];
        RecordingStaffProvisioner staff = new();
        RecordingStaffPropertyProvisioner staffProperties = new();
        RecordingPlanRepository plans = new();
        WorkspaceStaffOnboardingProcessor processor = CreateProcessor(
            staff,
            staffProperties,
            new RecordingApplicationRepository(application, calls),
            new RecordingProjectionRepository(null, calls),
            new RecordingOperationLock(calls),
            plans,
            WorkspaceOperationalAdmissionTestSupport.Restricted(TenantId));

        Result result = await processor.ProcessAsync(
            application,
            CancellationToken.None);

        Assert.Equal(
            WorkspaceOperationalAdmissionErrors.ProcessingRestricted,
            result.Error);
        Assert.Equal(["source", "lock", "reload"], calls);
        Assert.Equal(0, staff.CallCount);
        Assert.Equal(0, staffProperties.CallCount);
        Assert.Equal(0, plans.GetCount);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Provisioning,
            application.Status);
    }

    [Fact]
    public async Task Release_recovery_rechecks_projection_and_stops_reapply_race()
    {
        WorkspaceStaffOnboarding application = CreateAcceptedApplication();
        Assert.True(application.BeginProvisioning(Now.AddMinutes(2)).IsSuccess);
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
                TenantId,
                application.Id,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                Now).Value;
        Assert.True(projection.Apply(
            expectedRevision: 0,
            WorkspaceStaffOnboardingProcessingRestrictionContract
                .CurrentVersion,
            Now.AddMinutes(3)).IsSuccess);

        List<string> calls = [];
        RecordingApplicationRepository applications =
            new(application, calls);
        RecordingOperationLock operationLock = new(calls);
        RecordingProjectionRepository projections = new(projection, calls);
        RecordingStaffProvisioner staff = new();
        WorkspaceStaffOnboardingProcessor processor = CreateProcessor(
            staff,
            new RecordingStaffPropertyProvisioner(),
            applications,
            projections,
            operationLock,
            new RecordingPlanRepository());
        WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler handler =
            new(
                applications,
                new RecordingPlanRepository(),
                new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
                processor,
                new TestClock(),
                NullLogger<
                    WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler>
                    .Instance);

        await handler.HandleAsync(
            CreateEvent(
                application.Id,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                isRestricted: true),
            CancellationToken.None);
        await handler.HandleAsync(
            CreateEvent(
                application.Id,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion + 1,
                isRestricted: false),
            CancellationToken.None);
        await handler.HandleAsync(
            CreateEvent(
                application.Id,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                isRestricted: false),
            CancellationToken.None);

        Assert.Equal(1, applications.GetCount);
        Assert.Equal(
            ["get", "source-write", "lock", "reload", "projection"],
            calls);
        Assert.Equal(1, operationLock.CallCount);
        Assert.Equal(0, staff.CallCount);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Provisioning,
            application.Status);
    }

    [Fact]
    public async Task Release_recovery_failure_is_surfaced_for_inbox_retry()
    {
        WorkspaceStaffOnboarding application = CreateAcceptedApplication();
        Assert.True(application.BeginProvisioning(Now.AddMinutes(2)).IsSuccess);
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
                TenantId,
                application.Id,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                Now).Value;

        List<string> calls = [];
        RecordingApplicationRepository applications =
            new(application, calls);
        WorkspaceStaffOnboardingProcessor processor = CreateProcessor(
            new RecordingStaffProvisioner(),
            new RecordingStaffPropertyProvisioner(),
            applications,
            new RecordingProjectionRepository(projection, calls),
            new RecordingOperationLock(calls),
            new RecordingPlanRepository());
        WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler handler =
            new(
                applications,
                new RecordingPlanRepository(),
                new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
                processor,
                new TestClock(),
                NullLogger<
                    WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler>
                    .Instance);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => handler.HandleAsync(
                    CreateEvent(
                        application.Id,
                        WorkspaceStaffOnboardingProcessingRestrictionContract
                            .CurrentVersion,
                        isRestricted: false),
                    CancellationToken.None));

        Assert.Contains(
            WorkspaceStaffOnboardingApplicationErrors
                .AccessPlanUnavailable.Code,
            failure.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Failed,
            application.Status);
    }

    [Fact]
    public async Task Resubmission_of_restricted_onboarding_never_contacts_auth()
    {
        Guid sourceId = Guid.NewGuid();
        Guid existingApplicationId = Guid.NewGuid();
        RecordingSubmissionRepository applications =
            new(existingApplicationId);
        RecordingOperationLock operationLock = new([]);
        RecordingAdmissionReader admissions = new();
        RecordingProjectionRepository projections = new(null, []);
        WorkspaceStaffAccessPlan plan = CreateActivePlan(sourceId);
        SubmitWorkspaceStaffOnboardingCommandHandler handler = new(
            applications,
            projections,
            WorkspaceStaffOnboardingMutationTestSupport.Create(
                applications,
                operationLock),
            new RecordingPlanRepository(plan),
            new WorkspaceStaffJoinTokenAuthorityResolver(
                new EnrollmentTokenInspector(OrganizationId, sourceId)),
            admissions,
            new FakeOrganizationEnrollmentClaimInspector(),
            Options.Create(new WorkspaceStaffOnboardingOptions
            {
                GlobalAuthScopeId = "global"
            }),
            WorkspaceOperationalAdmissionTestSupport.Allowed(TenantId),
            new ScopeContext(),
            new TestClock(),
            new TestIds());

        Result<WorkspaceStaffOnboardingDto> result =
            await handler.HandleAsync(
                new SubmitWorkspaceStaffOnboardingCommand(
                    WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
                    "join-token",
                    Guid.NewGuid().ToString("D"),
                    "Applicant",
                    LegalName: null,
                    WorkEmail: null,
                    WorkPhone: null,
                    EmployeeNumber: null,
                    JobTitle: null,
                    Department: null),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.ProcessingRestricted,
            result.Error);
        Assert.Equal(1, operationLock.CallCount);
        Assert.Equal(1, applications.OperationalGetCount);
        Assert.Equal(0, applications.AddCount);
        Assert.Equal(0, admissions.CallCount);
        Assert.Equal(0, projections.AddCount);
    }

    private static WorkspaceStaffOnboardingProcessor CreateProcessor(
        RecordingStaffProvisioner staff,
        RecordingStaffPropertyProvisioner staffProperties,
        RecordingApplicationRepository applications,
        RecordingProjectionRepository projections,
        RecordingOperationLock operationLock,
        RecordingPlanRepository plans,
        WorkspaceOperationalAdmissionEvaluator? operationalAdmission = null) =>
        new(
            staff,
            staffProperties,
            projections,
            WorkspaceStaffOnboardingMutationTestSupport.Create(
                applications,
                operationLock),
            plans,
            new WorkspaceStaffAccessPlanPolicy(
                profiles: null!,
                roles: null!,
                authorization: null!,
                properties: null!),
            new WorkspaceAccessProvisioner(
                roles: null!,
                profiles: null!,
                scopedProfiles: null!),
            operationalAdmission ??
                WorkspaceOperationalAdmissionTestSupport.Allowed(TenantId),
            new WorkspaceOrganizationMembershipObserver(
                [new ActiveOrganizationLifecycle()],
                [new ActiveMembershipReader()],
                new TestClock()),
            new TestClock(),
            NullLogger<WorkspaceStaffOnboardingProcessor>.Instance);

    private static
        WorkspaceStaffOnboardingProcessingRestrictionProjection?
        CreateProjection(
            WorkspaceStaffOnboarding application,
            string projectionState)
    {
        if (projectionState == "missing")
        {
            return null;
        }

        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
                TenantId,
                application.Id,
                projectionState == "unsupported"
                    ? WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion + 1
                    : WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                Now).Value;
        if (projectionState == "restricted")
        {
            Assert.True(projection.Apply(
                expectedRevision: 0,
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion,
                Now.AddMinutes(2)).IsSuccess);
        }

        return projection;
    }

    private static WorkspaceStaffOnboarding CreateAcceptedApplication()
    {
        WorkspaceStaffOnboarding application =
            CreateSubmittedApplication(
                WorkspaceStaffOnboardingSource.EnrollmentLink);
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            claimVersion: 1,
            Now.AddMinutes(1)).IsSuccess);
        return application;
    }

    private static WorkspaceStaffOnboarding CreateSubmittedApplication(
        WorkspaceStaffOnboardingSource sourceKind) =>
        WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            TenantId,
            sourceKind,
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
            Now).Value;

    private static WorkspaceStaffAccessPlan CreateActivePlan(Guid sourceId)
    {
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            sourceId,
            TenantId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            WorkspaceAccessProfileSeeds.FrontDeskKey,
            propertyIds: [],
            Guid.NewGuid().ToString("D"),
            Now).Value;
        Assert.True(plan.Activate(Now.AddSeconds(1)).IsSuccess);
        return plan;
    }

    private static
        WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent
        CreateEvent(
            Guid applicationId,
            int contractVersion,
            bool isRestricted) =>
        new(
            Guid.NewGuid(),
            TenantId,
            Now.AddMinutes(4),
            applicationId,
            contractVersion,
            projectionRevision: 2,
            isRestricted);

    private sealed class RecordingApplicationRepository(
        WorkspaceStaffOnboarding application,
        List<string> calls)
        : IWorkspaceStaffOnboardingRepository
    {
        public int GetCount { get; private set; }

        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            calls.Add("coordinate");
            return Task.FromResult(application.Id == applicationId
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
            this.GetCount++;
            calls.Add("get");
            return Task.FromResult(
                application.Id == applicationId ? application : null);
        }

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public Task<WorkspaceStaffOnboardingListResponse>
            ListActionableAsync(
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

    private sealed class RecordingSubmissionRepository(Guid applicationId)
        : IWorkspaceStaffOnboardingRepository
    {
        public int OperationalGetCount { get; private set; }
        public int AddCount { get; private set; }

        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboarding?> GetAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            this.OperationalGetCount++;
            return Task.FromResult<WorkspaceStaffOnboarding?>(null);
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
            CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(applicationId);

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

        public Task<WorkspaceStaffOnboardingListResponse>
            ListActionableAsync(
                PageRequest page,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReloadAsync(
            WorkspaceStaffOnboarding reloaded,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffOnboarding added,
            CancellationToken cancellationToken)
        {
            this.AddCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProjectionRepository(
        WorkspaceStaffOnboardingProcessingRestrictionProjection? projection,
        List<string> calls)
        : IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
    {
        public int AddCount { get; private set; }

        public Task<
            WorkspaceStaffOnboardingProcessingRestrictionProjection?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            calls.Add("projection");
            return Task.FromResult(projection);
        }

        public Task AddAsync(
            WorkspaceStaffOnboardingProcessingRestrictionProjection added,
            CancellationToken cancellationToken)
        {
            this.AddCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOperationLock(List<string> calls)
        : IWorkspaceStaffOnboardingOperationLock
    {
        public int CallCount { get; private set; }

        public Task AcquireSourceReadAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            calls.Add("source");
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
            calls.Add("applicant");
            return Task.CompletedTask;
        }

        public Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            calls.Add("lock");
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingPlanRepository(
        WorkspaceStaffAccessPlan? plan = null)
        : IWorkspaceStaffAccessPlanRepository
    {
        public int GetCount { get; private set; }

        public Task<WorkspaceStaffAccessPlan?> GetAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            this.GetCount++;
            return Task.FromResult(plan?.Id == sourceId ? plan : null);
        }

        public Task<IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>>
            GetManyAsync(
                IReadOnlyCollection<Guid> sourceIds,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffAccessPlan added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingStaffProvisioner
        : IStaffOnboardingProvisioner
    {
        public int CallCount { get; private set; }

        public Task<StaffOnboardingProvisioningResult> ProvisionAsync(
            StaffOnboardingProvisioningRequest request,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult(new StaffOnboardingProvisioningResult(
                true,
                Guid.NewGuid(),
                ErrorCode: null));
        }
    }

    private sealed class RecordingStaffPropertyProvisioner
        : IStaffPropertyAssignmentProvisioner
    {
        public int CallCount { get; private set; }

        public Task<StaffPropertyAssignmentProvisioningResult> ReconcileAsync(
            StaffPropertyAssignmentProvisioningRequest request,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult(
                new StaffPropertyAssignmentProvisioningResult(
                    true,
                    request.PropertyIds.ToArray(),
                    ErrorCode: null));
        }
    }

    private sealed class RecordingAdmissionReader : IAuthMemberAdmissionReader
    {
        public int CallCount { get; private set; }

        public ValueTask<AuthMemberAdmission?> FindActiveAsync(
            string scopeId,
            Guid memberId,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return ValueTask.FromResult<AuthMemberAdmission?>(
                new AuthMemberAdmission("verified@example.test"));
        }
    }

    private sealed class ActiveOrganizationLifecycle
        : IOrganizationScopeLifecycle
    {
        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken) => Task.FromResult(
            new OrganizationScopeSnapshot(OrganizationScopeStatus.Open, 1));

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ActiveMembershipReader : IOrganizationMembershipReader
    {
        private readonly Guid membershipId = Guid.NewGuid();

        public Task<OrganizationMembershipSnapshotDto?> FindAsync(
            Guid organizationId,
            string subjectId,
            CancellationToken cancellationToken = default) => Task.FromResult<
                OrganizationMembershipSnapshotDto?>(new(
                    organizationId,
                    OrganizationStatus.Active,
                    new OrganizationMembershipDto(
                        this.membershipId,
                        organizationId,
                        subjectId,
                        OrganizationMembershipRole.Member,
                        OrganizationMembershipStatus.Active,
                        Version: 1,
                        Now,
                        Now)));
    }

    private sealed class EnrollmentTokenInspector(
        Guid organizationId,
        Guid sourceId)
        : IOrganizationJoinTokenInspector
    {
        public Task<OrganizationJoinTokenInspection<
            OrganizationInvitationPreviewDto>> InspectInvitationAsync(
                string token,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OrganizationJoinTokenInspection<
            OrganizationEnrollmentPreviewDto>> InspectEnrollmentAsync(
                string token,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new OrganizationJoinTokenInspection<
                    OrganizationEnrollmentPreviewDto>(
                    new OrganizationEnrollmentPreviewDto(
                        sourceId,
                        organizationId,
                        "Workspace",
                        "workspace",
                        Now.AddDays(1),
                        5,
                        OrganizationEnrollmentApprovalMode.RequiresApproval,
                        OrganizationEnrollmentLinkStatus.Active),
                    ErrorCode: null));
    }

    private sealed class ScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now.AddMinutes(5);
    }

    private sealed class TestIds : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }
}

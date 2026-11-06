namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    WorkspaceStaffOnboardingProcessingRestrictionCommandHandlerTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Apply_binds_approval_and_equivalent_retry_replays()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            CreateProjection(application);
        RecordingRestrictionRepository restrictions = new();
        RecordingApprovalGate approval = new();
        ApplyWorkspaceStaffOnboardingProcessingRestrictionCommandHandler
            handler = new(
                new ProjectionRepository(projection),
                restrictions,
                WorkspaceStaffOnboardingMutationTestSupport.Create(
                    new ApplicationRepository(application),
                    new OperationLock()),
                approval,
                new ScopeContext(),
                new Clock(),
                new Ids());
        ApplyWorkspaceStaffOnboardingProcessingRestrictionCommand command =
            new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                4,
                application.Id,
                application.Version,
                ExpectedProjectionRevision: 0,
                "user:privacy");

        Result<WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>
            applied = await handler.HandleAsync(
                command,
                CancellationToken.None);
        Result<WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>
            replayed = await handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Code);
        Assert.Equal(applied.Value, replayed.Value);
        Assert.Equal(DataRightsCaseType.StaffRights, approval.Request?.CaseType);
        Assert.Null(approval.Request?.PropertyId);
        Assert.Equal(
            DataRightsOperation.Restriction,
            approval.Request?.Operation);
        Assert.Equal(
            DataRightsRestrictionDirective.Apply,
            approval.Request?.RestrictionDirective);
        Assert.Equal(
            WorkspacesDataRightsCoordinates.Owner,
            approval.Request?.OwnerKey);
        Assert.Equal(
            WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
            approval.Request?.RecordType);
        Assert.Equal(
            application.Version,
            approval.Request?.RecordVersion);
        Assert.True(projection.IsRestricted);
        Assert.Equal(1, projection.ActiveRestrictionCount);
        Assert.Equal(1, projection.Revision);
        Assert.Single(restrictions.Rows);
        Assert.Single(restrictions.Receipts);
    }

    [Fact]
    public async Task Changed_apply_retry_is_rejected_without_transition()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            CreateProjection(application);
        RecordingRestrictionRepository restrictions = new();
        ApplyWorkspaceStaffOnboardingProcessingRestrictionCommandHandler
            handler = CreateApplyHandler(
                application,
                projection,
                restrictions);
        ApplyWorkspaceStaffOnboardingProcessingRestrictionCommand command =
            new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                2,
                application.Id,
                application.Version,
                0,
                "user:privacy");

        Assert.True((await handler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Result<WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>
            conflict = await handler.HandleAsync(
                command with { ExpectedProjectionRevision = 1 },
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .RestrictionIdempotencyConflict,
            conflict.Error);
        Assert.Equal(1, projection.Revision);
        Assert.Single(restrictions.Receipts);
    }

    [Fact]
    public async Task Apply_rejects_after_staff_authority_handoff()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.ObserveInvitationAccepted(
            Now.AddMinutes(-5)).IsSuccess);
        Assert.True(application.MarkStaffReady(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(-4)).IsSuccess);
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            CreateProjection(application);
        RecordingRestrictionRepository restrictions = new();

        Result<WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>
            result = await CreateApplyHandler(
                application,
                projection,
                restrictions).HandleAsync(
                    new(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        3,
                        application.Id,
                        application.Version,
                        projection.Revision,
                        "user:privacy"),
                    CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .RestrictionAuthorityUnavailable,
            result.Error);
        Assert.False(projection.IsRestricted);
        Assert.Empty(restrictions.Rows);
        Assert.Empty(restrictions.Receipts);
    }

    [Fact]
    public async Task Release_can_follow_terminal_lifecycle_version()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            CreateProjection(application);
        WorkspaceStaffOnboardingProcessingRestriction restriction =
            CreateRestriction(
                application,
                Now.AddMinutes(-10));
        Assert.True(projection.Apply(
            0,
            WorkspaceStaffOnboardingProcessingRestrictionContract
                .CurrentVersion,
            Now.AddMinutes(-10)).IsSuccess);
        Assert.True(application.Supersede(Now.AddMinutes(-5)).IsSuccess);
        RecordingRestrictionRepository restrictions = new([restriction]);
        RecordingApprovalGate approval = new();
        ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommandHandler
            handler = new(
                new ProjectionRepository(projection),
                restrictions,
                WorkspaceStaffOnboardingMutationTestSupport.Create(
                    new ApplicationRepository(application),
                    new OperationLock()),
                approval,
                new ScopeContext(),
                new Clock(),
                new Ids());

        Result<WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>
            result = await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    restriction.Id,
                    Guid.NewGuid(),
                    8,
                    application.Id,
                    application.Version,
                    restriction.Version,
                    projection.Revision,
                    "user:decision-maker"),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            DataRightsRestrictionDirective.Release,
            approval.Request?.RestrictionDirective);
        Assert.Equal(
            WorkspaceStaffOnboardingProcessingRestrictionState.Released,
            restriction.Status);
        Assert.False(projection.IsRestricted);
        Assert.Equal(2, projection.Revision);
        Assert.Equal(
            WorkspaceStaffOnboardingProcessingRestrictionActionDto.Release,
            result.Value.Action);
    }

    [Fact]
    public async Task Denied_approval_does_not_change_owner_state()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection =
            CreateProjection(application);
        RecordingRestrictionRepository restrictions = new();
        ApplyWorkspaceStaffOnboardingProcessingRestrictionCommandHandler
            handler = new(
                new ProjectionRepository(projection),
                restrictions,
                WorkspaceStaffOnboardingMutationTestSupport.Create(
                    new ApplicationRepository(application),
                    new OperationLock()),
                new RecordingApprovalGate(isApproved: false),
                new ScopeContext(),
                new Clock(),
                new Ids());

        Result<WorkspaceStaffOnboardingProcessingRestrictionReceiptDto>
            result = await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    1,
                    application.Id,
                    application.Version,
                    0,
                    "user:privacy"),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .RestrictionApprovalRequired,
            result.Error);
        Assert.False(projection.IsRestricted);
        Assert.Empty(restrictions.Rows);
        Assert.Empty(restrictions.Receipts);
    }

    private static
        ApplyWorkspaceStaffOnboardingProcessingRestrictionCommandHandler
        CreateApplyHandler(
            WorkspaceStaffOnboarding application,
            WorkspaceStaffOnboardingProcessingRestrictionProjection
                projection,
            RecordingRestrictionRepository restrictions) =>
        new(
            new ProjectionRepository(projection),
            restrictions,
            WorkspaceStaffOnboardingMutationTestSupport.Create(
                new ApplicationRepository(application),
                new OperationLock()),
            new RecordingApprovalGate(),
            new ScopeContext(),
            new Clock(),
            new Ids());

    private static WorkspaceStaffOnboarding CreateApplication() =>
        WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
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
            Now.AddHours(-1)).Value;

    private static
        WorkspaceStaffOnboardingProcessingRestrictionProjection
        CreateProjection(WorkspaceStaffOnboarding application) =>
        WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
            application.ScopeId,
            application.Id,
            WorkspaceStaffOnboardingProcessingRestrictionContract
                .CurrentVersion,
            application.CreatedAtUtc).Value;

    private static WorkspaceStaffOnboardingProcessingRestriction
        CreateRestriction(
            WorkspaceStaffOnboarding application,
            DateTimeOffset appliedAtUtc) =>
        WorkspaceStaffOnboardingProcessingRestriction.Create(
            Guid.NewGuid(),
            application.ScopeId,
            application.Id,
            Guid.NewGuid(),
            2,
            application.Version,
            "user:privacy",
            appliedAtUtc).Value;

    private sealed class ApplicationRepository(
        WorkspaceStaffOnboarding application)
        : IWorkspaceStaffOnboardingRepository
    {
        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(application.Id == applicationId
                ? new WorkspaceStaffOnboardingCoordinate(
                    application.Id,
                    application.SourceKind,
                    application.SourceId)
                : null);

        public Task<WorkspaceStaffOnboarding?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                application.Id == applicationId ? application : null);

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            this.GetAsync(applicationId, cancellationToken);

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

        public Task<WorkspaceStaffOnboardingSummaryListResponse>
            ListActionableAsync(
                PageRequest page,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReloadAsync(
            WorkspaceStaffOnboarding ignored,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AddAsync(
            WorkspaceStaffOnboarding added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProjectionRepository(
        WorkspaceStaffOnboardingProcessingRestrictionProjection projection)
        : IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
    {
        public Task<
            WorkspaceStaffOnboardingProcessingRestrictionProjection?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                projection.ApplicationId == applicationId
                    ? projection
                    : null);

        public Task AddAsync(
            WorkspaceStaffOnboardingProcessingRestrictionProjection added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRestrictionRepository(
        IEnumerable<WorkspaceStaffOnboardingProcessingRestriction>? rows =
            null,
        IEnumerable<
            WorkspaceStaffOnboardingProcessingRestrictionReceipt>? receipts =
            null)
        : IWorkspaceStaffOnboardingProcessingRestrictionRepository
    {
        public List<WorkspaceStaffOnboardingProcessingRestriction> Rows
        {
            get;
        } = rows?.ToList() ?? [];

        public List<WorkspaceStaffOnboardingProcessingRestrictionReceipt>
            Receipts
        {
            get;
        } = receipts?.ToList() ?? [];

        public Task<
            WorkspaceStaffOnboardingProcessingRestrictionReceipt?>
            FindReceiptByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult(this.Receipts.SingleOrDefault(receipt =>
                receipt.IdempotencyKey == idempotencyKey));

        public Task<WorkspaceStaffOnboardingProcessingRestriction?>
            FindByApplyApprovalAsync(
                Guid applicationId,
                Guid caseId,
                long approvalRevision,
                CancellationToken cancellationToken) =>
            Task.FromResult(this.Rows.SingleOrDefault(restriction =>
                restriction.ApplicationId == applicationId &&
                restriction.ApplyCaseId == caseId &&
                restriction.ApplyApprovalRevision == approvalRevision));

        public Task<WorkspaceStaffOnboardingProcessingRestriction?>
            FindByReleaseApprovalAsync(
                Guid applicationId,
                Guid caseId,
                long approvalRevision,
                CancellationToken cancellationToken) =>
            Task.FromResult(this.Rows.SingleOrDefault(restriction =>
                restriction.ApplicationId == applicationId &&
                restriction.ReleaseCaseId == caseId &&
                restriction.ReleaseApprovalRevision == approvalRevision));

        public Task<WorkspaceStaffOnboardingProcessingRestriction?> GetAsync(
            Guid restrictionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Rows.SingleOrDefault(restriction =>
                restriction.Id == restrictionId));

        public Task<IReadOnlyCollection<
            WorkspaceStaffOnboardingProcessingRestriction>> ListActiveAsync(
            Guid applicationId,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<
                WorkspaceStaffOnboardingProcessingRestriction>>(
                this.Rows
                    .Where(restriction =>
                        restriction.ApplicationId == applicationId &&
                        restriction.Status ==
                            WorkspaceStaffOnboardingProcessingRestrictionState
                                .Active)
                    .Skip(pageRequest.SkipCount)
                    .Take(pageRequest.PageSize)
                    .ToArray());

        public Task AddAsync(
            WorkspaceStaffOnboardingProcessingRestriction restriction,
            CancellationToken cancellationToken)
        {
            this.Rows.Add(restriction);
            return Task.CompletedTask;
        }

        public Task AddReceiptAsync(
            WorkspaceStaffOnboardingProcessingRestrictionReceipt receipt,
            CancellationToken cancellationToken)
        {
            this.Receipts.Add(receipt);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingApprovalGate(bool isApproved = true)
        : IDataRightsOperationApprovalGate
    {
        public DataRightsOperationApprovalRequest? Request { get; private set; }

        public Task<DataRightsOperationApprovalResult> EvaluateAsync(
            DataRightsOperationApprovalRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            return Task.FromResult(isApproved
                ? DataRightsOperationApprovalResult.Approved
                : DataRightsOperationApprovalResult.Denied(
                    DataRightsOperationApprovalDenial.CaseNotApproved));
        }
    }

    private sealed class OperationLock
        : IWorkspaceStaffOnboardingOperationLock
    {
        public Task AcquireSourceReadAsync(
            Guid sourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireSourceWriteAsync(
            Guid sourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireApplicantAsync(
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class ScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Ids : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }
}

namespace BunkFy.Modules.Workspaces.Tests;

using System.Diagnostics.Metrics;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.AccessControl;
using Gma.Framework.Pagination;
using Gma.Framework.Runtime.Time;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class AttemptWorkspaceStaffOnboardingRecoveryCommandHandlerTests
{
    private const string TenantId =
        "11111111-1111-1111-1111-111111111111";
    private const string SubjectId = "auth:manager";
    private const string ActorId = "auth:owner";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid MembershipId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");
    private static readonly Guid PropertyOne = Guid.Parse(
        "10000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyTwo = Guid.Parse(
        "10000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Review_receipt_replays_without_external_reads_and_changed_reuse_conflicts()
    {
        TestContext context = CreateContext(restricted: true);
        long selectedVersion = context.Application.Version;
        Guid operationId = Guid.Parse(
            "33333333-3333-3333-3333-333333333333");
        AttemptWorkspaceStaffOnboardingRecoveryCommand command = new(
            context.Application.Id,
            operationId,
            selectedVersion,
            ActorId);

        var first = await context.Handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptOutcome.ReviewRequired,
            first.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptReason.ProcessingRestricted,
            first.Value.Reason);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryRetryDirective.None,
            first.Value.RetryDirective);
        Assert.Null(first.Value.RetryAfterSeconds);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed,
            context.Application.Status);
        Assert.Equal(selectedVersion, context.Application.Version);
        WorkspaceStaffOnboardingRecoveryReceipt receipt = Assert.Single(
            context.Receipts.Items);
        Assert.Equal(ActorId, receipt.RecoveryActorId);
        Assert.False(receipt.AccessMutationPerformed);
        int planReads = context.Plans.ReadCount;
        int restrictionReads = context.Restrictions.ReadCount;
        int membershipReads = context.Memberships.ReadCount;
        int staffReads = context.Staff.ReadCount;
        int accessReads = context.Access.ReadCount;

        var replay = await context.Handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(planReads, context.Plans.ReadCount);
        Assert.Equal(restrictionReads, context.Restrictions.ReadCount);
        Assert.Equal(membershipReads, context.Memberships.ReadCount);
        Assert.Equal(staffReads, context.Staff.ReadCount);
        Assert.Equal(accessReads, context.Access.ReadCount);
        Assert.Single(context.Receipts.Items);

        var collision = await context.Handler.HandleAsync(
            command with { RecoveryActorId = "auth:other-owner" },
            CancellationToken.None);

        Assert.True(collision.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .RecoveryIdempotencyConflict,
            collision.Error);
        Assert.Equal(planReads, context.Plans.ReadCount);
        Assert.Equal(restrictionReads, context.Restrictions.ReadCount);
        Assert.Equal(membershipReads, context.Memberships.ReadCount);
        Assert.Equal(staffReads, context.Staff.ReadCount);
        Assert.Equal(accessReads, context.Access.ReadCount);
        Assert.Equal(3, context.RecoveryLock.AcquireCount);
    }

    [Fact]
    public async Task Telemetry_uses_only_bounded_outcome_reason_and_replay_tags()
    {
        List<IReadOnlyDictionary<string, object?>> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, selectedListener) =>
        {
            if (string.Equals(
                    instrument.Meter.Name,
                    WorkspaceStaffOnboardingRecoveryMetrics.MeterName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    instrument.Name,
                    WorkspaceStaffOnboardingRecoveryMetrics.CounterName,
                    StringComparison.Ordinal))
            {
                selectedListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((
            _,
            _,
            tags,
            _) =>
        {
            Dictionary<string, object?> captured =
                new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                captured.Add(tag.Key, tag.Value);
            }

            measurements.Add(captured);
        });
        listener.Start();
        TestContext context = CreateContext(restricted: true);
        AttemptWorkspaceStaffOnboardingRecoveryCommand command = new(
            context.Application.Id,
            Guid.Parse("34333333-3333-3333-3333-333333333333"),
            context.Application.Version,
            ActorId);

        var first = await context.Handler.HandleAsync(
            command,
            CancellationToken.None);
        var replay = await context.Handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Collection(
            measurements,
            measurement => AssertTelemetry(
                measurement,
                expectedReplay: false),
            measurement => AssertTelemetry(
                measurement,
                expectedReplay: true));
    }

    [Fact]
    public async Task Unavailable_organization_commits_retry_receipt_without_changing_application()
    {
        TestContext context = CreateContext(
            restricted: false,
            organizationSnapshots:
            [
                new(OrganizationScopeStatus.Unknown, Revision: 8),
                new(OrganizationScopeStatus.Open, Revision: 9),
                new(OrganizationScopeStatus.Open, Revision: 9),
                new(OrganizationScopeStatus.Open, Revision: 9),
                new(OrganizationScopeStatus.Open, Revision: 9)
            ],
            propertyIds: [PropertyOne, PropertyTwo]);
        long selectedVersion = context.Application.Version;
        Guid operationId = Guid.Parse(
            "44444444-4444-4444-4444-444444444444");
        AttemptWorkspaceStaffOnboardingRecoveryCommand command = new(
            context.Application.Id,
            operationId,
            selectedVersion,
            ActorId);

        var result = await context.Handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptOutcome.RetryLater,
            result.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptReason
                .MembershipUnavailable,
            result.Value.Reason);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryRetryDirective
                .NewOperationAfterDelay,
            result.Value.RetryDirective);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptDto.RetryLaterDelaySeconds,
            result.Value.RetryAfterSeconds);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed,
            context.Application.Status);
        Assert.Equal(selectedVersion, context.Application.Version);
        WorkspaceStaffOnboardingRecoveryReceipt receipt = Assert.Single(
            context.Receipts.Items);
        Assert.Equal(selectedVersion, receipt.SelectedApplicationVersion);
        Assert.Equal(selectedVersion, receipt.ResultingApplicationVersion);
        Assert.Equal(
            WorkspaceRecoveryVerification.RetryLater,
            receipt.OrganizationVerification);
        Assert.Equal(1, context.Organizations.ReadCount);
        Assert.Equal(0, context.Memberships.ReadCount);
        Assert.Equal(0, context.Staff.ReadCount);
        Assert.Equal(0, context.Access.ReadCount);

        var sameOperationReplay = await context.Handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(sameOperationReplay.IsSuccess,
            sameOperationReplay.Error.Code);
        Assert.Equal(result.Value, sameOperationReplay.Value);
        Assert.Equal(1, context.Organizations.ReadCount);
        Assert.Equal(0, context.Memberships.ReadCount);
        Assert.Equal(0, context.Staff.ReadCount);
        Assert.Equal(0, context.Access.ReadCount);

        var newOperation = await context.Handler.HandleAsync(
            command with
            {
                OperationId = Guid.Parse(
                    "45444444-4444-4444-4444-444444444444")
            },
            CancellationToken.None);

        Assert.True(newOperation.IsSuccess, newOperation.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptOutcome.Recovered,
            newOperation.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryRetryDirective.None,
            newOperation.Value.RetryDirective);
        Assert.Null(newOperation.Value.RetryAfterSeconds);
        Assert.Equal(WorkspaceStaffOnboardingState.Completed,
            context.Application.Status);
        Assert.Equal(selectedVersion + 1, context.Application.Version);
        Assert.Equal(2, context.Receipts.Items.Count);
        Assert.Equal(5, context.Organizations.ReadCount);
        Assert.Equal(2, context.Memberships.ReadCount);
        Assert.Equal(2, context.Staff.ReadCount);
        Assert.Equal(2, context.Access.ReadCount);
        Assert.All(context.Access.Requests, request => Assert.Equal(
            [
                WorkspaceAccessScopes.CreateProperty(TenantId, PropertyOne),
                WorkspaceAccessScopes.CreateProperty(TenantId, PropertyTwo)
            ],
            request.ExpectedTargetScopes));
    }

    [Fact]
    public async Task Drift_after_access_first_is_retryable_and_does_not_complete()
    {
        TestContext context = CreateContext(
            restricted: false,
            organizationSnapshots:
            [
                new(OrganizationScopeStatus.Open, Revision: 10),
                new(OrganizationScopeStatus.Open, Revision: 10),
                new(OrganizationScopeStatus.Open, Revision: 11),
                new(OrganizationScopeStatus.Open, Revision: 11)
            ]);
        long selectedVersion = context.Application.Version;

        var result = await context.Handler.HandleAsync(
            new(
                context.Application.Id,
                Guid.Parse(
                    "55555555-5555-5555-5555-555555555555"),
                selectedVersion,
                ActorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptOutcome.RetryLater,
            result.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptReason
                .MembershipUnavailable,
            result.Value.Reason);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed,
            context.Application.Status);
        Assert.Equal(selectedVersion, context.Application.Version);
        WorkspaceStaffOnboardingRecoveryReceipt receipt = Assert.Single(
            context.Receipts.Items);
        Assert.Equal(11, receipt.OrganizationRevision);
        Assert.Equal(
            WorkspaceRecoveryVerification.RetryLater,
            receipt.OrganizationVerification);
        Assert.Equal(4, context.Organizations.ReadCount);
        Assert.Equal(2, context.Memberships.ReadCount);
        Assert.Equal(1, context.Staff.ReadCount);
        Assert.Equal(1, context.Access.ReadCount);
    }

    [Fact]
    public async Task Drift_after_staff_first_is_retryable_and_does_not_complete()
    {
        TestContext context = CreateContext(
            restricted: false,
            propertyIds: [PropertyOne, PropertyTwo],
            secondStaffStateDigest: new string('f', 64));
        long selectedVersion = context.Application.Version;

        var result = await context.Handler.HandleAsync(
            new(
                context.Application.Id,
                Guid.Parse(
                    "56555555-5555-5555-5555-555555555555"),
                selectedVersion,
                ActorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptOutcome.RetryLater,
            result.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptReason.StaffUnavailable,
            result.Value.Reason);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed,
            context.Application.Status);
        Assert.Equal(selectedVersion, context.Application.Version);
        WorkspaceStaffOnboardingRecoveryReceipt receipt = Assert.Single(
            context.Receipts.Items);
        Assert.Equal(
            WorkspaceRecoveryVerification.RetryLater,
            receipt.StaffVerification);
        Assert.Equal(new string('f', 64), receipt.StaffStateProofSha256);
        Assert.Equal(4, context.Organizations.ReadCount);
        Assert.Equal(2, context.Memberships.ReadCount);
        Assert.Equal(2, context.Staff.ReadCount);
        Assert.Equal(1, context.Access.ReadCount);
    }

    [Fact]
    public async Task Drift_after_access_first_proof_is_retryable_and_does_not_complete()
    {
        TestContext context = CreateContext(
            restricted: false,
            propertyIds: [PropertyOne, PropertyTwo],
            secondAccessFactsDigest: new string('f', 64));
        long selectedVersion = context.Application.Version;

        var result = await context.Handler.HandleAsync(
            new(
                context.Application.Id,
                Guid.Parse(
                    "57555555-5555-5555-5555-555555555555"),
                selectedVersion,
                ActorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptOutcome.RetryLater,
            result.Value.Outcome);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptReason.AccessUnavailable,
            result.Value.Reason);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed,
            context.Application.Status);
        Assert.Equal(selectedVersion, context.Application.Version);
        WorkspaceStaffOnboardingRecoveryReceipt receipt = Assert.Single(
            context.Receipts.Items);
        Assert.Equal(
            WorkspaceRecoveryVerification.RetryLater,
            receipt.AccessVerification);
        Assert.Equal(new string('f', 64), receipt.AccessFactsSha256);
        Assert.Equal(4, context.Organizations.ReadCount);
        Assert.Equal(2, context.Memberships.ReadCount);
        Assert.Equal(2, context.Staff.ReadCount);
        Assert.Equal(2, context.Access.ReadCount);
    }

    [Fact]
    public async Task Exact_recovery_records_decision_after_final_proof()
    {
        DateTimeOffset organizationFirst = Now.AddMinutes(5);
        DateTimeOffset staffFirst = Now.AddMinutes(5).AddSeconds(10);
        DateTimeOffset accessFirst = Now.AddMinutes(5).AddSeconds(20);
        DateTimeOffset organizationSecond = Now.AddMinutes(5).AddSeconds(30);
        DateTimeOffset staffSecond = Now.AddMinutes(5).AddSeconds(40);
        DateTimeOffset accessSecond = Now.AddMinutes(5).AddSeconds(50);
        DateTimeOffset decision = Now.AddMinutes(6);
        TestContext context = CreateContext(
            restricted: false,
            propertyIds: [PropertyOne, PropertyTwo],
            staffObservedAtUtc: [staffFirst, staffSecond],
            accessObservedAtUtc: [accessFirst, accessSecond],
            organizationClock: new SequenceClock(
                [organizationFirst, organizationSecond]),
            decisionClock: new SequenceClock([decision]));
        long selectedVersion = context.Application.Version;

        var result = await context.Handler.HandleAsync(
            new(
                context.Application.Id,
                Guid.Parse(
                    "58555555-5555-5555-5555-555555555555"),
                selectedVersion,
                ActorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRecoveryAttemptOutcome.Recovered,
            result.Value.Outcome);
        WorkspaceStaffOnboardingRecoveryReceipt receipt = Assert.Single(
            context.Receipts.Items);
        Assert.Equal(organizationSecond, receipt.OrganizationObservedAtUtc);
        Assert.Equal(staffSecond, receipt.StaffObservedAtUtc);
        Assert.Equal(accessSecond, receipt.AccessObservedAtUtc);
        Assert.Equal(decision, receipt.RecordedAtUtc);
        Assert.Equal(decision, context.Application.LastChangedAtUtc);
        Assert.True(receipt.RecordedAtUtc > accessSecond);
    }

    private static TestContext CreateContext(
        bool restricted,
        IReadOnlyList<OrganizationScopeSnapshot>? organizationSnapshots = null,
        IReadOnlyCollection<Guid>? propertyIds = null,
        string? secondStaffStateDigest = null,
        string? secondAccessFactsDigest = null,
        IReadOnlyList<DateTimeOffset>? staffObservedAtUtc = null,
        IReadOnlyList<DateTimeOffset>? accessObservedAtUtc = null,
        ISystemClock? organizationClock = null,
        ISystemClock? decisionClock = null)
    {
        WorkspaceStaffOnboarding application = CreateFailedApplication();
        Guid[] selectedPropertyIds = propertyIds?.Order().ToArray() ?? [];
        Guid profileId = Guid.Parse(
            "66666666-6666-6666-6666-666666666666");
        AccessProfileDto profile = new(
            profileId,
            WorkspaceAccessScopes.Create(TenantId).Value,
            WorkspaceAccessProfileSeeds.FrontDeskKey,
            "Front desk",
            string.Empty,
            AccessProfileStatus.Active,
            Version: 3,
            WorkspaceAccessProfileSeeds.FrontDesk.Permissions.ToArray(),
            AssignmentCount: 1,
            Now,
            Now);
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            application.SourceId,
            TenantId,
            application.SourceKind,
            profileId,
            profile.Key,
            selectedPropertyIds,
            SubjectId,
            Now).Value;
        Assert.True(plan.Activate(Now.AddSeconds(1)).IsSuccess);

        FakeApplicationRepository applications = new(application);
        FakeRecoveryLock recoveryLock = new();
        FakeReceiptRepository receipts = new();
        FakePlanRepository plans = new(plan);
        FakeRestrictionRepository restrictions = new(
            application,
            restricted);
        FakeOrganizationLifecycle organizations = new(
            organizationSnapshots ??
            [new(OrganizationScopeStatus.Open, Revision: 7)]);
        FakeMembershipReader memberships = new(
            application,
            MembershipId,
            membershipVersion: 5);
        FakeStaffVerifier staff = new(
            application,
            selectedPropertyIds,
            secondStaffStateDigest,
            staffObservedAtUtc);
        FakeAccessSnapshotReader access = new(
            application,
            profile,
            secondAccessFactsDigest,
            accessObservedAtUtc);
        FakeAccessProfileProvisioner profiles = new(profile);
        WorkspaceStaffAccessPlanPolicy planPolicy = new(
            profiles,
            new OwnerRoleProvisioner(),
            new AllowAuthorization(),
            new ActiveProperties());
        WorkspaceOrganizationMembershipObserver observer = new(
            [organizations],
            [memberships],
            organizationClock ?? new FixedClock());
        AttemptWorkspaceStaffOnboardingRecoveryCommandHandler handler = new(
            recoveryLock,
            receipts,
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            restrictions,
            plans,
            planPolicy,
            observer,
            [staff],
            access,
            decisionClock ?? new FixedClock());
        return new(
            handler,
            application,
            recoveryLock,
            receipts,
            plans,
            restrictions,
            organizations,
            memberships,
            staff,
            access);
    }

    private static WorkspaceStaffOnboarding CreateFailedApplication()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboarding.Create(
                Guid.Parse(
                    "77777777-7777-7777-7777-777777777777"),
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                Guid.Parse(
                    "88888888-8888-8888-8888-888888888888"),
                SubjectId,
                "manager@example.test",
                "Manager",
                legalName: null,
                "manager@example.test",
                workPhone: null,
                employeeNumber: null,
                jobTitle: "Manager",
                department: "Operations",
                Now).Value;
        Assert.True(application.ObserveInvitationAccepted(
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.MarkStaffReady(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.BindAcceptedMembership(
            MembershipId,
            membershipVersion: 5,
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(application.Fail(
            "AccessControl.ProfilePermissionEscalation",
            Now.AddMinutes(4)).IsSuccess);
        return application;
    }

    private static void AssertTelemetry(
        IReadOnlyDictionary<string, object?> measurement,
        bool expectedReplay)
    {
        Assert.Equal(
            ["outcome", "reason", "replay"],
            measurement.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("review-required", measurement["outcome"]);
        Assert.Equal("processing-restricted", measurement["reason"]);
        Assert.Equal(expectedReplay, measurement["replay"]);
        Assert.DoesNotContain(
            measurement.Values,
            value => string.Equals(
                value?.ToString(),
                TenantId,
                StringComparison.Ordinal) ||
                string.Equals(
                    value?.ToString(),
                    SubjectId,
                    StringComparison.Ordinal) ||
                string.Equals(
                    value?.ToString(),
                    ActorId,
                    StringComparison.Ordinal));
    }

    private sealed record TestContext(
        AttemptWorkspaceStaffOnboardingRecoveryCommandHandler Handler,
        WorkspaceStaffOnboarding Application,
        FakeRecoveryLock RecoveryLock,
        FakeReceiptRepository Receipts,
        FakePlanRepository Plans,
        FakeRestrictionRepository Restrictions,
        FakeOrganizationLifecycle Organizations,
        FakeMembershipReader Memberships,
        FakeStaffVerifier Staff,
        FakeAccessSnapshotReader Access);

    private sealed class FakeRecoveryLock
        : IWorkspaceStaffOnboardingRecoveryOperationLock
    {
        public int AcquireCount { get; private set; }

        public Task AcquireAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            this.AcquireCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiptRepository
        : IWorkspaceStaffOnboardingRecoveryReceiptRepository
    {
        public List<WorkspaceStaffOnboardingRecoveryReceipt> Items { get; } = [];

        public Task<WorkspaceStaffOnboardingRecoveryReceipt?>
            FindByOperationAsync(
                Guid operationId,
                CancellationToken cancellationToken) => Task.FromResult(
                    this.Items.SingleOrDefault(receipt =>
                        receipt.OperationId == operationId));

        public Task<WorkspaceStaffOnboardingRecoveryReceipt?> FindLatestAsync(
            Guid applicationId,
            CancellationToken cancellationToken) => Task.FromResult(
                this.Items
                    .Where(receipt => receipt.ApplicationId == applicationId)
                    .OrderByDescending(receipt => receipt.RecordedAtUtc)
                    .FirstOrDefault());

        public Task AddAsync(
            WorkspaceStaffOnboardingRecoveryReceipt receipt,
            CancellationToken cancellationToken)
        {
            this.Items.Add(receipt);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApplicationRepository(
        WorkspaceStaffOnboarding application)
        : IWorkspaceStaffOnboardingRepository
    {
        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid applicationId,
            CancellationToken cancellationToken) => Task.FromResult<
                WorkspaceStaffOnboardingCoordinate?>(applicationId ==
                    application.Id
                    ? new(application.Id, application.SourceKind,
                        application.SourceId)
                    : null);

        public Task<WorkspaceStaffOnboarding?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) => Task.FromResult(
                applicationId == application.Id ? application : null);

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
            Guid applicationId,
            CancellationToken cancellationToken) => this.GetAsync(
                applicationId,
                cancellationToken);

        public Task<WorkspaceStaffOnboarding?>
            GetOperationalBySourceAndSubjectAsync(
                WorkspaceStaffOnboardingSource sourceKind,
                Guid sourceId,
                string subjectId,
                CancellationToken cancellationToken) => Task.FromResult<
                    WorkspaceStaffOnboarding?>(application.SourceKind ==
                        sourceKind && application.SourceId == sourceId &&
                        application.SubjectId == subjectId
                        ? application
                        : null);

        public async Task<Guid?> FindIdBySourceAndSubjectAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) =>
            (await this.GetOperationalBySourceAndSubjectAsync(
                sourceKind,
                sourceId,
                subjectId,
                cancellationToken))?.Id;

        public Task<WorkspaceStaffOnboarding?> GetByClaimAsync(
            Guid claimId,
            CancellationToken cancellationToken) => Task.FromResult<
                WorkspaceStaffOnboarding?>(null);

        public Task<IReadOnlyList<WorkspaceStaffOnboarding>>
            ListActiveBySourceAsync(
                WorkspaceStaffOnboardingSource sourceKind,
                Guid sourceId,
                CancellationToken cancellationToken) => Task.FromResult<
                    IReadOnlyList<WorkspaceStaffOnboarding>>(
                        sourceKind == application.SourceKind &&
                        sourceId == application.SourceId
                            ? [application]
                            : []);

        public Task<WorkspaceStaffOnboardingSummaryListResponse> ListActionableAsync(
            PageRequest page,
            CancellationToken cancellationToken) => Task.FromResult(
                new WorkspaceStaffOnboardingSummaryListResponse(
                    [], page.Page, page.PageSize, HasMore: false));

        public Task ReloadAsync(
            WorkspaceStaffOnboarding selected,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddAsync(
            WorkspaceStaffOnboarding selected,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePlanRepository(WorkspaceStaffAccessPlan plan)
        : IWorkspaceStaffAccessPlanRepository
    {
        public int ReadCount { get; private set; }

        public Task<WorkspaceStaffAccessPlan?> GetAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            this.ReadCount++;
            return Task.FromResult<WorkspaceStaffAccessPlan?>(
                sourceId == plan.Id ? plan : null);
        }

        public Task<IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>>
            GetManyAsync(
                IReadOnlyCollection<Guid> sourceIds,
                CancellationToken cancellationToken) => Task.FromResult<
                    IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>>(
                        sourceIds.Contains(plan.Id)
                            ? new Dictionary<Guid, WorkspaceStaffAccessPlan>
                            {
                                [plan.Id] = plan
                            }
                            : []);

        public Task AddAsync(
            WorkspaceStaffAccessPlan selected,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeRestrictionRepository
        : IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
    {
        private readonly WorkspaceStaffOnboardingProcessingRestrictionProjection
            projection;

        public FakeRestrictionRepository(
            WorkspaceStaffOnboarding application,
            bool restricted)
        {
            this.projection =
                WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
                    application.ScopeId,
                    application.Id,
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                    application.CreatedAtUtc).Value;
            if (restricted)
            {
                Assert.True(this.projection.Apply(
                    expectedRevision: 0,
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                    application.LastChangedAtUtc).IsSuccess);
            }
        }

        public int ReadCount { get; private set; }

        public Task<WorkspaceStaffOnboardingProcessingRestrictionProjection?>
            GetAsync(
                Guid applicationId,
                CancellationToken cancellationToken)
        {
            this.ReadCount++;
            return Task.FromResult<
                WorkspaceStaffOnboardingProcessingRestrictionProjection?>(
                    applicationId == this.projection.ApplicationId
                        ? this.projection
                        : null);
        }

        public Task AddAsync(
            WorkspaceStaffOnboardingProcessingRestrictionProjection selected,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeOrganizationLifecycle(
        IReadOnlyList<OrganizationScopeSnapshot> snapshots)
        : IOrganizationScopeLifecycle
    {
        private int index;
        public int ReadCount { get; private set; }

        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            this.ReadCount++;
            OrganizationScopeSnapshot result = snapshots[
                Math.Min(this.index, snapshots.Count - 1)];
            this.index++;
            return Task.FromResult(result);
        }

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "Export is outside this test.");

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "Destroy is outside this test.");
    }

    private sealed class FakeMembershipReader(
        WorkspaceStaffOnboarding application,
        Guid membershipId,
        long membershipVersion)
        : IOrganizationMembershipReader
    {
        public int ReadCount { get; private set; }

        public Task<OrganizationMembershipSnapshotDto?> FindAsync(
            Guid organizationId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            this.ReadCount++;
            return Task.FromResult<OrganizationMembershipSnapshotDto?>(new(
                organizationId,
                OrganizationStatus.Active,
                new OrganizationMembershipDto(
                    membershipId,
                    organizationId,
                    application.SubjectId,
                    OrganizationMembershipRole.Member,
                    OrganizationMembershipStatus.Active,
                    membershipVersion,
                    Now,
                    Now)));
        }
    }

    private sealed class FakeStaffVerifier(
        WorkspaceStaffOnboarding application,
        IReadOnlyCollection<Guid> propertyIds,
        string? secondStateDigest,
        IReadOnlyList<DateTimeOffset>? observedAtUtc)
        : IStaffOnboardingStateVerifier
    {
        public int ReadCount { get; private set; }

        public Task<StaffOnboardingStateVerificationResult> VerifyAsync(
            StaffOnboardingStateVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            this.ReadCount++;
            int index = this.ReadCount - 1;
            string stateDigest = index > 0 && secondStateDigest is not null
                ? secondStateDigest
                : Digest;
            DateTimeOffset observed = observedAtUtc is { Count: > 0 }
                ? observedAtUtc[Math.Min(index, observedAtUtc.Count - 1)]
                : Now.AddMinutes(5);
            return Task.FromResult(new StaffOnboardingStateVerificationResult(
                StaffOnboardingStateVerificationStatus.Equivalent,
                StaffOnboardingStateVerificationReason.Equivalent,
                ProofContractVersion: 1,
                OperationResultVersion: application.Version - 1,
                CurrentMemberVersion: 2,
                TenantRevision: 9,
                PropertyCount: propertyIds.Count,
                PropertySetSha256: Digest,
                StateProofSha256: stateDigest,
                ObservedAtUtc: observed));
        }
    }

    private sealed class FakeAccessSnapshotReader(
        WorkspaceStaffOnboarding application,
        AccessProfileDto profile,
        string? secondFactsDigest,
        IReadOnlyList<DateTimeOffset>? observedAtUtc)
        : IAccessControlSubjectAuthoritySnapshotReader
    {
        public int ReadCount { get; private set; }
        public List<AccessControlSubjectAuthoritySnapshotRequest> Requests
        {
            get;
        } = [];

        public Task<AccessControlSubjectAuthoritySnapshot> ReadAsync(
            AccessControlSubjectAuthoritySnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            this.ReadCount++;
            this.Requests.Add(request);
            int index = this.ReadCount - 1;
            string factsDigest = index > 0 && secondFactsDigest is not null
                ? secondFactsDigest
                : Digest;
            DateTimeOffset observed = observedAtUtc is { Count: > 0 }
                ? observedAtUtc[Math.Min(index, observedAtUtc.Count - 1)]
                : Now.AddMinutes(5);
            AccessScope workspace = WorkspaceAccessScopes.Create(
                application.ScopeId);
            return Task.FromResult(new AccessControlSubjectAuthoritySnapshot(
                AccessControlAuthoritySnapshotStatus.Stable,
                ContractVersion: 2,
                AccessSubjectKind.User,
                application.SubjectId,
                workspace,
                request.ExpectedTargetScopes
                    .OrderBy(scope => scope.Value, StringComparer.Ordinal)
                    .ToArray(),
                ManagementRevision: 12,
                observed,
                EarliestExpiryAtUtc: null,
                factsDigest,
                [
                    new AccessControlAuthorityRoleGrant(
                        Guid.Parse(
                            "a1111111-1111-1111-1111-111111111111"),
                        Guid.Parse(
                            "a2222222-2222-2222-2222-222222222222"),
                        WorkspaceAccessRoles.MembershipMarker,
                        [],
                        workspace,
                        ExpiresAtUtc: null)
                ],
                request.ExpectedTargetScopes
                    .OrderBy(scope => scope.Value, StringComparer.Ordinal)
                    .Select((scope, targetIndex) =>
                        new AccessControlAuthorityProfileGrant(
                            CreateAssignmentId(targetIndex),
                            scope,
                            profile.Id,
                            workspace,
                            profile.Key,
                            profile.Status,
                            profile.Version,
                            profile.Permissions
                                .Order(StringComparer.Ordinal)
                                .ToArray()))
                    .ToArray()));
        }

        private static Guid CreateAssignmentId(int index)
        {
            byte[] bytes = Guid.Parse(
                "a3333333-3333-3333-3333-333333333333").ToByteArray();
            bytes[^1] = checked((byte)(index + 1));
            return new Guid(bytes);
        }
    }

    private sealed class FakeAccessProfileProvisioner(AccessProfileDto profile)
        : IAccessProfileProvisioner
    {
        public Task<AccessProfileDto> EnsureProfileAsync(
            AccessScope ownerScope,
            AccessProfileDefinition definition,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);

        public Task<AccessProfileDto?> FindProfileByKeyAsync(
            AccessScope ownerScope,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AccessProfileDto?>(
                key == profile.Key ? profile : null);

        public Task<AccessProfileAssignmentSet> GetSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessProfileAssignmentSet(
                subject, ownerScope, []));

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
                UnassignedCount: 0));
    }

    private sealed class OwnerRoleProvisioner : IAccessControlRoleProvisioner
    {
        public Task EnsureRoleAsync(
            AccessControlRoleDefinition role,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AccessControlAssignmentRemovalOutcome>
            RemoveAssignmentAsync(
                AccessSubject subject,
                string roleName,
                AccessScope scope,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessControlAssignmentRemovalOutcome.NotFound);

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(roleName == WorkspaceAccessRoles.Owner);

        public Task<AccessControlPage<AccessControlRoleAssignment>>
            ListAssignmentsAsync(
                string roleName,
                AccessScope scope,
                int page,
                int pageSize,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessControlPage<
                AccessControlRoleAssignment>([], page, pageSize, false));
    }

    private sealed class AllowAuthorization : IAccessAuthorizationService
    {
        public Task<AccessDecision> AuthorizeAsync(
            AccessRequirement requirement,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccessDecision.Allowed());
    }

    private sealed class ActiveProperties : IWorkspacePropertyProjectionRepository
    {
        public Task<bool> AreAllActiveAsync(
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ApplyAsync(
            WorkspacePropertyProjectionWriteModel property,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now.AddMinutes(6);
    }

    private sealed class SequenceClock(IReadOnlyList<DateTimeOffset> values)
        : ISystemClock
    {
        private int index;

        public DateTimeOffset UtcNow
        {
            get
            {
                DateTimeOffset value = values[
                    Math.Min(this.index, values.Count - 1)];
                this.index++;
                return value;
            }
        }
    }
}

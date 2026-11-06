namespace BunkFy.Modules.Workspaces.Tests.Application;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.Organizations.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffHistoricalNoProvisionCommandHandlerTests
{
    private const string TenantId = "aaaaaaaa-1111-1111-1111-111111111111";
    private const string OtherTenantId =
        "bbbbbbbb-1111-1111-1111-111111111111";
    private static readonly Guid ApplicationId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SourceId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OperationId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ReceiptId =
        Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ManifestId =
        Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        15,
        0,
        0,
        TimeSpan.Zero);
    private static readonly string EvidenceSha256 = new('a', 64);

    [Fact]
    public async Task Exact_terminal_review_redacts_and_excludes_without_anchor_emission()
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true);

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> result =
            await test.Handler.HandleAsync(
                Command(test.ApplicationVersion, test.ApplicationStatus),
                CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.False(result.Value.AlreadyReviewed);
        Assert.Equal(1, test.CrossGraph.AcquireCount);
        Assert.Equal("subject:historical", test.Staff.LastExpectedSubjectId);
        Assert.Equal(2, test.Organizations.SnapshotCalls);

        await test.DbContext.SaveChangesAsync();
        test.DbContext.ChangeTracker.Clear();
        WorkspaceStaffOnboarding application = await test.DbContext
            .StaffOnboardingApplications.SingleAsync();
        WorkspaceStaffHistoricalNoProvisionReceipt receipt = await test
            .DbContext.StaffHistoricalNoProvisionReceipts.SingleAsync();
        Assert.Equal(
            WorkspaceStaffOnboardingState.Superseded,
            application.Status);
        Assert.Equal(test.ApplicationVersion + 1, application.Version);
        Assert.Equal(receipt.CreateSubjectPseudonym(), application.SubjectId);
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
        Assert.False(application.HasIdentityAnchorState);
        Assert.True(receipt.HasValidCanonicalProof());
        Assert.DoesNotContain("subject:historical", receipt.CanonicalSha256);
        Assert.Empty(application.DomainEvents);
        Assert.Empty(receipt.DomainEvents);

        WorkspaceStaffIdentityAnchorCutoverSourceReader cutover = new(
            test.DbContext);
        Assert.Empty((await cutover.ListRelevantPageAsync(
            afterApplicationId: null,
            pageSize: 10,
            CancellationToken.None)).Records);

        WorkspaceStaffIdentityAnchorSweepRepository sweep = new(test.DbContext);
        Result<WorkspaceStaffIdentityAnchorSweepPage> page = await sweep
            .PreparePageAsync(
                TenantId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                batchSize: 10,
                Now.AddMinutes(1),
                CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error.Code);
        Assert.Empty(page.Value.Candidates);
        Assert.True(page.Value.ReachedEnd);
    }

    [Fact]
    public async Task Active_application_is_superseded_when_source_is_terminal()
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Expired,
            terminalApplication: false);

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> result =
            await test.Handler.HandleAsync(
                Command(test.ApplicationVersion, test.ApplicationStatus) with
                {
                    ExpectedOrganizationsSourceStatus =
                        WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                            .InvitationExpired
                },
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingState.Superseded,
            result.Value.ResultApplicationStatus);
        Assert.Equal(
            test.ApplicationVersion + 1,
            result.Value.ResultApplicationVersion);
    }

    [Theory]
    [InlineData(OrganizationInvitationStatus.Pending)]
    [InlineData(OrganizationInvitationStatus.Accepted)]
    public async Task Current_organization_authority_is_blocked(
        OrganizationInvitationStatus status)
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: status,
            terminalApplication: true);
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus expected = status ==
            OrganizationInvitationStatus.Pending
                ? WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .InvitationPending
                : WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .InvitationAccepted;

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> result =
            await test.Handler.HandleAsync(
                Command(test.ApplicationVersion, test.ApplicationStatus) with
                {
                    ExpectedOrganizationsSourceStatus = expected
                },
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict,
            result.Error);
        Assert.Empty(test.DbContext.StaffHistoricalNoProvisionReceipts);
    }

    [Theory]
    [InlineData(
        OrganizationEnrollmentLinkStatus.Disabled,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus
            .EnrollmentLinkDisabled,
        true)]
    [InlineData(
        OrganizationEnrollmentLinkStatus.Active,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus
            .EnrollmentLinkActive,
        false)]
    [InlineData(
        OrganizationEnrollmentLinkStatus.CapacityReached,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus
            .EnrollmentLinkCapacityReached,
        false)]
    public async Task Enrollment_authority_uses_the_same_exact_terminal_gate(
        OrganizationEnrollmentLinkStatus sourceStatus,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus expectedStatus,
        bool succeeds)
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true,
            sourceKind: WorkspaceStaffOnboardingSource.EnrollmentLink,
            enrollmentStatus: sourceStatus);

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> result =
            await test.Handler.HandleAsync(
                Command(test.ApplicationVersion, test.ApplicationStatus) with
                {
                    ExpectedOrganizationsSourceStatus = expectedStatus
                },
                CancellationToken.None);

        Assert.Equal(succeeds, result.IsSuccess);
        if (!succeeds)
        {
            Assert.Equal(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict,
                result.Error);
        }
    }

    [Theory]
    [InlineData(false, StaffIdentityProvisioningAnchorCutoverDisposition.Ambiguous)]
    [InlineData(true, StaffIdentityProvisioningAnchorCutoverDisposition.Conflict)]
    public async Task Staff_non_absent_or_non_ambiguous_evidence_is_blocked(
        bool absent,
        StaffIdentityProvisioningAnchorCutoverDisposition disposition)
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true,
            absent,
            disposition);

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> result =
            await test.Handler.HandleAsync(
                Command(test.ApplicationVersion, test.ApplicationStatus),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict,
            result.Error);
        Assert.Equal(0, test.Organizations.ExportCalls);
        Assert.Empty(test.DbContext.StaffHistoricalNoProvisionReceipts);
    }

    [Fact]
    public async Task Malformed_staff_outcome_batch_is_a_conflict()
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true,
            duplicateStaffOutcome: true);

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> result =
            await test.Handler.HandleAsync(
                Command(test.ApplicationVersion, test.ApplicationStatus),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict,
            result.Error);
        Assert.Equal(0, test.Organizations.ExportCalls);
        Assert.Empty(test.DbContext.StaffHistoricalNoProvisionReceipts);
    }

    [Fact]
    public async Task Exact_operation_replays_but_any_request_divergence_conflicts()
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true);
        ReviewWorkspaceStaffHistoricalNoProvisionCommand command = Command(
            test.ApplicationVersion,
            test.ApplicationStatus);
        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> first =
            await test.Handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        await test.DbContext.SaveChangesAsync();

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> replay =
            await test.Handler.HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.True(replay.Value.AlreadyReviewed);
        Assert.Equal(first.Value.ReceiptId, replay.Value.ReceiptId);
        Assert.Equal(1, test.Staff.OutcomeCalls);
        Assert.Equal(1, test.Organizations.ExportCalls);

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> conflict =
            await test.Handler.HandleAsync(
                command with { ExternalEvidenceSha256 = new string('b', 64) },
                CancellationToken.None);
        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict,
            conflict.Error);
        Assert.Single(test.DbContext.StaffHistoricalNoProvisionReceipts);
    }

    [Fact]
    public async Task Resurrected_or_anchored_result_is_neither_replayed_nor_excluded()
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true);
        ReviewWorkspaceStaffHistoricalNoProvisionCommand command = Command(
            test.ApplicationVersion,
            test.ApplicationStatus);
        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> first =
            await test.Handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        await test.DbContext.SaveChangesAsync();
        test.DbContext.ChangeTracker.Clear();

        WorkspaceStaffOnboarding application = await test.DbContext
            .StaffOnboardingApplications.SingleAsync();
        test.DbContext.Entry(application)
            .Property(candidate => candidate.DisplayName)
            .CurrentValue = "resurrected profile";
        test.DbContext.Entry(application)
            .Property(candidate => candidate.FailureCode)
            .CurrentValue = "resurrected-failure";
        test.DbContext.Entry(application)
            .Property(candidate => candidate.StaffMemberId)
            .CurrentValue = Guid.NewGuid();
        await test.DbContext.SaveChangesAsync();
        test.DbContext.ChangeTracker.Clear();

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> replay =
            await test.Handler.HandleAsync(command, CancellationToken.None);
        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict,
            replay.Error);

        WorkspaceStaffIdentityAnchorCutoverSourceReader cutover = new(
            test.DbContext);
        WorkspaceStaffIdentityAnchorSourcePage sourcePage = await cutover
            .ListRelevantPageAsync(
                afterApplicationId: null,
                pageSize: 10,
                CancellationToken.None);
        Assert.Equal(
            ApplicationId,
            Assert.Single(sourcePage.Records).ApplicationId);

        WorkspaceStaffIdentityAnchorSweepRepository sweep = new(
            test.DbContext);
        Result<WorkspaceStaffIdentityAnchorSweepPage> sweepPage = await sweep
            .PreparePageAsync(
                TenantId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                batchSize: 10,
                Now.AddMinutes(1),
                CancellationToken.None);
        Assert.True(sweepPage.IsSuccess, sweepPage.Error.Code);
        Assert.Equal(
            ApplicationId,
            Assert.Single(sweepPage.Value.Candidates).ApplicationId);
    }

    [Fact]
    public async Task Invalid_canonical_receipt_is_not_replayed()
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true);
        ReviewWorkspaceStaffHistoricalNoProvisionCommand command = Command(
            test.ApplicationVersion,
            test.ApplicationStatus);
        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> first =
            await test.Handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        await test.DbContext.SaveChangesAsync();

        WorkspaceStaffHistoricalNoProvisionReceipt receipt = await test
            .DbContext.StaffHistoricalNoProvisionReceipts.SingleAsync();
        test.DbContext.Entry(receipt)
            .Property(candidate => candidate.CanonicalSha256)
            .CurrentValue = new string('c', 64);

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> replay =
            await test.Handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict,
            replay.Error);
    }

    [Theory]
    [InlineData(OrganizationEvidenceFailure.Missing)]
    [InlineData(OrganizationEvidenceFailure.Duplicate)]
    [InlineData(OrganizationEvidenceFailure.Stale)]
    [InlineData(OrganizationEvidenceFailure.FinalRevisionDrift)]
    public async Task Malformed_or_drifting_organization_evidence_fails_closed(
        OrganizationEvidenceFailure failure)
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true,
            organizationFailure: failure);

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> result =
            await test.Handler.HandleAsync(
                Command(test.ApplicationVersion, test.ApplicationStatus),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict,
            result.Error);
        Assert.Empty(test.DbContext.StaffHistoricalNoProvisionReceipts);
    }

    [Fact]
    public async Task Noncanonical_tenant_fails_before_any_lock_or_evidence_call()
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true,
            scopeId: TenantId.ToUpperInvariant());

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> result =
            await test.Handler.HandleAsync(
                Command(test.ApplicationVersion, test.ApplicationStatus),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors
                .TenantRequired,
            result.Error);
        Assert.Equal(0, test.CrossGraph.AcquireCount);
        Assert.Equal(0, test.Staff.OutcomeCalls);
    }

    [Fact]
    public async Task Cross_tenant_scope_fails_closed_before_external_evidence()
    {
        await using TestContext test = await CreateAsync(
            sourceStatus: OrganizationInvitationStatus.Revoked,
            terminalApplication: true,
            scopeId: OtherTenantId);

        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult> result =
            await test.Handler.HandleAsync(
                Command(test.ApplicationVersion, test.ApplicationStatus),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict,
            result.Error);
        Assert.Equal(1, test.CrossGraph.AcquireCount);
        Assert.Equal(0, test.Staff.OutcomeCalls);
        Assert.Equal(0, test.Organizations.ExportCalls);
        Assert.Empty(test.DbContext.StaffHistoricalNoProvisionReceipts);
    }

    private static ReviewWorkspaceStaffHistoricalNoProvisionCommand Command(
        long applicationVersion,
        WorkspaceStaffOnboardingState applicationStatus) =>
        new(
            OperationId,
            ApplicationId,
            applicationVersion,
            applicationStatus,
            ExpectedOrganizationsScopeRevision: 4,
            ExpectedOrganizationsSourceVersion: 7,
            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                .InvitationRevoked,
            ManifestId,
            EvidenceSha256,
            ReviewerId: "operator:security-review");

    private static async Task<TestContext> CreateAsync(
        OrganizationInvitationStatus sourceStatus,
        bool terminalApplication,
        bool absent = true,
        StaffIdentityProvisioningAnchorCutoverDisposition disposition =
            StaffIdentityProvisioningAnchorCutoverDisposition.Ambiguous,
        bool duplicateStaffOutcome = false,
        OrganizationEvidenceFailure organizationFailure =
            OrganizationEvidenceFailure.None,
        WorkspaceStaffOnboardingSource sourceKind =
            WorkspaceStaffOnboardingSource.Invitation,
        OrganizationEnrollmentLinkStatus enrollmentStatus =
            OrganizationEnrollmentLinkStatus.Disabled,
        string scopeId = TenantId)
    {
        WorkspacesDbContext dbContext = new(
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            new ScopeContext(TenantId));
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboarding.Create(
                ApplicationId,
                TenantId,
                sourceKind,
                SourceId,
                "subject:historical",
                "historical@example.test",
                "Historical Person",
                legalName: "Historical Person",
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                Now.AddMinutes(-10)).Value;
        if (terminalApplication)
        {
            Assert.True(application.Supersede(Now.AddMinutes(-9)).IsSuccess);
        }

        dbContext.StaffOnboardingApplications.Add(application);
        dbContext.Entry(application)
            .Property(candidate => candidate.IdentityAnchorSweepOrdinal)
            .CurrentValue = 1;
        await dbContext.SaveChangesAsync();
        long applicationVersion = application.Version;
        WorkspaceStaffOnboardingState applicationStatus = application.Status;
        dbContext.ChangeTracker.Clear();

        WorkspaceStaffOnboardingRepository applications = new(dbContext);
        RecordingOperationLock operationLock = new();
        RecordingWorkspaceCrossGraphMutationLock crossGraph = new();
        FakeStaffEvidence staff = new(
            absent,
            disposition,
            duplicateStaffOutcome);
        FakeOrganizations organizations = new(
            sourceStatus,
            organizationFailure,
            enrollmentStatus);
        WorkspaceStaffHistoricalNoProvisionReceiptRepository receipts = new(
            dbContext);
        ReviewWorkspaceStaffHistoricalNoProvisionCommandHandler handler = new(
            crossGraph,
            new WorkspaceStaffOnboardingMutationCoordinator(
                operationLock,
                applications),
            receipts,
            staff,
            staff,
            new WorkspaceStaffHistoricalNoProvisionAuthorityReader(
                organizations),
            new ScopeContext(scopeId),
            new Clock(),
            new Ids());
        return new(
            dbContext,
            handler,
            crossGraph,
            staff,
            organizations,
            applicationVersion,
            applicationStatus);
    }

    private sealed record TestContext(
        WorkspacesDbContext DbContext,
        ReviewWorkspaceStaffHistoricalNoProvisionCommandHandler Handler,
        RecordingWorkspaceCrossGraphMutationLock CrossGraph,
        FakeStaffEvidence Staff,
        FakeOrganizations Organizations,
        long ApplicationVersion,
        WorkspaceStaffOnboardingState ApplicationStatus) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => this.DbContext.DisposeAsync();
    }

    private sealed class RecordingOperationLock
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
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeStaffEvidence(
        bool absent,
        StaffIdentityProvisioningAnchorCutoverDisposition disposition,
        bool duplicateOutcome)
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            IStaffIdentityProvisioningAnchorCutover
    {
        public int OutcomeCalls { get; private set; }
        public string? LastExpectedSubjectId { get; private set; }

        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
                IReadOnlyList<
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                    requests,
                CancellationToken cancellationToken = default)
        {
            this.OutcomeCalls++;
            StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest request =
                Assert.Single(requests);
            this.LastExpectedSubjectId = request.ExpectedAuthSubjectId;
            StaffWorkspaceOnboardingIdentityAnchorOutcome outcome = absent
                ? StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
                    .Absent(request)
                : new(
                    request.ApplicationId,
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                        .Unresolved,
                    Guid.NewGuid(),
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                        .Active,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                    WorkspaceApplicationVersion: null,
                    ResolutionDisposition: null,
                    ResolutionEventId: Guid.NewGuid());
            return Task.FromResult<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>(
                    duplicateOutcome ? [outcome, outcome] : [outcome]);
        }

        public Task<StaffIdentityProvisioningAnchorInspection> InspectAsync(
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            StaffIdentityProvisioningAnchorCandidate candidate = Assert.Single(
                candidates);
            return Task.FromResult(
                new StaffIdentityProvisioningAnchorInspection(
                    IsSuccess: true,
                    [new StaffIdentityProvisioningAnchorCandidateInspection(
                        candidate.SourceKind,
                        candidate.SourceId,
                        disposition)],
                    ErrorCode: null));
        }

        public Task<StaffIdentityProvisioningAnchorApplyResult> ApplyAsync(
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Apply must not be called.");
    }

    private sealed class FakeOrganizations(
        OrganizationInvitationStatus status,
        OrganizationEvidenceFailure failure,
        OrganizationEnrollmentLinkStatus enrollmentStatus)
        : IOrganizationScopeLifecycle
    {
        public int SnapshotCalls { get; private set; }
        public int ExportCalls { get; private set; }

        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            this.SnapshotCalls++;
            long revision = failure ==
                    OrganizationEvidenceFailure.FinalRevisionDrift &&
                this.SnapshotCalls > 1
                    ? 5
                    : 4;
            return Task.FromResult(new OrganizationScopeSnapshot(
                OrganizationScopeStatus.Open,
                revision));
        }

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken)
        {
            this.ExportCalls++;
            OrganizationScopeExportRecord record = request.Store ==
                OrganizationScopeExportStore.EnrollmentLinks
                    ? new OrganizationScopeEnrollmentLinkExportRecord(
                        SourceId,
                        Guid.Parse(TenantId),
                        "subject:creator",
                        TokenVersion: 1,
                        Now.AddDays(-1),
                        MaximumClaims: 10,
                        ReservedClaims: 0,
                        OrganizationEnrollmentApprovalMode.Automatic,
                        enrollmentStatus,
                        Version: 7,
                        CreatedBy: "operator",
                        CreatedAtUtc: Now.AddYears(-1),
                        LastChangedBy: "operator",
                        LastChangedAtUtc: Now.AddDays(-1))
                    : new OrganizationScopeInvitationExportRecord(
                        SourceId,
                        Guid.Parse(TenantId),
                        "subject:inviter",
                        RecipientEmail: null,
                        TokenVersion: 1,
                        Now.AddDays(-1),
                        status,
                        AcceptedSubjectId: null,
                        AcceptedMembershipId: null,
                        AcceptedAtUtc: null,
                        Version: 7,
                        CreatedBy: "operator",
                        CreatedAtUtc: Now.AddYears(-1),
                        LastChangedBy: "operator",
                        LastChangedAtUtc: Now.AddDays(-1));
            OrganizationScopeExportRecord[] records = failure switch
            {
                OrganizationEvidenceFailure.Missing => [],
                OrganizationEvidenceFailure.Duplicate => [record, record],
                _ => [record]
            };
            Guid? finalId = records.Select(item => item switch
                {
                    OrganizationScopeInvitationExportRecord invitation =>
                        (Guid?)invitation.InvitationId,
                    OrganizationScopeEnrollmentLinkExportRecord link =>
                        link.EnrollmentLinkId,
                    _ => null
                })
                .LastOrDefault();
            return Task.FromResult(new OrganizationScopeExportPage(
                failure == OrganizationEvidenceFailure.Stale
                    ? OrganizationScopeExportStatus.Stale
                    : OrganizationScopeExportStatus.Completed,
                ScopeRevision: 4,
                request.Store,
                records,
                finalId.HasValue ? "id:" + finalId.Value.ToString("D") : null,
                HasMore: false));
        }

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Destroy must not be called.");
    }

    private sealed class ScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }

    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Ids : IIdGenerator
    {
        public Guid NewId() => ReceiptId;
    }

    public enum OrganizationEvidenceFailure
    {
        None = 0,
        Missing = 1,
        Duplicate = 2,
        Stale = 3,
        FinalRevisionDrift = 4
    }
}

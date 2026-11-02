namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Authorization;
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
using Gma.Modules.Organizations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandlerTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approved_correction_mutates_only_applicant_profile_and_records_proof()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        RecordingExecutionGate gate = new();
        InMemoryReceiptRepository receipts = new();
        RecordingCorrectionLock correctionLock = new();
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand command =
            Command(application) with
            {
                DisplayName = "Ada Corrected",
                WorkEmail = "ada.corrected@example.test"
            };
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                receipts,
                correctionLock,
                gate);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(2, application.Version);
        Assert.Equal("Ada Corrected", application.DisplayName);
        Assert.Equal(
            "ada.corrected@example.test",
            application.WorkEmail);
        Assert.Equal(
            "verified@example.test",
            application.VerifiedAccountEmail);
        Assert.Equal(
            [
                WorkspaceStaffOnboardingDataRightsFieldKeys.DisplayName,
                WorkspaceStaffOnboardingDataRightsFieldKeys.WorkEmail
            ],
            result.Value.ChangedFieldKeys);
        Assert.Equal(1, correctionLock.AcquisitionCount);
        Assert.NotNull(receipts.Receipt);
        Assert.Equal(command.ExecutionId, receipts.Receipt.ExecutionId);
        Assert.Equal(DataRightsCaseType.StaffRights, gate.Request!.CaseType);
        Assert.Null(gate.Request.PropertyId);
        Assert.Equal(TenantId, gate.Request.TenantId);
        Assert.Equal(command.ActorId, gate.Request.ExecutingActorId);
        Assert.Equal(
            WorkspacesDataRightsCoordinates.Owner,
            gate.Request.Coordinate.OwnerKey);
        Assert.Equal(
            WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
            gate.Request.Coordinate.RecordType);
        Assert.Equal(application.Id, gate.Request.Coordinate.RecordId);
        Assert.Equal(1, gate.Request.Coordinate.RecordVersion);
    }

    [Fact]
    public async Task Exact_replay_returns_original_receipt_without_reauthorization()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        RecordingExecutionGate gate = new();
        InMemoryReceiptRepository receipts = new();
        RecordingCorrectionLock correctionLock = new();
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                receipts,
                correctionLock,
                gate);
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand command =
            Command(application) with { DisplayName = "Ada Corrected" };
        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> first =
            await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(application.UpdateSubmission(
            application.VerifiedAccountEmail!,
            "Ada Later",
            application.LegalName,
            application.WorkEmail,
            application.WorkPhone,
            application.EmployeeNumber,
            application.JobTitle,
            application.Department,
            Now.AddMinutes(1)).IsSuccess);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> replay =
            await handler.HandleAsync(command, CancellationToken.None);
        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> conflict =
            await handler.HandleAsync(
                command with { DisplayName = "Different request" },
                CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value.ReceiptId, replay.Value.ReceiptId);
        Assert.Equal(first.Value.ExecutionId, replay.Value.ExecutionId);
        Assert.Equal(first.Value.CaseId, replay.Value.CaseId);
        Assert.Equal(
            first.Value.ApprovalRevision,
            replay.Value.ApprovalRevision);
        Assert.Equal(first.Value.ApplicationId, replay.Value.ApplicationId);
        Assert.Equal(
            first.Value.SelectedRecordVersion,
            replay.Value.SelectedRecordVersion);
        Assert.Equal(
            first.Value.CurrentRecordVersion,
            replay.Value.CurrentRecordVersion);
        Assert.Equal(
            first.Value.ChangedFieldKeys,
            replay.Value.ChangedFieldKeys);
        Assert.Equal(
            first.Value.CompletedAtUtc,
            replay.Value.CompletedAtUtc);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .CorrectionIdempotencyConflict,
            conflict.Error);
        Assert.Equal("Ada Later", application.DisplayName);
        Assert.Equal(1, gate.EvaluationCount);
        Assert.Equal(1, correctionLock.AcquisitionCount);
    }

    [Theory]
    [InlineData(OrganizationEnrollmentClaimStatus.Unknown)]
    [InlineData(OrganizationEnrollmentClaimStatus.Accepted)]
    [InlineData(OrganizationEnrollmentClaimStatus.Rejected)]
    [InlineData(OrganizationEnrollmentClaimStatus.Expired)]
    [InlineData(OrganizationEnrollmentClaimStatus.Withdrawn)]
    public async Task Terminal_or_unknown_enrollment_claim_rejects_profile_correction(
        OrganizationEnrollmentClaimStatus status)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        FakeOrganizationEnrollmentClaimInspector claims = new(
            Claim(application, status));
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                new InMemoryReceiptRepository(),
                new RecordingCorrectionLock(),
                new RecordingExecutionGate(),
                claims);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(
                Command(application) with { DisplayName = "Changed" },
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .CorrectionTargetUnavailable,
            result.Error);
        Assert.Equal("Ada Operator", application.DisplayName);
        Assert.Single(claims.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_exact_pending_enrollment_claim_allows_profile_correction(
        bool hasPendingClaim)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        FakeOrganizationEnrollmentClaimInspector claims = new(
            hasPendingClaim
                ? Claim(
                    application,
                    OrganizationEnrollmentClaimStatus.Pending)
                : null);
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                new InMemoryReceiptRepository(),
                new RecordingCorrectionLock(),
                new RecordingExecutionGate(),
                claims);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(
                Command(application) with { DisplayName = "Changed" },
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("Changed", application.DisplayName);
        Assert.Single(claims.Requests);
    }

    [Theory]
    [InlineData(-10, false)]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public async Task Pending_claim_deadline_uses_strict_persistence_precision_boundary(
        long deadlineTicksFromNow,
        bool expectedAllowed)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        OrganizationEnrollmentClaimDto claim = Claim(
            application,
            OrganizationEnrollmentClaimStatus.Pending) with
        {
            DecisionExpiresAtUtc = Now.AddTicks(deadlineTicksFromNow)
        };
        FakeOrganizationEnrollmentClaimInspector claims = new(claim);
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                new InMemoryReceiptRepository(),
                new RecordingCorrectionLock(),
                new RecordingExecutionGate(),
                claims);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(
                Command(application) with { DisplayName = "Changed" },
                CancellationToken.None);

        Assert.Equal(expectedAllowed, result.IsSuccess);
        Assert.Equal(
            expectedAllowed ? "Changed" : "Ada Operator",
            application.DisplayName);
        if (!expectedAllowed)
        {
            Assert.Equal(
                WorkspaceStaffOnboardingApplicationErrors
                    .CorrectionTargetUnavailable,
                result.Error);
        }
    }

    [Fact]
    public async Task Pending_claim_without_decision_deadline_rejects_profile_correction()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        OrganizationEnrollmentClaimDto claim = Claim(
            application,
            OrganizationEnrollmentClaimStatus.Pending) with
        {
            DecisionExpiresAtUtc = null
        };
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                new InMemoryReceiptRepository(),
                new RecordingCorrectionLock(),
                new RecordingExecutionGate(),
                new FakeOrganizationEnrollmentClaimInspector(claim));

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(
                Command(application) with { DisplayName = "Changed" },
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .CorrectionTargetUnavailable,
            result.Error);
        Assert.Equal("Ada Operator", application.DisplayName);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("source")]
    [InlineData("subject")]
    public async Task Mismatched_pending_enrollment_claim_rejects_profile_correction(
        string coordinate)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        OrganizationEnrollmentClaimDto claim = Claim(
            application,
            OrganizationEnrollmentClaimStatus.Pending);
        claim = coordinate switch
        {
            "organization" => claim with { OrganizationId = Guid.NewGuid() },
            "source" => claim with { EnrollmentLinkId = Guid.NewGuid() },
            _ => claim with { SubjectId = "subject:other" }
        };
        FakeOrganizationEnrollmentClaimInspector claims = new(claim);
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                new InMemoryReceiptRepository(),
                new RecordingCorrectionLock(),
                new RecordingExecutionGate(),
                claims);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(
                Command(application) with { DisplayName = "Changed" },
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .CorrectionTargetUnavailable,
            result.Error);
        Assert.Equal("Ada Operator", application.DisplayName);
        Assert.Single(claims.Requests);
    }

    [Fact]
    public async Task Invitation_profile_correction_does_not_query_enrollment_claims()
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            WorkspaceStaffOnboardingSource.Invitation);
        FakeOrganizationEnrollmentClaimInspector claims = new(
            Claim(application, OrganizationEnrollmentClaimStatus.Accepted));
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                new InMemoryReceiptRepository(),
                new RecordingCorrectionLock(),
                new RecordingExecutionGate(),
                claims);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(
                Command(application) with { DisplayName = "Changed" },
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("Changed", application.DisplayName);
        Assert.Empty(claims.Requests);
    }

    [Fact]
    public async Task Version_and_local_status_errors_precede_external_claim_fence()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        FakeOrganizationEnrollmentClaimInspector claims = new(
            Claim(application, OrganizationEnrollmentClaimStatus.Accepted));
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                new InMemoryReceiptRepository(),
                new RecordingCorrectionLock(),
                new RecordingExecutionGate(),
                claims);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> stale =
            await handler.HandleAsync(
                Command(application) with
                {
                    ExpectedVersion = application.Version + 1,
                    DisplayName = "Changed"
                },
                CancellationToken.None);
        Assert.True(application.ObserveClaimRequested(
            Guid.NewGuid(),
            claimVersion: 1,
            Now.AddMinutes(1)).IsSuccess);
        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> reviewed =
            await handler.HandleAsync(
                Command(application) with { DisplayName = "Changed" },
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingErrors.CorrectionVersionConflict,
            stale.Error);
        Assert.Equal(
            WorkspaceStaffOnboardingErrors.CorrectionUnavailable,
            reviewed.Error);
        Assert.Empty(claims.Requests);
    }

    [Fact]
    public async Task Receipt_committed_while_waiting_for_lock_is_replayed()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand command =
            Command(application) with { DisplayName = "Ada Corrected" };
        WorkspaceStaffApplicantProfile requested =
            WorkspaceStaffApplicantProfile.Create(
                command.DisplayName,
                command.LegalName,
                command.WorkEmail,
                command.WorkPhone,
                command.EmployeeNumber,
                command.JobTitle,
                command.Department).Value;
        WorkspaceStaffOnboardingCorrectionReceipt committed =
            WorkspaceStaffOnboardingCorrectionReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                command.ExecutionId,
                command.CaseId,
                command.ApprovalRevision,
                command.ApplicationId,
                command.ExpectedVersion,
                command.ExpectedVersion + 1,
                [WorkspaceStaffOnboardingApplicantField.DisplayName],
                WorkspaceStaffOnboardingDataRightsCorrectionFingerprint
                    .Compute(requested),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now).Value;
        InMemoryReceiptRepository receipts = new(committed);
        RecordingCorrectionLock correctionLock = new();
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                receipts,
                correctionLock,
                new RecordingExecutionGate());

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(committed.Id, result.Value.ReceiptId);
        Assert.Equal(2, receipts.LookupCount);
        Assert.Equal(1, correctionLock.AcquisitionCount);
        Assert.Equal(1, application.Version);
        Assert.Equal("Ada Operator", application.DisplayName);
    }

    [Fact]
    public async Task Denied_or_noop_correction_does_not_create_receipt()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        InMemoryReceiptRepository receipts = new();
        RecordingCorrectionLock correctionLock = new();
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand changed =
            Command(application) with { DisplayName = "Ada Corrected" };
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler denied =
            CreateHandler(
                application,
                receipts,
                correctionLock,
                new RecordingExecutionGate(allowed: false));

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto>
            deniedResult =
            await denied.HandleAsync(changed, CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .DataRightsApprovalRequired,
            deniedResult.Error);
        Assert.Equal(0, correctionLock.AcquisitionCount);
        Assert.Null(receipts.Receipt);

        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler allowed =
            CreateHandler(
                application,
                receipts,
                correctionLock,
                new RecordingExecutionGate());
        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> noOp =
            await allowed.HandleAsync(
                Command(application),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingErrors.CorrectionNoChanges,
            noOp.Error);
        Assert.Equal(1, correctionLock.AcquisitionCount);
        Assert.Null(receipts.Receipt);
    }

    [Fact]
    public async Task Invalid_coordinates_fail_before_receipt_lookup()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        InMemoryReceiptRepository receipts = new();
        RecordingCorrectionLock correctionLock = new();
        RecordingExecutionGate gate = new();
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler handler =
            CreateHandler(
                application,
                receipts,
                correctionLock,
                gate);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(
                Command(application) with { ExecutionId = Guid.Empty },
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .CorrectionRequestInvalid,
            result.Error);
        Assert.Equal(0, receipts.LookupCount);
        Assert.Equal(0, correctionLock.AcquisitionCount);
        Assert.Equal(0, gate.EvaluationCount);
    }

    private static
        ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommandHandler
        CreateHandler(
            WorkspaceStaffOnboarding application,
            InMemoryReceiptRepository receipts,
            RecordingCorrectionLock correctionLock,
            RecordingExecutionGate gate,
            FakeOrganizationEnrollmentClaimInspector? claims = null)
    {
        InMemoryApplicationRepository applications = new(application);
        return new(
            receipts,
            WorkspaceStaffOnboardingMutationTestSupport.Create(
                applications,
                correctionLock),
            new WorkspaceStaffOnboardingDataRightsCorrectionAuthorizer(
                gate,
                new TestScopeContext()),
            claims ?? new FakeOrganizationEnrollmentClaimInspector(),
            new TestScopeContext(),
            new TestClock(),
            new SequenceIdGenerator());
    }

    private static ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand
        Command(WorkspaceStaffOnboarding application) =>
        new(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            ApprovalRevision: 4,
            application.Id,
            application.Version,
            application.DisplayName!,
            application.LegalName,
            application.WorkEmail,
            application.WorkPhone,
            application.EmployeeNumber,
            application.JobTitle,
            application.Department,
            "user:privacy-owner");

    private static WorkspaceStaffOnboarding CreateApplication(
        WorkspaceStaffOnboardingSource sourceKind =
            WorkspaceStaffOnboardingSource.EnrollmentLink) =>
        WorkspaceStaffOnboarding.Create(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            TenantId,
            sourceKind,
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            "subject:applicant",
            "verified@example.test",
            "Ada Operator",
            "Ada Lovelace",
            "ada@workspace.test",
            "+1 555 0100",
            "EMP-100",
            "Manager",
            "Operations",
            Now.AddHours(-1)).Value;

    private static OrganizationEnrollmentClaimDto Claim(
        WorkspaceStaffOnboarding application,
        OrganizationEnrollmentClaimStatus status) => new(
        Guid.Parse("60000000-0000-0000-0000-000000000001"),
        application.SourceId,
        Guid.Parse(TenantId),
        application.SubjectId,
        status,
        status == OrganizationEnrollmentClaimStatus.Accepted
            ? Guid.Parse("70000000-0000-0000-0000-000000000001")
            : null,
        Version: 2,
        Now,
        Now.AddMinutes(1))
        {
            DecisionExpiresAtUtc =
            status == OrganizationEnrollmentClaimStatus.Pending
                ? Now.AddMinutes(5)
                : null
        };

    private sealed class RecordingExecutionGate(bool allowed = true)
        : IDataRightsCorrectionExecutionGate
    {
        public DataRightsCorrectionExecutionGateRequest? Request
        {
            get;
            private set;
        }

        public int EvaluationCount { get; private set; }

        public Task<DataRightsCorrectionExecutionGateResult> EvaluateAsync(
            DataRightsCorrectionExecutionGateRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            this.EvaluationCount++;
            return Task.FromResult(
                allowed
                    ? DataRightsCorrectionExecutionGateResult.Allowed(
                        Now.AddMinutes(5))
                    : DataRightsCorrectionExecutionGateResult.Denied(
                        DataRightsCorrectionExecutionDenial
                            .ExecutionNotFound));
        }
    }

    private sealed class InMemoryReceiptRepository(
        WorkspaceStaffOnboardingCorrectionReceipt? delayedReceipt = null)
        : IWorkspaceStaffOnboardingCorrectionReceiptRepository
    {
        public WorkspaceStaffOnboardingCorrectionReceipt? Receipt
        {
            get;
            private set;
        }

        public int LookupCount { get; private set; }

        public Task<WorkspaceStaffOnboardingCorrectionReceipt?>
            FindByExecutionIdAsync(
                Guid executionId,
                CancellationToken cancellationToken)
        {
            this.LookupCount++;
            WorkspaceStaffOnboardingCorrectionReceipt? visible =
                this.Receipt ??
                (this.LookupCount >= 2 ? delayedReceipt : null);
            return Task.FromResult(
                visible?.ExecutionId == executionId
                    ? visible
                    : null);
        }

        public Task AddAsync(
            WorkspaceStaffOnboardingCorrectionReceipt receipt,
            CancellationToken cancellationToken)
        {
            this.Receipt = receipt;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCorrectionLock(bool acquired = true)
        : IWorkspaceStaffOnboardingOperationLock
    {
        public int AcquisitionCount { get; private set; }

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
            CancellationToken cancellationToken)
        {
            this.AcquisitionCount++;
            return Task.FromResult(acquired);
        }
    }

    private sealed class InMemoryApplicationRepository(
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
                application.Id == applicationId
                    ? application
                    : null);

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

        public Task<WorkspaceStaffOnboardingListResponse> ListActionableAsync(
            PageRequest page,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReloadAsync(
            WorkspaceStaffOnboarding ignored,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AddAsync(
            WorkspaceStaffOnboarding ignored,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string? ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now.AddTicks(7);
    }

    private sealed class SequenceIdGenerator : IIdGenerator
    {
        private int sequence;

        public Guid NewId()
        {
            this.sequence++;
            return Guid.Parse(
                $"80000000-0000-0000-0000-{this.sequence:000000000000}");
        }
    }
}

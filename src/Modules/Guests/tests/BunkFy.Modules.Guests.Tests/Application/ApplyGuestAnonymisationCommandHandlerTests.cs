namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Handlers;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ApplyGuestAnonymisationCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly string PolicySetSha256 = new('b', 64);

    [Fact]
    public async Task Exact_approval_commits_terminal_profile_receipt_and_tombstone()
    {
        GuestProfile profile = CreateProfile();
        profile.ClearDomainEvents();
        RecordingAnonymisationRepository repository = new();
        RecordingBoundary boundary = new();
        RecordingEligibility eligibility = new();
        RecordingApprovalGate approval = new(CreateApprovalEvidence());
        RecordingManagementOperationRepository managementOperations = new();
        Guid eventId = Guid.NewGuid();
        Guid receiptId = Guid.NewGuid();
        ApplyGuestAnonymisationCommand command = CreateCommand(profile);
        ApplyGuestAnonymisationCommandHandler handler = CreateHandler(
            profile,
            repository,
            boundary,
            eligibility,
            approval,
            new QueueIdGenerator(eventId, receiptId),
            managementOperations);

        Result<GuestAnonymisationReceiptDto> result =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(GuestProfileState.Anonymised, profile.Status);
        Assert.True(profile.MatchesAnonymisedState(2, Now));
        Assert.Empty(profile.LanguageTags);
        Assert.Equal(receiptId, result.Value.ReceiptId);
        Assert.Equal(eventId, result.Value.EventId);
        Assert.Equal(2, result.Value.AffectedPropertyCount);
        Assert.Equal(PolicySetSha256, result.Value.PolicySetSha256);
        Assert.Equal(64, result.Value.ApprovalEvidenceSha256.Length);
        Assert.Equal(64, result.Value.CanonicalSha256.Length);
        Assert.NotNull(repository.Receipt);
        Assert.NotNull(repository.Tombstone);
        Assert.True(repository.Tombstone.Matches(repository.Receipt));
        Assert.Equal(1, boundary.CallCount);
        Assert.Equal(1, eligibility.CallCount);
        Assert.Equal(1, approval.CallCount);
        Assert.Equal([profile.Id], managementOperations.DeletedGuestIds);
        Assert.DoesNotContain(
            typeof(GuestAnonymisationReceipt).GetProperties(),
            property => property.Name is
                nameof(GuestProfile.DisplayName) or
                nameof(GuestProfile.LegalName) or
                nameof(GuestProfile.Email) or
                nameof(GuestProfile.Phone) or
                nameof(GuestProfile.DateOfBirth) or
                nameof(GuestProfile.NationalityCountryCode) or
                nameof(GuestProfile.PreferredLanguageTag) or
                nameof(GuestProfile.LanguageTags) or
                nameof(GuestProfile.Notes));
    }

    [Fact]
    public async Task Equivalent_retry_returns_identical_proof_without_revalidating_or_mutating()
    {
        GuestProfile profile = CreateProfile();
        RecordingAnonymisationRepository repository = new();
        RecordingBoundary boundary = new();
        RecordingEligibility eligibility = new();
        RecordingApprovalGate approval = new(CreateApprovalEvidence());
        ApplyGuestAnonymisationCommand command = CreateCommand(profile);
        ApplyGuestAnonymisationCommandHandler handler = CreateHandler(
            profile,
            repository,
            boundary,
            eligibility,
            approval,
            new QueueIdGenerator(Guid.NewGuid(), Guid.NewGuid()));

        Result<GuestAnonymisationReceiptDto> first =
            await handler.HandleAsync(command, CancellationToken.None);
        Result<GuestAnonymisationReceiptDto> replay =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(2, profile.Version);
        Assert.Equal(1, repository.AddCount);
        Assert.Equal(1, boundary.CallCount);
        Assert.Equal(1, eligibility.CallCount);
        Assert.Equal(1, approval.CallCount);
    }

    [Fact]
    public async Task Reused_key_with_changed_operation_or_policy_fails_closed()
    {
        GuestProfile profile = CreateProfile();
        RecordingAnonymisationRepository repository = new();
        ApplyGuestAnonymisationCommand command = CreateCommand(profile);
        ApplyGuestAnonymisationCommandHandler handler = CreateHandler(
            profile,
            repository,
            new RecordingBoundary(),
            new RecordingEligibility(),
            new RecordingApprovalGate(CreateApprovalEvidence()),
            new QueueIdGenerator(Guid.NewGuid(), Guid.NewGuid()));
        Assert.True((await handler.HandleAsync(command, CancellationToken.None)).IsSuccess);

        Result<GuestAnonymisationReceiptDto> changedRevision = await handler.HandleAsync(
            command with { OperationRevision = command.OperationRevision + 1 },
            CancellationToken.None);
        Result<GuestAnonymisationReceiptDto> changedPolicy = await handler.HandleAsync(
            command with
            {
                RoutingPolicy = command.RoutingPolicy with
                {
                    ContentSha256 = new string('c', 64)
                }
            },
            CancellationToken.None);

        Assert.Equal(
            GuestsApplicationErrors.AnonymisationIdempotencyConflict,
            changedRevision.Error);
        Assert.Equal(
            GuestsApplicationErrors.AnonymisationIdempotencyConflict,
            changedPolicy.Error);
        Assert.Equal(2, profile.Version);
        Assert.Equal(1, repository.AddCount);
    }

    [Fact]
    public async Task Approval_or_eligibility_denial_leaves_owner_state_unchanged()
    {
        GuestProfile approvalProfile = CreateProfile();
        RecordingAnonymisationRepository approvalRepository = new();
        ApplyGuestAnonymisationCommand approvalCommand = CreateCommand(approvalProfile);
        ApplyGuestAnonymisationCommandHandler approvalHandler = CreateHandler(
            approvalProfile,
            approvalRepository,
            new RecordingBoundary(),
            new RecordingEligibility(),
            new RecordingApprovalGate(null),
            new QueueIdGenerator());

        Result<GuestAnonymisationReceiptDto> denied =
            await approvalHandler.HandleAsync(approvalCommand, CancellationToken.None);

        Assert.Equal(GuestsApplicationErrors.DataRightsApprovalRequired, denied.Error);
        Assert.Equal(GuestProfileState.Active, approvalProfile.Status);
        Assert.Null(approvalRepository.Receipt);

        GuestProfile blockedProfile = CreateProfile();
        RecordingAnonymisationRepository blockedRepository = new();
        RecordingEligibility blockedEligibility = new(
            GuestAnonymisationBlockerCode.ActiveDataHold);
        ApplyGuestAnonymisationCommandHandler blockedHandler = CreateHandler(
            blockedProfile,
            blockedRepository,
            new RecordingBoundary(),
            blockedEligibility,
            new RecordingApprovalGate(CreateApprovalEvidence()),
            new QueueIdGenerator());
        Result<GuestAnonymisationReceiptDto> blocked = await blockedHandler.HandleAsync(
            CreateCommand(blockedProfile),
            CancellationToken.None);

        Assert.Equal(
            "Guests.AnonymisationBlocked.ActiveDataHold",
            blocked.Error.Code);
        Assert.Equal(GuestProfileState.Active, blockedProfile.Status);
        Assert.Equal(["en-gb", "fr", "unknown-١"], blockedProfile.LanguageTags);
        Assert.Null(blockedRepository.Receipt);
    }

    private static ApplyGuestAnonymisationCommandHandler CreateHandler(
        GuestProfile profile,
        RecordingAnonymisationRepository repository,
        RecordingBoundary boundary,
        RecordingEligibility eligibility,
        RecordingApprovalGate approval,
        IIdGenerator ids,
        IGuestManagementOperationRepository? managementOperations = null) => new(
        new StubGuestRepository(profile),
        repository,
        managementOperations ?? new RecordingManagementOperationRepository(),
        boundary,
        eligibility,
        approval,
        new TestScopeContext(),
        new TestClock(),
        ids);

    private sealed class RecordingManagementOperationRepository
        : IGuestManagementOperationRepository
    {
        public List<Guid> DeletedGuestIds { get; } = [];

        public Task<GuestManagementOperationRecord?> GetAsync(
            Guid guestId,
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddAsync(
            GuestManagementOperationRecord operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteForGuestAsync(
            Guid guestId,
            CancellationToken cancellationToken)
        {
            this.DeletedGuestIds.Add(guestId);
            return Task.CompletedTask;
        }
    }

    private static ApplyGuestAnonymisationCommand CreateCommand(GuestProfile profile) => new(
        Guid.NewGuid(),
        profile.OriginPropertyId,
        Guid.NewGuid(),
        ApprovalRevision: 4,
        OperationRevision: 5,
        profile.Id,
        profile.Version,
        CreateRoutingEvidence(),
        "user:privacy-executor");

    private static GuestAnonymisationRoutingPolicyEvidence CreateRoutingEvidence() => new(
        PropertyPolicySourceVersion: 7,
        OperatingCountryCode: "GB",
        PolicyId: "gb-hostel",
        PolicyVersion: 3,
        RetentionPolicyId: "guest-operational",
        RetentionPolicyVersion: 2,
        ContentSha256: new string('a', 64),
        PurposeCode: "data-rights-anonymisation",
        Surface: "erasure",
        SourceProvenance: "authorized-workspace-operator",
        EvaluatedAtUtc: Now.AddMinutes(-5));

    private static DataRightsApprovalEvidence CreateApprovalEvidence()
    {
        GuestAnonymisationRoutingPolicyEvidence routing = CreateRoutingEvidence();
        return new(
            SchemaVersion: 1,
            PropertyId: Guid.Empty,
            PropertyVersion: routing.PropertyPolicySourceVersion,
            routing.OperatingCountryCode,
            routing.PolicyId,
            routing.PolicyVersion,
            routing.RetentionPolicyId,
            routing.RetentionPolicyVersion,
            routing.ContentSha256,
            routing.PurposeCode,
            routing.Surface,
            routing.SourceProvenance,
            routing.EvaluatedAtUtc,
            RequiresDistinctExecutor: true);
    }

    private static GuestProfile CreateProfile() => GuestProfile.Create(
        Guid.NewGuid(),
        "tenant-a",
        Guid.NewGuid(),
        "Maya Chen",
        "Maya Q. Chen",
        "maya@example.test",
        "+44 20 1234 5678",
        new DateOnly(1990, 2, 3),
        "GB",
        "en-GB",
        "Prefers a lower bunk.",
        "user:creator",
        Guid.NewGuid(),
        Now.AddDays(-1),
        languageTags: ["en-GB", "fr", "unknown-١"]).Value;

    private sealed class StubGuestRepository(GuestProfile profile)
        : IGuestProfileRepository
    {
        public Task AddUnderAcquiredOperationLockAsync(
            GuestProfile added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestProfile?> GetByIdAsync(
            Guid guestId,
            CancellationToken cancellationToken) => Task.FromResult(
            profile.Id == guestId ? profile : null);

        public Task<GuestProfile?> GetVisibleAsync(
            Guid propertyId,
            Guid guestId,
            CancellationToken cancellationToken) =>
            this.GetForDataRightsAsync(propertyId, guestId, cancellationToken);

        public Task<GuestProfile?> GetForDataRightsAsync(
            Guid propertyId,
            Guid guestId,
            CancellationToken cancellationToken) => Task.FromResult(
            profile.OriginPropertyId == propertyId && profile.Id == guestId
                ? profile
                : null);

        public Task<GuestListResponse> ListVisibleAsync(
            Guid propertyId,
            string? search,
            GuestStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingAnonymisationRepository
        : IGuestAnonymisationRepository
    {
        public GuestAnonymisationReceipt? Receipt { get; private set; }
        public GuestAnonymisationTombstone? Tombstone { get; private set; }
        public int AddCount { get; private set; }

        public Task<GuestAnonymisationReceipt?> FindReceiptByIdempotencyKeyAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken) => Task.FromResult(
            this.Receipt?.IdempotencyKey == idempotencyKey ? this.Receipt : null);

        public Task<GuestAnonymisationTombstone?> GetTombstoneAsync(
            Guid guestId,
            CancellationToken cancellationToken) => Task.FromResult(
            this.Tombstone?.Id == guestId ? this.Tombstone : null);

        public Task AddAsync(
            GuestAnonymisationReceipt receipt,
            GuestAnonymisationTombstone tombstone,
            CancellationToken cancellationToken)
        {
            this.Receipt = receipt;
            this.Tombstone = tombstone;
            this.AddCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBoundary : IGuestAnonymisationExecutionBoundary
    {
        public int CallCount { get; private set; }

        public Task AcquireAsync(
            string tenantId,
            Guid guestId,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEligibility(
        GuestAnonymisationBlockerCode blocker = GuestAnonymisationBlockerCode.None)
        : IGuestAnonymisationEligibilityEvaluator
    {
        public int CallCount { get; private set; }

        public Task<GuestAnonymisationEligibilityResult> EvaluateAsync(
            GuestAnonymisationEligibilityRequest request,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            bool eligible = blocker == GuestAnonymisationBlockerCode.None;
            return Task.FromResult(new GuestAnonymisationEligibilityResult(
                GuestAnonymisationEligibilityContract.CurrentVersion,
                eligible
                    ? GuestAnonymisationEligibilityStatus.Eligible
                    : GuestAnonymisationEligibilityStatus.Blocked,
                blocker,
                request.SelectedGuestVersion,
                AffectedPropertyCount: 2,
                eligible ? new string('d', 64) : null,
                eligible ? PolicySetSha256 : null,
                Now));
        }
    }

    private sealed class RecordingApprovalGate(DataRightsApprovalEvidence? evidence)
        : IDataRightsOperationApprovalGate
    {
        public int CallCount { get; private set; }

        public Task<DataRightsOperationApprovalResult> EvaluateAsync(
            DataRightsOperationApprovalRequest request,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            DataRightsApprovalEvidence? approvedEvidence = evidence is null
                ? null
                : evidence with { PropertyId = request.PropertyId!.Value };
            return Task.FromResult(approvedEvidence is null
                ? DataRightsOperationApprovalResult.Denied(
                    DataRightsOperationApprovalDenial.CaseNotApproved)
                : DataRightsOperationApprovalResult.ApprovedWithEvidence(approvedEvidence));
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class QueueIdGenerator(params Guid[] values) : IIdGenerator
    {
        private readonly Queue<Guid> ids = new(values);

        public Guid NewId() => this.ids.Dequeue();
    }
}

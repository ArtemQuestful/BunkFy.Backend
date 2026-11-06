namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Handlers;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ApplyGuestDataRightsCorrectionCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_approval_updates_profile_and_prepares_pii_free_receipt()
    {
        GuestProfile profile = CreateProfile();
        RecordingReceiptRepository receipts = new();
        RecordingApprovalGate approval = new();
        Guid eventId = Guid.NewGuid();
        Guid receiptId = Guid.NewGuid();
        Guid completionEventId = Guid.NewGuid();
        RecordingGuestOperationLock operationLock = new();
        ApplyGuestDataRightsCorrectionCommand command = CreateCommand(profile, "Corrected Guest");
        ApplyGuestDataRightsCorrectionCommandHandler handler = CreateHandler(
            profile,
            receipts,
            approval,
            new QueueIdGenerator(eventId, receiptId, completionEventId),
            operationLock);

        Result<GuestDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Corrected Guest", profile.DisplayName);
        Assert.Equal(2, profile.Version);
        Assert.Equal(receiptId, result.Value.ReceiptId);
        Assert.Equal(eventId, result.Value.EventId);
        Assert.Equal(completionEventId, receipts.Added!.CompletionEventId);
        Assert.Equal(["guest.profile.display-name"], result.Value.ChangedFields);
        Assert.Equal(command.IdempotencyKey, receipts.Added.IdempotencyKey);
        Assert.Equal(1, receipts.Added.SelectedRecordVersion);
        Assert.Equal(2, receipts.Added.CurrentRecordVersion);
        Assert.Equal(command.CaseId, approval.Request!.CaseId);
        Assert.Equal(command.ApprovalRevision, approval.Request.ApprovalRevision);
        Assert.Equal(command.IdempotencyKey, approval.Request.ExecutionId);
        Assert.Equal(command.GuestId, approval.Request.Coordinate.RecordId);
        Assert.Equal(command.ExpectedVersion, approval.Request.Coordinate.RecordVersion);
        Assert.Equal(
            GuestsDataRightsCoordinates.CorrectionFieldPolicyKey,
            approval.Request.FieldPolicyKey);
        Assert.Equal(command.ActorId, approval.Request.ExecutingActorId);
        Assert.Equal(
            (profile.ScopeId, profile.Id),
            Assert.Single(operationLock.GuestAcquisitions));
        Assert.DoesNotContain(
            typeof(GuestDataRightsCorrectionReceipt).GetProperties(),
            property => property.Name is
                nameof(GuestProfile.DisplayName) or
                nameof(GuestProfile.LegalName) or
                nameof(GuestProfile.Email) or
                nameof(GuestProfile.Phone) or
                nameof(GuestProfile.Notes));
    }

    [Fact]
    public async Task Approval_denial_leaves_profile_and_receipts_unchanged()
    {
        GuestProfile profile = CreateProfile();
        RecordingReceiptRepository receipts = new();
        RecordingApprovalGate approval = new(isApproved: false);
        ApplyGuestDataRightsCorrectionCommandHandler handler = CreateHandler(
            profile,
            receipts,
            approval,
            new QueueIdGenerator(Guid.NewGuid(), Guid.NewGuid()));

        Result<GuestDataRightsCorrectionReceiptDto> result = await handler.HandleAsync(
            CreateCommand(profile, "Corrected Guest"),
            CancellationToken.None);

        Assert.Equal(GuestsApplicationErrors.DataRightsApprovalRequired, result.Error);
        Assert.Equal("Original Guest", profile.DisplayName);
        Assert.Equal(1, profile.Version);
        Assert.Null(receipts.Added);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Equivalent_retry_returns_committed_receipt_without_rechecking_approval(int receiptVersion)
    {
        GuestProfile profile = CreateProfile();
        ApplyGuestDataRightsCorrectionCommand command = CreateCommand(profile, "Corrected Guest");
        Guid eventId = Guid.NewGuid();
        Result<Domain.ValueObjects.GuestProfileUpdateOutcome> updated =
            profile.UpdateWithOutcome(
                command.DisplayName,
                command.LegalName,
                command.Email,
                command.Phone,
                command.DateOfBirth,
                command.NationalityCountryCode,
                command.PreferredLanguageTag,
                command.Notes,
                command.ExpectedVersion,
                command.ActorId,
                eventId,
                Now);
        GuestDataRightsCorrectionReceipt receipt = GuestDataRightsCorrectionReceipt.Create(
            Guid.NewGuid(),
            "tenant-a",
            command.IdempotencyKey,
            command.PropertyId,
            command.CaseId,
            command.ApprovalRevision,
            command.GuestId,
            updated.Value.PreviousVersion,
            updated.Value.CurrentVersion,
            updated.Value.ChangedFields,
            updated.Value.EventId,
            Guid.NewGuid(),
            updated.Value.OccurredAtUtc).Value;
        // Model a historical persisted receipt without changing the production creation contract.
        typeof(GuestDataRightsCorrectionReceipt).GetProperty(nameof(receipt.ContractVersion))!
            .SetValue(receipt, receiptVersion);
        receipt.ClearDomainEvents();
        RecordingReceiptRepository receipts = new(receipt);
        RecordingApprovalGate approval = new();
        ApplyGuestDataRightsCorrectionCommandHandler handler = CreateHandler(
            profile,
            receipts,
            approval,
            new QueueIdGenerator());

        Result<GuestDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(receipt.Id, result.Value.ReceiptId);
        Assert.Null(approval.Request);
        Assert.Null(receipts.Added);
        Assert.Equal(2, profile.Version);
        Assert.Equal(receiptVersion, receipt.ContractVersion);
        Assert.Empty(receipt.DomainEvents);
    }

    [Fact]
    public async Task Reused_key_with_different_final_values_fails_closed()
    {
        GuestProfile profile = CreateProfile();
        ApplyGuestDataRightsCorrectionCommand committed = CreateCommand(profile, "Corrected Guest");
        Guid eventId = Guid.NewGuid();
        var updated = profile.UpdateWithOutcome(
            committed.DisplayName,
            committed.LegalName,
            committed.Email,
            committed.Phone,
            committed.DateOfBirth,
            committed.NationalityCountryCode,
            committed.PreferredLanguageTag,
            committed.Notes,
            committed.ExpectedVersion,
            committed.ActorId,
            eventId,
            Now).Value;
        GuestDataRightsCorrectionReceipt receipt = GuestDataRightsCorrectionReceipt.Create(
            Guid.NewGuid(),
            "tenant-a",
            committed.IdempotencyKey,
            committed.PropertyId,
            committed.CaseId,
            committed.ApprovalRevision,
            committed.GuestId,
            updated.PreviousVersion,
            updated.CurrentVersion,
            updated.ChangedFields,
            updated.EventId,
            Guid.NewGuid(),
            updated.OccurredAtUtc).Value;
        ApplyGuestDataRightsCorrectionCommand changed = committed with
        {
            DisplayName = "Different Guest"
        };
        ApplyGuestDataRightsCorrectionCommandHandler handler = CreateHandler(
            profile,
            new RecordingReceiptRepository(receipt),
            new RecordingApprovalGate(),
            new QueueIdGenerator());

        Result<GuestDataRightsCorrectionReceiptDto> result =
            await handler.HandleAsync(changed, CancellationToken.None);

        Assert.Equal(GuestsApplicationErrors.CorrectionIdempotencyConflict, result.Error);
        Assert.Equal("Corrected Guest", profile.DisplayName);
        Assert.Equal(2, profile.Version);
    }

    [Fact]
    public async Task Collection_only_correction_records_v2_and_equivalent_set_replay_without_another_event()
    {
        GuestProfile profile = CreateProfile();
        RecordingReceiptRepository receipts = new();
        ApplyGuestDataRightsCorrectionCommand command = CreateCommand(profile, profile.DisplayName) with
        {
            LanguageTags = ["en-GB", "fr"]
        };
        ApplyGuestDataRightsCorrectionCommandHandler handler = CreateHandler(
            profile, receipts, new RecordingApprovalGate(),
            new QueueIdGenerator(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Result<GuestDataRightsCorrectionReceiptDto> first = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.Equal(2, receipts.Added!.ContractVersion);
        Assert.Equal("guest.profile.language-tags", Assert.Single(first.Value.ChangedFields));
        int eventCount = receipts.Added.DomainEvents.Count;
        handler = CreateHandler(profile, new RecordingReceiptRepository(receipts.Added),
            new RecordingApprovalGate(isApproved: false), new QueueIdGenerator());
        Result<GuestDataRightsCorrectionReceiptDto> replay = await handler.HandleAsync(
            command with { LanguageTags = ["FR", "en-gb"] }, CancellationToken.None);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value.ReceiptId, replay.Value.ReceiptId);
        Assert.Equal(eventCount, receipts.Added.DomainEvents.Count);
        Assert.Equal(GuestsApplicationErrors.CorrectionIdempotencyConflict,
            (await handler.HandleAsync(command with { LanguageTags = ["en-gb", "de"] }, CancellationToken.None)).Error);
        Assert.Equal(2, profile.Version);
    }

    private static ApplyGuestDataRightsCorrectionCommandHandler CreateHandler(
        GuestProfile profile,
        RecordingReceiptRepository receipts,
        RecordingApprovalGate approval,
        IIdGenerator ids,
        IGuestOperationLock? operationLock = null) => new(
        new RecordingGuestRepository(profile),
        receipts,
        operationLock ?? new NoopGuestOperationLock(),
        approval,
        new AllowedCountryPolicyAdmission(),
        new TestScopeContext(),
        new TestClock(),
        ids);

    private static ApplyGuestDataRightsCorrectionCommand CreateCommand(
        GuestProfile profile,
        string displayName) => new(
        Guid.NewGuid(),
        profile.OriginPropertyId,
        Guid.NewGuid(),
        3,
        profile.Id,
        profile.Version,
        displayName,
        profile.LegalName,
        profile.Email,
        profile.Phone,
        profile.DateOfBirth,
        profile.NationalityCountryCode,
        profile.PreferredLanguageTag,
        profile.Notes,
        "user:privacy-operator");

    private static GuestProfile CreateProfile() => GuestProfile.Create(
        Guid.NewGuid(),
        "tenant-a",
        Guid.NewGuid(),
        "Original Guest",
        null,
        "original@example.test",
        null,
        null,
        null,
        null,
        null,
        "user:creator",
        Guid.NewGuid(),
        Now.AddDays(-1)).Value;

    private sealed class RecordingGuestRepository(GuestProfile profile) : IGuestProfileRepository
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
            Task.FromResult(
                profile.OriginPropertyId == propertyId && profile.Id == guestId ? profile : null);

        public Task<GuestProfile?> GetForDataRightsAsync(
            Guid propertyId,
            Guid guestId,
            CancellationToken cancellationToken) =>
            this.GetVisibleAsync(propertyId, guestId, cancellationToken);

        public Task<GuestListResponse> ListVisibleAsync(
            Guid propertyId,
            string? search,
            GuestStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingReceiptRepository(
        GuestDataRightsCorrectionReceipt? existing = null)
        : IGuestDataRightsCorrectionReceiptRepository
    {
        public GuestDataRightsCorrectionReceipt? Added { get; private set; }

        public Task<GuestDataRightsCorrectionReceipt?> FindByIdempotencyKeyAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(existing?.IdempotencyKey == idempotencyKey ? existing : null);

        public Task AddAsync(
            GuestDataRightsCorrectionReceipt receipt,
            CancellationToken cancellationToken)
        {
            this.Added = receipt;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingApprovalGate(bool isApproved = true)
        : IDataRightsCorrectionExecutionGate
    {
        public DataRightsCorrectionExecutionGateRequest? Request { get; private set; }

        public Task<DataRightsCorrectionExecutionGateResult> EvaluateAsync(
            DataRightsCorrectionExecutionGateRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            return Task.FromResult(isApproved
                ? DataRightsCorrectionExecutionGateResult.Allowed(Now.AddMinutes(5))
                : DataRightsCorrectionExecutionGateResult.Denied(
                    DataRightsCorrectionExecutionDenial.CaseNotExecuting));
        }
    }

    private sealed class AllowedCountryPolicyAdmission : IGuestCountryPolicyAdmission
    {
        public Task<CountryPolicyDecision> EvaluateAsync(
            Guid propertyId,
            string purposeCode,
            CountryPolicySurface surface,
            string sourceProvenance,
            CancellationToken cancellationToken) =>
            Task.FromResult(CountryPolicyDecision.Allow(new CountryPolicyEvidence(
                "GB",
                "gb-hostel",
                1,
                "eu-west-2",
                "uk-no-transfer",
                "guest-operational",
                1,
                new string('a', 64),
                purposeCode,
                surface,
                sourceProvenance,
                CountryPolicyApprovalState.Approved,
                Now.AddDays(-1),
                Now.AddDays(30),
                Now,
                [])));
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

    private sealed class QueueIdGenerator(params Guid[] ids) : IIdGenerator
    {
        private readonly Queue<Guid> values = new(ids);

        public Guid NewId() => this.values.Dequeue();
    }
}

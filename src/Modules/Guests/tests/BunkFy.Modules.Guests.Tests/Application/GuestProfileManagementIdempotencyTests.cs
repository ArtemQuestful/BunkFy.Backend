namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Handlers;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.ValueObjects;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestProfileManagementIdempotencyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 7, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Normalized_update_retry_returns_original_receipt_without_mutating_again()
    {
        GuestProfile profile = CreateProfile();
        RecordingGuestRepository profiles = new(profile);
        RecordingManagementOperationRepository operations = new();
        Guid operationId = Guid.NewGuid();
        UpdateGuestProfileCommandHandler handler = CreateUpdateHandler(profiles, operations);
        UpdateGuestProfileCommand command = UpdateCommand(profile, operationId) with
        {
            DisplayName = "  Maya Chen  ",
            Email = " MAYA@EXAMPLE.TEST ",
            NationalityCountryCode = " gb "
        };

        Result<GuestMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<GuestMutationReceiptDto> replay = await handler.HandleAsync(
            command with { ActorId = "user:retrying-operator" },
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(2, profile.Version);
        Assert.Equal("Maya Chen", profile.DisplayName);
        Assert.Equal("maya@example.test", profile.Email);
        Assert.Equal("GB", profile.NationalityCountryCode);
        Assert.Single(operations.Items);
    }

    [Fact]
    public async Task Reusing_update_operation_for_changed_request_conflicts()
    {
        GuestProfile profile = CreateProfile();
        RecordingGuestRepository profiles = new(profile);
        RecordingManagementOperationRepository operations = new();
        Guid operationId = Guid.NewGuid();
        UpdateGuestProfileCommandHandler handler = CreateUpdateHandler(profiles, operations);
        UpdateGuestProfileCommand command = UpdateCommand(profile, operationId);
        Assert.True((await handler.HandleAsync(command, CancellationToken.None)).IsSuccess);

        Result<GuestMutationReceiptDto> result = await handler.HandleAsync(
            command with { Phone = "+44 20 9999 0000" },
            CancellationToken.None);

        Assert.Equal(GuestsApplicationErrors.ManagementOperationConflict, result.Error);
        Assert.Equal(2, profile.Version);
        Assert.Single(operations.Items);
    }

    [Fact]
    public async Task Archive_retry_returns_original_receipt_without_mutating_again()
    {
        GuestProfile profile = CreateProfile();
        RecordingGuestRepository profiles = new(profile);
        RecordingManagementOperationRepository operations = new();
        Guid operationId = Guid.NewGuid();
        ArchiveGuestProfileCommandHandler handler = CreateArchiveHandler(profiles, operations);
        ArchiveGuestProfileCommand command = new(
            operationId,
            profile.OriginPropertyId,
            profile.Id,
            profile.Version,
            "user:operator");

        Result<GuestMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<GuestMutationReceiptDto> replay = await handler.HandleAsync(
            command with { ActorId = "user:retrying-operator" },
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(GuestStatus.Archived, replay.Value.Status);
        Assert.Equal(2, profile.Version);
        Assert.Single(operations.Items);
    }

    [Fact]
    public async Task Management_receipts_use_persistence_timestamp_precision()
    {
        GuestProfile profile = CreateProfile();
        RecordingGuestRepository profiles = new(profile);
        RecordingManagementOperationRepository operations = new();
        TestClock clock = new(Now.AddTicks(7));

        Result<GuestMutationReceiptDto> updated = await CreateUpdateHandler(
            profiles,
            operations,
            clock: clock).HandleAsync(
                UpdateCommand(profile, Guid.NewGuid()),
                CancellationToken.None);
        Assert.True(updated.IsSuccess);

        Result<GuestMutationReceiptDto> archived = await CreateArchiveHandler(
            profiles,
            operations,
            clock).HandleAsync(
                new ArchiveGuestProfileCommand(
                    Guid.NewGuid(),
                    profile.OriginPropertyId,
                    profile.Id,
                    updated.Value.Version,
                    "user:operator"),
                CancellationToken.None);

        Assert.True(archived.IsSuccess);
        const long ticksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        Assert.Equal(
            0,
            updated.Value.LastChangedAtUtc.Ticks % ticksPerMicrosecond);
        Assert.Equal(
            0,
            archived.Value.LastChangedAtUtc.Ticks % ticksPerMicrosecond);
        Assert.Equal(TimeSpan.Zero, updated.Value.LastChangedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, archived.Value.LastChangedAtUtc.Offset);
    }

    [Fact]
    public async Task Reusing_operation_for_another_management_kind_conflicts()
    {
        GuestProfile profile = CreateProfile();
        RecordingGuestRepository profiles = new(profile);
        RecordingManagementOperationRepository operations = new();
        Guid operationId = Guid.NewGuid();
        Assert.True((await CreateUpdateHandler(profiles, operations).HandleAsync(
            UpdateCommand(profile, operationId),
            CancellationToken.None)).IsSuccess);

        Result<GuestMutationReceiptDto> result = await CreateArchiveHandler(
            profiles,
            operations).HandleAsync(
                new ArchiveGuestProfileCommand(
                    operationId,
                    profile.OriginPropertyId,
                    profile.Id,
                    ExpectedVersion: 1,
                    "user:operator"),
                CancellationToken.None);

        Assert.Equal(GuestsApplicationErrors.ManagementOperationConflict, result.Error);
        Assert.Equal(GuestProfileState.Active, profile.Status);
        Assert.Equal(2, profile.Version);
    }

    [Fact]
    public async Task Failed_update_does_not_claim_the_operation_id()
    {
        GuestProfile profile = CreateProfile();
        RecordingManagementOperationRepository operations = new();
        UpdateGuestProfileCommand command = UpdateCommand(profile, Guid.NewGuid()) with
        {
            ExpectedVersion = profile.Version + 1
        };

        Result<GuestMutationReceiptDto> result = await CreateUpdateHandler(
            new RecordingGuestRepository(profile),
            operations).HandleAsync(command, CancellationToken.None);

        Assert.Equal(GuestsApplicationErrors.VersionConflict, result.Error);
        Assert.Empty(operations.Items);
        Assert.Equal(1, profile.Version);
    }

    [Fact]
    public async Task Country_policy_is_rechecked_before_update_replay()
    {
        GuestProfile profile = CreateProfile();
        RecordingGuestRepository profiles = new(profile);
        RecordingManagementOperationRepository operations = new();
        UpdateGuestProfileCommand command = UpdateCommand(profile, Guid.NewGuid());
        Assert.True((await CreateUpdateHandler(profiles, operations).HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        int readsBeforeReplay = profiles.VisibleReads;

        Result<GuestMutationReceiptDto> result = await CreateUpdateHandler(
            profiles,
            operations,
            new DeniedCountryPolicyAdmission()).HandleAsync(
                command,
                CancellationToken.None);

        Assert.Equal(
            GuestsApplicationErrors.CountryPolicyDenied(
                CountryPolicyDecisionReason.MissingBinding),
            result.Error);
        Assert.Equal(readsBeforeReplay, profiles.VisibleReads);
    }

    [Fact]
    public void Update_fingerprint_uses_normalized_values_and_contains_no_plain_profile_data()
    {
        Guid propertyId = Guid.NewGuid();
        Guid guestId = Guid.NewGuid();
        GuestProfileChange first = GuestProfileChange.Create(
            "  Maya Chen  ",
            null,
            " MAYA@EXAMPLE.TEST ",
            null,
            new DateOnly(1990, 2, 3),
            " gb ",
            " en-GB ",
            " Quiet room. ",
            "user:first",
            Now).Value;
        GuestProfileChange equivalent = GuestProfileChange.Create(
            "Maya Chen",
            null,
            "maya@example.test",
            null,
            new DateOnly(1990, 2, 3),
            "GB",
            "en-GB",
            "Quiet room.",
            "user:second",
            Now).Value;

        string firstFingerprint = GuestManagementOperationFingerprint.Update(
            propertyId,
            guestId,
            1,
            first);
        string equivalentFingerprint = GuestManagementOperationFingerprint.Update(
            propertyId,
            guestId,
            1,
            equivalent);
        string changedFingerprint = GuestManagementOperationFingerprint.Update(
            propertyId,
            guestId,
            2,
            equivalent);

        Assert.Equal(firstFingerprint, equivalentFingerprint);
        Assert.NotEqual(firstFingerprint, changedFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", firstFingerprint);
        Assert.DoesNotContain("maya", firstFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private static UpdateGuestProfileCommandHandler CreateUpdateHandler(
        RecordingGuestRepository profiles,
        IGuestManagementOperationRepository operations,
        IGuestCountryPolicyAdmission? countryPolicy = null,
        ISystemClock? clock = null) => new(
        new GuestMutationCoordinator(profiles, new NoopGuestOperationLock(), new TestScopeContext()),
        countryPolicy ?? new AllowedCountryPolicyAdmission(),
        operations,
        clock ?? new TestClock(),
        new TestIdGenerator());

    [Fact]
    public void Omitted_language_collection_retains_the_historic_v1_fingerprint_bytes()
    {
        GuestProfileChange request = GuestProfileChange.Create("Maya Chen", null,
            "maya@example.test", null, new DateOnly(1990, 2, 3), "GB", "en-GB",
            "Quiet room.", "operator", Now).Value;
        string fingerprint = GuestManagementOperationFingerprint.Update(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 1, request);
        Assert.Equal("99fcf36b3f9b85378fe07d7760f4a29b6bd81c6d546af5e36e0e1062d74a6257", fingerprint);
    }

    [Fact]
    public async Task Explicit_language_replay_uses_request_identity_before_later_profile_version()
    {
        GuestProfile profile = CreateProfile();
        RecordingGuestRepository profiles = new(profile);
        RecordingManagementOperationRepository operations = new();
        UpdateGuestProfileCommandHandler handler = CreateUpdateHandler(profiles, operations);
        UpdateGuestProfileCommand command = UpdateCommand(profile, Guid.NewGuid()) with
        {
            LanguageTags = ["en-GB", "fr"]
        };
        Result<GuestMutationReceiptDto> first = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess);
        UpdateGuestProfileCommand later = command with
        {
            OperationId = Guid.NewGuid(),
            ExpectedVersion = profile.Version,
            LanguageTags = ["en-gb", "de"]
        };
        Assert.True((await handler.HandleAsync(later, CancellationToken.None)).IsSuccess);
        Result<GuestMutationReceiptDto> replay = await handler.HandleAsync(
            command with { LanguageTags = ["FR", "en-gb", "fr"] }, CancellationToken.None);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(3, profile.Version);
        Assert.Contains("de", profile.LanguageTags);
        Assert.Equal(GuestsApplicationErrors.ManagementOperationConflict,
            (await handler.HandleAsync(command with { LanguageTags = ["en-gb", "it"] }, CancellationToken.None)).Error);
        Assert.Equal(GuestsApplicationErrors.ManagementOperationConflict,
            (await handler.HandleAsync(command with { LanguageTags = null }, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Explicit_empty_and_omitted_update_have_distinct_replay_identity()
    {
        GuestProfile profile = CreateProfile();
        RecordingGuestRepository profiles = new(profile);
        RecordingManagementOperationRepository operations = new();
        UpdateGuestProfileCommandHandler handler = CreateUpdateHandler(profiles, operations);
        UpdateGuestProfileCommand command = UpdateCommand(profile, Guid.NewGuid()) with
        {
            PreferredLanguageTag = null,
            LanguageTags = []
        };
        Assert.True((await handler.HandleAsync(command, CancellationToken.None)).IsSuccess);
        Assert.Equal(GuestsApplicationErrors.ManagementOperationConflict,
            (await handler.HandleAsync(command with { LanguageTags = null }, CancellationToken.None)).Error);
        Assert.True((await handler.HandleAsync(command, CancellationToken.None)).IsSuccess);
        Assert.Empty(profile.LanguageTags);
    }

    private static ArchiveGuestProfileCommandHandler CreateArchiveHandler(
        RecordingGuestRepository profiles,
        IGuestManagementOperationRepository operations,
        ISystemClock? clock = null) => new(
        new GuestMutationCoordinator(profiles, new NoopGuestOperationLock(), new TestScopeContext()),
        operations,
        clock ?? new TestClock(),
        new TestIdGenerator());

    private static UpdateGuestProfileCommand UpdateCommand(
        GuestProfile profile,
        Guid operationId) => new(
        operationId,
        profile.OriginPropertyId,
        profile.Id,
        "Maya Chen",
        "Maya Q. Chen",
        "maya@example.test",
        "+44 20 1234 5678",
        new DateOnly(1990, 2, 3),
        "GB",
        "en-GB",
        "Prefers a lower bunk.",
        profile.Version,
        "user:operator");

    private static GuestProfile CreateProfile() => GuestProfile.Create(
        Guid.NewGuid(),
        TestScopeContext.TenantId,
        Guid.NewGuid(),
        "Original",
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

    private sealed class RecordingGuestRepository(GuestProfile profile)
        : IGuestProfileRepository
    {
        public int VisibleReads { get; private set; }

        public Task AddUnderAcquiredOperationLockAsync(
            GuestProfile added,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GuestProfile?> GetByIdAsync(
            Guid guestId,
            CancellationToken cancellationToken) => Task.FromResult(
            profile.Id == guestId ? profile : null);

        public Task<GuestProfile?> GetVisibleAsync(
            Guid propertyId,
            Guid guestId,
            CancellationToken cancellationToken)
        {
            this.VisibleReads++;
            return Task.FromResult(
                profile.OriginPropertyId == propertyId && profile.Id == guestId
                    ? profile
                    : null);
        }

        public Task<GuestProfile?> GetForDataRightsAsync(
            Guid propertyId,
            Guid guestId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GuestListResponse> ListVisibleAsync(
            Guid propertyId,
            string? search,
            GuestStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingManagementOperationRepository
        : IGuestManagementOperationRepository
    {
        public List<GuestManagementOperationRecord> Items { get; } = [];

        public Task<GuestManagementOperationRecord?> GetAsync(
            Guid guestId,
            Guid operationId,
            CancellationToken cancellationToken) => Task.FromResult(
            this.Items.SingleOrDefault(item =>
                item.GuestId == guestId && item.OperationId == operationId));

        public Task AddAsync(
            GuestManagementOperationRecord operation,
            CancellationToken cancellationToken)
        {
            this.Items.Add(operation);
            return Task.CompletedTask;
        }

        public Task DeleteForGuestAsync(
            Guid guestId,
            CancellationToken cancellationToken)
        {
            this.Items.RemoveAll(item => item.GuestId == guestId);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowedCountryPolicyAdmission : IGuestCountryPolicyAdmission
    {
        public Task<CountryPolicyDecision> EvaluateAsync(
            Guid propertyId,
            string purposeCode,
            CountryPolicySurface surface,
            string sourceProvenance,
            CancellationToken cancellationToken) => Task.FromResult(
            CountryPolicyDecision.Allow(new CountryPolicyEvidence(
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

    private sealed class DeniedCountryPolicyAdmission : IGuestCountryPolicyAdmission
    {
        public Task<CountryPolicyDecision> EvaluateAsync(
            Guid propertyId,
            string purposeCode,
            CountryPolicySurface surface,
            string sourceProvenance,
            CancellationToken cancellationToken) => Task.FromResult(
            CountryPolicyDecision.Deny(CountryPolicyDecisionReason.MissingBinding));
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public const string TenantId = "tenant-a";
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock(DateTimeOffset? nowUtc = null) : ISystemClock
    {
        public DateTimeOffset UtcNow => nowUtc ?? Now;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }
}

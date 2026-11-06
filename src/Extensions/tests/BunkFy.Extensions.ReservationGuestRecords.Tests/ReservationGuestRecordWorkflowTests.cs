namespace BunkFy.Extensions.ReservationGuestRecords.Tests;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationGuestRecordWorkflowTests
{
    private static readonly Guid OperationId = Guid.Parse(
        "10000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyId = Guid.Parse(
        "20000000-0000-0000-0000-000000000002");
    private static readonly Guid ReservationId = Guid.Parse(
        "30000000-0000-0000-0000-000000000003");
    private static readonly Guid ConfirmationId = Guid.Parse(
        "40000000-0000-0000-0000-000000000004");
    private const string ActorId = "user:operator-a";

    [Fact]
    public async Task Prepared_process_creates_the_exact_guest_then_confirms_it()
    {
        RecordingGuests guests = new();
        RecordingReservations reservations = new(Process(
            ReservationGuestRecordLinkStatus.Prepared));
        ReservationGuestRecordWorkflow workflow = new(guests, reservations);

        Result<ReservationGuestRecordLinkProcessDto> result =
            await workflow.ExecuteAsync(Request(), ActorId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationGuestRecordLinkStatus.Ready, result.Value.Status);
        GuestProfileCreationRequest guest = Assert.Single(guests.Requests);
        Assert.Equal(OperationId, guest.OperationId);
        Assert.Equal(PropertyId, guest.PropertyId);
        Assert.Equal(ConfirmationId, guest.CreationConfirmationId);
        Assert.Equal(ActorId, guest.ActorId);
        Assert.Equal("Maya Chen", guest.DisplayName);
        PrepareReservationGuestRecordLinkRequest preparation =
            Assert.Single(reservations.Preparations);
        Assert.Equal(ActorId, preparation.ActorId);
        Assert.Equal(7, preparation.ExpectedReservationVersion);
        ConfirmReservationGuestRecordLinkRequest confirmation =
            Assert.Single(reservations.Confirmations);
        Assert.Equal(ConfirmationId, confirmation.CreationConfirmationId);
        Assert.Empty(reservations.Retries);
    }

    [Fact]
    public async Task Resumed_process_uses_the_canonical_operation_identity()
    {
        Guid canonicalOperationId = Guid.Parse(
            "50000000-0000-0000-0000-000000000005");
        ReservationGuestRecordLinkProcessDto canonical = Process(
            ReservationGuestRecordLinkStatus.Prepared) with
        {
            OperationId = canonicalOperationId,
            GuestId = canonicalOperationId
        };
        RecordingGuests guests = new();
        RecordingReservations reservations = new(canonical);
        ReservationGuestRecordWorkflow workflow = new(guests, reservations);

        Result<ReservationGuestRecordLinkProcessDto> result =
            await workflow.ExecuteAsync(Request(), ActorId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(canonicalOperationId, result.Value.OperationId);
        Assert.Equal(
            canonicalOperationId,
            Assert.Single(guests.Requests).OperationId);
        Assert.Equal(
            canonicalOperationId,
            Assert.Single(reservations.Confirmations).OperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_process_preserves_explicit_language_collection_presence(bool clear)
    {
        RecordingGuests guests = new();
        RecordingReservations reservations = new(Process(ReservationGuestRecordLinkStatus.Prepared));
        ReservationGuestRecordWorkflow workflow = new(guests, reservations);
        string[] languages = clear ? [] : ["fr", "unknown-١"];
        Result<ReservationGuestRecordLinkProcessDto> result = await workflow.ExecuteAsync(
            Request() with { PreferredLanguageTag = null, LanguageTags = languages }, ActorId, CancellationToken.None);
        Assert.True(result.IsSuccess);
        GuestProfileCreationRequest forwarded = Assert.Single(guests.Requests);
        Assert.Equal(languages, forwarded.LanguageTags);
        Assert.Null(forwarded.PreferredLanguageTag);
    }

    [Fact]
    public async Task Resumed_process_preserves_original_guest_creation_attribution()
    {
        const string originalActorId = "user:operator-a";
        const string resumingActorId = "user:operator-b";
        RecordingGuests guests = new();
        RecordingReservations reservations = new(
            Process(ReservationGuestRecordLinkStatus.Prepared),
            originalActorId);
        ReservationGuestRecordWorkflow workflow = new(guests, reservations);

        Result<ReservationGuestRecordLinkProcessDto> result =
            await workflow.ExecuteAsync(
                Request(),
                resumingActorId,
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            resumingActorId,
            Assert.Single(reservations.Preparations).ActorId);
        Assert.Equal(originalActorId, Assert.Single(guests.Requests).ActorId);
    }

    [Fact]
    public async Task Prepared_process_without_original_actor_fails_before_guest_creation()
    {
        RecordingGuests guests = new();
        RecordingReservations reservations = new(
            Process(ReservationGuestRecordLinkStatus.Prepared),
            guestCreationActorId: null);
        ReservationGuestRecordWorkflow workflow = new(guests, reservations);

        Result<ReservationGuestRecordLinkProcessDto> result =
            await workflow.ExecuteAsync(Request(), ActorId, CancellationToken.None);

        Assert.Equal(
            ReservationGuestRecordWorkflowErrors.ProcessStateInvalid,
            result.Error);
        Assert.Empty(guests.Requests);
        Assert.Empty(reservations.Confirmations);
    }

    [Fact]
    public async Task Completed_replay_does_not_recreate_or_redispatch()
    {
        RecordingGuests guests = new();
        RecordingReservations reservations = new(Process(
            ReservationGuestRecordLinkStatus.Completed));
        ReservationGuestRecordWorkflow workflow = new(guests, reservations);

        Result<ReservationGuestRecordLinkProcessDto> result =
            await workflow.ExecuteAsync(Request(), ActorId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationGuestRecordLinkStatus.Completed, result.Value.Status);
        Assert.Empty(guests.Requests);
        Assert.Empty(reservations.Confirmations);
        Assert.Empty(reservations.Retries);
    }

    [Fact]
    public async Task Review_replay_uses_the_explicit_reservations_retry_only()
    {
        RecordingGuests guests = new();
        RecordingReservations reservations = new(Process(
            ReservationGuestRecordLinkStatus.NeedsReview,
            ReservationGuestRecordLinkReviewReason.GuestUnavailable));
        ReservationGuestRecordWorkflow workflow = new(guests, reservations);

        Result<ReservationGuestRecordLinkProcessDto> result =
            await workflow.ExecuteAsync(Request(), ActorId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationGuestRecordLinkStatus.Ready, result.Value.Status);
        Assert.Empty(guests.Requests);
        Assert.Empty(reservations.Confirmations);
        Assert.Single(reservations.Retries);
    }

    [Fact]
    public async Task Guest_failure_leaves_the_prepared_process_unconfirmed()
    {
        Error error = new("Guests.DisplayNameInvalid", "Display name invalid.");
        RecordingGuests guests = new(error);
        RecordingReservations reservations = new(Process(
            ReservationGuestRecordLinkStatus.Prepared));
        ReservationGuestRecordWorkflow workflow = new(guests, reservations);

        Result<ReservationGuestRecordLinkProcessDto> result =
            await workflow.ExecuteAsync(Request(), ActorId, CancellationToken.None);

        Assert.Equal(error, result.Error);
        Assert.Empty(reservations.Confirmations);
        Assert.Empty(reservations.Retries);
    }

    [Fact]
    public void Workflow_coordinates_do_not_carry_profile_data_into_reservations()
    {
        string[] preparationMembers =
            typeof(PrepareReservationGuestRecordLinkRequest)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray();
        string[] processMembers = typeof(ReservationGuestRecordLinkProcessDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        string[] prohibited =
        [
            "DisplayName",
            "LegalName",
            "Email",
            "Phone",
            "DateOfBirth",
            "NationalityCountryCode",
            "PreferredLanguageTag",
            "Notes"
        ];

        Assert.DoesNotContain(preparationMembers, prohibited.Contains);
        Assert.DoesNotContain(processMembers, prohibited.Contains);
    }

    [Fact]
    public void Public_process_status_does_not_expose_actor_attribution()
    {
        string[] members = typeof(ReservationGuestRecordLinkProcessDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("GuestCreationActorId", members);
        Assert.DoesNotContain("RequestedBy", members);
        Assert.DoesNotContain("ActorId", members);
    }

    private static ReservationGuestRecordWorkflowRequest Request() => new(
        OperationId,
        PropertyId,
        ReservationId,
        7,
        "Maya Chen",
        "Maya Q. Chen",
        "maya@example.test",
        "+44 20 1234 5678",
        new DateOnly(1990, 2, 3),
        "GB",
        "en-GB",
        "Prefers a lower bunk.");

    private static ReservationGuestRecordLinkProcessDto Process(
        ReservationGuestRecordLinkStatus status,
        ReservationGuestRecordLinkReviewReason reason =
            ReservationGuestRecordLinkReviewReason.None) => new(
        OperationId,
        PropertyId,
        ReservationId,
        OperationId,
        status,
        reason,
        1,
        status == ReservationGuestRecordLinkStatus.Prepared ? 0 : 1,
        new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero),
        new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero));

    private sealed class RecordingGuests(Error? failure = null)
        : IGuestProfileCreationCapability
    {
        public List<GuestProfileCreationRequest> Requests { get; } = [];

        public Task<Result<GuestMutationReceiptDto>> EnsureCreatedAsync(
            GuestProfileCreationRequest request,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            return Task.FromResult(failure is null
                ? Result.Success(new GuestMutationReceiptDto(
                    request.OperationId,
                    GuestStatus.Active,
                    1,
                    new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero)))
                : Result.Failure<GuestMutationReceiptDto>(failure));
        }
    }

    private sealed class RecordingReservations(
        ReservationGuestRecordLinkProcessDto preparedProcess,
        string? guestCreationActorId = ActorId)
        : IReservationGuestRecordLinkCapability
    {
        public List<PrepareReservationGuestRecordLinkRequest> Preparations
        {
            get;
        } = [];

        public List<ConfirmReservationGuestRecordLinkRequest> Confirmations
        {
            get;
        } = [];

        public List<RetryReservationGuestRecordLinkRequest> Retries { get; } = [];

        public Task<Result<ReservationGuestRecordLinkPreparationDto>>
            PrepareAsync(
                PrepareReservationGuestRecordLinkRequest request,
                CancellationToken cancellationToken)
        {
            this.Preparations.Add(request);
            return Task.FromResult(Result.Success(
                new ReservationGuestRecordLinkPreparationDto(
                    preparedProcess,
                    ConfirmationId,
                    guestCreationActorId)));
        }

        public Task<Result<ReservationGuestRecordLinkProcessDto>>
            ConfirmGuestAsync(
                ConfirmReservationGuestRecordLinkRequest request,
                CancellationToken cancellationToken)
        {
            this.Confirmations.Add(request);
            return Task.FromResult(Result.Success(preparedProcess with
            {
                Status = ReservationGuestRecordLinkStatus.Ready,
                ReviewReason = ReservationGuestRecordLinkReviewReason.None,
                DispatchRevision = 1
            }));
        }

        public Task<Result<ReservationGuestRecordLinkProcessDto>> RetryAsync(
            RetryReservationGuestRecordLinkRequest request,
            CancellationToken cancellationToken)
        {
            this.Retries.Add(request);
            return Task.FromResult(Result.Success(preparedProcess with
            {
                Status = ReservationGuestRecordLinkStatus.Ready,
                ReviewReason = ReservationGuestRecordLinkReviewReason.None,
                DispatchRevision = 1
            }));
        }

        public Task<Result<ReservationGuestRecordLinkProcessDto>> GetAsync(
            GetReservationGuestRecordLinkRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(preparedProcess));
    }
}

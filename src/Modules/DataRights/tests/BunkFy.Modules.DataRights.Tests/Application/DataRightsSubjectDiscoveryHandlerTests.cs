namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Application.Validation;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsSubjectDiscoveryHandlerTests
{
    [Fact]
    public async Task Exact_owner_filter_is_normalized_and_does_not_invoke_other_owners()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DataRightsSubjectCandidate reservation = Candidate("reservations", Guid.NewGuid());
        StubContributor guests = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success(
                [Candidate("guests", Guid.NewGuid())]));
        StubContributor reservations = new(
            "reservations",
            _ => DataRightsSubjectDiscoveryResult.Success([reservation]));
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [guests, reservations],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(null, "guest@example.test", null, null, null),
                " RESERVATIONS "),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([reservation], result.Value.Candidates);
        Assert.False(result.Value.LimitReached);
        Assert.Equal(0, guests.DiscoveryInvocationCount);
        Assert.Equal(1, reservations.DiscoveryInvocationCount);
    }

    [Fact]
    public async Task Unknown_owner_filter_fails_closed_without_invoking_contributors()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        StubContributor guests = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]));
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [guests],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(null, "guest@example.test", null, null, null),
                "reservations"),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerUnavailable, result.Error);
        Assert.Equal(0, guests.DiscoveryInvocationCount);
    }

    [Fact]
    public async Task Filtered_discovery_fails_closed_for_duplicate_owner_registrations()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        StubContributor first = new(
            "reservations",
            _ => DataRightsSubjectDiscoveryResult.Success([]));
        StubContributor duplicate = new(
            " RESERVATIONS ",
            _ => DataRightsSubjectDiscoveryResult.Success([]));
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [first, duplicate],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(Guid.NewGuid(), null, null, null, null),
                "reservations"),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerCatalogInvalid, result.Error);
        Assert.Equal(0, first.DiscoveryInvocationCount);
        Assert.Equal(0, duplicate.DiscoveryInvocationCount);
    }

    [Fact]
    public void Owner_filter_rejects_blank_and_oversized_values()
    {
        DiscoverDataRightsSubjectsQueryValidator validator = new();
        DataRightsSubjectLookup lookup = new(
            Guid.NewGuid(),
            null,
            null,
            null,
            null);

        string[][] errors =
        [
            validator.Validate(new(
                DataRightsCaseScope.ForProperty(Guid.NewGuid()),
                Guid.NewGuid(),
                lookup,
                " ")).ToArray(),
            validator.Validate(new(
                DataRightsCaseScope.ForProperty(Guid.NewGuid()),
                Guid.NewGuid(),
                lookup,
                new string('o', DataRightsSubjectDiscoveryLimits.OwnerKeyMaxLength + 1))).ToArray()
        ];

        Assert.All(errors, validationErrors =>
            Assert.Contains(validationErrors, error => error.StartsWith(
                "OwnerKey must contain between 1 and ",
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Duplicate_candidates_do_not_starve_later_owners()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DataRightsSubjectCandidate first = Candidate("alpha", Guid.NewGuid());
        DataRightsSubjectCandidate second = Candidate("beta", Guid.NewGuid());
        StubContributor alpha = new(
            "alpha",
            request => DataRightsSubjectDiscoveryResult.Success(
                Enumerable.Repeat(first, request.MaxCandidates).ToArray()));
        StubContributor beta = new(
            "beta",
            _ => DataRightsSubjectDiscoveryResult.Success([second]));
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [beta, alpha],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(null, "guest@example.test", null, null, null)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([first, second], result.Value.Candidates);
    }

    [Fact]
    public async Task Discovery_reports_when_the_bounded_candidate_limit_is_reached()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DataRightsSubjectCandidate[] candidates = Enumerable
            .Range(0, DataRightsSubjectDiscoveryLimits.MaxCandidates)
            .Select(_ => Candidate("guests", Guid.NewGuid()))
            .ToArray();
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [
                new StubContributor(
                    "guests",
                    _ => DataRightsSubjectDiscoveryResult.Success(candidates))
            ],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(
                    null,
                    "guest@example.test",
                    null,
                    null,
                    null)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DataRightsSubjectDiscoveryLimits.MaxCandidates, result.Value.Candidates.Count);
        Assert.True(result.Value.LimitReached);
    }

    [Fact]
    public async Task Staff_discovery_invokes_only_staff_contributors_without_a_property_scope()
    {
        DataRightsCase dataRightsCase = CreateStaffDiscoveryCase();
        DataRightsSubjectCandidate candidate = new(
            new DataRightsSubjectCoordinate(
                "staff",
                "staff-profile",
                Guid.NewGuid(),
                3),
            "Staff member",
            null,
            null);
        StubContributor guests = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]));
        StubContributor staff = new(
            "staff",
            _ => DataRightsSubjectDiscoveryResult.Success([candidate]),
            supportedCaseTypes: [DataRightsCaseType.StaffRights]);
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [guests, staff],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                new DataRightsSubjectLookup(
                    RecordId: null,
                    Email: null,
                    Phone: null,
                    Name: null,
                    DateOfBirth: null,
                    AccountSubjectId: "account-subject-123")),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(candidate, Assert.Single(result.Value.Candidates));
        Assert.Equal(0, guests.DiscoveryInvocationCount);
        Assert.Equal(1, staff.DiscoveryInvocationCount);
        Assert.NotNull(staff.LastDiscoveryRequest);
        Assert.Equal(
            DataRightsCaseType.StaffRights,
            staff.LastDiscoveryRequest.CaseType);
        Assert.Null(staff.LastDiscoveryRequest.PropertyId);
        Assert.Equal(
            "account-subject-123",
            staff.LastDiscoveryRequest.Lookup.AccountSubjectId);
    }

    [Fact]
    public async Task Malformed_contributor_candidate_fails_closed_without_throwing()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DataRightsSubjectCandidate malformed = new(
            new DataRightsSubjectCoordinate(null!, "guest-profile", Guid.NewGuid(), 1),
            "Guest",
            null,
            null);
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [
                new StubContributor(
                    "guests",
                    _ => DataRightsSubjectDiscoveryResult.Success([malformed]))
            ],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(null, null, "+44 20 1234 5678", null, null)),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerResultInvalid, result.Error);
    }

    [Fact]
    public async Task Null_discovery_result_fails_closed_without_throwing()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [new StubContributor("guests", _ => null!)],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(null, "guest@example.test", null, null, null)),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerResultInvalid, result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_owner_retry_is_explicit_for_result_or_exception(
        bool throws)
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [
                new StubContributor(
                    "guests",
                    _ => throws
                        ? throw new InvalidOperationException("owner unavailable")
                        : DataRightsSubjectDiscoveryResult.RetryRequired())
            ],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(
                    null,
                    "guest@example.test",
                    null,
                    null,
                    null)),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerRetryRequired, result.Error);
    }

    [Fact]
    public async Task Discovery_owner_retry_with_candidates_is_an_invalid_result()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [
                new StubContributor(
                    "guests",
                    _ => new DataRightsSubjectDiscoveryResult(
                        DataRightsSubjectDiscoveryStatus.RetryRequired,
                        [Candidate("guests", Guid.NewGuid())]))
            ],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(
                    null,
                    "guest@example.test",
                    null,
                    null,
                    null)),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerResultInvalid, result.Error);
    }

    [Fact]
    public async Task Discovery_cancellation_is_not_reclassified_as_a_retry()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        using CancellationTokenSource source = new();
        source.Cancel();
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [
                new StubContributor(
                    "guests",
                    _ => throw new OperationCanceledException(source.Token))
            ],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(
                    null,
                    "guest@example.test",
                    null,
                    null,
                    null)),
            source.Token));
    }

    [Fact]
    public async Task Normal_discovery_return_after_cancellation_stops_later_owners()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        using CancellationTokenSource source = new();
        StubContributor guests = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]),
            afterDiscover: _ => source.Cancel());
        StubContributor reservations = new(
            "reservations",
            _ => DataRightsSubjectDiscoveryResult.Success([]));
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [reservations, guests],
            new TestScopeContext(),
            NullLogger<DiscoverDataRightsSubjectsQueryHandler>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new DiscoverDataRightsSubjectsQuery(
                    DataRightsCaseScope.ForProperty(propertyId),
                    dataRightsCase.Id,
                    new DataRightsSubjectLookup(
                        null,
                        "guest@example.test",
                        null,
                        null,
                        null)),
                source.Token));

        Assert.Equal(1, guests.DiscoveryInvocationCount);
        Assert.Equal(0, reservations.DiscoveryInvocationCount);
    }

    [Fact]
    public async Task Discovery_owner_failure_log_excludes_lookup_and_exception_details()
    {
        const string email = "private.lookup@example.test";
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        CapturingLogger<DiscoverDataRightsSubjectsQueryHandler> logger = new();
        DiscoverDataRightsSubjectsQueryHandler handler = new(
            new CaseRepository(dataRightsCase),
            [
                new StubContributor(
                    "guests",
                    _ => throw new InvalidOperationException("private failure detail"))
            ],
            new TestScopeContext(),
            logger);

        Result<DataRightsSubjectDiscoveryResponse> result = await handler.HandleAsync(
            new DiscoverDataRightsSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectLookup(null, email, null, null, null)),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerRetryRequired, result.Error);
        string message = Assert.Single(logger.Messages);
        Assert.Contains("guests", message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", message, StringComparison.Ordinal);
        Assert.DoesNotContain(email, message, StringComparison.Ordinal);
        Assert.DoesNotContain("private failure detail", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_cannot_validate_a_different_selection()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DataRightsSubjectCoordinate requested = new(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            4);
        StubContributor contributor = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]),
            _ => DataRightsSubjectSelectionValidation.Valid(
                requested with { RecordId = Guid.NewGuid() }));
        SelectDataRightsSubjectCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new CaseRepository(dataRightsCase)),
            [contributor],
            new TestScopeContext(),
            new TestClock(),
            NullLogger<SelectDataRightsSubjectCommandHandler>.Instance);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new SelectDataRightsSubjectCommand(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                requested,
                dataRightsCase.Version,
                "user:operator"),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerResultInvalid, result.Error);
        Assert.Empty(dataRightsCase.SelectedSubjects);
    }

    [Fact]
    public async Task Normal_selection_return_after_cancellation_does_not_mutate_the_case()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DataRightsSubjectCoordinate requested = new(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            4);
        using CancellationTokenSource source = new();
        StubContributor contributor = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]),
            _ => DataRightsSubjectSelectionValidation.Valid(requested),
            afterValidation: _ => source.Cancel());
        SelectDataRightsSubjectCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new CaseRepository(dataRightsCase)),
            [contributor],
            new TestScopeContext(),
            new TestClock(),
            NullLogger<SelectDataRightsSubjectCommandHandler>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new SelectDataRightsSubjectCommand(
                    DataRightsCaseScope.ForProperty(propertyId),
                    dataRightsCase.Id,
                    requested,
                    dataRightsCase.Version,
                    "user:operator"),
                source.Token));

        Assert.Equal(1, contributor.SelectionInvocationCount);
        Assert.Empty(dataRightsCase.SelectedSubjects);
    }

    [Fact]
    public async Task Null_selection_validation_fails_closed_without_throwing()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DataRightsSubjectCoordinate requested = new(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            4);
        StubContributor contributor = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]),
            _ => null!);
        SelectDataRightsSubjectCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new CaseRepository(dataRightsCase)),
            [contributor],
            new TestScopeContext(),
            new TestClock(),
            NullLogger<SelectDataRightsSubjectCommandHandler>.Instance);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new SelectDataRightsSubjectCommand(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                requested,
                dataRightsCase.Version,
                "user:operator"),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerResultInvalid, result.Error);
        Assert.Empty(dataRightsCase.SelectedSubjects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_owner_retry_is_explicit_for_result_or_exception(
        bool throws)
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DataRightsSubjectCoordinate requested = new(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            4);
        StubContributor contributor = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]),
            _ => throws
                ? throw new InvalidOperationException("owner unavailable")
                : DataRightsSubjectSelectionValidation.RetryRequired());
        SelectDataRightsSubjectCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new CaseRepository(dataRightsCase)),
            [contributor],
            new TestScopeContext(),
            new TestClock(),
            NullLogger<SelectDataRightsSubjectCommandHandler>.Instance);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new SelectDataRightsSubjectCommand(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                requested,
                dataRightsCase.Version,
                "user:operator"),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerRetryRequired, result.Error);
        Assert.Empty(dataRightsCase.SelectedSubjects);
    }

    [Fact]
    public async Task Selection_owner_failure_log_excludes_coordinate_and_exception_details()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        DataRightsSubjectCoordinate requested = new(
            "guests",
            "guest-profile",
            Guid.NewGuid(),
            4);
        StubContributor contributor = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]),
            _ => throw new InvalidOperationException("private failure detail"));
        CapturingLogger<SelectDataRightsSubjectCommandHandler> logger = new();
        SelectDataRightsSubjectCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new CaseRepository(dataRightsCase)),
            [contributor],
            new TestScopeContext(),
            new TestClock(),
            logger);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new SelectDataRightsSubjectCommand(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                requested,
                dataRightsCase.Version,
                "user:operator"),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerRetryRequired, result.Error);
        string message = Assert.Single(logger.Messages);
        Assert.Contains("guests", message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", message, StringComparison.Ordinal);
        Assert.DoesNotContain(requested.RecordId.ToString("D"), message, StringComparison.Ordinal);
        Assert.DoesNotContain("private failure detail", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_selection_is_rejected_before_owner_dispatch()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        StubContributor contributor = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]),
            _ => DataRightsSubjectSelectionValidation.NotFound());
        SelectDataRightsSubjectCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new CaseRepository(dataRightsCase)),
            [contributor],
            new TestScopeContext(),
            new TestClock(),
            NullLogger<SelectDataRightsSubjectCommandHandler>.Instance);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new SelectDataRightsSubjectCommand(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id,
                new DataRightsSubjectCoordinate(
                    "guests",
                    " ",
                    Guid.NewGuid(),
                    1),
                dataRightsCase.Version,
                "user:operator"),
            CancellationToken.None);

        Assert.Equal(DataRightsApplicationErrors.SubjectCoordinateInvalid, result.Error);
        Assert.Equal(0, contributor.SelectionInvocationCount);
        Assert.Empty(dataRightsCase.SelectedSubjects);
    }

    [Fact]
    public async Task Selected_subjects_can_be_resumed_without_exposing_previews()
    {
        Guid propertyId = Guid.NewGuid();
        DataRightsCase dataRightsCase = CreateDiscoveryCase(propertyId);
        Guid laterId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid earlierId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            laterId,
            5,
            dataRightsCase.Version,
            "user:operator",
            dataRightsCase.LastChangedAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "guests",
            "guest-profile",
            earlierId,
            3,
            dataRightsCase.Version,
            "user:operator",
            dataRightsCase.LastChangedAtUtc.AddMinutes(1)).IsSuccess);
        GetDataRightsSelectedSubjectsQueryHandler handler =
            new(new CaseRepository(dataRightsCase));

        Result<DataRightsSelectedSubjectsResponse> result = await handler.HandleAsync(
            new GetDataRightsSelectedSubjectsQuery(
                DataRightsCaseScope.ForProperty(propertyId),
                dataRightsCase.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(dataRightsCase.Version, result.Value.CaseVersion);
        Assert.Equal(
            [earlierId, laterId],
            result.Value.Subjects.Select(subject => subject.RecordId));
        Assert.All(result.Value.Subjects, subject =>
        {
            Assert.Equal("guests", subject.OwnerKey);
            Assert.Equal("guest-profile", subject.RecordType);
        });
    }

    [Fact]
    public void Duplicate_owner_registrations_fail_closed()
    {
        StubContributor first = new(
            "guests",
            _ => DataRightsSubjectDiscoveryResult.Success([]));
        StubContributor duplicate = new(
            " GUESTS ",
            _ => DataRightsSubjectDiscoveryResult.Success([]));

        Result<IReadOnlyCollection<IDataRightsSubjectDiscoveryContributor>> result =
            DataRightsSubjectContributorSet.Order(
                [first, duplicate],
                DataRightsCaseType.GuestRights);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerCatalogInvalid, result.Error);
    }

    [Fact]
    public void Explicit_owner_that_does_not_support_the_case_type_fails_closed()
    {
        StubContributor contributor = new(
            "staff",
            _ => DataRightsSubjectDiscoveryResult.Success([]),
            supportedCaseTypes: [DataRightsCaseType.StaffRights]);

        Result<IDataRightsSubjectDiscoveryContributor> result =
            DataRightsSubjectContributorSet.Find(
                [contributor],
                "staff",
                DataRightsCaseType.GuestRights);

        Assert.Equal(DataRightsApplicationErrors.SubjectOwnerUnavailable, result.Error);
    }

    [Fact]
    public void Invalid_supported_case_type_metadata_fails_closed()
    {
        IReadOnlyCollection<DataRightsCaseType>[] invalidSupportedCaseTypes =
        [
            [],
            [DataRightsCaseType.Unknown],
            [DataRightsCaseType.StaffRights, DataRightsCaseType.StaffRights]
        ];

        Assert.All(invalidSupportedCaseTypes, supportedCaseTypes =>
        {
            StubContributor contributor = new(
                "staff",
                _ => DataRightsSubjectDiscoveryResult.Success([]),
                supportedCaseTypes: supportedCaseTypes);

            Result<IReadOnlyCollection<IDataRightsSubjectDiscoveryContributor>> result =
                DataRightsSubjectContributorSet.Order(
                    [contributor],
                    DataRightsCaseType.StaffRights);

            Assert.Equal(
                DataRightsApplicationErrors.SubjectOwnerCatalogInvalid,
                result.Error);
        });
    }

    private static DataRightsSubjectCandidate Candidate(string owner, Guid recordId) => new(
        new DataRightsSubjectCoordinate(owner, "guest-profile", recordId, 1),
        $"{owner} guest",
        null,
        null);

    private static DataRightsCase CreateDiscoveryCase(Guid propertyId)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator",
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:operator",
            dataRightsCase.CreatedAtUtc.AddMinutes(1)).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsCase CreateStaffDiscoveryCase()
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator",
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:operator",
            dataRightsCase.CreatedAtUtc.AddMinutes(1)).IsSuccess);
        return dataRightsCase;
    }

    private sealed class CaseRepository(DataRightsCase dataRightsCase)
        : IDataRightsCaseRepository
    {
        public Task AddAsync(
            DataRightsCase added,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken) => Task.FromResult(
            dataRightsCase.PropertyId == scope.PropertyId &&
            dataRightsCase.Kind == (DataRightsCaseKind)scope.CaseType &&
            dataRightsCase.Id == caseId
                ? dataRightsCase
                : null);

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubContributor(
        string ownerKey,
        Func<DataRightsSubjectDiscoveryRequest, DataRightsSubjectDiscoveryResult> discover,
        Func<DataRightsSubjectSelectionRequest, DataRightsSubjectSelectionValidation>? validate = null,
        IReadOnlyCollection<DataRightsCaseType>? supportedCaseTypes = null,
        Action<CancellationToken>? afterDiscover = null,
        Action<CancellationToken>? afterValidation = null)
        : IDataRightsSubjectDiscoveryContributor
    {
        public string OwnerKey => ownerKey;

        public IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes =>
            supportedCaseTypes ?? [DataRightsCaseType.GuestRights];

        public int DiscoveryInvocationCount { get; private set; }

        public int SelectionInvocationCount { get; private set; }

        public DataRightsSubjectDiscoveryRequest? LastDiscoveryRequest { get; private set; }

        public Task<DataRightsSubjectDiscoveryResult> DiscoverAsync(
            DataRightsSubjectDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            this.DiscoveryInvocationCount++;
            this.LastDiscoveryRequest = request;
            DataRightsSubjectDiscoveryResult result = discover(request);
            afterDiscover?.Invoke(cancellationToken);
            return Task.FromResult(result);
        }

        public Task<DataRightsSubjectSelectionValidation> ValidateSelectionAsync(
            DataRightsSubjectSelectionRequest request,
            CancellationToken cancellationToken)
        {
            this.SelectionInvocationCount++;
            DataRightsSubjectSelectionValidation result = validate is null
                ? DataRightsSubjectSelectionValidation.NotFound()
                : validate(request);
            afterValidation?.Invoke(cancellationToken);
            return Task.FromResult(result);
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow =>
            new(2026, 7, 23, 12, 2, 0, TimeSpan.Zero);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            this.Messages.Add(formatter(state, exception));
    }
}

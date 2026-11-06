namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsRequiredCompanionExpansionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Review_expands_transitive_companions_as_one_case_change()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsSubjectCoordinate workspace =
            Coordinate("workspaces", "staff-access-process", 3);
        DataRightsSubjectCoordinate audit =
            Coordinate("audit", "staff-attribution", 2);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(DataRightsCaseOperation.Anonymisation, staff);
        long expectedVersion = dataRightsCase.Version;
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [
                Owner(staff),
                Owner(workspace),
                Owner(audit)
            ],
            [
                Companion(
                    "workspace-correlation",
                    staff,
                    [workspace]),
                Companion(
                    "audit-attribution",
                    workspace,
                    [audit])
            ]);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                expectedVersion,
                "user:reviewer"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DataRightsCaseStatus.ReviewRequired, result.Value.Status);
        Assert.Equal(3, result.Value.SelectedSubjectCount);
        Assert.Equal(expectedVersion + 1, result.Value.Version);
        Assert.Equal(
            [audit.RecordId, staff.RecordId, workspace.RecordId],
            dataRightsCase.SelectedSubjects
                .OrderBy(subject => subject.OwnerKey, StringComparer.Ordinal)
                .Select(subject => subject.RecordId));
    }

    [Fact]
    public async Task Cyclic_companion_graph_is_deduplicated()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsSubjectCoordinate workspace =
            Coordinate("workspaces", "staff-access-process", 3);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(DataRightsCaseOperation.Anonymisation, staff);
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [Owner(staff), Owner(workspace)],
            [
                Companion("to-workspaces", staff, [workspace]),
                Companion("back-to-staff", workspace, [staff])
            ]);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                dataRightsCase.Version,
                "user:reviewer"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.SelectedSubjectCount);
        Assert.Equal(2, dataRightsCase.SelectedSubjects.Count);
    }

    [Fact]
    public async Task Access_export_expands_only_access_export_companions()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsSubjectCoordinate audit =
            Coordinate("audit", "staff-export-history", 2);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(
                DataRightsCaseOperation.AccessExport,
                staff);
        StubCompanionContributor accessCompanion =
            Companion(
                "audit-export-history",
                staff,
                [audit],
                DataRightsOperation.AccessExport);
        StubCompanionContributor anonymisationCompanion =
            Companion(
                "audit-anonymisation-history",
                staff,
                [],
                DataRightsOperation.Anonymisation);
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [Owner(staff), Owner(audit)],
            [accessCompanion, anonymisationCompanion]);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                dataRightsCase.Version,
                "user:reviewer"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.SelectedSubjectCount);
        Assert.Equal(1, accessCompanion.InvocationCount);
        Assert.Equal(0, anonymisationCompanion.InvocationCount);
    }

    [Fact]
    public async Task Blocked_companion_leaves_case_in_discovery()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(DataRightsCaseOperation.Anonymisation, staff);
        long expectedVersion = dataRightsCase.Version;
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [Owner(staff)],
            [
                new StubCompanionContributor(
                    "workspace-correlation",
                    staff.OwnerKey,
                    staff.RecordType,
                    DataRightsOperation.Anonymisation,
                    _ => DataRightsRequiredCompanionResult.Blocked(
                        "Workspaces.ActiveAccessProcess"))
            ]);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                expectedVersion,
                "user:reviewer"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RequiredCompanionBlocked,
            result.Error);
        Assert.Equal(DataRightsCaseState.Discovery, dataRightsCase.Status);
        Assert.Equal(expectedVersion, dataRightsCase.Version);
        Assert.Single(dataRightsCase.SelectedSubjects);
    }

    [Fact]
    public async Task Subject_owner_retry_during_closure_remains_an_explicit_retry()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(DataRightsCaseOperation.Anonymisation, staff);
        long expectedVersion = dataRightsCase.Version;
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [
                new StubDiscoveryContributor(
                    staff.OwnerKey,
                    staff,
                    DataRightsSubjectSelectionValidation.RetryRequired())
            ],
            []);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                expectedVersion,
                "user:reviewer"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RequiredCompanionRetryRequired,
            result.Error);
        Assert.Equal(DataRightsCaseState.Discovery, dataRightsCase.Status);
        Assert.Equal(expectedVersion, dataRightsCase.Version);
    }

    [Fact]
    public async Task Normal_companion_return_after_cancellation_stops_the_closure()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(DataRightsCaseOperation.Anonymisation, staff);
        long expectedVersion = dataRightsCase.Version;
        using CancellationTokenSource source = new();
        StubCompanionContributor canceling = new(
            "a-canceling",
            staff.OwnerKey,
            staff.RecordType,
            DataRightsOperation.Anonymisation,
            _ =>
            {
                source.Cancel();
                return DataRightsRequiredCompanionResult.Completed([]);
            });
        StubCompanionContributor later = Companion(
            "b-later",
            staff,
            []);
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [Owner(staff)],
            [later, canceling]);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new(
                    DataRightsCaseScope.Staff,
                    dataRightsCase.Id,
                    expectedVersion,
                    "user:reviewer"),
                source.Token));

        Assert.Equal(1, canceling.InvocationCount);
        Assert.Equal(0, later.InvocationCount);
        Assert.Equal(DataRightsCaseState.Discovery, dataRightsCase.Status);
        Assert.Equal(expectedVersion, dataRightsCase.Version);
    }

    [Fact]
    public async Task Contradictory_subject_owner_retry_during_closure_is_invalid()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(DataRightsCaseOperation.Anonymisation, staff);
        long expectedVersion = dataRightsCase.Version;
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [
                new StubDiscoveryContributor(
                    staff.OwnerKey,
                    staff,
                    new DataRightsSubjectSelectionValidation(
                        DataRightsSubjectSelectionValidationStatus.RetryRequired,
                        staff))
            ],
            []);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                expectedVersion,
                "user:reviewer"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RequiredCompanionResultInvalid,
            result.Error);
        Assert.Equal(DataRightsCaseState.Discovery, dataRightsCase.Status);
        Assert.Equal(expectedVersion, dataRightsCase.Version);
    }

    [Fact]
    public async Task Invalid_subject_owner_catalog_during_closure_is_internal_failure()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(DataRightsCaseOperation.Anonymisation, staff);
        long expectedVersion = dataRightsCase.Version;
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [Owner(staff), Owner(staff)],
            []);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                expectedVersion,
                "user:reviewer"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RequiredCompanionResultInvalid,
            result.Error);
        Assert.Equal(DataRightsCaseState.Discovery, dataRightsCase.Status);
        Assert.Equal(expectedVersion, dataRightsCase.Version);
    }

    [Fact]
    public void Invalid_late_companion_does_not_partially_mutate_case()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsSubjectCoordinate workspace =
            Coordinate("workspaces", "staff-access-process", 3);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(
                DataRightsCaseOperation.Anonymisation,
                staff);
        long expectedVersion = dataRightsCase.Version;

        Result result = dataRightsCase.RequireReview(
            [
                new(
                    workspace.OwnerKey,
                    workspace.RecordType,
                    workspace.RecordId,
                    workspace.RecordVersion),
                new(
                    "zz-invalid",
                    "staff-proof",
                    Guid.Empty,
                    1)
            ],
            expectedVersion,
            "user:reviewer",
            Now.AddMinutes(3));

        Assert.True(result.IsFailure);
        Assert.Equal(DataRightsCaseState.Discovery, dataRightsCase.Status);
        Assert.Equal(expectedVersion, dataRightsCase.Version);
        Assert.Single(dataRightsCase.SelectedSubjects);
        Assert.DoesNotContain(
            dataRightsCase.SelectedSubjects,
            subject => subject.RecordId == workspace.RecordId);
    }

    [Theory]
    [InlineData(DataRightsCaseOperation.Correction)]
    [InlineData(DataRightsCaseOperation.Restriction)]
    public async Task Single_coordinate_operations_do_not_expand_companions(
        DataRightsCaseOperation operation)
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(operation, staff);
        StubCompanionContributor companion =
            Companion(
                "unexpected",
                staff,
                [Coordinate(
                    "workspaces",
                    "staff-access-process",
                    3)]);
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [],
            [companion]);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                dataRightsCase.Version,
                "user:reviewer"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.SelectedSubjectCount);
        Assert.Equal(0, companion.InvocationCount);
    }

    [Fact]
    public async Task Duplicate_contributor_keys_fail_closed_before_invocation()
    {
        DataRightsSubjectCoordinate staff =
            Coordinate("staff", "staff-member", 5);
        DataRightsCase dataRightsCase =
            CreateDiscoveryCase(DataRightsCaseOperation.Anonymisation, staff);
        StubCompanionContributor first =
            Companion("duplicate", staff, []);
        StubCompanionContributor second =
            Companion(" DUPLICATE ", staff, []);
        RequireDataRightsReviewCommandHandler handler = Handler(
            dataRightsCase,
            [Owner(staff)],
            [first, second]);

        Result<DataRightsCaseDto> result = await handler.HandleAsync(
            new(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                dataRightsCase.Version,
                "user:reviewer"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RequiredCompanionUnavailable,
            result.Error);
        Assert.Equal(0, first.InvocationCount);
        Assert.Equal(0, second.InvocationCount);
    }

    private static RequireDataRightsReviewCommandHandler Handler(
        DataRightsCase dataRightsCase,
        IReadOnlyCollection<IDataRightsSubjectDiscoveryContributor> owners,
        IReadOnlyCollection<IDataRightsRequiredCompanionContributor>
            companions) =>
        new(
            DataRightsMutationTestSupport.Case(
                new CaseRepository(dataRightsCase)),
            new DataRightsRequiredCompanionExpander(
                companions,
                owners,
                NullLogger<
                    DataRightsRequiredCompanionExpander>.Instance),
            [],
            new TestClock(),
            NullLogger<RequireDataRightsReviewCommandHandler>.Instance);

    private static DataRightsCase CreateDiscoveryCase(
        DataRightsCaseOperation operation,
        DataRightsSubjectCoordinate selected)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            operation,
            DataRightsRequesterRelation.ControllerInitiated,
            operation == DataRightsCaseOperation.Restriction
                ? DataRightsRestrictionAction.Apply
                : DataRightsRestrictionAction.None).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:operator",
            Now).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:operator",
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            selected.OwnerKey,
            selected.RecordType,
            selected.RecordId,
            selected.RecordVersion,
            dataRightsCase.Version,
            "user:operator",
            Now.AddMinutes(2)).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsSubjectCoordinate Coordinate(
        string owner,
        string recordType,
        long version) =>
        new(owner, recordType, Guid.NewGuid(), version);

    private static StubDiscoveryContributor Owner(
        DataRightsSubjectCoordinate coordinate) =>
        new(coordinate.OwnerKey, coordinate);

    private static StubCompanionContributor Companion(
        string key,
        DataRightsSubjectCoordinate source,
        IReadOnlyCollection<DataRightsSubjectCoordinate> companions,
        DataRightsOperation operation =
            DataRightsOperation.Anonymisation) =>
        new(
            key,
            source.OwnerKey,
            source.RecordType,
            operation,
            _ =>
                DataRightsRequiredCompanionResult.Completed(
                    companions));

    private sealed class CaseRepository(DataRightsCase dataRightsCase)
        : IDataRightsCaseRepository
    {
        public Task AddAsync(
            DataRightsCase added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                dataRightsCase.Id == caseId &&
                dataRightsCase.Kind ==
                    (DataRightsCaseKind)scope.CaseType &&
                dataRightsCase.PropertyId == scope.PropertyId
                    ? dataRightsCase
                    : null);

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubDiscoveryContributor(
        string ownerKey,
        DataRightsSubjectCoordinate expected,
        DataRightsSubjectSelectionValidation? validation = null)
        : IDataRightsSubjectDiscoveryContributor
    {
        public string OwnerKey => ownerKey;

        public IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes =>
            [DataRightsCaseType.StaffRights];

        public Task<DataRightsSubjectDiscoveryResult> DiscoverAsync(
            DataRightsSubjectDiscoveryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsSubjectSelectionValidation>
            ValidateSelectionAsync(
                DataRightsSubjectSelectionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                validation ?? (request.Coordinate == expected
                    ? DataRightsSubjectSelectionValidation.Valid(
                        expected)
                    : DataRightsSubjectSelectionValidation.NotFound()));
    }

    private sealed class StubCompanionContributor(
        string contributorKey,
        string sourceOwnerKey,
        string sourceRecordType,
        DataRightsOperation operation,
        Func<
            DataRightsRequiredCompanionRequest,
            DataRightsRequiredCompanionResult> expand)
        : IDataRightsRequiredCompanionContributor
    {
        public string ContributorKey => contributorKey;
        public string SourceOwnerKey => sourceOwnerKey;
        public string SourceRecordType => sourceRecordType;
        public DataRightsCaseType CaseType =>
            DataRightsCaseType.StaffRights;
        public DataRightsOperation Operation => operation;
        public int ContractVersion =>
            DataRightsRequiredCompanionContract.CurrentVersion;
        public int InvocationCount { get; private set; }

        public Task<DataRightsRequiredCompanionResult> ExpandAsync(
            DataRightsRequiredCompanionRequest request,
            CancellationToken cancellationToken)
        {
            this.InvocationCount++;
            return Task.FromResult(expand(request));
        }
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now.AddMinutes(3);
    }
}

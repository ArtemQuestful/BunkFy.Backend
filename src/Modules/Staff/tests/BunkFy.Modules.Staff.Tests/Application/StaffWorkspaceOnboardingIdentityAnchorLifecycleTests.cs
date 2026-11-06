namespace BunkFy.Modules.Staff.Tests;

using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Handlers;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using Gma.Framework.Messaging;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffWorkspaceOnboardingIdentityAnchorLifecycleTests
{
    private static readonly DateTimeOffset AnchoredAtUtc =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Read_is_ordered_and_never_echoes_expected_subjects()
    {
        Guid exactId = Guid.NewGuid();
        Guid mismatchId = Guid.NewGuid();
        Guid missingId = Guid.NewGuid();
        Guid absentId = Guid.NewGuid();
        Guid exactMemberId = Guid.NewGuid();
        Guid mismatchMemberId = Guid.NewGuid();
        Guid missingMemberId = Guid.NewGuid();
        FakeAnchors anchors = new(
            Anchor(exactId, exactMemberId),
            Anchor(mismatchId, mismatchMemberId),
            Anchor(missingId, missingMemberId));
        FakeResolutions resolutions = new();
        FakeMembers members = new(
            new(exactMemberId, "subject:exact", StaffMemberState.Active),
            new(mismatchMemberId, "subject:other", StaffMemberState.Suspended),
            new(missingMemberId, null, StaffMemberState.Anonymised));
        StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator coordinator =
            new(anchors, resolutions, members, new RecordingCreationLock());

        Result<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> result =
            await coordinator.ReadAsync(
                [
                    new(mismatchId, "subject:expected"),
                    new(absentId, "subject:absent"),
                    new(exactId, " subject:exact "),
                    new(missingId, "subject:erased")
                ],
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            [mismatchId, absentId, exactId, missingId],
            result.Value.Select(outcome => outcome.ApplicationId));
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Mismatch,
            result.Value[0].SubjectMatch);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent,
            result.Value[1].Status);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
            result.Value[2].SubjectMatch);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Missing,
            result.Value[3].SubjectMatch);
        string serialized = System.Text.Json.JsonSerializer.Serialize(
            result.Value);
        Assert.DoesNotContain("subject:", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_reports_resolution_disposition_and_application_version()
    {
        Guid applicationId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = NewResolutionEventId(applicationId);
        FakeAnchors anchors = new(Anchor(
            applicationId,
            staffMemberId,
            resolutionEventId));
        FakeResolutions resolutions = new(
            Resolution(
                applicationId,
                staffMemberId,
                resolutionEventId,
                workspaceApplicationVersion: 17));
        StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator coordinator =
            new(
                anchors,
                resolutions,
                new FakeMembers(new StaffMemberSafetyEvidence(
                    staffMemberId,
                    "subject:staff",
                    StaffMemberState.Departed)),
                new RecordingCreationLock());

        Result<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> result =
            await coordinator.ReadAsync(
                [new(applicationId, "subject:staff")],
                CancellationToken.None);

        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome =
            Assert.Single(result.Value);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved,
            outcome.Status);
        Assert.Equal(17, outcome.WorkspaceApplicationVersion);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            outcome.ResolutionDisposition);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Departed,
            outcome.TargetLifecycle);
        Assert.Equal(resolutionEventId, outcome.ResolutionEventId);
    }

    [Fact]
    public async Task Read_reports_a_resolution_event_mismatching_its_anchor_as_corrupt()
    {
        Guid applicationId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Guid anchorResolutionEventId = NewResolutionEventId(applicationId);
        Guid mismatchedResolutionEventId = NewDistinctId(
            applicationId,
            anchorResolutionEventId);
        FakeAnchors anchors = new(Anchor(
            applicationId,
            staffMemberId,
            anchorResolutionEventId));
        FakeResolutions resolutions = new(
            Resolution(
                applicationId,
                staffMemberId,
                mismatchedResolutionEventId));
        StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator coordinator =
            new(
                anchors,
                resolutions,
                new FakeMembers(new StaffMemberSafetyEvidence(
                    staffMemberId,
                    "subject:staff",
                    StaffMemberState.Active)),
                new RecordingCreationLock());

        Result<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> result =
            await coordinator.ReadAsync(
                [new(applicationId, "subject:staff")],
                CancellationToken.None);

        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Corrupt,
            Assert.Single(result.Value).Status);
    }

    [Fact]
    public async Task Record_cannot_manufacture_or_retarget_an_anchor()
    {
        Guid applicationId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = NewResolutionEventId(applicationId);
        FakeAnchors anchors = new();
        FakeResolutions resolutions = new();
        RecordingCreationLock creationLock = new();
        StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator coordinator =
            new(anchors, resolutions, new FakeMembers(), creationLock);
        StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request =
            Request(applicationId, staffMemberId, resolutionEventId);

        Result<StaffWorkspaceOnboardingIdentityAnchorResolutionStatus> absent =
            await coordinator.RecordAsync(
                "tenant-a",
                request,
                CancellationToken.None);
        anchors.Records.Add(Anchor(
            applicationId,
            Guid.NewGuid(),
            resolutionEventId));
        Result<StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>
            retargeted = await coordinator.RecordAsync(
                "tenant-a",
                request,
                CancellationToken.None);

        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.AnchorAbsent,
            absent.Value);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.Conflict,
            retargeted.Value);
        Assert.Empty(resolutions.Records);
        Assert.Equal([applicationId, applicationId], creationLock.SourceIds);
    }

    [Fact]
    public async Task Record_is_microsecond_exact_idempotent_and_causally_timed()
    {
        Guid applicationId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = NewResolutionEventId(applicationId);
        FakeAnchors anchors = new(Anchor(
            applicationId,
            staffMemberId,
            resolutionEventId));
        FakeResolutions resolutions = new();
        StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator coordinator =
            new(
                anchors,
                resolutions,
                new FakeMembers(),
                new RecordingCreationLock());
        StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request =
            Request(
                applicationId,
                staffMemberId,
                resolutionEventId,
                AnchoredAtUtc.AddTicks(-7));

        Result<StaffWorkspaceOnboardingIdentityAnchorResolutionStatus> first =
            await coordinator.RecordAsync(
                "tenant-a",
                request,
                CancellationToken.None);
        Result<StaffWorkspaceOnboardingIdentityAnchorResolutionStatus> replay =
            await coordinator.RecordAsync(
                "tenant-a",
                request,
                CancellationToken.None);
        Result<StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>
            divergentTime =
            await coordinator.RecordAsync(
                "tenant-a",
                request with
                {
                    ResolvedAtUtc = AnchoredAtUtc.AddMinutes(1)
                },
                CancellationToken.None);

        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.Recorded,
            first.Value);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
                .AlreadyRecorded,
            replay.Value);
        Assert.Equal(
            AnchoredAtUtc,
            Assert.Single(resolutions.Records).ResolvedAtUtc);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.Conflict,
            divergentTime.Value);
    }

    [Fact]
    public async Task Read_rejects_invalid_or_duplicate_coordinates_before_storage()
    {
        FakeAnchors anchors = new();
        FakeResolutions resolutions = new();
        StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator coordinator =
            new(
                anchors,
                resolutions,
                new FakeMembers(),
                new RecordingCreationLock());
        Guid applicationId = Guid.NewGuid();

        Result<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> result =
            await coordinator.ReadAsync(
                [
                    new(applicationId, "subject:staff"),
                    new(applicationId, "subject:other")
                ],
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorLifecycleRequestInvalid,
            result.Error);
        Assert.Equal(0, anchors.ListCount);
        Assert.Equal(0, resolutions.ListCount);
    }

    [Fact]
    public async Task Record_requires_the_resolution_event_id_owned_by_the_anchor()
    {
        Guid applicationId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Guid anchorResolutionEventId = NewResolutionEventId(applicationId);
        Guid mismatchedResolutionEventId = NewDistinctId(
            applicationId,
            anchorResolutionEventId);
        FakeResolutions resolutions = new();
        RecordingCreationLock creationLock = new();
        StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator coordinator =
            new(
                new FakeAnchors(Anchor(
                    applicationId,
                    staffMemberId,
                    anchorResolutionEventId)),
                resolutions,
                new FakeMembers(),
                creationLock);

        Result<StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>
            reusedSource =
            await coordinator.RecordAsync(
                "tenant-a",
                Request(
                    applicationId,
                    staffMemberId,
                    applicationId),
                CancellationToken.None);
        Result<StaffWorkspaceOnboardingIdentityAnchorResolutionStatus>
            mismatchedEvent =
            await coordinator.RecordAsync(
                "tenant-a",
                Request(
                    applicationId,
                    staffMemberId,
                    mismatchedResolutionEventId),
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorLifecycleRequestInvalid,
            reusedSource.Error);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.Conflict,
            mismatchedEvent.Value);
        Assert.Equal([applicationId], creationLock.SourceIds);
        Assert.Empty(resolutions.Records);
    }

    [Fact]
    public async Task Writer_persists_distinct_random_coordinates_when_historical_xor_domains_collide()
    {
        Guid firstApplicationId =
            Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid historicalCollision = HistoricalXor(
            firstApplicationId,
            HistoricalContinuationMask);
        Guid secondApplicationId = HistoricalXor(
            historicalCollision,
            HistoricalResolutionMask);
        Assert.Equal(
            historicalCollision,
            HistoricalXor(
                secondApplicationId,
                HistoricalResolutionMask));

        Guid firstResolutionEventId = NewDistinctId(
            firstApplicationId,
            secondApplicationId);
        Guid secondResolutionEventId = NewDistinctId(
            secondApplicationId,
            firstApplicationId,
            firstResolutionEventId);
        FakeAnchors anchors = new();
        RecordingOutbox outbox = new();
        SequenceIdGenerator ids = new(
            firstResolutionEventId,
            secondResolutionEventId);
        StaffIdentityProvisioningAnchorWriter writer = new(
            anchors,
            new RecordingOutboxRegistry(outbox),
            ids);

        StaffIdentityProvisioningAnchorRecord first =
            await writer.AddAsync(
                UnownedAnchor(firstApplicationId, Guid.NewGuid()),
                CancellationToken.None);
        StaffIdentityProvisioningAnchorRecord second =
            await writer.AddAsync(
                UnownedAnchor(secondApplicationId, Guid.NewGuid()),
                CancellationToken.None);

        Assert.Equal(firstResolutionEventId, first.ResolutionEventId);
        Assert.Equal(secondResolutionEventId, second.ResolutionEventId);
        Assert.NotEqual(first.ResolutionEventId, second.ResolutionEventId);
        Assert.Equal(
            [firstResolutionEventId, secondResolutionEventId],
            anchors.Records.Select(anchor =>
                anchor.ResolutionEventId!.Value));
        Assert.Equal(
            [firstResolutionEventId, secondResolutionEventId],
            outbox.Events
                .Cast<StaffIdentityProvisioningAnchorCreatedIntegrationEvent>()
                .Select(integrationEvent =>
                    integrationEvent.ResolutionEventId));
        Assert.Equal(2, ids.CallCount);
    }

    [Fact]
    public async Task Writer_rejects_a_caller_supplied_resolution_coordinate()
    {
        Guid applicationId = Guid.NewGuid();
        FakeAnchors anchors = new();
        RecordingOutbox outbox = new();
        SequenceIdGenerator ids = new(NewResolutionEventId(applicationId));
        StaffIdentityProvisioningAnchorWriter writer = new(
            anchors,
            new RecordingOutboxRegistry(outbox),
            ids);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.AddAsync(
                Anchor(
                    applicationId,
                    Guid.NewGuid(),
                    NewResolutionEventId(applicationId)),
                CancellationToken.None));

        Assert.Empty(anchors.Records);
        Assert.Empty(outbox.Events);
        Assert.Equal(0, ids.CallCount);
    }

    private static StaffIdentityProvisioningAnchorRecord Anchor(
        Guid applicationId,
        Guid staffMemberId,
        Guid? resolutionEventId = null) =>
        new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
            applicationId,
            staffMemberId,
            AnchoredAtUtc,
            resolutionEventId ?? NewResolutionEventId(applicationId));

    private static StaffIdentityProvisioningAnchorRecord UnownedAnchor(
        Guid applicationId,
        Guid staffMemberId) =>
        new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
            applicationId,
            staffMemberId,
            AnchoredAtUtc);

    private static StaffIdentityProvisioningAnchorResolutionRecord Resolution(
        Guid applicationId,
        Guid staffMemberId,
        Guid resolutionEventId,
        long workspaceApplicationVersion = 9) =>
        new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
            applicationId,
            staffMemberId,
            workspaceApplicationVersion,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            resolutionEventId,
            AnchoredAtUtc.AddMinutes(1));

    private static StaffWorkspaceOnboardingIdentityAnchorResolutionRequest
        Request(
            Guid applicationId,
            Guid staffMemberId,
            Guid resolutionEventId,
            DateTimeOffset? resolvedAtUtc = null) =>
        new(
            resolutionEventId,
            applicationId,
            staffMemberId,
            9,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            resolvedAtUtc ?? AnchoredAtUtc.AddMinutes(1));

    private static Guid NewResolutionEventId(Guid applicationId) =>
        NewDistinctId(applicationId);

    private static Guid NewDistinctId(params Guid[] excluded)
    {
        Guid candidate;
        do
        {
            candidate = Guid.NewGuid();
        }
        while (candidate == Guid.Empty || excluded.Contains(candidate));

        return candidate;
    }

    private static Guid HistoricalXor(Guid sourceId, Guid domainMask)
    {
        byte[] sourceBytes = sourceId.ToByteArray(bigEndian: true);
        byte[] maskBytes = domainMask.ToByteArray(bigEndian: true);
        for (int index = 0; index < sourceBytes.Length; index++)
        {
            sourceBytes[index] ^= maskBytes[index];
        }

        return new Guid(sourceBytes, bigEndian: true);
    }

    private sealed class FakeAnchors(
        params StaffIdentityProvisioningAnchorRecord[] records)
        : IStaffIdentityProvisioningAnchorRepository
    {
        public List<StaffIdentityProvisioningAnchorRecord> Records { get; } =
            [.. records];
        public int ListCount { get; private set; }

        public Task<IReadOnlyList<StaffIdentityProvisioningAnchorRecord>>
            ListAsync(
                IReadOnlyList<StaffIdentityProvisioningSourceKey> sources,
                CancellationToken cancellationToken)
        {
            this.ListCount++;
            HashSet<(StaffIdentityProvisioningSourceKind, Guid)> keys =
                sources.Select(source =>
                    (source.SourceKind, source.SourceId)).ToHashSet();
            return Task.FromResult<IReadOnlyList<
                StaffIdentityProvisioningAnchorRecord>>(this.Records
                    .Where(record => keys.Contains(
                        (record.SourceKind, record.SourceId)))
                    .ToArray());
        }

        public Task<StaffIdentityProvisioningAnchorRecord?> GetAsync(
            StaffIdentityProvisioningSourceKind sourceKind,
            Guid sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Records.SingleOrDefault(record =>
                record.SourceKind == sourceKind &&
                record.SourceId == sourceId));

        public Task<bool> HasWorkspaceOnboardingAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) => Task.FromResult(
            this.Records.Any(record =>
                record.SourceKind ==
                    StaffIdentityProvisioningSourceKind.WorkspaceOnboarding &&
                record.StaffMemberId == staffMemberId));

        public Task AddAsync(
            StaffIdentityProvisioningAnchorRecord anchor,
            CancellationToken cancellationToken)
        {
            this.Records.Add(anchor);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResolutions(
        params StaffIdentityProvisioningAnchorResolutionRecord[] records)
        : IStaffIdentityProvisioningAnchorResolutionRepository
    {
        public List<StaffIdentityProvisioningAnchorResolutionRecord> Records
        {
            get;
        } = [.. records];
        public int ListCount { get; private set; }

        public Task<IReadOnlyList<
            StaffIdentityProvisioningAnchorResolutionRecord>> ListAsync(
                IReadOnlyList<Guid> sourceIds,
                CancellationToken cancellationToken)
        {
            this.ListCount++;
            return Task.FromResult<IReadOnlyList<
                StaffIdentityProvisioningAnchorResolutionRecord>>(this.Records
                    .Where(record => sourceIds.Contains(record.SourceId))
                    .ToArray());
        }

        public Task<StaffIdentityProvisioningAnchorResolutionRecord?> GetAsync(
            Guid sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Records.SingleOrDefault(record =>
                record.SourceId == sourceId));

        public Task<bool> HasUnresolvedWorkspaceOnboardingAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            StaffIdentityProvisioningAnchorResolutionRecord resolution,
            CancellationToken cancellationToken)
        {
            this.Records.Add(resolution);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCreationLock : IStaffCreationOperationLock
    {
        public List<Guid> SourceIds { get; } = [];

        public Task AcquireAsync(
            string tenantId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            this.SourceIds.Add(operationId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public string ModuleName => StaffModuleMetadata.Name;
        public List<IIntegrationEvent> Events { get; } = [];

        public Task EnqueueAsync<TEvent>(
            TEvent integrationEvent,
            CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutboxRegistry(RecordingOutbox outbox)
        : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName)
        {
            Assert.Equal(StaffModuleMetadata.Name, moduleName);
            return outbox;
        }
    }

    private sealed class SequenceIdGenerator(params Guid[] values)
        : IIdGenerator
    {
        private readonly Queue<Guid> values = new(values);

        public int CallCount { get; private set; }

        public Guid NewId()
        {
            this.CallCount++;
            return this.values.Count > 0
                ? this.values.Dequeue()
                : throw new InvalidOperationException(
                    "No test identity coordinate remains.");
        }
    }

    private sealed class FakeMembers(params StaffMemberSafetyEvidence[] records)
        : IStaffMemberRepository
    {
        public Task<IReadOnlyList<StaffMemberSafetyEvidence>>
            ListSafetyEvidenceAsync(
                IReadOnlyList<Guid> staffMemberIds,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StaffMemberSafetyEvidence>>(
                records.Where(record =>
                    staffMemberIds.Contains(record.StaffMemberId)).ToArray());

        public Task AddAsync(
            StaffMember member,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffMember?> GetAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffMember?> GetForDataRightsAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffMember?> GetForSafetyTransitionAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffMember?> GetForSafetyTransitionByAuthSubjectAsync(
            string authSubjectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffMember?> GetByAuthSubjectAsync(
            string authSubjectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffDirectoryMemberDto?> GetDirectoryAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffDirectoryMemberDto?> GetDirectoryAtPropertyAsync(
            Guid propertyId,
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffDirectoryListResponse> ListDirectoryAsync(
            string? search,
            StaffStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffPropertyDirectoryListResponse>
            ListDirectoryAtPropertyAsync(
                Guid propertyId,
                string? search,
                StaffStatus? status,
                PageRequest pageRequest,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffAccountDirectoryResponse> ResolveAccountDirectoryAsync(
            IReadOnlyCollection<string> authSubjectIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> EmployeeNumberExistsAsync(
            string employeeNumber,
            Guid? exceptStaffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AuthSubjectExistsAsync(
            string authSubjectId,
            Guid? exceptStaffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static readonly Guid HistoricalContinuationMask =
        Guid.Parse("6c8c3277-a4b4-4cb0-b052-c51655d83cf7");
    private static readonly Guid HistoricalResolutionMask =
        Guid.Parse("d4e73767-4c8d-4e82-b2e9-6b51f48ed6a1");
}

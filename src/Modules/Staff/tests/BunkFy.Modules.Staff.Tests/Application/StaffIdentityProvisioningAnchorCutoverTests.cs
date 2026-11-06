namespace BunkFy.Modules.Staff.Tests;

using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Handlers;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.ValueObjects;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffIdentityProvisioningAnchorCutoverTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Inspect_classifies_only_exact_Workspaces_evidence_as_seedable()
    {
        Guid mappedSource = Guid.NewGuid();
        Guid ambiguousSource = Guid.NewGuid();
        Guid receiptOnlySource = Guid.NewGuid();
        StaffMember mapped = CreateMember();
        StaffMember receiptTarget = CreateMember();
        FakeOperations operations = new();
        operations.Add(Receipt(receiptOnlySource, receiptTarget.Id));
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [mapped, receiptTarget],
            operations: operations);

        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> result =
            await coordinator.InspectAsync(
                [
                    Workspace(mappedSource, mapped.Id),
                    Workspace(ambiguousSource, null),
                    Workspace(receiptOnlySource, null)
                ],
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition
                .SeedableFromWorkspace,
            Find(result.Value, mappedSource).Disposition);
        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition.Ambiguous,
            Find(result.Value, ambiguousSource).Disposition);
        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition.Conflict,
            Find(result.Value, receiptOnlySource).Disposition);
    }

    [Fact]
    public async Task Inspect_requires_the_reviewed_owner_map_to_match_the_target_subject()
    {
        StaffMember member = CreateMember();
        Guid membershipId = Guid.NewGuid();
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [member]);

        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> result =
            await coordinator.InspectAsync(
                [Owner(membershipId, member.Id, member.AuthSubjectId!)],
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition
                .SeedableFromReviewedOwnerMap,
            Assert.Single(result.Value).Disposition);
    }

    [Fact]
    public async Task Inspect_requires_exact_Workspaces_subject_and_fails_closed_after_target_erasure()
    {
        StaffMember live = CreateMember("subject:live");
        StaffMember erased = CreateMember(authSubjectId: null);
        Guid mismatchedSource = Guid.NewGuid();
        Guid erasedSource = Guid.NewGuid();
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [live, erased]);

        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> result =
            await coordinator.InspectAsync(
                [
                    Workspace(
                        mismatchedSource,
                        live.Id,
                        "subject:different"),
                    Workspace(
                        erasedSource,
                        erased.Id,
                        "subject:historical")
                ],
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.All(result.Value, item => Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition.Conflict,
            item.Disposition));
    }

    [Fact]
    public async Task Inspect_rejects_a_different_subject_and_requires_explicit_erased_target_review()
    {
        StaffMember live = CreateMember("subject:live");
        StaffMember erased = CreateMember(authSubjectId: null);
        Guid differentSubjectMembershipId = Guid.NewGuid();
        Guid unreviewedErasedMembershipId = Guid.NewGuid();
        Guid reviewedErasedMembershipId = Guid.NewGuid();
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [live, erased]);

        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> result =
            await coordinator.InspectAsync(
                [
                    Owner(
                        differentSubjectMembershipId,
                        live.Id,
                        "subject:different"),
                    Owner(
                        unreviewedErasedMembershipId,
                        erased.Id,
                        "subject:historical"),
                    Owner(
                        reviewedErasedMembershipId,
                        erased.Id,
                        "subject:historical",
                        reviewedErasedTarget: true)
                ],
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Dictionary<Guid, StaffIdentityProvisioningAnchorCutoverDisposition>
            dispositions = result.Value.ToDictionary(
                item => item.SourceId,
                item => item.Disposition);
        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition.Conflict,
            dispositions[differentSubjectMembershipId]);
        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition.Conflict,
            dispositions[unreviewedErasedMembershipId]);
        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition
                .SeedableFromReviewedOwnerMap,
            dispositions[reviewedErasedMembershipId]);
    }

    [Fact]
    public async Task Inspect_treats_an_exact_existing_anchor_as_ready_after_target_erasure()
    {
        StaffMember erased = CreateMember(authSubjectId: null);
        Guid membershipId = Guid.NewGuid();
        FakeAnchors anchors = new();
        anchors.Records.Add(new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.OrganizationMembership,
            membershipId,
            erased.Id,
            Now));
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [erased],
            anchors);

        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> result =
            await coordinator.InspectAsync(
                [new(
                    StaffIdentityProvisioningAnchorSourceKind
                        .OrganizationMembership,
                    membershipId,
                    StaffMemberId: null,
                    ExpectedAuthSubjectId: "subject:historical")],
                CancellationToken.None);

        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition.AlreadyAnchored,
            Assert.Single(result.Value).Disposition);
    }

    [Fact]
    public async Task Inspect_rejects_an_existing_owner_anchor_whose_live_target_has_a_different_subject()
    {
        StaffMember target = CreateMember("subject:other");
        Guid membershipId = Guid.NewGuid();
        FakeAnchors anchors = new();
        anchors.Records.Add(new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.OrganizationMembership,
            membershipId,
            target.Id,
            Now));
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [target],
            anchors);

        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> result =
            await coordinator.InspectAsync(
                [new(
                    StaffIdentityProvisioningAnchorSourceKind
                        .OrganizationMembership,
                    membershipId,
                    StaffMemberId: null,
                    ExpectedAuthSubjectId: "subject:expected")],
                CancellationToken.None);

        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition.Conflict,
            Assert.Single(result.Value).Disposition);
    }

    [Fact]
    public async Task Inspect_reports_missing_targets_and_conflicting_anchors()
    {
        Guid sourceId = Guid.NewGuid();
        StaffMember expected = CreateMember();
        StaffMember other = CreateMember();
        FakeAnchors anchors = new();
        anchors.Records.Add(new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
            sourceId,
            other.Id,
            Now));
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [expected, other],
            anchors: anchors);

        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> conflict =
            await coordinator.InspectAsync(
                [Workspace(sourceId, expected.Id)],
                CancellationToken.None);
        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> missing =
            await Create([]).InspectAsync(
                [Workspace(Guid.NewGuid(), Guid.NewGuid())],
                CancellationToken.None);

        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition.Conflict,
            Assert.Single(conflict.Value).Disposition);
        Assert.Equal(
            StaffIdentityProvisioningAnchorCutoverDisposition.Conflict,
            Assert.Single(missing.Value).Disposition);
    }

    [Fact]
    public async Task Apply_preflights_the_entire_batch_and_writes_nothing_on_conflict()
    {
        StaffMember member = CreateMember();
        FakeAnchors anchors = new();
        RecordingLock sourceLock = new();
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [member],
            anchors,
            creationLock: sourceLock);
        Guid good = Guid.NewGuid();
        Guid bad = Guid.NewGuid();

        Result<BunkFy.Modules.Staff.Application.Commands.
            StaffIdentityProvisioningAnchorApplySummary> result =
            await coordinator.ApplyAsync(
                "tenant-a",
                [Workspace(good, member.Id), Workspace(bad, Guid.NewGuid())],
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorCutoverBlocked,
            result.Error);
        Assert.Empty(anchors.Records);
        Assert.Equal(
            new[] { bad, good }.Order().ToArray(),
            sourceLock.SourceIds);
    }

    [Fact]
    public async Task Apply_rechecks_the_owner_subject_after_acquiring_the_target_lock()
    {
        StaffMember member = CreateMember("subject:owner-a");
        FakeAnchors anchors = new();
        CallbackOperationLock targetLock = new(staffMemberId =>
        {
            Assert.Equal(member.Id, staffMemberId);
            Assert.True(member.Suspend(
                member.Version,
                "system:concurrent-transition",
                "Concurrent identity transition.",
                Guid.NewGuid(),
                Now.AddMinutes(1)).IsSuccess);
            Assert.True(member.SetAuthSubject(
                null,
                member.Version,
                "system:concurrent-transition",
                Guid.NewGuid(),
                Now.AddMinutes(2)).IsSuccess);
        });
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [member],
            anchors,
            memberLock: targetLock);

        Result<BunkFy.Modules.Staff.Application.Commands.
            StaffIdentityProvisioningAnchorApplySummary> result =
            await coordinator.ApplyAsync(
                "tenant-a",
                [Owner(Guid.NewGuid(), member.Id, "subject:owner-a")],
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorCutoverBlocked,
            result.Error);
        Assert.Empty(anchors.Records);
        Assert.Equal(1, targetLock.AcquireCount);
    }

    [Fact]
    public async Task Apply_atomically_adds_both_source_kinds_and_preserves_receipt_time()
    {
        StaffMember workspaceMember = CreateMember();
        StaffMember ownerMember = CreateMember();
        Guid applicationId = Guid.NewGuid();
        Guid membershipId = Guid.NewGuid();
        DateTimeOffset completedAtUtc = Now.AddDays(-10);
        FakeOperations operations = new();
        operations.Add(Receipt(
            applicationId,
            workspaceMember.Id,
            completedAtUtc));
        FakeAnchors anchors = new();
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [workspaceMember, ownerMember],
            anchors,
            operations);

        Result<BunkFy.Modules.Staff.Application.Commands.
            StaffIdentityProvisioningAnchorApplySummary> result =
            await coordinator.ApplyAsync(
                "tenant-a",
                [
                    Workspace(applicationId, workspaceMember.Id),
                    Owner(
                        membershipId,
                        ownerMember.Id,
                        ownerMember.AuthSubjectId!)
                ],
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(2, result.Value.AppliedCount);
        Assert.Equal(
            completedAtUtc,
            anchors.Records.Single(record =>
                record.SourceId == applicationId).AnchoredAtUtc);
        Assert.Equal(
            Now,
            anchors.Records.Single(record =>
                record.SourceId == membershipId).AnchoredAtUtc);
    }

    [Fact]
    public async Task Apply_is_idempotent_for_an_exact_existing_anchor()
    {
        StaffMember member = CreateMember();
        Guid sourceId = Guid.NewGuid();
        FakeAnchors anchors = new();
        anchors.Records.Add(new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.OrganizationMembership,
            sourceId,
            member.Id,
            Now.AddDays(-1)));
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [member],
            anchors);

        Result<BunkFy.Modules.Staff.Application.Commands.
            StaffIdentityProvisioningAnchorApplySummary> result =
            await coordinator.ApplyAsync(
                "tenant-a",
                [Owner(sourceId, member.Id, member.AuthSubjectId!)],
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(0, result.Value.AppliedCount);
        Assert.Equal(1, result.Value.AlreadyAnchoredCount);
        Assert.Single(anchors.Records);
    }

    [Fact]
    public async Task Inspect_rejects_duplicates_and_oversized_batches()
    {
        Guid sourceId = Guid.NewGuid();
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            []);
        StaffIdentityProvisioningAnchorCandidate duplicate =
            Workspace(sourceId, Guid.NewGuid());
        StaffIdentityProvisioningAnchorCandidate[] oversized = Enumerable
            .Range(
                0,
                StaffWorkspaceOnboardingAnchorCutoverLimits.MaximumBatchSize +
                    1)
            .Select(_ => Workspace(Guid.NewGuid(), null))
            .ToArray();

        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> duplicates =
            await coordinator.InspectAsync(
                [duplicate, duplicate],
                CancellationToken.None);
        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> tooMany =
            await coordinator.InspectAsync(
                oversized,
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorCutoverRequestInvalid,
            duplicates.Error);
        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorCutoverRequestInvalid,
            tooMany.Error);
    }

    [Fact]
    public async Task Apply_rejects_an_invalid_subject_precondition_before_source_locks()
    {
        StaffMember member = CreateMember();
        RecordingLock sourceLock = new();
        FakeAnchors anchors = new();
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = Create(
            [member],
            anchors,
            creationLock: sourceLock);

        Result<BunkFy.Modules.Staff.Application.Commands.
            StaffIdentityProvisioningAnchorApplySummary> result =
            await coordinator.ApplyAsync(
                "tenant-a",
                [Owner(
                    Guid.NewGuid(),
                    member.Id,
                    new string('x', StaffAuthSubject.MaxLength + 1))],
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorCutoverRequestInvalid,
            result.Error);
        Assert.Empty(sourceLock.SourceIds);
        Assert.Empty(anchors.Records);
    }

    [Fact]
    public async Task Inspect_uses_three_bounded_bulk_reads_at_the_maximum_batch()
    {
        StaffMember member = CreateMember();
        FakeAnchors anchors = new();
        FakeOperations operations = new();
        FakeMembers members = new([member]);
        StaffWorkspaceOnboardingAnchorCutoverCoordinator coordinator = new(
            anchors,
            new RepositoryAnchorWriter(anchors),
            operations,
            members,
            new RecordingLock(),
            new StaffMemberMutationCoordinator(
                members,
                new NoopStaffOperationLock(),
                new TestScopeContext()),
            new TestClock());
        StaffIdentityProvisioningAnchorCandidate[] candidates = Enumerable
            .Range(
                0,
                StaffWorkspaceOnboardingAnchorCutoverLimits.MaximumBatchSize)
            .Select(_ => Workspace(Guid.NewGuid(), member.Id))
            .ToArray();

        Result<IReadOnlyList<
            StaffIdentityProvisioningAnchorCandidateInspection>> result =
            await coordinator.InspectAsync(
                candidates,
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(candidates.Length, result.Value.Count);
        Assert.Equal(1, anchors.ListCount);
        Assert.Equal(1, operations.ListCount);
        Assert.Equal(1, members.SafetyEvidenceListCount);
    }

    private static StaffWorkspaceOnboardingAnchorCutoverCoordinator Create(
        IReadOnlyList<StaffMember> members,
        FakeAnchors? anchors = null,
        FakeOperations? operations = null,
        RecordingLock? creationLock = null,
        IStaffOperationLock? memberLock = null)
    {
        FakeMembers memberRepository = new(members);
        FakeAnchors anchorRepository = anchors ?? new();
        return new(
            anchorRepository,
            new RepositoryAnchorWriter(anchorRepository),
            operations ?? new(),
            memberRepository,
            creationLock ?? new(),
            new StaffMemberMutationCoordinator(
                memberRepository,
                memberLock ?? new NoopStaffOperationLock(),
                new TestScopeContext()),
            new TestClock());
    }

    private static StaffIdentityProvisioningAnchorCandidate Workspace(
        Guid sourceId,
        Guid? staffMemberId,
        string expectedAuthSubjectId = "subject:staff") =>
        new(
            StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding,
            sourceId,
            staffMemberId,
            expectedAuthSubjectId);

    private static StaffIdentityProvisioningAnchorCandidate Owner(
        Guid sourceId,
        Guid staffMemberId,
        string expectedAuthSubjectId,
        bool reviewedErasedTarget = false) =>
        new(
            StaffIdentityProvisioningAnchorSourceKind.OrganizationMembership,
            sourceId,
            staffMemberId,
            expectedAuthSubjectId,
            reviewedErasedTarget);

    private static StaffIdentityProvisioningAnchorCandidateInspection Find(
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidateInspection> items,
        Guid sourceId) => items.Single(item => item.SourceId == sourceId);

    private static StaffMemberMutationOperationRecord Receipt(
        Guid sourceId,
        Guid staffMemberId,
        DateTimeOffset? completedAtUtc = null) =>
        new(
            sourceId,
            "tenant-a",
            staffMemberId,
            StaffMemberMutationKind.OnboardingProvision,
            1,
            new string('a', 64),
            StaffStatus.Active,
            1,
            completedAtUtc ?? Now.AddDays(-1));

    private static StaffMember CreateMember(
        string? authSubjectId = "subject:staff") => StaffMember.Create(
        Guid.NewGuid(),
        "tenant-a",
        "Staff member",
        null,
        null,
        null,
        null,
        null,
        null,
        authSubjectId,
        "system:cutover",
        Guid.NewGuid(),
        Now).Value;

    private sealed class FakeAnchors :
        IStaffIdentityProvisioningAnchorRepository
    {
        public List<StaffIdentityProvisioningAnchorRecord> Records { get; } =
            [];
        public int ListCount { get; private set; }

        public Task<IReadOnlyList<StaffIdentityProvisioningAnchorRecord>>
            ListAsync(
                IReadOnlyList<StaffIdentityProvisioningSourceKey> sources,
                CancellationToken cancellationToken)
        {
            this.ListCount++;
            HashSet<(StaffIdentityProvisioningSourceKind, Guid)> keys = sources
                .Select(source => (source.SourceKind, source.SourceId))
                .ToHashSet();
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

    private sealed class RepositoryAnchorWriter(
        IStaffIdentityProvisioningAnchorRepository anchors)
        : IStaffIdentityProvisioningAnchorWriter
    {
        public async Task<StaffIdentityProvisioningAnchorRecord> AddAsync(
            StaffIdentityProvisioningAnchorRecord anchor,
            CancellationToken cancellationToken)
        {
            await anchors.AddAsync(anchor, cancellationToken);
            return anchor;
        }
    }

    private sealed class FakeOperations :
        IStaffOnboardingProvisioningOperationRepository
    {
        private readonly Dictionary<Guid, StaffMemberMutationOperationRecord>
            records = [];
        public int ListCount { get; private set; }

        public Task<IReadOnlyList<StaffMemberMutationOperationRecord>>
            ListAsync(
                IReadOnlyList<Guid> operationIds,
                CancellationToken cancellationToken)
        {
            this.ListCount++;
            return Task.FromResult<IReadOnlyList<
                StaffMemberMutationOperationRecord>>(operationIds
                    .Where(this.records.ContainsKey)
                    .Select(operationId => this.records[operationId])
                    .ToArray());
        }

        public Task<StaffMemberMutationOperationRecord?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.records.GetValueOrDefault(operationId));

        public Task AddAsync(
            StaffMemberMutationOperationRecord operation,
            CancellationToken cancellationToken)
        {
            this.Add(operation);
            return Task.CompletedTask;
        }

        public void Add(StaffMemberMutationOperationRecord operation) =>
            this.records.Add(operation.OperationId, operation);
    }

    private sealed class RecordingLock : IStaffCreationOperationLock
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

    private sealed class CallbackOperationLock(Action<Guid> onAcquire)
        : IStaffOperationLock
    {
        public int AcquireCount { get; private set; }

        public Task<long?> GetStaffMemberRevisionAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<long?>(1);

        public Task<bool> TryAcquireStaffMemberAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            this.AcquireCount++;
            onAcquire(staffMemberId);
            return Task.FromResult(true);
        }
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class FakeMembers(IReadOnlyList<StaffMember> members)
        : IStaffMemberRepository
    {
        public int SafetyEvidenceListCount { get; private set; }

        public Task<IReadOnlyList<StaffMemberSafetyEvidence>>
            ListSafetyEvidenceAsync(
                IReadOnlyList<Guid> staffMemberIds,
                CancellationToken cancellationToken)
        {
            this.SafetyEvidenceListCount++;
            HashSet<Guid> ids = staffMemberIds.ToHashSet();
            return Task.FromResult<IReadOnlyList<StaffMemberSafetyEvidence>>(
                members.Where(member => ids.Contains(member.Id))
                    .Select(member => new StaffMemberSafetyEvidence(
                        member.Id,
                        member.AuthSubjectId,
                        member.Status))
                    .ToArray());
        }

        public Task<StaffMember?> GetForSafetyTransitionAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(members.SingleOrDefault(member =>
                member.Id == staffMemberId));

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
}

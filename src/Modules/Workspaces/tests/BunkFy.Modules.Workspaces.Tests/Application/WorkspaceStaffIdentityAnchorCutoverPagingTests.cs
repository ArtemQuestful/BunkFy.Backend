namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Gma.Modules.Organizations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffIdentityAnchorCutoverPagingTests
{
    private static readonly Guid OrganizationId =
        Guid.Parse("1d68a3a4-e4eb-4bf6-b36b-b0b447965d02");
    private static readonly string TenantId = OrganizationId.ToString("D");

    [Fact]
    public async Task More_than_one_source_page_is_read_in_strict_order()
    {
        int count = WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize +
            3;
        WorkspaceStaffIdentityAnchorSourceRecord[] expected = Enumerable
            .Range(1, count)
            .Select(Source)
            .OrderBy(record => record.ApplicationId)
            .ToArray();
        KeysetSources sources = new(expected.Reverse());
        RecordingStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = new(
            sources,
            staff,
            new EmptyOrganizationScopeLifecycle());

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await coordinator.GetStatusAsync(
                TenantId,
                ownerManifest: null,
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(count, result.Value.WorkspaceSourceCount);
        Assert.Equal(
            [null, expected[199].ApplicationId],
            sources.Requests.Select(request => request.AfterApplicationId));
        Assert.All(
            sources.Requests,
            request => Assert.Equal(
                WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize,
                request.PageSize));
        Assert.Equal(
            expected.Select(record => record.ApplicationId),
            staff.Inspections.SelectMany(batch => batch)
                .Select(candidate => candidate.SourceId));
        Assert.All(
            staff.Inspections,
            batch => Assert.InRange(
                batch.Count,
                1,
                StaffWorkspaceOnboardingAnchorCutoverLimits
                    .MaximumBatchSize));
    }

    [Fact]
    public async Task Non_advancing_source_page_fails_before_staff_inspection()
    {
        NonAdvancingSources sources = new();
        RecordingStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = new(
            sources,
            staff,
            new EmptyOrganizationScopeLifecycle());

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await coordinator.GetStatusAsync(
                TenantId,
                ownerManifest: null,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.SourcePageInvalid,
            result.Error);
        Assert.Equal(2, sources.RequestCount);
        Assert.Single(staff.Inspections);
    }

    [Fact]
    public async Task Noncanonical_source_subject_fails_closed_before_Staff_inspection()
    {
        KeysetSources sources = new([
            Source(1) with { SubjectId = " subject:1 " }
        ]);
        RecordingStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = new(
            sources,
            staff,
            new EmptyOrganizationScopeLifecycle());

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await coordinator.GetStatusAsync(
                TenantId,
                ownerManifest: null,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.SourcePageInvalid,
            result.Error);
        Assert.Empty(staff.Inspections);
    }

    [Fact]
    public async Task Duplicate_source_coordinate_fails_closed()
    {
        WorkspaceStaffIdentityAnchorSourceRecord duplicate = Source(1);
        RecordingStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = new(
            new KeysetSources([duplicate, duplicate]),
            staff,
            new EmptyOrganizationScopeLifecycle());

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await coordinator.GetStatusAsync(
                TenantId,
                ownerManifest: null,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.SourcePageInvalid,
            result.Error);
        Assert.Empty(staff.Inspections);
    }

    [Fact]
    public async Task More_than_ten_thousand_sources_stream_without_a_lifetime_cap()
    {
        const int count = 10_002;
        GeneratedSources sources = new(count, terminalNullTargetSeed: count);
        RecordingStaffCutover staff = new(retainInspections: false);
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = new(
            sources,
            staff,
            new EmptyOrganizationScopeLifecycle());

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await coordinator.GetStatusAsync(
                TenantId,
                ownerManifest: null,
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(count, result.Value.WorkspaceSourceCount);
        Assert.Equal(count - 1, result.Value.SeedableWorkspaceCount);
        Assert.Equal(1, result.Value.AmbiguousCount);
        Assert.Equal(count, staff.InspectedCandidateCount);
        Assert.InRange(
            staff.MaximumInspectionSize,
            1,
            StaffWorkspaceOnboardingAnchorCutoverLimits.MaximumBatchSize);
        Assert.Equal(1, result.Value.TotalIssueCount);
        Assert.Single(result.Value.Issues);
        Assert.Equal(
            CreateDeterministicGuid(count),
            result.Value.Issues[0].SourceId);
    }

    [Fact]
    public async Task Full_stream_status_and_prepare_match_and_only_the_requested_batch_is_retained()
    {
        const int count = 10_001;
        GeneratedSources sources = new(count);
        RecordingStaffCutover staff = new(retainInspections: false);
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = new(
            sources,
            staff,
            new EmptyOrganizationScopeLifecycle());
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = new(
            1,
            TenantId,
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            new(
                WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                    .ReviewedNoHistoricalOwnerSources,
                new string('a', 64)),
            []);

        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        Result<WorkspaceStaffIdentityAnchorPreparedReconcile> prepared =
            await coordinator.PrepareReconcileAsync(
                TenantId,
                status.SourceEvidenceSha256,
                status.AnchorStateSha256,
                manifest,
                status.OwnerManifestSha256!,
                StaffWorkspaceOnboardingAnchorCutoverLimits.MaximumBatchSize,
                CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.Error.Code);
        Assert.Equal(status, prepared.Value.AcceptedStatus);
        Assert.Equal(count, status.WorkspaceSourceCount);
        Assert.Equal(count, status.SeedableWorkspaceCount);
        Assert.Equal(
            StaffWorkspaceOnboardingAnchorCutoverLimits.MaximumBatchSize,
            prepared.Value.Batch.Count);
        Assert.Equal(
            Enumerable.Range(
                    1,
                    StaffWorkspaceOnboardingAnchorCutoverLimits
                        .MaximumBatchSize)
                .Select(CreateDeterministicGuid),
            prepared.Value.Batch.Select(candidate => candidate.SourceId));
        Assert.Equal((long)count * 2, staff.InspectedCandidateCount);
    }

    [Fact]
    public async Task More_than_ten_thousand_memberships_are_revision_pinned_and_streamed_without_N_plus_one_reads()
    {
        const int count = 10_002;
        GeneratedOrganizationScopeLifecycle organizations = new(
            count,
            activeOwnerSeed: count);
        RecordingStaffCutover staff = new(retainInspections: false);
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = new(
            new GeneratedSources(count: 0),
            staff,
            organizations);

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await coordinator.GetStatusAsync(
                TenantId,
                EmptyManifest(),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(1, result.Value.AuthoritativeOwnerCount);
        Assert.Equal(1, result.Value.AmbiguousCount);
        Assert.Equal(1, staff.InspectedCandidateCount);
        Assert.Equal(
            (int)Math.Ceiling(
                count /
                (double)OrganizationScopeLifecycleLimits.MaximumPageSize),
            organizations.MembershipExportCallCount);
        Assert.InRange(
            organizations.MaximumMembershipPageCount,
            1,
            OrganizationScopeLifecycleLimits.MaximumPageSize);
        Assert.Equal(2, organizations.SnapshotCallCount);
    }

    [Fact]
    public async Task Organizations_revision_drift_during_membership_paging_fails_closed()
    {
        GeneratedOrganizationScopeLifecycle organizations = new(
            count: OrganizationScopeLifecycleLimits.MaximumPageSize + 1,
            activeOwnerSeed: null)
        {
            DriftOnMembershipExportCall = 2
        };
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = new(
            new GeneratedSources(count: 0),
            new RecordingStaffCutover(retainInspections: false),
            organizations);

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await coordinator.GetStatusAsync(
                TenantId,
                EmptyManifest(),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors
                .OrganizationsEvidenceChanged,
            result.Error);
    }

    private static WorkspaceStaffIdentityAnchorOwnerManifest EmptyManifest() =>
        new(
            1,
            TenantId,
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            new(
                WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                    .ReviewedNoHistoricalOwnerSources,
                new string('a', 64)),
            []);

    private static WorkspaceStaffIdentityAnchorSourceRecord Source(int seed) =>
        new(
            CreateDeterministicGuid(seed),
            CreateDeterministicGuid(20_000 + seed),
            $"subject:{seed}",
            WorkspaceStaffOnboardingState.Completed);

    private static Guid CreateDeterministicGuid(int value) =>
        Guid.ParseExact(
            $"00000000-0000-0000-0000-{value:D12}",
            "D");

    private sealed class KeysetSources(
        IEnumerable<WorkspaceStaffIdentityAnchorSourceRecord> records)
        : IWorkspaceStaffIdentityAnchorCutoverSourceReader
    {
        private readonly WorkspaceStaffIdentityAnchorSourceRecord[] records =
            records.OrderBy(record => record.ApplicationId).ToArray();

        public List<(Guid? AfterApplicationId, int PageSize)> Requests
        { get; } = [];

        public Task<WorkspaceStaffIdentityAnchorSourcePage>
            ListRelevantPageAsync(
                Guid? afterApplicationId,
                int pageSize,
                CancellationToken cancellationToken)
        {
            this.Requests.Add((afterApplicationId, pageSize));
            WorkspaceStaffIdentityAnchorSourceRecord[] loaded = this.records
                .Where(record => !afterApplicationId.HasValue ||
                    record.ApplicationId.CompareTo(
                        afterApplicationId.Value) > 0)
                .Take(pageSize + 1)
                .ToArray();
            bool hasMore = loaded.Length > pageSize;
            WorkspaceStaffIdentityAnchorSourceRecord[] selected = loaded
                .Take(pageSize)
                .ToArray();
            return Task.FromResult(new WorkspaceStaffIdentityAnchorSourcePage(
                Array.AsReadOnly(selected),
                selected.Length == 0
                    ? afterApplicationId
                    : selected[^1].ApplicationId,
                hasMore));
        }
    }

    private sealed class NonAdvancingSources
        : IWorkspaceStaffIdentityAnchorCutoverSourceReader
    {
        public int RequestCount { get; private set; }

        public Task<WorkspaceStaffIdentityAnchorSourcePage>
            ListRelevantPageAsync(
                Guid? afterApplicationId,
                int pageSize,
                CancellationToken cancellationToken)
        {
            this.RequestCount++;
            int firstSeed = afterApplicationId.HasValue ? pageSize + 1 : 1;
            WorkspaceStaffIdentityAnchorSourceRecord[] records = Enumerable
                .Range(firstSeed, pageSize)
                .Select(Source)
                .ToArray();
            return Task.FromResult(new WorkspaceStaffIdentityAnchorSourcePage(
                records,
                afterApplicationId ?? records[^1].ApplicationId,
                HasMore: true));
        }
    }

    private sealed class GeneratedSources(
        int count,
        int? terminalNullTargetSeed = null)
        : IWorkspaceStaffIdentityAnchorCutoverSourceReader
    {
        public Task<WorkspaceStaffIdentityAnchorSourcePage>
            ListRelevantPageAsync(
                Guid? afterApplicationId,
                int pageSize,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = afterApplicationId.HasValue
                ? ParseSeed(afterApplicationId.Value) + 1
                : 1;
            int loadedCount = Math.Min(pageSize + 1, count - start + 1);
            WorkspaceStaffIdentityAnchorSourceRecord[] loaded = loadedCount > 0
                ? Enumerable.Range(start, loadedCount)
                    .Select(seed => new WorkspaceStaffIdentityAnchorSourceRecord(
                        CreateDeterministicGuid(seed),
                        seed == terminalNullTargetSeed
                            ? null
                            : CreateDeterministicGuid(20_000 + seed),
                        $"subject:{seed}",
                        seed == terminalNullTargetSeed
                            ? WorkspaceStaffOnboardingState.Superseded
                            : WorkspaceStaffOnboardingState.Completed))
                    .ToArray()
                : [];
            WorkspaceStaffIdentityAnchorSourceRecord[] selected = loaded
                .Take(pageSize)
                .ToArray();
            return Task.FromResult(new WorkspaceStaffIdentityAnchorSourcePage(
                selected,
                selected.Length == 0
                    ? afterApplicationId
                    : selected[^1].ApplicationId,
                loaded.Length > pageSize));
        }

        private static int ParseSeed(Guid id) => int.Parse(
            id.ToString("D", null)[^12..],
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class RecordingStaffCutover(
        bool retainInspections = true)
        : IStaffIdentityProvisioningAnchorCutover
    {
        public List<IReadOnlyList<StaffIdentityProvisioningAnchorCandidate>>
            Inspections
        { get; } = [];
        public long InspectedCandidateCount { get; private set; }
        public int MaximumInspectionSize { get; private set; }

        public Task<StaffIdentityProvisioningAnchorInspection> InspectAsync(
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            this.InspectedCandidateCount += candidates.Count;
            this.MaximumInspectionSize = Math.Max(
                this.MaximumInspectionSize,
                candidates.Count);
            if (retainInspections)
            {
                this.Inspections.Add(candidates.ToArray());
            }

            return Task.FromResult(new StaffIdentityProvisioningAnchorInspection(
                true,
                candidates.Select(candidate =>
                    new StaffIdentityProvisioningAnchorCandidateInspection(
                        candidate.SourceKind,
                        candidate.SourceId,
                        candidate.StaffMemberId.HasValue
                            ? StaffIdentityProvisioningAnchorCutoverDisposition
                                .SeedableFromWorkspace
                            : StaffIdentityProvisioningAnchorCutoverDisposition
                                .Ambiguous))
                    .ToArray(),
                ErrorCode: null));
        }

        public Task<StaffIdentityProvisioningAnchorApplyResult> ApplyAsync(
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyOrganizationScopeLifecycle
        : IOrganizationScopeLifecycle
    {
        private const long Revision = 7;
        private static readonly DateTimeOffset Now = new(
            2026,
            8,
            11,
            12,
            0,
            0,
            TimeSpan.Zero);

        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OrganizationScopeSnapshot(
                OrganizationScopeStatus.Open,
                Revision));

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken)
        {
            OrganizationScopeExportRecord[] records =
                request.Store == OrganizationScopeExportStore.Organization
                    ? [
                        new OrganizationScopeOrganizationExportRecord(
                            request.OrganizationId,
                            "Tenant",
                            "tenant",
                            OrganizationStatus.Active,
                            ActiveOwnerCount: 0,
                            Version: 3,
                            CreatedBy: "system:test",
                            CreatedAtUtc: Now,
                            LastChangedBy: "system:test",
                            LastChangedAtUtc: Now)
                      ]
                    : [];
            return Task.FromResult(new OrganizationScopeExportPage(
                OrganizationScopeExportStatus.Completed,
                Revision,
                request.Store,
                records,
                NextCursor: null,
                HasMore: false));
        }

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class GeneratedOrganizationScopeLifecycle(
        int count,
        int? activeOwnerSeed) : IOrganizationScopeLifecycle
    {
        private const long Revision = 11;
        private static readonly DateTimeOffset Now = new(
            2026,
            8,
            11,
            12,
            0,
            0,
            TimeSpan.Zero);

        public int MembershipExportCallCount { get; private set; }
        public int MaximumMembershipPageCount { get; private set; }
        public int SnapshotCallCount { get; private set; }
        public int? DriftOnMembershipExportCall { get; init; }

        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.SnapshotCallCount++;
            return Task.FromResult(new OrganizationScopeSnapshot(
                OrganizationScopeStatus.Open,
                Revision));
        }

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Store == OrganizationScopeExportStore.Organization)
            {
                return Task.FromResult(new OrganizationScopeExportPage(
                    OrganizationScopeExportStatus.Completed,
                    Revision,
                    request.Store,
                    [new OrganizationScopeOrganizationExportRecord(
                        request.OrganizationId,
                        "Tenant",
                        "tenant",
                        OrganizationStatus.Active,
                        activeOwnerSeed.HasValue ? 1 : 0,
                        Version: 3,
                        CreatedBy: "system:test",
                        CreatedAtUtc: Now,
                        LastChangedBy: "system:test",
                        LastChangedAtUtc: Now)],
                    "id:" + request.OrganizationId.ToString("D"),
                    HasMore: false));
            }

            this.MembershipExportCallCount++;
            if (this.DriftOnMembershipExportCall ==
                this.MembershipExportCallCount)
            {
                return Task.FromResult(new OrganizationScopeExportPage(
                    OrganizationScopeExportStatus.Stale,
                    Revision + 1,
                    request.Store,
                    [],
                    request.AfterCursor,
                    HasMore: false));
            }

            int start = request.AfterCursor is null
                ? 1
                : ParseSeed(Guid.ParseExact(
                    request.AfterCursor[3..],
                    "D")) + 1;
            int loadedCount = Math.Min(
                request.PageSize + 1,
                count - start + 1);
            OrganizationScopeMembershipExportRecord[] loaded = loadedCount > 0
                ? Enumerable.Range(start, loadedCount)
                    .Select(seed =>
                        new OrganizationScopeMembershipExportRecord(
                            CreateDeterministicGuid(seed),
                            request.OrganizationId,
                            $"subject:membership:{seed}",
                            seed == activeOwnerSeed
                                ? OrganizationMembershipRole.Owner
                                : OrganizationMembershipRole.Member,
                            seed == activeOwnerSeed
                                ? OrganizationMembershipStatus.Active
                                : OrganizationMembershipStatus.Removed,
                            Version: 1,
                            CreatedBy: "system:test",
                            JoinedAtUtc: Now,
                            LastChangedBy: "system:test",
                            LastChangedAtUtc: Now))
                    .ToArray()
                : [];
            OrganizationScopeMembershipExportRecord[] selected = loaded
                .Take(request.PageSize)
                .ToArray();
            this.MaximumMembershipPageCount = Math.Max(
                this.MaximumMembershipPageCount,
                selected.Length);
            return Task.FromResult(new OrganizationScopeExportPage(
                OrganizationScopeExportStatus.Completed,
                Revision,
                request.Store,
                selected,
                selected.Length == 0
                    ? request.AfterCursor
                    : "id:" + selected[^1].MembershipId.ToString("D"),
                loaded.Length > request.PageSize));
        }

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static int ParseSeed(Guid id) => int.Parse(
            id.ToString("D", null)[^12..],
            System.Globalization.CultureInfo.InvariantCulture);
    }
}

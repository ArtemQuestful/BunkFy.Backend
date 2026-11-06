namespace BunkFy.Modules.Workspaces.Tests.Application;

using BunkFy.Modules.Workspaces.Application;
using Gma.Framework.Runtime.Time;
using Gma.Modules.Organizations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceOrganizationMembershipObserverTests
{
    private static readonly Guid OrganizationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string SubjectId = "auth:manager";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Active_exact_member_returns_fenced_observation_time()
    {
        Guid membershipId = Guid.NewGuid();
        FakeLifecycle lifecycle = new(
            new OrganizationScopeSnapshot(OrganizationScopeStatus.Open, 7),
            new OrganizationScopeSnapshot(OrganizationScopeStatus.Open, 7));
        FakeReader reader = new(Snapshot(membershipId, version: 3));
        WorkspaceOrganizationMembershipObserver observer = new(
            [lifecycle],
            [reader],
            new FixedClock(Now));

        WorkspaceOrganizationMembershipObservation result =
            await observer.ObserveAsync(
                OrganizationId.ToString("D"),
                SubjectId,
                CancellationToken.None);

        Assert.Equal(WorkspaceRecoveryVerificationStatus.Equivalent, result.Status);
        Assert.Equal(WorkspaceOrganizationMembershipReason.Equivalent, result.Reason);
        Assert.Equal(7, result.OrganizationRevision);
        Assert.Equal(membershipId, result.MembershipId);
        Assert.Equal(3, result.MembershipVersion);
        Assert.Equal(OrganizationMembershipStatus.Active, result.MembershipStatus);
        Assert.Equal(Now, result.ObservedAtUtc);
    }

    [Theory]
    [InlineData(OrganizationScopeStatus.Closed)]
    [InlineData(OrganizationScopeStatus.Missing)]
    [InlineData(OrganizationScopeStatus.Invalid)]
    public async Task Closed_or_absent_scope_requires_review(
        OrganizationScopeStatus status)
    {
        FakeReader reader = new(null);
        WorkspaceOrganizationMembershipObserver observer = new(
            [new FakeLifecycle(new OrganizationScopeSnapshot(status, 4))],
            [reader],
            new FixedClock(Now));

        WorkspaceOrganizationMembershipObservation result =
            await observer.ObserveAsync(
                OrganizationId.ToString("D"),
                SubjectId,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceRecoveryVerificationStatus.ReviewRequired,
            result.Status);
        Assert.Equal(WorkspaceOrganizationMembershipReason.ScopeClosed, result.Reason);
        Assert.Equal(Now, result.ObservedAtUtc);
        Assert.Equal(0, reader.CallCount);
    }

    [Fact]
    public async Task Revision_change_is_retryable_and_keeps_actual_observation_time()
    {
        WorkspaceOrganizationMembershipObserver observer = new(
            [new FakeLifecycle(
                new OrganizationScopeSnapshot(OrganizationScopeStatus.Open, 7),
                new OrganizationScopeSnapshot(OrganizationScopeStatus.Open, 8))],
            [new FakeReader(Snapshot(Guid.NewGuid(), version: 1))],
            new FixedClock(Now));

        WorkspaceOrganizationMembershipObservation result =
            await observer.ObserveAsync(
                OrganizationId.ToString("D"),
                SubjectId,
                CancellationToken.None);

        Assert.Equal(WorkspaceRecoveryVerificationStatus.RetryLater, result.Status);
        Assert.Equal(
            WorkspaceOrganizationMembershipReason.RevisionChanged,
            result.Reason);
        Assert.Equal(Now, result.ObservedAtUtc);
    }

    private static OrganizationMembershipSnapshotDto Snapshot(
        Guid membershipId,
        long version) => new(
        OrganizationId,
        OrganizationStatus.Active,
        new OrganizationMembershipDto(
            membershipId,
            OrganizationId,
            SubjectId,
            OrganizationMembershipRole.Member,
            OrganizationMembershipStatus.Active,
            version,
            Now.AddDays(-1),
            Now));

    private sealed class FakeReader(OrganizationMembershipSnapshotDto? snapshot)
        : IOrganizationMembershipReader
    {
        public int CallCount { get; private set; }

        public Task<OrganizationMembershipSnapshotDto?> FindAsync(
            Guid organizationId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeLifecycle(
        params OrganizationScopeSnapshot[] snapshots)
        : IOrganizationScopeLifecycle
    {
        private int index;

        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            int selected = Math.Min(
                Interlocked.Increment(ref this.index) - 1,
                snapshots.Length - 1);
            return Task.FromResult(snapshots[selected]);
        }

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;
    }
}

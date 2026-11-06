namespace BunkFy.Modules.Retention.Tests.Application;

using BunkFy.Modules.Retention.Application.Handlers;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Application.Queries;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ListRetentionScheduleHealthQueryHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Health_snapshot_is_prioritized_paginated_and_exposes_run_coordinate()
    {
        Guid propertyId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        RetentionScheduleDescriptor overdue = Descriptor("raw-source-evidence");
        RetentionScheduleDescriptor neverRun = Descriptor("sensitive-history");
        TestClock clock = new();
        ListRetentionScheduleHealthQueryHandler handler = new(
            new FakeScopeRepository(new("tenant-a", propertyId)),
            new FakeHealthReader(new RetentionScheduleStateSnapshot(
                overdue.OwnerKey,
                overdue.DataClassKey,
                propertyId,
                overdue.ExecutionPolicyVersion,
                Version: 4,
                State: (int)RetentionExecutionState.Failed,
                runId,
                Now.AddHours(-2),
                Now.AddHours(-2).AddMinutes(1),
                Now.AddHours(-1),
                2,
                10,
                0,
                10,
                "owner-timeout",
                HoldReviewDueAtUtc: null,
                Retry: null)),
            [
                new Contributor(neverRun),
                new Contributor(overdue)
            ],
            clock);

        Result<RetentionScheduleHealthListResponse> firstPage =
            await handler.HandleAsync(
                new ListRetentionScheduleHealthQuery(1, 1),
                CancellationToken.None);
        Result<RetentionScheduleHealthListResponse> secondPage =
            await handler.HandleAsync(
                new ListRetentionScheduleHealthQuery(2, 1),
                CancellationToken.None);

        Assert.True(firstPage.IsSuccess);
        RetentionScheduleHealthDto failed = Assert.Single(firstPage.Value.Items);
        Assert.Equal(overdue.DataClassKey, failed.DataClassKey);
        Assert.Equal(RetentionExecutionStatus.Failed, failed.Status);
        Assert.Equal(runId, failed.LastRunId);
        Assert.True(failed.Overdue);
        Assert.Equal(2, failed.ConsecutiveFailures);
        Assert.Equal(1, firstPage.Value.Page);
        Assert.Equal(1, firstPage.Value.PageSize);
        Assert.True(firstPage.Value.HasMore);
        Assert.Equal(
            new RetentionScheduleHealthSummaryDto(2, 0, 0, 2),
            firstPage.Value.Summary);

        Assert.True(secondPage.IsSuccess);
        RetentionScheduleHealthDto pending = Assert.Single(secondPage.Value.Items);
        Assert.Equal(neverRun.DataClassKey, pending.DataClassKey);
        Assert.Equal(RetentionExecutionStatus.NeverRun, pending.Status);
        Assert.Null(pending.LastRunId);
        Assert.False(pending.Overdue);
        Assert.Equal(Now, pending.NextDueAtUtc);
        Assert.False(secondPage.Value.HasMore);
        Assert.Equal(firstPage.Value.Summary, secondPage.Value.Summary);
        Assert.Equal(2, clock.AccessCount);
    }

    [Fact]
    public async Task Duplicate_contributor_coordinate_fails_health_read_closed()
    {
        RetentionScheduleDescriptor duplicate = Descriptor("raw-source-evidence");
        ListRetentionScheduleHealthQueryHandler handler = new(
            new FakeScopeRepository(new("tenant-a", Guid.NewGuid())),
            new FakeHealthReader(),
            [new Contributor(duplicate), new Contributor(duplicate)],
            new TestClock());

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    new ListRetentionScheduleHealthQuery(),
                    CancellationToken.None));

        Assert.Equal(
            "Retention.ContributorDescriptorDuplicate",
            exception.Message);
    }

    private static RetentionScheduleDescriptor Descriptor(string dataClassKey) => new(
        "ingestion",
        dataClassKey,
        RetentionTargetScopeKind.Property,
        executionPolicyVersion: 1,
        TimeSpan.FromHours(1));

    private sealed class Contributor(RetentionScheduleDescriptor schedule)
        : IRetentionExecutionContributor
    {
        public RetentionScheduleDescriptor Schedule { get; } = schedule;

        public Task<RetentionContributionResult> ExecuteAsync(
            RetentionContributionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeScopeRepository(RetentionScheduleTarget target)
        : IRetentionScopeRepository
    {
        public Task<IReadOnlyList<RetentionScheduleTarget>> ListCurrentActiveTargetsAsync(
            RetentionTargetScopeKind targetKind,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetentionScheduleTarget>>([target]);

        public Task ApplyOrganizationAsync(
            RetentionOrganizationWriteModel organization,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ApplyPropertyTopologyAsync(
            RetentionPropertyTopologyWriteModel property,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ApplyPropertyPolicyAsync(
            RetentionPropertyPolicyWriteModel property,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RetentionScheduleTarget>> ListActiveTargetsAsync(
            RetentionTargetScopeKind targetKind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsActiveTargetAsync(
            RetentionTargetScopeKind targetKind,
            Guid? propertyId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHealthReader(
        params RetentionScheduleStateSnapshot[] snapshots)
        : IRetentionScheduleHealthReader
    {
        public Task<RetentionScheduleStateSnapshot?> GetAsync(
            string tenantId,
            string ownerKey,
            string dataClassKey,
            Guid? propertyId,
            int executionPolicyVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshots.SingleOrDefault(snapshot =>
                string.Equals(
                    snapshot.OwnerKey,
                    ownerKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    snapshot.DataClassKey,
                    dataClassKey,
                    StringComparison.Ordinal) &&
                snapshot.PropertyId == propertyId &&
                snapshot.ExecutionPolicyVersion == executionPolicyVersion));

        public Task<IReadOnlyList<RetentionScheduleStateSnapshot>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetentionScheduleStateSnapshot>>(snapshots);

        public Task<RetentionScheduleStateSnapshot?> GetByLastExecutionIdAsync(
            string tenantId,
            Guid lastExecutionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshots.SingleOrDefault(snapshot =>
                snapshot.LastExecutionId == lastExecutionId));
    }

    private sealed class TestClock : ISystemClock
    {
        public int AccessCount { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                this.AccessCount++;
                return Now;
            }
        }
    }
}

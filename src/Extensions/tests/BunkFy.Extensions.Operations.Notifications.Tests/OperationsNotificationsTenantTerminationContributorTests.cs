namespace BunkFy.Extensions.Operations.Notifications.Tests;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.Notifications.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class OperationsNotificationsTenantTerminationContributorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly string TenantId =
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private static readonly Guid ProcessId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid TerminationEpoch =
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly NotificationHistoryReference HistoryReference =
        new("tenant", new string('a', 64));

    [Fact]
    public void Descriptor_is_the_mandatory_export_and_destroy_owner_after_ingestion()
    {
        var contributor = CreateContributor(new TestLifecycle());

        Assert.Equal(
            OperationsNotificationsTenantTerminationMetadata.OwnerKey,
            contributor.Descriptor.OwnerKey);
        Assert.True(contributor.Descriptor.MandatoryForProduction);
        Assert.Equal(
            [
                TenantTerminationContributionPhase.Export,
                TenantTerminationContributionPhase.Destroy
            ],
            contributor.Descriptor.PhasePlans.Select(plan => plan.Phase));
        Assert.All(
            contributor.Descriptor.PhasePlans,
            plan => Assert.Equal(
                [OperationsNotificationsTenantTerminationMetadata
                    .DependencyOwnerKey],
                plan.DependsOnOwnerKeys));
        Assert.Equal(
            OperationsNotificationsTenantTerminationMetadata.CatalogSha256,
            contributor.Descriptor.CatalogSha256);
        Assert.Equal(
            OperationsNotificationsTenantTerminationMetadata.ExportSchemaId,
            contributor.ExportDescriptor.ExportSchemaId);
        Assert.Equal(
            OperationsNotificationsTenantTerminationMetadata.ExportFieldIds
                .Order(StringComparer.Ordinal),
            contributor.ExportDescriptor.FieldIds);
    }

    [Fact]
    public async Task Missing_scope_exports_a_stable_empty_fragment()
    {
        bool exported = false;
        var lifecycle = new TestLifecycle
        {
            Export = (_, _) =>
            {
                exported = true;
                throw new InvalidOperationException();
            }
        };
        var sink = new CapturingSink();

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExportAsync(Request(), sink, CancellationToken.None);

        Assert.False(exported);
        Assert.Equal(TenantTerminationContributionStatus.Completed, result.Status);
        Assert.Equal(0, result.AffectedCount);
        Assert.Equal(0, result.SelectedProofRevision);
        Assert.Equal(0, result.ResultingProofRevision);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Every_scope_store_is_exported_in_deterministic_order()
    {
        const long revision = 7;
        var requests = new List<NotificationScopeExportRequest>();
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _) => Task.FromResult(
                new NotificationScopeSnapshot(
                    NotificationScopeStatus.Open,
                    revision)),
            Export = (request, _) =>
            {
                requests.Add(request);
                NotificationScopeExportRecord record = RecordFor(request.Store);
                return Task.FromResult(new NotificationScopeExportPage(
                    NotificationScopeExportStatus.Completed,
                    revision,
                    request.Store,
                    [record],
                    "id:" + RecordId(request.Store).ToString("N"),
                    false));
            }
        };
        var sink = new CapturingSink();

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExportAsync(Request(), sink, CancellationToken.None);

        Assert.Equal(TenantTerminationContributionStatus.Completed, result.Status);
        Assert.Equal(12, result.AffectedCount);
        Assert.Equal(revision, result.SelectedProofRevision);
        Assert.Equal(revision, result.ResultingProofRevision);
        Assert.Equal(
            Enum.GetValues<NotificationScopeExportStore>()
                .Where(store => store != NotificationScopeExportStore.Unknown),
            requests.Select(request => request.Store));
        Assert.All(requests, request =>
        {
            Assert.Equal(TenantId, request.ScopeId);
            Assert.Equal(revision, request.ExpectedRevision);
            Assert.Null(request.AfterCursor);
            Assert.Equal(
                NotificationScopeLifecycleLimits.MaximumPageSize,
                request.PageSize);
        });
        Assert.Equal(
            OperationsNotificationsTenantTerminationMetadata.RecordTypes,
            sink.Records.Select(record => record.RecordType));
        Assert.All(sink.Records, record =>
        {
            Assert.NotEqual(Guid.Empty, record.RecordId);
            Assert.True(record.RecordVersion > 0);
            Assert.NotEmpty(record.Fields);
        });
    }

    [Fact]
    public async Task Transient_scope_page_change_discards_the_owner_result()
    {
        var lifecycle = OpenLifecycle(4) with
        {
            Export = (request, _) => Task.FromResult(
                new NotificationScopeExportPage(
                    NotificationScopeExportStatus.Stale,
                    5,
                    request.Store,
                    [],
                    null,
                    false))
        };

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExportAsync(
                Request(),
                new CapturingSink(),
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "operations-notifications.termination.export-revision-changed",
            result.ResultCode);
    }

    [Fact]
    public async Task Mismatched_store_record_fails_closed()
    {
        var lifecycle = OpenLifecycle(2) with
        {
            Export = (request, _) => Task.FromResult(
                new NotificationScopeExportPage(
                    NotificationScopeExportStatus.Completed,
                    2,
                    request.Store,
                    [RecordFor(NotificationScopeExportStore.Preferences)],
                    "id:" + Guid.NewGuid().ToString("N"),
                    false))
        };

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExportAsync(
                Request(),
                new CapturingSink(),
                CancellationToken.None);

        Assert.Equal(TenantTerminationContributionStatus.Failed, result.Status);
        Assert.Equal(
            "operations-notifications.termination.export-record-invalid",
            result.ResultCode);
    }

    [Fact]
    public async Task Changed_final_scope_revision_discards_the_owner_result()
    {
        int snapshots = 0;
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _) => Task.FromResult(
                new NotificationScopeSnapshot(
                    NotificationScopeStatus.Open,
                    ++snapshots == 1 ? 4 : 5)),
            Export = EmptyPage
        };

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExportAsync(
                Request(),
                new CapturingSink(),
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "operations-notifications.termination.export-revision-changed",
            result.ResultCode);
    }

    [Fact]
    public async Task Mismatched_fence_fails_before_scope_selection()
    {
        bool selected = false;
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _) =>
            {
                selected = true;
                throw new InvalidOperationException();
            }
        };
        var fence = new TestFenceReader(new WorkspaceTerminationFenceSnapshot(
            Guid.NewGuid(),
            TerminationEpoch,
            WorkspaceTerminationFenceState.Frozen,
            5));

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle,
                fence)
            .ExportAsync(
                Request(),
                new CapturingSink(),
                CancellationToken.None);

        Assert.False(selected);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "operations-notifications.termination.export-fence-unavailable",
            result.ResultCode);
    }

    [Fact]
    public async Task Export_refuses_a_lifecycle_response_at_the_attempt_deadline()
    {
        TenantTerminationExportRequest request = Request();
        var clock = new MutableClock(Now);
        var lifecycle = OpenLifecycle(4) with
        {
            Export = (export, cancellationToken) =>
            {
                clock.UtcNow = request.Contribution.DeadlineUtc;
                return EmptyPage(export, cancellationToken);
            }
        };
        var sink = new CapturingSink();

        await Assert.ThrowsAsync<TimeoutException>(() => CreateContributor(
                lifecycle,
                clock: clock)
            .ExportAsync(request, sink, CancellationToken.None));

        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Destroy_reports_bounded_durable_scope_progress()
    {
        TenantTerminationContributionRequest request = DestroyRequest();
        var lifecycle = OpenLifecycle(7) with
        {
            Destroy = (destroy, _) =>
            {
                Assert.Equal(request.IdempotencyKey, destroy.OperationId);
                Assert.Equal(TenantId, destroy.ScopeId);
                Assert.Equal(7, destroy.ExpectedRevision);
                Assert.Equal(
                    NotificationScopeLifecycleLimits.MaximumDestroyBatchSize,
                    destroy.BatchSize);
                return Task.FromResult(new NotificationScopeDestroyResult(
                    NotificationScopeDestroyStatus.InProgress,
                    new NotificationScopeDestroyProgress(
                        request.IdempotencyKey,
                        ResultingRevision: 8,
                        destroy.BatchSize,
                        NotificationScopeDestructionStage.UserNotifications,
                        RemovedRecordCount: 1000,
                        CompletedBatchCount: 1,
                        RemovalProofVersion: 1,
                        new string('a', 64),
                        Now.AddMinutes(-1),
                        Now),
                    null));
            }
        };

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "operations-notifications.termination.destroy-in-progress",
            result.ResultCode);
        Assert.Equal(1000, result.AffectedCount);
        Assert.Equal(1, result.RemainingActiveCount);
        Assert.Null(result.SelectedProofRevision);
        Assert.Null(result.ResultingProofRevision);
    }

    [Fact]
    public async Task Destroy_replays_from_the_closed_scope_revision()
    {
        TenantTerminationContributionRequest request = DestroyRequest();
        var receipt = Receipt(request.IdempotencyKey, 8, 2500);
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _) => Task.FromResult(new NotificationScopeSnapshot(
                NotificationScopeStatus.Closed,
                8)),
            Destroy = (destroy, _) =>
            {
                Assert.Equal(7, destroy.ExpectedRevision);
                return Task.FromResult(new NotificationScopeDestroyResult(
                    NotificationScopeDestroyStatus.Replayed,
                    null,
                    receipt));
            }
        };

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(TenantTerminationContributionStatus.Completed, result.Status);
        Assert.Equal(
            "operations-notifications.termination.destroyed",
            result.ResultCode);
        Assert.Equal(2500, result.AffectedCount);
        Assert.Equal(0, result.RemainingActiveCount);
        Assert.Equal(7, result.SelectedProofRevision);
        Assert.Equal(8, result.ResultingProofRevision);
        Assert.Equal(receipt.CompletedAtUtc, result.RecordedAtUtc);
    }

    [Fact]
    public async Task Destroy_closes_a_missing_scope_with_a_terminal_zero_receipt()
    {
        TenantTerminationContributionRequest request = DestroyRequest();
        var lifecycle = new TestLifecycle
        {
            Destroy = (destroy, _) =>
            {
                Assert.Equal(0, destroy.ExpectedRevision);
                return Task.FromResult(new NotificationScopeDestroyResult(
                    NotificationScopeDestroyStatus.Completed,
                    null,
                    Receipt(request.IdempotencyKey, 1, 0)));
            }
        };

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(TenantTerminationContributionStatus.Completed, result.Status);
        Assert.Equal(0, result.AffectedCount);
        Assert.Equal(0, result.SelectedProofRevision);
        Assert.Equal(1, result.ResultingProofRevision);
    }

    [Fact]
    public async Task Busy_scope_destruction_is_retryable_without_claiming_progress()
    {
        var lifecycle = OpenLifecycle(3) with
        {
            Destroy = (_, _) => Task.FromResult(
                new NotificationScopeDestroyResult(
                    NotificationScopeDestroyStatus.Busy,
                    null,
                    null))
        };

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExecuteAsync(DestroyRequest(), CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "operations-notifications.termination.destroy-scope-busy",
            result.ResultCode);
        Assert.Equal(0, result.AffectedCount);
        Assert.Null(result.SelectedProofRevision);
        Assert.Null(result.ResultingProofRevision);
    }

    [Fact]
    public async Task Destroy_rejects_a_malformed_completion_receipt()
    {
        TenantTerminationContributionRequest request = DestroyRequest();
        var lifecycle = OpenLifecycle(3) with
        {
            Destroy = (_, _) => Task.FromResult(
                new NotificationScopeDestroyResult(
                    NotificationScopeDestroyStatus.Completed,
                    null,
                    Receipt(Guid.NewGuid(), 4, 1)))
        };

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(TenantTerminationContributionStatus.Failed, result.Status);
        Assert.Equal(
            "operations-notifications.termination.destroy-response-invalid",
            result.ResultCode);
    }

    [Fact]
    public async Task Destroy_refuses_a_lifecycle_response_at_the_attempt_deadline()
    {
        TenantTerminationContributionRequest request = DestroyRequest();
        var clock = new MutableClock(Now);
        var lifecycle = OpenLifecycle(3) with
        {
            Destroy = (_, _) =>
            {
                clock.UtcNow = request.DeadlineUtc;
                return Task.FromResult(new NotificationScopeDestroyResult(
                    NotificationScopeDestroyStatus.Completed,
                    null,
                    Receipt(request.IdempotencyKey, 4, 1)));
            }
        };

        await Assert.ThrowsAsync<TimeoutException>(() => CreateContributor(
                lifecycle,
                clock: clock)
            .ExecuteAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Destroy_retries_when_the_frozen_fence_changes_during_the_lifecycle_call()
    {
        TenantTerminationContributionRequest request = DestroyRequest();
        var lifecycle = OpenLifecycle(3) with
        {
            Destroy = (_, _) => Task.FromResult(
                new NotificationScopeDestroyResult(
                    NotificationScopeDestroyStatus.Completed,
                    null,
                    Receipt(request.IdempotencyKey, 4, 1)))
        };
        var fences = new SequenceFenceReader(
            Fence(),
            Fence() with { Version = 6 });

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle,
                fences)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "operations-notifications.termination.destroy-fence-changed",
            result.ResultCode);
        Assert.Equal(2, fences.ReadCount);
    }

    [Fact]
    public async Task Destroy_requires_the_matching_frozen_fence()
    {
        bool selected = false;
        var lifecycle = new TestLifecycle
        {
            Snapshot = (_, _) =>
            {
                selected = true;
                throw new InvalidOperationException();
            }
        };
        var fence = new TestFenceReader(new WorkspaceTerminationFenceSnapshot(
            Guid.NewGuid(),
            TerminationEpoch,
            WorkspaceTerminationFenceState.Frozen,
            5));

        TenantTerminationContributionResult result = await CreateContributor(
                lifecycle,
                fence)
            .ExecuteAsync(DestroyRequest(), CancellationToken.None);

        Assert.False(selected);
        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "operations-notifications.termination.destroy-fence-unavailable",
            result.ResultCode);
    }

    private static OperationsNotificationsTenantTerminationContributor
        CreateContributor(
            TestLifecycle lifecycle,
            IWorkspaceTerminationFenceReader? fence = null,
            ISystemClock? clock = null) =>
        new(
            lifecycle,
            new TestScopeContext(),
            clock ?? new FixedClock(),
            fence ?? new TestFenceReader(Fence()));

    private static TestLifecycle OpenLifecycle(long revision) =>
        new()
        {
            Snapshot = (_, _) => Task.FromResult(new NotificationScopeSnapshot(
                NotificationScopeStatus.Open,
                revision)),
            Export = EmptyPage
        };

    private static Task<NotificationScopeExportPage> EmptyPage(
        NotificationScopeExportRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new NotificationScopeExportPage(
            NotificationScopeExportStatus.Completed,
            request.ExpectedRevision,
            request.Store,
            [],
            request.AfterCursor,
            false));

    private static TenantTerminationExportRequest Request() =>
        new(
            new TenantTerminationContributionRequest(
                TenantTerminationContract.CurrentVersion,
                TenantId,
                ProcessId,
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                ApprovalRevision: 2,
                OperationRevision: 4,
                TerminationEpoch,
                TenantTerminationContributionPhase.Export,
                Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                new string('a', 64),
                "system:tenant-termination",
                Now.AddHours(1)),
            FreezeOperationRevision: 3,
            WorkspaceFenceRevision: 5,
            new string('b', 64),
            Now.AddMinutes(-1));

    private static TenantTerminationContributionRequest DestroyRequest() =>
        Request().Contribution with
        {
            Phase = TenantTerminationContributionPhase.Destroy
        };

    private static WorkspaceTerminationFenceSnapshot Fence() =>
        new(
            ProcessId,
            TerminationEpoch,
            WorkspaceTerminationFenceState.Frozen,
            5);

    private static NotificationScopeDestroyReceipt Receipt(
        Guid operationId,
        long resultingRevision,
        long removedCount) =>
        new(
            operationId,
            resultingRevision,
            NotificationScopeLifecycleLimits.MaximumDestroyBatchSize,
            removedCount,
            CompletedBatchCount: removedCount == 0 ? 0 : 3,
            RemovalProofVersion: 1,
            new string('b', 64),
            Now.AddMinutes(-3),
            Now.AddMinutes(-1));

    private static NotificationScopeExportRecord RecordFor(
        NotificationScopeExportStore store)
    {
        Guid id = RecordId(store);
        var provider = new NotificationDeliveryProviderCode("web");
        return store switch
        {
            NotificationScopeExportStore.UserNotifications =>
                new NotificationScopeUserNotificationExportRecord(
                    id,
                    "staff-subject",
                    "reservations",
                    "reservation-confirmed",
                    1,
                    "Reservation confirmed",
                    "A reservation was confirmed.",
                    NotificationSeverity.Success,
                    1,
                    Now.AddMinutes(-5),
                    Now.AddMinutes(-4),
                    null,
                    JsonSerializer.SerializeToElement(new { reservation = 1 }),
                    NotificationDeliveryPolicy.RespectPreferences,
                    true,
                    ["domain:reservations"],
                    [HistoryReference]),
            NotificationScopeExportStore.Preferences =>
                new NotificationScopePreferenceExportRecord(
                    id,
                    "staff-subject",
                    "domain:reservations",
                    true,
                    1,
                    Now),
            NotificationScopeExportStore.DeliveryRoutes =>
                new NotificationScopeDeliveryRouteExportRecord(
                    id,
                    "delivery:web",
                    provider,
                    true,
                    1,
                    Now,
                    "system:seed"),
            NotificationScopeExportStore.TagDefinitions =>
                new NotificationScopeTagDefinitionExportRecord(
                    id,
                    "domain:reservations",
                    NotificationTagKind.Domain,
                    "Reservations",
                    "Reservation activity",
                    NotificationTagOrigin.Module,
                    "reservations",
                    true,
                    1,
                    Now.AddMinutes(-1),
                    Now,
                    "system:seed",
                    "system:seed"),
            NotificationScopeExportStore.Deliveries =>
                new NotificationScopeDeliveryExportRecord(
                    id,
                    RecordId(NotificationScopeExportStore.UserNotifications),
                    "delivery:web",
                    provider,
                    NotificationDeliveryStatus.Pending,
                    0,
                    3,
                    Now,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    Guid.Parse("11111111-1111-1111-1111-111111111111")),
            NotificationScopeExportStore.DeliveryAttempts =>
                new NotificationScopeDeliveryAttemptExportRecord(
                    id,
                    RecordId(NotificationScopeExportStore.Deliveries),
                    1,
                    provider,
                    NotificationDeliveryAttemptOutcome.Delivered,
                    Now.AddMinutes(-1),
                    Now,
                    "accepted",
                    "provider-message"),
            NotificationScopeExportStore.TenantBroadcasts =>
                new NotificationScopeBroadcastExportRecord(
                    id,
                    NotificationBroadcastAudience.TenantUsers,
                    "administration",
                    "workspace-announcement",
                    1,
                    "Maintenance window",
                    null,
                    NotificationSeverity.Info,
                    2,
                    Now.AddMinutes(-2),
                    Now.AddMinutes(-1),
                    JsonSerializer.SerializeToElement(new { window = 1 })),
            NotificationScopeExportStore.TenantBroadcastReads =>
                new NotificationScopeBroadcastReadExportRecord(
                    id,
                    RecordId(NotificationScopeExportStore.TenantBroadcasts),
                    NotificationBroadcastRecipientKind.User,
                    "staff-subject",
                    Now),
            NotificationScopeExportStore.HistoryReferenceStates =>
                new NotificationScopeHistoryReferenceStateExportRecord(
                    HistoryReference,
                    2,
                    false,
                    null,
                    null,
                    null),
            NotificationScopeExportStore.HistoryCloseReceipts =>
                new NotificationScopeHistoryCloseReceiptExportRecord(
                    id,
                    HistoryReference,
                    new string('c', 64),
                    3,
                    2,
                    new string('d', 64),
                    Now),
            NotificationScopeExportStore.HistoryBatchCloseOperations =>
                new NotificationScopeHistoryBatchCloseOperationExportRecord(
                    id,
                    HistoryReference,
                    new string('e', 64),
                    2,
                    3,
                    NotificationHistoryLifecycleLimits.MaximumCloseBatchSize,
                    2,
                    1,
                    1,
                    new string('f', 64),
                    Now.AddMinutes(-1),
                    Now),
            NotificationScopeExportStore.HistoryBatchCloseReceipts =>
                new NotificationScopeHistoryBatchCloseReceiptExportRecord(
                    id,
                    HistoryReference,
                    new string('1', 64),
                    3,
                    2,
                    1,
                    1,
                    new string('2', 64),
                    Now.AddMinutes(-1),
                    Now),
            _ => throw new ArgumentOutOfRangeException(nameof(store), store, null)
        };
    }

    private static Guid RecordId(NotificationScopeExportStore store) =>
        Guid.Parse($"00000000-0000-0000-0000-{(int)store:D12}");

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class FixedClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class TestFenceReader(
        WorkspaceTerminationFenceSnapshot? snapshot)
        : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class SequenceFenceReader(
        params WorkspaceTerminationFenceSnapshot?[] snapshots)
        : IWorkspaceTerminationFenceReader
    {
        private int index;

        public int ReadCount => this.index;

        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            int selected = Math.Min(this.index, snapshots.Length - 1);
            this.index++;
            return Task.FromResult(snapshots[selected]);
        }
    }

    private sealed class CapturingSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record TestLifecycle : INotificationScopeLifecycle
    {
        public Func<
            string,
            CancellationToken,
            Task<NotificationScopeSnapshot>> Snapshot
        { get; init; } =
            (_, _) => Task.FromResult(new NotificationScopeSnapshot(
                NotificationScopeStatus.Missing,
                0));

        public Func<
            NotificationScopeExportRequest,
            CancellationToken,
            Task<NotificationScopeExportPage>> Export
        { get; init; } = EmptyPage;

        public Func<
            NotificationScopeDestroyRequest,
            CancellationToken,
            Task<NotificationScopeDestroyResult>> Destroy
        { get; init; } =
            (_, _) => Task.FromResult(new NotificationScopeDestroyResult(
                NotificationScopeDestroyStatus.Invalid,
                null,
                null));

        public Task<NotificationScopeSnapshot> GetSnapshotAsync(
            string scopeId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, scopeId);
            return this.Snapshot(scopeId, cancellationToken);
        }

        public Task<NotificationScopeExportPage> ExportAsync(
            NotificationScopeExportRequest request,
            CancellationToken cancellationToken) =>
            this.Export(request, cancellationToken);

        public Task<NotificationScopeDestroyResult> DestroyBatchAsync(
            NotificationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            this.Destroy(request, cancellationToken);
    }
}

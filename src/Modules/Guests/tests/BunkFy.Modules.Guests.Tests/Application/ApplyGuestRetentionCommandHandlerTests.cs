namespace BunkFy.Modules.Guests.Tests.Application;

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Contributors;
using BunkFy.Modules.Guests.Application.Handlers;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ApplyGuestRetentionCommandHandlerTests
{
    [Fact]
    public async Task Due_guest_commits_profile_and_both_proofs_once()
    {
        Fixture fixture = CreateFixture("tenant-a");
        ApplyGuestRetentionCommand command = new(
            fixture.Execution.Id,
            fixture.Execution.Attempt,
            fixture.Profile.Id,
            fixture.Profile.Version);

        Result<GuestRetentionMutationResult> first =
            await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None);
        Result<GuestRetentionMutationResult> replay =
            await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.Equal(
            GuestRetentionMutationStatus.Applied,
            first.Value.Status);
        Assert.Equal(
            GuestRetentionMutationStatus.AlreadyApplied,
            replay.Value.Status);
        Assert.Equal(GuestProfileState.Anonymised, fixture.Profile.Status);
        Assert.Empty(fixture.Profile.LanguageTags);
        Assert.Equal(1, fixture.Execution.AffectedCount);
        Assert.Equal(1, fixture.Repository.AddProofCount);
        Assert.Equal(1, fixture.Candidates.LoadCount);
        Assert.Equal(1, fixture.Boundary.CallCount);
        Assert.Equal([fixture.Profile.Id], fixture.ManagementOperations.DeletedGuestIds);
        Assert.NotNull(fixture.Repository.Receipt);
        Assert.Equal(2, fixture.Repository.Receipt.ContractVersion);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            fixture.Repository.Receipt.TimeZoneCatalogVersion);
        Assert.NotNull(fixture.Repository.Tombstone);
        Assert.True(fixture.Repository.Tombstone.MatchesRetention(
            fixture.Repository.Receipt));
    }

    [Fact]
    public async Task Cross_tenant_execution_is_rejected_before_discovery()
    {
        Fixture fixture = CreateFixture("tenant-b");

        Result<GuestRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    fixture.Execution.Attempt,
                    fixture.Profile.Id,
                    fixture.Profile.Version),
                CancellationToken.None);

        Assert.Equal(
            GuestsApplicationErrors.RetentionExecutionNotFound,
            result.Error);
        Assert.Equal(0, fixture.Candidates.LoadCount);
        Assert.Equal(0, fixture.Boundary.CallCount);
        Assert.Equal(GuestProfileState.Active, fixture.Profile.Status);
    }

    [Fact]
    public async Task Stale_attempt_is_rejected_before_owner_work()
    {
        Fixture fixture = CreateFixture("tenant-a");
        Assert.True(fixture.Execution.BeginRetry(
            attempt: 2,
            GuestRetentionTestData.Now,
            GuestRetentionTestData.Now.AddMinutes(11)).IsSuccess);

        Result<GuestRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    Attempt: 1,
                    fixture.Profile.Id,
                    fixture.Profile.Version),
                CancellationToken.None);

        Assert.Equal(
            GuestsApplicationErrors.RetentionExecutionNotFound,
            result.Error);
        Assert.Equal(0, fixture.Repository.ReceiptLookupCount);
        Assert.Equal(0, fixture.Candidates.LoadCount);
        Assert.Equal(0, fixture.Boundary.CallCount);
        Assert.Equal(GuestProfileState.Active, fixture.Profile.Status);
    }

    [Fact]
    public async Task Active_hold_observed_under_lock_blocks_mutation()
    {
        Fixture fixture = CreateFixture("tenant-a");
        GuestRetentionCandidateSnapshot source =
            fixture.Candidates.Snapshot;
        fixture.Candidates.Snapshot = source with
        {
            ActiveHolds =
            [
                new(
                    source.OriginPropertyId,
                    GuestRetentionTestData.Now.AddDays(-2))
            ]
        };

        Result<GuestRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    fixture.Execution.Attempt,
                    fixture.Profile.Id,
                    fixture.Profile.Version),
                CancellationToken.None);

        Assert.Equal(
            GuestRetentionMutationStatus.Blocked,
            result.Value.Status);
        Assert.Equal(GuestProfileState.Active, fixture.Profile.Status);
        Assert.Equal(0, fixture.Execution.AffectedCount);
        Assert.Equal(0, fixture.Repository.AddProofCount);
        Assert.Equal(1, fixture.Boundary.CallCount);
    }

    [Fact]
    public async Task Time_zone_drift_under_lock_fails_distinctly()
    {
        Fixture fixture = CreateFixture("tenant-a");
        GuestRetentionCandidateSnapshot source =
            fixture.Candidates.Snapshot;
        GuestRetentionPropertySnapshot property =
            Assert.Single(source.Properties);
        fixture.Candidates.Snapshot = source with
        {
            Properties =
            [
                property with
                {
                    TimeZoneId = "UTC",
                    CanonicalTimeZoneId = "Etc/UTC",
                    TimeZoneStatus = PropertyTimeZoneStatus.Alias,
                    TimeZoneCatalogVersion =
                        TimeZoneCatalog.Default.CatalogVersion
                }
            ]
        };

        Result<GuestRetentionMutationResult> result =
            await fixture.Handler.HandleAsync(
                new(
                    fixture.Execution.Id,
                    fixture.Execution.Attempt,
                    fixture.Profile.Id,
                    fixture.Profile.Version),
                CancellationToken.None);

        Assert.Equal(
            GuestRetentionMutationStatus.Failed,
            result.Value.Status);
        Assert.Equal(
            GuestRetentionMutationFailure.TimeZoneUnavailable,
            result.Value.Failure);
        Assert.Equal(GuestProfileState.Active, fixture.Profile.Status);
        Assert.Equal(0, fixture.Repository.AddProofCount);
        Assert.Equal(1, fixture.Boundary.CallCount);
    }

    [Fact]
    public async Task Completed_v2_execution_replays_immutable_proof()
    {
        Fixture fixture = CreateFixture("tenant-a");
        ApplyGuestRetentionCommand command = new(
            fixture.Execution.Id,
            fixture.Execution.Attempt,
            fixture.Profile.Id,
            fixture.Profile.Version);

        Assert.Equal(
            GuestRetentionMutationStatus.Applied,
            (await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None)).Value.Status);
        Assert.True(fixture.Execution.Complete(
            GuestRetentionExecutionState.Completed,
            attempt: fixture.Execution.Attempt,
            scannedCount: 1,
            remainingCount: 0,
            GuestRetentionCoordinates.CompletedOutcome,
            GuestRetentionTestData.Now,
            holdReviewDueAtUtc: null).IsSuccess);

        Result<GuestRetentionMutationResult> replay =
            await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.Equal(
            GuestRetentionMutationStatus.AlreadyApplied,
            replay.Value.Status);
        Assert.Equal(1, fixture.Candidates.LoadCount);
        Assert.Equal(1, fixture.Boundary.CallCount);
    }

    [Fact]
    public async Task Terminal_v1_execution_replays_original_receipt_bytes()
    {
        Fixture fixture = CreateFixture("tenant-a");
        ApplyGuestRetentionCommand command = new(
            fixture.Execution.Id,
            fixture.Execution.Attempt,
            fixture.Profile.Id,
            fixture.Profile.Version);
        Assert.Equal(
            GuestRetentionMutationStatus.Applied,
            (await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None)).Value.Status);
        GuestRetentionAnonymisationReceipt receipt =
            fixture.Repository.Receipt!;
        Set(receipt, nameof(receipt.ContractVersion), 1);
        Set(receipt, nameof(receipt.TimeZoneCatalogVersion), null);
        string versionOneDigest =
            ComputeVersionOneCanonicalSha256(receipt);
        Set(
            receipt,
            nameof(receipt.CanonicalSha256),
            versionOneDigest);
        Set(
            fixture.Repository.Tombstone!,
            nameof(GuestAnonymisationTombstone.OwnerReceiptSha256),
            versionOneDigest);
        Set(
            fixture.Execution,
            nameof(fixture.Execution.ExecutionPolicyVersion),
            1);
        Assert.True(fixture.Execution.Complete(
            GuestRetentionExecutionState.Completed,
            attempt: fixture.Execution.Attempt,
            scannedCount: 1,
            remainingCount: 0,
            GuestRetentionCoordinates.CompletedOutcome,
            GuestRetentionTestData.Now,
            holdReviewDueAtUtc: null).IsSuccess);

        Result<GuestRetentionMutationResult> replay =
            await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.Equal(
            GuestRetentionMutationStatus.AlreadyApplied,
            replay.Value.Status);
        Assert.True(
            fixture.Repository.Tombstone!.MatchesRetention(receipt));
        Assert.Equal(1, fixture.Candidates.LoadCount);
        Assert.Equal(1, fixture.Boundary.CallCount);
    }

    private static Fixture CreateFixture(string activeScopeId)
    {
        GuestRetentionPolicyFixture policy =
            GuestRetentionTestData.CreatePolicy();
        GuestProfile profile = GuestProfile.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "Maya Chen",
            "Maya Q. Chen",
            "maya@example.test",
            "+44 20 1234 5678",
            new DateOnly(1990, 2, 3),
            "GB",
            "en-GB",
            "Prefers a lower bunk.",
            "user:creator",
            Guid.NewGuid(),
            GuestRetentionTestData.Now.AddYears(-2),
            languageTags: ["en-GB", "fr", "unknown-١"]).Value;
        profile.ClearDomainEvents();
        GuestRetentionExecution execution =
            GuestRetentionExecution.Start(
                Guid.NewGuid(),
                profile.ScopeId,
                GuestRetentionCoordinates.DataClassKey,
                GuestRetentionCoordinates.ExecutionPolicyVersion,
                attempt: 1,
                startingProjectionOrdinal: 0,
                GuestRetentionTestData.Now.AddMinutes(-1),
                GuestRetentionTestData.Now.AddMinutes(10)).Value;
        RecordingExecutionRepository repository = new(
            execution,
            profile);
        RecordingCandidateRepository candidates = new(
            GuestRetentionTestData.Snapshot(
                policy.Binding,
                GuestStayStatus.CheckedOut,
                new DateOnly(2025, 1, 1),
                guestVersion: profile.Version,
                guestId: profile.Id,
                propertyId: profile.OriginPropertyId));
        RecordingBoundary boundary = new();
        RecordingManagementOperationRepository managementOperations = new();
        ApplyGuestRetentionCommandHandler handler = new(
            repository,
            candidates,
            managementOperations,
            boundary,
            new GuestRetentionEligibilityEvaluator(policy.Registry),
            new TestScopeContext(activeScopeId),
            new TestClock(),
            new QueueIdGenerator(
                Guid.NewGuid(),
                Guid.NewGuid()));
        return new(
            handler,
            repository,
            candidates,
            managementOperations,
            boundary,
            execution,
            profile);
    }

    private sealed record Fixture(
        ApplyGuestRetentionCommandHandler Handler,
        RecordingExecutionRepository Repository,
        RecordingCandidateRepository Candidates,
        RecordingManagementOperationRepository ManagementOperations,
        RecordingBoundary Boundary,
        GuestRetentionExecution Execution,
        GuestProfile Profile);

    private sealed class RecordingManagementOperationRepository
        : IGuestManagementOperationRepository
    {
        public List<Guid> DeletedGuestIds { get; } = [];

        public Task<GuestManagementOperationRecord?> GetAsync(
            Guid guestId,
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddAsync(
            GuestManagementOperationRecord operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteForGuestAsync(
            Guid guestId,
            CancellationToken cancellationToken)
        {
            this.DeletedGuestIds.Add(guestId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutionRepository(
        GuestRetentionExecution execution,
        GuestProfile profile)
        : IGuestRetentionExecutionRepository
    {
        public GuestRetentionAnonymisationReceipt? Receipt
        {
            get;
            private set;
        }

        public GuestAnonymisationTombstone? Tombstone
        {
            get;
            private set;
        }

        public int AddProofCount { get; private set; }
        public int ReceiptLookupCount { get; private set; }

        public Task<GuestRetentionExecution?> GetExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                execution.Id == executionId ? execution : null);

        public Task AddExecutionAsync(
            GuestRetentionExecution added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestRetentionSweepCheckpoint?> GetCheckpointAsync(
            string dataClassKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddCheckpointAsync(
            GuestRetentionSweepCheckpoint checkpoint,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestRetentionAnonymisationReceipt?> GetReceiptAsync(
            Guid guestId,
            CancellationToken cancellationToken)
        {
            this.ReceiptLookupCount++;
            return Task.FromResult(
                this.Receipt?.GuestId == guestId
                    ? this.Receipt
                    : null);
        }

        public Task<GuestAnonymisationTombstone?> GetTombstoneAsync(
            Guid guestId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Tombstone?.Id == guestId
                    ? this.Tombstone
                    : null);

        public Task<GuestProfile?> GetProfileAsync(
            Guid guestId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                profile.Id == guestId ? profile : null);

        public Task AddAnonymisationProofAsync(
            GuestRetentionAnonymisationReceipt receipt,
            GuestAnonymisationTombstone tombstone,
            CancellationToken cancellationToken)
        {
            this.Receipt = receipt;
            this.Tombstone = tombstone;
            this.AddProofCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCandidateRepository(
        GuestRetentionCandidateSnapshot snapshot)
        : IGuestRetentionCandidateRepository
    {
        public GuestRetentionCandidateSnapshot Snapshot { get; set; } =
            snapshot;

        public int LoadCount { get; private set; }

        public Task<GuestRetentionScanPage> ScanAsync(
            long afterProjectionOrdinal,
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestRetentionCandidateSnapshot?> LoadAsync(
            Guid guestId,
            CancellationToken cancellationToken)
        {
            this.LoadCount++;
            return Task.FromResult(
                this.Snapshot.GuestId == guestId
                    ? this.Snapshot
                    : null);
        }
    }

    private sealed class RecordingBoundary
        : IGuestAnonymisationExecutionBoundary
    {
        public int CallCount { get; private set; }

        public Task AcquireAsync(
            string tenantId,
            Guid guestId,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => GuestRetentionTestData.Now;
    }

    private sealed class QueueIdGenerator(params Guid[] values)
        : IIdGenerator
    {
        private readonly Queue<Guid> values = new(values);

        public Guid NewId() => this.values.Dequeue();
    }

    private static string ComputeVersionOneCanonicalSha256(
        GuestRetentionAnonymisationReceipt receipt)
    {
        StringBuilder canonical = new();
        Append(canonical, "1");
        Append(canonical, receipt.Id.ToString("N"));
        Append(canonical, receipt.ScopeId);
        Append(canonical, receipt.ExecutionId.ToString("N"));
        Append(canonical, receipt.GuestId.ToString("N"));
        Append(
            canonical,
            receipt.SelectedGuestVersion.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            receipt.ResultingGuestVersion.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            receipt.AffectedPropertyCount.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            receipt.RetentionDeadlineUtc.ToString(
                "O",
                CultureInfo.InvariantCulture));
        Append(canonical, receipt.PolicySetSha256);
        Append(canonical, receipt.EventId.ToString("N"));
        Append(canonical, receipt.ActorId);
        Append(
            canonical,
            receipt.CompletedAtUtc.ToString(
                "O",
                CultureInfo.InvariantCulture));
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(
            value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }

    private static void Set(
        object target,
        string propertyName,
        object? value) =>
        target.GetType()
            .GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(target, value);
}

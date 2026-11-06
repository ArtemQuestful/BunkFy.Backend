namespace BunkFy.Modules.Ingestion.Tests.Application;

using System.Text;
using BunkFy.DataGovernance;
using BunkFy.Adapter.Abstractions;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using BunkFy.Modules.Ingestion.Application;
using BunkFy.Modules.Ingestion.Application.Adapters;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reprocessing;
using BunkFy.Modules.Ingestion.Domain.Runs;
using BunkFy.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReceiveObservationCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Accepted_observation_stores_raw_payload_and_receipt()
    {
        TestContext context = CreateContext();
        ReceiveObservationCommand command = CreateCommand(context.Connection.Id);

        Result<AdapterObservationResult> result = await context.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AdapterObservationDisposition.Accepted, result.Value.Disposition);
        Assert.Single(context.Receipts.Items);
        ObservationCountryPolicyEvidence evidence = Assert.IsType<ObservationCountryPolicyEvidence>(
            context.Receipts.Items[0].CountryPolicyEvidence);
        Assert.Equal("GB", evidence.OperatingCountryCode);
        Assert.Equal("gb-hostel", evidence.PolicyId);
        Assert.Equal("reservation-ingestion", evidence.PurposeCode);
        Assert.Equal("adapter-ingress", evidence.ProcessingSurface);
        Assert.Equal("approved-adapter", evidence.SourceProvenance);
        Assert.Equal(Now, evidence.EvaluatedAtUtc);
        ObservationAdapterProvenance provenance = Assert.IsType<ObservationAdapterProvenance>(
            context.Receipts.Items[0].AdapterProvenance);
        Assert.Null(provenance.CredentialId);
        Assert.Equal("fake.http", provenance.AdapterType);
        Assert.Equal(1, provenance.AdapterProtocolVersion);
        Assert.Equal(1, provenance.ConfigurationSchemaVersion);
        Assert.Equal("fake.http", provenance.SourceSystem);
        Assert.Null(provenance.CustomerOwner);
        Assert.Single(context.RawPayloads.Writes);
        Assert.Equal(result.Value.ReceiptId, context.RawPayloads.Writes[0].PayloadId);
        Assert.IsType<ObservationReceiptAcceptedIntegrationEvent>(Assert.Single(context.Outbox.Events));
    }

    [Fact]
    public async Task Same_source_revision_is_duplicate_without_second_payload_write()
    {
        TestContext context = CreateContext();
        ReceiveObservationCommand first = CreateCommand(context.Connection.Id);
        ReceiveObservationCommand retry = first with { OperationId = Guid.NewGuid() };

        Result<AdapterObservationResult> accepted = await context.Handler.HandleAsync(first, CancellationToken.None);
        Result<AdapterObservationResult> duplicate = await context.Handler.HandleAsync(retry, CancellationToken.None);

        Assert.True(duplicate.IsSuccess);
        Assert.Equal(AdapterObservationDisposition.Duplicate, duplicate.Value.Disposition);
        Assert.Equal(accepted.Value.ReceiptId, duplicate.Value.ReceiptId);
        Assert.Single(context.RawPayloads.Writes);
        Assert.Single(context.Receipts.Items);
        Assert.Single(context.Outbox.Events);
    }

    [Fact]
    public async Task Public_ingress_replay_checks_policy_without_consuming_quota_twice()
    {
        TestContext context = CreateContext();
        ReceiveObservationCommand first = CreateCommand(context.Connection.Id) with
        {
            IngressProvenance = new AdapterIngressProvenance(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "fake.http",
                1,
                1,
                "fake.http",
                "user:owner")
        };
        ReceiveObservationCommand retry = first with { OperationId = Guid.NewGuid() };

        Result<AdapterObservationResult> accepted = await context.Handler.HandleAsync(
            first,
            CancellationToken.None);
        Result<AdapterObservationResult> duplicate = await context.Handler.HandleAsync(
            retry,
            CancellationToken.None);

        Assert.True(accepted.IsSuccess);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(AdapterObservationDisposition.Duplicate, duplicate.Value.Disposition);
        Assert.Collection(
            context.IngressGate.Requests,
            request => Assert.True(request.ConsumeQuota),
            request => Assert.False(request.ConsumeQuota));
        Assert.Single(context.RawPayloads.Writes);
        Assert.Single(context.Receipts.Items);
    }

    [Fact]
    public async Task Reused_operation_id_with_different_content_is_rejected()
    {
        TestContext context = CreateContext();
        ReceiveObservationCommand first = CreateCommand(context.Connection.Id);
        byte[] changedPayload = Encoding.UTF8.GetBytes("{\"revision\":2}");
        ReceiveObservationCommand conflict = first with
        {
            Payload = changedPayload,
            ContentSha256 = AdapterPayloadHash.ComputeSha256(changedPayload)
        };

        await context.Handler.HandleAsync(first, CancellationToken.None);
        Result<AdapterObservationResult> result = await context.Handler.HandleAsync(conflict, CancellationToken.None);

        Assert.Equal(IngestionApplicationErrors.OperationIdentityConflict, result.Error);
        Assert.Single(context.RawPayloads.Writes);
    }

    [Fact]
    public async Task Observation_cannot_use_another_connections_run()
    {
        IngestionRun run = IngestionRun.Start(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            null,
            Now).Value;
        TestContext context = CreateContext(run);
        ReceiveObservationCommand command = CreateCommand(context.Connection.Id) with { RunId = run.Id };

        Result<AdapterObservationResult> result = await context.Handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(IngestionApplicationErrors.RunConnectionMismatch, result.Error);
        Assert.Empty(context.RawPayloads.Writes);
    }

    [Fact]
    public async Task Run_fence_precedes_the_source_graph_lock()
    {
        List<string> calls = [];
        TestContext context = CreateContext(
            executionLock: new OrderedExecutionLock(calls),
            sourceLock: new OrderedSourceLock(calls));
        IngestionRun run = IngestionRun.Start(
            Guid.NewGuid(),
            "tenant-a",
            context.Connection.Id,
            context.Connection.PropertyId,
            Guid.NewGuid(),
            1,
            null,
            Now).Value;
        context.Runs.Run = run;

        Result<AdapterObservationResult> result = await context.Handler
            .HandleAsync(
                CreateCommand(context.Connection.Id) with
                {
                    RunId = run.Id
                },
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            ["connection-read-lock", "run-read-lock", "source-lock"],
            calls);
    }

    [Fact]
    public async Task Payload_hash_is_verified_again_at_the_application_boundary()
    {
        TestContext context = CreateContext();
        ReceiveObservationCommand command = CreateCommand(context.Connection.Id) with
        {
            ContentSha256 = new string('0', AdapterProtocolLimits.Sha256Length)
        };

        Result<AdapterObservationResult> result = await context.Handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(IngestionApplicationErrors.PayloadHashMismatch, result.Error);
        Assert.Empty(context.RawPayloads.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Partial_remote_execution_proof_is_rejected_before_acquiring_a_lock(
        bool includeLease)
    {
        TestContext context = CreateContext(executionLock: new ThrowingExecutionLock());
        ReceiveObservationCommand command = CreateCommand(context.Connection.Id) with
        {
            RemoteLease = includeLease
                ? new AdapterRemoteLeaseProof(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    1,
                    Guid.NewGuid())
                : null,
            RemoteCredentialId = includeLease ? null : Guid.NewGuid()
        };

        Result<AdapterObservationResult> result = await context.Handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.Equal(IngestionApplicationErrors.ObservationInvalid, result.Error);
        Assert.Empty(context.RawPayloads.Writes);
    }

    [Fact]
    public async Task Retired_property_rejects_observation_before_writing_ingestion_state()
    {
        TestContext context = CreateContext(propertyActive: false);

        Result<AdapterObservationResult> result = await context.Handler.HandleAsync(
            CreateCommand(context.Connection.Id),
            CancellationToken.None);

        Assert.Equal(
            IngestionApplicationErrors.CountryPolicyDenied(CountryPolicyDecisionReason.MissingBinding),
            result.Error);
        Assert.Empty(context.Receipts.Items);
        Assert.Empty(context.RawPayloads.Writes);
        Assert.Empty(context.Outbox.Events);
    }

    [Fact]
    public async Task Tombstoned_direct_observation_is_denied_before_storage()
    {
        TestContext context = CreateContext(anonymisationBlocked: true);

        Result<AdapterObservationResult> result = await context.Handler
            .HandleAsync(
                CreateCommand(context.Connection.Id),
                CancellationToken.None);

        Assert.Equal(
            IngestionApplicationErrors.AnonymisationBarrierActive,
            result.Error);
        Assert.Empty(context.Receipts.Items);
        Assert.Empty(context.RawPayloads.Writes);
        Assert.Empty(context.Outbox.Events);
    }

    [Fact]
    public async Task Tombstoned_reprocessed_observation_is_denied_before_storage()
    {
        TestContext context = CreateContext(anonymisationBlocked: true);
        ObservationReceipt source = CreateRejectedSource(context.Connection);
        context.Receipts.Items.Add(source);
        Guid attemptId = Guid.NewGuid();
        ObservationReprocessingAttempt attempt =
            ObservationReprocessingAttempt.Create(
                attemptId,
                "tenant-a",
                context.Connection.PropertyId,
                context.Connection.Id,
                source.Id,
                attemptId,
                "reservation-mail",
                1,
                "staff:42",
                Now.AddMinutes(-3),
                Now.AddHours(2))
            .Value;
        Assert.True(attempt.Start(
            attemptId,
            taskAttempt: 1,
            Now.AddMinutes(-2),
            Now.AddHours(2)).IsSuccess);
        context.ReprocessingAttempts.Items.Add(attempt);
        ReceiveObservationCommand command =
            CreateCommand(context.Connection.Id) with
            {
                SourceReceiptId = source.Id,
                ReprocessingAttemptId = attempt.Id,
                ParserType = attempt.ParserType,
                ParserVersion = attempt.ParserVersion,
                ParserOutputIndex = 0
            };

        Result<AdapterObservationResult> result = await context.Handler
            .HandleAsync(command, CancellationToken.None);

        Assert.Equal(
            IngestionApplicationErrors.AnonymisationBarrierActive,
            result.Error);
        Assert.Single(context.Receipts.Items);
        Assert.Empty(context.RawPayloads.Writes);
        Assert.Empty(context.Outbox.Events);
    }

    private static TestContext CreateContext(
        IngestionRun? run = null,
        bool propertyActive = true,
        bool anonymisationBlocked = false,
        IIngestionExecutionLock? executionLock = null,
        IIngestionSourceOperationLock? sourceLock = null)
    {
        AdapterConnection connection = AdapterConnection.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "fake.http",
            AdapterExecutionMode.Polling,
            IngestionConflictPolicy.AutoApplyWhenAdapterBaselineUnchanged,
            "config/fake-http",
            null,
            Now).Value;
        FakeReceiptRepository receipts = new();
        FakeReprocessingAttemptRepository reprocessingAttempts = new();
        FakeRawPayloadStore rawPayloads = new();
        RecordingOutbox outbox = new();
        RecordingIngressGate ingressGate = new();
        FakeRunRepository runs = new(run);
        ServiceCollection services = new();
        if (executionLock is not null)
        {
            services.AddSingleton(executionLock);
        }

        services.AddSingleton<IAdapterConnectionRepository>(new FakeConnectionRepository(connection));
        services.AddSingleton<IAdapterDescriptorRegistry>(new TestDescriptorRegistry());
        services.AddSingleton<IIngestionCountryPolicyAdmission>(
            new TestCountryPolicyAdmission(allowed: propertyActive));
        services.AddSingleton<IIngestionRunRepository>(runs);
        services.AddSingleton<IObservationReceiptRepository>(receipts);
        services.AddSingleton<IObservationReprocessingAttemptRepository>(
            reprocessingAttempts);
        services.AddSingleton<IRawPayloadStore>(rawPayloads);
        services.AddSingleton<IIngestionRetentionPolicy>(new TestRetentionPolicy());
        services.AddSingleton<IOutboxWriterRegistry>(new RecordingOutboxRegistry(outbox));
        services.AddSingleton<IScopeContext>(new TestScopeContext());
        services.AddSingleton<ISystemClock>(new TestClock());
        services.AddSingleton<IIdGenerator>(new TestIdGenerator());
        services.AddSingleton<IAdapterIngressGate>(ingressGate);
        if (anonymisationBlocked)
        {
            services.AddBlockingAnonymisationBarrier(sourceLock);
        }
        else
        {
            services.AddAllowingAnonymisationBarrier(sourceLock);
        }
        services.AddIngestionApplication();
        ServiceProvider provider = services.BuildServiceProvider();
        return new(
            connection,
            receipts,
            reprocessingAttempts,
            rawPayloads,
            outbox,
            ingressGate,
            runs,
            provider.GetRequiredService<ICommandHandler<ReceiveObservationCommand, AdapterObservationResult>>());
    }

    private static ReceiveObservationCommand CreateCommand(Guid connectionId)
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"revision\":1}");
        return new(
            connectionId,
            RunId: null,
            Guid.NewGuid(),
            "reservation.changed",
            "booking-123",
            "1",
            Now.AddMinutes(-2),
            Now.AddMinutes(-1),
            "application/json",
            payload,
            AdapterPayloadHash.ComputeSha256(payload));
    }

    private static ObservationReceipt CreateRejectedSource(
        AdapterConnection connection)
    {
        Guid receiptId = Guid.NewGuid();
        byte[] payload = Encoding.UTF8.GetBytes("{\"unsupported\":true}");
        ObservationReceipt receipt = ObservationReceipt.Create(
            receiptId,
            "tenant-a",
            connection.PropertyId,
            connection.Id,
            runId: null,
            Guid.NewGuid(),
            "reservation-mail",
            "mail-42",
            sourceRevision: null,
            ObservationIdentity.CreateDeduplicationKey(
                "reservation-mail",
                "mail-42",
                sourceRevision: null,
                AdapterPayloadHash.ComputeSha256(payload)),
            AdapterPayloadHash.ComputeSha256(payload),
            TestObservationCountryPolicyEvidence.Create(Now.AddMinutes(-5)),
            receiptId,
            Now.AddDays(30),
            Now.AddMinutes(-5),
            Now.AddMinutes(-5),
            Now.AddMinutes(-5))
        .Value;
        Assert.True(receipt.Reject(
            "unsupported",
            Now.AddMinutes(-4)).IsSuccess);
        return receipt;
    }

    private sealed record TestContext(
        AdapterConnection Connection,
        FakeReceiptRepository Receipts,
        FakeReprocessingAttemptRepository ReprocessingAttempts,
        FakeRawPayloadStore RawPayloads,
        RecordingOutbox Outbox,
        RecordingIngressGate IngressGate,
        FakeRunRepository Runs,
        ICommandHandler<ReceiveObservationCommand, AdapterObservationResult> Handler);

    private sealed class RecordingIngressGate : IAdapterIngressGate
    {
        public List<IngressGateRequest> Requests { get; } = [];

        public ValueTask<AdapterIngressGateDecision> AdmitAsync(
            AdapterIngressIdentity identity,
            AdapterIngressOperation operation,
            int permitCount,
            bool consumeQuota,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(new(identity, operation, permitCount, consumeQuota));
            return ValueTask.FromResult(AdapterIngressGateDecision.Allowed());
        }
    }

    private sealed record IngressGateRequest(
        AdapterIngressIdentity Identity,
        AdapterIngressOperation Operation,
        int PermitCount,
        bool ConsumeQuota);

    private sealed class FakeConnectionRepository(AdapterConnection connection) : IAdapterConnectionRepository
    {
        public Task<AdapterConnection?> GetAsync(Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<AdapterConnection?>(connectionId == connection.Id ? connection : null);

        public Task<AdapterConnection?> GetAsync(Guid propertyId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<AdapterConnection?>(propertyId == connection.PropertyId && connectionId == connection.Id ? connection : null);

        public Task AddAsync(AdapterConnection added, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRunRepository(IngestionRun? run) : IIngestionRunRepository
    {
        public IngestionRun? Run { get; set; } = run;

        public Task<IngestionRun?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Run?.Id == runId ? this.Run : null);

        public Task<IngestionRun?> FindByTaskExecutionAsync(
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Run?.TaskRunId == taskRunId &&
                this.Run.TaskAttempt == taskAttempt ? this.Run : null);

        public Task<Guid?> FindByTaskExecutionIdAsync(
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(
                this.Run?.TaskRunId == taskRunId &&
                this.Run.TaskAttempt == taskAttempt
                    ? this.Run.Id
                    : null);

        public Task<IngestionRun?> FindActiveByConnectionAsync(
            Guid connectionId,
            CancellationToken cancellationToken) => Task.FromResult(
                this.Run is not null &&
                this.Run.ConnectionId == connectionId &&
                this.Run.State == IngestionRunState.Running
                    ? this.Run
                    : null);

        public Task<Guid?> FindActiveIdByConnectionAsync(
            Guid connectionId,
            CancellationToken cancellationToken) => Task.FromResult<Guid?>(
                this.Run is not null &&
                this.Run.ConnectionId == connectionId &&
                this.Run.State == IngestionRunState.Running
                    ? this.Run.Id
                    : null);

        public Task AddAsync(IngestionRun added, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReceiptRepository : IObservationReceiptRepository
    {
        public List<ObservationReceipt> Items { get; } = [];

        public Task<ObservationReceipt?> GetAsync(Guid receiptId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.FirstOrDefault(receipt => receipt.Id == receiptId));

        public Task<ObservationReceipt?> FindByOperationAsync(
            Guid connectionId,
            Guid operationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.FirstOrDefault(receipt =>
                receipt.ConnectionId == connectionId && receipt.OperationId == operationId));

        public Task<ObservationReceipt?> FindByDeduplicationKeyAsync(
            Guid connectionId,
            string deduplicationKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.FirstOrDefault(receipt =>
                receipt.ConnectionId == connectionId && receipt.DeduplicationKey == deduplicationKey));

        public Task AddAsync(ObservationReceipt receipt, CancellationToken cancellationToken)
        {
            this.Items.Add(receipt);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReprocessingAttemptRepository : IObservationReprocessingAttemptRepository
    {
        public List<ObservationReprocessingAttempt> Items { get; } = [];

        public Task<ObservationReprocessingAttempt?> GetAsync(
            Guid attemptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(
                attempt => attempt.Id == attemptId));

        public Task<ObservationReprocessingAttempt?> FindActiveBySourceAsync(
            Guid sourceReceiptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(
                attempt =>
                    attempt.SourceReceiptId == sourceReceiptId &&
                    attempt.State is ObservationReprocessingState.Queued or
                        ObservationReprocessingState.Running));

        public Task AddAsync(
            ObservationReprocessingAttempt attempt,
            CancellationToken cancellationToken)
        {
            this.Items.Add(attempt);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRawPayloadStore : IRawPayloadStore
    {
        public List<RawPayloadWrite> Writes { get; } = [];

        public Task StoreAsync(RawPayloadWrite write, CancellationToken cancellationToken)
        {
            this.Writes.Add(write);
            return Task.CompletedTask;
        }

        public Task<RawPayloadRead?> ReadAsync(
            Guid payloadId,
            string scopeId,
            Guid connectionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid payloadId,
            string scopeId,
            Guid connectionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestRetentionPolicy : IIngestionRetentionPolicy
    {
        public DateTimeOffset GetRawPayloadRetainUntilUtc(
            Guid propertyId,
            Guid connectionId,
            DateTimeOffset receivedAtUtc) => receivedAtUtc.AddDays(30);

        public DateTimeOffset GetSensitiveHistoryRetainUntilUtc(
            Guid propertyId,
            Guid connectionId,
            DateTimeOffset terminalAtUtc) => terminalAtUtc.AddDays(90);

        public DateTimeOffset GetLegalHoldReviewDueAtUtc(
            DateTimeOffset placedAtUtc) => placedAtUtc.AddDays(30);
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public string ModuleName => IngestionModuleMetadata.Name;
        public List<IIntegrationEvent> Events { get; } = [];

        public Task EnqueueAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutboxRegistry(RecordingOutbox outbox) : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName) => outbox;
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class ThrowingExecutionLock : IIngestionExecutionLock
    {
        public Task AcquireTaskExecutionAsync(
            string tenantId,
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireRetentionExecutionAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireConnectionReadAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireConnectionWriteAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireRunReadAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireRunWriteAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken) => Unexpected();

        private static Task Unexpected() =>
            throw new InvalidOperationException("Execution lock must not be acquired.");
    }

    private sealed class OrderedExecutionLock(List<string> calls)
        : IIngestionExecutionLock
    {
        public Task AcquireTaskExecutionAsync(
            string tenantId,
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireRetentionExecutionAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireConnectionReadAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken)
        {
            calls.Add("connection-read-lock");
            return Task.CompletedTask;
        }

        public Task AcquireConnectionWriteAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireRunReadAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken)
        {
            calls.Add("run-read-lock");
            return Task.CompletedTask;
        }

        public Task AcquireRunWriteAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken) => Unexpected();

        private static Task Unexpected() =>
            throw new InvalidOperationException("Unexpected execution lock acquisition.");
    }

    private sealed class OrderedSourceLock(List<string> calls)
        : IIngestionSourceOperationLock
    {
        public Task AcquireAsync(
            string tenantId,
            Guid sourceLinkId,
            CancellationToken cancellationToken)
        {
            calls.Add("source-lock");
            return Task.CompletedTask;
        }
    }

    private sealed class TestDescriptorRegistry : IAdapterDescriptorRegistry
    {
        private static readonly AdapterDescriptor Descriptor = new(
            "fake.http",
            1,
            1,
            [AdapterExecutionMode.Polling]);

        public IReadOnlyCollection<AdapterDescriptor> GetAll() => [Descriptor];

        public bool TryGet(string adapterType, out AdapterDescriptor? descriptor)
        {
            descriptor = string.Equals(adapterType, Descriptor.AdapterType, StringComparison.Ordinal)
                ? Descriptor
                : null;
            return descriptor is not null;
        }
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
}

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
using Gma.Framework.Messaging;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RequestDataRightsExportCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approved_staff_export_creates_one_tenant_artifact_and_event()
    {
        DataRightsCase dataRightsCase = ApprovedStaffCase();
        RecordingArtifactRepository artifacts = new();
        RecordingOutbox outbox = new();
        Guid artifactId = Guid.NewGuid();
        RequestDataRightsExportCommandHandler handler = Handler(
            dataRightsCase,
            artifacts,
            outbox,
            artifactId);
        Guid idempotencyKey = Guid.NewGuid();

        Result<DataRightsExportArtifactDto> result = await handler.HandleAsync(
            new RequestDataRightsExportCommand(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                idempotencyKey,
                dataRightsCase.Version,
                "user:privacy"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(artifactId, result.Value.Id);
        Assert.Equal(DataRightsCaseType.StaffRights, result.Value.CaseType);
        Assert.Null(result.Value.PropertyId);
        Assert.Equal(Now.AddHours(24), result.Value.ExpiresAtUtc);
        Assert.Same(artifacts.Added, artifacts.ByCase);
        DataRightsExportArtifactRequestedIntegrationEvent requested =
            Assert.IsType<DataRightsExportArtifactRequestedIntegrationEvent>(
                Assert.Single(outbox.Events));
        Assert.Equal(artifactId, requested.ArtifactId);
        Assert.Equal(dataRightsCase.DecisionRevision, requested.DecisionRevision);
        Assert.Equal(Now.AddHours(24), requested.ExpiresAtUtc);
    }

    [Fact]
    public async Task Equivalent_retry_returns_existing_artifact_without_new_event()
    {
        DataRightsCase dataRightsCase = ApprovedStaffCase();
        RecordingArtifactRepository artifacts = new();
        RecordingOutbox outbox = new();
        RequestDataRightsExportCommandHandler handler = Handler(
            dataRightsCase,
            artifacts,
            outbox,
            Guid.NewGuid());
        RequestDataRightsExportCommand command = new(
            DataRightsCaseScope.Staff,
            dataRightsCase.Id,
            Guid.NewGuid(),
            dataRightsCase.Version,
            "user:privacy");

        Result<DataRightsExportArtifactDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<DataRightsExportArtifactDto> retry = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(retry.IsSuccess);
        Assert.Equal(first.Value.Id, retry.Value.Id);
        Assert.Single(outbox.Events);
    }

    [Fact]
    public async Task Creation_replay_after_failure_does_not_enqueue_retry()
    {
        DataRightsCase dataRightsCase = ApprovedStaffCase();
        RecordingArtifactRepository artifacts = new();
        RecordingOutbox outbox = new();
        RequestDataRightsExportCommandHandler handler = Handler(
            dataRightsCase,
            artifacts,
            outbox,
            Guid.NewGuid());
        RequestDataRightsExportCommand command = new(
            DataRightsCaseScope.Staff,
            dataRightsCase.Id,
            Guid.NewGuid(),
            dataRightsCase.Version,
            "user:privacy");
        _ = await handler.HandleAsync(command, CancellationToken.None);
        DataRightsExportArtifact artifact = artifacts.Added!;
        Guid runId = Guid.NewGuid();
        Assert.True(artifact.BeginGeneration(
            runId,
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(artifact.MarkFailed(
            runId,
            attempt: 1,
            "owner-export-failed",
            Now.AddMinutes(2)).IsSuccess);

        Result<DataRightsExportArtifactDto> retry = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(retry.IsSuccess);
        Assert.Equal(DataRightsExportArtifactStatus.Failed, retry.Value.Status);
        Assert.Single(outbox.Events);
    }

    [Fact]
    public async Task Fresh_creation_key_cannot_bypass_explicit_retry_endpoint()
    {
        DataRightsCase dataRightsCase = ApprovedStaffCase();
        RecordingArtifactRepository artifacts = new();
        RecordingOutbox outbox = new();
        RequestDataRightsExportCommandHandler handler = Handler(
            dataRightsCase,
            artifacts,
            outbox,
            Guid.NewGuid());
        RequestDataRightsExportCommand firstCommand = new(
            DataRightsCaseScope.Staff,
            dataRightsCase.Id,
            Guid.NewGuid(),
            dataRightsCase.Version,
            "user:privacy");
        _ = await handler.HandleAsync(firstCommand, CancellationToken.None);
        DataRightsExportArtifact artifact = artifacts.Added!;
        Guid runId = Guid.NewGuid();
        Assert.True(artifact.BeginGeneration(
            runId,
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(artifact.MarkFailed(
            runId,
            attempt: 1,
            "owner-export-failed",
            Now.AddMinutes(2)).IsSuccess);

        Result<DataRightsExportArtifactDto> retry = await handler.HandleAsync(
            firstCommand with { IdempotencyKey = Guid.NewGuid() },
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.ExportArtifactAlreadyRequested.Code,
            retry.Error.Code);
        Assert.Single(outbox.Events);
    }

    [Fact]
    public async Task Missing_unique_owner_contributor_fails_before_persistence()
    {
        DataRightsCase dataRightsCase = ApprovedStaffCase();
        RecordingArtifactRepository artifacts = new();
        RecordingOutbox outbox = new();
        RequestDataRightsExportCommandHandler handler = Handler(
            dataRightsCase,
            artifacts,
            outbox,
            Guid.NewGuid(),
            contributors: []);

        Result<DataRightsExportArtifactDto> result = await handler.HandleAsync(
            new RequestDataRightsExportCommand(
                DataRightsCaseScope.Staff,
                dataRightsCase.Id,
                Guid.NewGuid(),
                dataRightsCase.Version,
                "user:privacy"),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.ExportOwnerUnavailable.Code,
            result.Error.Code);
        Assert.Null(artifacts.Added);
        Assert.Empty(outbox.Events);
    }

    private static RequestDataRightsExportCommandHandler Handler(
        DataRightsCase dataRightsCase,
        RecordingArtifactRepository artifacts,
        RecordingOutbox outbox,
        Guid artifactId,
        IReadOnlyCollection<IDataRightsSubjectExportContributor>? contributors =
            null) =>
        new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            artifacts,
            contributors ?? [new StaffExportContributor()],
            new TestPolicy(),
            new RecordingAuditSink(),
            new OutboxRegistry(outbox),
            new TestScopeContext(),
            new TestClock(),
            new SequenceIdGenerator(
                artifactId,
                Guid.NewGuid(),
                Guid.NewGuid()));

    private static DataRightsCase ApprovedStaffCase()
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
            "user:privacy",
            Now.AddMinutes(-5)).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-4)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "staff",
            "profile",
            Guid.NewGuid(),
            recordVersion: 3,
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-3)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-2)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(-1)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            Now).IsSuccess);
        return dataRightsCase;
    }

    private sealed class StubCaseRepository(DataRightsCase dataRightsCase)
        : IDataRightsCaseRepository
    {
        public Task AddAsync(
            DataRightsCase value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DataRightsCase?>(
                caseId == dataRightsCase.Id ? dataRightsCase : null);

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingArtifactRepository
        : IDataRightsExportArtifactRepository
    {
        public DataRightsExportArtifact? Added { get; private set; }
        public DataRightsExportArtifact? ByCase => this.Added;

        public Task AddAsync(
            DataRightsExportArtifact artifact,
            CancellationToken cancellationToken)
        {
            this.Added = artifact;
            return Task.CompletedTask;
        }

        public Task<DataRightsExportArtifact?> GetAsync(
            DataRightsCaseScope scope,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DataRightsExportArtifact?>(null);

        public Task<DataRightsExportArtifact?> GetByCaseAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Added?.CaseId == caseId ? this.Added : null);

        public Task<DataRightsExportArtifact?> GetByIdempotencyKeyAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Added?.IdempotencyKey == idempotencyKey
                    ? this.Added
                    : null);
    }

    private sealed class StaffExportContributor
        : IDataRightsSubjectExportContributor
    {
        public string OwnerKey => "staff";
        public IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes =>
            [DataRightsCaseType.StaffRights];
        public DataRightsExportDescriptor Descriptor => new(
            "staff",
            "staff.catalog",
            1,
            1,
            "staff.export",
            1,
            ["name"]);

        public Task<DataRightsSubjectExportResult> ExportAsync(
            DataRightsSubjectExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestPolicy : IDataRightsExportArtifactPolicy
    {
        public DateTimeOffset ExpiresAt(DateTimeOffset requestedAtUtc) =>
            requestedAtUtc.AddHours(24);
    }

    private sealed class RecordingAuditSink : IDataRightsExportAuditSink
    {
        public Task RecordAsync(
            DataRightsExportAuditFact fact,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
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

    private sealed class SequenceIdGenerator(params Guid[] ids) : IIdGenerator
    {
        private readonly Queue<Guid> ids = new(ids);

        public Guid NewId() => this.ids.Dequeue();
    }

    private sealed class OutboxRegistry(RecordingOutbox outbox)
        : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName)
        {
            Assert.Equal(DataRightsModuleMetadata.Name, moduleName);
            return outbox;
        }
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public string ModuleName => DataRightsModuleMetadata.Name;
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
}

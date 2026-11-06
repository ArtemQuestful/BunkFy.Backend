namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Validation;
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
public sealed class RetryDataRightsExportCommandHandlerTests
{
    private static readonly DateTimeOffset DecisionAt =
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = DecisionAt.AddMinutes(3);

    [Fact]
    public void Retry_command_requires_exact_case_and_artifact_versions()
    {
        string[] errors = new RetryDataRightsExportCommandValidator()
            .Validate(new RetryDataRightsExportCommand(
                DataRightsCaseScope.Staff,
                Guid.Empty,
                Guid.Empty,
                ExpectedCaseVersion: 0,
                ExpectedArtifactVersion: 0,
                ActorId: string.Empty))
            .ToArray();

        Assert.Contains("Scope and CaseId are required.", errors);
        Assert.Contains("ExpectedVersion must be greater than zero.", errors);
        Assert.Contains("ArtifactId is required.", errors);
        Assert.Contains("ExpectedArtifactVersion must be positive.", errors);
        Assert.Contains(
            "ActorId is required and must be within the supported limit.",
            errors);
    }

    [Fact]
    public async Task Failed_artifact_retry_is_version_pinned_and_enqueued_once()
    {
        TestFixture fixture = Fixture(failed: true);
        long failedVersion = fixture.Artifact.Version;
        RetryDataRightsExportCommand command = fixture.Command(failedVersion);

        Result<DataRightsExportArtifactDto> result =
            await fixture.Handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DataRightsExportArtifactStatus.Requested, result.Value.Status);
        Assert.Equal(failedVersion + 1, result.Value.Version);
        Assert.Equal(failedVersion, fixture.Artifact.LastRetryBaseVersion);
        Assert.Null(fixture.Artifact.GenerationRunId);
        Assert.Null(fixture.Artifact.GenerationStartedAtUtc);
        Assert.Null(fixture.Artifact.FailureCode);
        DataRightsExportArtifactRequestedIntegrationEvent requested =
            Assert.IsType<DataRightsExportArtifactRequestedIntegrationEvent>(
                Assert.Single(fixture.Outbox.Events));
        Assert.Equal(fixture.Artifact.Id, requested.ArtifactId);
        DataRightsExportAuditFact audit = Assert.Single(fixture.Audit.Facts);
        Assert.Equal(DataRightsExportAuditAction.GenerationRequested, audit.Action);
        Assert.Equal("retry-requested", audit.OutcomeCode);
        Assert.Equal("user:privacy", audit.ActorId);
    }

    [Fact]
    public async Task Same_failed_version_replay_returns_current_artifact_without_side_effects()
    {
        TestFixture fixture = Fixture(failed: true);
        long failedVersion = fixture.Artifact.Version;
        RetryDataRightsExportCommand command = fixture.Command(failedVersion);

        Result<DataRightsExportArtifactDto> first =
            await fixture.Handler.HandleAsync(command, CancellationToken.None);
        Result<DataRightsExportArtifactDto> replay =
            await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Single(fixture.Outbox.Events);
        Assert.Single(fixture.Audit.Facts);
    }

    [Fact]
    public async Task Stale_artifact_version_fails_without_enqueueing()
    {
        TestFixture fixture = Fixture(failed: true);

        Result<DataRightsExportArtifactDto> result =
            await fixture.Handler.HandleAsync(
                fixture.Command(fixture.Artifact.Version - 1),
                CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.ExportArtifactVersionConflict.Code,
            result.Error.Code);
        Assert.Empty(fixture.Outbox.Events);
        Assert.Empty(fixture.Audit.Facts);
        Assert.Equal(DataRightsExportArtifactState.Failed, fixture.Artifact.State);
    }

    [Fact]
    public async Task Active_artifact_cannot_be_retried()
    {
        TestFixture fixture = Fixture(failed: false);

        Result<DataRightsExportArtifactDto> result =
            await fixture.Handler.HandleAsync(
                fixture.Command(fixture.Artifact.Version),
                CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.ExportArtifactTransitionInvalid.Code,
            result.Error.Code);
        Assert.Empty(fixture.Outbox.Events);
        Assert.Empty(fixture.Audit.Facts);
    }

    [Fact]
    public async Task Expired_failed_artifact_cannot_be_retried()
    {
        TestFixture fixture = Fixture(
            failed: true,
            expiresAtUtc: DecisionAt.AddMinutes(2).AddSeconds(30));

        Result<DataRightsExportArtifactDto> result =
            await fixture.Handler.HandleAsync(
                fixture.Command(fixture.Artifact.Version),
                CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.ExportArtifactExpired.Code,
            result.Error.Code);
        Assert.Empty(fixture.Outbox.Events);
        Assert.Empty(fixture.Audit.Facts);
    }

    private static TestFixture Fixture(
        bool failed,
        DateTimeOffset? expiresAtUtc = null)
    {
        DataRightsCase dataRightsCase = ApprovedStaffCase();
        DataRightsSubjectCoordinate[] subjects =
            RequestDataRightsExportCommandHandler.ToCoordinates(dataRightsCase);
        DataRightsExportArtifact artifact = DataRightsExportArtifact.Request(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            dataRightsCase.Id,
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            dataRightsCase.DecisionRevision!.Value,
            subjects.Length,
            DataRightsExportIdentity.SelectionSha256(subjects),
            "user:privacy",
            DecisionAt,
            expiresAtUtc ?? DecisionAt.AddHours(24)).Value;
        if (failed)
        {
            Guid runId = Guid.NewGuid();
            Assert.True(artifact.BeginGeneration(
                runId,
                attempt: 5,
                "system:data-rights-export",
                DecisionAt.AddMinutes(1)).IsSuccess);
            Assert.True(artifact.MarkFailed(
                runId,
                attempt: 5,
                "owner-retry-required",
                DecisionAt.AddMinutes(2)).IsSuccess);
        }

        RecordingOutbox outbox = new();
        RecordingAuditSink audit = new();
        RetryDataRightsExportCommandHandler handler = new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            new StubArtifactRepository(artifact),
            [new StaffExportContributor()],
            audit,
            new OutboxRegistry(outbox),
            new TestScopeContext(),
            new TestClock(),
            new FixedIdGenerator(Guid.NewGuid()));
        return new(dataRightsCase, artifact, handler, outbox, audit);
    }

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
            DecisionAt.AddMinutes(-5)).Value;
        _ = dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy",
            DecisionAt.AddMinutes(-4));
        _ = dataRightsCase.SelectSubject(
            "staff",
            "profile",
            Guid.NewGuid(),
            recordVersion: 3,
            dataRightsCase.Version,
            "user:privacy",
            DecisionAt.AddMinutes(-3));
        _ = dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy",
            DecisionAt.AddMinutes(-2));
        _ = dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            DecisionAt.AddMinutes(-1));
        _ = dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            DecisionAt);
        return dataRightsCase;
    }

    private sealed record TestFixture(
        DataRightsCase Case,
        DataRightsExportArtifact Artifact,
        RetryDataRightsExportCommandHandler Handler,
        RecordingOutbox Outbox,
        RecordingAuditSink Audit)
    {
        public RetryDataRightsExportCommand Command(long artifactVersion) =>
            new(
                DataRightsCaseScope.Staff,
                this.Case.Id,
                this.Artifact.Id,
                this.Case.Version,
                artifactVersion,
                "user:privacy");
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

    private sealed class StubArtifactRepository(DataRightsExportArtifact artifact)
        : IDataRightsExportArtifactRepository
    {
        public Task AddAsync(
            DataRightsExportArtifact value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsExportArtifact?> GetAsync(
            DataRightsCaseScope scope,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DataRightsExportArtifact?>(
                artifactId == artifact.Id ? artifact : null);

        public Task<DataRightsExportArtifact?> GetByCaseAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsExportArtifact?> GetByIdempotencyKeyAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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

    private sealed class RecordingAuditSink : IDataRightsExportAuditSink
    {
        public List<DataRightsExportAuditFact> Facts { get; } = [];

        public Task RecordAsync(
            DataRightsExportAuditFact fact,
            CancellationToken cancellationToken)
        {
            this.Facts.Add(fact);
            return Task.CompletedTask;
        }
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

    private sealed class FixedIdGenerator(Guid id) : IIdGenerator
    {
        public Guid NewId() => id;
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

namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DeleteExpiredTenantTerminationExportTaskHandlerTests
{
    private const string TenantId =
        "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset ExpiresAtUtc =
        new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Artifact_task_checkpoints_deletes_and_completes_in_order()
    {
        List<string> events = [];
        Guid processId = Guid.NewGuid();
        Guid artifactId = Guid.NewGuid();
        RecordingTaskDispatcher dispatcher = new(events);
        RecordingObjectStore objectStore = new(events);
        DeleteExpiredTenantTerminationExportArtifactTaskHandler handler = new(
            dispatcher,
            objectStore,
            new FixedScopeContext(TenantId));
        DeleteExpiredTenantTerminationExportArtifactPayload payload = new(
            TenantId,
            processId,
            artifactId,
            ExportOperationRevision: 9,
            ExpiresAtUtc);
        TaskExecutionContext context = Context(
            processId,
            artifactId,
            artifact: true);

        await handler.HandleAsync(payload, context, CancellationToken.None);

        Assert.Equal(["begin", "delete-artifact", "complete"], events);
        BeginTenantTerminationExportArtifactDeletionCommand started =
            Assert.IsType<
                BeginTenantTerminationExportArtifactDeletionCommand>(
                    dispatcher.Started);
        CompleteTenantTerminationExportArtifactDeletionCommand completed =
            Assert.IsType<
                CompleteTenantTerminationExportArtifactDeletionCommand>(
                    dispatcher.Completed);
        Assert.Equal(context.RunId, started.RunId);
        Assert.Equal(context.RunId, completed.RunId);
        Assert.Equal(artifactId, objectStore.DeletedArtifactId);
    }

    [Fact]
    public async Task Missing_fragment_object_is_an_idempotent_success()
    {
        List<string> events = [];
        Guid processId = Guid.NewGuid();
        Guid fragmentId = Guid.NewGuid();
        RecordingTaskDispatcher dispatcher = new(events);
        RecordingObjectStore objectStore = new(events, deleteResult: false);
        DeleteExpiredTenantTerminationExportFragmentTaskHandler handler = new(
            dispatcher,
            objectStore,
            new FixedScopeContext(TenantId));
        DeleteExpiredTenantTerminationExportFragmentPayload payload = new(
            TenantId,
            processId,
            fragmentId,
            ExportOperationRevision: 9,
            ExpiresAtUtc);

        await handler.HandleAsync(
            payload,
            Context(processId, fragmentId, artifact: false),
            CancellationToken.None);

        Assert.Equal(["begin", "delete-fragment", "complete"], events);
        Assert.Equal(fragmentId, objectStore.DeletedFragmentId);
        Assert.IsType<
            CompleteTenantTerminationExportFragmentDeletionCommand>(
                dispatcher.Completed);
    }

    [Fact]
    public async Task Late_cancellation_after_artifact_delete_stops_and_retry_converges()
    {
        List<string> events = [];
        using CancellationTokenSource source = new();
        Guid processId = Guid.NewGuid();
        Guid artifactId = Guid.NewGuid();
        RecordingTaskDispatcher dispatcher = new(events);
        RecordingObjectStore objectStore = new(
            events,
            afterDelete: source.Cancel);
        DeleteExpiredTenantTerminationExportArtifactTaskHandler handler = new(
            dispatcher,
            objectStore,
            new FixedScopeContext(TenantId));
        DeleteExpiredTenantTerminationExportArtifactPayload payload = new(
            TenantId,
            processId,
            artifactId,
            ExportOperationRevision: 9,
            ExpiresAtUtc);
        TaskExecutionContext context = Context(
            processId,
            artifactId,
            artifact: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(payload, context, source.Token));

        Assert.Equal(["begin", "delete-artifact"], events);
        Assert.Null(dispatcher.Completed);

        await handler.HandleAsync(
            payload,
            context,
            CancellationToken.None);

        Assert.Equal(
            [
                "begin",
                "delete-artifact",
                "begin",
                "delete-artifact",
                "complete"
            ],
            events);
        Assert.NotNull(dispatcher.Completed);
    }

    [Fact]
    public async Task Task_rejects_scope_mismatch_before_any_mutation()
    {
        List<string> events = [];
        Guid processId = Guid.NewGuid();
        Guid artifactId = Guid.NewGuid();
        RecordingTaskDispatcher dispatcher = new(events);
        RecordingObjectStore objectStore = new(events);
        DeleteExpiredTenantTerminationExportArtifactTaskHandler handler = new(
            dispatcher,
            objectStore,
            new FixedScopeContext(
                "22222222-2222-2222-2222-222222222222"));
        DeleteExpiredTenantTerminationExportArtifactPayload payload = new(
            TenantId,
            processId,
            artifactId,
            ExportOperationRevision: 9,
            ExpiresAtUtc);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    payload,
                    Context(processId, artifactId, artifact: true),
                    CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationExecutionBoundaryInvalid",
            failure.Message);
        Assert.Empty(events);
        Assert.Null(dispatcher.Started);
    }

    [Fact]
    public async Task Task_rejects_a_tenant_scoped_lease_before_any_mutation()
    {
        List<string> events = [];
        Guid processId = Guid.NewGuid();
        Guid artifactId = Guid.NewGuid();
        RecordingTaskDispatcher dispatcher = new(events);
        RecordingObjectStore objectStore = new(events);
        DeleteExpiredTenantTerminationExportArtifactTaskHandler handler = new(
            dispatcher,
            objectStore,
            new FixedScopeContext(TenantId));
        DeleteExpiredTenantTerminationExportArtifactPayload payload = new(
            TenantId,
            processId,
            artifactId,
            ExportOperationRevision: 9,
            ExpiresAtUtc);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    payload,
                    Context(
                        processId,
                        artifactId,
                        artifact: true,
                        scopeId: TenantId),
                    CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationExecutionBoundaryInvalid",
            failure.Message);
        Assert.Empty(events);
        Assert.Null(dispatcher.Started);
    }

    private static TaskExecutionContext Context(
        Guid processId,
        Guid objectId,
        bool artifact,
        string? scopeId = null)
    {
        Guid runId = artifact
            ? TenantTerminationExecutionIdentity
                .CreateExportArtifactCleanupTaskRunId(objectId)
            : TenantTerminationExecutionIdentity
                .CreateExportFragmentCleanupTaskRunId(objectId);
        string taskName = artifact
            ? DeleteExpiredTenantTerminationExportArtifactPayload.TaskName
            : DeleteExpiredTenantTerminationExportFragmentPayload.TaskName;
        return new(
            runId,
            DataRightsModuleMetadata.Name,
            taskName,
            DataRightsModuleMetadata.TenantTerminationWorkerGroup,
            "worker-1",
            "node-1",
            attempt: 1,
            scopeId,
            correlationId: processId,
            payloadVersion: 1);
    }

    private sealed class RecordingTaskDispatcher(
        List<string> events,
        bool deletionRequired = true) : ITaskCommandDispatcher
    {
        public object? Started { get; private set; }
        public object? Completed { get; private set; }

        public Task<Result<TResponse>> DispatchAsync<TCommand, TResponse>(
            TaskExecutionContext context,
            TCommand command,
            CancellationToken cancellationToken)
            where TCommand : ICommand<TResponse>
        {
            object result = command switch
            {
                BeginTenantTerminationExportArtifactDeletionCommand started =>
                    this.Start(started, started.ArtifactId),
                BeginTenantTerminationExportFragmentDeletionCommand started =>
                    this.Start(started, started.FragmentId),
                CompleteTenantTerminationExportArtifactDeletionCommand completed =>
                    this.Complete(completed),
                CompleteTenantTerminationExportFragmentDeletionCommand completed =>
                    this.Complete(completed),
                _ => throw new InvalidOperationException(
                    $"Unexpected command {command.GetType().Name}.")
            };
            return Task.FromResult((Result<TResponse>)result);
        }

        private Result<TenantTerminationExportObjectDeletionStart> Start(
            object command,
            Guid objectId)
        {
            this.Started = command;
            events.Add("begin");
            return Result.Success(
                new TenantTerminationExportObjectDeletionStart(
                    deletionRequired,
                    objectId));
        }

        private Result<Unit> Complete(object command)
        {
            this.Completed = command;
            events.Add("complete");
            return Result.Success(Unit.Value);
        }
    }

    private sealed class RecordingObjectStore(
        List<string> events,
        bool deleteResult = true,
        Action? afterDelete = null) : ITenantTerminationExportObjectStore
    {
        public Guid? DeletedArtifactId { get; private set; }
        public Guid? DeletedFragmentId { get; private set; }

        public Task<bool> DeleteArtifactAsync(
            Guid processId,
            Guid artifactId,
            CancellationToken cancellationToken)
        {
            this.DeletedArtifactId = artifactId;
            events.Add("delete-artifact");
            afterDelete?.Invoke();
            return Task.FromResult(deleteResult);
        }

        public Task<bool> DeleteFragmentAsync(
            Guid processId,
            Guid fragmentId,
            CancellationToken cancellationToken)
        {
            this.DeletedFragmentId = fragmentId;
            events.Add("delete-fragment");
            afterDelete?.Invoke();
            return Task.FromResult(deleteResult);
        }
    }

    private sealed class FixedScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}

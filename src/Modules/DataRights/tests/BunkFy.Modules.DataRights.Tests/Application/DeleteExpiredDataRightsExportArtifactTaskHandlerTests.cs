namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DeleteExpiredDataRightsExportArtifactTaskHandlerTests
{
    [Fact]
    public async Task Task_checkpoints_then_deletes_then_completes()
    {
        Guid artifactId = Guid.NewGuid();
        FakeTaskDispatcher dispatcher = new(artifactId);
        RecordingObjectStore objectStore = new();
        DeleteExpiredDataRightsExportArtifactTaskHandler handler = new(
            dispatcher,
            objectStore);
        DeleteExpiredDataRightsExportArtifactPayload payload = Payload(
            artifactId);
        TaskExecutionContext context = Context();

        await handler.HandleAsync(
            payload,
            context,
            CancellationToken.None);

        Assert.Equal(artifactId, objectStore.DeletedArtifactId);
        BeginDataRightsExportDeletionCommand started =
            Assert.IsType<BeginDataRightsExportDeletionCommand>(
                dispatcher.Started);
        CompleteDataRightsExportDeletionCommand completed =
            Assert.IsType<CompleteDataRightsExportDeletionCommand>(
                dispatcher.Completed);
        Assert.Equal(context.RunId, started.RunId);
        Assert.Equal(context.RunId, completed.RunId);
    }

    [Fact]
    public async Task Missing_object_is_an_idempotent_success()
    {
        Guid artifactId = Guid.NewGuid();
        FakeTaskDispatcher dispatcher = new(artifactId);
        RecordingObjectStore objectStore = new(deleteResult: false);
        DeleteExpiredDataRightsExportArtifactTaskHandler handler = new(
            dispatcher,
            objectStore);

        await handler.HandleAsync(
            Payload(artifactId),
            Context(),
            CancellationToken.None);

        Assert.NotNull(dispatcher.Completed);
    }

    [Fact]
    public async Task Late_cancellation_after_delete_stops_completion_and_retry_converges()
    {
        Guid artifactId = Guid.NewGuid();
        FakeTaskDispatcher dispatcher = new(artifactId);
        using CancellationTokenSource source = new();
        RecordingObjectStore objectStore = new(afterDelete: source.Cancel);
        DeleteExpiredDataRightsExportArtifactTaskHandler handler = new(
            dispatcher,
            objectStore);
        DeleteExpiredDataRightsExportArtifactPayload payload = Payload(
            artifactId);
        TaskExecutionContext context = Context();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(payload, context, source.Token));

        Assert.NotNull(dispatcher.Started);
        Assert.Null(dispatcher.Completed);
        Assert.Equal(1, objectStore.DeleteCount);

        await handler.HandleAsync(
            payload,
            context,
            CancellationToken.None);

        Assert.NotNull(dispatcher.Completed);
        Assert.Equal(2, objectStore.DeleteCount);
    }

    private static DeleteExpiredDataRightsExportArtifactPayload Payload(
        Guid artifactId) =>
        new(
            artifactId,
            Guid.NewGuid(),
            DataRightsCaseType.StaffRights,
            PropertyId: null,
            DecisionRevision: 7,
            new DateTimeOffset(
                2026,
                7,
                28,
                12,
                0,
                0,
                TimeSpan.Zero));

    private static TaskExecutionContext Context() => new(
        Guid.NewGuid(),
        DataRightsModuleMetadata.Name,
        DeleteExpiredDataRightsExportArtifactPayload.TaskName,
        DataRightsModuleMetadata.ExportWorkerGroup,
        "worker-1",
        "node-1",
        attempt: 1,
        scopeId: "tenant-a",
        correlationId: Guid.NewGuid());

    private sealed class FakeTaskDispatcher(Guid artifactId)
        : ITaskCommandDispatcher
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
                BeginDataRightsExportDeletionCommand started =>
                    this.Start(started),
                CompleteDataRightsExportDeletionCommand completed =>
                    this.Complete(completed),
                _ => throw new InvalidOperationException(
                    $"Unexpected command {command.GetType().Name}.")
            };
            return Task.FromResult((Result<TResponse>)result);
        }

        private Result<DataRightsExportDeletionStart> Start(
            BeginDataRightsExportDeletionCommand command)
        {
            this.Started = command;
            return Result.Success(new DataRightsExportDeletionStart(
                DeletionRequired: true,
                artifactId));
        }

        private Result<Unit> Complete(
            CompleteDataRightsExportDeletionCommand command)
        {
            this.Completed = command;
            return Result.Success(Unit.Value);
        }
    }

    private sealed class RecordingObjectStore(
        bool deleteResult = true,
        Action? afterDelete = null)
        : IDataRightsExportArtifactObjectStore
    {
        public Guid? DeletedArtifactId { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<bool> DeleteAsync(
            Guid artifactId,
            CancellationToken cancellationToken)
        {
            this.DeletedArtifactId = artifactId;
            this.DeleteCount++;
            afterDelete?.Invoke();
            return Task.FromResult(deleteResult);
        }
    }
}

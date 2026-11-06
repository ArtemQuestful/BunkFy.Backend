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
public sealed class GenerateDataRightsExportTaskHandlerTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Task_generates_and_records_protected_artifact()
    {
        DataRightsExportGenerationStart start = Start(dispatchRequired: true);
        DataRightsProtectedExportArtifact generated = Protected();
        FakeTaskDispatcher dispatcher = new(start);
        RecordingGenerator generator = new(generated);
        GenerateDataRightsExportTaskHandler handler = new(
            dispatcher,
            generator,
            new RecordingObjectStore());
        TaskExecutionContext context = Context();

        await handler.HandleAsync(
            Payload(start),
            context,
            CancellationToken.None);

        Assert.NotNull(generator.Request);
        CompleteDataRightsExportGenerationCommand completed =
            Assert.IsType<CompleteDataRightsExportGenerationCommand>(
                dispatcher.Completed);
        Assert.Equal(context.RunId, completed.RunId);
        Assert.Equal(context.Attempt, completed.Attempt);
        Assert.Same(generated, completed.ProtectedArtifact);
        Assert.Null(dispatcher.Failed);
    }

    [Fact]
    public async Task Available_retry_does_not_generate_again()
    {
        DataRightsExportGenerationStart start = Start(dispatchRequired: false);
        FakeTaskDispatcher dispatcher = new(start);
        RecordingGenerator generator = new(Protected());
        GenerateDataRightsExportTaskHandler handler = new(
            dispatcher,
            generator,
            new RecordingObjectStore());

        await handler.HandleAsync(
            Payload(start),
            Context(),
            CancellationToken.None);

        Assert.Null(generator.Request);
        Assert.Null(dispatcher.Completed);
        Assert.Null(dispatcher.Failed);
    }

    [Fact]
    public async Task Terminal_generation_failure_records_code_and_stops_retrying()
    {
        DataRightsExportGenerationStart start = Start(dispatchRequired: true);
        FakeTaskDispatcher dispatcher = new(start);
        RecordingGenerator generator = new("subject-stale");
        RecordingObjectStore objectStore = new();
        GenerateDataRightsExportTaskHandler handler = new(
            dispatcher,
            generator,
            objectStore);

        TaskRunTerminalFailureException exception =
            await Assert.ThrowsAsync<TaskRunTerminalFailureException>(
                () => handler.HandleAsync(
                    Payload(start),
                    Context(attempt: 1, maxAttempts: 5),
                    CancellationToken.None));

        Assert.Equal(
            "data-rights.export:subject-stale",
            exception.FailureCode);
        FailDataRightsExportGenerationCommand failed =
            Assert.IsType<FailDataRightsExportGenerationCommand>(
                dispatcher.Failed);
        Assert.Equal("subject-stale", failed.FailureCode);
        Assert.Null(dispatcher.Completed);
        Assert.Equal(start.ArtifactId, objectStore.DeletedArtifactId);
    }

    [Fact]
    public async Task Retryable_generation_failure_stays_active_before_final_attempt()
    {
        DataRightsExportGenerationStart start = Start(dispatchRequired: true);
        FakeTaskDispatcher dispatcher = new(start);
        RecordingObjectStore objectStore = new();
        GenerateDataRightsExportTaskHandler handler = new(
            dispatcher,
            new RecordingGenerator(
                "owner-retry-required",
                DataRightsExportFailureDisposition.Retryable),
            objectStore);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => handler.HandleAsync(
                    Payload(start),
                    Context(attempt: 2, maxAttempts: 5),
                    CancellationToken.None));

        Assert.Equal(
            "DataRights.ExportGenerationRetryRequired",
            exception.Message);
        Assert.Null(dispatcher.Failed);
        Assert.Null(dispatcher.Completed);
        Assert.Equal(start.ArtifactId, objectStore.DeletedArtifactId);
    }

    [Fact]
    public async Task Retryable_generation_failure_becomes_terminal_on_final_attempt()
    {
        DataRightsExportGenerationStart start = Start(dispatchRequired: true);
        FakeTaskDispatcher dispatcher = new(start);
        GenerateDataRightsExportTaskHandler handler = new(
            dispatcher,
            new RecordingGenerator(
                "owner-retry-required",
                DataRightsExportFailureDisposition.Retryable),
            new RecordingObjectStore());

        TaskRunTerminalFailureException exception =
            await Assert.ThrowsAsync<TaskRunTerminalFailureException>(
                () => handler.HandleAsync(
                    Payload(start),
                    Context(attempt: 5, maxAttempts: 5),
                    CancellationToken.None));

        Assert.Equal(
            "data-rights.export:owner-retry-required",
            exception.FailureCode);
        FailDataRightsExportGenerationCommand failed =
            Assert.IsType<FailDataRightsExportGenerationCommand>(
                dispatcher.Failed);
        Assert.Equal(5, failed.Attempt);
        Assert.Equal("owner-retry-required", failed.FailureCode);
    }

    [Fact]
    public async Task Rejected_generation_start_records_terminal_failure()
    {
        DataRightsExportGenerationStart start = Start(dispatchRequired: true);
        FakeTaskDispatcher dispatcher = new(
            start,
            startError: new Error(
                "DataRights.ExportOwnerCatalogInvalid",
                "The catalogue is invalid."));
        GenerateDataRightsExportTaskHandler handler = new(
            dispatcher,
            new RecordingGenerator(Protected()),
            new RecordingObjectStore());

        TaskRunTerminalFailureException exception =
            await Assert.ThrowsAsync<TaskRunTerminalFailureException>(
                () => handler.HandleAsync(
                    Payload(start),
                    Context(attempt: 1, maxAttempts: 5),
                    CancellationToken.None));

        Assert.Equal(
            "data-rights.export:owner-catalog-invalid",
            exception.FailureCode);
        FailDataRightsExportGenerationCommand failed =
            Assert.IsType<FailDataRightsExportGenerationCommand>(
                dispatcher.Failed);
        Assert.Equal("owner-catalog-invalid", failed.FailureCode);
    }

    [Fact]
    public async Task Generation_cancellation_removes_partial_object()
    {
        DataRightsExportGenerationStart start = Start(dispatchRequired: true);
        RecordingObjectStore objectStore = new();
        GenerateDataRightsExportTaskHandler handler = new(
            new FakeTaskDispatcher(start),
            new CancellingGenerator(),
            objectStore);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(
                Payload(start),
                Context(),
                CancellationToken.None));

        Assert.Equal(start.ArtifactId, objectStore.DeletedArtifactId);
    }

    [Fact]
    public async Task Rejected_database_completion_is_terminal_and_removes_object()
    {
        DataRightsExportGenerationStart start = Start(dispatchRequired: true);
        FakeTaskDispatcher dispatcher = new(start, completeFails: true);
        RecordingObjectStore objectStore = new();
        GenerateDataRightsExportTaskHandler handler = new(
            dispatcher,
            new RecordingGenerator(Protected()),
            objectStore);

        TaskRunTerminalFailureException exception =
            await Assert.ThrowsAsync<TaskRunTerminalFailureException>(
            () => handler.HandleAsync(
                Payload(start),
                Context(attempt: 1, maxAttempts: 5),
                CancellationToken.None));

        Assert.Equal(
            "data-rights.export:completion-rejected",
            exception.FailureCode);
        Assert.NotNull(dispatcher.Failed);
        Assert.Equal(start.ArtifactId, objectStore.DeletedArtifactId);
    }

    [Fact]
    public async Task Transient_database_completion_failure_retries_without_failing_artifact()
    {
        DataRightsExportGenerationStart start = Start(dispatchRequired: true);
        FakeTaskDispatcher dispatcher = new(start, completeThrows: true);
        RecordingObjectStore objectStore = new();
        GenerateDataRightsExportTaskHandler handler = new(
            dispatcher,
            new RecordingGenerator(Protected()),
            objectStore);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(
                Payload(start),
                Context(attempt: 2, maxAttempts: 5),
                CancellationToken.None));

        Assert.Null(dispatcher.Failed);
        Assert.Equal(start.ArtifactId, objectStore.DeletedArtifactId);
    }

    private static DataRightsExportGenerationStart Start(
        bool dispatchRequired)
    {
        Guid artifactId = Guid.NewGuid();
        Guid caseId = Guid.NewGuid();
        return new(
            dispatchRequired,
            artifactId,
            "tenant-a",
            caseId,
            DataRightsCaseType.StaffRights,
            PropertyId: null,
            DecisionRevision: 7,
            [new("staff", "profile", Guid.NewGuid(), 3)],
            GeneratedAt,
            GeneratedAt.AddHours(24));
    }

    private static DataRightsProtectedExportArtifact Protected() => new(
        "data-rights/exports/scope-1234/artifact-1234.bfdrx",
        EncryptedByteLength: 100,
        PlaintextSha256: new string('a', 64),
        EncryptionKeyVersion: 1,
        FormatVersion: 1,
        GeneratedAt.AddMinutes(1),
        GeneratedAt.AddHours(24));

    private static GenerateDataRightsExportPayload Payload(
        DataRightsExportGenerationStart start) => new(
            start.ArtifactId,
            start.CaseId,
            start.CaseType,
            start.PropertyId,
            start.DecisionRevision);

    private static TaskExecutionContext Context(
        int attempt = 1,
        int maxAttempts = 5) => new(
        Guid.NewGuid(),
        DataRightsModuleMetadata.Name,
        GenerateDataRightsExportPayload.TaskName,
        DataRightsModuleMetadata.ExportWorkerGroup,
        "worker-1",
        "node-1",
        attempt,
        scopeId: "tenant-a",
        correlationId: Guid.NewGuid(),
        maxAttempts: maxAttempts);

    private sealed class FakeTaskDispatcher(
        DataRightsExportGenerationStart start,
        bool completeFails = false,
        bool completeThrows = false,
        Error? startError = null)
        : ITaskCommandDispatcher
    {
        public object? Completed { get; private set; }
        public object? Failed { get; private set; }

        public Task<Result<TResponse>> DispatchAsync<TCommand, TResponse>(
            TaskExecutionContext context,
            TCommand command,
            CancellationToken cancellationToken)
            where TCommand : ICommand<TResponse>
        {
            object result = command switch
            {
                BeginDataRightsExportGenerationCommand => this.Begin(),
                CompleteDataRightsExportGenerationCommand completed =>
                    this.Complete(completed),
                FailDataRightsExportGenerationCommand failed =>
                    this.Fail(failed),
                _ => throw new InvalidOperationException(
                    $"Unexpected command {command.GetType().Name}.")
            };
            return Task.FromResult((Result<TResponse>)result);
        }

        private Result<DataRightsExportGenerationStart> Begin() =>
            startError is null
                ? Result.Success(start)
                : Result.Failure<DataRightsExportGenerationStart>(startError);

        private Result<Unit> Complete(
            CompleteDataRightsExportGenerationCommand command)
        {
            this.Completed = command;
            if (completeThrows)
            {
                throw new TimeoutException("The database timed out.");
            }

            return completeFails
                ? Result.Failure<Unit>(
                    new Error(
                        "DataRights.ExportCompletionConflict",
                        "The export completion conflicted."))
                : Result.Success(Unit.Value);
        }

        private Result<Unit> Fail(
            FailDataRightsExportGenerationCommand command)
        {
            this.Failed = command;
            return Result.Success(Unit.Value);
        }
    }

    private sealed class RecordingGenerator : IDataRightsExportArtifactGenerator
    {
        private readonly DataRightsProtectedExportArtifact? result;
        private readonly DataRightsExportGenerationException? failure;

        public RecordingGenerator(DataRightsProtectedExportArtifact result) =>
            this.result = result;

        public RecordingGenerator(
            string failureCode,
            DataRightsExportFailureDisposition disposition =
                DataRightsExportFailureDisposition.Terminal) =>
            this.failure = new DataRightsExportGenerationException(
                failureCode,
                disposition);

        public DataRightsExportGenerationRequest? Request { get; private set; }

        public Task<DataRightsProtectedExportArtifact> GenerateAsync(
            DataRightsExportGenerationRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            if (this.failure is not null)
            {
                throw this.failure;
            }

            return Task.FromResult(this.result!);
        }
    }

    private sealed class RecordingObjectStore
        : IDataRightsExportArtifactObjectStore
    {
        public Guid? DeletedArtifactId { get; private set; }

        public Task<bool> DeleteAsync(
            Guid artifactId,
            CancellationToken cancellationToken)
        {
            this.DeletedArtifactId = artifactId;
            return Task.FromResult(true);
        }
    }

    private sealed class CancellingGenerator
        : IDataRightsExportArtifactGenerator
    {
        public Task<DataRightsProtectedExportArtifact> GenerateAsync(
            DataRightsExportGenerationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<DataRightsProtectedExportArtifact>(
                new CancellationToken(canceled: true));
    }
}

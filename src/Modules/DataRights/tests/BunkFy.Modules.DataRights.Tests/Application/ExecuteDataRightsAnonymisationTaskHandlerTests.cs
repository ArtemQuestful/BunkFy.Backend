namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ExecuteDataRightsAnonymisationTaskHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Task_dispatches_exact_contributor_and_records_its_result()
    {
        DataRightsAnonymisationContributionRequest request = CreateRequest();
        DataRightsAnonymisationContributionResult contribution =
            DataRightsAnonymisationContributionResult.Completed(
                new DataRightsAnonymisationOwnerProof(
                    ReceiptContractVersion: 1,
                    Guid.NewGuid(),
                    ResultingRecordVersion: request.Coordinate.RecordVersion + 1,
                    "guests.completed",
                    "guests.profile-anonymised",
                    new string('d', 64),
                    Now));
        FakeTaskDispatcher dispatcher = new(
            DataRightsAnonymisationWorkItemStart.Ready(
                workItemVersion: 2,
                request));
        RecordingContributor contributor = new(contribution);
        ExecuteDataRightsAnonymisationTaskHandler handler = new(
            dispatcher,
            [contributor],
            new TestClock());
        TaskExecutionContext context = CreateContext();

        await handler.HandleAsync(
            Payload(request),
            context,
            CancellationToken.None);

        Assert.Same(request, contributor.Request);
        RecordDataRightsAnonymisationOwnerResultCommand recorded =
            Assert.IsType<RecordDataRightsAnonymisationOwnerResultCommand>(
                dispatcher.Recorded);
        Assert.Equal(context.RunId, recorded.TaskRunId);
        Assert.Equal(context.Attempt, recorded.TaskAttempt);
        Assert.Equal(2, recorded.ExpectedWorkItemVersion);
        Assert.Same(contribution, recorded.Result);
        FinalizeDataRightsAnonymisationLedgerCommand finalized =
            Assert.IsType<FinalizeDataRightsAnonymisationLedgerCommand>(
                dispatcher.Finalized);
        Assert.Equal(context.RunId, finalized.TaskRunId);
    }

    [Fact]
    public async Task Terminal_retry_does_not_call_owner_again()
    {
        DataRightsAnonymisationContributionRequest request = CreateRequest();
        FakeTaskDispatcher dispatcher = new(
            DataRightsAnonymisationWorkItemStart.Terminal(workItemVersion: 3));
        RecordingContributor contributor = new(
            DataRightsAnonymisationContributionResult.Failed("not-used"));
        ExecuteDataRightsAnonymisationTaskHandler handler = new(
            dispatcher,
            [contributor],
            new TestClock());

        await handler.HandleAsync(
            Payload(request),
            CreateContext(),
            CancellationToken.None);

        Assert.Null(contributor.Request);
        Assert.Null(dispatcher.Recorded);
        Assert.IsType<FinalizeDataRightsAnonymisationLedgerCommand>(
            dispatcher.Finalized);
    }

    [Fact]
    public async Task Missing_owner_contributor_fails_for_task_runtime_retry()
    {
        DataRightsAnonymisationContributionRequest request = CreateRequest();
        FakeTaskDispatcher dispatcher = new(
            DataRightsAnonymisationWorkItemStart.Ready(
                workItemVersion: 2,
                request));
        ExecuteDataRightsAnonymisationTaskHandler handler = new(
            dispatcher,
            [],
            new TestClock());

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => handler.HandleAsync(
                Payload(request),
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(
            "DataRights.AnonymisationOwnerContributorUnavailable",
            exception.Message);
        Assert.Null(dispatcher.Recorded);
    }

    [Fact]
    public async Task Owner_result_returned_at_deadline_is_not_recorded()
    {
        DataRightsAnonymisationContributionRequest request = CreateRequest();
        MutableClock clock = new(Now);
        FakeTaskDispatcher dispatcher = new(
            DataRightsAnonymisationWorkItemStart.Ready(
                workItemVersion: 2,
                request));
        RecordingContributor contributor = new(
            DataRightsAnonymisationContributionResult.Failed("retry-required"),
            ownerRequest => clock.UtcNow = ownerRequest.DeadlineUtc);
        ExecuteDataRightsAnonymisationTaskHandler handler = new(
            dispatcher,
            [contributor],
            clock);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(
            () => handler.HandleAsync(
                Payload(request),
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(
            "DataRights.AnonymisationOwnerDeadlineExceeded",
            exception.Message);
        Assert.Same(request, contributor.Request);
        Assert.Null(dispatcher.Recorded);
        Assert.Null(dispatcher.Finalized);
    }

    [Fact]
    public async Task Owner_proof_completed_at_deadline_is_not_recorded()
    {
        DataRightsAnonymisationContributionRequest request = CreateRequest();
        DataRightsAnonymisationContributionResult contribution =
            DataRightsAnonymisationContributionResult.Completed(
                new DataRightsAnonymisationOwnerProof(
                    ReceiptContractVersion: 1,
                    Guid.NewGuid(),
                    ResultingRecordVersion: request.Coordinate.RecordVersion + 1,
                    "guests.completed",
                    "guests.profile-anonymised",
                    new string('d', 64),
                    request.DeadlineUtc));
        FakeTaskDispatcher dispatcher = new(
            DataRightsAnonymisationWorkItemStart.Ready(
                workItemVersion: 2,
                request));
        ExecuteDataRightsAnonymisationTaskHandler handler = new(
            dispatcher,
            [new RecordingContributor(contribution)],
            new TestClock());

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => handler.HandleAsync(
                Payload(request),
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(
            "DataRights.AnonymisationOwnerResultInvalid",
            exception.Message);
        Assert.Null(dispatcher.Recorded);
        Assert.Null(dispatcher.Finalized);
    }

    [Fact]
    public async Task V2_task_dispatches_exact_scoped_contributor_and_records_result()
    {
        DataRightsAnonymisationContributionRequestV2 request =
            CreateScopedRequest();
        DataRightsAnonymisationContributionResult contribution =
            DataRightsAnonymisationContributionResult.Completed(
                DataRightsAnonymisationContractV2.CurrentVersion,
                new DataRightsAnonymisationOwnerProof(
                    ReceiptContractVersion: 1,
                    Guid.NewGuid(),
                    ResultingRecordVersion:
                        request.Coordinate.RecordVersion + 1,
                    "staff.completed",
                    "staff.profile-anonymised",
                    new string('d', 64),
                    Now));
        FakeTaskDispatcher dispatcher = new(
            DataRightsAnonymisationWorkItemStart.Ready(
                workItemVersion: 2,
                request));
        RecordingScopedContributor contributor = new(contribution);
        RecordingPrerequisite prerequisite = new(
            DataRightsAnonymisationExecutionPrerequisiteResult.Completed(
                DataRightsAnonymisationExecutionPrerequisiteContractV2
                    .CurrentVersion));
        ExecuteDataRightsAnonymisationTaskV2Handler handler = new(
            dispatcher,
            [contributor],
            new TestClock(),
            [prerequisite]);
        TaskExecutionContext context = CreateContext();

        await handler.HandleAsync(
            Payload(request),
            context,
            CancellationToken.None);

        Assert.Same(request, prerequisite.Request);
        Assert.Same(request, contributor.Request);
        RecordDataRightsAnonymisationOwnerResultCommand recorded =
            Assert.IsType<
                RecordDataRightsAnonymisationOwnerResultCommand>(
                dispatcher.Recorded);
        Assert.Equal(DataRightsCaseType.StaffRights,
            recorded.Scope.CaseType);
        Assert.Null(recorded.Scope.PropertyId);
        Assert.Same(contribution, recorded.Result);
        Assert.IsType<
            FinalizeDataRightsAnonymisationLedgerCommand>(
            dispatcher.Finalized);
    }

    [Fact]
    public async Task V2_task_records_blocked_prerequisite_without_calling_owner()
    {
        DataRightsAnonymisationContributionRequestV2 request =
            CreateScopedRequest();
        FakeTaskDispatcher dispatcher = new(
            DataRightsAnonymisationWorkItemStart.Ready(
                workItemVersion: 2,
                request));
        RecordingScopedContributor contributor = new(
            DataRightsAnonymisationContributionResult.Failed(
                DataRightsAnonymisationContractV2.CurrentVersion,
                "not-used"));
        RecordingPrerequisite prerequisite = new(
            DataRightsAnonymisationExecutionPrerequisiteResult.Blocked(
                DataRightsAnonymisationExecutionPrerequisiteContractV2
                    .CurrentVersion,
                "workspace.access-denial-blocked"));
        ExecuteDataRightsAnonymisationTaskV2Handler handler = new(
            dispatcher,
            [contributor],
            new TestClock(),
            [prerequisite]);

        await handler.HandleAsync(
            Payload(request),
            CreateContext(),
            CancellationToken.None);

        Assert.Same(request, prerequisite.Request);
        Assert.Null(contributor.Request);
        RecordDataRightsAnonymisationOwnerResultCommand recorded =
            Assert.IsType<
                RecordDataRightsAnonymisationOwnerResultCommand>(
                dispatcher.Recorded);
        Assert.Equal(
            DataRightsAnonymisationContributionStatus.Blocked,
            recorded.Result.Status);
        Assert.Equal(
            "workspace.access-denial-blocked",
            recorded.Result.OutcomeCode);
    }

    [Fact]
    public async Task V2_task_requires_exact_staff_prerequisite()
    {
        DataRightsAnonymisationContributionRequestV2 request =
            CreateScopedRequest();
        FakeTaskDispatcher dispatcher = new(
            DataRightsAnonymisationWorkItemStart.Ready(
                workItemVersion: 2,
                request));
        ExecuteDataRightsAnonymisationTaskV2Handler handler = new(
            dispatcher,
            [new RecordingScopedContributor(
                DataRightsAnonymisationContributionResult.Failed(
                    DataRightsAnonymisationContractV2.CurrentVersion,
                    "not-used"))],
            new TestClock());

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => handler.HandleAsync(
                Payload(request),
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(
            "DataRights.AnonymisationPrerequisiteUnavailable",
            exception.Message);
        Assert.Null(dispatcher.Recorded);
    }

    [Fact]
    public async Task V2_prerequisite_returned_at_deadline_does_not_call_owner()
    {
        DataRightsAnonymisationContributionRequestV2 request =
            CreateScopedRequest();
        MutableClock clock = new(Now);
        FakeTaskDispatcher dispatcher = new(
            DataRightsAnonymisationWorkItemStart.Ready(
                workItemVersion: 2,
                request));
        RecordingScopedContributor contributor = new(
            DataRightsAnonymisationContributionResult.Failed(
                DataRightsAnonymisationContractV2.CurrentVersion,
                "not-used"));
        RecordingPrerequisite prerequisite = new(
            DataRightsAnonymisationExecutionPrerequisiteResult.Completed(
                DataRightsAnonymisationExecutionPrerequisiteContractV2
                    .CurrentVersion),
            ownerRequest => clock.UtcNow = ownerRequest.DeadlineUtc);
        ExecuteDataRightsAnonymisationTaskV2Handler handler = new(
            dispatcher,
            [contributor],
            clock,
            [prerequisite]);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(
            () => handler.HandleAsync(
                Payload(request),
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(
            "DataRights.AnonymisationPrerequisiteDeadlineExceeded",
            exception.Message);
        Assert.Same(request, prerequisite.Request);
        Assert.Null(contributor.Request);
        Assert.Null(dispatcher.Recorded);
        Assert.Null(dispatcher.Finalized);
    }

    private static ExecuteDataRightsAnonymisationPayload Payload(
        DataRightsAnonymisationContributionRequest request) =>
        new(
            request.WorkItemId,
            request.CaseId,
            request.RoutingPropertyId,
            request.ApprovalRevision,
            request.OperationRevision);

    private static ExecuteDataRightsAnonymisationPayloadV2 Payload(
        DataRightsAnonymisationContributionRequestV2 request) =>
        new(
            request.WorkItemId,
            request.CaseId,
            request.CaseType,
            request.ScopeKind,
            request.PropertyId,
            request.ApprovalRevision,
            request.OperationRevision);

    private static DataRightsAnonymisationContributionRequest CreateRequest()
    {
        Guid propertyId = Guid.NewGuid();
        return new(
            DataRightsAnonymisationContract.CurrentVersion,
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            propertyId,
            Guid.NewGuid(),
            ApprovalRevision: 6,
            OperationRevision: 7,
            new DataRightsSubjectCoordinate(
                "guests",
                "guest-profile",
                Guid.NewGuid(),
                RecordVersion: 3),
            new DataRightsApprovalEvidence(
                SchemaVersion: 1,
                propertyId,
                PropertyVersion: 11,
                "GB",
                "approved-policy",
                PolicyVersion: 3,
                "guest-retention",
                RetentionPolicyVersion: 2,
                new string('a', 64),
                "data-rights-anonymisation",
                "erasure",
                "authorized-workspace-operator",
                Now.AddMinutes(-1),
                RequiresDistinctExecutor: true),
            "user:executor",
            Now.AddMinutes(2));
    }

    private static DataRightsAnonymisationContributionRequestV2
        CreateScopedRequest() =>
        new(
            DataRightsAnonymisationContractV2.CurrentVersion,
            "tenant-a",
            DataRightsCaseType.StaffRights,
            DataRightsExecutionScopeKind.Tenant,
            PropertyId: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ApprovalRevision: 6,
            OperationRevision: 7,
            new DataRightsSubjectCoordinate(
                "staff",
                "staff-member",
                Guid.NewGuid(),
                RecordVersion: 3),
            new DataRightsApprovalEvidence(
                SchemaVersion: 2,
                PropertyId: null,
                PropertyVersion: 0,
                "GB",
                "approved-policy",
                PolicyVersion: 3,
                "staff-employment",
                RetentionPolicyVersion: 2,
                new string('a', 64),
                "staff-data-rights-anonymisation",
                "erasure",
                "authorized-workspace-operator",
                Now.AddMinutes(-1),
                RequiresDistinctExecutor: true,
                CaseType: DataRightsCaseType.StaffRights,
                ScopeKind: DataRightsExecutionScopeKind.Tenant,
                RetentionDataClass: "staff-employment",
                RetentionTrigger: "employment-ended",
                RetentionTriggeredAtUtc: Now.AddDays(-2_556),
                RetentionDeadlineUtc: Now.AddDays(-1),
                StateBindings:
                [
                    new("staff.governance", 1, new string('b', 64)),
                    new("staff.holds", 42, new string('c', 64)),
                    new("staff.record", 3, new string('d', 64)),
                    new("staff.restriction", 0, new string('e', 64))
                ],
                StateBindingsSha256: new string('f', 64)),
            "user:executor",
            Now.AddMinutes(2));

    private static TaskExecutionContext CreateContext() =>
        new(
            Guid.NewGuid(),
            DataRightsModuleMetadata.Name,
            ExecuteDataRightsAnonymisationPayload.TaskName,
            DataRightsModuleMetadata.AnonymisationWorkerGroup,
            "worker-1",
            "node-1",
            attempt: 1,
            scopeId: "tenant-a",
            correlationId: Guid.NewGuid());

    private sealed class FakeTaskDispatcher(DataRightsAnonymisationWorkItemStart start)
        : ITaskCommandDispatcher
    {
        public object? Recorded { get; private set; }
        public object? Finalized { get; private set; }

        public Task<Result<TResponse>> DispatchAsync<TCommand, TResponse>(
            TaskExecutionContext context,
            TCommand command,
            CancellationToken cancellationToken)
            where TCommand : ICommand<TResponse>
        {
            object result = command switch
            {
                BeginDataRightsAnonymisationWorkItemCommand =>
                    Result.Success(start),
                RecordDataRightsAnonymisationOwnerResultCommand recorded =>
                    this.Record(recorded),
                FinalizeDataRightsAnonymisationLedgerCommand finalized =>
                    this.Finalize(finalized),
                _ => throw new InvalidOperationException(
                    $"Unexpected command {command.GetType().Name}.")
            };
            return Task.FromResult((Result<TResponse>)result);
        }

        private Result<Unit> Record(
            RecordDataRightsAnonymisationOwnerResultCommand command)
        {
            this.Recorded = command;
            return Result.Success(Unit.Value);
        }

        private Result<Unit> Finalize(
            FinalizeDataRightsAnonymisationLedgerCommand command)
        {
            this.Finalized = command;
            return Result.Success(Unit.Value);
        }
    }

    private sealed class RecordingContributor(
        DataRightsAnonymisationContributionResult result,
        Action<DataRightsAnonymisationContributionRequest>? onExecute = null)
        : IDataRightsAnonymisationContributor
    {
        public string OwnerKey => "guests";
        public int ContractVersion => DataRightsAnonymisationContract.CurrentVersion;
        public DataRightsAnonymisationContributionRequest? Request { get; private set; }

        public Task<DataRightsAnonymisationContributionResult> ExecuteAsync(
            DataRightsAnonymisationContributionRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            onExecute?.Invoke(request);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingScopedContributor(
        DataRightsAnonymisationContributionResult result)
        : IDataRightsAnonymisationContributorV2
    {
        public string OwnerKey => "staff";
        public string RecordType => "staff-member";
        public DataRightsCaseType CaseType =>
            DataRightsCaseType.StaffRights;
        public int ContractVersion =>
            DataRightsAnonymisationContractV2.CurrentVersion;
        public DataRightsAnonymisationContributionRequestV2? Request
        {
            get;
            private set;
        }

        public Task<DataRightsAnonymisationContributionResult>
            ExecuteAsync(
                DataRightsAnonymisationContributionRequestV2 request,
                CancellationToken cancellationToken)
        {
            this.Request = request;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingPrerequisite(
        DataRightsAnonymisationExecutionPrerequisiteResult result,
        Action<DataRightsAnonymisationContributionRequestV2>? onExecute = null)
        : IDataRightsAnonymisationExecutionPrerequisiteV2
    {
        public string OwnerKey => "staff";
        public string RecordType => "staff-member";
        public DataRightsCaseType CaseType =>
            DataRightsCaseType.StaffRights;
        public int ContractVersion =>
            DataRightsAnonymisationExecutionPrerequisiteContractV2
                .CurrentVersion;
        public DataRightsAnonymisationContributionRequestV2? Request
        {
            get;
            private set;
        }

        public Task<DataRightsAnonymisationExecutionPrerequisiteResult>
            ExecuteAsync(
                DataRightsAnonymisationContributionRequestV2 request,
                CancellationToken cancellationToken)
        {
            this.Request = request;
            onExecute?.Invoke(request);
            return Task.FromResult(result);
        }
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}

namespace BunkFy.Modules.DataRights.Tests.Application;

using System.Reflection;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Pagination;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsMutationCoordinatorTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Case_writer_locks_before_authoritative_reload()
    {
        List<string> calls = [];
        DataRightsCase dataRightsCase = CreateCase();
        RecordingCaseRepository cases = new(dataRightsCase, calls);
        DataRightsCaseMutationCoordinator coordinator = new(
            cases,
            cases,
            cases,
            new RecordingOperationLock(calls),
            new TestScopeContext());

        DataRightsCase? acquired = await coordinator.AcquireAsync(
            DataRightsCaseScope.ForProperty(
                dataRightsCase.PropertyId!.Value),
            dataRightsCase.Id,
            CancellationToken.None);

        Assert.Same(dataRightsCase, acquired);
        Assert.Equal(
            [
                $"case-write:{dataRightsCase.Id:N}",
                $"case-reload:{dataRightsCase.Id:N}"
            ],
            calls);
    }

    [Fact]
    public async Task Case_creation_locks_before_tenant_identity_reload()
    {
        List<string> calls = [];
        DataRightsCase dataRightsCase = CreateCase();
        RecordingCaseRepository cases = new(dataRightsCase, calls);
        DataRightsCaseMutationCoordinator coordinator = new(
            cases,
            cases,
            cases,
            new RecordingOperationLock(calls),
            new TestScopeContext());

        DataRightsCase? acquired = await coordinator.AcquireCreationAsync(
            dataRightsCase.Id,
            CancellationToken.None);

        Assert.Same(dataRightsCase, acquired);
        Assert.Equal(
            [
                $"case-write:{dataRightsCase.Id:N}",
                $"case-identity-reload:{dataRightsCase.Id:N}"
            ],
            calls);
    }

    [Fact]
    public async Task Tenant_admission_uses_canonical_hierarchy_before_reload()
    {
        List<string> calls = [];
        DataRightsCase dataRightsCase = CreateTenantTerminationCase();
        TenantTerminationProcess process = CreateProcess(dataRightsCase.Id);
        RecordingCaseRepository cases = new(dataRightsCase, calls);
        RecordingProcessRepository processes = new(process, calls);
        TenantTerminationMutationCoordinator coordinator = new(
            processes,
            cases,
            new RecordingOperationLock(calls),
            new TestScopeContext());

        TenantTerminationMutationState? state =
            await coordinator.AcquireAdmissionAsync(
                process.Id,
                dataRightsCase.Id,
                CancellationToken.None);

        Assert.Same(process, state!.Process);
        Assert.Same(dataRightsCase, state.Case);
        Assert.Equal(
            [
                "tenant-control",
                $"process-write:{process.Id:N}",
                $"process-reload:{process.Id:N}",
                $"case-write:{dataRightsCase.Id:N}",
                $"termination-case-reload:{dataRightsCase.Id:N}"
            ],
            calls);
    }

    [Fact]
    public async Task Owner_work_keeps_process_shared_and_child_exclusive()
    {
        List<string> calls = [];
        TenantTerminationProcess process = CreateProcess(Guid.NewGuid());
        Guid workItemId = Guid.NewGuid();
        TenantTerminationMutationCoordinator coordinator = new(
            new RecordingProcessRepository(process, calls),
            new RecordingCaseRepository(null, calls),
            new RecordingOperationLock(calls),
            new TestScopeContext());

        TenantTerminationProcess? acquired =
            await coordinator.AcquireOwnerWorkAsync(
                process.Id,
                workItemId,
                CancellationToken.None);

        Assert.Same(process, acquired);
        Assert.Equal(
            [
                $"process-read:{process.Id:N}",
                $"owner-work:{workItemId:N}",
                $"process-reload:{process.Id:N}"
            ],
            calls);
    }

    [Fact]
    public async Task Ledger_finalization_locks_case_ledger_and_work_item_in_order()
    {
        List<string> calls = [];
        DataRightsCase dataRightsCase = CreateCase();
        Guid workItemId = Guid.NewGuid();
        RecordingCaseRepository cases = new(dataRightsCase, calls);
        DataRightsExecutionMutationCoordinator coordinator = new(
            cases,
            new RecordingOperationLock(calls),
            new TestScopeContext());

        DataRightsCase? acquired =
            await coordinator.AcquireLedgerWorkItemAsync(
                DataRightsCaseScope.ForProperty(
                    dataRightsCase.PropertyId!.Value),
                dataRightsCase.Id,
                workItemId,
                CancellationToken.None);

        Assert.Same(dataRightsCase, acquired);
        Assert.Equal(
            [
                $"case-read:{dataRightsCase.Id:N}",
                "processing-ledger",
                $"execution-work-item:{workItemId:N}",
                $"case-reload:{dataRightsCase.Id:N}"
            ],
            calls);
    }

    [Fact]
    public void All_online_writers_require_their_mutation_boundary()
    {
        AssertCoordinator<DataRightsCaseMutationCoordinator>(
            typeof(BeginDataRightsDecisionCommandHandler),
            typeof(BeginDataRightsDiscoveryCommandHandler),
            typeof(BeginDataRightsExportGenerationCommandHandler),
            typeof(CancelDataRightsCaseCommandHandler),
            typeof(CompleteDataRightsExportGenerationCommandHandler),
            typeof(CreateDataRightsCaseCommandHandler),
            typeof(DataRightsAnonymisationExecutionReconciler),
            typeof(DataRightsCorrectionCompletionCoordinator),
            typeof(DecideTenantTerminationCommandHandler),
            typeof(ExecuteDataRightsRestrictionCommandHandler),
            typeof(RecordControllerRoutingCommandHandler),
            typeof(RecordDataRightsDecisionCommandHandler),
            typeof(RecordRequesterVerificationCommandHandler),
            typeof(RequestDataRightsExportCommandHandler),
            typeof(RequireDataRightsReviewCommandHandler),
            typeof(SelectDataRightsSubjectCommandHandler),
            typeof(StartDataRightsAnonymisationExecutionCommandHandler),
            typeof(StartDataRightsCorrectionExecutionCommandHandler),
            typeof(UnselectDataRightsSubjectCommandHandler));
        AssertCoordinator<DataRightsExecutionMutationCoordinator>(
            typeof(BeginDataRightsAnonymisationWorkItemCommandHandler),
            typeof(FinalizeDataRightsAnonymisationLedgerCommandHandler),
            typeof(RecordDataRightsAnonymisationOwnerResultCommandHandler));
        AssertCoordinator<TenantTerminationMutationCoordinator>(
            typeof(BeginTenantTerminationExportArtifactGenerationCommandHandler),
            typeof(BeginTenantTerminationExportFragmentGenerationCommandHandler),
            typeof(BeginTenantTerminationOwnerWorkCommandHandler),
            typeof(BeginTenantTerminationPhaseCommandHandler),
            typeof(BeginTenantTerminationVerificationCommandHandler),
            typeof(CompleteTenantTerminationExportArtifactGenerationCommandHandler),
            typeof(CompleteTenantTerminationExportFragmentGenerationCommandHandler),
            typeof(CompleteTenantTerminationVerificationCommandHandler),
            typeof(FailTenantTerminationExportArtifactGenerationCommandHandler),
            typeof(FailTenantTerminationExportFragmentGenerationCommandHandler),
            typeof(PrepareTenantTerminationVerificationCommandHandler),
            typeof(ReconcileTenantTerminationPhaseCommandHandler),
            typeof(RecordTenantTerminationOwnerResultCommandHandler),
            typeof(RequestTenantTerminationCancellationCommandHandler),
            typeof(RetryTenantTerminationCommandHandler),
            typeof(TenantTerminationStartCoordinator));
        AssertCoordinator<IDataRightsOperationLock>(
            typeof(RequestTenantTerminationCommandHandler));
    }

    private static void AssertCoordinator<TCoordinator>(params Type[] writers)
    {
        foreach (Type writer in writers)
        {
            bool found = writer
                .GetConstructors(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter =>
                    parameter.ParameterType == typeof(TCoordinator));
            Assert.True(
                found,
                $"{writer.Name} must serialize through " +
                $"{typeof(TCoordinator).Name}.");
        }
    }

    private static DataRightsCase CreateCase()
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            Guid.NewGuid(),
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        return DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:privacy",
            Now).Value;
    }

    private static DataRightsCase CreateTenantTerminationCase()
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.TenantTermination,
            DataRightsCaseOperation.Anonymisation,
            DataRightsRequesterRelation.TenantOwner,
            DataRightsRestrictionAction.None).Value;
        return DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:owner",
            Now).Value;
    }

    private static TenantTerminationProcess CreateProcess(Guid caseId) =>
        TenantTerminationProcess.Prepare(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            caseId,
            approvalRevision: 2,
            Guid.NewGuid(),
            exportRequested: false,
            new string('a', 64),
            "user:approver",
            Now,
            "system:tenant-termination",
            Now).Value;

    private sealed class RecordingOperationLock(List<string> calls)
        : IDataRightsOperationLock
    {
        public Task AcquireTenantControlAsync(CancellationToken cancellationToken) =>
            this.Record("tenant-control", cancellationToken);

        public Task AcquireProcessReadAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            this.Record($"process-read:{processId:N}", cancellationToken);

        public Task AcquireProcessWriteAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            this.Record($"process-write:{processId:N}", cancellationToken);

        public Task AcquireOwnerWorkItemAsync(
            Guid workItemId,
            CancellationToken cancellationToken) =>
            this.Record($"owner-work:{workItemId:N}", cancellationToken);

        public Task AcquireCaseReadAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            this.Record($"case-read:{caseId:N}", cancellationToken);

        public Task AcquireCaseWriteAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            this.Record($"case-write:{caseId:N}", cancellationToken);

        public Task AcquireExecutionWorkItemAsync(
            Guid workItemId,
            CancellationToken cancellationToken) =>
            this.Record(
                $"execution-work-item:{workItemId:N}",
                cancellationToken);

        public Task AcquireProcessingLedgerAsync(
            CancellationToken cancellationToken) =>
            this.Record("processing-ledger", cancellationToken);

        private Task Record(
            string call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add(call);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCaseRepository(
        DataRightsCase? dataRightsCase,
        List<string> calls)
        : IDataRightsCaseRepository,
          IDataRightsCaseIdentityRepository,
          ITenantTerminationCaseRepository
    {
        public Task AddAsync(
            DataRightsCase candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken)
        {
            calls.Add($"case-reload:{caseId:N}");
            return Task.FromResult(
                dataRightsCase?.Id == caseId ? dataRightsCase : null);
        }

        public Task<DataRightsCase?> GetByIdAsync(
            Guid caseId,
            CancellationToken cancellationToken)
        {
            calls.Add($"case-identity-reload:{caseId:N}");
            return Task.FromResult(
                dataRightsCase?.Id == caseId ? dataRightsCase : null);
        }

        public Task<DataRightsCase?> GetAsync(
            Guid caseId,
            CancellationToken cancellationToken)
        {
            calls.Add($"termination-case-reload:{caseId:N}");
            return Task.FromResult(
                dataRightsCase?.Id == caseId ? dataRightsCase : null);
        }

        public Task<DataRightsCase?> GetActiveAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProcessRepository(
        TenantTerminationProcess process,
        List<string> calls)
        : ITenantTerminationRepository
    {
        public Task AddProcessAsync(
            TenantTerminationProcess candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddOwnerWorkItemAsync(
            TenantTerminationOwnerWorkItem workItem,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationProcess?> GetProcessAsync(
            Guid processId,
            CancellationToken cancellationToken)
        {
            calls.Add($"process-reload:{processId:N}");
            return Task.FromResult<TenantTerminationProcess?>(
                process.Id == processId ? process : null);
        }

        public Task<TenantTerminationProcess?> GetActiveProcessAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationProcess?> GetProcessByIdempotencyKeyAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationOwnerWorkItem?> GetOwnerWorkItemAsync(
            Guid processId,
            TenantTerminationOwnerPhase phase,
            string ownerKey,
            long operationRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TenantTerminationOwnerWorkItem>>
            ListOwnerWorkItemsAsync(
                Guid processId,
                TenantTerminationOwnerPhase phase,
                long operationRevision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;

        public string ScopeId => TenantId;
    }
}

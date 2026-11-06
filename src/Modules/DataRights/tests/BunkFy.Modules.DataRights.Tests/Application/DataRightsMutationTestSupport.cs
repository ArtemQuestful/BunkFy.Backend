namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Pagination;
using Gma.Framework.Scoping;

internal static class DataRightsMutationTestSupport
{
    public static IDataRightsOperationLock OperationLock { get; } =
        new NoOpOperationLock();

    public static DataRightsCaseMutationCoordinator Case(
        IDataRightsCaseRepository cases,
        string scopeId = "tenant-a",
        IDataRightsCaseIdentityRepository? caseIdentities = null) => new(
        cases,
        caseIdentities ?? ThrowingDataRightsCaseIdentityRepository.Instance,
        ThrowingTenantTerminationCaseRepository.Instance,
        OperationLock,
        new TestScopeContext(scopeId));

    public static DataRightsCaseMutationCoordinator TenantCase(
        ITenantTerminationCaseRepository cases,
        string scopeId = "tenant-a") => new(
        ThrowingDataRightsCaseRepository.Instance,
        ThrowingDataRightsCaseIdentityRepository.Instance,
        cases,
        OperationLock,
        new TestScopeContext(scopeId));

    public static DataRightsExecutionMutationCoordinator Execution(
        IDataRightsCaseRepository cases,
        string scopeId = "tenant-a") => new(
        cases,
        OperationLock,
        new TestScopeContext(scopeId));

    public static TenantTerminationMutationCoordinator TenantTermination(
        ITenantTerminationRepository processes,
        ITenantTerminationCaseRepository? cases = null,
        string scopeId = "tenant-a") => new(
        processes,
        cases ?? ThrowingTenantTerminationCaseRepository.Instance,
        OperationLock,
        new TestScopeContext(scopeId));

    private sealed class NoOpOperationLock : IDataRightsOperationLock
    {
        public Task AcquireTenantControlAsync(
            CancellationToken cancellationToken) => Complete(cancellationToken);

        public Task AcquireProcessReadAsync(
            Guid processId,
            CancellationToken cancellationToken) => Complete(cancellationToken);

        public Task AcquireProcessWriteAsync(
            Guid processId,
            CancellationToken cancellationToken) => Complete(cancellationToken);

        public Task AcquireOwnerWorkItemAsync(
            Guid workItemId,
            CancellationToken cancellationToken) => Complete(cancellationToken);

        public Task AcquireCaseReadAsync(
            Guid caseId,
            CancellationToken cancellationToken) => Complete(cancellationToken);

        public Task AcquireCaseWriteAsync(
            Guid caseId,
            CancellationToken cancellationToken) => Complete(cancellationToken);

        public Task AcquireExecutionWorkItemAsync(
            Guid workItemId,
            CancellationToken cancellationToken) => Complete(cancellationToken);

        public Task AcquireProcessingLedgerAsync(
            CancellationToken cancellationToken) => Complete(cancellationToken);

        private static Task Complete(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;

        public string? ScopeId => scopeId;

        public IDisposable Push(string? requestedScopeId) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingDataRightsCaseRepository
        : IDataRightsCaseRepository
    {
        public static ThrowingDataRightsCaseRepository Instance { get; } = new();

        public Task AddAsync(
            DataRightsCase dataRightsCase,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            DataRightsCaseScope scope,
            Guid caseId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCaseListResponse> ListAsync(
            DataRightsCaseScope scope,
            DataRightsCaseStatus? status,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingDataRightsCaseIdentityRepository
        : IDataRightsCaseIdentityRepository
    {
        public static ThrowingDataRightsCaseIdentityRepository Instance { get; } =
            new();

        public Task<DataRightsCase?> GetByIdAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingTenantTerminationCaseRepository
        : ITenantTerminationCaseRepository
    {
        public static ThrowingTenantTerminationCaseRepository Instance { get; } =
            new();

        public Task AddAsync(
            DataRightsCase dataRightsCase,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsCase?> GetActiveAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

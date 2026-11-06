namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Pagination;

internal static class WorkspaceStaffAccessMutationTestSupport
{
    public static IWorkspaceStaffAccessProcessRepository NoOpenProcesses { get; } =
        new NoOpenProcessRepository();

    public static WorkspaceStaffAccessMutationCoordinator Create(
        IWorkspaceStaffAccessProcessRepository? processes = null,
        bool processExists = true,
        List<string>? calls = null) => new(
        new NoOpOperationLock(processExists, calls),
        processes ?? ThrowingProcessRepository.Instance);

    private sealed class NoOpOperationLock(
        bool processExists,
        List<string>? calls)
        : IWorkspaceStaffAccessOperationLock
    {
        public Task AcquireSubjectAsync(
            string subjectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls?.Add("subject-coordinate");
            return Task.CompletedTask;
        }

        public Task AcquireStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls?.Add("staff-coordinate");
            return Task.CompletedTask;
        }

        public Task AcquireCoordinatesAsync(
            Guid staffMemberId,
            string subjectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls?.Add("subject-coordinate");
            calls?.Add("staff-coordinate");
            return Task.CompletedTask;
        }

        public Task<bool> TryAcquireProcessAsync(
            Guid processId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls?.Add("staff-coordinate");
            return Task.FromResult(processExists);
        }

        public Task<bool> TryAcquireStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls?.Add("staff-coordinate");
            return Task.FromResult(processExists);
        }
    }

    private sealed class ThrowingProcessRepository
        : IWorkspaceStaffAccessProcessRepository
    {
        public static ThrowingProcessRepository Instance { get; } = new();

        public Task<WorkspaceStaffAccessProcess?> GetAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetByStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetOpenByStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?>
            GetLatestCompletedSuspensionAsync(
                Guid staffMemberId,
                string subjectId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetCompletedDepartureAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcessListResponse> ListOpenAsync(
            PageRequest page,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffAccessProcess process,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoOpenProcessRepository
        : IWorkspaceStaffAccessProcessRepository
    {
        public Task<WorkspaceStaffAccessProcess?> GetAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetByStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetOpenByStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceStaffAccessProcess?>(null);

        public Task<WorkspaceStaffAccessProcess?>
            GetLatestCompletedSuspensionAsync(
                Guid staffMemberId,
                string subjectId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetCompletedDepartureAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcessListResponse> ListOpenAsync(
            PageRequest page,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffAccessProcess process,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

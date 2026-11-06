namespace BunkFy.Modules.Workspaces.Tests;

using System.Reflection;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Pagination;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffAccessMutationCoordinatorTests
{
    [Fact]
    public async Task Existing_process_locks_before_authoritative_reload()
    {
        WorkspaceStaffAccessProcess process = CreateProcess();
        List<string> calls = [];
        SequencedProcessRepository processes = new(process, calls);
        WorkspaceStaffAccessMutationCoordinator coordinator = new(
            new CallbackOperationLock(
                calls,
                acquired: () => processes.Visible = false),
            processes);

        WorkspaceStaffAccessProcess? result =
            await coordinator.AcquireExistingAsync(
                process.Id,
                CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(["process-lock", "process-read"], calls);
    }

    [Fact]
    public async Task Missing_process_coordinate_skips_reload()
    {
        List<string> calls = [];
        SequencedProcessRepository processes = new(null, calls);
        WorkspaceStaffAccessMutationCoordinator coordinator = new(
            new CallbackOperationLock(calls, processExists: false),
            processes);

        WorkspaceStaffAccessProcess? result =
            await coordinator.AcquireExistingAsync(
                Guid.NewGuid(),
                CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(["process-lock"], calls);
    }

    [Fact]
    public async Task Staff_version_coordinate_locks_before_authoritative_reload()
    {
        WorkspaceStaffAccessProcess process = CreateProcess();
        List<string> calls = [];
        SequencedProcessRepository processes = new(process, calls);
        WorkspaceStaffAccessMutationCoordinator coordinator = new(
            new CallbackOperationLock(
                calls,
                acquired: () => processes.Visible = false),
            processes);

        WorkspaceStaffAccessProcess? result =
            await coordinator.AcquireVersionAsync(
                process.StaffMemberId,
                process.TargetStaffVersion,
                CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(["version-lock", "version-read"], calls);
    }

    [Fact]
    public async Task Explicit_coordinates_lock_subject_before_staff()
    {
        List<string> calls = [];
        WorkspaceStaffAccessMutationCoordinator coordinator = new(
            new CallbackOperationLock(calls),
            new SequencedProcessRepository(null, calls));

        await coordinator.AcquireCoordinatesAsync(
            Guid.NewGuid(),
            "subject-a",
            CancellationToken.None);

        Assert.Equal(["subject-lock", "staff-lock"], calls);
    }

    [Fact]
    public async Task Invalid_coordinates_fail_without_entering_the_lock()
    {
        List<string> calls = [];
        WorkspaceStaffAccessMutationCoordinator coordinator = new(
            new CallbackOperationLock(calls),
            new SequencedProcessRepository(null, calls));

        WorkspaceStaffAccessProcess? byProcess =
            await coordinator.AcquireExistingAsync(
                Guid.Empty,
                CancellationToken.None);
        WorkspaceStaffAccessProcess? byVersion =
            await coordinator.AcquireVersionAsync(
                Guid.Empty,
                targetStaffVersion: 0,
                CancellationToken.None);
        bool coordinate =
            await coordinator.TryAcquireExistingCoordinateAsync(
                Guid.Empty,
                CancellationToken.None);

        Assert.Null(byProcess);
        Assert.Null(byVersion);
        Assert.False(coordinate);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(typeof(PrepareWorkspaceStaffAccessCommandHandler))]
    [InlineData(typeof(DenyWorkspaceStaffAccessCommandHandler))]
    [InlineData(typeof(RetryWorkspaceStaffAccessProcessCommandHandler))]
    [InlineData(typeof(StaffLifecycleWorkspaceAccessHandler))]
    [InlineData(typeof(OrganizationMembershipAccessProfileSeedHandler))]
    [InlineData(typeof(ScrubWorkspaceStaffRetentionCorrelationCommandHandler))]
    [InlineData(typeof(ApplyWorkspaceStaffCorrelationAnonymisationCommandHandler))]
    [InlineData(typeof(RestoreWorkspaceStaffCorrelationAnonymisationCommandHandler))]
    public void Staff_access_writers_require_mutation_coordinator(
        Type handlerType)
    {
        bool hasCoordinator = handlerType
            .GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType ==
                typeof(WorkspaceStaffAccessMutationCoordinator));

        Assert.True(
            hasCoordinator,
            $"{handlerType.Name} must serialize through " +
            $"{nameof(WorkspaceStaffAccessMutationCoordinator)}.");
    }

    private static WorkspaceStaffAccessProcess CreateProcess() =>
        WorkspaceStaffAccessProcess.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "subject-a",
            WorkspaceStaffAccessTargetState.Suspended,
            targetStaffVersion: 2,
            new DateOnly(2026, 8, 6),
            "user:owner",
            [],
            new DateTimeOffset(
                2026,
                8,
                6,
                14,
                0,
                0,
                TimeSpan.Zero)).Value;

    private sealed class CallbackOperationLock(
        List<string> calls,
        Action? acquired = null,
        bool processExists = true)
        : IWorkspaceStaffAccessOperationLock
    {
        public Task AcquireSubjectAsync(
            string subjectId,
            CancellationToken cancellationToken)
        {
            calls.Add("subject-lock");
            return Task.CompletedTask;
        }

        public Task AcquireStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            calls.Add("staff-lock");
            acquired?.Invoke();
            return Task.CompletedTask;
        }

        public Task AcquireCoordinatesAsync(
            Guid staffMemberId,
            string subjectId,
            CancellationToken cancellationToken)
        {
            calls.Add("subject-lock");
            calls.Add("staff-lock");
            acquired?.Invoke();
            return Task.CompletedTask;
        }

        public Task<bool> TryAcquireProcessAsync(
            Guid processId,
            CancellationToken cancellationToken)
        {
            calls.Add("process-lock");
            acquired?.Invoke();
            return Task.FromResult(processExists);
        }

        public Task<bool> TryAcquireStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken)
        {
            calls.Add("version-lock");
            acquired?.Invoke();
            return Task.FromResult(processExists);
        }
    }

    private sealed class SequencedProcessRepository(
        WorkspaceStaffAccessProcess? process,
        List<string> calls)
        : IWorkspaceStaffAccessProcessRepository
    {
        public bool Visible { get; set; } = true;

        public Task<WorkspaceStaffAccessProcess?> GetAsync(
            Guid processId,
            CancellationToken cancellationToken)
        {
            calls.Add("process-read");
            return Task.FromResult(this.Visible ? process : null);
        }

        public Task<WorkspaceStaffAccessProcess?> GetByStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken)
        {
            calls.Add("version-read");
            return Task.FromResult(this.Visible ? process : null);
        }

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
            WorkspaceStaffAccessProcess value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

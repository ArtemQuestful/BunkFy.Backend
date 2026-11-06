namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;

internal sealed class WorkspaceStaffAccessMutationCoordinator(
    IWorkspaceStaffAccessOperationLock operationLock,
    IWorkspaceStaffAccessProcessRepository processes)
{
    public Task AcquireSubjectAsync(
        string subjectId,
        CancellationToken cancellationToken) =>
        operationLock.AcquireSubjectAsync(subjectId, cancellationToken);

    public Task AcquireStaffAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        operationLock.AcquireStaffAsync(staffMemberId, cancellationToken);

    public Task AcquireCoordinatesAsync(
        Guid staffMemberId,
        string subjectId,
        CancellationToken cancellationToken) =>
        operationLock.AcquireCoordinatesAsync(
            staffMemberId,
            subjectId,
            cancellationToken);

    public async Task<WorkspaceStaffAccessProcess?> AcquireExistingAsync(
        Guid processId,
        CancellationToken cancellationToken)
    {
        if (processId == Guid.Empty)
        {
            return null;
        }

        if (!await operationLock.TryAcquireProcessAsync(
                processId,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await processes.GetAsync(processId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorkspaceStaffAccessProcess?> AcquireVersionAsync(
        Guid staffMemberId,
        long targetStaffVersion,
        CancellationToken cancellationToken)
    {
        if (staffMemberId == Guid.Empty || targetStaffVersion <= 0)
        {
            return null;
        }

        if (!await operationLock.TryAcquireStaffVersionAsync(
                staffMemberId,
                targetStaffVersion,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await processes.GetByStaffVersionAsync(
                staffMemberId,
                targetStaffVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<bool> TryAcquireExistingCoordinateAsync(
        Guid processId,
        CancellationToken cancellationToken) => processId == Guid.Empty
        ? Task.FromResult(false)
        : operationLock.TryAcquireProcessAsync(
            processId,
            cancellationToken);
}

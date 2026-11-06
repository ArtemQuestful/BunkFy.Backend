namespace BunkFy.Modules.Workspaces.Application.Ports;

public interface IWorkspaceStaffAccessOperationLock
{
    Task AcquireSubjectAsync(
        string subjectId,
        CancellationToken cancellationToken);

    Task AcquireStaffAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken);

    Task AcquireCoordinatesAsync(
        Guid staffMemberId,
        string subjectId,
        CancellationToken cancellationToken);

    Task<bool> TryAcquireProcessAsync(
        Guid processId,
        CancellationToken cancellationToken);

    Task<bool> TryAcquireStaffVersionAsync(
        Guid staffMemberId,
        long targetStaffVersion,
        CancellationToken cancellationToken);
}

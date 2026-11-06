namespace BunkFy.Modules.Workspaces.Application.Ports;

using Gma.Framework.Results;

public interface IWorkspaceStaffOnboardingSerializedReadBoundary
{
    Task<Result<T>> RunAsync<T>(
        Func<CancellationToken, Task<Result<T>>> read,
        CancellationToken cancellationToken);
}

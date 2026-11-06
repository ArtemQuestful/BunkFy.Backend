namespace BunkFy.Modules.Workspaces.Application.Ports;

using Gma.Framework.Results;

public interface IWorkspaceIdentityAnchorCutoverExecutionBoundary
{
    Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken);
}

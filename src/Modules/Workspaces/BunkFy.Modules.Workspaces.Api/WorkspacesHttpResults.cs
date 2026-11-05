namespace BunkFy.Modules.Workspaces.Api;

using Gma.Framework.Api.Results;
using Gma.Framework.Results;
using Microsoft.AspNetCore.Http;

public static class WorkspacesHttpResults
{
    public static IResult ToWorkspacesHttpResult<TValue>(this Result<TValue> result) =>
        result.ToHttpResult(WorkspacesApiEndpointSupport.ErrorStatusCodes);
}

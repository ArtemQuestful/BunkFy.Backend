namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class StaffIdentityBootstrapper(IRequestDispatcher dispatcher)
    : IStaffIdentityBootstrapper
{
    public async Task<StaffIdentityBootstrapResult> BootstrapAsync(
        StaffIdentityBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<Unit> result = await dispatcher.SendAsync(
            new BootstrapStaffIdentityCommand(
                request.OperationId,
                request.SourceId,
                request.AuthSubjectId,
                request.DisplayName,
                request.WorkEmail,
                request.ActorId),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? new StaffIdentityBootstrapResult(true, null)
            : new StaffIdentityBootstrapResult(false, result.Error.Code);
    }
}

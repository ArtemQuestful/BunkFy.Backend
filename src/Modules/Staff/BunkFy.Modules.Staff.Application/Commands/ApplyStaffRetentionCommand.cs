namespace BunkFy.Modules.Staff.Application.Commands;

using Gma.Framework.Cqrs;

internal sealed record ApplyStaffRetentionCommand(
    Guid ExecutionId,
    int Attempt,
    Guid StaffMemberId,
    long ExpectedStaffVersion)
    : ITransactionalCommand<StaffRetentionMutationResult>;

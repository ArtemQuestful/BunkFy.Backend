namespace BunkFy.Modules.Staff.Application.Commands;

using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;

public sealed record ReleaseStaffProcessingRestrictionCommand(
    Guid IdempotencyKey,
    Guid RestrictionId,
    Guid CaseId,
    long ApprovalRevision,
    Guid StaffMemberId,
    long ExpectedStaffVersion,
    long ExpectedRestrictionVersion,
    long ExpectedProjectionRevision,
    string ActorId,
    bool LegacyUnboundTarget = false)
    : ITransactionalCommand<StaffProcessingRestrictionReceiptDto>;

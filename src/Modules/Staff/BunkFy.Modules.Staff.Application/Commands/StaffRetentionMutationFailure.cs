namespace BunkFy.Modules.Staff.Application.Commands;

internal enum StaffRetentionMutationFailure
{
    None = 0,
    ProjectionUnavailable = 1,
    PolicyUnavailable = 2,
    PrerequisiteBlocked = 3,
    PrerequisiteUnavailable = 4,
    MutationFailed = 5,
    IdentityAnchorResolutionRequired = 6
}

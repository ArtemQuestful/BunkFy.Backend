namespace BunkFy.Modules.Guests.Application.Commands;

internal enum GuestRetentionMutationFailure
{
    None = 0,
    ProjectionUnavailable = 1,
    PolicyUnavailable = 2,
    TimeZoneUnavailable = 3,
    MutationFailed = 4
}

namespace BunkFy.Modules.Reservations.Contracts;

public static class ReservationsContractLimits
{
    public const int PrimaryGuestNameMaxLength = 200;
    public const int EmailMaxLength = 320;
    public const int PhoneMaxLength = 50;
    public const int SourceSystemMaxLength = 100;
    public const int SourceReferenceMaxLength = 200;
    public const int NotesMaxLength = 2000;
    public const int ActorIdMaxLength = 200;
    public const int TimeZoneIdMaxLength = 100;
    public const int SearchMaxLength = 200;
    public const int MaximumRequestedUnits = 100;
    public const int DefaultOperationsSnapshotUpcomingLimit = 25;
    public const int MaximumOperationsSnapshotUpcomingLimit = 50;
    public const int MaximumScheduleWindowDays = 42;
}

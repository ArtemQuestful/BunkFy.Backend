namespace BunkFy.Modules.Reservations.Application.Handlers;

internal static class ReservationMutationTime
{
    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        const long ticksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        DateTimeOffset utc = value.ToUniversalTime();
        return new(
            utc.Ticks - (utc.Ticks % ticksPerMicrosecond),
            TimeSpan.Zero);
    }
}

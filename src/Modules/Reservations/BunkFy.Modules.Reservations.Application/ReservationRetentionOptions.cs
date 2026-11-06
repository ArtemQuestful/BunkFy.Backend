namespace BunkFy.Modules.Reservations.Application;

public sealed class ReservationRetentionOptions
{
    public const string SectionName = "Reservations:Retention";
    internal const int MaximumScanSize = 1000;

    public int IntervalMinutes { get; set; } = 60;
    public int ScanSize { get; set; } = 100;
    public int MutationBatchSize { get; set; } = 25;

    internal TimeSpan Interval =>
        TimeSpan.FromMinutes(this.IntervalMinutes);
}

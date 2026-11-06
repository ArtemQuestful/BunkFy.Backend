namespace BunkFy.Modules.Reservations.Application;

using Microsoft.Extensions.Options;

internal sealed class ReservationRetentionOptionsValidator
    : IValidateOptions<ReservationRetentionOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        ReservationRetentionOptions options)
    {
        List<string> failures = [];
        if (options.IntervalMinutes is < 15 or > 1440)
        {
            failures.Add(
                $"{ReservationRetentionOptions.SectionName}:IntervalMinutes must be between 15 and 1440.");
        }

        if (options.ScanSize is < 1 or >
            ReservationRetentionOptions.MaximumScanSize)
        {
            failures.Add(
                $"{ReservationRetentionOptions.SectionName}:ScanSize must " +
                $"be between 1 and " +
                $"{ReservationRetentionOptions.MaximumScanSize}.");
        }

        if (options.MutationBatchSize is < 1 or > 250)
        {
            failures.Add(
                $"{ReservationRetentionOptions.SectionName}:MutationBatchSize must be between 1 and 250.");
        }

        if (options.MutationBatchSize > options.ScanSize)
        {
            failures.Add(
                $"{ReservationRetentionOptions.SectionName}:MutationBatchSize cannot exceed ScanSize.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

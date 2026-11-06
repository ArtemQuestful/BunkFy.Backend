namespace BunkFy.Modules.Properties.Application;

internal static class PropertiesObservationTime
{
    // The upper bound leaves room for the runtime probe's complete six-year
    // comparison window without overflowing DateTimeOffset.
    public const int MinimumSupportedYear = 1900;
    public const int MaximumSupportedYear = 9993;

    public static bool IsValid(DateTimeOffset value) =>
        value != default &&
        value.Offset == TimeSpan.Zero &&
        value.Year is >= MinimumSupportedYear and <= MaximumSupportedYear;

    public static void ThrowIfInvalid(
        DateTimeOffset value,
        string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A supported non-default UTC observation instant is required.");
        }
    }
}

namespace BunkFy.Modules.DataRights.Application;

using Gma.Framework.Runtime.Time;

internal static class DataRightsDeadlineExecutor
{
    public static async Task<DataRightsDeadlineExecution<T>> ExecuteAsync<T>(
        ISystemClock clock,
        DateTimeOffset deadlineUtc,
        string timeoutCode,
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeoutCode);
        ArgumentNullException.ThrowIfNull(execute);

        TimeSpan remaining = deadlineUtc - clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(timeoutCode);
        }

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        try
        {
            T result = await execute(deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            deadline.Token.ThrowIfCancellationRequested();
            DateTimeOffset observedAtUtc = clock.UtcNow;
            if (observedAtUtc >= deadlineUtc)
            {
                throw new TimeoutException(timeoutCode);
            }

            return new(result, observedAtUtc);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutCode);
        }
    }
}

internal readonly record struct DataRightsDeadlineExecution<T>(
    T Value,
    DateTimeOffset ObservedAtUtc);

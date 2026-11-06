namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using Gma.Framework.ProjectionRebuild;
using Gma.Framework.Runtime.Time;

internal sealed class ReservationPropertyProjectionRebuildWriter(
    IReservationArrivalReminderRepository reminders,
    IReservationPropertyPolicyRepository policies,
    ReservationsDbContext dbContext,
    ISystemClock clock)
    : IReservationPropertyProjectionRebuildWriter
{
    public async Task<ProjectionWriteResult> WriteAsync(
        ProjectionRebuildRequest request,
        IReadOnlyCollection<PropertyTopologyProjectionExport> snapshots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (request.DryRun)
        {
            return new(writtenCount: 0, skippedCount: snapshots.Count);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        foreach (PropertyTopologyProjectionExport property in snapshots)
        {
            await reminders.ApplyPropertyAsync(
                new(
                    property.TenantId,
                    property.PropertyId,
                    property.TimeZoneId,
                    property.Status == PropertyStatus.Active,
                    property.Version,
                    nowUtc),
                cancellationToken).ConfigureAwait(false);
            await policies.ApplyPolicyAsync(
                new(
                    property.TenantId,
                    property.PropertyId,
                    property.ProcessingStatus,
                    property.GovernancePolicy,
                    property.Version),
                cancellationToken).ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(snapshots.Count);
    }
}

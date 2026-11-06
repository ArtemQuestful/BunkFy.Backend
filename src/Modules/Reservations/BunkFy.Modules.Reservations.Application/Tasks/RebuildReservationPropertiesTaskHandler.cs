namespace BunkFy.Modules.Reservations.Application.Tasks;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.ProjectionRebuild;
using Gma.Framework.ProjectionRebuild.Tasks;
using Gma.Framework.Tasks;

internal sealed class RebuildReservationPropertiesTaskHandler(
    IPropertiesTopologyProjectionExportSource source,
    IReservationPropertyProjectionRebuildWriter writer,
    TaskProjectionRebuildRunner<PropertyTopologyProjectionExport> runner)
    : ITaskHandler<RebuildReservationPropertiesPayload>
{
    public Task HandleAsync(
        RebuildReservationPropertiesPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken) => runner.RunAsync(
        ReservationsModuleMetadata.Name,
        new ProjectionRebuildRequest(
            ReservationsModuleMetadata.PropertyProjectionName,
            payload.ProjectionVersion,
            payload.BatchSize,
            payload.DryRun,
            payload.Cursor),
        source,
        writer,
        context,
        scopeAware: true,
        cancellationToken);
}

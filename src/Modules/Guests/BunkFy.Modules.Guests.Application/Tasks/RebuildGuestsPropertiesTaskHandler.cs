namespace BunkFy.Modules.Guests.Application.Tasks;

using Gma.Framework.ProjectionRebuild;
using Gma.Framework.ProjectionRebuild.Tasks;
using Gma.Framework.Tasks;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Properties.Contracts;

internal sealed class RebuildGuestsPropertiesTaskHandler(
    IPropertiesTopologyProjectionExportSource source,
    IGuestsPropertiesProjectionRebuildWriter writer,
    TaskProjectionRebuildRunner<PropertyTopologyProjectionExport> runner)
    : ITaskHandler<RebuildGuestsPropertiesPayload>
{
    public Task HandleAsync(
        RebuildGuestsPropertiesPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        ProjectionRebuildRequest request = new(
            GuestsModuleMetadata.PropertiesProjectionName,
            payload.ProjectionVersion,
            payload.BatchSize,
            payload.DryRun,
            payload.Cursor);

        return runner.RunAsync(
            GuestsModuleMetadata.Name,
            request,
            source,
            writer,
            context,
            scopeAware: true,
            cancellationToken);
    }
}

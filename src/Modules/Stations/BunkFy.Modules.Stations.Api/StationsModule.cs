namespace BunkFy.Modules.Stations.Api;

using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Persistence;
using Gma.Framework.Api.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public sealed class StationsModule : IModule
{
    public string Name => StationsModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Configuration.GetSection(StationApiOptions.SectionName).Get<StationApiOptions>() ?? new();
        builder.Services.AddOptions<StationApiOptions>()
            .Bind(builder.Configuration.GetSection(StationApiOptions.SectionName))
            .Validate(o => o.IsValid(), "Invalid Stations HTTP settings.").ValidateOnStart();
        if (!options.Enabled)
        { return; }
        if (!options.IsValid())
        { throw new InvalidOperationException("Stations HTTP requires an explicit HTTPS origin allowlist."); }

        // This guard rejects unsupported providers before any station route is registered.
        builder.AddStationsPersistence();
        builder.Services.AddStationsCore(o => builder.Configuration.GetSection("Stations:Core").Bind(o));
        builder.Services.AddStationsRuntime();
        // The existing host data-protection setup owns key storage and application isolation.
        builder.Services.AddDataProtection();
        builder.Services.TryAddScoped<StationHttpSecurity>();
        // Host separately calls AddStationsManagement with its real destructive-operation
        // assurance requirement and Auth scope, and supplies IStationPepperProvider.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!endpoints.ServiceProvider.GetRequiredService<IOptions<StationApiOptions>>().Value.Enabled)
        { return; }
        _ = endpoints.ServiceProvider.GetRequiredService<StationPrimaryAssurance>();
        _ = endpoints.ServiceProvider.GetRequiredService<IStationPepperProvider>();
        StationWorkspaceSettingsEndpoints.Map(endpoints, this.Name);
        StationManagementEndpoints.Map(endpoints, this.Name);
        StationSetupEndpoints.Map(endpoints, this.Name);
        StationRuntimeEndpoints.Map(endpoints, this.Name);
    }
}

public static class StationHttpPipelineExtensions
{
    /// <summary>Call before UseAuthentication, so mixed runtime credentials never reach Auth owners.</summary>
    public static IApplicationBuilder UseStationHttpSecurity(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        StationApiOptions options = app.ApplicationServices.GetRequiredService<IOptions<StationApiOptions>>().Value;
        if (!options.Enabled)
        { return app; }
        return app.Use(async (context, next) =>
        {
            if (StationHttpSecurity.IsStationPath(context.Request.Path))
            {
                StationHttpSecurity.MarkSensitive(context);
                IResult? failure = StationHttpSecurity.CheckLane(context, options, out _);
                if (failure is not null)
                {
                    await failure.ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }
            }
            await next(context).ConfigureAwait(false);
        });
    }
}

namespace BunkFy.Modules.DataRights.Persistence;

using BunkFy.Modules.DataRights.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

internal sealed class DataRightsTenantTerminationReplayStartupValidator(
    IOptions<DataRightsTenantTerminationReplayOptions> options,
    IServiceProvider services,
    IHostEnvironment environment)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ITenantTerminationReplayStore store =
            services.GetRequiredService<ITenantTerminationReplayStore>();
        TenantTerminationReplayStoreReadiness readiness =
            await store.CheckReadinessAsync(cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!readiness.IsReady)
        {
            throw new TenantTerminationReplayStoreException(
                readiness.FailureCode ??
                    LocalFileTenantTerminationReplayStore.UnavailableCode,
                "The tenant-termination replay store is not ready.");
        }

        bool local = options.Value.Provider ==
            DataRightsTenantTerminationReplayProvider.LocalFile;
        bool providerMismatch = local != string.Equals(
            readiness.Provider,
            LocalFileTenantTerminationReplayStore.ProviderName,
            StringComparison.Ordinal);
        if (providerMismatch)
        {
            throw new InvalidOperationException(
                "The registered tenant-termination replay store does not match the configured provider.");
        }

        if (environment.IsProduction() && !readiness.IsProductionGrade)
        {
            throw new InvalidOperationException(
                "The production tenant-termination replay store must report production-grade durability.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

namespace BunkFy.Modules.DataRights.Persistence;

using BunkFy.Modules.DataRights.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

internal sealed class DataRightsLedgerDeltaStartupValidator(
    IOptions<DataRightsLedgerDeltaOptions> options,
    IServiceProvider services,
    IHostEnvironment environment)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        DataRightsLedgerDeltaOptions configured = options.Value;
        IDataRightsLedgerDeltaStore? store =
            services.GetService<IDataRightsLedgerDeltaStore>();
        if (store is null)
        {
            throw new InvalidOperationException(
                "A data-rights ledger delta store must be registered for the configured provider.");
        }

        DataRightsLedgerDeltaStoreReadiness readiness =
            await store.CheckReadinessAsync(cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!readiness.IsReady)
        {
            throw new DataRightsLedgerDeltaStoreException(
                readiness.FailureCode ??
                    LocalFileDataRightsLedgerDeltaStore.UnavailableCode,
                "The data-rights ledger delta store is not ready.");
        }

        bool providerMismatch =
            configured.Provider == DataRightsLedgerDeltaProvider.LocalFile
                ? !string.Equals(
                    readiness.Provider,
                    LocalFileDataRightsLedgerDeltaStore.ProviderName,
                    StringComparison.Ordinal)
                : string.Equals(
                    readiness.Provider,
                    LocalFileDataRightsLedgerDeltaStore.ProviderName,
                    StringComparison.Ordinal);
        if (providerMismatch)
        {
            throw new InvalidOperationException(
                "The registered data-rights ledger delta store does not match the configured provider.");
        }

        if (environment.IsProduction() && !readiness.IsProductionGrade)
        {
            throw new InvalidOperationException(
                "The production data-rights ledger delta store must report production-grade durability.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

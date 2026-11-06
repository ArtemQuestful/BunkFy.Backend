namespace BunkFy.Modules.DataRights.Application.Production;

using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Results;

internal sealed class TenantTerminationProductionReadinessProbe(
    ITenantTerminationProductionCatalog catalog,
    ITenantTerminationReplayStore replayStore)
    : ITenantTerminationProductionReadinessProbe
{
    public async Task<TenantTerminationProductionReadinessEvidence> CheckAsync(
        IReadOnlyCollection<string> requiredOwnerKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requiredOwnerKeys);

        Result<TenantTerminationProductionCatalogEvidence> catalogResult =
            catalog.Validate(requiredOwnerKeys);
        if (catalogResult.IsFailure)
        {
            return new(
                IsCatalogValid: false,
                OwnerCount: 0,
                ExportOwnerCount: 0,
                TerminalOwnerKey: string.Empty,
                CatalogSha256: string.Empty,
                ReplayStoreProvider: string.Empty,
                IsReplayStoreReady: false,
                IsReplayStoreProductionGrade: false,
                ReplayStoreFailureCode: null);
        }

        TenantTerminationReplayStoreReadiness replayReadiness =
            await replayStore.CheckReadinessAsync(cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        TenantTerminationProductionCatalogEvidence catalogEvidence =
            catalogResult.Value;
        return new(
            IsCatalogValid: true,
            catalogEvidence.OwnerCount,
            catalogEvidence.ExportOwnerCount,
            catalogEvidence.TerminalOwnerKey,
            catalogEvidence.CatalogSha256,
            replayReadiness.Provider,
            replayReadiness.IsReady,
            replayReadiness.IsProductionGrade,
            replayReadiness.FailureCode);
    }
}

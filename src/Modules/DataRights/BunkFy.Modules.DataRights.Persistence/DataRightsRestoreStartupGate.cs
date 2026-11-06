namespace BunkFy.Modules.DataRights.Persistence;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Ports;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal sealed class DataRightsRestoreStartupGate(
    IDataRightsRestoreScopeSource scopeSource,
    IDataRightsLedgerDeltaStore deltaStore,
    IServiceScopeFactory scopeFactory,
    DataRightsRestoreReadinessState readinessState,
    IHostEnvironment environment,
    TimeProvider timeProvider)
    : IHostedService
{
    private const int ScopePageSize = 100;
    private const int MaximumSnapshotPasses = 4;
    private const string FailedCode = "data-rights.restore.failed";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        readinessState.MarkStarting();
        try
        {
            await this.EnsureProvidersReadyAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            for (int pass = 0; pass < MaximumSnapshotPasses; pass++)
            {
                DataRightsRestoreScopeSnapshot snapshot =
                    await scopeSource.OpenSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                bool retry;
                try
                {
                    retry = await this.ReconcileSnapshotAsync(
                        snapshot,
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (DataRightsRestoreScopeSourceException exception)
                    when (string.Equals(
                        exception.Code,
                        DataRightsRestoreScopeSourceException
                            .SnapshotChangedCode,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (retry)
                {
                    continue;
                }

                bool isCurrent = await scopeSource.IsCurrentAsync(
                    snapshot,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (isCurrent)
                {
                    readinessState.MarkReady(
                        timeProvider.GetUtcNow(),
                        snapshot.SnapshotSha256);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            readinessState.MarkFailed("data-rights.restore.cancelled");
            throw;
        }
        catch (Exception exception)
        {
            readinessState.MarkFailed(StatusCode(exception));
            throw new InvalidOperationException(
                "Data-rights restore readiness verification failed.");
        }

        readinessState.MarkFailed(
            "data-rights.restore.snapshot-changed");
        throw new InvalidOperationException(
            "Data-rights restore readiness could not stabilize its recovery snapshot.");
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task EnsureProvidersReadyAsync(
        CancellationToken cancellationToken)
    {
        DataRightsLedgerDeltaStoreReadiness deltaReadiness =
            await deltaStore.CheckReadinessAsync(cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        DataRightsRestoreScopeSourceReadiness scopeReadiness =
            await scopeSource.CheckReadinessAsync(cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!deltaReadiness.IsReady ||
            !scopeReadiness.IsReady ||
            !string.Equals(
                deltaReadiness.Provider,
                scopeReadiness.Provider,
                StringComparison.Ordinal) ||
            (environment.IsProduction() &&
             (!deltaReadiness.IsProductionGrade ||
              !scopeReadiness.IsProductionGrade)))
        {
            throw new InvalidOperationException(
                "The data-rights recovery providers are not ready.");
        }
    }

    private async Task<bool> ReconcileSnapshotAsync(
        DataRightsRestoreScopeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        string? afterScopeId = null;
        while (true)
        {
            DataRightsRestoreScopePage page =
                await scopeSource.ReadScopesAsync(
                    snapshot,
                    afterScopeId,
                    ScopePageSize,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasValidPage(page))
            {
                throw new InvalidOperationException(
                    "The data-rights recovery scope page is invalid.");
            }

            foreach (DataRightsRestoreScope restoreScope in page.Scopes)
            {
                await using AsyncServiceScope serviceScope =
                    scopeFactory.CreateAsyncScope();
                serviceScope.ServiceProvider
                    .GetRequiredService<IScopeContextAccessor>()
                    .SetScope(restoreScope.ScopeId);
                IDataRightsRestoreCoordinator coordinator =
                    serviceScope.ServiceProvider.GetRequiredService<
                        IDataRightsRestoreCoordinator>();
                Result<Gma.Framework.Cqrs.Unit> result =
                    await coordinator.ReconcileAsync(
                        restoreScope,
                        snapshot.SnapshotSha256,
                        cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.IsFailure)
                {
                    if (string.Equals(
                            result.Error.Code,
                            DataRightsApplicationErrors
                                .RestoreCheckpointConflict.Code,
                            StringComparison.Ordinal))
                    {
                        return true;
                    }

                    throw new DataRightsRestoreReconciliationException(
                        result.Error.Code);
                }
            }

            if (!page.HasMore)
            {
                return false;
            }

            afterScopeId = page.NextScopeId;
        }
    }

    private static bool HasValidPage(
        DataRightsRestoreScopePage? page) =>
        page is not null &&
        page.ContractVersion ==
            DataRightsRestoreScopePage.CurrentContractVersion &&
        page.Scopes is not null &&
        page.Scopes.Count <= ScopePageSize &&
        (page.HasMore
            ? page.Scopes.Count > 0 &&
              !string.IsNullOrWhiteSpace(page.NextScopeId) &&
              string.Equals(
                  page.NextScopeId,
                  page.Scopes[^1].ScopeId,
                  StringComparison.Ordinal)
            : page.NextScopeId is null);

    private static string StatusCode(Exception exception) =>
        exception switch
        {
            DataRightsLedgerDeltaStoreException deltaException =>
                deltaException.Code,
            DataRightsRestoreScopeSourceException sourceException =>
                sourceException.Code,
            DataRightsRestoreReconciliationException restoreException =>
                restoreException.Code,
            _ => FailedCode
        };

    private sealed class DataRightsRestoreReconciliationException(
        string code)
        : Exception("A data-rights restore owner failed reconciliation.")
    {
        public string Code { get; } = code;
    }
}

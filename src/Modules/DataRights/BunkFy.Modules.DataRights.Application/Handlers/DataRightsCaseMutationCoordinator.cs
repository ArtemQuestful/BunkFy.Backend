namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using Gma.Framework.Scoping;

internal sealed class DataRightsCaseMutationCoordinator(
    IDataRightsCaseRepository cases,
    IDataRightsCaseIdentityRepository caseIdentities,
    ITenantTerminationCaseRepository tenantTerminationCases,
    IDataRightsOperationLock operationLock,
    IScopeContext scopeContext)
{
    public async Task<DataRightsCase?> AcquireCreationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!this.HasValidCoordinates(operationId))
        {
            return null;
        }

        await operationLock.AcquireCaseWriteAsync(
            operationId,
            cancellationToken).ConfigureAwait(false);
        return this.Validate(await caseIdentities.GetByIdAsync(
                operationId,
                cancellationToken).ConfigureAwait(false),
            operationId);
    }

    public async Task<DataRightsCase?> AcquireAsync(
        DataRightsCaseScope scope,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!this.HasValidCoordinates(caseId))
        {
            return null;
        }

        await operationLock.AcquireCaseWriteAsync(caseId, cancellationToken)
            .ConfigureAwait(false);
        return this.Validate(await cases.GetAsync(
                scope,
                caseId,
                cancellationToken).ConfigureAwait(false),
            caseId);
    }

    public async Task<DataRightsCase?> AcquireTenantTerminationCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        if (!this.HasValidCoordinates(caseId))
        {
            return null;
        }

        await operationLock.AcquireCaseWriteAsync(caseId, cancellationToken)
            .ConfigureAwait(false);
        return this.Validate(await tenantTerminationCases.GetAsync(
                caseId,
                cancellationToken).ConfigureAwait(false),
            caseId);
    }

    private bool HasValidCoordinates(Guid caseId) =>
        caseId != Guid.Empty &&
        scopeContext.IsEnabled &&
        !string.IsNullOrWhiteSpace(scopeContext.ScopeId);

    private DataRightsCase? Validate(
        DataRightsCase? dataRightsCase,
        Guid caseId) =>
        dataRightsCase is not null &&
        dataRightsCase.Id == caseId &&
        string.Equals(
            dataRightsCase.ScopeId,
            scopeContext.ScopeId,
            StringComparison.Ordinal)
                ? dataRightsCase
                : null;
}

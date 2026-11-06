namespace BunkFy.Modules.DataRights.Persistence;

using BunkFy.Modules.DataRights.Application.Ports;
using Gma.Framework.FileManagement;
using Gma.Framework.Scoping;

internal sealed class DataRightsExportArtifactObjectStore(
    IFileStorage storage,
    IScopeContext scopeContext) : IDataRightsExportArtifactObjectStore,
        ITenantTerminationExportObjectStore
{
    public Task<bool> DeleteAsync(
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            throw new InvalidOperationException(
                "DataRights.ExportArtifactTenantRequired");
        }

        FileStorageObjectKey key = DataRightsExportStorageKey.Create(
            scopeContext.ScopeId,
            artifactId);
        return storage.DeleteAsync(key, cancellationToken);
    }

    public Task<bool> DeleteArtifactAsync(
        Guid processId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        string tenantId = this.RequireTenant();
        FileStorageObjectKey key = DataRightsExportStorageKey
            .CreateTenantTerminationArtifact(
                tenantId,
                processId,
                artifactId);
        return storage.DeleteAsync(key, cancellationToken);
    }

    public Task<bool> DeleteFragmentAsync(
        Guid processId,
        Guid fragmentId,
        CancellationToken cancellationToken)
    {
        string tenantId = this.RequireTenant();
        FileStorageObjectKey key = DataRightsExportStorageKey
            .CreateTenantTerminationFragment(
                tenantId,
                processId,
                fragmentId);
        return storage.DeleteAsync(key, cancellationToken);
    }

    private string RequireTenant()
    {
        if (!scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            throw new InvalidOperationException(
                "DataRights.ExportArtifactTenantRequired");
        }

        return scopeContext.ScopeId;
    }
}

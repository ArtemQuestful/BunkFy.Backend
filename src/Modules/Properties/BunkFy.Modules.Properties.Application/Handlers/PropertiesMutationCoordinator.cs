namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Scoping;

internal sealed class PropertiesMutationCoordinator(
    IPropertyRepository properties,
    IRoomRepository rooms,
    IPropertiesOperationLock operationLock,
    IPropertiesUniqueCoordinateLock uniqueCoordinates,
    IScopeContext scopeContext)
{
    public Task<Property?> AcquirePropertyAsync(
        Guid propertyId,
        CancellationToken cancellationToken) =>
        this.AcquirePropertyAndReloadAsync(propertyId, cancellationToken);

    public async Task<bool> AcquirePropertyOperationAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        if (!this.TryGetTenantId(propertyId, out string tenantId))
        {
            return false;
        }

        return await operationLock.TryAcquirePropertyAsync(
            tenantId,
            propertyId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Property?> ReloadPropertyAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        if (!this.TryGetTenantId(propertyId, out string tenantId))
        {
            return null;
        }

        Property? property = await properties.GetAsync(
            propertyId,
            cancellationToken).ConfigureAwait(false);
        return property is not null &&
               property.Id == propertyId &&
               string.Equals(
                   property.ScopeId,
                   tenantId,
                   StringComparison.Ordinal)
            ? property
            : null;
    }

    public Task<Room?> AcquireRoomAsync(
        Guid roomId,
        CancellationToken cancellationToken) => this.AcquireAndReloadAsync(
        roomId,
        operationLock.TryAcquireRoomAsync,
        rooms.GetAsync,
        cancellationToken);

    public Task AcquirePropertyCodeAsync(
        PropertyCode code,
        CancellationToken cancellationToken)
    {
        string tenantId = this.GetRequiredTenantId();
        if (code.Value.Length == 0)
        {
            throw new InvalidOperationException(
                "A Properties code coordinate must be normalized before locking.");
        }

        return uniqueCoordinates.AcquirePropertyCodeAsync(
            tenantId,
            code.Value,
            cancellationToken);
    }

    public Task AcquireRoomNameAsync(
        Guid propertyId,
        RoomName roomName,
        CancellationToken cancellationToken)
    {
        string tenantId = this.GetRequiredTenantId();
        if (propertyId == Guid.Empty || roomName.Value.Length == 0)
        {
            throw new InvalidOperationException(
                "A Properties room-name coordinate must be normalized before locking.");
        }

        return uniqueCoordinates.AcquireRoomNameAsync(
            tenantId,
            propertyId,
            roomName.Value,
            cancellationToken);
    }

    private async Task<TAggregate?> AcquireAndReloadAsync<TAggregate>(
        Guid aggregateId,
        Func<string, Guid, CancellationToken, Task<bool>> acquire,
        Func<Guid, CancellationToken, Task<TAggregate?>> reload,
        CancellationToken cancellationToken)
        where TAggregate : class
    {
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(reload);
        if (!this.TryGetTenantId(aggregateId, out string tenantId) ||
            !await acquire(
                tenantId,
                aggregateId,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        TAggregate? aggregate = await reload(
            aggregateId,
            cancellationToken).ConfigureAwait(false);
        return aggregate switch
        {
            Property property when
                property.Id == aggregateId &&
                string.Equals(
                    property.ScopeId,
                    tenantId,
                    StringComparison.Ordinal) => aggregate,
            Room room when
                room.Id == aggregateId &&
                string.Equals(
                    room.ScopeId,
                    tenantId,
                    StringComparison.Ordinal) => aggregate,
            _ => null
        };
    }

    private async Task<Property?> AcquirePropertyAndReloadAsync(
        Guid propertyId,
        CancellationToken cancellationToken) =>
        await this.AcquirePropertyOperationAsync(
            propertyId,
            cancellationToken).ConfigureAwait(false)
            ? await this.ReloadPropertyAsync(
                propertyId,
                cancellationToken).ConfigureAwait(false)
            : null;

    private string GetRequiredTenantId()
    {
        string tenantId = scopeContext.IsEnabled
            ? scopeContext.ScopeId?.Trim() ?? string.Empty
            : string.Empty;
        if (tenantId.Length > 0)
        {
            return tenantId;
        }

        throw new InvalidOperationException(
            "A Properties uniqueness lock requires an active tenant scope.");
    }

    private bool TryGetTenantId(Guid aggregateId, out string tenantId)
    {
        tenantId = scopeContext.IsEnabled
            ? scopeContext.ScopeId?.Trim() ?? string.Empty
            : string.Empty;
        return aggregateId != Guid.Empty && tenantId.Length > 0;
    }
}

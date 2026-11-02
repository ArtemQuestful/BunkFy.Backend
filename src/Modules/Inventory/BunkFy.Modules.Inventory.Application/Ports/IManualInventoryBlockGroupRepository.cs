namespace BunkFy.Modules.Inventory.Application.Ports;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;

public interface IManualInventoryBlockGroupRepository
{
    Task<ManualInventoryBlockGroup?> GetAsync(
        Guid propertyId,
        Guid blockGroupId,
        CancellationToken cancellationToken);

    Task ReloadAsync(
        ManualInventoryBlockGroup blockGroup,
        CancellationToken cancellationToken);

    Task AddAsync(
        ManualInventoryBlockGroup blockGroup,
        CancellationToken cancellationToken);

    Task<ManualInventoryBlockGroupDto?> GetDtoAsync(
        Guid propertyId,
        Guid blockGroupId,
        CancellationToken cancellationToken);

    Task<ManualInventoryBlockGroupListResponse> ListAsync(
        Guid propertyId,
        ManualInventoryBlockGroupStatus? status,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);
}

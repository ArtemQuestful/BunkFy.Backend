namespace BunkFy.Modules.Inventory.Application.Handlers;

using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;

internal static class ManualInventoryBlockGroupDigest
{
    private const string SelectionPrefix =
        "bunkfy-inventory-manual-block-group-selection/v2";
    private const string MembershipPrefix =
        "bunkfy-inventory-manual-block-group-membership/v1";

    public static string ComputeSelection(
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        IReadOnlyCollection<InventoryBlockSelectionCoordinate> coordinates) => Compute(
            BuildSelectionPayload(target, arrival, departure, coordinates));

    public static string ComputeMembership(
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        IReadOnlyCollection<Guid> inventoryUnitIds) => Compute(
            BuildCanonicalPayload(
                MembershipPrefix,
                target,
                arrival,
                departure,
                inventoryUnitIds));

    internal static string BuildMembershipPayload(
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        IReadOnlyCollection<Guid> inventoryUnitIds) => BuildCanonicalPayload(
            MembershipPrefix,
            target,
            arrival,
            departure,
            inventoryUnitIds);

    internal static string BuildSelectionPayload(
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        IReadOnlyCollection<InventoryBlockSelectionCoordinate> coordinates)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(coordinates);
        string building = target.BuildingLabel?.Trim() ?? string.Empty;
        string floor = target.FloorLabel?.Trim() ?? string.Empty;
        string room = target.RoomId?.ToString("N") ?? string.Empty;
        string unit = target.InventoryUnitId?.ToString("N") ?? string.Empty;
        StringBuilder payload = new(SelectionPrefix);
        payload.Append("|kind=").Append(Invariant((int)target.Kind))
            .Append("|building=").Append(Invariant(Encoding.UTF8.GetByteCount(building))).Append(':').Append(building)
            .Append("|floor=").Append(Invariant(Encoding.UTF8.GetByteCount(floor))).Append(':').Append(floor)
            .Append("|room=").Append(room)
            .Append("|unit=").Append(unit)
            .Append("|arrival=").Append(arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("|departure=").Append(departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("|coordinates=").Append(Invariant(coordinates.Count));
        foreach (InventoryBlockSelectionCoordinate coordinate in coordinates
                     .OrderBy(item => GuidN(item.InventoryUnitId), StringComparer.Ordinal))
        {
            payload.Append('|').Append(GuidN(coordinate.InventoryUnitId))
                .Append(',').Append(Invariant(coordinate.PropertySourceVersion))
                .Append(',').Append(Invariant(coordinate.PropertyDetailsVersion))
                .Append(',').Append(Invariant(coordinate.PropertyAvailabilitySelectionVersion))
                .Append(',').Append(Invariant(coordinate.PropertyStatus))
                .Append(',').Append(GuidN(coordinate.RoomId))
                .Append(',').Append(Invariant(coordinate.RoomSourceVersion))
                .Append(',').Append(Invariant(coordinate.RoomDetailsVersion))
                .Append(',').Append(Invariant(coordinate.RoomStatus))
                .Append(',').Append(Invariant(coordinate.RoomSalesMode))
                .Append(',').Append(Invariant(coordinate.RoomConfigurationVersion))
                .Append(',').Append(Invariant(coordinate.RoomAvailabilityMutationVersion))
                .Append(',').Append(Invariant(coordinate.UnitSourceVersion))
                .Append(',').Append(Invariant(coordinate.UnitDetailsVersion))
                .Append(',').Append(coordinate.UnitTopologyActive ? 1 : 0)
                .Append(',').Append(Invariant(coordinate.UnitAvailabilityMutationVersion))
                .Append(",r=").Append(Invariant(coordinate.Retirements.Count));
            foreach (InventoryBlockSelectionRetirementCoordinate retirement in coordinate.Retirements
                         .OrderBy(item => item.Kind)
                         .ThenBy(item => GuidN(item.TopologyChangeId), StringComparer.Ordinal))
            {
                payload.Append(',').Append(Invariant(retirement.Kind))
                    .Append(':').Append(GuidN(retirement.TopologyChangeId))
                    .Append(':').Append(Invariant(retirement.Version))
                    .Append(':').Append(Invariant(retirement.State));
            }
        }

        return payload.ToString();
    }

    private static string BuildCanonicalPayload(
        string prefix,
        InventoryBlockTarget target,
        DateOnly? arrival,
        DateOnly? departure,
        IReadOnlyCollection<Guid> inventoryUnitIds)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(inventoryUnitIds);
        string building = target.BuildingLabel?.Trim() ?? string.Empty;
        string floor = target.FloorLabel?.Trim() ?? string.Empty;
        string room = target.RoomId?.ToString("N") ?? string.Empty;
        string unit = target.InventoryUnitId?.ToString("N") ?? string.Empty;
        string[] members = inventoryUnitIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Select(GuidN)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        StringBuilder payload = new(prefix);
        payload.Append("|kind=").Append(Invariant((int)target.Kind))
            .Append("|building=").Append(Invariant(Encoding.UTF8.GetByteCount(building))).Append(':').Append(building)
            .Append("|floor=").Append(Invariant(Encoding.UTF8.GetByteCount(floor))).Append(':').Append(floor)
            .Append("|room=").Append(room)
            .Append("|unit=").Append(unit);
        if (arrival.HasValue && departure.HasValue)
        {
            payload.Append("|arrival=").Append(arrival.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append("|departure=").Append(departure.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        payload.Append("|members=").Append(Invariant(members.Length)).Append('|')
            .AppendJoin(',', members);
        return payload.ToString();
    }

    private static string Compute(string payload) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static string GuidN(Guid value) => value.ToString("N");

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

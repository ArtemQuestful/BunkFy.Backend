namespace BunkFy.Modules.Reservations.Application.StayAmendments;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Reservations.Application.Commands;

internal static class ReservationStayAmendmentFingerprint
{
    public static string ComputeV2(AmendReservationStayCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string canonical = string.Join(
            '|',
            "v2",
            $"reservation={command.ReservationId:N}",
            $"operation={command.OperationId:N}",
            $"expected-details-revision={command.ExpectedDetailsRevision.ToString(CultureInfo.InvariantCulture)}",
            $"arrival={command.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"departure={command.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"expected-arrival-time={FormatTime(command.ExpectedArrivalTime)}",
            $"expected-departure-time={FormatTime(command.ExpectedDepartureTime)}",
            $"units={string.Join(',', command.InventoryUnitIds.Order().Select(id => id.ToString("N")))}");
        return Hash(canonical);
    }

    public static string ComputeLegacyV1(ReassignReservationInventoryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string canonical = string.Join(
            '|',
            command.ReservationId.ToString("N"),
            command.AmendmentRequestId.ToString("N"),
            command.ExpectedDetailsRevision.ToString(CultureInfo.InvariantCulture),
            string.Join(',', command.InventoryUnitIds.Order().Select(id => id.ToString("N"))));
        return Hash(canonical);
    }

    private static string FormatTime(TimeOnly? time) =>
        time?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "-";

    private static string Hash(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}

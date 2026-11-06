namespace BunkFy.Modules.Reservations.Tests;

using System.Globalization;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationStayAmendmentFingerprintTests
{
    [Fact]
    public void V2_is_order_independent_for_units()
    {
        Guid firstUnitId = Guid.NewGuid();
        Guid secondUnitId = Guid.NewGuid();
        AmendReservationStayCommand command = Command([firstUnitId, secondUnitId]);

        string forward = ReservationStayAmendmentFingerprint.ComputeV2(command);
        string reverse = ReservationStayAmendmentFingerprint.ComputeV2(
            command with { InventoryUnitIds = [secondUnitId, firstUnitId] });

        Assert.Equal(forward, reverse);
        Assert.Equal(64, forward.Length);
        Assert.Equal(forward.ToLowerInvariant(), forward);
    }

    [Fact]
    public void V2_binds_every_durable_request_coordinate_and_target_field()
    {
        AmendReservationStayCommand command = Command([Guid.NewGuid()]);
        string baseline = ReservationStayAmendmentFingerprint.ComputeV2(command);
        string[] changed =
        [
            ReservationStayAmendmentFingerprint.ComputeV2(
                command with { ReservationId = Guid.NewGuid() }),
            ReservationStayAmendmentFingerprint.ComputeV2(
                command with { OperationId = Guid.NewGuid() }),
            ReservationStayAmendmentFingerprint.ComputeV2(
                command with { ExpectedDetailsRevision = command.ExpectedDetailsRevision + 1 }),
            ReservationStayAmendmentFingerprint.ComputeV2(
                command with { Arrival = command.Arrival.AddDays(1) }),
            ReservationStayAmendmentFingerprint.ComputeV2(
                command with { Departure = command.Departure.AddDays(1) }),
            ReservationStayAmendmentFingerprint.ComputeV2(
                command with { ExpectedArrivalTime = new TimeOnly(16, 0) }),
            ReservationStayAmendmentFingerprint.ComputeV2(
                command with { ExpectedDepartureTime = new TimeOnly(9, 0) }),
            ReservationStayAmendmentFingerprint.ComputeV2(
                command with { InventoryUnitIds = [Guid.NewGuid()] })
        ];

        Assert.All(changed, fingerprint => Assert.NotEqual(baseline, fingerprint));
        Assert.Equal(changed.Length, changed.Distinct().Count());
    }

    [Theory]
    [InlineData("fa-IR")]
    [InlineData("th-TH")]
    public void V2_is_invariant_across_non_Gregorian_current_cultures(
        string cultureName)
    {
        AmendReservationStayCommand command = Command([Guid.NewGuid()]);
        string baseline = ReservationStayAmendmentFingerprint.ComputeV2(command);
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            Assert.Equal(
                baseline,
                ReservationStayAmendmentFingerprint.ComputeV2(command));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Legacy_v1_preserves_existing_inventory_reassignment_fingerprint()
    {
        ReassignReservationInventoryCommand command = new(
            Guid.NewGuid(),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            [Guid.Parse("33333333-3333-3333-3333-333333333333")],
            7,
            "user:operator-a");
        string canonical = string.Join(
            '|',
            command.ReservationId.ToString("N"),
            command.AmendmentRequestId.ToString("N"),
            "7",
            "33333333333333333333333333333333");
        string expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical)));

        Assert.Equal(expected, ReservationStayAmendmentFingerprint.ComputeLegacyV1(command));
    }

    private static AmendReservationStayCommand Command(
        IReadOnlyCollection<Guid> unitIds) => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 8, 20),
            new DateOnly(2026, 8, 23),
            new TimeOnly(15, 0),
            new TimeOnly(10, 0),
            unitIds,
            3,
            "user:operator-a");
}

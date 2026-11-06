namespace BunkFy.Modules.Reservations.Application.Stations;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts.Stations;

internal static class StationOperationProvenance
{
    internal static bool ValidNew(StationCheckInProvenance provenance) => provenance.IsValid &&
        (provenance.Authority != StationReservationAuthority.StationOnly || provenance.JobGrantRevision is > 0);

    internal static bool Matches(ReservationManagementOperationKind kind, StationCheckInProvenance? recorded,
        StationCheckInProvenance? requested)
    {
        if (recorded is null || requested is null)
        { return recorded == requested; }
        if (!recorded.IsValid || !ValidNew(requested))
        { return false; }
        if (recorded == requested)
        { return true; }
        // Only the old check-in schema omitted this job revision. All original actor coordinates remain exact.
        return kind == ReservationManagementOperationKind.CheckIn &&
            recorded.Authority == StationReservationAuthority.StationOnly && recorded.JobGrantRevision is null &&
            requested.JobGrantRevision is > 0 && recorded with { JobGrantRevision = requested.JobGrantRevision } == requested;
    }
}

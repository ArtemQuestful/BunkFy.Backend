namespace BunkFy.Modules.Staff.Contracts;

/// <summary>Owner-only exact-ID labels. Caller supplies a server-selected set and authoritative property-local date.</summary>
public interface IStaffStationLabelReader
{
    Task<IReadOnlyList<StaffStationLabel>> ResolveAsync(string scopeId, Guid propertyId,
        IReadOnlyCollection<Guid> exactStaffIds, DateOnly propertyLocalDate, CancellationToken cancellationToken = default);
}

public sealed record StaffStationLabel(Guid StaffMemberId, string DisplayName, long Version);

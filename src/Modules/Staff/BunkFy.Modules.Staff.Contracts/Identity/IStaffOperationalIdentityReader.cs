namespace BunkFy.Modules.Staff.Contracts;

public interface IStaffOperationalIdentityReader
{
    Task<StaffOperationalIdentitySnapshot?> FindAsync(
        string tenantId,
        string authSubjectId,
        CancellationToken cancellationToken = default);
}

public sealed record StaffOperationalIdentitySnapshot(
    Guid StaffMemberId,
    string AuthSubjectId,
    StaffStatus Status,
    long Version);

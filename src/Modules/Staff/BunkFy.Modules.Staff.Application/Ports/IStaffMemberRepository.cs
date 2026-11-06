namespace BunkFy.Modules.Staff.Application.Ports;

using Gma.Framework.Pagination;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;

public interface IStaffMemberRepository
{
    async Task<IReadOnlyList<StaffMemberSafetyEvidence>> ListSafetyEvidenceAsync(
        IReadOnlyList<Guid> staffMemberIds,
        CancellationToken cancellationToken)
    {
        List<StaffMemberSafetyEvidence> records = [];
        foreach (Guid staffMemberId in staffMemberIds)
        {
            StaffMember? member = await this.GetForSafetyTransitionAsync(
                staffMemberId,
                cancellationToken).ConfigureAwait(false);
            if (member is not null)
            {
                records.Add(new(
                    member.Id,
                    member.AuthSubjectId,
                    member.Status));
            }
        }

        return records;
    }

    Task AddAsync(StaffMember member, CancellationToken cancellationToken);
    Task<StaffMember?> GetAsync(Guid staffMemberId, CancellationToken cancellationToken);
    Task<StaffMember?> ReloadOperationalAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        this.GetAsync(staffMemberId, cancellationToken);
    Task<StaffMember?> GetForDataRightsAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken);
    Task<StaffMember?> GetForSafetyTransitionAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken);
    Task<StaffMember?> ReloadForSafetyTransitionAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        this.GetForSafetyTransitionAsync(
            staffMemberId,
            cancellationToken);
    Task<StaffMember?> GetForSafetyTransitionByAuthSubjectAsync(
        string authSubjectId,
        CancellationToken cancellationToken);
    Task<StaffMember?> GetByAuthSubjectAsync(string authSubjectId, CancellationToken cancellationToken);
    Task<StaffDirectoryMemberDto?> GetDirectoryAsync(Guid staffMemberId, CancellationToken cancellationToken);
    Task<StaffDirectoryMemberDto?> GetDirectoryAtPropertyAsync(
        Guid propertyId,
        Guid staffMemberId,
        CancellationToken cancellationToken);
    Task<StaffDirectoryListResponse> ListDirectoryAsync(
        string? search,
        StaffStatus? status,
        PageRequest pageRequest,
        CancellationToken cancellationToken);
    Task<StaffPropertyDirectoryListResponse> ListDirectoryAtPropertyAsync(
        Guid propertyId,
        string? search,
        StaffStatus? status,
        PageRequest pageRequest,
        CancellationToken cancellationToken);
    Task<StaffAccountDirectoryResponse> ResolveAccountDirectoryAsync(
        IReadOnlyCollection<string> authSubjectIds,
        CancellationToken cancellationToken);
    Task<bool> EmployeeNumberExistsAsync(string employeeNumber, Guid? exceptStaffMemberId,
        CancellationToken cancellationToken);
    Task<bool> AuthSubjectExistsAsync(string authSubjectId, Guid? exceptStaffMemberId,
        CancellationToken cancellationToken);
}

public sealed record StaffMemberSafetyEvidence(
    Guid StaffMemberId,
    string? AuthSubjectId,
    StaffMemberState Status);

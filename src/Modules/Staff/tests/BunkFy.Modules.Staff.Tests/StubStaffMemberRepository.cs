namespace BunkFy.Modules.Staff.Tests;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using Gma.Framework.Pagination;

internal sealed class StubStaffMemberRepository(StaffMember? member)
    : IStaffMemberRepository
{
    public Task AddAsync(
        StaffMember added,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StaffMember?> GetAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            member?.Id == staffMemberId
                ? member
                : null);

    public Task<StaffMember?> GetForDataRightsAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        this.GetAsync(staffMemberId, cancellationToken);

    public Task<StaffMember?> GetForSafetyTransitionAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StaffMember?> GetForSafetyTransitionByAuthSubjectAsync(
        string authSubjectId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StaffMember?> GetByAuthSubjectAsync(
        string authSubjectId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StaffDirectoryMemberDto?> GetDirectoryAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StaffDirectoryMemberDto?> GetDirectoryAtPropertyAsync(
        Guid propertyId,
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StaffDirectoryListResponse> ListDirectoryAsync(
        string? search,
        StaffStatus? status,
        PageRequest pageRequest,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StaffPropertyDirectoryListResponse> ListDirectoryAtPropertyAsync(
        Guid propertyId,
        string? search,
        StaffStatus? status,
        PageRequest pageRequest,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StaffAccountDirectoryResponse> ResolveAccountDirectoryAsync(
        IReadOnlyCollection<string> authSubjectIds,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> EmployeeNumberExistsAsync(
        string employeeNumber,
        Guid? exceptStaffMemberId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> AuthSubjectExistsAsync(
        string authSubjectId,
        Guid? exceptStaffMemberId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

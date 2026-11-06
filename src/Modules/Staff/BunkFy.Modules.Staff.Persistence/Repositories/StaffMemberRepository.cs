namespace BunkFy.Modules.Staff.Persistence.Repositories;

using Gma.Framework.Pagination;
using Microsoft.EntityFrameworkCore;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Entities;
using BunkFy.Modules.Staff.Persistence.Models;
using Gma.Framework.Results;

internal sealed class StaffMemberRepository(StaffDbContext dbContext)
    : IStaffMemberRepository
{
    public async Task<IReadOnlyList<StaffMemberSafetyEvidence>>
        ListSafetyEvidenceAsync(
            IReadOnlyList<Guid> staffMemberIds,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staffMemberIds);
        return await dbContext.StaffMembers
            .AsNoTracking()
            .Where(member => staffMemberIds.Contains(member.Id))
            .OrderBy(member => member.Id)
            .Select(member => new StaffMemberSafetyEvidence(
                member.Id,
                member.AuthSubjectId,
                member.Status))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task AddAsync(
        StaffMember member,
        CancellationToken cancellationToken)
    {
        dbContext.StaffMembers.Add(member);
        dbContext.OperationLocks.Add(
            new StaffOperationLock(
                member.Id,
                member.ScopeId,
                member.Id));
        Result<StaffProcessingRestrictionProjection> projection =
            StaffProcessingRestrictionProjection.Create(
                member.ScopeId,
                member.Id,
                StaffProcessingRestrictionContract.CurrentVersion,
                member.CreatedAtUtc);
        if (projection.IsFailure)
        {
            throw new InvalidOperationException(projection.Error.Code);
        }

        dbContext.ProcessingRestrictionProjections.Add(projection.Value);
        return Task.CompletedTask;
    }

    public Task<StaffMember?> GetAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        this.OperationalMembers()
            .Include(member => member.Assignments)
            .FirstOrDefaultAsync(member => member.Id == staffMemberId, cancellationToken);

    public Task<StaffMember?> ReloadOperationalAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        this.ReloadAfterOperationLockAsync(
            staffMemberId,
            operational: true,
            cancellationToken);

    public Task<StaffMember?> GetForDataRightsAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        dbContext.StaffMembers
            .Include(member => member.Assignments)
            .FirstOrDefaultAsync(
                member => member.Id == staffMemberId,
                cancellationToken);

    public Task<StaffMember?> GetForSafetyTransitionAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        dbContext.StaffMembers
            .Include(member => member.Assignments)
            .FirstOrDefaultAsync(
                member => member.Id == staffMemberId,
                cancellationToken);

    public async Task<StaffMember?> ReloadForSafetyTransitionAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        await this.ReloadAfterOperationLockAsync(
            staffMemberId,
            operational: false,
            cancellationToken).ConfigureAwait(false);

    private async Task<StaffMember?> ReloadAfterOperationLockAsync(
        Guid staffMemberId,
        bool operational,
        CancellationToken cancellationToken)
    {
        StaffMember? tracked = dbContext.StaffMembers.Local
            .SingleOrDefault(member => member.Id == staffMemberId);
        if (tracked is not null)
        {
            Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<
                StaffMember> memberEntry = dbContext.Entry(tracked);
            if (memberEntry.State != EntityState.Unchanged)
            {
                throw new InvalidOperationException(
                    "A changed Staff member cannot be reloaded after its operation lock.");
            }

            foreach (StaffPropertyAssignment assignment in
                tracked.Assignments.ToArray())
            {
                Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<
                    StaffPropertyAssignment> assignmentEntry =
                    dbContext.Entry(assignment);
                if (assignmentEntry.State != EntityState.Unchanged)
                {
                    throw new InvalidOperationException(
                        "Changed Staff assignments cannot be reloaded after the member operation lock.");
                }

                assignmentEntry.State = EntityState.Detached;
            }

            memberEntry.State = EntityState.Detached;
        }

        IQueryable<StaffMember> source = operational
            ? this.OperationalMembers()
            : dbContext.StaffMembers;
        return await source
            .Include(member => member.Assignments)
            .SingleOrDefaultAsync(
                member => member.Id == staffMemberId,
                cancellationToken).ConfigureAwait(false);
    }

    public Task<StaffMember?> GetForSafetyTransitionByAuthSubjectAsync(
        string authSubjectId,
        CancellationToken cancellationToken)
    {
        string normalized = authSubjectId.Trim();
        return dbContext.StaffMembers
            .Include(member => member.Assignments)
            .FirstOrDefaultAsync(
                member => member.AuthSubjectId == normalized,
                cancellationToken);
    }

    public Task<StaffMember?> GetByAuthSubjectAsync(string authSubjectId, CancellationToken cancellationToken)
    {
        string normalized = authSubjectId.Trim();
        return this.OperationalMembers()
            .Include(member => member.Assignments)
            .FirstOrDefaultAsync(member => member.AuthSubjectId == normalized, cancellationToken);
    }

    public Task<StaffDirectoryMemberDto?> GetDirectoryAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) => this.ProjectDirectory(
            this.OperationalMembers()
                .AsNoTracking()
                .Where(member => member.Id == staffMemberId),
            visiblePropertyId: null)
        .SingleOrDefaultAsync(cancellationToken);

    public Task<StaffDirectoryMemberDto?> GetDirectoryAtPropertyAsync(
        Guid propertyId,
        Guid staffMemberId,
        CancellationToken cancellationToken) => this.ProjectDirectory(
            this.OperationalMembers().AsNoTracking().Where(member =>
                member.Id == staffMemberId &&
                member.Assignments.Any(assignment =>
                    assignment.PropertyId == propertyId && assignment.IsCurrent) &&
                dbContext.PropertyProjections.Any(property =>
                    property.Id == propertyId &&
                    property.Status == BunkFy.Modules.Properties.Contracts.PropertyStatus.Active)),
            propertyId)
        .SingleOrDefaultAsync(cancellationToken);

    public Task<StaffDirectoryListResponse> ListDirectoryAsync(
        string? search,
        StaffStatus? status,
        PageRequest pageRequest,
        CancellationToken cancellationToken) => this.ListDirectoryAsync(
            ApplyDirectoryFilters(this.OperationalMembers().AsNoTracking(), search, status),
            pageRequest,
            cancellationToken);

    public Task<StaffPropertyDirectoryListResponse> ListDirectoryAtPropertyAsync(
        Guid propertyId,
        string? search,
        StaffStatus? status,
        PageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        IQueryable<StaffMember> query = ApplyDirectoryFilters(
            this.OperationalMembers().AsNoTracking()
            .Where(member => member.Assignments.Any(assignment => assignment.PropertyId == propertyId &&
                    assignment.IsCurrent) &&
                dbContext.PropertyProjections.Any(property => property.Id == propertyId &&
                    property.Status == BunkFy.Modules.Properties.Contracts.PropertyStatus.Active)),
            search,
            status);
        return ListDirectoryAtPropertyAsync(
            query,
            propertyId,
            pageRequest,
            cancellationToken);
    }

    public async Task<StaffAccountDirectoryResponse> ResolveAccountDirectoryAsync(
        IReadOnlyCollection<string> authSubjectIds,
        CancellationToken cancellationToken)
    {
        StaffAccountDirectoryEntryDto[] rows = await this.OperationalMembers()
            .AsNoTracking()
            .Where(member =>
                member.AuthSubjectId != null &&
                authSubjectIds.Contains(member.AuthSubjectId))
            .OrderBy(member => member.DisplayName)
            .ThenBy(member => member.Id)
            .Select(member => new StaffAccountDirectoryEntryDto(
                member.Id,
                member.AuthSubjectId!,
                member.DisplayName,
                member.JobTitle,
                (StaffStatus)member.Status,
                member.Version))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new(rows);
    }

    public Task<bool> EmployeeNumberExistsAsync(string employeeNumber, Guid? exceptStaffMemberId,
        CancellationToken cancellationToken)
    {
        string normalized = employeeNumber.Trim().ToUpperInvariant();
        return dbContext.StaffMembers.AsNoTracking().AnyAsync(member =>
            member.EmployeeNumberSearch == normalized &&
            (!exceptStaffMemberId.HasValue || member.Id != exceptStaffMemberId.Value), cancellationToken);
    }

    public Task<bool> AuthSubjectExistsAsync(string authSubjectId, Guid? exceptStaffMemberId,
        CancellationToken cancellationToken)
    {
        string normalized = authSubjectId.Trim();
        return dbContext.StaffMembers.AsNoTracking().AnyAsync(member =>
            member.AuthSubjectId == normalized &&
            (!exceptStaffMemberId.HasValue || member.Id != exceptStaffMemberId.Value), cancellationToken);
    }

    private IQueryable<StaffMember> OperationalMembers() =>
        dbContext.StaffMembers.Where(member =>
            member.Status != StaffMemberState.Anonymised &&
            dbContext.ProcessingRestrictionProjections.Any(projection =>
                projection.StaffMemberId == member.Id &&
                projection.ContractVersion ==
                    StaffProcessingRestrictionContract.CurrentVersion &&
                !projection.IsRestricted));

    private static IQueryable<StaffMember> ApplyDirectoryFilters(
        IQueryable<StaffMember> query,
        string? search,
        StaffStatus? status)
    {
        if (status.HasValue)
        {
            StaffMemberState state = status.Value switch
            {
                StaffStatus.Active => StaffMemberState.Active,
                StaffStatus.Suspended => StaffMemberState.Suspended,
                StaffStatus.Departed => StaffMemberState.Departed,
                _ => StaffMemberState.Unknown
            };
            query = query.Where(member => member.Status == state);
        }

        string? normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToUpperInvariant();
        if (normalizedSearch is not null)
        {
            query = query.Where(member => member.DisplayNameSearch.Contains(normalizedSearch));
        }

        return query;
    }

    private async Task<StaffDirectoryListResponse> ListDirectoryAsync(
        IQueryable<StaffMember> query,
        PageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        StaffDirectoryListItemDto[] rows = await query
            .OrderBy(member => member.DisplayName)
            .ThenBy(member => member.Id)
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize + 1)
            .Select(member => new StaffDirectoryListItemDto(
                member.Id,
                member.DisplayName,
                member.JobTitle,
                member.Department,
                (StaffStatus)member.Status,
                member.Version,
                member.Assignments.Count(assignment =>
                    assignment.IsCurrent &&
                    dbContext.PropertyProjections.Any(property =>
                        property.Id == assignment.PropertyId &&
                        property.Status ==
                            BunkFy.Modules.Properties.Contracts.PropertyStatus.Active))))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = rows.Length > pageRequest.PageSize;
        return new(rows.Take(pageRequest.PageSize).ToArray(), pageRequest.Page,
            pageRequest.PageSize, hasMore);
    }

    private static async Task<StaffPropertyDirectoryListResponse> ListDirectoryAtPropertyAsync(
        IQueryable<StaffMember> query,
        Guid propertyId,
        PageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        StaffPropertyDirectoryListItemDto[] rows = await query
            .OrderBy(member => member.DisplayName)
            .ThenBy(member => member.Id)
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize + 1)
            .Select(member => new StaffPropertyDirectoryListItemDto(
                member.Id,
                member.DisplayName,
                member.JobTitle,
                member.Department,
                (StaffStatus)member.Status,
                member.Version,
                member.Assignments
                    .Where(assignment => assignment.PropertyId == propertyId && assignment.IsCurrent)
                    .Select(assignment => new StaffDirectoryAssignmentDto(
                        assignment.Id,
                        assignment.PropertyId,
                        assignment.PropertyJobTitle,
                        assignment.IsPrimary,
                        assignment.EffectiveFrom))
                    .Single()))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = rows.Length > pageRequest.PageSize;
        return new(rows.Take(pageRequest.PageSize).ToArray(), pageRequest.Page,
            pageRequest.PageSize, hasMore);
    }

    private IQueryable<StaffDirectoryMemberDto> ProjectDirectory(
        IQueryable<StaffMember> query,
        Guid? visiblePropertyId) => query.Select(member => new StaffDirectoryMemberDto(
        member.Id,
        member.DisplayName,
        member.JobTitle,
        member.Department,
        (StaffStatus)member.Status,
        member.Version,
        member.Assignments
            .Where(assignment =>
                assignment.IsCurrent &&
                dbContext.PropertyProjections.Any(property =>
                    property.Id == assignment.PropertyId &&
                    property.Status ==
                        BunkFy.Modules.Properties.Contracts.PropertyStatus.Active) &&
                (!visiblePropertyId.HasValue ||
                    assignment.PropertyId == visiblePropertyId.Value))
            .OrderByDescending(assignment => assignment.IsPrimary)
            .ThenBy(assignment => assignment.PropertyId)
            .Select(assignment => new StaffDirectoryAssignmentDto(
                assignment.Id,
                assignment.PropertyId,
                assignment.PropertyJobTitle,
                assignment.IsPrimary,
                assignment.EffectiveFrom))
            .ToArray()));
}

namespace BunkFy.Modules.DataRights.Persistence.Repositories;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Pagination;
using Microsoft.EntityFrameworkCore;

internal sealed class DataRightsCaseRepository(DataRightsDbContext dbContext)
    : IDataRightsCaseRepository,
      IDataRightsCaseIdentityRepository,
      ITenantTerminationCaseRepository
{
    public Task AddAsync(DataRightsCase dataRightsCase, CancellationToken cancellationToken)
    {
        dbContext.Cases.Add(dataRightsCase);
        return Task.CompletedTask;
    }

    public async Task<DataRightsCase?> GetAsync(
        DataRightsCaseScope scope,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return await ApplyScope(dbContext.Cases, scope)
            .FirstOrDefaultAsync(
                dataRightsCase => dataRightsCase.Id == caseId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<DataRightsCase?> GetByIdAsync(
        Guid caseId,
        CancellationToken cancellationToken) =>
        dbContext.Cases.SingleOrDefaultAsync(
            dataRightsCase => dataRightsCase.Id == caseId,
            cancellationToken);

    public Task<DataRightsCase?> GetAsync(
        Guid caseId,
        CancellationToken cancellationToken) =>
        dbContext.Cases.SingleOrDefaultAsync(
            dataRightsCase =>
                dataRightsCase.Id == caseId &&
                dataRightsCase.Kind ==
                    DataRightsCaseKind.TenantTermination &&
                dataRightsCase.PropertyId == null,
            cancellationToken);

    public Task<DataRightsCase?> GetActiveAsync(
        CancellationToken cancellationToken) =>
        dbContext.Cases.SingleOrDefaultAsync(
            dataRightsCase =>
                dataRightsCase.Kind ==
                    DataRightsCaseKind.TenantTermination &&
                dataRightsCase.PropertyId == null &&
                dataRightsCase.Status != DataRightsCaseState.Denied &&
                dataRightsCase.Status != DataRightsCaseState.Completed &&
                dataRightsCase.Status != DataRightsCaseState.Canceled,
            cancellationToken);

    public async Task<DataRightsCaseListResponse> ListAsync(
        DataRightsCaseScope scope,
        DataRightsCaseStatus? status,
        PageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IQueryable<DataRightsCase> query = ApplyScope(
            dbContext.Cases.AsNoTracking(),
            scope);
        if (status.HasValue)
        {
            DataRightsCaseState state = (DataRightsCaseState)status.Value;
            query = query.Where(dataRightsCase => dataRightsCase.Status == state);
        }

        DataRightsCaseSummaryDto[] lookahead = await query
            .OrderByDescending(dataRightsCase => dataRightsCase.CreatedAtUtc)
            .ThenBy(dataRightsCase => dataRightsCase.Id)
            .Select(dataRightsCase => new DataRightsCaseSummaryDto(
                dataRightsCase.Id,
                dataRightsCase.PropertyId,
                (DataRightsCaseType)dataRightsCase.Kind,
                (DataRightsRequesterRelationship)dataRightsCase.RequesterRelationship,
                (DataRightsOperation)dataRightsCase.RequestedOperations,
                (DataRightsRestrictionDirective)dataRightsCase.RestrictionAction,
                (DataRightsCaseStatus)dataRightsCase.Status,
                dataRightsCase.SelectedSubjects.Count,
                dataRightsCase.DueAtUtc,
                dataRightsCase.Version,
                dataRightsCase.CreatedAtUtc,
                dataRightsCase.LastChangedAtUtc))
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = lookahead.Length > pageRequest.PageSize;
        return new(
            lookahead.Take(pageRequest.PageSize).ToArray(),
            pageRequest.Page,
            pageRequest.PageSize,
            hasMore);
    }

    private static IQueryable<DataRightsCase> ApplyScope(
        IQueryable<DataRightsCase> query,
        DataRightsCaseScope scope) =>
        scope.IsTenant
            ? query.Where(dataRightsCase =>
                dataRightsCase.Kind == (DataRightsCaseKind)scope.CaseType &&
                dataRightsCase.PropertyId == null)
            : query.Where(dataRightsCase =>
                dataRightsCase.Kind == (DataRightsCaseKind)scope.CaseType &&
                dataRightsCase.PropertyId == scope.PropertyId!.Value);
}

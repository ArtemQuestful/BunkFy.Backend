namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Models;
using Gma.Framework.Pagination;
using Microsoft.EntityFrameworkCore;

internal sealed class ReservationProcessingRestrictionRepository(
    ReservationsDbContext dbContext)
    : IReservationProcessingRestrictionRepository
{
    public Task<ReservationProcessingRestrictionReceipt?>
        FindReceiptByIdempotencyKeyAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
        dbContext.ProcessingRestrictionReceipts
            .AsNoTracking()
            .FirstOrDefaultAsync(
                receipt => receipt.IdempotencyKey == idempotencyKey,
                cancellationToken);

    public Task<ReservationProcessingRestriction?> FindByApplyApprovalAsync(
        Guid propertyId,
        Guid reservationId,
        Guid caseId,
        long approvalRevision,
        CancellationToken cancellationToken) =>
        dbContext.ProcessingRestrictions.AsNoTracking().FirstOrDefaultAsync(
            restriction =>
                restriction.PropertyId == propertyId &&
                restriction.ReservationId == reservationId &&
                restriction.ApplyCaseId == caseId &&
                restriction.ApplyApprovalRevision == approvalRevision,
            cancellationToken);

    public Task<ReservationProcessingRestriction?> FindByReleaseApprovalAsync(
        Guid propertyId,
        Guid reservationId,
        Guid caseId,
        long approvalRevision,
        CancellationToken cancellationToken) =>
        dbContext.ProcessingRestrictions.AsNoTracking().FirstOrDefaultAsync(
            restriction =>
                restriction.PropertyId == propertyId &&
                restriction.ReservationId == reservationId &&
                restriction.ReleaseCaseId == caseId &&
                restriction.ReleaseApprovalRevision == approvalRevision,
            cancellationToken);

    public Task<ReservationProcessingRestriction?> GetAsync(
        Guid propertyId,
        Guid restrictionId,
        CancellationToken cancellationToken) =>
        dbContext.ProcessingRestrictions.FirstOrDefaultAsync(
            restriction =>
                restriction.Id == restrictionId &&
                restriction.PropertyId == propertyId,
            cancellationToken);

    public async Task<IReadOnlyCollection<ReservationProcessingRestriction>>
        ListActiveAsync(
            Guid propertyId,
            Guid reservationId,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
        await dbContext.ProcessingRestrictions
            .AsNoTracking()
            .Where(restriction =>
                restriction.PropertyId == propertyId &&
                restriction.ReservationId == reservationId &&
                restriction.Status ==
                    ReservationProcessingRestrictionStatus.Active)
            .OrderBy(restriction => restriction.AppliedAtUtc)
            .ThenBy(restriction => restriction.Id)
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task AddAsync(
        ReservationProcessingRestriction restriction,
        CancellationToken cancellationToken)
    {
        dbContext.ProcessingRestrictions.Add(restriction);
        return Task.CompletedTask;
    }

    public Task AddReceiptAsync(
        ReservationProcessingRestrictionReceipt receipt,
        CancellationToken cancellationToken)
    {
        dbContext.ProcessingRestrictionReceipts.Add(receipt);
        return Task.CompletedTask;
    }
}

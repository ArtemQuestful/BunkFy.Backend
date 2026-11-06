namespace BunkFy.Modules.Retention.Persistence.Repositories;

using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

internal sealed class RetentionRunRetryRequestRepository(
    RetentionDbContext dbContext)
    : IRetentionRunRetryRequestRepository
{
    public Task<RetentionRunRetryRequest?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        dbContext.RunRetryRequests.SingleOrDefaultAsync(
            request => request.Id == requestId,
            cancellationToken);

    public Task<RetentionRunRetryRequest?> GetForEvidenceAsync(
        Guid runId,
        long evidenceVersion,
        CancellationToken cancellationToken) =>
        dbContext.RunRetryRequests.SingleOrDefaultAsync(
            request =>
                request.RunId == runId &&
                request.EvidenceVersion == evidenceVersion,
            cancellationToken);

    public Task AddAsync(
        RetentionRunRetryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        dbContext.RunRetryRequests.Add(request);
        return Task.CompletedTask;
    }
}

namespace BunkFy.Modules.Retention.Application.Ports;

using BunkFy.Modules.Retention.Domain.Aggregates;

internal interface IRetentionRunRetryRequestRepository
{
    Task<RetentionRunRetryRequest?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken);

    Task<RetentionRunRetryRequest?> GetForEvidenceAsync(
        Guid runId,
        long evidenceVersion,
        CancellationToken cancellationToken);

    Task AddAsync(
        RetentionRunRetryRequest request,
        CancellationToken cancellationToken);
}

namespace BunkFy.Modules.Ingestion.Persistence.Repositories;

using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using Microsoft.EntityFrameworkCore;

internal sealed class ChangeProposalRepository(IngestionDbContext dbContext) : IChangeProposalRepository
{
    public Task<ChangeProposal?> GetAsync(Guid proposalId, CancellationToken cancellationToken) =>
        dbContext.ChangeProposals.FirstOrDefaultAsync(proposal => proposal.Id == proposalId, cancellationToken);

    public Task<ChangeProposal?> FindByReceiptAsync(Guid receiptId, CancellationToken cancellationToken) =>
        dbContext.ChangeProposals.FirstOrDefaultAsync(proposal => proposal.ReceiptId == receiptId, cancellationToken);

    public async Task<IReadOnlyCollection<ChangeProposal>> ListPendingForSourceAsync(
        Guid connectionId,
        string externalId,
        Guid reservationId,
        Guid excludingReceiptId,
        CancellationToken cancellationToken) => await (
            from proposal in dbContext.ChangeProposals
            join receipt in dbContext.ObservationReceipts
                on proposal.ReceiptId equals receipt.Id
            where proposal.ConnectionId == connectionId &&
                  proposal.ReservationId == reservationId &&
                  proposal.ReceiptId != excludingReceiptId &&
                  proposal.State == ChangeProposalState.Pending &&
                  receipt.ConnectionId == connectionId &&
                  receipt.ExternalId == externalId
            select proposal)
        .OrderBy(proposal => proposal.CreatedAtUtc)
        .ThenBy(proposal => proposal.Id)
        .ToArrayAsync(cancellationToken)
        .ConfigureAwait(false);

    public Task AddAsync(ChangeProposal proposal, CancellationToken cancellationToken)
    {
        dbContext.ChangeProposals.Add(proposal);
        return Task.CompletedTask;
    }
}

namespace BunkFy.Modules.Ingestion.Application.Ports;

using BunkFy.Modules.Ingestion.Domain.Proposals;

public interface IChangeProposalRepository
{
    Task<ChangeProposal?> GetAsync(Guid proposalId, CancellationToken cancellationToken);
    Task<ChangeProposal?> FindByReceiptAsync(Guid receiptId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ChangeProposal>> ListPendingForSourceAsync(
        Guid connectionId,
        string externalId,
        Guid reservationId,
        Guid excludingReceiptId,
        CancellationToken cancellationToken);
    Task AddAsync(ChangeProposal proposal, CancellationToken cancellationToken);
}

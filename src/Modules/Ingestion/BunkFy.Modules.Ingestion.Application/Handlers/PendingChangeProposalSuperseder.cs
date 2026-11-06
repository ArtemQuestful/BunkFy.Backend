namespace BunkFy.Modules.Ingestion.Application.Handlers;

using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using Gma.Framework.Runtime.Time;

internal sealed class PendingChangeProposalSuperseder(
    IChangeProposalRepository proposals,
    IIngestionRetentionPolicy retentionPolicy,
    ISystemClock clock)
{
    internal const string SupersessionReason = "A newer source observation replaced this proposal.";

    public async Task SupersedeAsync(
        Guid connectionId,
        string externalId,
        Guid reservationId,
        Guid currentReceiptId,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<ChangeProposal> pending = await proposals
            .ListPendingForSourceAsync(
                connectionId,
                externalId,
                reservationId,
                currentReceiptId,
                cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        foreach (ChangeProposal proposal in pending)
        {
            Gma.Framework.Results.Result superseded = proposal.Supersede(
                SupersessionReason,
                proposal.Version,
                retentionPolicy.GetSensitiveHistoryRetainUntilUtc(
                    proposal.PropertyId,
                    proposal.ConnectionId,
                    nowUtc),
                nowUtc);
            if (superseded.IsFailure)
            {
                throw new InvalidOperationException(
                    $"The older reservation proposal could not be superseded: {superseded.Error.Code}");
            }
        }
    }
}

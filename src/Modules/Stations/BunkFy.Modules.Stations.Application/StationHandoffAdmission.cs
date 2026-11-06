namespace BunkFy.Modules.Stations.Application;

using BunkFy.Modules.Stations.Contracts;
using Gma.Modules.Auth.Contracts;
using Microsoft.Extensions.Options;

/// <summary>Device possession is not staff-mode readiness while the original manager session is active.</summary>
public sealed class StationHandoffAdmission(IStationPairingHandoffReader receipts,
    IAuthSessionAdmissionReader sessions, IOptions<StationOptions> options)
{
    // Null means this prerequisite is satisfied, not that the device or staff actor is authorized.
    public async Task<StationSessionState?> ObserveAsync(StationDeviceReference device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? authScope = options.Value.ManagementAuthScopeId;
        if (string.IsNullOrWhiteSpace(authScope))
        { return StationSessionState.Unavailable; }
        try
        {
            StationPairingHandoffFacts? receipt = await receipts.ReadHandoffAsync(device, cancellationToken).ConfigureAwait(false);
            if (receipt is null || receipt.Device != device || receipt.IssuerSessionId == Guid.Empty ||
                !Guid.TryParseExact(receipt.IssuerSubjectId, "D", out Guid issuer) || issuer == Guid.Empty ||
                receipt.IssuerSubjectId != issuer.ToString("D"))
            { return StationSessionState.StateChanged; }
            return await sessions.IsActiveAsync(authScope, issuer, receipt.IssuerSessionId, cancellationToken).ConfigureAwait(false)
                ? StationSessionState.HandoffPending : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return StationSessionState.Unavailable; }
    }
}

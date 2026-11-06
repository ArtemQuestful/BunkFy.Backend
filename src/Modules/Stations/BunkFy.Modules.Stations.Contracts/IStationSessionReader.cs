namespace BunkFy.Modules.Stations.Contracts;

/// <summary>Current minimized state only. Reading never renews activity or fabricates primary authentication.</summary>
public interface IStationSessionReader
{
    Task<StationRuntimeResponse> ReadAsync(string opaqueCredential, CancellationToken cancellationToken = default);
}

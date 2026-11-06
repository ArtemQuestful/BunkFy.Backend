namespace BunkFy.Modules.Stations.Tests;

using BunkFy.Modules.Stations.Persistence;
using Xunit;

public sealed class StationWorkspaceSettingsTests
{
    [Fact]
    public void Workspace_station_access_defaults_on_and_uses_optimistic_revision()
    {
        var state = new StationsTenantLifecycleState(StationDomainTests.Tenant);

        Assert.True(state.SharedStationPinEnabled);
        Assert.Equal(1, state.Revision);
        Assert.False(state.TrySetSharedStationPinEnabled(false, 0));
        Assert.True(state.TrySetSharedStationPinEnabled(false, 1));
        Assert.False(state.SharedStationPinEnabled);
        Assert.Equal(2, state.Revision);
        Assert.False(state.TrySetSharedStationPinEnabled(true, 1));
        Assert.True(state.TrySetSharedStationPinEnabled(true, 2));
        Assert.True(state.SharedStationPinEnabled);
        Assert.Equal(3, state.Revision);
    }
}

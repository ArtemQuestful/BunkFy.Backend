namespace Integration.Tests.Support;

using BunkFy.Modules.Stations.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

internal static class StationMigrationTestData
{
    private static readonly string[] ExpectedMigrationIds =
    [
        "20260920000000_InitialStations",
        "20260920010000_AddStationRuntimeBindingsAndActivity",
        "20260920020000_AddStationManagementPrerequisites",
        "20261008102749_AddNeutralStationAdmissionAndCheckOutGrant",
        "20261018012110_AddWorkspaceStationPinSetting"
    ];

    internal static async Task AssertCurrentAsync(StationsDbContext context)
    {
        Assert.Equal(ExpectedMigrationIds, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.False(context.Database.HasPendingModelChanges());
    }
}

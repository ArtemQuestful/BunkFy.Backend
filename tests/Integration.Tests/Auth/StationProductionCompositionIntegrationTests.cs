namespace Integration.Tests;

using BunkFy.Host.ServiceDefaults.Security.Stations;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Integration")]
public sealed class StationProductionCompositionIntegrationTests
{
    [Fact]
    public async Task Public_and_admin_hosts_start_with_station_off_and_zero_eligible_endpoints()
    {
        await using NotificationStreamingTestApplication publicApplication = new();
        StationEndpointInventory publicInventory = BuildInventory(publicApplication.Services, "public");
        await using AdminApiTestApplication adminApplication = new(
            "SqlServer",
            "Server=127.0.0.1,1433;Database=unused;User Id=sa;Password=Pass@word1;" +
            "TrustServerCertificate=True;Connect Timeout=1",
            "nats://127.0.0.1:4222",
            disableDatabaseBackgroundServices: true);
        StationEndpointInventory adminInventory = BuildInventory(adminApplication.Services, "admin");
        await WriteEvidenceAsync(publicInventory, adminInventory);
        await AssertProductionK0Async(
            publicApplication.Services,
            publicInventory,
            expectedEndpointCount: 325,
            expectedCanonicalSha256: "8ebfbd4559bca9d31d3eaa68e85865bb01b35652dc5001381aa2ce90b8e677e8");
        await AssertProductionK0Async(
            adminApplication.Services,
            adminInventory,
            expectedEndpointCount: 181,
            expectedCanonicalSha256: "8515ba6b3e7adce05b33805f40f0eac7e16637a89da4b274fdb546654c3ee54e");

        Assert.NotEqual(publicInventory.CanonicalSha256, adminInventory.CanonicalSha256);
    }

    private static StationEndpointInventory BuildInventory(IServiceProvider services, string hostSurface)
    {
        EndpointDataSource[] dataSources = services.GetServices<EndpointDataSource>().ToArray();
        Assert.NotEmpty(dataSources);
        return StationEndpointInventoryBuilder.Build(
            dataSources,
            hostSurface,
            services.GetRequiredService<IAuthorizationPolicyProvider>());
    }

    private static async Task AssertProductionK0Async(
        IServiceProvider services,
        StationEndpointInventory inventory,
        int expectedEndpointCount,
        string expectedCanonicalSha256)
    {
        StationFeatureSnapshot feature = services.GetRequiredService<IStationFeatureState>().Capture();
        IAuthorizationPolicyProvider policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        Assert.False(feature.Enabled);
        Assert.Equal(0, feature.Epoch);
        Assert.IsType<DisabledStationTokenValidator>(services.GetRequiredService<IStationTokenValidator>());
        Assert.Null(await policyProvider.GetPolicyAsync(StationAuthenticationDefaults.StationOnlyPolicy));
        Assert.Null(await policyProvider.GetPolicyAsync(StationAuthenticationDefaults.DualPolicy));
        Assert.Equal(expectedEndpointCount, inventory.Payload.Endpoints.Count);
        Assert.Equal(expectedCanonicalSha256, inventory.CanonicalSha256);
        Assert.Equal(0, inventory.EligibleCount);
        Assert.All(
            inventory.Payload.Endpoints,
            static endpoint =>
            {
                Assert.False(endpoint.StationAwarePolicy);
                Assert.Equal(nameof(StationOperationClass.Forbidden), endpoint.EffectiveClass);
            });
    }

    private static async Task WriteEvidenceAsync(
        StationEndpointInventory publicInventory,
        StationEndpointInventory adminInventory)
    {
        string? evidenceDirectory = Environment.GetEnvironmentVariable("BFX_STATION_K0_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            return;
        }

        Directory.CreateDirectory(evidenceDirectory);
        await WriteInventoryAsync(evidenceDirectory, "public", publicInventory);
        await WriteInventoryAsync(evidenceDirectory, "admin", adminInventory);
    }

    private static async Task WriteInventoryAsync(
        string evidenceDirectory,
        string hostSurface,
        StationEndpointInventory inventory)
    {
        string prefix = $"BFX-SS-K0-EPI-0001-{hostSurface}";
        string jsonPath = Path.Combine(evidenceDirectory, $"{prefix}.canonical.json");
        string csvPath = Path.Combine(evidenceDirectory, $"{prefix}.csv");
        await File.WriteAllTextAsync(jsonPath, inventory.CanonicalJson);
        await File.WriteAllTextAsync(csvPath, inventory.Csv);
    }
}

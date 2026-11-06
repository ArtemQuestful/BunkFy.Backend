namespace Integration.Tests;

using BunkFy.Host.Api;
using BunkFy.Host.Migrations;
using BunkFy.Modules.Stations.Application;
using Gma.Modules.Auth.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class StationHostConfigurationTests
{
    [Theory]
    [InlineData(false, 15)]
    [InlineData(true, 16)]
    public void Migration_catalog_includes_stations_only_on_explicit_enablement(bool enabled, int count)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:PostgreSql"] = "Host=127.0.0.1;Port=1;Database=synthetic-no-connection;Username=fixture;Password=unused",
            ["Stations:Http:Enabled"] = enabled.ToString()
        });
        builder.AddBunkFyMigrationPersistence(AuthProfile.DefaultGlobalScopeId);
        using IHost host = builder.Build();
        using IServiceScope scope = host.Services.CreateScope();
        var catalog = BunkFyMigrationCatalog.Resolve(scope.ServiceProvider);
        Assert.Equal(count, catalog.Count);
        Assert.Equal(enabled, catalog.Any(module => module.Name == "stations"));
        // No connection, Plan or Apply is performed by this composition-only test.
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("short")]
    public void Missing_or_invalid_pin_keys_fail_without_disclosing_configuration(string condition)
    {
        string value = condition switch
        {
            "malformed" => "synthetic-invalid-key",
            "short" => Convert.ToBase64String(new byte[4]),
            _ => ""
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Stations:PepperKeys:test-v1"] = value }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ConfiguredStationPepperProvider(configuration,
            Options.Create(new StationOptions { PepperVersion = "test-v1" })));
        Assert.Equal("Station PIN keys require valid deployment-owned configuration.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Supplied_versioned_pin_key_is_not_an_implicit_fallback_and_is_erased_on_dispose()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Stations:PepperKeys:test-v1"] = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()) }).Build();
        using var provider = new ConfiguredStationPepperProvider(configuration,
            Options.Create(new StationOptions { PepperVersion = "test-v1" }));
        Assert.False(provider.TryGet("unknown-version", out _));
        Assert.True(provider.TryGet("test-v1", out ReadOnlyMemory<byte> key));
        Assert.Equal(32, key.Length);
        provider.Dispose();
        Assert.All(key.ToArray(), value => Assert.Equal((byte)0, value));
        Assert.False(provider.TryGet("test-v1", out _));
    }

    [Fact]
    public void Station_http_cannot_start_without_named_bounded_host_limits()
    {
        Assert.Throws<InvalidOperationException>(() => StationHttpHostAdmission.Validate(new ConfigurationBuilder().Build()));
        var values = new Dictionary<string, string?>
        {
            ["Http:RateLimiting:Enabled"] = "true",
            ["Http:RateLimiting:WindowSeconds"] = "60"
        };
        (string Name, int Limit, string[] Methods)[] policies =
        [
            ("station-write", 30, ["POST", "PUT", "PATCH", "DELETE"]),
            ("station-read", 120, ["GET", "HEAD"])
        ];
        foreach ((string name, int limit, string[] methods) in policies)
        {
            int index = name == "station-write" ? 0 : 1;
            string prefix = $"Http:RateLimiting:Policies:{index}";
            values[$"{prefix}:Name"] = name;
            values[$"{prefix}:PermitLimit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values[$"{prefix}:PathPrefixes:0"] = "/api/station-management";
            values[$"{prefix}:PathPrefixes:1"] = "/api/station-setup";
            values[$"{prefix}:PathPrefixes:2"] = "/api/station-runtime";
            for (int i = 0; i < methods.Length; i++)
            { values[$"{prefix}:Methods:{i}"] = methods[i]; }
        }
        StationHttpHostAdmission.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        values["Http:RateLimiting:Enabled"] = "false";
        Assert.Throws<InvalidOperationException>(() => StationHttpHostAdmission.Validate(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }
}

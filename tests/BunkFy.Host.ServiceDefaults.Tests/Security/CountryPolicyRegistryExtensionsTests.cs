namespace BunkFy.Host.ServiceDefaults.Tests.Security;

using BunkFy.DataGovernance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

[Trait("Category", "Unit")]
public sealed class CountryPolicyRegistryExtensionsTests
{
    [Fact]
    public void Empty_configuration_registers_a_fail_closed_registry()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.AddBunkFyCountryPolicies();

        using ServiceProvider provider = builder.Services.BuildServiceProvider();
        CountryPolicyRegistry registry = provider.GetRequiredService<CountryPolicyRegistry>();
        Assert.Empty(registry.ListPolicies());
    }

    [Fact]
    public void Version_two_pack_uses_the_composed_embedded_tzdb_rules()
    {
        string root = CreateTemporaryDirectory();
        string packs = Directory.CreateDirectory(
            Path.Combine(root, "packs")).FullName;
        string source = Path.Combine(
            AppContext.BaseDirectory,
            "CountryPolicies",
            "example-hostel-policy.v2.json");
        string destination = Path.Combine(
            packs,
            "example-hostel-policy.v2.json");
        File.Copy(source, destination);

        try
        {
            CountryPolicyPackArtifact artifact = CountryPolicyPackJson.Parse(
                File.ReadAllBytes(destination));
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings
                {
                    ContentRootPath = root,
                    EnvironmentName = Environments.Development
                });
            string section =
                CountryPolicyRegistryExtensions.ConfigurationSection;
            builder.Configuration[$"{section}:PackDirectory"] = "packs";
            builder.Configuration[$"{section}:Allowlist:0:OperatingCountryCode"] =
                artifact.Document.OperatingCountryCode;
            builder.Configuration[$"{section}:Allowlist:0:PolicyId"] =
                artifact.Document.PolicyId;
            builder.Configuration[$"{section}:Allowlist:0:PolicyVersion"] =
                artifact.Document.PolicyVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            builder.Configuration[$"{section}:Allowlist:0:ContentSha256"] =
                artifact.ContentSha256;
            builder.Configuration[$"{section}:Allowlist:0:LaunchStatus"] =
                nameof(CountryLaunchStatus.Engineering);

            builder.AddBunkFyCountryPolicies();

            using ServiceProvider provider =
                builder.Services.BuildServiceProvider();
            CountryPolicyDescriptor descriptor = Assert.Single(provider
                .GetRequiredService<CountryPolicyRegistry>()
                .ListPolicies());
            Assert.Equal(2, descriptor.PolicyVersion);
            Assert.True(descriptor.SupportsRightsResponseDeadlines);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Configured_pack_directory_must_exist()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            HostApplicationBuilder builder = CreateBuilder(root);
            builder.Configuration[$"{CountryPolicyRegistryExtensions.ConfigurationSection}:PackDirectory"] =
                "missing";

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                builder.AddBunkFyCountryPolicies);

            Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Oversized_pack_is_rejected_before_its_contents_are_read()
    {
        string root = CreateTemporaryDirectory();
        string packs = Directory.CreateDirectory(Path.Combine(root, "packs")).FullName;
        File.WriteAllBytes(
            Path.Combine(packs, "oversized.json"),
            new byte[CountryPolicyPackJson.MaximumDocumentBytes + 1]);
        try
        {
            HostApplicationBuilder builder = CreateBuilder(root);
            builder.Configuration[$"{CountryPolicyRegistryExtensions.ConfigurationSection}:PackDirectory"] = "packs";

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                builder.AddBunkFyCountryPolicies);

            Assert.Contains("must contain between 1", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Pack_directory_has_a_bounded_file_count()
    {
        string root = CreateTemporaryDirectory();
        string packs = Directory.CreateDirectory(Path.Combine(root, "packs")).FullName;
        for (int index = 0; index <= CountryPolicyRegistryExtensions.MaximumPackFiles; index++)
        {
            File.WriteAllText(Path.Combine(packs, $"pack-{index:D3}.json"), "{}");
        }

        try
        {
            HostApplicationBuilder builder = CreateBuilder(root);
            builder.Configuration[$"{CountryPolicyRegistryExtensions.ConfigurationSection}:PackDirectory"] = "packs";

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                builder.AddBunkFyCountryPolicies);

            Assert.Contains("cannot contain more than", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HostApplicationBuilder CreateBuilder(string contentRootPath) =>
        Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = contentRootPath,
            EnvironmentName = Environments.Production
        });

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bunkfy-country-policies-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

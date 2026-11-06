namespace BunkFy.Host.ServiceDefaults.Tests.Security;

using BunkFy.DataGovernance;
using BunkFy.TimeZones;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PreviewCountryPolicyDefaultsTests
{
    [Theory]
    [InlineData("Production", true, false)]
    [InlineData("Production", false, true)]
    [InlineData("Development", true, false)]
    [InlineData("Development", false, true)]
    [InlineData("Staging", true, false)]
    [InlineData("Staging", false, true)]
    public void Test_defaults_cannot_be_enabled_outside_Preview(string environment, bool install, bool automatic)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment
        });
        builder.Configuration[PreviewCountryPolicyDefaults.EnabledConfigurationKey] = install.ToString();
        builder.Configuration[PreviewCountryPolicyDefaults.AutoActivateConfigurationKey] = automatic.ToString();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => builder.AddBunkFyCountryPolicies());

        Assert.Contains("only in the Preview environment", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_defaults_cover_every_canonical_time_zone_through_normal_policy_evaluation()
    {
        string root = CreateTemplateDirectory();
        try
        {
            HostApplicationBuilder builder = CreateBuilder(root, automatic: true);
            builder.AddBunkFyCountryPolicies();
            using ServiceProvider provider = builder.Services.BuildServiceProvider();
            CountryPolicyRegistry registry = provider.GetRequiredService<CountryPolicyRegistry>();
            IReadOnlyList<CountryPolicyDescriptor> policies = registry.ListPolicies();
            Assert.NotEmpty(policies);
            Assert.All(policies, policy =>
            {
                Assert.Equal(CountryLaunchStatus.Engineering, policy.LaunchStatus);
                Assert.Equal(CountryPolicyApprovalState.Example, policy.ApprovalState);
                Assert.True(policy.SupportsRightsResponseDeadlines);
            });

            DateTimeOffset observed = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
            foreach (string zone in TimeZoneCatalog.Default.CanonicalIds)
            {
                Assert.Contains(policies, policy =>
                {
                    CountryPolicyRetentionDescriptor retention = policy.RetentionPolicies.First();
                    CountryPolicyDecision activation = registry.EvaluateActivation(new(
                        policy.OperatingCountryCode, policy.PolicyId, policy.PolicyVersion,
                        policy.PermittedDataRegions.First(), policy.PermittedTransferProfiles.First(),
                        retention.RetentionPolicyId, retention.RetentionPolicyVersion,
                        policy.RequiredAcknowledgements, "hostel", "property-activation",
                        "authorized-workspace-operator", observed));
                    return activation.IsAllowed && activation.Evidence is not null &&
                        registry.EvaluateTimeZoneCompatibility(new(activation.Evidence.ToBinding(),
                            "hostel", zone, observed)).IsAllowed;
                });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Disabling_new_hostel_defaults_preserves_installed_policy_identities_and_digests()
    {
        string root = CreateTemplateDirectory();
        try
        {
            HostApplicationBuilder automatic = CreateBuilder(root, automatic: true);
            HostApplicationBuilder manual = CreateBuilder(root, automatic: false);
            automatic.AddBunkFyCountryPolicies();
            manual.AddBunkFyCountryPolicies();
            using ServiceProvider automaticServices = automatic.Services.BuildServiceProvider();
            using ServiceProvider manualServices = manual.Services.BuildServiceProvider();
            var before = automaticServices.GetRequiredService<CountryPolicyRegistry>().ListPolicies();
            var after = manualServices.GetRequiredService<CountryPolicyRegistry>().ListPolicies();

            Assert.Equal(before.Select(policy => (policy.PolicyId, policy.ContentSha256)),
                after.Select(policy => (policy.PolicyId, policy.ContentSha256)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Automatic_activation_without_installed_test_policies_is_rejected()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Preview"
        });
        builder.Configuration[PreviewCountryPolicyDefaults.AutoActivateConfigurationKey] = "true";

        Assert.Throws<InvalidOperationException>(() => builder.AddBunkFyCountryPolicies());
    }

    private static HostApplicationBuilder CreateBuilder(string root, bool automatic)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Preview",
            ContentRootPath = root
        });
        builder.Configuration[PreviewCountryPolicyDefaults.EnabledConfigurationKey] = "true";
        builder.Configuration[PreviewCountryPolicyDefaults.AutoActivateConfigurationKey] = automatic.ToString();
        builder.Configuration[CountryPolicyRegistryExtensions.ConfigurationSection + ":PackDirectory"] = "packs";
        return builder;
    }

    private static string CreateTemplateDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "bunkfy-preview-policies-" + Guid.NewGuid().ToString("N"));
        string packs = Directory.CreateDirectory(Path.Combine(root, "packs")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "CountryPolicies", "example-hostel-policy.v2.json"),
            Path.Combine(packs, "example-hostel-policy.v2.json"));
        return root;
    }
}

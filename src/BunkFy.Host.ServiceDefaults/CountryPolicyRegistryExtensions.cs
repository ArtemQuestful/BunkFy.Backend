namespace BunkFy.Host.ServiceDefaults;

using BunkFy.DataGovernance;
using BunkFy.TimeZones;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class CountryPolicyRegistryExtensions
{
    public const string ConfigurationSection = "BunkFy:CountryPolicies";
    public const int MaximumPackFiles = CountryPolicyRegistry.MaximumPolicyArtifacts;

    public static IHostApplicationBuilder AddBunkFyCountryPolicies(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        bool previewDefaults = builder.Configuration.GetValue<bool>(PreviewCountryPolicyDefaults.EnabledConfigurationKey);
        bool autoActivate = builder.Configuration.GetValue<bool>(PreviewCountryPolicyDefaults.AutoActivateConfigurationKey);
        if ((previewDefaults || autoActivate) && !builder.Environment.IsEnvironment("Preview"))
        {
            throw new InvalidOperationException("Automatic test country policies are available only in the Preview environment.");
        }

        if (autoActivate && !previewDefaults)
        {
            throw new InvalidOperationException("Automatic property activation requires Preview test country policies.");
        }

        CountryPolicyHostingOptions options =
            builder.Configuration.GetSection(ConfigurationSection).Get<CountryPolicyHostingOptions>() ?? new();
        CountryPolicyPackArtifact[] artifacts = LoadArtifacts(builder.Environment.ContentRootPath, options.PackDirectory);
        CountryPolicyAllowlistEntry[] allowlist = options.Allowlist?
            .Select(entry => entry is null
                ? null!
                : new CountryPolicyAllowlistEntry(
                    entry.OperatingCountryCode,
                    entry.PolicyId,
                    entry.PolicyVersion,
                    entry.ContentSha256,
                    entry.LaunchStatus))
            .ToArray() ?? [];

        if (previewDefaults)
        {
            CountryPolicyPackArtifact template = artifacts.SingleOrDefault(artifact =>
                artifact.Document.PolicyId == "development-hostel-example" &&
                artifact.Document.PolicyVersion == 2) ??
                throw new InvalidOperationException("Preview defaults require the mounted version-two example policy.");
            CountryPolicyPackArtifact[] defaults = [.. PreviewCountryPolicyDefaults.Create(
                template, TimeZoneCatalog.Default.CanonicalIds)];
            artifacts = [.. artifacts, .. defaults];
            allowlist = [.. allowlist, .. defaults.Select(artifact => new CountryPolicyAllowlistEntry(
                artifact.Document.OperatingCountryCode,
                artifact.Document.PolicyId,
                artifact.Document.PolicyVersion,
                artifact.ContentSha256,
                CountryLaunchStatus.Engineering))];
        }

        CountryPolicyRegistry registry = CountryPolicyRegistry.Create(
            artifacts,
            allowlist,
            builder.Environment.IsProduction()
                ? CountryPolicyRuntimeMode.Production
                : CountryPolicyRuntimeMode.Engineering,
            EmbeddedTzdbCountryPolicyTimeZoneRules.Instance);
        builder.Services.AddSingleton(registry);
        return builder;
    }

    private static CountryPolicyPackArtifact[] LoadArtifacts(string contentRootPath, string? configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return [];
        }

        string root = Path.GetFullPath(contentRootPath);
        string directory = Path.GetFullPath(
            Path.IsPathRooted(configuredDirectory)
                ? configuredDirectory
                : Path.Combine(root, configuredDirectory));
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"Country-policy pack directory '{directory}' does not exist.");
        }

        string[] files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (files.Length > MaximumPackFiles)
        {
            throw new InvalidOperationException(
                $"Country-policy pack directory cannot contain more than {MaximumPackFiles} JSON files.");
        }

        return files.Select(path =>
        {
            FileInfo file = new(path);
            if (file.Length is <= 0 or > CountryPolicyPackJson.MaximumDocumentBytes)
            {
                throw new InvalidOperationException(
                    $"Country-policy pack '{file.Name}' must contain between 1 and " +
                    $"{CountryPolicyPackJson.MaximumDocumentBytes} bytes.");
            }

            return CountryPolicyPackJson.Parse(File.ReadAllBytes(path));
        }).ToArray();
    }
}

public sealed class CountryPolicyHostingOptions
{
    public string? PackDirectory { get; set; }
    public CountryPolicyAllowlistOptions?[] Allowlist { get; set; } = [];
}

public sealed class CountryPolicyAllowlistOptions
{
    public string OperatingCountryCode { get; set; } = string.Empty;
    public string PolicyId { get; set; } = string.Empty;
    public int PolicyVersion { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public CountryLaunchStatus LaunchStatus { get; set; }
}

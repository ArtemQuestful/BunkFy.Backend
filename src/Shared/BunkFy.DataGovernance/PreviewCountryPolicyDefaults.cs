namespace BunkFy.DataGovernance;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class PreviewCountryPolicyDefaults
{
    public const string EnabledConfigurationKey = "BunkFy:CountryPolicies:PreviewDefaults:Enabled";
    public const string AutoActivateConfigurationKey = "BunkFy:CountryPolicies:PreviewDefaults:AutoActivateNewProperties";
    public const string PolicyIdPrefix = "preview-ux-hostel-";

    public static IReadOnlyList<CountryPolicyPackArtifact> Create(
        CountryPolicyPackArtifact template,
        IEnumerable<string> canonicalTimeZoneIds)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(canonicalTimeZoneIds);
        if (template.Document.SchemaVersion != 2 ||
            template.Document.ApprovalState != CountryPolicyApprovalState.Example ||
            template.Document.RightsRule.ResponseRules is not { Length: > 0 })
        {
            throw new InvalidOperationException("Preview defaults require a version-two example policy template.");
        }

        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        List<CountryPolicyPackArtifact> artifacts = [];
        // Keep each artifact within the existing 64-zone validation bound.
        foreach (string[] zones in canonicalTimeZoneIds.Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal).Chunk(64))
        {
            string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                template.ContentSha256 + "\n" + string.Join("\n", zones))))[..16];
            CountryPolicyPackDocument document = template.Document with
            {
                PolicyId = PolicyIdPrefix + identity,
                PolicyVersion = 1,
                RequiredAcknowledgements = [],
                RightsRule = template.Document.RightsRule with
                {
                    ResponseRules = template.Document.RightsRule.ResponseRules
                        .Select(rule => rule with { AllowedTimeZoneIds = zones }).ToArray()
                }
            };
            artifacts.Add(CountryPolicyPackJson.Parse(JsonSerializer.SerializeToUtf8Bytes(document, json)));
        }

        return artifacts;
    }
}

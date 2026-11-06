namespace BunkFy.Modules.Properties.Api;

using Gma.Framework.Security;

public sealed class PropertiesApiSecurityOptions
{
    public AuthenticationAssuranceRequirement? TimeZoneManagementAssurance { get; set; }

    public AuthenticationAssuranceRequirement? ProcessingActivationAssurance { get; set; }

    public AuthenticationAssuranceRequirement? PropertyRetirementAssurance { get; set; }
}

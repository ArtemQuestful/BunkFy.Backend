namespace BunkFy.Modules.Retention.Api;

using Gma.Framework.Security;

public sealed class RetentionApiSecurityOptions
{
    public AuthenticationAssuranceRequirement? RetryAssurance { get; set; }
}

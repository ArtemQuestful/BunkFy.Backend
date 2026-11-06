namespace BunkFy.Host.Api;

using BunkFy.Adapters.SmtpEmail;
using BunkFy.Modules.Workspaces.Api;
using BunkFy.Modules.Stations.Api;
using Gma.Modules.Auth.Application;
using Gma.Modules.Notifications.Adapters.Email;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

public static class ProductCapabilitiesEndpoints
{
    public static IEndpointRouteBuilder MapBunkFyProductCapabilities(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
                "/api/product-capabilities",
                GetProductCapabilities)
            .AllowAnonymous()
            .WithName("GetBunkFyProductCapabilities")
            .WithTags("Product")
            .Produces<BunkFyProductCapabilitiesResponse>();

        return endpoints;
    }

    private static Ok<BunkFyProductCapabilitiesResponse> GetProductCapabilities(
        IOptions<SmtpEmailOptions> smtp,
        IOptions<NotificationEmailAdapterOptions> notificationEmail,
        IOptions<WorkspacesApiSecurityOptions> workspaces,
        IOptions<StationApiOptions> stations,
        IOptions<RememberedBrowserOptions> remembered) =>
        TypedResults.Ok(new BunkFyProductCapabilitiesResponse(
            smtp.Value.Enabled && notificationEmail.Value.Enabled,
            workspaces.Value.StaffOnboardingRecoveryPostEnabled,
            stations.Value.Enabled,
            remembered.Value.Enabled));
}

public sealed record BunkFyProductCapabilitiesResponse(
    bool EmailVerificationEnabled,
    bool WorkspaceStaffOnboardingRecoveryEnabled,
    bool StaffPinEnabled = false,
    bool SharedAccountSwitchEnabled = false);

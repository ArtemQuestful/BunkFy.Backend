namespace BunkFy.Host.Api;

using System.Security.Cryptography;
using System.Text.Json;
using BunkFy.Modules.Workspaces.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Primitives;

// Transport custody only. Every preview, submission and join still goes through
// the owning Organizations/Workspaces operation with its original token and the
// authenticated subject. A context ID is public metadata, never join authority.
internal sealed class WorkspaceJoinBrowserContinuation(
    IDataProtectionProvider protection,
    IHostEnvironment environment)
{
    internal const string HeaderName = "X-BunkFy-Join-Context";
    internal const string CookieName = "bunkfy.workspace-join";
    internal const string RoutePrefix = "/api/workspace-join";
    private const int MaximumTokenLength = 256;
    private const int MaximumCookieLength = 2048;

    internal static bool IsAdmitted(HttpContext context, Guid contextId)
    {
        StringValues header = context.Request.Headers[HeaderName];
        if (contextId == Guid.Empty || header.Count != 1 ||
            !Guid.TryParseExact(header[0], "D", out Guid supplied) || supplied != contextId)
        {
            return false;
        }

        // The custom header cannot be sent by a form/navigation. A cross-origin
        // browser fetch must also pass the host's existing explicit CORS policy.
        StringValues mode = context.Request.Headers["Sec-Fetch-Mode"];
        StringValues site = context.Request.Headers["Sec-Fetch-Site"];
        return (mode.Count == 0 || (mode.Count == 1 && mode[0] is "cors" or "same-origin")) &&
            (site.Count == 0 || (site.Count == 1 && site[0] is "same-origin" or "same-site" or "none"));
    }

    internal bool TryStore(
        HttpContext context,
        Guid contextId,
        WorkspaceStaffOnboardingSourceKind kind,
        string token)
    {
        if (!IsAdmitted(context, contextId) || !this.TransportAllowed(context) || !Valid(kind, token))
        {
            return false;
        }

        string encoded = this.Protector(contextId).Protect(JsonSerializer.Serialize(new Binding(kind, token)));
        context.Response.Cookies.Append(CookieName, encoded, this.CookieOptions(context, contextId));
        return true;
    }

    internal bool TryRead(
        HttpContext context,
        Guid contextId,
        out WorkspaceStaffOnboardingSourceKind kind,
        out string token)
    {
        kind = default;
        token = string.Empty;
        if (!IsAdmitted(context, contextId) || !this.TransportAllowed(context) ||
            !TryGetSingleCookie(context.Request, out string? encoded))
        {
            return false;
        }

        try
        {
            Binding? binding = JsonSerializer.Deserialize<Binding>(this.Protector(contextId).Unprotect(encoded!));
            if (binding is null || !Valid(binding.Kind, binding.Token))
            {
                return false;
            }

            kind = binding.Kind;
            token = binding.Token;
            return true;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        {
            // Never log the credential, protected value, or exception payload.
            return false;
        }
    }

    internal bool TryClear(HttpContext context, Guid contextId)
    {
        if (!IsAdmitted(context, contextId) || !this.TransportAllowed(context))
        {
            return false;
        }

        context.Response.Cookies.Delete(CookieName, this.CookieOptions(context, contextId));
        return true;
    }

    private static bool Valid(WorkspaceStaffOnboardingSourceKind kind, string? token) =>
        kind is WorkspaceStaffOnboardingSourceKind.Invitation or WorkspaceStaffOnboardingSourceKind.EnrollmentLink &&
        token is { Length: > 0 and <= MaximumTokenLength } && !string.IsNullOrWhiteSpace(token);

    private static bool TryGetSingleCookie(HttpRequest request, out string? encoded)
    {
        encoded = null;
        int count = 0;
        foreach (string? line in request.Headers.Cookie)
        {
            if (line is null)
            {
                continue;
            }
            foreach (string part in line.Split(';'))
            {
                string segment = part.Trim();
                int separator = segment.IndexOf('=', StringComparison.Ordinal);
                string name = separator < 0 ? segment : segment[..separator].Trim();
                if (!string.Equals(name, CookieName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (++count != 1 || separator < 0)
                {
                    return false;
                }
                encoded = segment[(separator + 1)..];
            }
        }

        return count == 1 && encoded is { Length: > 0 and <= MaximumCookieLength };
    }

    private IDataProtector Protector(Guid contextId) => protection.CreateProtector(
        "BunkFy.WorkspaceJoin.BrowserContinuation.v1", contextId.ToString("D"));

    private bool AllowsLocalHttp => environment.IsDevelopment() ||
        environment.IsEnvironment("Preview") || environment.IsEnvironment("Integration");

    private bool TransportAllowed(HttpContext context) => context.Request.IsHttps || this.AllowsLocalHttp;

    private CookieOptions CookieOptions(HttpContext context, Guid contextId) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps || !this.AllowsLocalHttp,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = RoutePrefix + "/" + contextId.ToString("D")
        // Host-only, browser-session cookie: no Domain, Expires or MaxAge.
        // Source expiry/revocation/capacity and claim recovery stay with GMA.
        // In particular, an expired link can still preview a pending application.
    };

    private sealed record Binding(WorkspaceStaffOnboardingSourceKind Kind, string Token);
}

namespace Integration.Tests;

using BunkFy.Host.Api;
using BunkFy.Modules.Workspaces.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Xunit;

public sealed class WorkspaceJoinBrowserContinuationTests
{
    private static readonly Guid ContextId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid OtherContextId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string Token = "synthetic-invitation-or-enrollment-secret";

    [Theory]
    [InlineData(WorkspaceStaffOnboardingSourceKind.Invitation)]
    [InlineData(WorkspaceStaffOnboardingSourceKind.EnrollmentLink)]
    public void Cookie_retains_exact_secret_with_http_only_context_scope(
        WorkspaceStaffOnboardingSourceKind kind)
    {
        var owner = Owner();
        DefaultHttpContext issuing = Request(ContextId);
        Assert.True(owner.TryStore(issuing, ContextId, kind, Token));
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(issuing.Response.Headers.SetCookie)!);
        Assert.Equal(WorkspaceJoinBrowserContinuation.CookieName, cookie.Name.ToString());
        Assert.Equal("/api/workspace-join/" + ContextId, cookie.Path.ToString());
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.False(cookie.Domain.HasValue);
        Assert.Null(cookie.Expires);
        Assert.Null(cookie.MaxAge);
        Assert.DoesNotContain(Token, cookie.Value.ToString(), StringComparison.Ordinal);

        DefaultHttpContext restored = Request(ContextId, cookie.Name + "=" + cookie.Value);
        Assert.True(owner.TryRead(restored, ContextId, out var actualKind, out string actualToken));
        Assert.Equal(kind, actualKind);
        Assert.Equal(Token, actualToken);
    }

    [Fact]
    public void Public_context_id_and_headers_alone_do_not_recover_a_credential()
    {
        Assert.False(Owner().TryRead(Request(ContextId), ContextId, out _, out string value));
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void A_protected_cookie_cannot_move_to_another_context_or_key_ring()
    {
        var owner = Owner();
        string cookie = Issue(owner);
        Assert.False(owner.TryRead(Request(OtherContextId, cookie), OtherContextId, out _, out _));
        Assert.False(Owner().TryRead(Request(ContextId, cookie), ContextId, out _, out _));
    }

    [Fact]
    public void Separate_contexts_retain_their_own_kind_and_secret()
    {
        var owner = Owner();
        string first = Issue(owner);
        DefaultHttpContext issuing = Request(OtherContextId);
        Assert.True(owner.TryStore(issuing, OtherContextId, WorkspaceStaffOnboardingSourceKind.EnrollmentLink, "other-synthetic-secret"));
        string second = Cookie(issuing);
        Assert.True(owner.TryRead(Request(ContextId, first), ContextId, out var kind, out string value));
        Assert.Equal(WorkspaceStaffOnboardingSourceKind.Invitation, kind);
        Assert.Equal(Token, value);
        Assert.True(owner.TryRead(Request(OtherContextId, second), OtherContextId, out kind, out value));
        Assert.Equal(WorkspaceStaffOnboardingSourceKind.EnrollmentLink, kind);
        Assert.Equal("other-synthetic-secret", value);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("duplicate")]
    [InlineData("comma")]
    [InlineData("navigate")]
    [InlineData("cross-site")]
    [InlineData("duplicate-mode")]
    public void Cookie_custody_requires_the_exact_request_context(string condition)
    {
        var owner = Owner();
        DefaultHttpContext request = Request(ContextId, Issue(owner));
        switch (condition)
        {
            case "missing":
                request.Request.Headers.Remove(WorkspaceJoinBrowserContinuation.HeaderName);
                break;
            case "wrong":
                request.Request.Headers[WorkspaceJoinBrowserContinuation.HeaderName] = OtherContextId.ToString();
                break;
            case "duplicate":
                request.Request.Headers.Append(WorkspaceJoinBrowserContinuation.HeaderName, ContextId.ToString());
                break;
            case "comma":
                request.Request.Headers[WorkspaceJoinBrowserContinuation.HeaderName] = ContextId + "," + ContextId;
                break;
            case "navigate":
                request.Request.Headers["Sec-Fetch-Mode"] = "navigate";
                break;
            case "cross-site":
                request.Request.Headers["Sec-Fetch-Site"] = "cross-site";
                break;
            case "duplicate-mode":
                request.Request.Headers.Append("Sec-Fetch-Mode", "cors");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition));
        }

        Assert.False(owner.TryRead(request, ContextId, out _, out _));
        Assert.False(owner.TryStore(request, ContextId, WorkspaceStaffOnboardingSourceKind.Invitation, Token));
        Assert.False(owner.TryClear(request, ContextId));
        Assert.Equal(0, request.Response.Headers.SetCookie.Count);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("modified")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    [InlineData("bare-duplicate")]
    public void Malformed_modified_or_ambiguous_cookies_fail_closed(string condition)
    {
        var owner = Owner();
        string cookie = Issue(owner);
        string name = WorkspaceJoinBrowserContinuation.CookieName;
        cookie = condition switch
        {
            "malformed" => name + "=invalid%not-base64",
            "modified" => cookie[..(cookie.Length / 2)] + "!" + cookie[((cookie.Length / 2) + 1)..],
            "oversized" => name + "=" + new string('x', 2049),
            "duplicate" => cookie + "; " + cookie,
            _ => name + "; " + cookie,
        };
        Assert.False(owner.TryRead(Request(ContextId, cookie), ContextId, out _, out string value));
        Assert.Equal(string.Empty, value);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Preview", true)]
    [InlineData("Integration", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("custom-deployment", false)]
    public void Only_existing_local_profiles_can_use_http(string environment, bool allowed)
    {
        var owner = Owner(environment);
        DefaultHttpContext request = Request(ContextId);
        request.Request.Scheme = "http";
        Assert.Equal(allowed, owner.TryStore(request, ContextId, WorkspaceStaffOnboardingSourceKind.Invitation, Token));
        if (allowed)
        {
            Assert.False(SetCookieHeaderValue.Parse(Assert.Single(request.Response.Headers.SetCookie)!).Secure);
            Assert.True(owner.TryRead(Request(ContextId, Cookie(request), "http"), ContextId, out _, out _));
        }
        else
        {
            Assert.Equal(0, request.Response.Headers.SetCookie.Count);
            Assert.False(owner.TryRead(request, ContextId, out _, out _));
        }
    }

    [Theory]
    [InlineData(WorkspaceStaffOnboardingSourceKind.Unknown, "synthetic-secret")]
    [InlineData((WorkspaceStaffOnboardingSourceKind)3, "synthetic-secret")]
    [InlineData(WorkspaceStaffOnboardingSourceKind.Invitation, "")]
    [InlineData(WorkspaceStaffOnboardingSourceKind.EnrollmentLink, " ")]
    public void Unsupported_or_empty_payload_is_never_stored(WorkspaceStaffOnboardingSourceKind kind, string token)
    {
        DefaultHttpContext request = Request(ContextId);
        Assert.False(Owner().TryStore(request, ContextId, kind, token));
        Assert.Equal(0, request.Response.Headers.SetCookie.Count);
    }

    [Fact]
    public void Empty_context_or_oversized_payload_is_never_stored()
    {
        var owner = Owner();
        Assert.False(owner.TryStore(Request(Guid.Empty), Guid.Empty, WorkspaceStaffOnboardingSourceKind.Invitation, Token));
        Assert.False(owner.TryStore(Request(ContextId), ContextId, WorkspaceStaffOnboardingSourceKind.Invitation, new string('x', 257)));
    }

    [Fact]
    public void Cleanup_retains_the_original_cookie_path_and_security_flags()
    {
        var owner = Owner();
        DefaultHttpContext request = Request(ContextId, Issue(owner));
        Assert.True(owner.TryClear(request, ContextId));
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(request.Response.Headers.SetCookie)!);
        Assert.Equal("/api/workspace-join/" + ContextId, cookie.Path.ToString());
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.True(cookie.Expires < DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Existing_persistent_key_ring_supports_new_host_instances_and_separates_applications()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("bunkfy-synthetic-join-dp-");
        try
        {
            var first = new WorkspaceJoinBrowserContinuation(
                DataProtectionProvider.Create(directory, options => options.SetApplicationName("BunkFy-synthetic")), new Environment());
            string cookie = Issue(first);
            var restarted = new WorkspaceJoinBrowserContinuation(
                DataProtectionProvider.Create(directory, options => options.SetApplicationName("BunkFy-synthetic")), new Environment());
            Assert.True(restarted.TryRead(Request(ContextId, cookie), ContextId, out _, out string value));
            Assert.Equal(Token, value);
            var other = new WorkspaceJoinBrowserContinuation(
                DataProtectionProvider.Create(directory, options => options.SetApplicationName("Other-synthetic")), new Environment());
            Assert.False(other.TryRead(Request(ContextId, cookie), ContextId, out _, out _));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static WorkspaceJoinBrowserContinuation Owner(string environment = "Production") =>
        new(new EphemeralDataProtectionProvider(), new Environment { EnvironmentName = environment });

    private static DefaultHttpContext Request(Guid contextId, string? cookie = null, string scheme = "https")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString("localhost");
        context.Request.Headers[WorkspaceJoinBrowserContinuation.HeaderName] = contextId.ToString("D");
        context.Request.Headers["Sec-Fetch-Mode"] = "cors";
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = cookie;
        }
        return context;
    }

    private static string Issue(WorkspaceJoinBrowserContinuation owner)
    {
        DefaultHttpContext issuing = Request(ContextId);
        Assert.True(owner.TryStore(issuing, ContextId, WorkspaceStaffOnboardingSourceKind.Invitation, Token));
        return Cookie(issuing);
    }

    private static string Cookie(DefaultHttpContext context)
    {
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(context.Response.Headers.SetCookie));
        return cookie.Name + "=" + cookie.Value;
    }

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "BunkFy-synthetic";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

namespace Integration.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BunkFy.Host.Api;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Modules.Organizations.Application;
using Gma.Modules.Organizations.Application.Commands;
using Gma.Modules.Organizations.Application.Queries;
using Gma.Modules.Organizations.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Xunit;

// Actual product endpoints, routing, authorization metadata and protected
// cookies. Business owners and the authenticated principal are controlled;
// this is transport qualification, not native Auth/Organizations acceptance.
public sealed class WorkspaceJoinBrowserHttpTests
{
    private const string Credential = "synthetic-workspace-join-credential";
    private const string Member = "33333333-3333-4333-8333-333333333333";
    private static readonly Guid ContextId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid OtherContextId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Theory]
    [InlineData(WorkspaceStaffOnboardingSourceKind.Invitation)]
    [InlineData(WorkspaceStaffOnboardingSourceKind.EnrollmentLink)]
    public async Task Prepare_and_preview_keep_the_token_out_of_response_and_use_the_original_query(
        WorkspaceStaffOnboardingSourceKind kind)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string cookie = await fixture.PrepareAsync(kind);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/preview", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(Credential, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(2, fixture.Owner.Queries.Count);
        Assert.All(fixture.Owner.Queries, query => Assert.Equal(Credential, QueryToken(query)));
        Assert.Empty(fixture.Owner.Commands);
        Assert.Empty(fixture.Owner.Submissions);
    }

    [Fact]
    public async Task Missing_custom_header_cannot_prepare_or_set_a_cookie()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "",
            new { sourceKind = 1, token = Credential }, includeContextHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        Assert.Empty(fixture.Owner.Queries);
    }

    [Theory]
    [InlineData("/applications")]
    [InlineData("/join")]
    public async Task Protected_cookie_does_not_replace_authentication(string operation)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string cookie = await fixture.PrepareAsync(WorkspaceStaffOnboardingSourceKind.Invitation);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, operation,
            Profile(), cookie, authenticated: false);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(fixture.Owner.Commands);
        Assert.Empty(fixture.Owner.Submissions);
    }

    [Theory]
    [InlineData("/preview")]
    [InlineData("/applications")]
    [InlineData("/join")]
    public async Task Cookie_for_another_context_never_reaches_a_business_owner(string operation)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string cookie = await fixture.PrepareAsync(WorkspaceStaffOnboardingSourceKind.Invitation);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, operation,
            Profile(), cookie, contextId: OtherContextId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(fixture.Owner.Queries);
        Assert.Empty(fixture.Owner.Commands);
        Assert.Empty(fixture.Owner.Submissions);
    }

    [Fact]
    public async Task Staff_submission_uses_the_cookie_source_and_authenticated_subject_and_preserves_owner_errors()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string cookie = await fixture.PrepareAsync(WorkspaceStaffOnboardingSourceKind.Invitation);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/applications", Profile(), cookie);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Workspaces.VerifiedIdentityRequired", body.RootElement.GetProperty("title").GetString());
        SubmitWorkspaceStaffOnboardingCommand submission = Assert.Single(fixture.Owner.Submissions);
        Assert.Equal(Credential, submission.Token);
        Assert.Equal(Member, submission.SubjectId);
        Assert.Equal(WorkspaceStaffOnboardingSourceKind.Invitation, submission.SourceKind);
        Assert.Equal("Proposed Name", submission.DisplayName);
        Assert.Equal("Proposed Legal Name", submission.LegalName);
        Assert.Equal("proposed@example.test", submission.WorkEmail);
        Assert.Equal("synthetic-phone", submission.WorkPhone);
        Assert.Equal("synthetic-number", submission.EmployeeNumber);
        Assert.Equal("Proposed title", submission.JobTitle);
        Assert.Equal("Proposed department", submission.Department);
        Assert.Empty(fixture.Owner.Commands);
    }

    [Theory]
    [InlineData(WorkspaceStaffOnboardingSourceKind.Invitation)]
    [InlineData(WorkspaceStaffOnboardingSourceKind.EnrollmentLink)]
    public async Task Cookie_submission_preserves_authority_moved_conflict_without_profile_payload(
        WorkspaceStaffOnboardingSourceKind kind)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string cookie = await fixture.PrepareAsync(kind);
        fixture.Owner.StaffOwnsIdentity = true;
        using HttpResponseMessage response = await fixture.SendAsync(
            HttpMethod.Post, "/applications", Profile(), cookie);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument body = JsonDocument.Parse(json);
        Assert.Equal(WorkspaceStaffOnboardingApplicationErrors.ProfileMutationAuthorityUnavailable.Code,
            body.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain(Credential, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Proposed Name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("subjectId", json, StringComparison.Ordinal);
        Assert.Single(fixture.Owner.Submissions);
        Assert.Empty(fixture.Owner.Commands);
    }

    [Theory]
    [InlineData(WorkspaceStaffOnboardingSourceKind.Invitation, HttpStatusCode.Forbidden)]
    [InlineData(WorkspaceStaffOnboardingSourceKind.EnrollmentLink, HttpStatusCode.Gone)]
    public async Task Join_forwards_the_exact_original_command_and_owner_http_contract(
        WorkspaceStaffOnboardingSourceKind kind, HttpStatusCode expectedStatus)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string cookie = await fixture.PrepareAsync(kind);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/join",
            new { token = "forged-token", subjectId = "forged-subject", sourceKind = 2 }, cookie);
        Assert.Equal(expectedStatus, response.StatusCode);
        object command = Assert.Single(fixture.Owner.Commands);
        if (kind == WorkspaceStaffOnboardingSourceKind.Invitation)
        {
            var accepted = Assert.IsType<AcceptOrganizationInvitationCommand>(command);
            Assert.Equal(Credential, accepted.Token);
            Assert.Equal(Member, accepted.SubjectId);
            Assert.Equal("user:" + Member, accepted.ActorId);
        }
        else
        {
            var claimed = Assert.IsType<ClaimOrganizationEnrollmentLinkCommand>(command);
            Assert.Equal(Credential, claimed.Token);
            Assert.Equal(Member, claimed.SubjectId);
            Assert.Equal("user:" + Member, claimed.ActorId);
        }

        Assert.DoesNotContain(Credential, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkspaceStaffOnboardingSourceKind.Invitation)]
    [InlineData(WorkspaceStaffOnboardingSourceKind.EnrollmentLink)]
    public async Task An_expired_source_can_still_preview_pending_recovery_without_a_new_admission_decision(
        WorkspaceStaffOnboardingSourceKind kind)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        fixture.Owner.Expired = true;
        string cookie = await fixture.PrepareAsync(kind);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/preview", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("expired", body.RootElement.GetProperty("status").GetString());
        Assert.Empty(fixture.Owner.Commands);
    }

    [Theory]
    [InlineData("/applications", AccessSubjectKind.AdminActor)]
    [InlineData("/applications", AccessSubjectKind.Service)]
    [InlineData("/join", AccessSubjectKind.AdminActor)]
    [InlineData("/join", AccessSubjectKind.Service)]
    public async Task A_non_user_principal_cannot_use_the_app_join_facade(string operation, AccessSubjectKind kind)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string cookie = await fixture.PrepareAsync(WorkspaceStaffOnboardingSourceKind.Invitation);
        fixture.Owner.SubjectKind = kind;
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, operation, Profile(), cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(fixture.Owner.Commands);
        Assert.Empty(fixture.Owner.Submissions);
    }

    [Fact]
    public async Task Cleanup_deletes_only_the_matching_cookie_path()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string cookie = await fixture.PrepareAsync(WorkspaceStaffOnboardingSourceKind.Invitation);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Delete, "", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cleared = SetCookieHeaderValue.Parse(Assert.Single(response.Headers.GetValues(HeaderNames.SetCookie)));
        Assert.Equal("/api/workspace-join/" + ContextId, cleared.Path.ToString());
        Assert.True(cleared.HttpOnly);
        Assert.True(cleared.Secure);
        Assert.True(cleared.Expires < DateTimeOffset.UtcNow);
    }

    private static object Profile() => new
    {
        displayName = "Proposed Name",
        legalName = "Proposed Legal Name",
        workEmail = "proposed@example.test",
        workPhone = "synthetic-phone",
        employeeNumber = "synthetic-number",
        jobTitle = "Proposed title",
        department = "Proposed department",
        token = "forged-token",
        sourceKind = 2,
        subjectId = "forged-subject",
        organizationId = OtherContextId,
    };

    private static string QueryToken(object query) => query switch
    {
        PreviewOrganizationInvitationQuery invitation => invitation.Token,
        PreviewOrganizationEnrollmentLinkQuery enrollment => enrollment.Token,
        _ => throw new InvalidOperationException("Unexpected controlled query."),
    };

    private sealed class Fixture(WebApplication application, HttpClient client, OwnerProbe owner) : IAsyncDisposable
    {
        public OwnerProbe Owner => owner;

        public static async Task<Fixture> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            var probe = new OwnerProbe();
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton<WorkspaceJoinBrowserContinuation>();
            builder.Services.AddSingleton<IRequestDispatcher>(probe);
            builder.Services.AddSingleton<IWorkspaceStaffOnboardingSubmitter>(probe);
            builder.Services.AddSingleton<IAccessHttpSubjectResolver>(probe);
            builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("synthetic", _ => { });
            builder.Services.AddAuthorization();
            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapWorkspaceJoinBrowserEndpoints();
            await app.StartAsync();
            HttpClient http = app.GetTestClient();
            http.BaseAddress = new Uri("https://localhost");
            return new Fixture(app, http, probe);
        }

        public async Task<string> PrepareAsync(WorkspaceStaffOnboardingSourceKind kind)
        {
            using HttpResponseMessage response = await this.SendAsync(HttpMethod.Post, "", new { sourceKind = kind, token = Credential });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(Credential, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var cookie = SetCookieHeaderValue.Parse(Assert.Single(response.Headers.GetValues(HeaderNames.SetCookie)));
            Assert.True(cookie.HttpOnly);
            Assert.True(cookie.Secure);
            Assert.Equal("/api/workspace-join/" + ContextId, cookie.Path.ToString());
            return cookie.Name + "=" + cookie.Value;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string operation,
            object? body = null, string? cookie = null, bool authenticated = true,
            bool includeContextHeader = true, Guid? contextId = null)
        {
            Guid id = contextId ?? ContextId;
            using var request = new HttpRequestMessage(method, "/api/workspace-join/" + id + operation);
            if (includeContextHeader)
            {
                request.Headers.Add(WorkspaceJoinBrowserContinuation.HeaderName, id.ToString("D"));
            }

            if (authenticated)
            {
                request.Headers.Add("X-Synthetic-Subject", Member);
            }

            if (cookie is not null)
            {
                request.Headers.Add(HeaderNames.Cookie, cookie);
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await application.DisposeAsync();
        }
    }

    private sealed class OwnerProbe : IRequestDispatcher, IWorkspaceStaffOnboardingSubmitter, IAccessHttpSubjectResolver
    {
        public List<object> Queries { get; } = [];
        public List<object> Commands { get; } = [];
        public List<SubmitWorkspaceStaffOnboardingCommand> Submissions { get; } = [];
        public bool Expired { get; set; }
        public bool StaffOwnsIdentity { get; set; }
        public AccessSubjectKind SubjectKind { get; set; } = AccessSubjectKind.User;

        public AccessSubject? ResolveSubject(HttpContext context) => context.User.Identity?.IsAuthenticated == true
            ? new AccessSubject(this.SubjectKind, context.User.FindFirst("sub")!.Value)
            : null;

        public Task<Result<TResponse>> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            this.Queries.Add(query);
            object preview = query switch
            {
                PreviewOrganizationInvitationQuery => new OrganizationInvitationPreviewDto(ContextId, OtherContextId, "Synthetic workspace", "synthetic",
                    false, DateTimeOffset.UtcNow.AddDays(this.Expired ? -1 : 1), this.Expired ? OrganizationInvitationStatus.Expired : OrganizationInvitationStatus.Pending),
                PreviewOrganizationEnrollmentLinkQuery => new OrganizationEnrollmentPreviewDto(ContextId, OtherContextId, "Synthetic workspace", "synthetic",
                    DateTimeOffset.UtcNow.AddDays(this.Expired ? -1 : 1), 1, OrganizationEnrollmentApprovalMode.RequiresApproval,
                    this.Expired ? OrganizationEnrollmentLinkStatus.Expired : OrganizationEnrollmentLinkStatus.Active),
                _ => throw new InvalidOperationException("Unexpected controlled query."),
            };
            return Task.FromResult(Result.Success((TResponse)preview));
        }

        public Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            this.Commands.Add(command);
            return Task.FromResult(Result.Failure<TResponse>(command is AcceptOrganizationInvitationCommand
                ? OrganizationApplicationErrors.RecipientVerificationRequired : OrganizationApplicationErrors.EnrollmentLinkExpired));
        }

        public async Task<Result<WorkspaceStaffOnboardingDto>> SubmitAsync(SubmitWorkspaceStaffOnboardingCommand command, CancellationToken cancellationToken = default) =>
            WorkspaceStaffOnboardingSubmitter.Map(
                await this.SubmitWithAuthorityOutcomeAsync(command, cancellationToken).ConfigureAwait(false));

        public Task<Result<WorkspaceStaffOnboardingSubmissionOutcome>> SubmitWithAuthorityOutcomeAsync(
            SubmitWorkspaceStaffOnboardingCommand command, CancellationToken cancellationToken = default)
        {
            this.Submissions.Add(command);
            return Task.FromResult(this.StaffOwnsIdentity
                ? Result.Success(WorkspaceStaffOnboardingSubmissionOutcome.AuthorityMovedToStaff())
                : Result.Failure<WorkspaceStaffOnboardingSubmissionOutcome>(WorkspaceStaffOnboardingApplicationErrors.VerifiedIdentityRequired));
        }
    }

    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? subject = this.Request.Headers["X-Synthetic-Subject"];
            return Task.FromResult(string.IsNullOrEmpty(subject)
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "synthetic")), "synthetic")));
        }
    }
}

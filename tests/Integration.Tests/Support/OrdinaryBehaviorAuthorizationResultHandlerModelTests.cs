namespace Integration.Tests.Support;

using System.Net;
using Xunit;

public sealed class OrdinaryBehaviorAuthorizationResultHandlerModelTests
{
    [Theory]
    [InlineData(0, "framework-default-fallback")]
    [InlineData(1, "di-single-handler")]
    public void Framework_fallback_and_one_DI_registration_are_one_effective_handler(
        int registeredCount,
        string rawSelection)
    {
        Assert.Equal(
            rawSelection,
            OrdinaryBehaviorAuthorizationResultHandlerModel.RawSelection(registeredCount));
        Assert.Equal(1, OrdinaryBehaviorAuthorizationResultHandlerModel.EffectiveCount(registeredCount));
        Assert.Equal(
            "single-effective-handler",
            OrdinaryBehaviorAuthorizationResultHandlerModel.EffectiveComposition(registeredCount));
    }

    [Fact]
    public void Multiple_DI_registrations_remain_visible_and_invalid()
    {
        Assert.Equal(
            "invalid-multiple-di-handlers",
            OrdinaryBehaviorAuthorizationResultHandlerModel.RawSelection(2));
        Assert.Equal(2, OrdinaryBehaviorAuthorizationResultHandlerModel.EffectiveCount(2));
        Assert.Equal(
            "invalid-multiple-effective-handlers",
            OrdinaryBehaviorAuthorizationResultHandlerModel.EffectiveComposition(2));
    }
}

public sealed class OrdinaryBehaviorEvidenceNormalizationTests
{
    [Fact]
    public void Embedded_generated_material_outside_the_path_allowlist_fails_closed()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            OrdinaryBehaviorComparisonEvidence.CanonicalizeBody(
                """{"message":"unexpected id 86d6b3d6-22f4-4cdd-a84e-94ea2f8b27cd in response"}"""));

        Assert.Contains("Unlisted generated response value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_scope_path_retains_a_GUID_shaped_fixture_value()
    {
        const string body =
            """{"permissions":[{"scope":"b1000000-0000-0000-0000-000000000001"}]}""";

        Assert.Equal(body, OrdinaryBehaviorComparisonEvidence.CanonicalizeBody(body));
    }

    [Fact]
    public void Cookie_path_case_is_retained_while_the_cookie_value_is_redacted()
    {
        using HttpResponseMessage response = new(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation(
            "Set-Cookie",
            "session=sensitive-value; Path=/API/CaseSensitive; HttpOnly");

        IReadOnlyDictionary<string, string[]> headers =
            OrdinaryBehaviorComparisonEvidence.NormalizeHeaders(response);

        Assert.Equal(
            "session=<redacted>;httponly;path=/API/CaseSensitive",
            Assert.Single(headers["set-cookie"]));
    }

    [Fact]
    public void Sse_delivery_normalizes_only_named_generated_fields()
    {
        const string body =
            """{"sentAtUtc":"2026-08-30T20:00:00Z","kind":"notification","notification":{"id":"86d6b3d6-22f4-4cdd-a84e-94ea2f8b27cd","name":"streaming.test","occurredAtUtc":"2026-08-30T19:59:59Z","scopeId":"b1000000-0000-0000-0000-000000000001","severity":"warning","title":"CMP SSE delivered message"}}""";

        Assert.Equal(
            """{"kind":"notification","notification":{"id":"\u003Cgenerated-id\u003E","name":"streaming.test","occurredAtUtc":"\u003Ctimestamp\u003E","scopeId":"b1000000-0000-0000-0000-000000000001","severity":"warning","title":"CMP SSE delivered message"},"sentAtUtc":"\u003Ctimestamp\u003E"}""",
            OrdinaryBehaviorComparisonEvidence.CanonicalizeBody(body));
    }
}

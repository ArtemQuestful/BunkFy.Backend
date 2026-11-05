namespace Integration.Tests.Auth;

using Gma.Framework.Messaging;
using Gma.Framework.Scoping;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Notifications.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Integration.Tests.Support;

[Trait("Category", "Integration")]
public sealed class AuthNotificationConsumerScopeTests
{
    private const string IdentityScope = "notification-consumer-identity";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualHostBindsEachRecoveryEventBeforeNotificationAdmission(bool passwordRecovery)
    {
        await using AuthTestApplication factory = CreateFactory();
        await using AsyncServiceScope first = factory.Services.CreateAsyncScope();
        await using AsyncServiceScope second = factory.Services.CreateAsyncScope();
        IScopeContextAccessor firstContext = first.ServiceProvider.GetRequiredService<IScopeContextAccessor>();
        IScopeContextAccessor secondContext = second.ServiceProvider.GetRequiredService<IScopeContextAccessor>();
        Assert.True(firstContext.IsEnabled);
        Assert.Null(firstContext.ScopeId);
        Assert.Null(secondContext.ScopeId);

        // A previous tenant selection must never determine the scope of a
        // separately authenticated integration event.
        firstContext.SetScope("previous-hostel");
        Prepare(first.ServiceProvider, Event(passwordRecovery, IdentityScope), passwordRecovery);
        NotificationsDbContext firstDatabase = first.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        Assert.True(firstDatabase.ScopeFilterEnabled);
        Assert.Equal(IdentityScope, firstDatabase.CurrentScopeId);
        Assert.Null(secondContext.ScopeId);

        Prepare(second.ServiceProvider, Event(passwordRecovery, "independent-identity"), passwordRecovery);
        NotificationsDbContext secondDatabase = second.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        Assert.Equal("independent-identity", secondDatabase.CurrentScopeId);
        Assert.Equal(IdentityScope, firstDatabase.CurrentScopeId);
        Assert.NotSame(firstDatabase, secondDatabase);
    }

    [Fact]
    public async Task ScopedRecoverySubscriptionRejectsAnEventWithoutScopeAndClearsPreviousContext()
    {
        await using AuthTestApplication factory = CreateFactory();
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IScopeContextAccessor context = scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>();
        context.SetScope("previous-hostel");

        Assert.Throws<InvalidOperationException>(() =>
            Prepare(scope.ServiceProvider, new UnscopedEvent(), passwordRecovery: false));

        Assert.Null(context.ScopeId);
        Assert.True(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().ScopeFilterEnabled);
    }

    private static void Prepare(IServiceProvider provider, IIntegrationEvent integrationEvent, bool passwordRecovery)
    {
        string handlerName = passwordRecovery
            ? "auth-password-recovery-request-notification"
            : "auth-email-verification-request-notification";
        IntegrationEventSubscription subscription = Assert.Single(provider
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>().Subscriptions,
            item => item.HandlerName == handlerName);
        IIntegrationEventProcessingContextContributor[] contributors = provider
            .GetServices<IIntegrationEventProcessingContextContributor>().ToArray();
        Assert.NotEmpty(contributors);
        foreach (IIntegrationEventProcessingContextContributor contributor in contributors)
        {
            contributor.Prepare(subscription, integrationEvent);
        }
    }

    private static IIntegrationEvent Event(bool passwordRecovery, string scopeId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return passwordRecovery
            ? new MemberPasswordRecoveryRequestedIntegrationEvent(Guid.NewGuid(), scopeId, now,
                Guid.NewGuid(), Guid.NewGuid(), "synthetic@example.invalid", "synthetic-code", now.AddMinutes(5))
            : new MemberEmailVerificationRequestedIntegrationEvent(Guid.NewGuid(), scopeId, now,
                Guid.NewGuid(), "synthetic@example.invalid", "synthetic-code", now.AddMinutes(5));
    }

    private static AuthTestApplication CreateFactory() =>
        new("PostgreSql",
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable;Password=synthetic",
            "nats://127.0.0.1:1",
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Auth:GlobalScopeId"] = IdentityScope,
                ["NatsConsumers:Enabled"] = "false",
            });

    private sealed record UnscopedEvent() : IntegrationEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "unscoped-probe", 1);
}

namespace BunkFy.Modules.Retention.Persistence;

using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;

internal sealed class RetentionOutboxWriter(
    RetentionDbContext dbContext,
    ISystemClock clock,
    IOptions<ApplicationIdentityOptions> applicationIdentity,
    IEnumerable<IIntegrationEventScopeResolver> scopeResolvers)
    : EfOutboxWriter<RetentionDbContext>(
        dbContext,
        clock,
        applicationIdentity,
        RetentionMigrations.Schema,
        scopeResolvers);

namespace BunkFy.Modules.Retention.Persistence;

using Gma.Framework.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

internal sealed class RetentionOutboxStore(
    RetentionDbContext dbContext,
    IOptions<OutboxOptions> options)
    : EfOutboxStore<RetentionDbContext>(
        dbContext,
        options,
        RetentionMigrations.Schema)
{
    protected override IQueryable<OutboxMessage> ApplyClaimAdmission(
        IQueryable<OutboxMessage> candidates) =>
        candidates.Where(message =>
            message.ScopeId == null ||
            !this.DbContext.TenantRevisions
                .IgnoreQueryFilters()
                .Any(state =>
                    state.ScopeId == message.ScopeId &&
                    state.LifecycleStatus !=
                        RetentionTenantLifecycleStatus.Open));
}

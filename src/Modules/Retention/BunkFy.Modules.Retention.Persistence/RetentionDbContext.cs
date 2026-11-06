namespace BunkFy.Modules.Retention.Persistence;

using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Persistence.TenantTermination;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public sealed class RetentionDbContext(
    DbContextOptions<RetentionDbContext> options,
    IScopeContext scopeContext,
    IWorkspaceTerminationFenceReader? terminationFences = null)
    : ScopeAwareDbContext<RetentionDbContext>(options, scopeContext)
{
    private readonly IScopeContext scopeContext = scopeContext;

    public DbSet<RetentionExecution> Executions => this.Set<RetentionExecution>();
    public DbSet<RetentionTenantProjection> TenantProjections =>
        this.Set<RetentionTenantProjection>();
    public DbSet<RetentionPropertyProjection> PropertyProjections =>
        this.Set<RetentionPropertyProjection>();
    public DbSet<RetentionScheduleState> ScheduleStates =>
        this.Set<RetentionScheduleState>();
    public DbSet<RetentionRunRetryRequest> RunRetryRequests =>
        this.Set<RetentionRunRetryRequest>();
    public DbSet<InboxMessage> InboxMessages => this.Set<InboxMessage>();
    public DbSet<OutboxMessage> OutboxMessages => this.Set<OutboxMessage>();
    internal DbSet<RetentionTenantRevision> TenantRevisions =>
        this.Set<RetentionTenantRevision>();
    internal DbSet<RetentionTenantDestroyOperation> TenantDestroyOperations =>
        this.Set<RetentionTenantDestroyOperation>();
    internal DbSet<RetentionTenantDestroyReceipt> TenantDestroyReceipts =>
        this.Set<RetentionTenantDestroyReceipt>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.EnsureTenantDestroyReceiptsAreAppendOnly();
        return this.SaveChangesWithAdmissionAsync(
                acceptAllChangesOnSuccess,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        this.EnsureTenantDestroyReceiptsAreAppendOnly();
        return this.SaveChangesWithAdmissionAsync(
            acceptAllChangesOnSuccess,
            cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(RetentionMigrations.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RetentionDbContext).Assembly);
        this.ApplyScopeConventions(modelBuilder);
    }

    private async Task<int> SaveChangesWithAdmissionAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        bool hasOperationalMutation = this.HasOperationalMutation();
        if (!hasOperationalMutation && !this.HasTenantOwnedMutation())
        {
            return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess,
                cancellationToken).ConfigureAwait(false);
        }

        if (!this.scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(this.scopeContext.ScopeId))
        {
            throw new RetentionOperationalAdmissionException(
                RetentionOperationalAdmissionFailure.Unavailable);
        }

        string tenantId = this.scopeContext.ScopeId;
        IDbContextTransaction? ownedTransaction = null;
        if (this.Database.IsRelational() &&
            this.Database.CurrentTransaction is null)
        {
            ownedTransaction = await this.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            if (this.Database.IsRelational())
            {
                await RetentionTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    tenantId,
                    cancellationToken).ConfigureAwait(false);
                if (hasOperationalMutation)
                {
                    await RetentionTenantMutationLock.AcquireRevisionAdvanceAsync(
                        this,
                        tenantId,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await this.EnsureOperationalAdmissionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (hasOperationalMutation)
            {
                await this.AdvanceTenantRevisionAsync(
                    tenantId,
                    cancellationToken).ConfigureAwait(false);
            }

            int affected = await base.SaveChangesAsync(
                acceptAllChangesOnSuccess,
                cancellationToken).ConfigureAwait(false);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return affected;
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureOperationalAdmissionAsync(
        CancellationToken cancellationToken)
    {
        RetentionTenantRevision? localState =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (localState is not null && !localState.IsOpen)
        {
            throw new RetentionOperationalAdmissionException(
                RetentionOperationalAdmissionFailure.Restricted);
        }

        if (terminationFences is null)
        {
            if (this.Database.IsRelational())
            {
                throw new RetentionOperationalAdmissionException(
                    RetentionOperationalAdmissionFailure.Unavailable);
            }

            return;
        }

        WorkspaceTerminationFenceSnapshot? fence;
        try
        {
            fence = await terminationFences.GetCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            throw new RetentionOperationalAdmissionException(
                RetentionOperationalAdmissionFailure.Unavailable);
        }

        if (fence is null)
        {
            return;
        }

        bool valid = fence.ProcessId != Guid.Empty &&
            fence.TerminationEpoch != Guid.Empty &&
            fence.Version > 0 &&
            fence.State is
                WorkspaceTerminationFenceState.Frozen or
                WorkspaceTerminationFenceState.DestructionStarted or
                WorkspaceTerminationFenceState.Closed;
        throw new RetentionOperationalAdmissionException(
            valid
                ? RetentionOperationalAdmissionFailure.Restricted
                : RetentionOperationalAdmissionFailure.Unavailable);
    }

    private async Task AdvanceTenantRevisionAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        RetentionTenantRevision? revision =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (revision is null)
        {
            this.TenantRevisions.Add(
                RetentionTenantRevision.Create(tenantId));
        }
        else
        {
            revision.Advance();
        }
    }

    private bool HasOperationalMutation() =>
        this.ChangeTracker.Entries().Any(entry =>
            (entry.State is EntityState.Added or
                EntityState.Modified or EntityState.Deleted) &&
            entry.Entity is not (
                InboxMessage or
                OutboxMessage or
                RetentionTenantRevision or
                RetentionTenantDestroyOperation or
                RetentionTenantDestroyReceipt));

    private bool HasTenantOwnedMutation() =>
        this.ChangeTracker.Entries().Any(entry =>
            (entry.State is EntityState.Added or
                EntityState.Modified or EntityState.Deleted) &&
            entry.Entity switch
            {
                InboxMessage message =>
                    entry.State == EntityState.Added &&
                    !string.IsNullOrWhiteSpace(message.ScopeId),
                OutboxMessage message =>
                    entry.State == EntityState.Added &&
                    !string.IsNullOrWhiteSpace(message.ScopeId),
                RetentionTenantRevision or
                RetentionTenantDestroyOperation or
                RetentionTenantDestroyReceipt => false,
                _ => true
            });

    internal async ValueTask<bool> TryAdmitMessageMutationAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (!TenantIds.TryNormalize(tenantId, out string? canonicalTenantId) ||
            !this.scopeContext.IsEnabled ||
            !string.Equals(
                this.scopeContext.ScopeId,
                canonicalTenantId,
                StringComparison.Ordinal) ||
            (this.Database.IsRelational() &&
             this.Database.CurrentTransaction is null))
        {
            return false;
        }

        try
        {
            if (this.Database.IsRelational())
            {
                await RetentionTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    canonicalTenantId,
                    cancellationToken).ConfigureAwait(false);
            }

            await this.EnsureOperationalAdmissionAsync(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RetentionOperationalAdmissionException)
        {
            return false;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    internal async Task<int> SaveTenantDestructionChangesAsync(
        string tenantId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!TenantIds.TryNormalize(tenantId, out string? canonicalTenantId) ||
            operationId == Guid.Empty ||
            !this.scopeContext.IsEnabled ||
            !string.Equals(
                this.scopeContext.ScopeId,
                canonicalTenantId,
                StringComparison.Ordinal) ||
            (this.Database.IsRelational() &&
             this.Database.CurrentTransaction is null))
        {
            throw new InvalidOperationException(
                "Retention tenant destruction admission is invalid.");
        }

        RetentionTenantRevision? state =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (state is null || state.DestroyOperationId != operationId ||
            state.LifecycleStatus is not (
                RetentionTenantLifecycleStatus.Closing or
                RetentionTenantLifecycleStatus.Closed))
        {
            throw new InvalidOperationException(
                "Retention tenant destruction state is invalid.");
        }

        return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureTenantDestroyReceiptsAreAppendOnly()
    {
        bool mutationRequested = this.ChangeTracker
            .Entries<RetentionTenantDestroyReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        if (mutationRequested)
        {
            throw new InvalidOperationException(
                "Retention tenant destruction receipts are append-only.");
        }
    }
}

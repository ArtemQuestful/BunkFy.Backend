namespace BunkFy.Modules.Properties.Persistence;

using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Persistence.TenantTermination;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public sealed class PropertiesDbContext(
    DbContextOptions<PropertiesDbContext> options,
    IScopeContext scopeContext,
    IWorkspaceTerminationFenceReader? terminationFences = null)
    : ScopeAwareDbContext<PropertiesDbContext>(options, scopeContext)
{
    private readonly IScopeContext scopeContext = scopeContext;

    public DbSet<Property> Properties => this.Set<Property>();
    public DbSet<Room> Rooms => this.Set<Room>();
    internal DbSet<PropertyOperationLock> PropertyOperationLocks =>
        this.Set<PropertyOperationLock>();
    internal DbSet<RoomOperationLock> RoomOperationLocks =>
        this.Set<RoomOperationLock>();
    internal DbSet<PropertyMutationOperation> PropertyMutationOperations =>
        this.Set<PropertyMutationOperation>();
    internal DbSet<PropertyTimeZoneOperation> PropertyTimeZoneOperations =>
        this.Set<PropertyTimeZoneOperation>();
    internal DbSet<PropertyTimeZoneCatalogEntry> PropertyTimeZoneCatalogEntries =>
        this.Set<PropertyTimeZoneCatalogEntry>();
    internal DbSet<PropertyTimeZoneCatalogResolution>
        PropertyTimeZoneCatalogResolutions =>
            this.Set<PropertyTimeZoneCatalogResolution>();
    public DbSet<PropertyGovernanceRevision> GovernanceRevisions =>
        this.Set<PropertyGovernanceRevision>();
    public DbSet<OutboxMessage> OutboxMessages => this.Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => this.Set<InboxMessage>();
    internal DbSet<PropertiesTenantRevision> TenantRevisions =>
        this.Set<PropertiesTenantRevision>();
    internal DbSet<PropertiesTenantDestroyOperation> TenantDestroyOperations =>
        this.Set<PropertiesTenantDestroyOperation>();
    internal DbSet<PropertiesTenantDestroyReceipt> TenantDestroyReceipts =>
        this.Set<PropertiesTenantDestroyReceipt>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.EnsureProofRecordsAreAppendOnly();
        return this.SaveChangesWithAdmissionAsync(
                acceptAllChangesOnSuccess,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        this.EnsureProofRecordsAreAppendOnly();
        return await this.SaveChangesWithAdmissionAsync(
            acceptAllChangesOnSuccess,
            cancellationToken).ConfigureAwait(false);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(PropertiesMigrations.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PropertiesDbContext).Assembly);
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
            throw new PropertiesOperationalAdmissionException(
                PropertiesOperationalAdmissionFailure.Unavailable);
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
            await this.AcquireOperationalMutationAdmissionAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (this.Database.IsRelational() && hasOperationalMutation)
            {
                await PropertiesTenantMutationLock.AcquireRevisionAdvanceAsync(
                        this,
                        tenantId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
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
                try
                {
                    await ownedTransaction.RollbackAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // A failed deferred-constraint commit can already have
                    // completed the provider transaction. Preserve the
                    // original commit exception instead of masking it with a
                    // secondary rollback failure.
                }
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

    internal async Task AcquireOperationalMutationAdmissionAsync(
        CancellationToken cancellationToken)
    {
        if (!this.scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(this.scopeContext.ScopeId) ||
            (this.Database.IsRelational() &&
             this.Database.CurrentTransaction is null))
        {
            throw new PropertiesOperationalAdmissionException(
                PropertiesOperationalAdmissionFailure.Unavailable);
        }

        if (this.Database.IsRelational())
        {
            await PropertiesTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    this.scopeContext.ScopeId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await this.EnsureOperationalAdmissionAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureOperationalAdmissionAsync(
        CancellationToken cancellationToken)
    {
        PropertiesTenantRevision? localState =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (localState is not null && !localState.IsOpen)
        {
            throw new PropertiesOperationalAdmissionException(
                PropertiesOperationalAdmissionFailure.Restricted);
        }

        if (terminationFences is null)
        {
            if (this.Database.IsRelational())
            {
                throw new PropertiesOperationalAdmissionException(
                    PropertiesOperationalAdmissionFailure.Unavailable);
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
            throw new PropertiesOperationalAdmissionException(
                PropertiesOperationalAdmissionFailure.Unavailable);
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
        throw new PropertiesOperationalAdmissionException(
            valid
                ? PropertiesOperationalAdmissionFailure.Restricted
                : PropertiesOperationalAdmissionFailure.Unavailable);
    }

    private async Task AdvanceTenantRevisionAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        PropertiesTenantRevision? revision =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (revision is null)
        {
            this.TenantRevisions.Add(
                PropertiesTenantRevision.Create(tenantId));
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
                PropertyOperationLock or
                RoomOperationLock or
                PropertiesTenantRevision or
                PropertiesTenantDestroyOperation or
                PropertiesTenantDestroyReceipt));

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
                PropertiesTenantRevision or
                PropertiesTenantDestroyOperation or
                PropertiesTenantDestroyReceipt => false,
                _ => true
            });

    private void EnsureProofRecordsAreAppendOnly()
    {
        if (this.ChangeTracker
            .Entries<PropertyGovernanceRevision>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Property governance revisions are append-only.");
        }

        if (this.ChangeTracker
            .Entries<PropertyMutationOperation>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Property mutation operations are append-only.");
        }

        if (this.ChangeTracker
            .Entries<PropertyTimeZoneOperation>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Property time-zone operations are append-only.");
        }

        if (this.ChangeTracker
            .Entries<PropertyTimeZoneCatalogEntry>()
            .Any(entry =>
                entry.State is EntityState.Added or
                    EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Property time-zone catalog entries are migration-owned and immutable.");
        }

        if (this.ChangeTracker
            .Entries<PropertyTimeZoneCatalogResolution>()
            .Any(entry =>
                entry.State is EntityState.Added or
                    EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Property time-zone catalog resolutions are migration-owned and immutable.");
        }

        if (this.ChangeTracker
            .Entries<PropertiesTenantDestroyReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Properties tenant destruction receipts are append-only.");
        }
    }

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
                await PropertiesTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    canonicalTenantId,
                    cancellationToken).ConfigureAwait(false);
            }

            await this.EnsureOperationalAdmissionAsync(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (PropertiesOperationalAdmissionException)
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
                "Properties tenant destruction admission is invalid.");
        }

        PropertiesTenantRevision? state =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (state is null || state.DestroyOperationId != operationId ||
            state.LifecycleStatus is not (
                PropertiesTenantLifecycleStatus.Closing or
                PropertiesTenantLifecycleStatus.Closed))
        {
            throw new InvalidOperationException(
                "Properties tenant destruction state is invalid.");
        }

        if (this.Database.IsNpgsql())
        {
            await this.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('bunkfy.properties_tenant_destroy_operation_id', {operationId.ToString("D")}, true)",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

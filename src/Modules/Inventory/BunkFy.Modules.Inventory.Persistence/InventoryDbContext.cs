namespace BunkFy.Modules.Inventory.Persistence;

using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.DataRights;
using BunkFy.Modules.Inventory.Domain.Entities;
using BunkFy.Modules.Inventory.Persistence.TenantTermination;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public sealed class InventoryDbContext(
    DbContextOptions<InventoryDbContext> options,
    IScopeContext scopeContext,
    IWorkspaceTerminationFenceReader? terminationFences = null)
    : ScopeAwareDbContext<InventoryDbContext>(options, scopeContext)
{
    private readonly IScopeContext scopeContext = scopeContext;

    public DbSet<InventoryPropertyTopology> PropertyTopology => this.Set<InventoryPropertyTopology>();
    public DbSet<InventoryRoomTopology> RoomTopology => this.Set<InventoryRoomTopology>();
    public DbSet<InventoryBedTopology> BedTopology => this.Set<InventoryBedTopology>();
    public DbSet<InventoryUnit> InventoryUnits => this.Set<InventoryUnit>();
    public DbSet<RoomInventoryConfiguration> RoomConfigurations => this.Set<RoomInventoryConfiguration>();
    internal DbSet<InventoryManagementOperation> ManagementOperations =>
        this.Set<InventoryManagementOperation>();
    public DbSet<ManualInventoryBlockGroup> ManualBlockGroups =>
        this.Set<ManualInventoryBlockGroup>();
    public DbSet<ManualInventoryBlock> ManualBlocks => this.Set<ManualInventoryBlock>();
    public DbSet<InventoryAllocation> Allocations => this.Set<InventoryAllocation>();
    public DbSet<InventoryAllocationUnit> AllocationUnits => this.Set<InventoryAllocationUnit>();
    public DbSet<InventoryAllocationAmendmentDecision> AllocationAmendmentDecisions => this.Set<InventoryAllocationAmendmentDecision>();
    internal DbSet<InventoryAllocationOperationLock> AllocationOperationLocks => this.Set<InventoryAllocationOperationLock>();
    public DbSet<InventoryAllocationAnonymisationReceipt> AllocationAnonymisationReceipts => this.Set<InventoryAllocationAnonymisationReceipt>();
    public DbSet<InventoryAllocationAnonymisationTombstone> AllocationAnonymisationTombstones => this.Set<InventoryAllocationAnonymisationTombstone>();
    public DbSet<InventoryAllocationAnonymisationRestoreReceipt> AllocationAnonymisationRestoreReceipts => this.Set<InventoryAllocationAnonymisationRestoreReceipt>();
    public DbSet<BedRetirementProcess> BedRetirements => this.Set<BedRetirementProcess>();
    public DbSet<RoomRetirementProcess> RoomRetirements => this.Set<RoomRetirementProcess>();
    public DbSet<OutboxMessage> OutboxMessages => this.Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => this.Set<InboxMessage>();
    public DbSet<InventoryProjectionRebuildCheckpoint> ProjectionRebuildCheckpoints => this.Set<InventoryProjectionRebuildCheckpoint>();
    internal DbSet<InventoryTenantRevision> TenantRevisions =>
        this.Set<InventoryTenantRevision>();
    internal DbSet<InventoryTenantDestroyOperation> TenantDestroyOperations =>
        this.Set<InventoryTenantDestroyOperation>();
    internal DbSet<InventoryTenantDestroyReceipt> TenantDestroyReceipts =>
        this.Set<InventoryTenantDestroyReceipt>();

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
        modelBuilder.HasDefaultSchema(InventoryMigrations.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
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
            throw new InventoryOperationalAdmissionException(
                InventoryOperationalAdmissionFailure.Unavailable);
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
                await InventoryTenantMutationLock.AcquireRevisionAdvanceAsync(
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
                    // A deferred provider constraint can complete the transaction
                    // while CommitAsync is failing. Preserve that original failure;
                    // a best-effort rollback must never replace its diagnostics.
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
            throw new InventoryOperationalAdmissionException(
                InventoryOperationalAdmissionFailure.Unavailable);
        }

        if (this.Database.IsRelational())
        {
            await InventoryTenantMutationLock.AcquireAdmissionAsync(
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
        InventoryTenantRevision? localState =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (localState is not null && !localState.IsOpen)
        {
            throw new InventoryOperationalAdmissionException(
                InventoryOperationalAdmissionFailure.Restricted);
        }

        if (terminationFences is null)
        {
            if (this.Database.IsRelational())
            {
                throw new InventoryOperationalAdmissionException(
                    InventoryOperationalAdmissionFailure.Unavailable);
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
            throw new InventoryOperationalAdmissionException(
                InventoryOperationalAdmissionFailure.Unavailable);
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
        throw new InventoryOperationalAdmissionException(
            valid
                ? InventoryOperationalAdmissionFailure.Restricted
                : InventoryOperationalAdmissionFailure.Unavailable);
    }

    private async Task AdvanceTenantRevisionAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        InventoryTenantRevision? revision =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (revision is null)
        {
            this.TenantRevisions.Add(
                InventoryTenantRevision.Create(tenantId));
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
                InventoryProjectionRebuildCheckpoint or
                InventoryTenantRevision or
                InventoryTenantDestroyOperation or
                InventoryTenantDestroyReceipt));

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
                InventoryTenantRevision or
                InventoryTenantDestroyOperation or
                InventoryTenantDestroyReceipt => false,
                _ => true
            });

    private void EnsureProofRecordsAreAppendOnly()
    {
        this.EnsureManualBlockGroupTransitionsAreLegal();
        bool receiptMutation = this.ChangeTracker
            .Entries<InventoryAllocationAnonymisationReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        bool restoreReceiptMutation = this.ChangeTracker
            .Entries<InventoryAllocationAnonymisationRestoreReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        if (receiptMutation || restoreReceiptMutation)
        {
            throw new InvalidOperationException(
                "Inventory anonymisation receipts are append-only.");
        }

        bool tombstoneDeletion = this.ChangeTracker
            .Entries<InventoryAllocationAnonymisationTombstone>()
            .Any(entry => entry.State == EntityState.Deleted);
        if (tombstoneDeletion)
        {
            throw new InvalidOperationException(
                "Inventory anonymisation tombstones cannot be deleted.");
        }

        bool tenantDestroyReceiptMutation = this.ChangeTracker
            .Entries<InventoryTenantDestroyReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        if (tenantDestroyReceiptMutation)
        {
            throw new InvalidOperationException(
                "Inventory tenant destruction receipts are append-only.");
        }

        bool managementOperationMutation = this.ChangeTracker
            .Entries<InventoryManagementOperation>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        if (managementOperationMutation)
        {
            throw new InvalidOperationException(
                "Inventory management operation receipts are append-only.");
        }
    }

    private void EnsureManualBlockGroupTransitionsAreLegal()
    {
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<
                     ManualInventoryBlockGroup> entry in this.ChangeTracker
                     .Entries<ManualInventoryBlockGroup>())
        {
            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    "Inventory manual block groups cannot be deleted.");
            }

            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            string[] immutableProperties =
            [
                nameof(ManualInventoryBlockGroup.Id),
                nameof(ManualInventoryBlockGroup.ScopeId),
                nameof(ManualInventoryBlockGroup.PropertyId),
                nameof(ManualInventoryBlockGroup.TargetKind),
                nameof(ManualInventoryBlockGroup.BuildingLabel),
                nameof(ManualInventoryBlockGroup.FloorLabel),
                nameof(ManualInventoryBlockGroup.RoomId),
                nameof(ManualInventoryBlockGroup.InventoryUnitId),
                nameof(ManualInventoryBlockGroup.Arrival),
                nameof(ManualInventoryBlockGroup.Departure),
                nameof(ManualInventoryBlockGroup.Reason),
                nameof(ManualInventoryBlockGroup.SelectionDigest),
                nameof(ManualInventoryBlockGroup.MembershipDigest),
                nameof(ManualInventoryBlockGroup.MembershipDigestVersion),
                nameof(ManualInventoryBlockGroup.InitialBlockCount),
                nameof(ManualInventoryBlockGroup.ReplacesGroupId),
                nameof(ManualInventoryBlockGroup.CreatedAtUtc),
                nameof(ManualInventoryBlockGroup.CreatedByActorId)
            ];
            if (immutableProperties.Any(propertyName =>
                    entry.Property(propertyName).IsModified))
            {
                throw new InvalidOperationException(
                    "Inventory manual block-group definitions are immutable.");
            }

            ManualInventoryBlockGroupState originalState =
                entry.Property(group => group.State).OriginalValue;
            ManualInventoryBlockGroupState currentState =
                entry.Entity.State;
            int originalCount = entry.Property(group => group.ActiveBlockCount)
                .OriginalValue;
            int currentCount = entry.Entity.ActiveBlockCount;
            long originalVersion = entry.Property(group => group.Version)
                .OriginalValue;
            bool legalState = originalState switch
            {
                ManualInventoryBlockGroupState.Active => currentState is
                    ManualInventoryBlockGroupState.PartiallyReleased or
                    ManualInventoryBlockGroupState.Released or
                    ManualInventoryBlockGroupState.Replaced,
                ManualInventoryBlockGroupState.PartiallyReleased =>
                    currentState is
                        ManualInventoryBlockGroupState.PartiallyReleased or
                        ManualInventoryBlockGroupState.Released or
                        ManualInventoryBlockGroupState.Replaced,
                _ => false
            };
            bool legalCount = currentCount >= 0 &&
                currentCount < originalCount;
            bool legalVersion = entry.Entity.Version == originalVersion + 1;
            bool legalTimestamps = entry.Entity.UpdatedAtUtc is { } updated &&
                updated >= entry.Entity.CreatedAtUtc &&
                ((currentState is ManualInventoryBlockGroupState.Released or
                      ManualInventoryBlockGroupState.Replaced &&
                  entry.Entity.ReleasedAtUtc == updated) ||
                 (currentState ==
                      ManualInventoryBlockGroupState.PartiallyReleased &&
                  entry.Entity.ReleasedAtUtc is null));
            string? currentActor = entry.Entity.LastModifiedByActorId;
            bool legalActor = !string.IsNullOrWhiteSpace(currentActor);
            if (!legalState || !legalCount || !legalVersion ||
                !legalTimestamps || !legalActor)
            {
                throw new InvalidOperationException(
                    "Inventory manual block-group transition is invalid.");
            }
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
                await InventoryTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    canonicalTenantId,
                    cancellationToken).ConfigureAwait(false);
            }

            await this.EnsureOperationalAdmissionAsync(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (InventoryOperationalAdmissionException)
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
                "Inventory tenant destruction admission is invalid.");
        }

        InventoryTenantRevision? state =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (state is null || state.DestroyOperationId != operationId ||
            state.LifecycleStatus is not (
                InventoryTenantLifecycleStatus.Closing or
                InventoryTenantLifecycleStatus.Closed))
        {
            throw new InvalidOperationException(
                "Inventory tenant destruction state is invalid.");
        }

        if (this.Database.IsNpgsql())
        {
            await this.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('bunkfy.inventory_tenant_destroy_operation_id', {operationId.ToString("D")}, true)",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

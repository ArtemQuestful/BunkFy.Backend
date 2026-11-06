namespace BunkFy.Modules.Reservations.Persistence;

using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Entities;
using BunkFy.Modules.Reservations.Domain.GuestRecords;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using BunkFy.Modules.Reservations.Persistence.TenantTermination;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public sealed class ReservationsDbContext(
    DbContextOptions<ReservationsDbContext> options,
    IScopeContext scopeContext,
    IWorkspaceTerminationFenceReader? terminationFences = null)
    : ScopeAwareDbContext<ReservationsDbContext>(options, scopeContext)
{
    private readonly IScopeContext scopeContext = scopeContext;

    public DbSet<OutboxMessage> OutboxMessages => this.Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => this.Set<InboxMessage>();
    public DbSet<Reservation> Reservations => this.Set<Reservation>();
    public DbSet<ReservationDataRightsCorrectionReceipt> DataRightsCorrectionReceipts =>
        this.Set<ReservationDataRightsCorrectionReceipt>();
    public DbSet<ReservationProcessingRestriction> ProcessingRestrictions =>
        this.Set<ReservationProcessingRestriction>();
    public DbSet<ReservationProcessingRestrictionReceipt> ProcessingRestrictionReceipts =>
        this.Set<ReservationProcessingRestrictionReceipt>();
    public DbSet<ReservationProcessingRestrictionProjection>
        ProcessingRestrictionProjections =>
        this.Set<ReservationProcessingRestrictionProjection>();
    public DbSet<ReservationDataHold> DataHolds =>
        this.Set<ReservationDataHold>();
    public DbSet<ReservationDataHoldReceipt> DataHoldReceipts =>
        this.Set<ReservationDataHoldReceipt>();
    public DbSet<ReservationAnonymisationReceipt> AnonymisationReceipts =>
        this.Set<ReservationAnonymisationReceipt>();
    public DbSet<ReservationAnonymisationTombstone>
        AnonymisationTombstones =>
        this.Set<ReservationAnonymisationTombstone>();
    public DbSet<ReservationAnonymisationRestoreReceipt>
        AnonymisationRestoreReceipts =>
        this.Set<ReservationAnonymisationRestoreReceipt>();
    public DbSet<ReservationRetentionExecution> RetentionExecutions =>
        this.Set<ReservationRetentionExecution>();
    public DbSet<ReservationRetentionSweepCheckpoint>
        RetentionSweepCheckpoints =>
        this.Set<ReservationRetentionSweepCheckpoint>();
    public DbSet<ReservationRetentionAnonymisationReceipt>
        RetentionAnonymisationReceipts =>
        this.Set<ReservationRetentionAnonymisationReceipt>();
    public DbSet<RequestedInventoryUnit> RequestedInventoryUnits => this.Set<RequestedInventoryUnit>();
    public DbSet<ReservationGuest> ReservationGuests => this.Set<ReservationGuest>();
    public DbSet<ReservationGuestRecordLinkProcess> GuestRecordLinkProcesses =>
        this.Set<ReservationGuestRecordLinkProcess>();
    public DbSet<ReservationGuestProfileProjection> GuestProfileProjections => this.Set<ReservationGuestProfileProjection>();
    public DbSet<ReservationGuestProcessingRestrictionProjection> GuestProcessingRestrictionProjections =>
        this.Set<ReservationGuestProcessingRestrictionProjection>();
    public DbSet<ReservationDetailsHistoryEntry> ReservationDetailsHistory => this.Set<ReservationDetailsHistoryEntry>();
    public DbSet<ReservationPropertyProjection> PropertyProjections => this.Set<ReservationPropertyProjection>();
    public DbSet<ReservationArrivalReminder> ArrivalReminders => this.Set<ReservationArrivalReminder>();
    public DbSet<ReservationExternalOperation> ExternalOperations => this.Set<ReservationExternalOperation>();
    internal DbSet<ReservationManagementOperation> ManagementOperations =>
        this.Set<ReservationManagementOperation>();
    internal DbSet<ReservationStationAttribution> StationAttributions => this.Set<ReservationStationAttribution>();
    internal DbSet<ReservationStayAmendmentOperation> StayAmendmentOperations =>
        this.Set<ReservationStayAmendmentOperation>();
    internal DbSet<ReservationOperationLock> OperationLocks =>
        this.Set<ReservationOperationLock>();
    public DbSet<ReservationInventoryUnitProjection> InventoryUnitProjections => this.Set<ReservationInventoryUnitProjection>();
    public DbSet<ReservationInventoryBlockProjection> InventoryBlockProjections => this.Set<ReservationInventoryBlockProjection>();
    public DbSet<ReservationInventoryAllocationProjection> InventoryAllocationProjections => this.Set<ReservationInventoryAllocationProjection>();
    public DbSet<ReservationInventoryAllocationUnitProjection> InventoryAllocationUnitProjections => this.Set<ReservationInventoryAllocationUnitProjection>();
    public DbSet<ReservationsProjectionRebuildCheckpoint> ProjectionRebuildCheckpoints => this.Set<ReservationsProjectionRebuildCheckpoint>();
    internal DbSet<ReservationsTenantRevision> TenantRevisions =>
        this.Set<ReservationsTenantRevision>();
    internal DbSet<ReservationsTenantDestroyOperation>
        TenantDestroyOperations =>
        this.Set<ReservationsTenantDestroyOperation>();
    internal DbSet<ReservationsTenantDestroyReceipt> TenantDestroyReceipts =>
        this.Set<ReservationsTenantDestroyReceipt>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.EnsureDataRightsReceiptsAreAppendOnly();
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
        this.EnsureDataRightsReceiptsAreAppendOnly();
        return await this.SaveChangesWithAdmissionAsync(
            acceptAllChangesOnSuccess,
            cancellationToken).ConfigureAwait(false);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(ReservationsMigrations.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReservationsDbContext).Assembly);
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
            throw new ReservationsOperationalAdmissionException(
                ReservationsOperationalAdmissionFailure.Unavailable);
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
                await ReservationsTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    tenantId,
                    cancellationToken).ConfigureAwait(false);
                if (hasOperationalMutation)
                {
                    await ReservationsTenantMutationLock.AcquireRevisionAdvanceAsync(
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

    private async Task EnsureOperationalAdmissionAsync(
        CancellationToken cancellationToken)
    {
        ReservationsTenantRevision? localState =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (localState is not null && !localState.IsOpen)
        {
            throw new ReservationsOperationalAdmissionException(
                ReservationsOperationalAdmissionFailure.Restricted);
        }

        if (terminationFences is null)
        {
            if (this.Database.IsRelational())
            {
                throw new ReservationsOperationalAdmissionException(
                    ReservationsOperationalAdmissionFailure.Unavailable);
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
            throw new ReservationsOperationalAdmissionException(
                ReservationsOperationalAdmissionFailure.Unavailable);
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
        throw new ReservationsOperationalAdmissionException(
            valid
                ? ReservationsOperationalAdmissionFailure.Restricted
                : ReservationsOperationalAdmissionFailure.Unavailable);
    }

    private async Task AdvanceTenantRevisionAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        ReservationsTenantRevision? revision =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (revision is null)
        {
            this.TenantRevisions.Add(
                ReservationsTenantRevision.Create(tenantId));
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
                ReservationProcessingRestrictionProjection or
                ReservationGuestProcessingRestrictionProjection or
                ReservationGuestProfileProjection or
                ReservationInventoryAllocationProjection or
                ReservationInventoryAllocationUnitProjection or
                ReservationInventoryBlockProjection or
                ReservationInventoryUnitProjection or
                ReservationPropertyProjection or
                ReservationPropertyPolicyBinding or
                ReservationPropertyPolicyAcknowledgement or
                ReservationOperationLock or
                ReservationsProjectionRebuildCheckpoint or
                ReservationRetentionSweepCheckpoint or
                ReservationsTenantRevision or
                ReservationsTenantDestroyOperation or
                ReservationsTenantDestroyReceipt));

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
                ReservationsTenantRevision or
                ReservationsTenantDestroyOperation or
                ReservationsTenantDestroyReceipt => false,
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
                await ReservationsTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    canonicalTenantId,
                    cancellationToken).ConfigureAwait(false);
            }

            await this.EnsureOperationalAdmissionAsync(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (ReservationsOperationalAdmissionException)
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
                "Reservations tenant destruction admission is invalid.");
        }

        ReservationsTenantRevision? state =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (state is null || state.DestroyOperationId != operationId ||
            state.LifecycleStatus is not (
                ReservationsTenantLifecycleStatus.Closing or
                ReservationsTenantLifecycleStatus.Closed))
        {
            throw new InvalidOperationException(
                "Reservations tenant destruction state is invalid.");
        }

        if (this.Database.IsNpgsql())
        {
            await this.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('bunkfy.reservations_tenant_destroy_operation_id', {operationId.ToString("D")}, true)",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureDataRightsReceiptsAreAppendOnly()
    {
        if (this.ChangeTracker.Entries<ReservationStationAttribution>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        { throw new InvalidOperationException("Reservation station attribution is immutable; destruction is parent-led."); }
        bool correctionMutationRequested = this.ChangeTracker
            .Entries<ReservationDataRightsCorrectionReceipt>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (correctionMutationRequested)
        {
            throw new InvalidOperationException(
                "Reservation data-rights correction receipts are append-only.");
        }

        bool restrictionMutationRequested = this.ChangeTracker
            .Entries<ReservationProcessingRestrictionReceipt>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (restrictionMutationRequested)
        {
            throw new InvalidOperationException(
                "Reservation processing-restriction receipts are append-only.");
        }

        bool holdReceiptMutationRequested = this.ChangeTracker
            .Entries<ReservationDataHoldReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        if (holdReceiptMutationRequested)
        {
            throw new InvalidOperationException(
                "Reservation data-hold receipts are append-only.");
        }

        bool anonymisationReceiptMutationRequested = this.ChangeTracker
            .Entries<ReservationAnonymisationReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        if (anonymisationReceiptMutationRequested)
        {
            throw new InvalidOperationException(
                "Reservation anonymisation receipts are append-only.");
        }

        bool restoreReceiptMutationRequested = this.ChangeTracker
            .Entries<ReservationAnonymisationRestoreReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        if (restoreReceiptMutationRequested)
        {
            throw new InvalidOperationException(
                "Reservation anonymisation restore receipts are append-only.");
        }

        bool retentionReceiptMutationRequested = this.ChangeTracker
            .Entries<ReservationRetentionAnonymisationReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or
                    EntityState.Deleted);
        if (retentionReceiptMutationRequested)
        {
            throw new InvalidOperationException(
                "Reservation retention anonymisation receipts are append-only.");
        }

        bool tombstoneDeletionRequested = this.ChangeTracker
            .Entries<ReservationAnonymisationTombstone>()
            .Any(entry => entry.State == EntityState.Deleted);
        if (tombstoneDeletionRequested)
        {
            throw new InvalidOperationException(
                "Reservation anonymisation tombstones cannot be deleted.");
        }

        bool tenantDestroyReceiptMutationRequested = this.ChangeTracker
            .Entries<ReservationsTenantDestroyReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        if (tenantDestroyReceiptMutationRequested)
        {
            throw new InvalidOperationException(
                "Reservations tenant destruction receipts are append-only.");
        }
    }
}
